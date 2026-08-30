[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $StationRepoPath,

    [string] $SourceCommit = "947b7462235f95bc3f9d48d834e6485af1557a91"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$adapterRoot = Split-Path -Parent $PSScriptRoot
$stationRoot = (Resolve-Path $StationRepoPath).Path
$manifestPath = Join-Path $adapterRoot "adapter-manifest.json"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cogr-ss14-adapter-import-" + [Guid]::NewGuid().ToString("N"))

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git failed: git $($Arguments -join ' ')"
    }
}

if (-not (Test-Path (Join-Path $stationRoot ".git"))) {
    throw "StationRepoPath is not a Git checkout: $stationRoot"
}

if (-not (Test-Path $manifestPath)) {
    throw "Adapter manifest not found: $manifestPath"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$mappings = @($manifest.mappings)
if ($mappings.Count -eq 0) {
    throw "Adapter manifest contains no declared mappings."
}

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Remove-Item -Recurse -Force $tempRoot

    Write-Host "Creating detached worktree for COGR-Station $SourceCommit..."
    Invoke-Git -C $stationRoot worktree add --detach $tempRoot $SourceCommit

    $sourceCommitDate = (& git -C $stationRoot show -s --format=%cI $SourceCommit).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommitDate)) {
        throw "Could not resolve source commit timestamp for $SourceCommit."
    }

    # Use a native PowerShell array rather than Generic.List[object]. Windows PowerShell 5.1
    # can throw 'Argument types do not match' while enumerating generic lists during
    # expression/serialization binding.
    $provenanceMappings = @()

    Write-Host "Importing $($mappings.Count) declared adapter mapping(s)..."
    foreach ($mapping in $mappings) {
        $mappingSource = [string] $mapping.source
        $stationDestination = [string] $mapping.stationDestination
        $ownership = [string] $mapping.ownership

        if ([string]::IsNullOrWhiteSpace($mappingSource) -or [string]::IsNullOrWhiteSpace($stationDestination)) {
            throw "Adapter manifest contains a mapping without source/stationDestination."
        }

        $source = Join-Path $tempRoot $stationDestination
        $destination = Join-Path $adapterRoot $mappingSource

        if (-not (Test-Path -LiteralPath $source)) {
            throw "Expected adapter source path does not exist at extraction commit: $stationDestination"
        }

        if (Test-Path -LiteralPath $destination) {
            Remove-Item -Recurse -Force -LiteralPath $destination
        }

        $destinationParent = Split-Path -Parent $destination
        if (-not [string]::IsNullOrWhiteSpace($destinationParent)) {
            New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        }

        Copy-Item -Recurse -Force -LiteralPath $source -Destination $destination
        Write-Host "Imported $stationDestination -> $mappingSource"

        $provenanceMappings += [pscustomobject][ordered]@{
            source = $stationDestination
            destination = $mappingSource
            ownership = $ownership
        }
    }

    Write-Host "Writing deterministic extraction provenance..."
    $provenance = [ordered]@{
        schemaVersion = 1
        sourceRepository = "stoffeldaniel96/COGR-Station"
        sourceCommit = $SourceCommit
        sourceCommitDate = $sourceCommitDate
        mappings = $provenanceMappings
    }

    $provenanceJson = $provenance | ConvertTo-Json -Depth 6
    Set-Content -LiteralPath (Join-Path $adapterRoot "extraction-provenance.json") -Value $provenanceJson -Encoding UTF8

    Write-Host ""
    Write-Host "Import complete. Run scripts/verify-public-readiness.ps1 before committing the extracted source."
}
finally {
    if (Test-Path $tempRoot) {
        try {
            Invoke-Git -C $stationRoot worktree remove --force $tempRoot
        }
        catch {
            Write-Warning "Could not remove temporary worktree automatically: $tempRoot"
        }
    }
}
