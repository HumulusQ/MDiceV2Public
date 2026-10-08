# MDiceV2 完整发布脚本 - 上传完整包、Core和Mods
# 使用方式: .\tools\publish-all.ps1
# 会上传：
#   - MDiceV2-UI-vX.X.X.zip (完整有头版本)
#   - MDiceV2-Console-vX.X.X.zip (完整无头版本)
#   - MDiceV2.Core.dll
#   - 所有 Mods

[CmdletBinding()]
param(
    [string]$Owner = "HumulusQ",
    [string]$Repo = "MDiceV2Public",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$BuildDir = "MDiceV2_Release",
    [string]$PackageDir = "MDiceV2_Release_Packages",
    [switch]$SkipBuild,
    [switch]$SkipPack,
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
        $tokenFile = Join-Path $PSScriptRoot "token.txt"
        if (-not (Test-Path $tokenFile)) {
            $tokenFile = Join-Path (Join-Path $PSScriptRoot "..") "token.txt"
        }
        if (Test-Path $tokenFile) {
            $token = Get-Content -Raw -Path $tokenFile | Select-Object -First 1
            $token = $token.Trim()
        }
    }

    if ([string]::IsNullOrWhiteSpace($token) -and -not $SkipRelease.IsPresent) {
        throw "Set GITHUB_TOKEN/GH_TOKEN or provide token.txt to upload release assets."
    }

    return $token
}

function Get-ApiHeaders {
    param([string]$Token)

    $headers = @{
        "User-Agent" = "MDiceV2-Publish-All"
        Accept        = "application/vnd.github+json"
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
            $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Tag" -Headers $headers -Method Get
            $updatePayload = @{ name = $Name; body = $Body; draft = $false; prerelease = $false } | ConvertTo-Json
            $null = Invoke-RestMethod -Uri $existing.url -Headers $headers -Method Patch -Body $updatePayload
            return $existing
        }
        throw
    }
}

function Upload-Asset {
    param(
        [object]$Release,
        [string]$Token,
        [string]$AssetName,
        [string]$AssetPath,
        [string]$ContentType = "application/octet-stream"
    )

    $headers = Get-ApiHeaders -Token $Token

    # 删除同名旧资源
    foreach ($asset in ($Release.assets | Where-Object { $_.name -eq $AssetName })) {
        Write-Host "  删除旧文件: $AssetName" -ForegroundColor Gray
        Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
    }

    # 上传新文件
    $uploadUrl = $Release.upload_url -replace "\{\?name,label\}$", "?name=$AssetName"
    $uploadHeaders = $headers.Clone()
    $uploadHeaders["Content-Type"] = $ContentType

    Write-Host "  上传: $AssetName" -ForegroundColor Yellow
    $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $AssetPath -UseBasicParsing
    Write-Host "  ✅ 完成: $AssetName" -ForegroundColor Green
}

function Publish-ModPackage {
    param(
        [object]$Release,
        [string]$Token,
        [string]$ModName,
        [string]$ModPath,
        [string]$Version,
        [string]$ModSourceDir = "",
        [string]$ModBuildDir = ""
    )
    
    $versionNumber = $Version -replace '[^0-9]', ''
    $modPackageName = "$($ModName)PackV$versionNumber"
    $releaseDir = Join-Path $root "releases"
    $modZipPath = Join-Path $releaseDir "$modPackageName.zip"
    
    if (-not (Test-Path $releaseDir)) {
        New-Item -ItemType Directory -Path $releaseDir | Out-Null
    }
    
    if (Test-Path $modZipPath) {
        Remove-Item $modZipPath -Force
    }
    
    $tempModDir = Join-Path $env:TEMP "$($ModName)Mod_$([System.Guid]::NewGuid())"
    New-Item -ItemType Directory -Path $tempModDir | Out-Null
    
    try {
        if ($ModName -eq "AIMod") {
            if ([string]::IsNullOrWhiteSpace($ModSourceDir) -or [string]::IsNullOrWhiteSpace($ModBuildDir)) {
                throw "AIMod package requires ModSourceDir and ModBuildDir"
            }

            $aiModTargetDir = Join-Path $tempModDir "AIMod"
            $null = New-Item -ItemType Directory -Path $aiModTargetDir -Force
            Copy-AIModSlimPackageContent -ModSourceDir $ModSourceDir -ModBuildDir $ModBuildDir -DestinationDir $aiModTargetDir
        }
        else {
            Copy-Item -Path "$ModPath\*" -Destination $tempModDir -Recurse -Force

            $dataPath = Join-Path $root "MDiceV2.Launcher/data/mods/$ModName/data.json"
            if (Test-Path $dataPath) {
                Copy-Item -Path $dataPath -Destination (Join-Path $tempModDir "data.json") -Force
            }
        }
        
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($tempModDir, $modZipPath)
        
        Upload-Asset -Release $release -Token $Token -AssetName "$modPackageName.zip" -AssetPath $modZipPath -ContentType "application/zip"
        
    } finally {
        if (Test-Path $tempModDir) {
            Remove-Item -Path $tempModDir -Recurse -Force
        }
    }
}

