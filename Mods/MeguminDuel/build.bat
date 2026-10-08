@echo off
setlocal

set "MOD_DIR=%~dp0"
set "PROJECT=%MOD_DIR%MeguminDuel.csproj"
set "OUTPUT_DLL=%MOD_DIR%bin\Release\net10.0-windows\MeguminDuel.dll"
set "ROOT_DLL=%MOD_DIR%MeguminDuel.dll"

echo Building MeguminDuel only...
dotnet build "%PROJECT%" -c Release -p:RunVersionSyncOnBuild=false
if errorlevel 1 goto :failed

if not exist "%OUTPUT_DLL%" (
    echo Build completed, but the output DLL was not found:
    echo %OUTPUT_DLL%
    goto :failed
)

copy /Y "%OUTPUT_DLL%" "%ROOT_DLL%" >nul
if errorlevel 1 goto :failed

set /P MOD_VERSION=<"%MOD_DIR%mod-version.txt"
set "RELEASE_MMOD=%MOD_DIR%bin\Release\net10.0-windows\MeguminDuel-%MOD_VERSION%.mmod"

echo.
echo Build succeeded.
echo DLL:  %ROOT_DLL%
echo MMOD: %RELEASE_MMOD%
echo COPY: %MOD_DIR%..\..\artifacts\mods\MeguminDuel-%MOD_VERSION%.mmod
exit /b 0

:failed
echo.
echo Build failed.
exit /b 1
