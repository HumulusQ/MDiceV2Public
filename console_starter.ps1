# MDiceV2 Console Starter - Lightweight launcher for headless mode
# This script starts the Core application in console mode

$ErrorActionPreference = "Stop"

# Get the directory where this script is located
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$CoreExePath = Join-Path $ScriptDir "Core\bin\Release\net10.0-windows\win-x64\MDiceV2.Core.exe"

# Check if Core executable exists
if (-not (Test-Path $CoreExePath)) {
    Write-Host "[Console Starter] ERROR: Core executable not found at: $CoreExePath"
    Write-Host "[Console Starter] Please ensure Core is built in Release mode"
    exit 1
}

Write-Host "[Console Starter] Starting MDiceV2 Core in headless mode..."
Write-Host "[Console Starter] Core Path: $CoreExePath"

# Start the Core application
try {
    & $CoreExePath
}
catch {
    Write-Host "[Console Starter] ERROR: Failed to start Core: $_"
    exit 1
}
