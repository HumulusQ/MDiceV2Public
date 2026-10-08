#!/usr/bin/env pwsh
# 发布脚本 - 确保根目录只包含可执行文件，所有依赖都在 Core 子目录中
# 修复版：使用 --no-self-contained 而不是 PublishSingleFile=true

param(
    [switch]$Clean = $false,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$PublishDir = "c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2_Published"
$WorkspaceDir = "c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2"

Write-Host "=== MDiceV2 发布脚本 ===" -ForegroundColor Cyan
Write-Host "配置：$Configuration | 运行时：$Runtime" -ForegroundColor Gray

if ($Clean) {
    Write-Host "清理发布目录..." -ForegroundColor Yellow
    if (Test-Path $PublishDir) {
        Remove-Item -Path "$PublishDir\*" -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
}

# 第一步: 发布 Launcher 到根目录
Write-Host "`n[1/3] 正在发布 Launcher..." -ForegroundColor Cyan
dotnet publish "$WorkspaceDir\MDiceV2.Launcher\MDiceV2.Launcher.csproj" `
    -c $Configuration `
    -r $Runtime `
    -o $PublishDir `
    --no-self-contained `
    --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Host "Launcher 发布失败!" -ForegroundColor Red
    exit 1
}

# 第二步: 发布 Console 到根目录
Write-Host "`n[2/3] 正在发布 Console..." -ForegroundColor Cyan
dotnet publish "$WorkspaceDir\MDiceV2.Console\MDiceV2.Console.csproj" `
    -c $Configuration `
    -r $Runtime `
    -o $PublishDir `
    --no-self-contained `
    --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Host "Console 发布失败!" -ForegroundColor Red
    exit 1
}

# 第三步: 发布 Core 到 Core 子目录
Write-Host "`n[3/3] 正在发布 Core 到 Core 子目录..." -ForegroundColor Cyan
$CoreDir = "$PublishDir\Core"
if (Test-Path $CoreDir) {
    Remove-Item -Path "$CoreDir\*" -Recurse -Force -ErrorAction SilentlyContinue
}

dotnet publish "$WorkspaceDir\MDiceV2.Core\MDiceV2.Core.csproj" `
    -c $Configuration `
    -r $Runtime `
    -o $CoreDir `
    --no-self-contained `
    --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Host "Core 发布失败!" -ForegroundColor Red
    exit 1
}

# 重命名 Core.exe 为 Core.Dice
Write-Host "`n重命名 Core.exe 为 .Dice..." -ForegroundColor Yellow
$CoreExeFile = Get-ChildItem -Path $CoreDir -Filter "MDiceV2.Core.exe" -File | Select-Object -First 1
if ($CoreExeFile) {
    $DiceFile = Join-Path $CoreDir "$($CoreExeFile.BaseName).Dice"
    Rename-Item -Path $CoreExeFile.FullName -NewName $DiceFile -Force
    Write-Host "✓ 已重命名为: $($CoreExeFile.BaseName).Dice" -ForegroundColor Green
}

# 清理 PDB 符号文件
Write-Host "清理 PDB 符号文件..." -ForegroundColor Yellow
Get-ChildItem -Path $PublishDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# 显示最终结果
Write-Host "`n=== 发布完成 ===" -ForegroundColor Green
Write-Host "`n根目录文件:" -ForegroundColor Cyan
Get-ChildItem -Path $PublishDir -MaxDepth 1 -File | ForEach-Object {
    $SizeMB = [Math]::Round($_.Length / 1MB, 2)
    "$($_.Name) - $SizeMB MB"
}

Write-Host "`nCore 子目录文件:" -ForegroundColor Cyan
Get-ChildItem -Path "$PublishDir\Core" -File | Select-Object Name | Select-Object -First 10

Write-Host "`n✓ 发布成功! 结构:" -ForegroundColor Green
Write-Host "  根目录: Launcher.exe + Console.exe + Avalonia依赖 DLL" -ForegroundColor Green
Write-Host "  Core/: MDiceV2.Core.Dice (游戏应用) + 所有依赖 DLL" -ForegroundColor Green
