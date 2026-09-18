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

& $ui --restart
if ($LASTEXITCODE -ne 0) { throw "AppWatcher restart did not complete cleanly (exit code $LASTEXITCODE)." }
Write-Host "AppWatcher background components restarted. Monitored applications were left running."
