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
    if ($LASTEXITCODE -ne 0) {
        throw "Adapter source synchronization verification failed."
    }

    & $wiringScript -StationRepoPath $StationRepoPath -VerifyOnly
    if ($LASTEXITCODE -ne 0) {
        throw "Adapter install wiring verification failed."
    }

    Write-Host "Adapter source and install wiring are synchronized with the Station checkout."
    exit 0
}

& $syncScript -StationRepoPath $StationRepoPath
if ($LASTEXITCODE -ne 0) {
    throw "Adapter source synchronization failed."
}

& $wiringScript -StationRepoPath $StationRepoPath
if ($LASTEXITCODE -ne 0) {
    throw "Adapter install wiring failed."
}

& $syncScript -StationRepoPath $StationRepoPath -VerifyOnly
if ($LASTEXITCODE -ne 0) {
    throw "Adapter source verification failed after installation."
}

& $wiringScript -StationRepoPath $StationRepoPath -VerifyOnly
if ($LASTEXITCODE -ne 0) {
    throw "Adapter wiring verification failed after installation."
}

Write-Host "Adapter installation completed and verified."
Write-Host "The Station checkout still requires its normal build/local acceptance gate."
