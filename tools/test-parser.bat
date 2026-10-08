@echo off
REM Quick Parser Test Script - Validates Lexer/Parser without full build
REM This tests just the Parser/Lexer compilation which already succeeded

setlocal enabledelayedexpansion

set WORKSPACE=%~dp0..
set CORE_SRC=!WORKSPACE!\ABot\ABot.Core\src
set TEST_OUT_DIR=!WORKSPACE!\bin\Test\x64
set OBJ_TEST=!WORKSPACE!\obj\Test\x64

if not exist "!TEST_OUT_DIR!" mkdir "!TEST_OUT_DIR!"
if not exist "!OBJ_TEST!" mkdir "!OBJ_TEST!"

echo.
echo ======================================================
echo Testing ABOT Script Parser
echo ======================================================
echo.

REM Setup compiler
set VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools
set VCVARS=!VS_PATH!\VC\Auxiliary\Build\vcvars64.bat

if not exist "!VCVARS!" (
    echo Error: Visual Studio Build Tools not found
    exit /b 1
)

call "!VCVARS!" > nul 2>&1

echo [1/3] Compiling test file...
cl.exe /c /std:c++17 /ZI /Od /MTd /RTC1 /GR /utf-8 ^
    /D_DEBUG /D_CRT_SECURE_NO_WARNINGS ^
    /I"!CORE_SRC!" /W4 ^
    /Fo"!OBJ_TEST!\TestAssignmentParsing.obj" ^
    "!CORE_SRC!\TestAssignmentParsing.cpp" 1>nul 2>&1

if errorlevel 1 (
    echo ✗ Failed to compile test - retrying with details...
    cl.exe /c /std:c++17 /ZI /Od /MTd /RTC1 /GR ^
        /D_DEBUG /D_CRT_SECURE_NO_WARNINGS ^
        /I"!CORE_SRC!" /W4 ^
        /Fo"!OBJ_TEST!\TestAssignmentParsing.obj" ^
        "!CORE_SRC!\TestAssignmentParsing.cpp"
    exit /b 1
)

echo ✓ Test compiled



echo [2/3] Linking test executable...
link.exe /MACHINE:X64 /OUT:"!TEST_OUT_DIR!\TestAssignmentParsing.exe" ^
    "!OBJ_TEST!\TestAssignmentParsing.obj" ^
    "!WORKSPACE!\obj\Debug\ABot.Core\x64\Lexer.obj" ^
    "!WORKSPACE!\obj\Debug\ABot.Core\x64\Parser.obj" ^
    "!WORKSPACE!\obj\Debug\ABot.Core\x64\Bytecode.obj" 1>nul 2>&1

if errorlevel 1 (
    echo ✗ Failed to link test - retrying with details...
    link.exe /MACHINE:X64 /OUT:"!TEST_OUT_DIR!\TestAssignmentParsing.exe" ^
        "!OBJ_TEST!\TestAssignmentParsing.obj" ^
        "!WORKSPACE!\obj\Debug\ABot.Core\x64\Lexer.obj" ^
        "!WORKSPACE!\obj\Debug\ABot.Core\x64\Parser.obj" ^
        "!WORKSPACE!\obj\Debug\ABot.Core\x64\Bytecode.obj"
    exit /b 1
)

echo ✓ Test linked



echo [3/3] Running tests...
echo.
echo ======================================================

"!TEST_OUT_DIR!\TestAssignmentParsing.exe"

if errorlevel 1 (
    echo.
    echo ✗ Tests failed
    exit /b 1
) else (
    echo.
    echo ✓ All tests passed
)

echo ======================================================
echo.
