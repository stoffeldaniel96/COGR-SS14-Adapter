[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $StationRepoPath,

    [switch] $VerifyOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$syncScript = Join-Path $PSScriptRoot "sync-to-station.ps1"
$wiringScript = Join-Path $PSScriptRoot "install-wiring.ps1"

if ($VerifyOnly) {
    & $syncScript -StationRepoPath $StationRepoPath -VerifyOnly
    & $wiringScript -StationRepoPath $StationRepoPath -VerifyOnly
    Write-Host "Adapter source and install wiring are synchronized with the Station checkout."
    return
}

& $syncScript -StationRepoPath $StationRepoPath
& $wiringScript -StationRepoPath $StationRepoPath
& $syncScript -StationRepoPath $StationRepoPath -VerifyOnly
& $wiringScript -StationRepoPath $StationRepoPath -VerifyOnly

Write-Host "Adapter installation completed and verified."
Write-Host "The Station checkout still requires its normal build/local acceptance gate."
