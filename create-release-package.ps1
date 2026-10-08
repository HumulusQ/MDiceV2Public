param(
    [string]$Version = "0.2.5.2720",
    [string]$OutputDir = ".\releases",
    [switch]$IncludeConsole = $false,
    [switch]$IncludeDebugSymbols = $false
)

$ErrorActionPreference = "Stop"

function Write-Section($Title) {
    Write-Host ""
    Write-Host "===================================================================" -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host "===================================================================" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Success($Message) {
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Info($Message) {
    Write-Host "[*] $Message" -ForegroundColor Cyan
}

function Write-Error($Message) {
    Write-Host "[!] $Message" -ForegroundColor Red
}

try {
    Write-Host ""
    Write-Host "===================================================================" -ForegroundColor Cyan
    Write-Host "  MDiceV2 Release Package Creator" -ForegroundColor Cyan
    Write-Host "===================================================================" -ForegroundColor Cyan
    Write-Host ""
    
    Write-Info "Configuration:"
    Write-Info "  Version: $Version"
    Write-Info "  Output: $OutputDir"
    Write-Info "  Include Console: $IncludeConsole"
    Write-Info "  Include Debug Symbols: $IncludeDebugSymbols"
    Write-Host ""
    
    $ProjectRoot = Get-Location
    $LauncherPublish = "MDiceV2.Launcher\bin\Release\net10.0-windows\win-x64\publish"
    $ConsolePublish = "MDiceV2.Console\bin\Release\net10.0-windows\win-x64\publish"
    
    Write-Section "Verifying Build Artifacts"
    
    if (-not (Test-Path $LauncherPublish)) {
        Write-Error "Launcher publish path not found: $LauncherPublish"
        Write-Info "Please run: dotnet publish MDiceV2.Launcher -c Release -p:PublishSingleFile=true"
        exit 1
    }
    Write-Success "Launcher artifacts found"
    
    if ($IncludeConsole) {
        if (-not (Test-Path $ConsolePublish)) {
            Write-Error "Console publish path not found: $ConsolePublish"
            exit 1
        }
        Write-Success "Console artifacts found"
    }
    
    Write-Host ""
    Write-Section "Creating Release Packages"
    
    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }
    
    $LauncherPackage = Join-Path $OutputDir "MDiceV2-Launcher-$Version"
    
    if (Test-Path $LauncherPackage) {
        Write-Info "Cleaning old Launcher package..."
        Remove-Item -Path $LauncherPackage -Recurse -Force
    }
    
    New-Item -ItemType Directory -Path $LauncherPackage -Force | Out-Null
    Write-Info "Creating Launcher package..."
    
    $RequiredFiles = @("MDiceV2.Launcher.exe", "MDiceV2.Core.dll")
    $NativeDlls = @("av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll")
    
    foreach ($file in $RequiredFiles) {
        $src = Join-Path $LauncherPublish $file
        if (Test-Path $src) {
            Copy-Item $src $LauncherPackage -Force
            $fileSize = [math]::Round((Get-Item $src).Length/1MB, 2)
            Write-Success "$file ($fileSize MB)"
        }
    }
    
    foreach ($dll in $NativeDlls) {
        $src = Join-Path $LauncherPublish $dll
        if (Test-Path $src) {
            Copy-Item $src $LauncherPackage -Force
            $dllSize = [math]::Round((Get-Item $src).Length/1MB, 2)
            Write-Info "$dll ($dllSize MB)"
        }
    }
    
    if ($IncludeDebugSymbols) {
        Write-Info "Including debug symbols..."
        Get-Item "$LauncherPublish\*.pdb" -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName $LauncherPackage
            Write-Info "$($_.Name)"
        }
    }
    
    $LauncherSize = (Get-ChildItem $LauncherPackage -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Success "Launcher package created: $([math]::Round($LauncherSize, 2)) MB"
    Write-Info "Location: $LauncherPackage"
    Write-Host ""
    
    if ($IncludeConsole) {
        $ConsolePackage = Join-Path $OutputDir "MDiceV2-Console-$Version"
        
        if (Test-Path $ConsolePackage) {
            Write-Info "Cleaning old Console package..."
            Remove-Item -Path $ConsolePackage -Recurse -Force
        }
        
        New-Item -ItemType Directory -Path $ConsolePackage -Force | Out-Null
        Write-Info "Creating Console package..."
        
        Copy-Item "$ConsolePublish\MDiceV2.Console.exe" $ConsolePackage -Force
        Write-Success "MDiceV2.Console.exe"
        
        Copy-Item "$ConsolePublish\MDiceV2.Core.dll" $ConsolePackage -Force
        Write-Success "MDiceV2.Core.dll"
        
        Get-Item "$ConsolePublish\*.dll" -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -notlike "MDiceV2.*.pdb"
        } | ForEach-Object {
            Copy-Item $_.FullName $ConsolePackage -Force
            $dllSize = [math]::Round($_.Length/1MB, 2)
            Write-Info "$($_.Name) ($dllSize MB)"
        }
        
        if ($IncludeDebugSymbols) {
            Write-Info "Including debug symbols..."
            Get-Item "$ConsolePublish\*.pdb" -ErrorAction SilentlyContinue | ForEach-Object {
                Copy-Item $_.FullName $ConsolePackage
                Write-Info "$($_.Name)"
            }
        }
        
        $ConsoleSize = (Get-ChildItem $ConsolePackage -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
        Write-Success "Console package created: $([math]::Round($ConsoleSize, 2)) MB"
        Write-Info "Location: $ConsolePackage"
        Write-Host ""
    }
    
    Write-Section "Generating SHA256 Checksums"
    
    Write-Host "Launcher package:" -ForegroundColor Green
    Get-Item "$LauncherPackage\MDiceV2.Launcher.exe" | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        Write-Host "  MDiceV2.Launcher.exe"
        Write-Host "  $hash"
    }
    
    Get-Item "$LauncherPackage\MDiceV2.Core.dll" | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        Write-Host "  MDiceV2.Core.dll"
        Write-Host "  $hash"
    }
    
    if ($IncludeConsole) {
        Write-Host ""
        Write-Host "Console package:" -ForegroundColor Green
        Get-Item "$ConsolePackage\MDiceV2.Console.exe" | ForEach-Object {
            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            Write-Host "  MDiceV2.Console.exe"
            Write-Host "  $hash"
        }
        
        Get-Item "$ConsolePackage\MDiceV2.Core.dll" | ForEach-Object {
            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            Write-Host "  MDiceV2.Core.dll"
            Write-Host "  $hash"
        }
    }
    
    Write-Host ""
    Write-Host "===================================================================" -ForegroundColor Green
    Write-Host "  SUCCESS: Release packages created!" -ForegroundColor Green
    Write-Host "===================================================================" -ForegroundColor Green
    Write-Host ""
    
    Write-Info "Next steps:"
    Write-Info "  1. Test the EXE files for correct functionality"
    Write-Info "  2. Verify Core.dll can be replaced independently"
    Write-Info "  3. Create ZIP distribution files"
    Write-Info "  4. Publish SHA256 checksums with release notes"
    Write-Host ""

} catch {
    Write-Host ""
    Write-Error "ERROR: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
