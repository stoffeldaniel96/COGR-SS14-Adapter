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
    throw "StationRepoPath is not a Git checkout: $stationRoot"
}

if (-not (Test-Path (Join-Path $stationRoot "Content.Server/Content.Server.csproj"))) {
    throw "StationRepoPath does not look like a compatible Space Station 14 content checkout: $stationRoot"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

function Get-TreeState {
    param([string] $Root)

    $state = @{}
    if (-not (Test-Path $Root)) {
        return $state
    }

    foreach ($file in Get-ChildItem -Path $Root -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\\', '/')
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

$allDifferences = New-Object System.Collections.Generic.List[string]

foreach ($mapping in $manifest.mappings) {
    $source = Join-Path $adapterRoot $mapping.source
    $destination = Join-Path $stationRoot $mapping.stationDestination

    if (-not (Test-Path $source)) {
        throw "Adapter source has not been extracted/populated: $($mapping.source)"
    }

    if ($VerifyOnly) {
        $expected = Get-TreeState $source
        $actual = Get-TreeState $destination
        foreach ($difference in Compare-TreeState $expected $actual $mapping.stationDestination) {
            $allDifferences.Add($difference)
        }
        continue
    }

    if (Test-Path $destination) {
        Remove-Item -Recurse -Force $destination
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -Recurse -Force $source $destination
    Write-Host "Synchronized $($mapping.source) -> $($mapping.stationDestination)"
}

if ($VerifyOnly) {
    if ($allDifferences.Count -gt 0) {
        Write-Host "Adapter/Station synchronization verification failed:" -ForegroundColor Red
        foreach ($difference in $allDifferences | Sort-Object -Unique) {
            Write-Host "  - $difference" -ForegroundColor Red
        }
        exit 1
    }

    Write-Host "Adapter/Station source trees are synchronized."
    exit 0
}

& git -C $adapterRoot rev-parse HEAD | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not resolve adapter Git revision."
}
$adapterCommit = (& git -C $adapterRoot rev-parse HEAD).Trim()
$stationCommitBeforeSync = (& git -C $stationRoot rev-parse HEAD).Trim()

$version = [ordered]@{
    schemaVersion = 1
    adapterRepository = "stoffeldaniel96/COGR-SS14-Adapter"
    adapterCommit = $adapterCommit
    synchronizedAtUtc = [DateTime]::UtcNow.ToString("o")
    stationCommitBeforeSync = $stationCommitBeforeSync
}

$version | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $stationRoot "COGR-ADAPTER-VERSION.json")

& $PSCommandPath -StationRepoPath $stationRoot -VerifyOnly
if ($LASTEXITCODE -ne 0) {
    throw "Post-sync verification failed."
}

Write-Host "Station mirror synchronized to adapter commit $adapterCommit"
Write-Host "Build/local-gate the Station checkout before declaring the integration green."
