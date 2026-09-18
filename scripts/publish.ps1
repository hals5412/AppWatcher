[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [string]$PackageSuffix = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $repo "artifacts"
$packageName = "AppWatcher-$Runtime$PackageSuffix"
$dist = Join-Path $outRoot $packageName
$temp = Join-Path $outRoot "publish-temp-$packageName"

Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
New-Item $dist -ItemType Directory -Force | Out-Null
New-Item $temp -ItemType Directory -Force | Out-Null

$projects = @(
    "src/AppWatcher.Agent/AppWatcher.Agent.csproj",
    "src/AppWatcher.Elevated/AppWatcher.Elevated.csproj",
    "src/AppWatcher.UI/AppWatcher.UI.csproj"
)

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    $projectOut = Join-Path $temp $name
    dotnet publish (Join-Path $repo $project) `
        -c Release `
        -r $Runtime `
        --self-contained $selfContainedValue `
        -o $projectOut

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
    Copy-Item (Join-Path $projectOut "*") $dist -Recurse -Force
}

Copy-Item (Join-Path $repo "README.md") $dist -Force
Copy-Item (Join-Path $repo "README.ja.md") $dist -Force
Copy-Item (Join-Path $repo "CHANGELOG.md") $dist -Force
Copy-Item (Join-Path $repo "docs") (Join-Path $dist "docs") -Recurse -Force
Copy-Item (Join-Path $repo "scripts/stop-appwatcher.ps1") $dist -Force
Copy-Item (Join-Path $repo "scripts/restart-appwatcher.ps1") $dist -Force

$zip = "$dist.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $dist "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $temp -Recurse -Force

Write-Host ""
Write-Host "Published folder: $dist"
Write-Host "ZIP package:      $zip"
if (-not $SelfContained) {
    Write-Host "Note: this build requires the .NET 10 Desktop Runtime. Use -SelfContained to bundle the runtime."
}