function Publish-AllMods {
    param(
        [object]$Release,
        [string]$Token,
        [string]$Version
    )
    
    Write-Host ""
    Write-Host "📦 发布所有 Mods..." -ForegroundColor Cyan
    
    $modsDir = Join-Path $root "MDiceV2.Launcher/mods"
    if (-not (Test-Path $modsDir)) {
        Write-Host "  ℹ️  没有找到 Mods 目录" -ForegroundColor Gray
        return
    }
    
    $modDirs = Get-ChildItem -Path $modsDir -Directory -ErrorAction SilentlyContinue
    $publishedCount = 0
    $publishedModNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    
    foreach ($modDir in $modDirs) {
        $modName = $modDir.Name
        $modPath = $modDir.FullName
        $dllPath = Join-Path $modPath "$modName.dll"
        
        if (Test-Path $dllPath) {
            try {
                Write-Host "  Module: $modName" -ForegroundColor Gray
                Publish-ModPackage -Release $release -Token $token -ModName $modName -ModPath $modPath -Version $Version
                $publishedCount++
                $null = $publishedModNames.Add($modName)
            }
            catch {
                Write-Host "  ❌ 发布 Mod '$modName' 失败: $_" -ForegroundColor Red
            }
        }
    }

    if (-not $publishedModNames.Contains("AIMod")) {
        $aiModSourceDir = Join-Path $root "Mods/AIMod"
        $aiModBuildDir = Join-Path $aiModSourceDir "bin\$Configuration\net10.0-windows"
        try {
            $resolvedAiModBuildDir = Resolve-AIModBuildOutputDir -BuildPath $aiModBuildDir
            $aiModDllPath = Join-Path $resolvedAiModBuildDir "AIMod.dll"
            if (Test-Path $aiModDllPath -PathType Leaf) {
                Write-Host "  Module: AIMod" -ForegroundColor Gray
                Publish-ModPackage -Release $release -Token $token -ModName "AIMod" -ModPath $resolvedAiModBuildDir -Version $Version -ModSourceDir $aiModSourceDir -ModBuildDir $resolvedAiModBuildDir
                $publishedCount++
                $null = $publishedModNames.Add("AIMod")
            }
        }
        catch {
            Write-Host "  ❌ 发布 Mod 'AIMod' 失败: $_" -ForegroundColor Red
        }
    }
    
    Write-Host "  ✅ 已发布 $publishedCount 个 Mods" -ForegroundColor Green
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$token = Get-GitHubToken

Push-Location $root
try {
    Write-Host ""
    Write-Host "╔════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "║           MDiceV2 完整发布 - 所有发行物                ║" -ForegroundColor Cyan
    Write-Host "╚════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host ""

    # ========== 步骤 1: 构建 Release ==========
    if (-not $SkipBuild.IsPresent) {
        Write-Host "📦 步骤 1/3: 构建 Release..." -ForegroundColor Cyan
        Write-Host ""
        
        $buildScript = Join-Path $PSScriptRoot "build-release.ps1"
        if (-not (Test-Path $buildScript)) {
            throw "未找到构建脚本: $buildScript"
        }
        
        & $buildScript -Output $BuildDir -SkipClean
        if ($LASTEXITCODE -ne 0) {
            throw "构建失败"
        }
        
        Write-Host ""
    }
    else {
        Write-Host "⏭️  跳过构建 (使用现有的 $BuildDir)" -ForegroundColor Yellow
        Write-Host ""
    }

    # ========== 步骤 2: 打包两个版本 ==========
    if (-not $SkipPack.IsPresent) {
        Write-Host "📦 步骤 2/3: 打包两个版本..." -ForegroundColor Cyan
        Write-Host ""
        
        $packScript = Join-Path $PSScriptRoot "pack-release.ps1"
        if (-not (Test-Path $packScript)) {
            throw "未找到打包脚本: $packScript"
        }
        
        & $packScript -BuildDir $BuildDir -OutputDir $PackageDir
        if ($LASTEXITCODE -ne 0) {
            throw "打包失败"
        }
        
        Write-Host ""
    }
    else {
        Write-Host "⏭️  跳过打包 (使用现有的 $PackageDir)" -ForegroundColor Yellow
        Write-Host ""
    }

    # ========== 步骤 3: 上传到 GitHub Release ==========
    if (-not $SkipRelease.IsPresent) {
        Write-Host "📤 步骤 3/3: 上传到 GitHub Release..." -ForegroundColor Cyan
        Write-Host ""

        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "没有 GitHub Token，无法上传。请设置 GITHUB_TOKEN 环境变量或提供 token.txt"
        }

        # 获取版本号
        $coreDllPath = Join-Path $BuildDir "app\MDiceV2.Core.dll"
        if (-not (Test-Path $coreDllPath)) {
            throw "未找到 MDiceV2.Core.dll: $coreDllPath"
        }

        $assemblyVersion = (Get-Item $coreDllPath).VersionInfo.ProductVersion
        if (-not $assemblyVersion) {
            $assemblyVersion = (Get-Item $coreDllPath).VersionInfo.FileVersion
        }
        if ([string]::IsNullOrWhiteSpace($assemblyVersion)) {
            throw "无法读取程序集版本"
        }

        # 创建或更新 Release
        Write-Host "创建 Release 标签: v$assemblyVersion" -ForegroundColor Yellow
        $releaseName = Get-NextPackageName -Owner $Owner -Repo $Repo -Token $token
        $releaseTag = $assemblyVersion
        
        $releaseBody = @"
# MDiceV2 v$assemblyVersion 完整发布包

## 包含文件:
- **UI 版本** (有头): MDiceV2-UI-v$assemblyVersion.zip
- **Console 版本** (无头): MDiceV2-Console-v$assemblyVersion.zip
- **Core 库**: MDiceV2.Core.dll (与两个版本通用)
- **Mods**: 所有模组文件

## 使用方式:
### UI 版本
1. 下载并解压 MDiceV2-UI-v$assemblyVersion.zip
2. 双击运行 MDiceV2.Launcher.exe

### Console 版本
1. 下载并解压 MDiceV2-Console-v$assemblyVersion.zip
2. 在命令行中运行: MDiceV2.Console.exe

## 更新方式:
- **仅更新核心**: 替换 MDiceV2.Core.dll
- **仅更新模组**: 替换 mods 文件夹中对应模组目录
- **完整更新**: 重新下载并解压完整包

Release: $releaseTag
"@

        Write-Host ""
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Gray
        
        $release = Get-OrCreateRelease -Owner $Owner -Repo $Repo -Token $token -Tag $releaseTag -Name $releaseName -Body $releaseBody
        
        Write-Host "✅ Release 已创建/更新: $($release.html_url)" -ForegroundColor Green
        Write-Host ""

        # ========== 上传文件 ==========
        Write-Host "上传文件:" -ForegroundColor Yellow
        
        # 上传两个完整包
        Write-Host ""
        Write-Host "📦 完整包:" -ForegroundColor Cyan
        $uiZip = Join-Path $PackageDir "MDiceV2-UI-v$assemblyVersion.zip"
        $consoleZip = Join-Path $PackageDir "MDiceV2-Console-v$assemblyVersion.zip"
        
        if (Test-Path $uiZip) {
            Upload-Asset -Release $release -Token $token `
                -AssetName "MDiceV2-UI-v$assemblyVersion.zip" `
                -AssetPath $uiZip `
                -ContentType "application/zip"
        }
        else {
            Write-Host "  ⚠️  未找到 UI 包: $uiZip" -ForegroundColor Yellow
        }

        if (Test-Path $consoleZip) {
            Upload-Asset -Release $release -Token $token `
                -AssetName "MDiceV2-Console-v$assemblyVersion.zip" `
                -AssetPath $consoleZip `
                -ContentType "application/zip"
        }
        else {
            Write-Host "  ⚠️  未找到 Console 包: $consoleZip" -ForegroundColor Yellow
        }

        # 上传 Core.dll
        Write-Host ""
        Write-Host "🔧 核心库:" -ForegroundColor Cyan
        Upload-Asset -Release $release -Token $token `
            -AssetName "MDiceV2.Core.dll" `
            -AssetPath $coreDllPath

        # 上传 Mods
        Publish-AllMods -Release $release -Token $token -Version $assemblyVersion

        Write-Host ""
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Gray
        Write-Host "✅ 所有文件已上传到 GitHub Release!" -ForegroundColor Green
        Write-Host ""
        
    }
    else {
        Write-Host "⏭️  跳过上传到 GitHub" -ForegroundColor Yellow
        Write-Host ""
    }

    Write-Host "╔════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║                   ✅ 完整发布成功！                    ║" -ForegroundColor Green
    Write-Host "╚════════════════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""

}
finally {
    Pop-Location
}
