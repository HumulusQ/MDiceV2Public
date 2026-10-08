# MDiceV2 Release Packaging Script
# 将编译好的Release文件打包为两个ZIP：有头版本 + 无头版本
# Usage: .\tools\pack-release.ps1

param(
    [string]$BuildDir = "MDiceV2_Release",
    [string]$OutputDir = "MDiceV2_Release_Packages",
    [string]$Version = ""
)

# 读取版本号
if (-not $Version) {
    [xml]$versionFile = Get-Content "Version.props"
    $Version = $versionFile.Project.PropertyGroup.AssemblyVersion
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "MDiceV2 Release Packaging v$Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 检查构建输出目录
if (-not (Test-Path $BuildDir)) {
    Write-Host "❌ 错误：找不到构建目录 '$BuildDir'" -ForegroundColor Red
    Write-Host "请先运行: .\tools\build-release.ps1" -ForegroundColor Yellow
    exit 1
}

# 创建输出目录
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
    Write-Host "✅ 创建输出目录: $OutputDir" -ForegroundColor Green
}

# 注意：需要确保以下文件已在 MDiceV2_Release 目录中
$appDir = Join-Path $BuildDir "app"
$modsDir = Join-Path $BuildDir "mods"

if (-not (Test-Path $appDir)) {
    Write-Host "❌ 错误：找不到应用目录 '$appDir'" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "📦 开始打包..." -ForegroundColor Yellow

# ============================================
# 有头版本 (UI Launcher)
# ============================================
$launcherPackage = "MDiceV2-UI-v$Version.zip"
$launcherPath = Join-Path $OutputDir $launcherPackage

Write-Host ""
Write-Host "🎨 打包有头版本 (UI Launcher)..." -ForegroundColor Cyan
Write-Host "   输出: $launcherPath" -ForegroundColor Gray

# 清理旧的包
if (Test-Path $launcherPath) {
    Remove-Item $launcherPath -Force
    Write-Host "   清理旧包" -ForegroundColor Gray
}

# 创建临时目录用于打包
$tempDir = Join-Path $env:TEMP "MDiceV2_Launcher_Pack"
if (Test-Path $tempDir) {
    Remove-Item $tempDir -Recurse -Force
}
New-Item -ItemType Directory -Path $tempDir | Out-Null

# 复制应用文件
Write-Host "   复制应用文件..." -ForegroundColor Gray
Copy-Item "$appDir\*" -Destination $tempDir -Recurse -Force

# 复制Mods
if (Test-Path $modsDir) {
    $modsDestDir = Join-Path $tempDir "mods"
    Copy-Item $modsDir -Destination $modsDestDir -Recurse -Force
    Write-Host "   复制Mods..." -ForegroundColor Gray
}

# 创建说明文件
$readmeContent = @"
MDiceV2 - UI版本 (有头版本)
版本: v$Version
================================

快速开始:
1. 解压此文件夹
2. 双击运行 MDiceV2.Launcher.exe

系统要求:
- Windows 10 或更高版本
- .NET 9.0 运行时 (已包含)

文件说明:
- MDiceV2.Launcher.exe ......... 主程序 (UI界面)
- MDiceV2.Core.dll ............ 核心库
- mods/ ...................... 模组目录
- data/ ...................... 数据文件

更新:
- 替换 MDiceV2.Core.dll 即可更新核心功能
- 替换 mods 目录下的DLL可更新模组

支持:
- GitHub: https://github.com/HumulusQ/MDiceV2
"@

$readmeContent | Out-File -FilePath (Join-Path $tempDir "README.txt") -Encoding UTF8
Write-Host "   创建说明文件..." -ForegroundColor Gray

# 压缩
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $launcherPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "   ✅ 完成" -ForegroundColor Green

# 清理临时文件
Remove-Item $tempDir -Recurse -Force

# ============================================
# 无头版本 (Console/Service)
# ============================================
$consolePackage = "MDiceV2-Console-v$Version.zip"
$consolePath = Join-Path $OutputDir $consolePackage

Write-Host ""
Write-Host "🖥️  打包无头版本 (Console/Service)..." -ForegroundColor Cyan
Write-Host "   输出: $consolePath" -ForegroundColor Gray

if (Test-Path $consolePath) {
    Remove-Item $consolePath -Force
    Write-Host "   清理旧包" -ForegroundColor Gray
}

# 创建临时目录用于打包
$tempDir = Join-Path $env:TEMP "MDiceV2_Console_Pack"
if (Test-Path $tempDir) {
    Remove-Item $tempDir -Recurse -Force
}
New-Item -ItemType Directory -Path $tempDir | Out-Null

# 复制应用文件
Write-Host "   复制应用文件..." -ForegroundColor Gray
Copy-Item "$appDir\*" -Destination $tempDir -Recurse -Force

# 复制Mods
if (Test-Path $modsDir) {
    $modsDestDir = Join-Path $tempDir "mods"
    Copy-Item $modsDir -Destination $modsDestDir -Recurse -Force
    Write-Host "   复制Mods..." -ForegroundColor Gray
}

# 创建说明文件
$readmeContent = @"
MDiceV2 - Console版本 (无头版本/Service模式)
版本: v$Version
================================

快速开始:
1. 解压此文件夹
2. 打开命令行，cd 到此目录
3. 运行: MDiceV2.Console.exe

或作为 Windows 服务运行 (详见配置文件)

系统要求:
- Windows 7 或更高版本
- .NET 9.0 运行时 (已包含)

文件说明:
- MDiceV2.Console.exe ........ 主程序 (控制台/Service版本)
- MDiceV2.Core.dll .......... 核心库 (与UI版本共用)
- mods/ .................... 模组目录
- data/ .................... 数据文件

更新:
- 替换 MDiceV2.Core.dll 即可更新核心功能
- 替换 mods 目录下的DLL可更新模组

配置:
- 编辑 data/ 中的配置文件进行部署配置

注意:
- 此版本不需要图形界面
- 适合作为服务器或后台服务运行
- 与UI版本共用相同的 MDiceV2.Core.dll

支持:
- GitHub: https://github.com/HumulusQ/MDiceV2
"@

$readmeContent | Out-File -FilePath (Join-Path $tempDir "README.txt") -Encoding UTF8
Write-Host "   创建说明文件..." -ForegroundColor Gray

# 压缩
[System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $consolePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "   ✅ 完成" -ForegroundColor Green

# 清理临时文件
Remove-Item $tempDir -Recurse -Force

# ============================================
# 输出总结
# ============================================
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "✅ 打包完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "📦 生成的文件:" -ForegroundColor Cyan
Write-Host "   1. $launcherPackage (有头版本/UI)" -ForegroundColor White
Write-Host "      └─ 包含: MDiceV2.Launcher.exe + 依赖 + Mods" -ForegroundColor Gray
Write-Host ""
Write-Host "   2. $consolePackage (无头版本/Console)" -ForegroundColor White
Write-Host "      └─ 包含: MDiceV2.Console.exe + 依赖 + Mods" -ForegroundColor Gray
Write-Host ""
Write-Host "📁 输出目录: $OutputDir" -ForegroundColor Yellow
Write-Host ""
Write-Host "使用方式:" -ForegroundColor Cyan
Write-Host "   - publish.bat      → 只上传 Core.dll 和 Mod (增量更新)" -ForegroundColor Gray
Write-Host "   - publish_all.bat  → 上传两个完整包 + Core.dll + Mod" -ForegroundColor Gray
Write-Host ""
