@echo off
setlocal EnableExtensions

cd /d "%~dp0"

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This folder is not a Git repository.
    pause
    exit /b 1
)

for /f "delims=" %%i in ('git branch --show-current') do set "BRANCH=%%i"
if not defined BRANCH (
    echo [ERROR] Could not detect the current branch.
    pause
    exit /b 1
)

set "REMOTE=origin"
set "COMMIT_MSG=%*"
if not defined COMMIT_MSG set "COMMIT_MSG=Update project files"

echo Working directory: %cd%
echo Remote: %REMOTE%
echo Branch: %BRANCH%
echo Commit message: %COMMIT_MSG%
echo.

git add -A
if errorlevel 1 (
    echo [ERROR] git add failed.
    pause
    exit /b 1
)

git diff --cached --quiet
if errorlevel 1 goto commit_changes

echo No staged changes detected. Nothing to commit.
pause
exit /b 0

:commit_changes
git commit -m "%COMMIT_MSG%"
if errorlevel 1 (
    echo [ERROR] git commit failed.
    pause
    exit /b 1
)

git push %REMOTE% %BRANCH%
if errorlevel 1 (
    echo [ERROR] git push failed.
    pause
    exit /b 1
)

echo.
echo Done. Changes have been pushed to %REMOTE%/%BRANCH%.
pause
exit /b 0
