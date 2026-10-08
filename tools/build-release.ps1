[CmdletBinding()]
param(
    [switch]$SkipVersionSync,
    [switch]$SkipClean,
    [switch]$SkipPackaging,
    [string]$Output = "MDiceV2_Release"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "AIModPackageHelpers.ps1")

$rootPath = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host ""
Write-Host "========== MDiceV2 Release Build ==========" -ForegroundColor Cyan
Write-Host "Working directory: $rootPath"
Write-Host "Output directory: $Output"
Write-Host ""

try {
    # Check dotnet
    Write-Host "Checking dotnet installation..." -ForegroundColor Cyan
    $version = dotnet --version
    Write-Host "[+] dotnet $version" -ForegroundColor Green
    Write-Host ""
    
    # ============ Bump Build Counter ============
    Write-Host "Updating build counter..." -ForegroundColor Cyan
    
    $versionPropsPath = Join-Path $rootPath "Version.props"
    $counterFile = Join-Path $rootPath "build.counter"
    $baseFile = Join-Path $rootPath "build.counter.base"
    $sessionFile = Join-Path $rootPath "build.counter.session"
    
    [xml]$versionXml = Get-Content -Path $versionPropsPath -Raw
    $currentVersion = ($versionXml.Project.PropertyGroup | Select-Object -First 1).AssemblyVersion
    if ([string]::IsNullOrWhiteSpace($currentVersion)) {
        Write-Host "[!] Warning: Cannot read AssemblyVersion from Version.props, skipping counter update" -ForegroundColor Yellow
    } else {
        $parts = ($currentVersion.Trim() -split '\.')
        while ($parts.Count -lt 3) { $parts += 0 }
        $baseVersion = '{0}.{1}.{2}' -f $parts[0], $parts[1], $parts[2]
        
        $storedBase = ''
        $counter = 0
        $shouldReset = $true
        
        if (Test-Path $baseFile) {
            $storedBase = (Get-Content -Path $baseFile -Raw).Trim()
            if ($storedBase -eq $baseVersion -and (Test-Path $counterFile)) {
                [int]::TryParse((Get-Content -Path $counterFile -Raw).Trim(), [ref]$counter) | Out-Null
                $shouldReset = $false
            }
        } elseif (Test-Path $counterFile) {
            [int]::TryParse((Get-Content -Path $counterFile -Raw).Trim(), [ref]$counter) | Out-Null
            $shouldReset = $false
        }
        
        if ($shouldReset) {
            $counter = 0
            Write-Host "  Build counter reset to 0 (version: $storedBase -> $baseVersion)" -ForegroundColor Yellow
        }
        
        $counter++
        Set-Content -Path $counterFile -Value $counter.ToString() -Encoding UTF8 -NoNewline
        Set-Content -Path $baseFile -Value $baseVersion -Encoding UTF8 -NoNewline
        Set-Content -Path $sessionFile -Value "$baseVersion|$([DateTime]::UtcNow.ToFileTimeUtc())" -Encoding UTF8 -NoNewline
        
        Write-Host "[+] Build counter: ${baseVersion}.${counter}" -ForegroundColor Green
    }
    Write-Host ""

    # Sync versions if not skipped
    if (-not $SkipVersionSync) {
        Write-Host "Syncing versions..." -ForegroundColor Cyan
        & powershell -ExecutionPolicy Bypass -NoProfile -File (Join-Path $PSScriptRoot "sync-versions.ps1")
        if ($LASTEXITCODE -ne 0) {
            throw "Version sync failed"
        }
        Write-Host ""
    }
    
    # Clean if not skipped
    if (-not $SkipClean) {
        Write-Host "Cleaning build artifacts..." -ForegroundColor Cyan
        $cleanPaths = @(
            (Join-Path $rootPath "bin"),
            (Join-Path $rootPath "obj"),
            (Join-Path $rootPath "MDiceV2.Core\bin"),
            (Join-Path $rootPath "MDiceV2.Core\obj"),
            (Join-Path $rootPath "MDiceV2.Launcher\bin"),
            (Join-Path $rootPath "MDiceV2.Launcher\obj"),
            (Join-Path $rootPath "Mods\CustomizedReply\bin"),
            (Join-Path $rootPath "Mods\CustomizedReply\obj"),
            (Join-Path $rootPath "Mods\AIMod\bin"),
            (Join-Path $rootPath "Mods\AIMod\obj")
        )
        
        foreach ($path in $cleanPaths) {
            if (Test-Path $path) {
                Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
                Write-Host "[+] Deleted $path"
            }
        }
        Write-Host ""
    }
    
    # Restore and Build
    Write-Host "Building projects..." -ForegroundColor Cyan
    
    Write-Host "Restoring NuGet packages..."
    dotnet restore (Join-Path $rootPath "MDiceV2.sln") | Out-Null
    Write-Host "[+] NuGet restore complete"
    
    $projects = @(
        "MDiceV2.Abstractions\MDiceV2.Abstractions.csproj",
        "MDiceV2.Interfaces\MDiceV2.Interfaces.csproj",
        "MDiceV2.Core\MDiceV2.Core.csproj",
        "Mods\AIMod\AIMod.csproj",
        "Mods\CustomizedReply\CustomizedReply.csproj",
        "MDiceV2.Launcher\MDiceV2.Launcher.csproj",
        "MDiceV2.Console\MDiceV2.Console.csproj"
    )
    
    foreach ($project in $projects) {
        $projPath = Join-Path $rootPath $project
        $projName = Split-Path -Leaf $project
        
        Write-Host "Building $projName..."
        dotnet build $projPath -c Release --no-restore | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed for $projName"
        }
        Write-Host "[+] $projName compiled"
    }
    
    Write-Host ""
    
    # Package if not skipped
    if (-not $SkipPackaging) {
        Write-Host "Packaging..." -ForegroundColor Cyan
        
        if (-not (Test-Path $Output)) {
            New-Item -ItemType Directory -Path $Output -Force | Out-Null
        }
        
        $launcherProj = Join-Path $rootPath "MDiceV2.Launcher\MDiceV2.Launcher.csproj"
        $publishDir = Join-Path $Output "app"
        
        Write-Host "Publishing self-contained..."
        dotnet publish $launcherProj `
            -c Release `
            -r win-x64 `
            --self-contained `
            -o $publishDir `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true | Out-Null
        
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed for Launcher"
        }
        Write-Host "[+] Published Launcher to $publishDir"
        
        # Also publish Console version to the same directory
        Write-Host "Publishing Console version..."
        $consoleProj = Join-Path $rootPath "MDiceV2.Console\MDiceV2.Console.csproj"
        
        dotnet publish $consoleProj `
            -c Release `
            -r win-x64 `
            --self-contained `
            -o $publishDir `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true | Out-Null
        
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed for Console"
        }
        Write-Host "[+] Published Console to $publishDir"
        
        # Package mods
        Write-Host "Packaging mods..."
        $modsDir = Join-Path $rootPath "Mods"
        $outputModsDir = Join-Path $Output "mods"
        
        if (Test-Path $modsDir) {
            $modDirs = Get-ChildItem -Path $modsDir -Directory | Where-Object { $_.Name -notmatch "^\."}  | Where-Object { $_.Name -in @("AIMod", "CustomizedReply") }
            foreach ($modDir in $modDirs) {
                $modName = $modDir.Name
                $binDir = Join-Path $modDir.FullName "bin\Release"
                
                if (Test-Path $binDir) {
                    # 寻找实际的输出目录，例如 net10.0-windows
                    $actualBinDir = Get-ChildItem -Path $binDir -Directory | Select-Object -First 1
                    if ($actualBinDir) {
                        $binDir = $actualBinDir.FullName
                    }
                }
                
                if (Test-Path $binDir) {
                    $outputModDir = Join-Path $outputModsDir $modName
                    New-Item -ItemType Directory -Path $outputModDir -Force | Out-Null

                    if ($modName -eq "AIMod") {
                        Copy-AIModSlimPackageContent -ModSourceDir $modDir.FullName -ModBuildDir $binDir -DestinationDir $outputModDir
                    }
                    else {
                        Copy-Item -Path "$binDir\*" -Destination $outputModDir -Recurse -Force

                        # 复制 mod 配置文件
                        if (Test-Path (Join-Path $modDir "mod.json")) {
                            Copy-Item -Path (Join-Path $modDir "mod.json") -Destination $outputModDir -Force -ErrorAction SilentlyContinue
                        }

                        if ($modName -eq "CustomizedReply" -and (Test-Path (Join-Path $modDir "data.json"))) {
                            Copy-Item -Path (Join-Path $modDir "data.json") -Destination $outputModDir -Force -ErrorAction SilentlyContinue
                        }
                    }
                    
                    Write-Host "[+] Packaged mod: $modName"
                }
            }
        }
        
        Write-Host ""
    }
    
    Write-Host "========== Build Complete ==========" -ForegroundColor Green
    Write-Host "[+] Release build successful!"
    if (Test-Path $Output) {
        Write-Host "Output: $(Resolve-Path $Output)"
    } else {
        Write-Host "Output: $Output"
    }
    Write-Host ""
    
    exit 0
}
catch {
    Write-Host ""
    Write-Host "========== Build Failed ==========" -ForegroundColor Red
    Write-Host "[-] $_"
    Write-Host ""
    exit 1
}
