@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if not "%~2"=="" goto usage
if "%~1"=="" goto preview
if /i "%~1"=="--preview" goto preview
if /i "%~1"=="--upload" goto upload
goto usage

:preview
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\update-source.ps1"
exit /b %errorlevel%

:upload
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\update-source.ps1" -Upload
exit /b %errorlevel%

:usage
echo Usage: update.bat [--preview ^| --upload]
echo Default: preview only. --upload requires confirmation before pushing.
exit /b 2
