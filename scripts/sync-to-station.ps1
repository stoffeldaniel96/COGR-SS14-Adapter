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

function Get-PortableRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $BasePath,
        [Parameter(Mandatory = $true)][string] $TargetPath
    )

    $baseFull = [System.IO.Path]::GetFullPath($BasePath)
    $targetFull = [System.IO.Path]::GetFullPath($TargetPath)
    $separator = [System.IO.Path]::DirectorySeparatorChar.ToString()

    if (-not $baseFull.EndsWith($separator, [System.StringComparison]::Ordinal)) {
        $baseFull += $separator
    }

    if (-not $targetFull.StartsWith($baseFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$targetFull' is outside expected root '$baseFull'."
    }

    return $targetFull.Substring($baseFull.Length).Replace('\', '/')
}

if (-not (Test-Path (Join-Path $stationRoot ".git"))) {
    throw "StationRepoPath is not a Git checkout: $stationRoot"
}

if (-not (Test-Path (Join-Path $stationRoot "Content.Server/Content.Server.csproj"))) {
    throw "StationRepoPath does not look like a compatible Space Station 14 content checkout: $stationRoot"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

function Get-TreeState {
    param([string] $Root)

    $state = @{}
    if (-not (Test-Path -LiteralPath $Root)) {
        return $state
    }

    if (Test-Path -LiteralPath $Root -PathType Leaf) {
        $state["."] = (Get-FileHash -Algorithm SHA256 -LiteralPath $Root).Hash.ToLowerInvariant()
        return $state
    }

    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        $relative = Get-PortableRelativePath -BasePath $Root -TargetPath $file.FullName
        $state[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }

    return $state
}

function Compare-TreeState {
    param(
        [hashtable] $Expected,
        [hashtable] $Actual,
        [string] $Label
    )

    $differences = New-Object System.Collections.Generic.List[string]
    foreach ($path in $Expected.Keys) {
        if (-not $Actual.ContainsKey($path)) {
            $differences.Add("missing in Station: $Label/$path")
        }
        elseif ($Expected[$path] -ne $Actual[$path]) {
            $differences.Add("content differs: $Label/$path")
        }
    }

    foreach ($path in $Actual.Keys) {
        if (-not $Expected.ContainsKey($path)) {
            $differences.Add("Station-only drift: $Label/$path")
        }
    }

    return $differences
}

function Get-AllDifferences {
    $differences = New-Object System.Collections.Generic.List[string]

    foreach ($mapping in $manifest.mappings) {
        $source = Join-Path $adapterRoot $mapping.source
        $destination = Join-Path $stationRoot $mapping.stationDestination

        if (-not (Test-Path -LiteralPath $source)) {
            throw "Adapter source has not been extracted/populated: $($mapping.source)"
        }

        $expected = Get-TreeState $source
        $actual = Get-TreeState $destination
        foreach ($difference in Compare-TreeState $expected $actual $mapping.stationDestination) {
            $differences.Add($difference)
        }
    }

    return $differences
}

if ($VerifyOnly) {
    $differences = Get-AllDifferences
    if ($differences.Count -gt 0) {
        $detail = ($differences | Sort-Object -Unique) -join [Environment]::NewLine
        throw "Adapter/Station synchronization verification failed:$([Environment]::NewLine)$detail"
    }

    Write-Host "Adapter/Station source trees are synchronized."
    return
}

foreach ($mapping in $manifest.mappings) {
    $source = Join-Path $adapterRoot $mapping.source
    $destination = Join-Path $stationRoot $mapping.stationDestination

    if (-not (Test-Path -LiteralPath $source)) {
        throw "Adapter source has not been extracted/populated: $($mapping.source)"
    }

    if (Test-Path -LiteralPath $destination) {
        Remove-Item -Recurse -Force -LiteralPath $destination
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -Recurse -Force -LiteralPath $source -Destination $destination
    Write-Host "Synchronized $($mapping.source) -> $($mapping.stationDestination)"
}

$adapterCommit = (& git -C $adapterRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($adapterCommit)) {
    throw "Could not resolve adapter Git revision."
}

$stationCommitBeforeSync = (& git -C $stationRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($stationCommitBeforeSync)) {
    throw "Could not resolve Station Git revision."
}

$version = [ordered]@{
    schemaVersion = 1
    adapterRepository = "stoffeldaniel96/COGR-SS14-Adapter"
    adapterCommit = $adapterCommit
    synchronizedAtUtc = [DateTime]::UtcNow.ToString("o")
    stationCommitBeforeSync = $stationCommitBeforeSync
}

$version | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $stationRoot "COGR-ADAPTER-VERSION.json")

$postSyncDifferences = Get-AllDifferences
if ($postSyncDifferences.Count -gt 0) {
    $detail = ($postSyncDifferences | Sort-Object -Unique) -join [Environment]::NewLine
    throw "Post-sync verification failed:$([Environment]::NewLine)$detail"
}

Write-Host "Station mirror synchronized to adapter commit $adapterCommit"
Write-Host "Build/local-gate the Station checkout before declaring the integration green."
