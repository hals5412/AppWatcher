[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relativePath) {
    $path = Join-Path $repo $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required repository file is missing: $relativePath"
    }

    return [IO.File]::ReadAllText($path)
}

[xml]$props = Read-Utf8 "Directory.Build.props"
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Directory.Build.props does not define Version."
}

$changelog = Read-Utf8 "CHANGELOG.md"
$firstVersionHeading = [regex]::Match(
    $changelog,
    '(?m)^##\s+([^\r\n]+)').Groups[1].Value.Trim()

if ($firstVersionHeading -ne $version) {
    throw "CHANGELOG top version '$firstVersionHeading' does not match project version '$version'."
}

$readme = Read-Utf8 "README.md"
$readmeJa = Read-Utf8 "README.ja.md"

if ($readme -match 'Status:\s+\*\*v0\.1\.0-alpha\.\d+') {
    throw "README.md contains a stale hard-coded alpha version banner."
}

if ($readmeJa -match '現在の状態:\s+\*\*v0\.1\.0-alpha\.\d+') {
    throw "README.ja.md contains a stale hard-coded alpha version banner."
}

$documentationPaths = @(
    "README.md",
    "README.ja.md",
    "docs\specification.md",
    "docs\architecture.md",
    "docs\manual-test-plan.md"
)

$legacyProductPattern = '(?i)\bTVRock\b|\bTVTest\b|Libre\s*Hardware\s*Monitor|LibreHardwareMonitor'
foreach ($relative in $documentationPaths) {
    $text = Read-Utf8 $relative
    if ($text -match $legacyProductPattern) {
        throw "Product-specific legacy example remains in current documentation: $relative"
    }
}

$architecture = Read-Utf8 "docs\architecture.md"
if ($architecture -match "default per-process pipe DACL" -or
    $architecture -match "SID-only ACL construction is planned") {
    throw "docs/architecture.md still describes the obsolete pre-alpha.6 pipe ACL design."
}

$resxFiles = Get-ChildItem -LiteralPath (Join-Path $repo "src") -Filter "*.resx" -File -Recurse
foreach ($file in $resxFiles) {
    try {
        [xml]$null = [IO.File]::ReadAllText($file.FullName)
    }
    catch {
        throw "Invalid RESX XML: $($file.FullName): $($_.Exception.Message)"
    }
}

foreach ($workflow in @(
    ".github\workflows\build.yml",
    ".github\workflows\release.yml"
)) {
    $text = Read-Utf8 $workflow
    if ($text -notmatch 'prebeta-check\.ps1') {
        throw "$workflow does not run the pre-beta repository checks."
    }
    if ($text -notmatch 'verify-package\.ps1') {
        throw "$workflow does not verify published packages."
    }
}

$publish = Read-Utf8 "scripts\publish.ps1"
foreach ($requiredDoc in @("README.md", "README.ja.md", "CHANGELOG.md")) {
    if ($publish -notmatch [regex]::Escape($requiredDoc)) {
        throw "publish.ps1 does not package $requiredDoc."
    }
}

$verify = Read-Utf8 "scripts\verify-package.ps1"
foreach ($requiredDoc in @("README.md", "README.ja.md", "CHANGELOG.md")) {
    if ($verify -notmatch [regex]::Escape($requiredDoc)) {
        throw "verify-package.ps1 does not require $requiredDoc."
    }
}

Write-Host "Pre-beta repository checks succeeded."
Write-Host "  Version:         $version"
Write-Host "  RESX files:      $($resxFiles.Count)"
Write-Host "  Documentation:   aligned"
Write-Host "  Package checks:  wired into CI/release"
