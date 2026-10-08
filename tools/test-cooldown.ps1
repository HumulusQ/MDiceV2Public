#!/usr/bin/env powershell
# CustomizedReply 冷却时间功能测试脚本
# 用途: 验证按用户和全局冷却模式是否正常工作

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("parse", "log-check", "build", "test")]
    [string]$TestMode = "parse"
)

$WorkspaceRoot = Split-Path -Parent (Get-Location)
$ModPath = Join-Path $WorkspaceRoot "Mods\CustomizedReply"
$DataPath = Join-Path $ModPath "data.json"
$SourcePath = Join-Path $ModPath "CustomizedReplyMod.cs"

Write-Host "🔍 CustomizedReply 冷却时间测试工具" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# ============ 模式 1: 检查配置格式 ============
if ($TestMode -eq "parse")
{
    Write-Host "📋 模式: 检查配置格式" -ForegroundColor Yellow
    Write-Host ""
    
    if (-not (Test-Path $DataPath))
    {
        Write-Host "❌ 找不到 data.json: $DataPath" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "✓ 找到 data.json" -ForegroundColor Green
    Write-Host ""
    
    $json = Get-Content $DataPath -Raw | ConvertFrom-Json
    $timeCooldownRules = $json.replies | Where-Object {
        $_.conditions | Where-Object { $_.type -eq "TimeCooldown" }
    }
    
    if ($timeCooldownRules.Count -eq 0)
    {
        Write-Host "⚠️  未找到 TimeCooldown 条件的规则" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "请确保 data.json 中至少有一个这样的规则:" -ForegroundColor Cyan
        Write-Host @"
    {
      "trigger": "测试",
      "conditions": [{
        "type": "TimeCooldown",
        "value": "30",
        "value2": "秒_按用户"
      }]
    }
"@ -ForegroundColor Gray
        exit 0
    }
    
    Write-Host "✓ 找到 $($timeCooldownRules.Count) 个 TimeCooldown 规则" -ForegroundColor Green
    Write-Host ""
    
    foreach ($rule in $timeCooldownRules)
    {
        $trigger = $rule.trigger
        $cooldownCondition = $rule.conditions | Where-Object { $_.type -eq "TimeCooldown" } | Select-Object -First 1
        $value = $cooldownCondition.value
        $value2 = $cooldownCondition.value2
        
        Write-Host "📌 规则: '$trigger'" -ForegroundColor Cyan
        Write-Host "  - 触发词: $trigger" -ForegroundColor Gray
        Write-Host "  - 持续时间: $value"
        Write-Host "  - 模式配置: $value2"
        
        # 验证格式
        if (-not $value2 -or $value2 -notmatch "_")
        {
            Write-Host "    ❌ 格式错误! 应该是 '单位_作用域' 如 '秒_按用户' 或 '分钟_全局'" -ForegroundColor Red
        }
        else
        {
            $parts = $value2.Split('_')
            $unit = $parts[0]
            $scope = $parts[1]
            
            $unitValid = $unit -in @("秒", "分钟", "小时")
            $scopeValid = $scope -in @("按用户", "全局")
            
            if ($unitValid -and $scopeValid)
            {
                Write-Host "    ✓ 格式正确: 单位=$unit, 作用域=$scope" -ForegroundColor Green
                
                # 计算秒数
                $seconds = switch ($unit)
                {
                    "秒" { [int]$value }
                    "分钟" { [int]$value * 60 }
                    "小时" { [int]$value * 3600 }
                    default { 0 }
                }
                
                Write-Host "    ➜ 实际冷却: $seconds 秒 ($unit)" -ForegroundColor Gray
                
                if ($scope -eq "按用户")
                {
                    Write-Host "    ➜ 模式: 每个用户独立冷却" -ForegroundColor Gray
                }
                else
                {
                    Write-Host "    ➜ 模式: 所有用户共享冷却" -ForegroundColor Gray
                }
            }
            else
            {
                if (-not $unitValid)
                {
                    Write-Host "    ❌ 单位无效: '$unit' (应该是 秒、分钟 或 小时)" -ForegroundColor Red
                }
                if (-not $scopeValid)
                {
                    Write-Host "    ❌ 作用域无效: '$scope' (应该是 按用户 或 全局)" -ForegroundColor Red
                }
            }
        }
        
        Write-Host ""
    }
    
    Write-Host "✨ 配置检查完成" -ForegroundColor Green
}

# ============ 模式 2: 检查关键代码行 ============
elseif ($TestMode -eq "log-check")
{
    Write-Host "📟 模式: 检查关键代码行" -ForegroundColor Yellow
    Write-Host ""
    
    if (-not (Test-Path $SourcePath))
    {
        Write-Host "❌ 找不到源文件: $SourcePath" -ForegroundColor Red
        exit 1
    }
    
    $source = Get-Content $SourcePath -Raw
    
    # 检查关键的代码片段
    $checks = @(
        @{
            Name = "CheckCooldown 方法中的作用域解析"
            Pattern = 'parts\[1\]'
            Description = "正确解析 '单位_作用域' 格式"
        },
        @{
            Name = "UpdateTrackingMetrics 中的作用域传递"
            Pattern = 'msgProc\.UpdateCooldownTimestamp\([^,]+,\s*userId,\s*scope'
            Description = "正确传递 scope 参数"
        },
        @{
            Name = "全局模式的 key 生成"
            Pattern = '\$\"{ruleId}_\*\"'
            Description = "使用 '_*' 作为全局模式的标记"
        }
    )
    
    $passCount = 0
    $failCount = 0
    
    foreach ($check in $checks)
    {
        Write-Host "🔎 $($check.Name)" -ForegroundColor Cyan
        
        if ($source -match $check.Pattern)
        {
            Write-Host "  ✓ 检查通过" -ForegroundColor Green
            Write-Host "  ➜ $($check.Description)" -ForegroundColor Gray
            $passCount++
        }
        else
        {
            Write-Host "  ❌ 检查失败" -ForegroundColor Red
            Write-Host "  ➜ $($check.Description)" -ForegroundColor Gray
            $failCount++
        }
        
        Write-Host ""
    }
    
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Gray
    Write-Host "检查结果: $passCount 通过, $failCount 失败" -ForegroundColor Cyan
    
    if ($failCount -gt 0)
    {
        Write-Host "⚠️  建议检查上述失败的代码行" -ForegroundColor Yellow
    }
}

# ============ 模式 3: 构建项目 ============
elseif ($TestMode -eq "build")
{
    Write-Host "🔨 模式: 构建项目" -ForegroundColor Yellow
    Write-Host ""
    
    $rootPath = Split-Path -Parent $WorkspaceRoot
    $slnPath = Join-Path $rootPath "MDiceV2.sln"
    
    if (-not (Test-Path $slnPath))
    {
        Write-Host "❌ 找不到解决方案: $slnPath" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "构建中: dotnet build..." -ForegroundColor Cyan
    Write-Host ""
    
    & dotnet build $slnPath -c Debug
    
    if ($LASTEXITCODE -eq 0)
    {
        Write-Host ""
        Write-Host "✓ 构建成功" -ForegroundColor Green
    }
    else
    {
        Write-Host ""
        Write-Host "❌ 构建失败" -ForegroundColor Red
        exit 1
    }
}

# ============ 模式 4: 运行测试 ============
elseif ($TestMode -eq "test")
{
    Write-Host "🧪 模式: 快速功能测试提示" -ForegroundColor Yellow
    Write-Host ""
    
    Write-Host @"
请按以下步骤手动测试功能:

【按用户冷却 (rule_009)】
1. 用户 A 输入: 签到
   预期: ✅ 收到回复
2. 用户 A 在 5 秒内再次输入: 签到
   预期: ❌ 无反应 (冷却中)
3. 用户 B 立即输入: 签到
   预期: ✅ 收到回复 (独立冷却)
4. 等待 30 秒后，用户 A 输入: 签到
   预期: ✅ 收到回复 (冷却已过)

【全局冷却 (rule_010)】
1. 用户 A 输入: 抽奖
   预期: ✅ 收到回复
2. 用户 B 立即输入: 抽奖
   预期: ❌ 无反应 (全局冷却)
3. 用户 C 输入: 抽奖
   预期: ❌ 无反应 (全局冷却)
4. 等待 5 分钟后，任何用户输入: 抽奖
   预期: ✅ 收到回复 (冷却已过)

【验证日志】
搜索以下日志确认工作状态:
- "[CustomizedReply] Updated cooldown timestamp for rule: {规则} (scope: {作用域})"
- 查看 scope 是否为 "按用户" 或 "全局"

"@ -ForegroundColor Gray
    
    Write-Host "💡 提示:" -ForegroundColor Cyan
    Write-Host "- 按用户: 每个用户有独立的冷却时间" -ForegroundColor Gray
    Write-Host "- 全局: 任何用户触发后，其他用户都需要等待" -ForegroundColor Gray
    Write-Host ""
}

Write-Host "✨ 测试完成" -ForegroundColor Green
