@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "PUBLISH_SCRIPT=%SCRIPT_DIR%tools\publish.ps1"
set "TOKEN_FILE=%SCRIPT_DIR%token.txt"
set "SKIP_PAUSE=0"
set "KEEP_RELEASES=10"
set "CLEANUP_OPTION="
set "CLEANUP_MODE="

:parse_args
if "%~1"=="" goto args_parsed
set "ARG=%~1"
if /I "!ARG!"=="/nopause" set "SKIP_PAUSE=1"
if /I "!ARG!"=="/nocleanup" set "CLEANUP_OPTION=-SkipReleaseCleanup"
if /I "!ARG!"=="/cleanup" set "CLEANUP_MODE=-ReleaseCleanupOnly"
if /I "!ARG!"=="/previewcleanup" set "CLEANUP_MODE=-ReleaseCleanupOnly -PreviewReleaseCleanup"
if /I "!ARG:~0,6!"=="/keep:" set "KEEP_RELEASES=!ARG:~6!"
shift
goto parse_args

:args_parsed

if not exist "%PUBLISH_SCRIPT%" (
    echo Publish script not found: "%PUBLISH_SCRIPT%"
    exit /b 1
)

echo ===================================
echo MDiceV2 GitHub publish script
echo ===================================
echo.
echo Release retention: keep newest %KEEP_RELEASES%
if defined CLEANUP_OPTION echo Release cleanup: disabled for this run
echo.

if not exist "%TOKEN_FILE%" if "%GITHUB_TOKEN%"=="" if "%GH_TOKEN%"=="" (
    echo GitHub token not found. Create token.txt or set GITHUB_TOKEN/GH_TOKEN.
    if "%SKIP_PAUSE%"=="0" pause
    exit /b 1
)

echo Running GitHub publish...
powershell -NoProfile -ExecutionPolicy Bypass -File "%PUBLISH_SCRIPT%" -KeepReleases "%KEEP_RELEASES%" %CLEANUP_OPTION% %CLEANUP_MODE%
set "EXITCODE=!ERRORLEVEL!"

if not "!EXITCODE!"=="0" (
    echo.
    echo Publish failed with exit code !EXITCODE!.
    if "!SKIP_PAUSE!"=="0" pause
    exit /b !EXITCODE!
)

echo.
echo Publish complete.
echo Output directory: "%SCRIPT_DIR%MDiceV2_Published"
if "!SKIP_PAUSE!"=="0" pause
endlocal
