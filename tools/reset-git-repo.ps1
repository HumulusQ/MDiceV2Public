#!/usr/bin/env pwsh
# Git 仓库重置脚本
# 功能：完全重新初始化 Git 仓库，保留最新代码但删除所有历史

Write-Host "=== MDiceV2 Git 仓库重置脚本 ===" -ForegroundColor Cyan
Write-Host "`n[警告] 此操作将：" -ForegroundColor Red
Write-Host "  1. 删除所有 Git 历史记录"
Write-Host "  2. 创建新的初始提交（包含当前所有代码）"
Write-Host "  3. 需要强制推送到 GitHub (git push -f)"
Write-Host ""

$confirm = Read-Host "确认进行操作？(y/N)"
if ($confirm -ne 'y' -and $confirm -ne 'Y') {
    Write-Host "操作已取消" -ForegroundColor Yellow
    exit 0
}

try {
    Write-Host "`n[1] 移除 .git 目录..." -ForegroundColor Yellow
    Remove-Item -Path ".git" -Recurse -Force -ErrorAction Stop
    Write-Host "  ✓ 完成" -ForegroundColor Green

    Write-Host "`n[2] 应用新的 .gitignore..." -ForegroundColor Yellow
    # .gitignore 已经被修复，只需要确认它存在
    if (Test-Path ".gitignore") {
        Write-Host "  ✓ .gitignore 已存在" -ForegroundColor Green
    } else {
        Write-Host "  ✗ .gitignore 不存在！" -ForegroundColor Red
    }

    Write-Host "`n[3] 重新初始化 Git 仓库..." -ForegroundColor Yellow
    git init | Out-Null
    Write-Host "  ✓ 初始化完成" -ForegroundColor Green

    Write-Host "`n[4] 配置远程仓库..." -ForegroundColor Yellow
    git remote add origin "https://github.com/HumulusQ/MDiceV2Public.git" | Out-Null
    Write-Host "  ✓ 远程配置完成" -ForegroundColor Green

    Write-Host "`n[5] 暂存文件（根据新 .gitignore）..." -ForegroundColor Yellow
    git add . 2>&1 | Out-Null
    $stagedFiles = (git ls-files).Count
    Write-Host "  ✓ 已暂存 $stagedFiles 个文件" -ForegroundColor Green

    Write-Host "`n[6] 创建初始提交..." -ForegroundColor Yellow
    git commit -m "Initial commit: Clean repository with proper .gitignore" | Out-Null
    Write-Host "  ✓ 提交完成" -ForegroundColor Green

    Write-Host "`n[完成] Git 仓库重置完毕！" -ForegroundColor Cyan
    Write-Host "`n[下一步] 运行以下命令强制推送到 GitHub：" -ForegroundColor Yellow
    Write-Host "  git push -f origin master" -ForegroundColor White

} catch {
    Write-Host "`n[错误] 操作失败！" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
