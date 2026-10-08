 MDiceV2 Hybrid Debug Build Script
# 使用 MSBuild 处理 C++ 项目，dotnet CLI 处理 .NET 项目

[CmdletBinding()]
param(
    [switch]$NoClean,
    [switch]$NoBuild,
    [switch]$NoRun,
    [string]$Output = "MDiceV2_Debug",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$rootPath = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputPath = Join-Path $rootPath $Output
$msbuildPath = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe'

Write-Host ""
Write-Host "========== MDiceV2 Hybrid Debug Build ==========" -ForegroundColor Cyan
Write-Host "Root directory: $rootPath"
Write-Host "Output directory: $outputPath"
Write-Host "MSBuild path: $msbuildPath"
Write-Host ""

try {
    # 检查 MSBuild
    if (-not (Test-Path $msbuildPath)) {
        Write-Host "Error: MSBuild.exe not found at $msbuildPath" -ForegroundColor Red
        Write-Host "Please install Visual Studio Build Tools" -ForegroundColor Red
        exit 1
    }
    Write-Host "[+] MSBuild found" -ForegroundColor Green
    Write-Host ""

    # 清理输出目录
    if (-not $NoClean) {
        Write-Host "Cleaning output directory..." -ForegroundColor Cyan
        
        # 保存 Core\data 目录
        $dataBackup = $null
        $coreDataPath = Join-Path (Join-Path $outputPath "Core") "data"
        if (Test-Path $coreDataPath) {
            $dataBackup = Join-Path $env:TEMP "mdice_data_backup_$([guid]::NewGuid())"
            Write-Host "  Backing up Core\data directory..." -ForegroundColor Cyan
            Copy-Item -Path $coreDataPath -Destination $dataBackup -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "  [+] Data backed up" -ForegroundColor Green
        }
        
        if (Test-Path $outputPath) {
            Remove-Item -Path $outputPath -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "[+] Deleted $outputPath"
        }
        
        # 恢复 Core\data
        if ($dataBackup -and (Test-Path $dataBackup)) {
            $coreDir = Join-Path $outputPath "Core"
            New-Item -Path $coreDir -ItemType Directory -Force | Out-Null
            $dataDest = Join-Path $coreDir "data"
            Copy-Item -Path $dataBackup -Destination $dataDest -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "  [+] Core\data restored" -ForegroundColor Green
            Remove-Item -Path $dataBackup -Recurse -Force -ErrorAction SilentlyContinue
        }
        
        # 清理编译输出
        $cleanPaths = @(
            (Join-Path $rootPath "MDiceV2.Abstractions\bin"),
            (Join-Path $rootPath "MDiceV2.Abstractions\obj"),
            (Join-Path $rootPath "MDiceV2.Interfaces\bin"),
            (Join-Path $rootPath "MDiceV2.Interfaces\obj"),
            (Join-Path $rootPath "MDiceV2.Core\bin"),
            (Join-Path $rootPath "MDiceV2.Core\obj"),
            (Join-Path $rootPath "MDiceV2.Launcher\bin"),
            (Join-Path $rootPath "MDiceV2.Launcher\obj"),
            (Join-Path $rootPath "MDiceV2.Console\bin"),
            (Join-Path $rootPath "MDiceV2.Console\obj"),
            (Join-Path $rootPath "Mods\CustomizedReply\bin"),
            (Join-Path $rootPath "Mods\CustomizedReply\obj"),
            (Join-Path $rootPath "Mods\AIMod\bin"),
            (Join-Path $rootPath "Mods\AIMod\obj"),
            (Join-Path $rootPath "Mods\ABot\bin"),
            (Join-Path $rootPath "Mods\ABot\obj"),
            (Join-Path $rootPath "Mods\ABotTestPanel\bin"),
            (Join-Path $rootPath "Mods\ABotTestPanel\obj")
        )
        
        foreach ($path in $cleanPaths) {
            if (Test-Path $path) {
                Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
                Write-Host "[+] Cleaned $path"
            }
        }
        Write-Host ""
    }

    if (-not $NoBuild) {
        # ============ Phase 1: Build C++ Projects with MSBuild ============
        Write-Host "Building C++ projects with MSBuild..." -ForegroundColor Cyan
        
        $cppProjects = @(
            @{
                Name = "ABot.Core"
                Path = Join-Path $rootPath "ABot\ABot.Core\ABot.Core.vcxproj"
            },
            @{
                Name = "ABot.CLI"
                Path = Join-Path $rootPath "ABot\ABot.CLI\ABot.CLI.vcxproj"
            }
        )
        
        foreach ($proj in $cppProjects) {
            if (Test-Path $proj.Path) {
                Write-Host "  Building $($proj.Name)..." -ForegroundColor Cyan
                $output = & $msbuildPath $proj.Path /p:Configuration=Debug /p:Platform=x64 /verbosity:minimal 2>&1
                
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "    [!] Build failed for $($proj.Name)" -ForegroundColor Yellow
                    Write-Host $output | Select-String "error|Error"
                } else {
                    Write-Host "    [+] $($proj.Name) built successfully" -ForegroundColor Green
                }
            }
        }
        Write-Host ""
        
        # ============ Phase 2: Restore NuGet packages ============
        Write-Host "Restoring NuGet packages..." -ForegroundColor Cyan
        $restoreResult = dotnet restore (Join-Path $rootPath "MDiceV2.sln") 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Restore failed:" -ForegroundColor Red
            Write-Host $restoreResult | Select-String "error|Error"
            exit 1
        }
        Write-Host "[+] NuGet restore complete" -ForegroundColor Green
        Write-Host ""

        # ============ Phase 3: Build .NET Projects with dotnet CLI ============
        Write-Host "Building .NET projects..." -ForegroundColor Cyan
        
        $csprojFiles = @(
            (Join-Path $rootPath "MDiceV2.Abstractions\MDiceV2.Abstractions.csproj"),
            (Join-Path $rootPath "MDiceV2.Interfaces\MDiceV2.Interfaces.csproj"),
            (Join-Path $rootPath "MDiceV2.Core\MDiceV2.Core.csproj"),
            (Join-Path $rootPath "MDiceV2.Launcher\MDiceV2.Launcher.csproj"),
            (Join-Path $rootPath "MDiceV2.Console\MDiceV2.Console.csproj"),
            (Join-Path $rootPath "Mods\CustomizedReply\CustomizedReply.csproj"),
            (Join-Path $rootPath "Mods\AIMod\AIMod.csproj"),
            (Join-Path $rootPath "Mods\ABot\ABot.csproj"),
            (Join-Path $rootPath "MDiceV2.Tests\MDiceV2.Tests.csproj")
        )
        
        foreach ($csproj in $csprojFiles) {
            if (Test-Path $csproj) {
                $projName = Split-Path (Split-Path $csproj -Parent) -Leaf
                Write-Host "  Building $projName..."
                $output = dotnet build $csproj -c Debug 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "    [!] Build failed for $projName" -ForegroundColor Yellow
                    Write-Host $output | Select-String "error|Error" | Select-Object -First 3
                } else {
                    Write-Host "    [+] $projName built successfully" -ForegroundColor Green
                }
            }
        }
        Write-Host ""
        
        # Note: Skip ABotTestPanel due to XAML configuration issues
        Write-Host "[!] ABotTestPanel skipped (XAML configuration issues)" -ForegroundColor Yellow
        Write-Host ""
        
        Write-Host "[+] All buildable projects completed" -ForegroundColor Green
        Write-Host ""
    }

    # ============ Phase 4: Publish to Debug Directory ============
    Write-Host "Publishing to debug output directory..." -ForegroundColor Cyan
    
    New-Item -Path $outputPath -ItemType Directory -Force | Out-Null
    
    # 1. Publish Launcher
    Write-Host "Publishing Launcher..."
    $launcherProj = Join-Path $rootPath "MDiceV2.Launcher\MDiceV2.Launcher.csproj"
    $launcherTemp = Join-Path $outputPath "launcher_temp"
    dotnet publish $launcherProj -c Debug -o $launcherTemp --no-build --self-contained=false --runtime $Runtime 2>&1 | Out-Null
    
    Get-Item (Join-Path $launcherTemp "MDiceV2.Launcher.exe") | Copy-Item -Destination $outputPath -Force
    Get-Item (Join-Path $launcherTemp "MDiceV2.Launcher.pdb") | Copy-Item -Destination $outputPath -Force -ErrorAction SilentlyContinue
    Get-Item (Join-Path $launcherTemp "*.dll") | Copy-Item -Destination $outputPath -Force
    Remove-Item -Path $launcherTemp -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[+] Launcher published"
    
    # 2. Publish Core
    Write-Host "Publishing Core..."
    $coreProj = Join-Path $rootPath "MDiceV2.Core\MDiceV2.Core.csproj"
    $coreOutput = Join-Path $outputPath "Core"
    
    if (Test-Path $coreOutput) {
        Write-Host "  Cleaning old Core directory..."
        Remove-Item -Path $coreOutput -Recurse -Force -ErrorAction Stop
        Start-Sleep -Milliseconds 100
    }
    
    New-Item -Path $coreOutput -ItemType Directory -Force | Out-Null
    $publishResult = dotnet publish $coreProj -c Debug -o $coreOutput --no-build --self-contained=false --runtime $Runtime 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Core publish output:"
        Write-Host $publishResult
        throw "Core publish failed with exit code $LASTEXITCODE"
    }
    
    $coreDice = Join-Path $coreOutput "MDiceV2.Core.Dice"
    if (-not (Test-Path $coreDice)) {
        throw "Core.Dice not found after publish at $coreDice"
    }
    Write-Host "[+] Core published as Core.Dice"
    
    # 3. Publish Console
    Write-Host "Publishing Console..."
    $consoleProj = Join-Path $rootPath "MDiceV2.Console\MDiceV2.Console.csproj"
    $consoleTemp = Join-Path $outputPath "console_temp"
    
    dotnet publish $consoleProj -c Debug -o $consoleTemp --no-build --self-contained=false --runtime $Runtime 2>&1 | Out-Null
    
    if (Test-Path $consoleTemp) {
        $consoleExe = $null
        if (Test-Path (Join-Path $consoleTemp "MDiceV2.Console.exe")) {
            $consoleExe = Join-Path $consoleTemp "MDiceV2.Console.exe"
        }
        
        if ($consoleExe -and (Test-Path $consoleExe)) {
            Copy-Item -Path $consoleExe -Destination $outputPath -Force
            
            $consoleDir = Split-Path $consoleExe -Parent
            foreach ($ext in @(".dll", ".runtimeconfig.json", ".deps.json")) {
                $file = Join-Path $consoleDir "MDiceV2.Console$ext"
                if (Test-Path $file) {
                    Copy-Item -Path $file -Destination $outputPath -Force -ErrorAction SilentlyContinue
                }
            }
            Write-Host "[+] Console.exe published"
            
            $consolePdb = [System.IO.Path]::ChangeExtension($consoleExe, ".pdb")
            if (Test-Path $consolePdb) {
                Copy-Item -Path $consolePdb -Destination $outputPath -Force -ErrorAction SilentlyContinue
            }
        }
        
        Remove-Item -Path $consoleTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    # 4. Copy Mods
    Write-Host "Copying mods..."
    $modsTarget = Join-Path $outputPath "mods"
    New-Item -Path $modsTarget -ItemType Directory -Force | Out-Null

    # CustomizedReply
    $modSourceCustomized = Join-Path $rootPath "Mods\CustomizedReply\bin\Debug"
    $modTargetCustomized = Join-Path $modsTarget "CustomizedReply"
    if (Test-Path $modSourceCustomized) {
        $actualModSource = Get-ChildItem -Path $modSourceCustomized -Directory | Select-Object -First 1
        if ($actualModSource) {
            Copy-Item -Path $actualModSource.FullName -Destination $modTargetCustomized -Recurse -Force
            Write-Host "[+] CustomizedReply Mod copied"
        }
    }

    # AIMod
    $modSourceAI = Join-Path $rootPath "Mods\AIMod\bin\Debug"
    $modTargetAI = Join-Path $modsTarget "AIMod"
    if (Test-Path $modSourceAI) {
        $actualModSourceAI = Get-ChildItem -Path $modSourceAI -Directory | Select-Object -First 1
        if ($actualModSourceAI) {
            Copy-Item -Path $actualModSourceAI.FullName -Destination $modTargetAI -Recurse -Force
            Write-Host "[+] AIMod copied"
        }
    }

    # ABot
    $modSourceABot = Join-Path $rootPath "Mods\ABot\bin\Debug"
    $modTargetABot = Join-Path $modsTarget "ABot"
    if (Test-Path $modSourceABot) {
        $actualModSourceABot = Get-ChildItem -Path $modSourceABot -Directory | Select-Object -First 1
        if ($actualModSourceABot) {
            Copy-Item -Path $actualModSourceABot.FullName -Destination $modTargetABot -Recurse -Force
            Write-Host "[+] ABot Mod copied"
        }
    }

    # Copy mod.json files
    Copy-Item -Path (Join-Path $rootPath "Mods\AIMod\mod.json") -Destination $modTargetAI -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $rootPath "Mods\CustomizedReply\mod.json") -Destination $modTargetCustomized -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $rootPath "Mods\ABot\mod.json") -Destination $modTargetABot -Force -ErrorAction SilentlyContinue
    
    # 5. Copy data folder
    Write-Host "Copying data folder..."
    $dataSource = Join-Path $rootPath "data"
    $dataTarget = Join-Path $outputPath "data"
    if (Test-Path $dataSource) {
        Copy-Item -Path $dataSource -Destination $dataTarget -Recurse -Force
        Write-Host "[+] Data folder copied"
    }
    
    Write-Host ""
    Write-Host "========== Build Complete ==========" -ForegroundColor Green
    Write-Host ""
    
    # Run Launcher
    if (-not $NoRun) {
        Write-Host "Starting Launcher..." -ForegroundColor Cyan
        $launcherExe = Join-Path $outputPath "MDiceV2.Launcher.exe"
        Write-Host "Executing: $launcherExe"
        Write-Host ""
        
        & $launcherExe
    }
}
catch {
    Write-Host ""
    Write-Host "========== Build Failed ==========" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
