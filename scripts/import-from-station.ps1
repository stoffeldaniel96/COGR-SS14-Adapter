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

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Remove-Item -Recurse -Force $tempRoot

    Write-Host "Creating detached worktree for COGR-Station $SourceCommit..."
    Invoke-Git -C $stationRoot worktree add --detach $tempRoot $SourceCommit

    $sourceCommitDate = (& git -C $stationRoot show -s --format=%cI $SourceCommit).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommitDate)) {
        throw "Could not resolve source commit timestamp for $SourceCommit."
    }

    $mappings = @(
        @{ Source = "Content.Server/COGR"; Destination = "overlay/Content.Server/COGR" },
        @{ Source = "Content.Shared/COGR"; Destination = "overlay/Content.Shared/COGR" }
    )

    foreach ($mapping in $mappings) {
        $source = Join-Path $tempRoot $mapping.Source
        $destination = Join-Path $adapterRoot $mapping.Destination

        if (-not (Test-Path $source)) {
            throw "Expected adapter source path does not exist at extraction commit: $($mapping.Source)"
        }

        if (Test-Path $destination) {
            Remove-Item -Recurse -Force $destination
        }

        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -Recurse -Force $source $destination
        Write-Host "Imported $($mapping.Source) -> $($mapping.Destination)"
    }

    $provenance = [ordered]@{
        schemaVersion = 1
        sourceRepository = "stoffeldaniel96/COGR-Station"
        sourceCommit = $SourceCommit
        sourceCommitDate = $sourceCommitDate
        mappings = @(
            [ordered]@{ source = "Content.Server/COGR"; destination = "overlay/Content.Server/COGR" },
            [ordered]@{ source = "Content.Shared/COGR"; destination = "overlay/Content.Shared/COGR" }
        )
    }

    $provenance | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $adapterRoot "extraction-provenance.json")

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
