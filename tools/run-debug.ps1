#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Debug runner - Builds and runs MDiceV2 with mod error tolerance
    
.DESCRIPTION
    Combines building and running:
    1. Runs build-debug-with-mods.ps1 (tolerates mod compilation errors)
    2. If main build succeeds, launches MDiceV2.Launcher
    3. Ensures failed mods don't prevent application startup
    
.EXAMPLE
    .\run-debug.ps1
    .\run-debug.ps1 -Clean
#>

param(
    [switch]$Clean = $false
)

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $workspaceRoot "tools\build-debug-with-mods.ps1"
$launcherProject = "MDiceV2.Launcher\MDiceV2.Launcher.csproj"

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "       MDiceV2 Debug Run - With Mod Error Tolerance            " -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

# Run build script
Write-Host "Runner - Executing build script..." -ForegroundColor Yellow
$buildArgs = if ($Clean) { "-Clean" } else { "" }
& $buildScript $buildArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "Runner - Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Runner - OK Build successful" -ForegroundColor Green
Write-Host "Runner - Launching MDiceV2..." -ForegroundColor Cyan
Write-Host ""

# Launch application
Set-Location $workspaceRoot
dotnet run --project $launcherProject

Write-Host ""
Write-Host "Runner - OK MDiceV2 application terminated" -ForegroundColor Green
