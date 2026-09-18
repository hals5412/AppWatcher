[CmdletBinding()]
param(
    [string]$AppWatcherDirectory = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
$ui = Join-Path $AppWatcherDirectory "AppWatcher.UI.exe"
if (-not (Test-Path $ui)) {
    $repo = Split-Path -Parent $PSScriptRoot
    $candidate = Join-Path $repo "artifacts\AppWatcher-win-x64\AppWatcher.UI.exe"
    if (Test-Path $candidate) { $ui = $candidate }
}
if (-not (Test-Path $ui)) { throw "AppWatcher.UI.exe was not found." }

& $ui --shutdown
if ($LASTEXITCODE -ne 0) { throw "AppWatcher shutdown did not complete cleanly (exit code $LASTEXITCODE)." }
Write-Host "AppWatcher background components stopped. Monitored applications were left running."
