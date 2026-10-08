# ABot C++ Compilation Script
# Compiles C++17 projects without Visual Studio IDE
param(
    [switch]$Clean,
    [switch]$Verbose
)

# Visual Studio paths
$VSPath = "C:\Program Files\Microsoft Visual Studio\18\Community"
$VCToolPath = "$VSPath\VC\Tools\MSVC\14.50.35717"
$CompilerPath = "$VCToolPath\bin\Hostx64\x64\cl.exe"
$LinkerPath = "$VCToolPath\bin\Hostx64\x64\link.exe"
$IncludePath = "$VCToolPath\include"
$LibPath = "$VCToolPath\lib\x64"
$WindowsKitPath = "C:\Program Files (x86)\Windows Kits\10"
$WindowsKitVersion = "10.0.22621.0"

# Project paths
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$RootPath = $workspaceRoot

$CoreProjectPath = "$RootPath\ABot\ABot.Core"
$CoreSrcPath = "$CoreProjectPath\src"
$CoreOutDir = "$RootPath\bin\Debug\x64"
$CoreObjDir = "$RootPath\obj\Debug\ABot.Core\x64"

$CliProjectPath = "$RootPath\ABot\ABot.CLI"
$CliOutDir = "$RootPath\bin\Debug\x64"
$CliObjDir = "$RootPath\obj\Debug\ABot.CLI\x64"

# Create output directories
if (-not (Test-Path $CoreOutDir)) { New-Item -ItemType Directory -Path $CoreOutDir -Force | Out-Null }
if (-not (Test-Path $CoreObjDir)) { New-Item -ItemType Directory -Path $CoreObjDir -Force | Out-Null }
if (-not (Test-Path $CliObjDir)) { New-Item -ItemType Directory -Path $CliObjDir -Force | Out-Null }

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning build directories..." -ForegroundColor Yellow
    Remove-Item "$CoreObjDir\*" -Force -Recurse -ErrorAction SilentlyContinue
    Remove-Item "$CliObjDir\*" -Force -Recurse -ErrorAction SilentlyContinue
    Write-Host "✓ Clean complete" -ForegroundColor Green
}

# Set up environment for compilation
Write-Host "Setting up compiler environment..." -ForegroundColor Cyan
$env:PATH = "$VCToolPath\bin\Hostx64\x64;$WindowsKitPath\bin\$WindowsKitVersion\x64;$env:PATH"
$env:INCLUDE = "$IncludePath;$WindowsKitPath\Include\$WindowsKitVersion\ucrt;$WindowsKitPath\Include\$WindowsKitVersion\um;$CoreSrcPath"
$env:LIB = "$LibPath;$WindowsKitPath\Lib\$WindowsKitVersion\ucrt\x64;$WindowsKitPath\Lib\$WindowsKitVersion\um\x64"

# Compiler flags (C++17, MultiThreadedDebug, RTTI, EH)
$CompileFlags = @(
    "/c"                      # Compile only
    "/std:c++17"              # C++17 standard
    "/ZI"                     # Debug info
    "/Od"                     # No optimization
    "/MTd"                    # MultiThreaded Debug runtime
    "/EHsc"                   # Exception handling
    "/RTC1"                   # Runtime checks
    "/GR"                     # RTTI enabled
    "/D_DEBUG"                # Debug define
    "/D_CRT_SECURE_NO_WARNINGS"
    "/I`"$IncludePath`""
    "/I`"$CoreSrcPath`""
    "/I`"$WindowsKitPath\Include\$WindowsKitVersion\ucrt`""
    "/I`"$WindowsKitPath\Include\$WindowsKitVersion\um`""
    "/W4"                     # Warning level 4
    "/Fd$CoreObjDir\vc.pdb"   # Debug database
)

# ============================================================================
# Phase 1: Compile ABot.Core C++ files to object files
# ============================================================================
Write-Host "`n=== Phase 1: Compiling ABot.Core ===" -ForegroundColor Cyan

# Get all .cpp files in ABot.Core\src
$CppFiles = @(
    "Lexer.cpp",
    "Parser.cpp",
    "Bytecode.cpp",
    "VM.cpp",
    "Value.cpp",
    "C_API.cpp"
)

$ObjectFiles = @()
$CompileSuccess = $true

foreach ($cppFile in $CppFiles) {
    $SourceFile = "$CoreSrcPath\$cppFile"
    $ObjectFile = "$CoreObjDir\$($cppFile -replace '\.cpp', '.obj')"
    
    if (-not (Test-Path $SourceFile)) {
        Write-Host "✗ Source not found: $SourceFile" -ForegroundColor Red
        $CompileSuccess = $false
        continue
    }
    
    Write-Host "  Compiling $cppFile..." -ForegroundColor White
    
    # Compile
    & $CompilerPath $CompileFlags "/Fo$ObjectFile" $SourceFile 2>&1 | ForEach-Object {
        if ($_ -match "error") {
            Write-Host "    ERROR: $_" -ForegroundColor Red
            $CompileSuccess = $false
        } elseif ($Verbose) {
            Write-Host "    $_"
        }
    }
    
    if (Test-Path $ObjectFile) {
        Write-Host "  ✓ Created $($cppFile -replace '\.cpp').obj" -ForegroundColor Green
        $ObjectFiles += $ObjectFile
    } else {
        Write-Host "  ✗ Failed to create object file" -ForegroundColor Red
        $CompileSuccess = $false
    }
}

