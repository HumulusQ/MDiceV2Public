[CmdletBinding()]
param(
    [string]$PropsPath = "../Version.props",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Write-Success {
    param([string]$Message)
    Write-Host "[+] $Message" -ForegroundColor Green
}

function Write-Warning-Custom {
    param([string]$Message)
    Write-Host "[!] $Message" -ForegroundColor Yellow
}

function Write-Error-Custom {
    param([string]$Message)
    Write-Host "[-] $Message" -ForegroundColor Red
}

function Write-Info {
    param([string]$Message)
    Write-Host "    $Message"
}

function Get-VersionFromProps {
    param([string]$PropsFile)
    
    if (-not (Test-Path $PropsFile)) {
        throw "Version.props not found: $PropsFile"
    }
    
    [xml]$xml = Get-Content -Path $PropsFile -Raw
    $pg = $xml.Project.PropertyGroup | Select-Object -First 1
    
    if (-not $pg) {
        throw "PropertyGroup not found"
    }
    
    $version = $pg.AssemblyVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "AssemblyVersion not defined"
    }
    
    $infoVersion = $pg.InformationalVersion
    if ([string]::IsNullOrWhiteSpace($infoVersion)) {
        $infoVersion = ""
    }
    
    return @{
        AssemblyVersion = $version.ToString().Trim()
        InformationalVersion = $infoVersion.ToString().Trim()
    }
}

function Update-ProjectVersion {
    param(
        [string]$ProjectFile,
        [hashtable]$Versions,
        [switch]$DryRun
    )
    
    if (-not (Test-Path $ProjectFile)) {
        return $false
    }
    
    [xml]$xml = Get-Content -Path $ProjectFile
    $pg = $xml.Project.PropertyGroup | Select-Object -First 1
    
    if (-not $pg) {
        $pg = $xml.Project.AppendChild($xml.CreateElement('PropertyGroup'))
    }
    
    $oldVer = if ($pg.Version) { $pg.Version } else { "" }
    
    if (-not $pg.Version) {
        $pg.AppendChild($xml.CreateElement('Version')).InnerText = $Versions["AssemblyVersion"]
    } else {
        $pg.Version = $Versions["AssemblyVersion"]
    }
    
    if (-not $pg.AssemblyVersion) {
        $pg.AppendChild($xml.CreateElement('AssemblyVersion')).InnerText = $Versions["AssemblyVersion"]
    } else {
        $pg.AssemblyVersion = $Versions["AssemblyVersion"]
    }
    
    if (-not $pg.FileVersion) {
        $pg.AppendChild($xml.CreateElement('FileVersion')).InnerText = $Versions["AssemblyVersion"]
    } else {
        $pg.FileVersion = $Versions["AssemblyVersion"]
    }
    
    if (-not [string]::IsNullOrWhiteSpace($Versions["InformationalVersion"])) {
        if (-not $pg.InformationalVersion) {
            $pg.AppendChild($xml.CreateElement('InformationalVersion')).InnerText = $Versions["InformationalVersion"]
        } else {
            $pg.InformationalVersion = $Versions["InformationalVersion"]
        }
    }
    
    if ($oldVer -ne $Versions["AssemblyVersion"]) {
        if (-not $DryRun) {
            $xml.Save($ProjectFile)
        }
        return $true
    }
    
    return $false
}

function Find-ProjectFiles {
    param([string]$RootPath)
    
    $projects = @()
    
    $projects += (Join-Path $RootPath "MDiceV2.Core\MDiceV2.Core.csproj")
    $projects += (Join-Path $RootPath "MDiceV2.Launcher\MDiceV2.Launcher.csproj")
    $projects += (Join-Path $RootPath "MDiceV2.Abstractions\MDiceV2.Abstractions.csproj")
    $projects += (Join-Path $RootPath "MDiceV2.Interfaces\MDiceV2.Interfaces.csproj")
    $projects += (Join-Path $RootPath "MDiceV2.Console\MDiceV2.Console.csproj")
    
    $modPaths = Get-ChildItem -Path (Join-Path $RootPath "Mods") -Directory -ErrorAction SilentlyContinue
    foreach ($modPath in $modPaths) {
        $csprojPath = Join-Path $modPath.FullName "$($modPath.Name).csproj"
        if (Test-Path $csprojPath) {
            $projects += $csprojPath
        }
    }
    
    return ($projects | Where-Object { Test-Path $_ })
}

function Main {
    $startTime = Get-Date
    $rootPath = Resolve-Path (Join-Path $PSScriptRoot "..")
    $propsFullPath = Join-Path $rootPath "Version.props"
    
    if (-not (Test-Path $propsFullPath)) {
        $propsFullPath = Resolve-Path (Join-Path $PSScriptRoot $PropsPath)
    }
    
    Write-Host ""
    Write-Host "========== Version Sync ==========" -ForegroundColor Cyan
    
    if ($DryRun) {
        Write-Warning-Custom "DRY RUN mode - no files will be modified"
    }
    
    Write-Info "Working directory: $rootPath"
    Write-Info "Version file: $propsFullPath"
    Write-Host ""
    
    try {
        Write-Host "Reading version info..." -ForegroundColor Cyan
        $versions = Get-VersionFromProps -PropsFile $propsFullPath
        Write-Success "Assembly: $($versions['AssemblyVersion'])"
        Write-Success "Informational: $($versions['InformationalVersion'])"
        
        Write-Host "Finding projects..." -ForegroundColor Cyan
        $projects = Find-ProjectFiles -RootPath $rootPath
        Write-Info "Found $($projects.Count) projects"
        
        Write-Host "Syncing versions..." -ForegroundColor Cyan
        $updated = 0
        $skipped = 0
        
        foreach ($project in $projects) {
            $projectName = Split-Path -Leaf $project
            
            $isUpdated = Update-ProjectVersion `
                -ProjectFile $project `
                -Versions $versions `
                -DryRun:$DryRun
            
            if ($isUpdated) {
                Write-Success "$projectName"
                $updated++
            }
            else {
                if ($VerbosePreference -ne 'SilentlyContinue') {
                    Write-Info "$projectName (no changes)"
                }
                $skipped++
            }
        }
        
        Write-Host ""
        Write-Host "========== Summary ==========" -ForegroundColor Cyan
        Write-Info "Updated: $updated projects"
        Write-Info "Unchanged: $skipped projects"
        
        if ($DryRun) {
            Write-Warning-Custom "DRY RUN - files NOT modified"
        } else {
            Write-Success "Version sync complete!"
        }
        
        return 0
    }
    catch {
        Write-Host ""
        Write-Error-Custom $_
        return 1
    }
}

exit (Main)
