#!/usr/bin/env pwsh
# Git 大文件清理脚本
# 功能：从 Git 中移除不应该被追踪的大文件

param(
    [switch]$Full = $false  # 如果为 true，尝试使用 git filter-repo
)

Write-Host "=== MDiceV2 Git 清理脚本 ===" -ForegroundColor Cyan

# 第一步：检查是否可以取消追踪文件
Write-Host "`n[1] 取消追踪问题文件..." -ForegroundColor Yellow

$problematicPatterns = @(
    "*/bin/*",
    "*/obj/*", 
    "*.dll",
    "*.exe",
    "MDiceV2_Debug/*",
    "MDiceV2_Published/*",
    "MDiceV2_Release/*"
)

# 注意：这些只是测试，实际操作需要谨慎
$fileCount = 0
foreach ($pattern in $problematicPatterns) {
    $files = git ls-files | Where-Object { $_ -like $pattern }
    $fileCount += $files.Count
}

Write-Host "  发现 $fileCount 个需要移除的文件"

# 第二步：提示用户
Write-Host "`n[2] 清理方式：" -ForegroundColor Yellow

Write-Host "  选项 A：使用 git rm --cached (仅适合小数量文件)" -ForegroundColor Gray
Write-Host "  选项 B：使用 git filter-repo (需要安装，可删除历史)" -ForegroundColor Gray
Write-Host "  选项 C：完全重新初始化仓库" -ForegroundColor Gray

Write-Host "`n[建议] 由于已有 339 个 bin/obj 和 243 个 DLL/EXE 文件，" -ForegroundColor Cyan
Write-Host "推荐使用选项 C：完全重新初始化仓库，保留最新代码。" -ForegroundColor Cyan

Write-Host "`n[3] 执行步骤（如需要）：" -ForegroundColor Yellow
Write-Host "  1. 确保所有本地更改已提交或丢弃"
Write-Host "  2. 运行：cd 到项目目录"
Write-Host "  3. 运行：.\tools\reset-git-repo.ps1"
Write-Host "  4. 运行：git push -f origin master"

Write-Host "`n[完成] 诊断脚本执行完毕。" -ForegroundColor Green
