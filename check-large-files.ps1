#!/usr/bin/env pwsh
# 诊断脚本：检查 Git 中的大文件和被追踪的不应该被追踪的文件

Write-Host "=== MDiceV2 Git 大文件诊断 ===" -ForegroundColor Cyan
Write-Host ""

# 1. 显示 Git 中的所有大文件
Write-Host "[1] Git 中大于 1MB 的文件：" -ForegroundColor Yellow
git ls-files --stage | awk '{print $4}' | while read file; do
    if [[ -f "$file" ]]; then
        size=$(stat -c%s "$file" 2>/dev/null || stat -f%z "$file" 2>/dev/null)
        if [[ $size -gt 1048576 ]]; then
            mb=$(echo "scale=2; $size / 1048576" | bc)
            echo "$mb MB: $file"
        fi
    fi
done | sort -rn

# 2. 显示 Git 对象数据库中的大对象
Write-Host "`n[2] Git 对象数据库统计：" -ForegroundColor Yellow
git count-objects -v

# 3. 显示工作目录中被追踪但不应该被追踪的文件
Write-Host "`n[3] 被追踪的不应该被追踪的文件：" -ForegroundColor Yellow
$patterns = @(
    "bin/*",
    "obj/*",
    "*.exe",
    "*.dll",
    "*.pdb",
    "*.zip",
    "MDiceV2_Debug/*",
    "MDiceV2_Published/*",
    "MDiceV2_Release/*",
    "releases/*"
)

foreach ($pattern in $patterns) {
    $files = git ls-files --stage | where-object { $_ -match ($pattern -replace '\*', '.*') }
    if ($files) {
        Write-Host "  $pattern：找到 $($files.Count) 个文件" -ForegroundColor Red
    }
}

# 4. 检查 .gitignore 文件是否有问题
Write-Host "`n[4] .gitignore 文件内容检查：" -ForegroundColor Yellow
if (Test-Path ".gitignore") {
    $gitignoreContent = Get-Content ".gitignore"
    if ($gitignoreContent -match "ECHO") {
        Write-Host "  警告：.gitignore 包含脚本输出！需要修复。" -ForegroundColor Red
    }
    Write-Host "  .gitignore 行数：" (($gitignoreContent | Measure-Object -Line).Lines)
} else {
    Write-Host "  警告：.gitignore 不存在！" -ForegroundColor Red
}

Write-Host "`n=== 诊断完成 ===" -ForegroundColor Cyan
