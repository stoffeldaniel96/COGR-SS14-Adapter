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
if (-not $manifest.mappings -or $manifest.mappings.Count -eq 0) {
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

    $provenanceMappings = New-Object System.Collections.Generic.List[object]

    foreach ($mapping in $manifest.mappings) {
        if ([string]::IsNullOrWhiteSpace($mapping.source) -or [string]::IsNullOrWhiteSpace($mapping.stationDestination)) {
            throw "Adapter manifest contains a mapping without source/stationDestination."
        }

        $source = Join-Path $tempRoot $mapping.stationDestination
        $destination = Join-Path $adapterRoot $mapping.source

        if (-not (Test-Path -LiteralPath $source)) {
            throw "Expected adapter source path does not exist at extraction commit: $($mapping.stationDestination)"
        }

        if (Test-Path -LiteralPath $destination) {
            Remove-Item -Recurse -Force -LiteralPath $destination
        }

        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -Recurse -Force -LiteralPath $source -Destination $destination
        Write-Host "Imported $($mapping.stationDestination) -> $($mapping.source)"

        $provenanceMappings.Add([ordered]@{
            source = $mapping.stationDestination
            destination = $mapping.source
            ownership = $mapping.ownership
        })
    }

    $provenance = [ordered]@{
        schemaVersion = 1
        sourceRepository = "stoffeldaniel96/COGR-Station"
        sourceCommit = $SourceCommit
        sourceCommitDate = $sourceCommitDate
        mappings = @($provenanceMappings)
    }

    $provenance | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $adapterRoot "extraction-provenance.json")

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
