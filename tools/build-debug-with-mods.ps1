#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Debug build script - Separates main program and mod compilation
    
.DESCRIPTION
    1. Builds MDiceV2 main program (without C++ mods)
    2. Attempts to build mods separately
    3. If mods fail, continues (won't block main program)
    4. Copies successful mod DLLs to output directory
    
.EXAMPLE
    .\build-debug-with-mods.ps1
#>

param(
    [switch]$Clean = $false,
    [switch]$Verbose = $false
)

$ErrorActionPreference = "Continue"
$WarningPreference = "Continue"

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$solutionFile = Join-Path $workspaceRoot "MDiceV2.sln"
$launcherDebugDir = Join-Path $workspaceRoot "MDiceV2_Debug"
$launcherCoreBinDir = Join-Path $workspaceRoot "MDiceV2.Launcher\bin\Debug\net10.0-windows"
$modsDir = Join-Path $workspaceRoot "mods"

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "       MDiceV2 Debug Build - With Mod Error Tolerance          " -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

# ============= Phase 1: Clean if requested =============
if ($Clean) {
    Write-Host "[Phase 1] - Cleaning previous builds..." -ForegroundColor Yellow
    Get-ChildItem $workspaceRoot -Include "bin", "obj" -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[Phase 1] - Clean complete" -ForegroundColor Green
    Write-Host ""
}

# ============= Phase 2: Build main program (without mods) =============
Write-Host "[Phase 2] - Building main program (MDiceV2.Core + MDiceV2.Launcher)..." -ForegroundColor Yellow

$mainProjectsConfig = @(
    "MDiceV2.Abstractions\MDiceV2.Abstractions.csproj"
    "MDiceV2.Interfaces\MDiceV2.Interfaces.csproj"
    "MDiceV2.Core\MDiceV2.Core.csproj"
    "MDiceV2.Console\MDiceV2.Console.csproj"
    "MDiceV2.Launcher\MDiceV2.Launcher.csproj"
    "MDiceV2.Tests\MDiceV2.Tests.csproj"
)

$buildFailed = $false

foreach ($project in $mainProjectsConfig) {
    $projectPath = Join-Path $workspaceRoot $project
    $projectName = Split-Path $project -Leaf
    
    Write-Host "[Phase 2] Building $projectName..." -ForegroundColor Cyan
    
    $buildOutput = dotnet build $projectPath -c Debug 2>&1
    $buildResult = $LASTEXITCODE
    
    if ($buildResult -ne 0) {
        Write-Host "[Phase 2] - FAILED to build $projectName" -ForegroundColor Red
        Write-Host $buildOutput
        $buildFailed = $true
        break
    } else {
        Write-Host "[Phase 2] - OK $projectName built successfully" -ForegroundColor Green
    }
}

if ($buildFailed) {
    Write-Host ""
    Write-Host "[Phase 2] - Main program build FAILED - Cannot continue" -ForegroundColor Red
    exit 1
}

Write-Host "[Phase 2] - Main program build successful" -ForegroundColor Green
Write-Host ""

# ============= Phase 3: Build other mods (AIMod, CustomizedReply) =============
Write-Host "[Phase 3] - Building standard mods..." -ForegroundColor Yellow

$standardMods = @(
    @{ ModName = "AIMod"; ProjectPath = "Mods\AIMod\AIMod.csproj" },
    @{ ModName = "CustomizedReply"; ProjectPath = "Mods\CustomizedReply\CustomizedReply.csproj" }
)

$failedMods = @()

foreach ($mod in $standardMods) {
    $modName = $mod.ModName
    $modProject = $mod.ProjectPath
    $projectPath = Join-Path $workspaceRoot $modProject
    
    Write-Host "[Phase 3] Building mod: $modName..." -ForegroundColor Cyan
    
    $buildOutput = dotnet build $projectPath -c Debug 2>&1
    $buildResult = $LASTEXITCODE
    
    if ($buildResult -ne 0) {
        Write-Host "[Phase 3] - WARNING: Failed to build $modName (non-blocking)" -ForegroundColor Yellow
        $failedMods += $modName
    } else {
        Write-Host "[Phase 3] - OK $modName built successfully" -ForegroundColor Green
    }
}

Write-Host ""

# ============= Phase 4: Build ABot mod (C# only, without C++ compilation) =============
Write-Host "[Phase 4] - Building ABot mod (C# layer only - C++ requires manual compilation)..." -ForegroundColor Yellow

$abotCsProjPath = Join-Path $workspaceRoot "Mods\ABot\ABot.csproj"

Write-Host "[Phase 4] Building ABot C# layer..." -ForegroundColor Cyan

$buildOutput = dotnet build $abotCsProjPath -c Debug 2>&1
$buildResult = $LASTEXITCODE

if ($buildResult -ne 0) {
    Write-Host "[Phase 4] - WARNING: Failed to build ABot (non-blocking)" -ForegroundColor Yellow
    Write-Host "[Phase 4] This might be due to missing C++/CLI DLL (will be gracefully handled at runtime)" -ForegroundColor Gray
    $failedMods += "ABot"
} else {
    Write-Host "[Phase 4] - OK ABot C# layer built successfully" -ForegroundColor Green
    Write-Host "[Phase 4] - NOTE: C++/CLI interop layer (ABot.CLI.dll) requires Visual Studio C++ compiler" -ForegroundColor Gray
}

Write-Host ""

# ============= Phase 5: Copy mod DLLs to output directory =============
Write-Host "[Phase 5] - Copying mod DLLs to output directories..." -ForegroundColor Yellow

# Create mods directory if it doesn't exist
if (-not (Test-Path $modsDir)) {
    New-Item -ItemType Directory -Force -Path $modsDir | Out-Null
    Write-Host "[Phase 5] Created mods directory: $modsDir" -ForegroundColor Cyan
}

$modDllMappings = @(
    @{
        ModName = "AIMod"
        SourceDll = "Mods\AIMod\bin\Debug\net10.0-windows\AIMod.dll"
        DestDir = "$modsDir"
    },
    @{
        ModName = "CustomizedReply"
        SourceDll = "Mods\CustomizedReply\bin\Debug\net10.0-windows\CustomizedReply.dll"
        DestDir = "$modsDir"
    },
    @{
        ModName = "ABot"
        SourceDll = "Mods\ABot\bin\Debug\net10.0-windows\ABot.dll"
        DestDir = "$modsDir"
    }
)

$copiedCount = 0

foreach ($mapping in $modDllMappings) {
    $sourcePath = Join-Path $workspaceRoot $mapping.SourceDll
    $destPath = Join-Path $mapping.DestDir (Split-Path $sourcePath -Leaf)
    
    if (Test-Path $sourcePath) {
        Copy-Item $sourcePath $destPath -Force
        Write-Host "[Phase 5] - OK Copied $($mapping.ModName) DLL" -ForegroundColor Green
        $copiedCount++
    } else {
        Write-Host "[Phase 5] - WARNING $($mapping.ModName) DLL not found (mod compilation failed or skipped)" -ForegroundColor Yellow
    }
}

Write-Host ""

# ============= Summary =============
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "                      BUILD SUMMARY                             " -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan

Write-Host ""
Write-Host "OK Main Program: SUCCESS" -ForegroundColor Green
Write-Host "OK Standard Mods: $(if ($failedMods.Count -eq 0) { 'SUCCESS' } else { 'PARTIAL (1 or more failed)' })" -ForegroundColor Green
Write-Host "OK Mod DLLs Copied: $copiedCount/$($modDllMappings.Count)" -ForegroundColor Green

if ($failedMods.Count -gt 0) {
    Write-Host ""
    Write-Host "- Failed Mods (non-blocking):" -ForegroundColor Yellow
    foreach ($mod in $failedMods) {
        Write-Host "  - $mod" -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "NOTE: Failed mods will NOT be available at runtime, but main program will still launch" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "1. Run: dotnet run --project MDiceV2.Launcher" -ForegroundColor Gray
Write-Host "2. Mods directory: $modsDir" -ForegroundColor Gray
Write-Host "3. For ABot C++ compilation, see Phase 1.5 guide" -ForegroundColor Gray

Write-Host ""
Write-Host "Build - OK Build process complete" -ForegroundColor Green
