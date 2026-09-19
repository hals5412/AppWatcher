[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$resolved = (Resolve-Path -LiteralPath $PackagePath).Path
$repo = Split-Path -Parent $PSScriptRoot

[xml]$props = Get-Content (Join-Path $repo "Directory.Build.props")
$expectedVersion = [string]$props.Project.PropertyGroup.Version

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("AppWatcher-PackageVerify-" + [Guid]::NewGuid().ToString("N"))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Expand-Archive -LiteralPath $resolved -DestinationPath $tempRoot -Force

    $required = @(
        "AppWatcher.Agent.exe",
        "AppWatcher.Elevated.exe",
        "AppWatcher.UI.exe",
        "AppWatcher.Core.dll",
        "README.md",
        "README.ja.md",
        "CHANGELOG.md",
        "stop-appwatcher.ps1",
        "restart-appwatcher.ps1",
        "docs\specification.md",
        "docs\architecture.md",
        "docs\manual-test-plan.md"
    )

    foreach ($relative in $required) {
        $path = Join-Path $tempRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required package file is missing: $relative"
        }
    }

    $forbidden = @(
        "config.json",
        "events.db",
        "events.db-wal",
        "events.db-shm",
        "appwatcher-fallback.log"
    )

    foreach ($relative in $forbidden) {
        if (Test-Path -LiteralPath (Join-Path $tempRoot $relative)) {
            throw "Runtime/user-data file must not be shipped in the package: $relative"
        }
    }

    foreach ($exe in @(
        "AppWatcher.Agent.exe",
        "AppWatcher.Elevated.exe",
        "AppWatcher.UI.exe"
    )) {
        $path = Join-Path $tempRoot $exe
        $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion

        if ([string]::IsNullOrWhiteSpace($productVersion) -or
            -not $productVersion.StartsWith($expectedVersion, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$exe product version '$productVersion' does not start with expected version '$expectedVersion'."
        }
    }

    $hostFxr = Get-ChildItem -LiteralPath $tempRoot -Filter "hostfxr.dll" -File -Recurse -ErrorAction SilentlyContinue

    if ($SelfContained) {
        if (-not $hostFxr) {
            throw "Self-contained package does not contain hostfxr.dll."
        }
    }
    elseif ($hostFxr) {
        throw "Framework-dependent package unexpectedly contains hostfxr.dll."
    }

    $unexpected = Get-ChildItem -LiteralPath $tempRoot -File -Recurse | Where-Object {
        $_.Name -match '^(AppWatcher\.(Core\.Tests|TestWorker)(\.|$)|xunit|testhost|Microsoft\.(TestPlatform|Testing\.Platform|VisualStudio\.TestPlatform))' -or
        $_.Name -match '^(config(\.backup-\d+)?\.json|events\.db(-wal|-shm)?|appwatcher-fallback\.log(\.\d+)?)$'
    }
    if ($unexpected) {
        throw "Test components or runtime data found in package: $($unexpected.Name -join ', ')"
    }

    $fileCount = (Get-ChildItem -LiteralPath $tempRoot -File -Recurse).Count
    if ($fileCount -lt 10) {
        throw "Package contains unexpectedly few files: $fileCount"
    }

    Write-Host "Package verification succeeded."
    Write-Host "  Package: $resolved"
    Write-Host "  Version: $expectedVersion"
    Write-Host "  Mode:    $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })"
    Write-Host "  Files:   $fileCount"
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
