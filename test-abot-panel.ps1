#!/usr/bin/env pwsh

<#
ABot 面板诊断脚本
用于测试面板是否正确注册和显示
#>

Write-Host "=== ABot 面板诊断脚本 ===" -ForegroundColor Cyan
Write-Host ""

# 第1步：检查编译产物
Write-Host "步骤 1: 检查编译产物" -ForegroundColor Yellow
$debugDir = "C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2_Debug"

$requiredDlls = @(
    "ABot.dll",
    "ABot.Core.dll",
    "ABot.CLI.dll",
    "MDiceV2.Interfaces.dll",
    "MDiceV2.Abstractions.dll",
    "MDiceV2.Launcher.exe"
)

$allFound = $true
foreach ($dll in $requiredDlls) {
    $path = Join-Path $debugDir $dll
    if (Test-Path $path) {
        $fileInfo = Get-Item $path
        Write-Host "  ✓ $dll - $(($fileInfo).Length / 1024)KB" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $dll - 未找到" -ForegroundColor Red
        $allFound = $false
    }
}

if (-not $allFound) {
    Write-Host ""
    Write-Host "❌ 缺少编译产物，需要重新构建" -ForegroundColor Red
    exit 1
}

# 第2步: 清理旧日志
Write-Host ""
Write-Host "步骤 2: 清理旧日志" -ForegroundColor Yellow

$logFiles = @(
    "$env:TEMP\abot_init.log",
    "$env:TEMP\abot_diagnose.log",
    "C:\Windows\Temp\abot_cpp_debug.log"
)

foreach ($log in $logFiles) {
    if (Test-Path $log) {
        Remove-Item $log -Force
        Write-Host "  ✓ 已删除 $(Split-Path $log -Leaf)"
    }
}

# 第3步: 启动程序
Write-Host ""
Write-Host "步骤 3: 启动程序进行测试" -ForegroundColor Yellow
Write-Host "当程序启动时，请" -ForegroundColor Cyan
Write-Host "  1. 等待 UI 完全加载" -ForegroundColor Cyan
Write-Host "  2. 检查导航栏中是否显示 'ABOT Interpreter' 面板" -ForegroundColor Cyan
Write-Host "  3. 如果显示，点击该面板后再点击脚本执行按钮" -ForegroundColor Cyan
Write-Host "  4. 完成后关闭程序" -ForegroundColor Cyan
Write-Host ""

cd $debugDir
& ".\MDiceV2.Launcher.exe" 2>&1 | Out-Null

# 第4步: 收集诊断信息
Write-Host ""
Write-Host "步骤 4: 收集诊断信息" -ForegroundColor Yellow
Write-Host ""

Write-Host "=== C# 初始化日志 ===" -ForegroundColor Cyan
if (Test-Path "$env:TEMP\abot_init.log") {
    Get-Content "$env:TEMP\abot_init.log" | Select-Object -Last 40
} else {
    Write-Host "（日志未生成）" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== 诊断日志 ===" -ForegroundColor Cyan
if (Test-Path "$env:TEMP\abot_diagnose.log") {
    Get-Content "$env:TEMP\abot_diagnose.log"
} else {
    Write-Host "（未生成）" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "=== C++ 诊断日志 ===" -ForegroundColor Cyan
if (Test-Path "C:\Windows\Temp\abot_cpp_debug.log") {
    Get-Content "C:\Windows\Temp\abot_cpp_debug.log"
} else {
    Write-Host "（未生成）" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "=== 诊断完成 ===" -ForegroundColor Green
