[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $StationRepoPath,

    [switch] $VerifyOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$adapterRoot = Split-Path -Parent $PSScriptRoot
$stationRoot = (Resolve-Path $StationRepoPath).Path
$manifestPath = Join-Path $adapterRoot "adapter-manifest.json"

if (-not (Test-Path (Join-Path $stationRoot ".git"))) {
    throw "StationRepoPath is not a Git checkout/worktree: $stationRoot"
}

if (-not (Test-Path $manifestPath)) {
    throw "Adapter manifest not found: $manifestPath"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$wiring = $manifest.integrationWiring
if ($null -eq $wiring) {
    throw "Adapter manifest does not declare integrationWiring."
}

$serverProjectPath = Join-Path $stationRoot $wiring.contentServerProject
$testsProjectPath = Join-Path $stationRoot $wiring.contentTestsProject
$centralPackagePath = Join-Path $stationRoot $wiring.centralPackageFile

foreach ($requiredPath in @($serverProjectPath, $testsProjectPath, $centralPackagePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required Station integration file is missing: $requiredPath"
    }
}

[xml] $serverProject = Get-Content -Raw -LiteralPath $serverProjectPath
[xml] $testsProject = Get-Content -Raw -LiteralPath $testsProjectPath
[xml] $centralPackages = Get-Content -Raw -LiteralPath $centralPackagePath

$failures = New-Object System.Collections.Generic.List[string]
$serverChanged = $false
$testsChanged = $false
$packagesChanged = $false

function Get-SingleNode {
    param(
        [Parameter(Mandatory = $true)][System.Xml.XmlDocument] $Document,
        [Parameter(Mandatory = $true)][string] $XPath,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $nodes = $Document.SelectNodes($XPath)
    if ($nodes.Count -gt 1) {
        throw "Station integration structure is ambiguous: multiple $Description nodes matched '$XPath'."
    }

    if ($nodes.Count -eq 0) {
        return $null
    }

    return $nodes[0]
}

function Save-XmlDocument {
    param(
        [Parameter(Mandatory = $true)][System.Xml.XmlDocument] $Document,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.IndentChars = "  "
    $settings.NewLineChars = [Environment]::NewLine
    $settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)

    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $Document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function Ensure-AssemblyReferences {
    param(
        [Parameter(Mandatory = $true)][System.Xml.XmlDocument] $Project,
        [Parameter(Mandatory = $true)][System.Xml.XmlElement] $ItemGroup,
        [Parameter(Mandatory = $true)] $References,
        [Parameter(Mandatory = $true)][string] $ProjectLabel,
        [Parameter(Mandatory = $true)][ref] $Changed
    )

    foreach ($reference in $References) {
        $id = [string] $reference.id
        $hintPath = [string] $reference.hintPath
        $xpath = "/Project/ItemGroup/Reference[@Include='$id']"
        $existing = Get-SingleNode -Document $Project -XPath $xpath -Description "Reference '$id' in $ProjectLabel"

        if ($null -ne $existing) {
            $existingHint = $existing.SelectSingleNode('HintPath')
            if ($null -eq $existingHint) {
                $failures.Add("$ProjectLabel Reference '$id' exists without a HintPath; expected '$hintPath'.")
            }
            elseif (([string] $existingHint.InnerText) -ne $hintPath) {
                $failures.Add("$ProjectLabel Reference '$id' uses '$($existingHint.InnerText)' but adapter declares '$hintPath'.")
            }
            continue
        }

        if ($VerifyOnly) {
            $failures.Add("Missing $ProjectLabel assembly Reference '$id' -> '$hintPath'.")
            continue
        }

        $node = $Project.CreateElement('Reference')
        $node.SetAttribute('Include', $id)
        $hintNode = $Project.CreateElement('HintPath')
        $hintNode.InnerText = $hintPath
        [void] $node.AppendChild($hintNode)
        [void] $ItemGroup.AppendChild($node)
        $Changed.Value = $true
    }
}

$centralItemGroup = $centralPackages.SelectSingleNode('/Project/ItemGroup')
if ($null -eq $centralItemGroup) {
    throw "Could not locate an ItemGroup in $($wiring.centralPackageFile)."
}

foreach ($package in $wiring.packageReferences) {
    $id = [string] $package.id
    $version = [string] $package.version
    $xpath = "/Project/ItemGroup/PackageVersion[@Include='$id']"
    $existing = Get-SingleNode -Document $centralPackages -XPath $xpath -Description "PackageVersion '$id'"

    if ($null -ne $existing) {
        $existingVersion = [string] $existing.GetAttribute('Version')
        if ($existingVersion -ne $version) {
            $failures.Add("Central package '$id' is '$existingVersion' but adapter declares '$version'.")
        }
        continue
    }

    if ($VerifyOnly) {
        $failures.Add("Missing central package version '$id' ($version).")
        continue
    }

    $node = $centralPackages.CreateElement('PackageVersion')
    $node.SetAttribute('Include', $id)
    $node.SetAttribute('Version', $version)
    [void] $centralItemGroup.AppendChild($node)
    $packagesChanged = $true
}

$serverItemGroup = $serverProject.SelectSingleNode('/Project/ItemGroup[PackageReference]')
if ($null -eq $serverItemGroup) {
    throw "Could not locate the Content.Server PackageReference ItemGroup."
}

foreach ($package in $wiring.packageReferences) {
    $id = [string] $package.id
    $xpath = "/Project/ItemGroup/PackageReference[@Include='$id']"
    $existing = Get-SingleNode -Document $serverProject -XPath $xpath -Description "PackageReference '$id'"

    if ($null -ne $existing) {
        continue
    }

    if ($VerifyOnly) {
        $failures.Add("Missing Content.Server PackageReference '$id'.")
        continue
    }

    $node = $serverProject.CreateElement('PackageReference')
    $node.SetAttribute('Include', $id)
    [void] $serverItemGroup.AppendChild($node)
    $serverChanged = $true
}

Ensure-AssemblyReferences `
    -Project $serverProject `
    -ItemGroup $serverItemGroup `
    -References $wiring.assemblyReferences `
    -ProjectLabel "Content.Server" `
    -Changed ([ref] $serverChanged)

$testsItemGroup = $testsProject.SelectSingleNode('/Project/ItemGroup[ProjectReference]')
if ($null -eq $testsItemGroup) {
    throw "Could not locate the Content.Tests ProjectReference ItemGroup."
}

Ensure-AssemblyReferences `
    -Project $testsProject `
    -ItemGroup $testsItemGroup `
    -References $wiring.testAssemblyReferences `
    -ProjectLabel "Content.Tests" `
    -Changed ([ref] $testsChanged)

if ($failures.Count -gt 0) {
    $detail = ($failures | Sort-Object -Unique) -join [Environment]::NewLine
    throw "Adapter install wiring verification failed:$([Environment]::NewLine)$detail"
}

if ($VerifyOnly) {
    Write-Host "Adapter install wiring is present and compatible."
    return
}

if ($packagesChanged) {
    Save-XmlDocument -Document $centralPackages -Path $centralPackagePath
    Write-Host "Updated $($wiring.centralPackageFile)."
}

if ($serverChanged) {
    Save-XmlDocument -Document $serverProject -Path $serverProjectPath
    Write-Host "Updated $($wiring.contentServerProject)."
}

if ($testsChanged) {
    Save-XmlDocument -Document $testsProject -Path $testsProjectPath
    Write-Host "Updated $($wiring.contentTestsProject)."
}

if (-not $packagesChanged -and -not $serverChanged -and -not $testsChanged) {
    Write-Host "Adapter install wiring was already present and compatible."
}

Write-Host "Run this script again with -VerifyOnly after composition if independent verification is required."
