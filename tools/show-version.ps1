#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Show current version and build counter information

.DESCRIPTION
    Displays the current version (from Version.props and build.counter)
    and provides options to manage version numbers.

.PARAMETER Show
    Show current version information

.PARAMETER Bump
    Manually bump the version counter

.PARAMETER Reset
    Reset counter to 1

.PARAMETER ShowAll
    Show detailed version and assembly information from all projects

.EXAMPLE
    ./show-version.ps1 -Show
    ./show-version.ps1 -Bump
    ./show-version.ps1 -ShowAll
#>

[CmdletBinding()]
param(
    [switch]$Show,
    [switch]$Bump,
    [switch]$Reset,
    [switch]$ShowAll
)

$ErrorActionPreference = "Stop"

$rootPath = Split-Path -Parent $PSScriptRoot
$versionPropsPath = Join-Path $rootPath "Version.props"
$counterFile = Join-Path $rootPath "build.counter"

function Get-VersionInfo {
    if (-not (Test-Path $versionPropsPath)) {
        Write-Host "[ERROR] Version.props not found" -ForegroundColor Red
        return $null
    }

    [xml]$xml = Get-Content -Path $versionPropsPath -Raw
    $pg = $xml.Project.PropertyGroup | Select-Object -First 1
    
    if ($pg) {
        $versionBase = $pg.VersionBase
        if (-not $versionBase -or $versionBase -match "^\s*$") {
            # Fallback for old format
            $versionBase = $pg.AssemblyVersion
            if ($versionBase -match "(\d+\.\d+\.\d+)") {
                $versionBase = $matches[1]
            }
        }
        
        $infoVersion = $pg.InformationalVersion
        
        return @{
            VersionBase = $versionBase
            InformationalVersion = $infoVersion
        }
    }
    
    return $null
}

function Get-BuildCounter {
    $counter = 1
    if (Test-Path $counterFile) {
        [int]::TryParse((Get-Content -Path $counterFile -Raw).Trim(), [ref]$counter) | Out-Null
    }
    return $counter
}

function Show-VersionInfo {
    Write-Host ""
    Write-Host "========== MDiceV2 Version Information ==========" -ForegroundColor Cyan
    Write-Host ""
    
    $versionInfo = Get-VersionInfo
    if (-not $versionInfo) { return }
    
    $counter = Get-BuildCounter
    $fullVersion = "$($versionInfo.VersionBase).$counter"
    
    Write-Host "Version Files:" -ForegroundColor Yellow
    Write-Host "  Version.props: $versionPropsPath"
    Write-Host "  build.counter: $counterFile"
    Write-Host ""
    
    Write-Host "Version Components:" -ForegroundColor Yellow
    Write-Host "  Major.Minor.Patch: $($versionInfo.VersionBase)"
    Write-Host "  Build Counter:     $counter"
    Write-Host "  Full Version:      $fullVersion"
    Write-Host "  Suffix:            $($versionInfo.InformationalVersion)"
    Write-Host ""
    
    Write-Host "Assembly Version:" -ForegroundColor Green
    Write-Host "  AssemblyVersion:        $fullVersion"
    Write-Host "  FileVersion:            $fullVersion"
    Write-Host "  InformationalVersion:   $fullVersion-$($versionInfo.InformationalVersion)"
    Write-Host ""
}

function Bump-Counter {
    $counter = Get-BuildCounter
    $newCounter = $counter + 1
    Set-Content -Path $counterFile -Value $newCounter -Encoding UTF8
    Write-Host "[+] Build counter bumped from $counter to $newCounter" -ForegroundColor Green
}

function Reset-Counter {
    Set-Content -Path $counterFile -Value 1 -Encoding UTF8
    Write-Host "[+] Build counter reset to 1" -ForegroundColor Green
}

function Show-AssemblyVersions {
    Write-Host ""
    Write-Host "========== Assembly Versions ==========" -ForegroundColor Cyan
    Write-Host ""
    
    $counter = Get-BuildCounter
    $versionInfo = Get-VersionInfo
    $fullVersion = "$($versionInfo.VersionBase).$counter"
    
    Write-Host "Checking assembly versions in compiled output..." -ForegroundColor Yellow
    Write-Host ""
    
    $paths = @(
        (Join-Path $rootPath "MDiceV2_Debug/MDiceV2.Launcher.exe"),
        (Join-Path $rootPath "MDiceV2_Debug/MDiceV2.Core.Dice"),
        (Join-Path $rootPath "MDiceV2_Debug/MDiceV2.Launcher.dll"),
        (Join-Path $rootPath "MDiceV2.Launcher/bin/Debug/net10.0-windows/MDiceV2.Launcher.exe"),
        (Join-Path $rootPath "MDiceV2.Core/bin/Debug/net10.0-windows/MDiceV2.Core.dll")
    )
    
    foreach ($path in $paths) {
        if (Test-Path $path) {
            try {
                $file = [System.Reflection.AssemblyName]::GetAssemblyName($path)
                $name = Split-Path -Leaf (Split-Path $path -Parent)
                $fileName = Split-Path -Leaf $path
                Write-Host "  $fileName" -ForegroundColor Cyan
                Write-Host "    Location: $path"
                Write-Host "    Version:  $($file.Version)"
                Write-Host ""
            }
            catch {
                # Not a .NET assembly
            }
        }
    }
    
    Write-Host "Next build will use version: $fullVersion" -ForegroundColor Green
    Write-Host ""
}

# Main execution
if (-not ($Show -or $Bump -or $Reset -or $ShowAll)) {
    $Show = $true  # Default action
}

if ($Show) {
    Show-VersionInfo
}

if ($Bump) {
    Bump-Counter
    Show-VersionInfo
}

if ($Reset) {
    Reset-Counter
    Show-VersionInfo
}

if ($ShowAll) {
    Show-VersionInfo
    Show-AssemblyVersions
}
