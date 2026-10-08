# MDiceV2 ZIP 包发布脚本 - 仅上传 Zip 格式，跳过 .Dice 源文件
# 使用方式: .\tools\publish-zip-only.ps1 [选项]
# 选项: -SkipRelease (仅本地发布，不上传到GitHub)
#
# 与 publish.ps1 的区别：
#   - 上传: MDiceV2.Core.Zip + CustomizedReplyPackV*.zip + AIModPackV*.zip
#   - 跳过: MDiceV2.Core.Dice (避免网络上传过大文件导致失败)
#   - 原因: 网络不好时，上传 Core.Dice 容易超时失败

[CmdletBinding()]
param(
    [string]$Owner = "HumulusQ",
    [string]$Repo = "MDiceV2Public",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "MDiceV2_Published",
    [switch]$SkipRelease
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "AIModPackageHelpers.ps1")

function Get-GitHubToken {
    $token = $env:GITHUB_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        $token = $env:GH_TOKEN
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
        $tokenFile = Join-Path (Join-Path $PSScriptRoot "..") "token.txt"
        if (Test-Path $tokenFile) {
            $token = Get-Content -Raw -Path $tokenFile | Select-Object -First 1
            $token = $token.Trim()
        }
    }

    if ([string]::IsNullOrWhiteSpace($token) -and -not $SkipRelease.IsPresent) {
        throw "Set GITHUB_TOKEN/GH_TOKEN or create token.txt with your GitHub Personal Access Token"
    }

    return $token
}

function Get-ApiHeaders {
    param([string]$Token)

    $headers = @{
        "User-Agent" = "MDiceV2-Publish-Zip-Only"
        "Accept" = "application/vnd.github+json"
    }

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $headers["Authorization"] = "Bearer $Token"
    }

    return $headers
}

function Get-NextPackageName {
    param(
        [string]$Owner,
        [string]$Repo,
        [string]$Token
    )

    $headers = Get-ApiHeaders -Token $Token
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases" -Headers $headers -Method Get

    $numbers = @()
    foreach ($release in $releases) {
        if ($release.name -match "^UpdatePackageV(\d+)$") {
            $numbers += [int]$Matches[1]
        }
    }

    $max = ($numbers | Measure-Object -Maximum).Maximum
    if (-not $max) { $max = 0 }
    $next = $max + 1

    return "UpdatePackageV$next"
}

function Get-OrCreateRelease {
    param(
        [string]$Owner,
        [string]$Repo,
        [string]$Token,
        [string]$Tag,
        [string]$Name,
        [string]$Body
    )

    $headers = Get-ApiHeaders -Token $Token
    $payload = @{ tag_name = $Tag; name = $Name; body = $Body; draft = $false; prerelease = $false } | ConvertTo-Json

    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases" -Headers $headers -Method Post -Body $payload
        return $release
    }
    catch {
        if ($_.Exception.Response.StatusCode.Value__ -eq 422) {
            # Release already exists, update it
            $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Tag" -Headers $headers -Method Get
            $updatePayload = @{ name = $Name; body = $Body; draft = $false; prerelease = $false } | ConvertTo-Json
            $null = Invoke-RestMethod -Uri $existing.url -Headers $headers -Method Patch -Body $updatePayload
            return $existing
        }

        throw
    }
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$token = Get-GitHubToken
$outputPath = Join-Path $root $Output

