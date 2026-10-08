# Launch Debug MDiceV2

$launcherPath = Join-Path $PSScriptRoot ".." "MDiceV2_Debug" "MDiceV2.Launcher.exe"

if (-not (Test-Path $launcherPath)) {
    Write-Host "Error: Launcher not found at $launcherPath" -ForegroundColor Red
    exit 1
}

Write-Host "Starting MDiceV2 Launcher from: $launcherPath" -ForegroundColor Cyan
& $launcherPath
