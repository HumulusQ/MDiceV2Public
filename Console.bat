@echo off
setlocal enabledelayedexpansion

cd /d "%~dp0"

echo ========================================
echo MDiceV2 Console Launcher (BAT version)
echo ========================================

:: 直接使用PowerShell获取当前进程树，找到批处理文件的PID
for /f %%i in ('powershell -NoProfile -Command "Get-WmiObject Win32_Process -Filter \"CommandLine LIKE '%%%~nx0%%'\" | Select-Object -First 1 | ForEach-Object { $_.ProcessId }"') do set PARENT_PID=%%i

if "%PARENT_PID%"=="" (
    echo WARNING: Could not get parent PID, starting without monitoring
    echo.
    echo Starting Core in headless mode...
    echo Output will be saved to core_output.log
    echo ========================================
    echo.
    "Core\MDiceV2.Core.Dice" --headless > core_output.log 2>&1
) else (
    echo Console PID: %PARENT_PID%
    echo Base Directory: %CD%
    echo.
    echo Starting Core in headless mode...
    echo Output will be saved to core_output.log
    echo ========================================
    echo.
    "Core\MDiceV2.Core.Dice" --headless --parent-pid=%PARENT_PID% > core_output.log 2>&1
)

:: 等待Core退出
set EXIT_CODE=%ERRORLEVEL%

echo.
echo ========================================
echo Core exited with code: %EXIT_CODE%
echo ========================================
echo.
echo Core output:
echo ========================================
type core_output.log
echo ========================================
pause