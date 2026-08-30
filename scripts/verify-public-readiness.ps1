[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$adapterRoot = Split-Path -Parent $PSScriptRoot
$overlayRoot = Join-Path $adapterRoot "overlay"

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

if (-not (Test-Path $overlayRoot)) {
    throw "No extracted overlay exists. Run scripts/import-from-station.ps1 first."
}

$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

$forbiddenPaths = @(
    "lib",
    "Resources/Maps/COGR"
)

foreach ($relative in $forbiddenPaths) {
    if (Test-Path (Join-Path $adapterRoot $relative)) {
        $failures.Add("Unexpected excluded path is present: $relative")
    }
}

$failurePatterns = @(
    @{ Name = "private key material"; Regex = '-----BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----' },
    @{ Name = "GitHub token-shaped secret"; Regex = '(ghp|github_pat)_[A-Za-z0-9_]+' },
    @{ Name = "machine-local Windows user path"; Regex = '[A-Za-z]:\\Users\\[^\\]+' }
)

$warningPatterns = @(
    @{ Name = "hard-coded development launch token configuration debt"; Regex = 'dev-token-f05' }
)

$textExtensions = @(".cs", ".md", ".json", ".ps1", ".psm1", ".yml", ".yaml", ".toml", ".txt", ".xml", ".props", ".targets")
$files = Get-ChildItem -Path $overlayRoot -Recurse -File | Where-Object { $textExtensions -contains $_.Extension.ToLowerInvariant() }

foreach ($file in $files) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    $relativePath = Get-PortableRelativePath -BasePath $adapterRoot -TargetPath $file.FullName

    foreach ($pattern in $failurePatterns) {
        if ($content -match $pattern.Regex) {
            $failures.Add("$($pattern.Name): $relativePath")
        }
    }

    foreach ($pattern in $warningPatterns) {
        if ($content -match $pattern.Regex) {
            $warnings.Add("$($pattern.Name): $relativePath")
        }
    }
}

if ($warnings.Count -gt 0) {
    Write-Host "Public-readiness warnings:" -ForegroundColor Yellow
    foreach ($warning in $warnings | Sort-Object -Unique) {
        Write-Host "  - $warning" -ForegroundColor Yellow
    }
    Write-Host "Warnings do not change the extraction snapshot. Resolve configuration debt in a separately gated adapter change."
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