Push-Location $root
try {
    # Clean output directory
    if (Test-Path $outputPath) {
        Write-Host "Cleaning previous publish output..."
        Remove-Item -Path $outputPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Build Interfaces and Core
    Write-Host "Building MDiceV2.Interfaces..."
    dotnet build "MDiceV2.Interfaces/MDiceV2.Interfaces.csproj" -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Building Interfaces failed"
    }

    Write-Host "Building MDiceV2.Core..."
    dotnet build "MDiceV2.Core/MDiceV2.Core.csproj" -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Building Core failed"
    }

    # Build CustomizedReply Mod
    Write-Host "Building CustomizedReply Mod..."
    dotnet build "Mods/CustomizedReply/CustomizedReply.csproj" -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Building CustomizedReply Mod failed"
    }

    # Build AIMod
    Write-Host "Building AIMod..."
    dotnet build "Mods/AIMod/AIMod.csproj" -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Building AIMod failed"
    }

    # Step 1: Publish Launcher to output root
    Write-Host "Publishing Launcher to root..."
    dotnet publish "MDiceV2.Launcher/MDiceV2.Launcher.csproj" -c $Configuration -r $Runtime -o "$outputPath" --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing Launcher failed"
    }

    # Step 2: Publish Console to output root
    Write-Host "Publishing Console to root..."
    dotnet publish "MDiceV2.Console/MDiceV2.Console.csproj" -c $Configuration -r $Runtime -o "$outputPath"
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing Console failed"
    }

    # Step 3: Publish Core to Core subdirectory
    $coreOutputPath = Join-Path $outputPath "Core"
    
    # 杀死任何正在运行的 Core.Dice 进程
    Write-Host "Checking for running Core processes..."
    $coreProcesses = Get-Process -Name "MDiceV2.Core.Dice" -ErrorAction SilentlyContinue
    if ($coreProcesses) {
        Write-Host "Killing running Core.Dice processes..."
        $coreProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
    
    Write-Host "Publishing Core to Core subdirectory..."
    dotnet publish "MDiceV2.Core/MDiceV2.Core.csproj" -c $Configuration -r $Runtime -o "$coreOutputPath" --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing Core failed"
    }

    # Step 3.5: Copy mods to the output directory
    Write-Host "Copying mods to output directory..."
    $modsSourceDir = Join-Path $root "Mods"
    $modsOutputDir = Join-Path $outputPath "mods"
    
    if (Test-Path $modsSourceDir) {
        $null = New-Item -ItemType Directory -Path $modsOutputDir -Force
        
        Get-ChildItem -Path $modsSourceDir -Directory | Where-Object { $_.Name -notmatch '^\.' } | ForEach-Object {
            $modName = $_.Name
            $modBuildDir = Join-Path $_.FullName "bin\$Configuration\net10.0-windows"
            
            if (Test-Path $modBuildDir) {
                $modTargetDir = Join-Path $modsOutputDir $modName
                $null = New-Item -ItemType Directory -Path $modTargetDir -Force

                if ($modName -eq "AIMod") {
                    Copy-AIModSlimPackageContent -ModSourceDir $_.FullName -ModBuildDir $modBuildDir -DestinationDir $modTargetDir
                    Write-Host "  Copied AIMod slim package"
                } else {
                    Copy-Item -Path "$modBuildDir\*" -Destination $modTargetDir -Recurse -Force
                    Write-Host "  Copied mod runtime output: $modName"

                    $modJson = Join-Path $_.FullName "mod.json"
                    if (Test-Path $modJson) {
                        Copy-Item -Path $modJson -Destination $modTargetDir -Force
                    }

                    $dataJson = Join-Path $_.FullName "data.json"
                    if (Test-Path $dataJson) {
                        Copy-Item -Path $dataJson -Destination $modTargetDir -Force
                    }
                }
            }
        }
    }

    # Step 4: Organize dependencies
    Write-Host "Organizing dependencies..."
    $depsPath = Join-Path $outputPath "deps"
    
    $coreDllNames = @("MDiceV2.Launcher.dll", "MDiceV2.Console.dll", "MDiceV2.Core.dll", "MDiceV2.Abstractions.dll", "MDiceV2.Interfaces.dll")
    
    $null = New-Item -ItemType Directory -Path $depsPath -Force -ErrorAction SilentlyContinue
    
    Get-ChildItem -Path $outputPath -Filter "*.dll" -File | Where-Object { 
        $name = $_.Name
        $coreDllNames -notcontains $name
    } | ForEach-Object {
        Write-Host "  Moving to deps/: $($_.Name)"
        Move-Item -Path $_.FullName -Destination $depsPath -Force -ErrorAction SilentlyContinue
    }
    
    Write-Host "  Core directory: keeping all DLLs together for Avalonia framework"

    # Verify Core.Dice file exists
    $coreDiceFile = Get-ChildItem -Path $coreOutputPath -Filter "MDiceV2.Core.Dice" -File | Select-Object -First 1
    if ($coreDiceFile) {
        Write-Host "Core published as: $($coreDiceFile.Name)"
    } else {
        throw "Core.Dice file not found in $coreOutputPath - publish may have failed"
    }

    # Clean PDB files
    Get-ChildItem -Path $outputPath -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # Read build counter for version
    $buildCounterFile = Join-Path $root "build.counter"
    if (-not (Test-Path $buildCounterFile)) {
        throw "build.counter file not found"
    }
    $buildCounter = (Get-Content $buildCounterFile).Trim()
    Write-Host "Build counter: $buildCounter"

    # Create releases directory
    $releasesPath = Join-Path $root "releases"
    $null = New-Item -ItemType Directory -Path $releasesPath -Force -ErrorAction SilentlyContinue

    # ========== Core 文件打包为 Zip ==========
    Write-Host "Packaging Core files to Zip..."
    
    $coreDiceFile = Get-ChildItem -Path $coreOutputPath -Filter "MDiceV2.Core.Dice" -File | Select-Object -First 1
    if ($coreDiceFile) {
        Write-Host "Core published as: $($coreDiceFile.Name)"
    } else {
        throw "Core.Dice file not found in $coreOutputPath - publish may have failed"
    }
    
    $coreZipName = "MDiceV2.Core.Zip"
    $coreZipPath = Join-Path $releasesPath $coreZipName
    $coreTempPath = Join-Path ([System.IO.Path]::GetTempPath()) "MDiceV2_Core_$buildCounter"
    
    if (Test-Path $coreTempPath) {
        Remove-Item -Path $coreTempPath -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $coreTempPath -Force
    
    try {
        $coreDir = Join-Path $coreTempPath "Core"
        $null = New-Item -ItemType Directory -Path $coreDir -Force
        
        Write-Host "  Copying Core files to temp directory..."
        Copy-Item -Path "$coreOutputPath/*" -Destination $coreDir -Recurse -Force
        Write-Host "  Core files copied"
        
        if (Test-Path $coreZipPath) {
            Remove-Item -Path $coreZipPath -Force
            Write-Host "  Removed old Zip file"
        }
        
        Write-Host "  Creating zip file..."
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($coreTempPath, $coreZipPath)
        
        if (Test-Path $coreZipPath) {
            $zipSize = (Get-Item $coreZipPath).Length
            Write-Host "Core packaged to Zip: $coreZipName ($zipSize bytes)"
        } else {
            throw "Failed to create Core Zip file"
        }
    }
    finally {
        if (Test-Path $coreTempPath) {
            Remove-Item -Path $coreTempPath -Recurse -Force
        }
    }

    # Package CustomizedReply Mod
    Write-Host "Packaging CustomizedReply Mod..."
    $modBuildPath = Join-Path $root "Mods/CustomizedReply/bin/$Configuration/net10.0-windows"
    $modDllFile = Join-Path $modBuildPath "CustomizedReply.dll"
    
    if (-not (Test-Path $modDllFile)) {
        throw "CustomizedReply.dll not found at $modDllFile"
    }

    $modZipName = "CustomizedReplyPackV$buildCounter.zip"
    $modZipPath = Join-Path $releasesPath $modZipName
    
    $modTempPath = Join-Path ([System.IO.Path]::GetTempPath()) "MDiceV2_CustomizedReply_$buildCounter"
    if (Test-Path $modTempPath) {
        Remove-Item -Path $modTempPath -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $modTempPath -Force
    
    try {
        $modDir = Join-Path $modTempPath "CustomizedReply"
        $null = New-Item -ItemType Directory -Path $modDir -Force
        
        Copy-Item -Path $modDllFile -Destination $modDir -Force
        Write-Host "  Copied CustomizedReply.dll"
        
        $modJsonPath = Join-Path $root "Mods/CustomizedReply/mod.json"
        if (Test-Path $modJsonPath) {
            Copy-Item -Path $modJsonPath -Destination $modDir -Force
            Write-Host "  Copied mod.json"
        }
        
        $dataJsonPath = Join-Path $root "Mods/CustomizedReply/data.json"
        if (Test-Path $dataJsonPath) {
            Copy-Item -Path $dataJsonPath -Destination $modDir -Force
            Write-Host "  Copied data.json"
        }
        
        if (Test-Path $modZipPath) {
            Remove-Item -Path $modZipPath -Force
        }
        
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($modTempPath, $modZipPath)
        Write-Host "CustomizedReply mod packaged: $modZipName"
    }
    finally {
        if (Test-Path $modTempPath) {
            Remove-Item -Path $modTempPath -Recurse -Force
        }
    }

    # Package AIMod Mod
    Write-Host "Packaging AIMod..."
    $aiModBuildPath = Join-Path $root "Mods/AIMod/bin/$Configuration/net10.0-windows"
    $aiModDllFile = Join-Path $aiModBuildPath "AIMod.dll"

    if (-not (Test-Path $aiModDllFile)) {
        throw "AIMod.dll not found at $aiModDllFile"
    }

    $aiModZipName = "AIModPackV$buildCounter.zip"
    $aiModZipPath = Join-Path $releasesPath $aiModZipName

    $aiModTempPath = Join-Path ([System.IO.Path]::GetTempPath()) "MDiceV2_AIMod_$buildCounter"
    if (Test-Path $aiModTempPath) {
        Remove-Item -Path $aiModTempPath -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $aiModTempPath -Force

    try {
        $aiModDir = Join-Path $aiModTempPath "AIMod"
        $null = New-Item -ItemType Directory -Path $aiModDir -Force

        Copy-AIModSlimPackageContent -ModSourceDir (Join-Path $root "Mods/AIMod") -ModBuildDir $aiModBuildPath -DestinationDir $aiModDir
        Write-Host "  Copied AIMod slim package"

        if (Test-Path $aiModZipPath) {
            Remove-Item -Path $aiModZipPath -Force
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($aiModTempPath, $aiModZipPath)
        Write-Host "AIMod packaged: $aiModZipName"
    }
    finally {
        if (Test-Path $aiModTempPath) {
            Remove-Item -Path $aiModTempPath -Recurse -Force
        }
    }

    # Verify Core .Dice file exists
    $coreDiceFile = Get-ChildItem -Path $coreOutputPath -Filter "*.Dice" -File | Select-Object -First 1
    if (-not $coreDiceFile) {
        throw "Core .Dice file not found at $coreOutputPath"
    }

    # Get version from Core executable or DLL
    $coreExePath = Join-Path $coreOutputPath "MDiceV2.Core.Dice"
    $coreDllPath = Join-Path $coreOutputPath "MDiceV2.Core.dll"
    $assemblyVersion = ""
    
    if (Test-Path $coreExePath) {
        $assemblyVersion = (Get-Item $coreExePath).VersionInfo.ProductVersion
        if (-not $assemblyVersion) {
            $assemblyVersion = (Get-Item $coreExePath).VersionInfo.FileVersion
        }
        if ($assemblyVersion) {
            Write-Host "Version found from Core.Dice: $assemblyVersion"
        }
    }
    
    if ([string]::IsNullOrWhiteSpace($assemblyVersion) -and (Test-Path $coreDllPath)) {
        $assemblyVersion = (Get-Item $coreDllPath).VersionInfo.ProductVersion
        if (-not $assemblyVersion) {
            $assemblyVersion = (Get-Item $coreDllPath).VersionInfo.FileVersion
        }
        if ($assemblyVersion) {
            Write-Host "Version found from Core.dll: $assemblyVersion"
        }
    }

    if ([string]::IsNullOrWhiteSpace($assemblyVersion)) {
        Write-Host "Warning: Could not read assembly version, using default"
        $assemblyVersion = "0.0.0.0"
    }

    # ========== GitHub Release (仅上传 Zip，跳过 .Dice) ==========
    if (-not $SkipRelease.IsPresent) {
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "No GitHub token available"
        }

        Write-Host ""
        Write-Host "==================================================" -ForegroundColor Cyan
        Write-Host "  ZIP Only Mode - Skipping Core.Dice upload     " -ForegroundColor Yellow
        Write-Host "==================================================" -ForegroundColor Cyan
        Write-Host ""

        Write-Host "Creating GitHub release..."
        $releaseName = Get-NextPackageName -Owner $Owner -Repo $Repo -Token $token
        $releaseTag = $assemblyVersion
        Write-Host "Release: $releaseName (Tag: $releaseTag)"

        $releaseBody = "AssemblyVersion=$assemblyVersion`r`n`r`nZIP Only Mode - Core.Dice upload skipped to reduce network load.`r`n`r`nIncluded:`r`n  - MDiceV2.Core.Zip (Core application bundle - RECOMMENDED)`r`n  - CustomizedReplyPackV$buildCounter.zip (CustomizedReply Mod)`r`n  - AIModPackV$buildCounter.zip (AIMod slim plugin package)`r`nCore runtime dependencies stay in the host package; AIMod relies on host-shared libraries."
        $release = Get-OrCreateRelease -Owner $Owner -Repo $Repo -Token $token -Tag $releaseTag -Name $releaseName -Body $releaseBody

        # ========== Upload Core.Zip asset ==========
        Write-Host "Uploading MDiceV2.Core.Zip to release..."
        $headers = Get-ApiHeaders -Token $token
        
        # 删除旧的 Core.Zip 资源
        foreach ($asset in ($release.assets | Where-Object { $_.name -like "*Core.Zip*" })) {
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
            Write-Host "  Removed old: $($asset.name)"
        }
        
        # 上传新的 Core.Zip
        $coreZipPath = Join-Path $releasesPath "MDiceV2.Core.Zip"
        if (Test-Path $coreZipPath) {
            $zipSizeMB = [Math]::Round((Get-Item $coreZipPath).Length / 1MB, 2)
            Write-Host "  File size: $zipSizeMB MB" -ForegroundColor Gray
            
            $uploadUrl = $release.upload_url -replace "\{\?name,label\}$", "?name=MDiceV2.Core.Zip"
            $uploadHeaders = $headers.Clone()
            $uploadHeaders["Content-Type"] = "application/zip"
            
            Write-Host "  Uploading Core.Zip..."
            $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $coreZipPath -UseBasicParsing
            Write-Host "  MDiceV2.Core.Zip uploaded" -ForegroundColor Green
        } else {
            Write-Host "  Warning: MDiceV2.Core.Zip not found" -ForegroundColor Red
        }

        # ========== Upload CustomizedReply Mod asset ==========
        Write-Host "Uploading CustomizedReply Mod to release..."
        
        # 删除旧的 CustomizedReply 资源
        foreach ($asset in ($release.assets | Where-Object { $_.name -like "*CustomizedReplyPack*" })) {
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
            Write-Host "  Removed old: $($asset.name)"
        }
        
        # 上传新的 CustomizedReply mod zip
        $modZipPath = Join-Path $releasesPath $modZipName
        if (Test-Path $modZipPath) {
            $modZipSizeMB = [Math]::Round((Get-Item $modZipPath).Length / 1MB, 2)
            Write-Host "  File size: $modZipSizeMB MB" -ForegroundColor Gray
            
            $uploadUrl = $release.upload_url -replace "\{\?name,label\}$", "?name=$modZipName"
            $uploadHeaders = $headers.Clone()
            $uploadHeaders["Content-Type"] = "application/zip"
            
            $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $modZipPath -UseBasicParsing
            Write-Host "  CustomizedReply Mod uploaded" -ForegroundColor Green
        }

        # ========== Upload AIMod asset ==========
        Write-Host "Uploading AIMod to release..."

        foreach ($asset in ($release.assets | Where-Object { $_.name -like "*AIModPack*" })) {
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
            Write-Host "  Removed old: $($asset.name)"
        }

        $aiModZipPath = Join-Path $releasesPath $aiModZipName
        if (Test-Path $aiModZipPath) {
            $aiModZipSizeMB = [Math]::Round((Get-Item $aiModZipPath).Length / 1MB, 2)
            Write-Host "  File size: $aiModZipSizeMB MB" -ForegroundColor Gray

            $uploadUrl = $release.upload_url -replace "\{\?name,label\}$", "?name=$aiModZipName"
            $uploadHeaders = $headers.Clone()
            $uploadHeaders["Content-Type"] = "application/zip"

            $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $aiModZipPath -UseBasicParsing
            Write-Host "  AIMod uploaded" -ForegroundColor Green
        }
        
        Write-Host ""
        Write-Host "==================================================" -ForegroundColor Green
        Write-Host "  ZIP Only Release Complete!" -ForegroundColor Green
        Write-Host "==================================================" -ForegroundColor Green
        Write-Host ""
        Write-Host "  Skipped: MDiceV2.Core.Dice (reduces upload size)" -ForegroundColor Yellow
        Write-Host "  Release URL: $($release.html_url)" -ForegroundColor Cyan
        Write-Host ""

    } else {
        Write-Host "SkipRelease enabled - not uploading to GitHub"
    }

    Write-Host ""
    Write-Host "Publish complete!" -ForegroundColor Green
}
finally {
    Pop-Location
}
