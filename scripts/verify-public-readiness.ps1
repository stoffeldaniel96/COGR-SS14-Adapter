[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$adapterRoot = Split-Path -Parent $PSScriptRoot
$overlayRoot = Join-Path $adapterRoot "overlay"

if (-not (Test-Path $overlayRoot)) {
    throw "No extracted overlay exists. Run scripts/import-from-station.ps1 first."
}

$failures = New-Object System.Collections.Generic.List[string]

$forbiddenPaths = @(
    "lib",
    "Resources/Maps/COGR"
)

foreach ($relative in $forbiddenPaths) {
    if (Test-Path (Join-Path $adapterRoot $relative)) {
        $failures.Add("Unexpected excluded path is present: $relative")
    }
}

$patterns = @(
    @{ Name = "hard-coded development launch token"; Regex = 'dev-token-f05' },
    @{ Name = "private key material"; Regex = '-----BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----' },
    @{ Name = "GitHub token-shaped secret"; Regex = '(ghp|github_pat)_[A-Za-z0-9_]+' },
    @{ Name = "machine-local Windows user path"; Regex = '[A-Za-z]:\\Users\\[^\\]+' }
)

$textExtensions = @(".cs", ".md", ".json", ".ps1", ".psm1", ".yml", ".yaml", ".toml", ".txt", ".xml", ".props", ".targets")
$files = Get-ChildItem -Path $overlayRoot -Recurse -File | Where-Object { $textExtensions -contains $_.Extension.ToLowerInvariant() }

foreach ($file in $files) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($pattern in $patterns) {
        if ($content -match $pattern.Regex) {
            $relativePath = [System.IO.Path]::GetRelativePath($adapterRoot, $file.FullName)
            $failures.Add("$($pattern.Name): $relativePath")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Public-readiness verification failed:" -ForegroundColor Red
    foreach ($failure in $failures | Sort-Object -Unique) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Public-readiness verification passed for extracted adapter text source."
Write-Host "This check is intentionally bounded; it does not replace dependency/license review or Station integration validation."
