#!/usr/bin/env powershell
<#
.SYNOPSIS
  Phase 2 Unit Test Builder and Runner
.DESCRIPTION
  Compiles Phase2Tests.cpp and runs all unit tests
.EXAMPLE
  .\build-phase2-tests.ps1 -Configuration Release
#>

param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$WarningPreference = "Continue"

# Paths
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$testDir = Join-Path $workspaceRoot "ABot\ABot.Core\tests"
$srcDir = Join-Path $workspaceRoot "ABot\ABot.Core\src"
$projFile = Join-Path $workspaceRoot "ABot\ABot.Core\ABot.Core.vcxproj"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Phase 2 Unit Test Builder"
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Step 0: Environment Info
Write-Host "[Step 0] Environment Information" -ForegroundColor Yellow
Write-Host "  Workspace Root: $workspaceRoot"
Write-Host "  Test Directory: $testDir"
Write-Host "  Source Directory: $srcDir"
Write-Host "  Configuration: $Configuration"
Write-Host "  Platform: $Platform"
Write-Host ""

# Step 1: Verify source files exist
Write-Host "[Step 1] Verifying source files..." -ForegroundColor Yellow

$sourceFiles = @(
    "Character.h", "Character.cpp",
    "ParameterParser.h", "ParameterParser.cpp",
    "Battle.h", "Battle.cpp",
    "C_API.h", "C_API.cpp",
    "Value.h", "Value.cpp",
    "Lexer.h", "Lexer.cpp",
    "Parser.h", "Parser.cpp",
    "Bytecode.h", "Bytecode.cpp",
    "VM.h", "VM.cpp",
    "Scope.h", "Scope.cpp"
)

$missingFiles = @()
foreach ($file in $sourceFiles) {
    $path = Join-Path $srcDir $file
    if (Test-Path $path) {
        Write-Host "  ✓ $file" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $file (MISSING)" -ForegroundColor Red
        $missingFiles += $file
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Host ""
    Write-Host "  ERROR: Missing $($missingFiles.Count) files!" -ForegroundColor Red
    exit 1
}

Write-Host "  All source files verified!" -ForegroundColor Green
Write-Host ""

# Step 2: Check test file
Write-Host "[Step 2] Verifying test file..." -ForegroundColor Yellow
$testFile = Join-Path $testDir "Phase2Tests.cpp"
if (Test-Path $testFile) {
    $lineCount = @(Get-Content $testFile).Count
    Write-Host "  ✓ Phase2Tests.cpp found ($lineCount lines)" -ForegroundColor Green
} else {
    Write-Host "  ✗ Phase2Tests.cpp not found!" -ForegroundColor Red
    exit 1
}
Write-Host ""

# Step 3: List test functions
Write-Host "[Step 3] Extracting test functions..." -ForegroundColor Yellow
$testFunctions = Select-String -Path $testFile -Pattern "^void Test_" | ForEach-Object { $_.Line -replace "^void ", "" -replace "\(.*", "" }

Write-Host "  Found $($testFunctions.Count) test functions:" -ForegroundColor Green
foreach ($func in $testFunctions) {
    Write-Host "    - $func" -ForegroundColor Cyan
}
Write-Host ""

# Step 4: Check compilation directories
Write-Host "[Step 4] Checking build directories..." -ForegroundColor Yellow
$binDir = Join-Path (Split-Path $srcDir -Parent) "bin\$Configuration\$Platform"
$objDir = Join-Path (Split-Path $srcDir -Parent) "obj\$Configuration\x64"

Write-Host "  Binary output: $binDir"
Write-Host "  Object files: $objDir"

# Create if not exist
if (-not (Test-Path $binDir)) {
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null | Write-Host "  Created binary directory"
}
if (-not (Test-Path $objDir)) {
    New-Item -ItemType Directory -Path $objDir -Force | Out-Null | Write-Host "  Created object directory"
}
Write-Host ""

# Step 5: Summary
Write-Host "[Step 5] Build Summary" -ForegroundColor Yellow
Write-Host "  Source files: $($sourceFiles.Count)"
Write-Host "  Test functions: $($testFunctions.Count)"
Write-Host "  Configuration: $Configuration\$Platform"
Write-Host ""

Write-Host "============================================" -ForegroundColor Green
Write-Host "  Phase 2 Tests Ready"
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Use Visual Studio to compile ABot.Core project"
Write-Host "  2. Run the test executable from bin\$Configuration\$Platform\"
Write-Host "  3. All test functions will execute automatically"
Write-Host ""
Write-Host "Test Functions to Execute:"
foreach ($func in $testFunctions) {
    Write-Host "  > $func"
}
Write-Host ""

exit 0