if (-not $CompileSuccess) {
    Write-Host "`n✗ ABot.Core compilation failed" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 2: Link object files into static library (ABot.Core.lib)
# ============================================================================
Write-Host "`n=== Phase 2: Linking ABot.Core ===" -ForegroundColor Cyan

$CoreLib = "$CoreOutDir\ABot.Core.lib"
$LibFlags = @(
    "/MACHINE:X64"
    "/OUT:$CoreLib"
)

Write-Host "  Linking to $CoreLib..." -ForegroundColor White
& $LinkerPath $LibFlags $ObjectFiles 2>&1 | ForEach-Object {
    if ($_ -match "error") {
        Write-Host "  ERROR: $_" -ForegroundColor Red
        exit 1
    } elseif ($Verbose) {
        Write-Host "  $_"
    }
}

if (Test-Path $CoreLib) {
    $LibSize = [math]::Round((Get-Item $CoreLib).Length / 1MB, 1)
    Write-Host "  ✓ Created ABot.Core.lib ($LibSize MB)" -ForegroundColor Green
} else {
    Write-Host "  ✗ Failed to create ABot.Core.lib" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 3: Compile ABot.CLI (C++/CLI) with CLR support
# ============================================================================
Write-Host "`n=== Phase 3: Compiling ABot.CLI ===" -ForegroundColor Cyan

$CliCompileFlags = @(
    "/c"
    "/std:c++17"
    "/ZI"
    "/Od"
    "/MTd"
    "/clr"                   # Common Language Runtime
    "/RTC1"
    "/GR"
    "/D_DEBUG"
    "/D_CRT_SECURE_NO_WARNINGS"
    "/I`"$IncludePath`""
    "/I`"$CoreSrcPath`""
    "/I`"$CliProjectPath`""
    "/W4"
    "/Fd$CliObjDir\vc.pdb"
)

$CliCppFile = "ABotInterop.cpp"
$CliSourceFile = "$CliProjectPath\$CliCppFile"
$CliObjectFile = "$CliObjDir\ABotInterop.obj"

if (-not (Test-Path $CliSourceFile)) {
    Write-Host "✗ Source not found: $CliSourceFile" -ForegroundColor Red
    exit 1
}

Write-Host "  Compiling $CliCppFile..." -ForegroundColor White
& $CompilerPath $CliCompileFlags "/Fo$CliObjectFile" $CliSourceFile 2>&1 | ForEach-Object {
    if ($_ -match "error") {
        Write-Host "    ERROR: $_" -ForegroundColor Red
        exit 1
    } elseif ($Verbose) {
        Write-Host "    $_"
    }
}

if (Test-Path $CliObjectFile) {
    Write-Host "  ✓ Created ABotInterop.obj" -ForegroundColor Green
} else {
    Write-Host "  ✗ Failed to create ABotInterop.obj" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 4: Link ABot.CLI to DLL
# ============================================================================
Write-Host "`n=== Phase 4: Linking ABot.CLI ===" -ForegroundColor Cyan

$CliDll = "$CliOutDir\ABot.CLI.dll"
$CliLib = "$CliOutDir\ABot.CLI.lib"
$CliPdb = "$CliOutDir\ABot.CLI.pdb"

$DllLinkFlags = @(
    "/DLL"
    "/MACHINE:X64"
    "/OUT:$CliDll"
    "/IMPLIB:$CliLib"
    "/PDB:$CliPdb"
    "$CoreLib"                # Link ABot.Core.lib
    "mscoree.lib"             # CLR runtime
    "kernel32.lib"
    "user32.lib"
)

Write-Host "  Linking to $CliDll..." -ForegroundColor White
& $LinkerPath $DllLinkFlags $CliObjectFile 2>&1 | ForEach-Object {
    if ($_ -match "error") {
        Write-Host "  ERROR: $_" -ForegroundColor Red
        exit 1
    } elseif ($Verbose) {
        Write-Host "  $_"
    }
}

if (Test-Path $CliDll) {
    $DllSize = [math]::Round((Get-Item $CliDll).Length / 1MB, 1)
    Write-Host "  ✓ Created ABot.CLI.dll ($DllSize MB)" -ForegroundColor Green
} else {
    Write-Host "  ✗ Failed to create ABot.CLI.dll" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 5: Copy to mods directory
# ============================================================================
Write-Host "`n=== Phase 5: Deploying to mods ===" -ForegroundColor Cyan

$ModDestPath = "$RootPath\MDiceV2_Debug\mods\ABot"
if (-not (Test-Path $ModDestPath)) {
    New-Item -ItemType Directory -Path $ModDestPath -Force | Out-Null
}

Copy-Item $CliDll $ModDestPath -Force
Copy-Item $CliPdb $ModDestPath -Force -ErrorAction SilentlyContinue

Write-Host "  ✓ Copied DLL to $ModDestPath" -ForegroundColor Green

# ============================================================================
# Success
# ============================================================================
Write-Host "`n=== ✓ Build Complete ===" -ForegroundColor Green
Write-Host "ABot.Core.lib: $CoreLib" -ForegroundColor White
Write-Host "ABot.CLI.dll:  $CliDll" -ForegroundColor White
Write-Host "`nNext: Run .\tools\run-debug.ps1 to test" -ForegroundColor Cyan
