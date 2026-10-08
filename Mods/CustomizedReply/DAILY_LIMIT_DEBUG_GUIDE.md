# 每日使用次数限制调试指南

**创建日期**: 2026-04-01  
**目的**: 帮助诊断为什么每日次数限制修复没有生效

---

## 📋 添加的日志记录点

### 1. MessageProcessor.cs 中的日志

#### CheckDailyLimit() 方法 [行 1348]

添加的日志记录:
```
[CheckDailyLimit] 检查开始 - 规则: {ruleId}, 用户: {userId}, 作用域: {scope}, 限额: {limitCount}
[CheckDailyLimit] 生成的 key: {key}, 今日日期: {date}
[CheckDailyLimit] 找到历史记录 - 记录日期: {date}, 当前计数: {count}
[CheckDailyLimit] ⚠️ 日期已更改，计数重置为 0
[CheckDailyLimit] 次数比较: {count} < {limit} = {result}
[CheckDailyLimit] ✓ 返回 true (未超限，可继续使用)
[CheckDailyLimit] ✗ 返回 false (已达到每日限额)
```

**作用**: 追踪每日限制的检查过程和次数比较结果

#### IncrementDailyCount() 方法 [行 1380]

添加的日志记录:
```
[IncrementDailyCount] 递增开始 - 规则: {ruleId}, 用户: {userId}, 作用域: {scope}
[IncrementDailyCount] 生成的 key: {key}, 今日日期: {date}
[IncrementDailyCount] 增加前的计数: {count}
[IncrementDailyCount] 无历史记录或日期不同，从 0 开始计数
[IncrementDailyCount] 日期已更改，重置计数
[IncrementDailyCount] 计数递增: {before} -> {after}
[IncrementDailyCount] ✓ 增加后的计数: {count}
```

**作用**: 追踪每日次数计数的递增过程

---

### 2. CustomizedReplyMod.cs 中的日志

#### CheckAllConditions() 方法 [行 2600]

添加的日志记录:
```
[CustomizedReply.CheckAllConditions] 开始检查 {count} 个条件
[CustomizedReply.CheckAllConditions] 检查条件: {type}
[CustomizedReply.CheckAllConditions] ✗ 条件检查失败: {type}
[CustomizedReply.CheckAllConditions] ✓ 条件通过: {type}
[CustomizedReply.CheckAllConditions] ✓ 所有条件都通过
```

**作用**: 追踪规则的条件检查过程

#### CheckSingleCondition() 方法 [行 2628]

添加的日志记录:
```
[CustomizedReply.CheckSingleCondition] 开始检查 {type}
[CustomizedReply.CheckSingleCondition] 检查结果 (反相前): {result}
[CustomizedReply.CheckSingleCondition] 应用反相匹配: {before} -> {after}
[CustomizedReply.CheckSingleCondition] 最终结果: {result}
```

**作用**: 追踪单个条件的检查结果和反相逻辑

#### CheckDailyLimit() 方法 (CustomizedReplyMod 中) [行 2630]

添加的日志记录:
```
[CustomizedReply.CheckDailyLimit] 参数解析失败
[CustomizedReply.CheckDailyLimit] MessageProcessor.Instance 为 null!
[CustomizedReply.CheckDailyLimit] 即将检查每日限额 - 规则: {ruleId}, 用户: {userId}, 作用域: {scope}, 限额: {limitCount}
[CustomizedReply.CheckDailyLimit] 检查结果: ✓ 通过 或 ✗ 失败
```

**作用**: 追踪 CustomizedReply 层的每日限制检查

#### UpdateTrackingMetrics() 方法 [行 2520]

添加的日志记录:
```
[CustomizedReply.UpdateTrackingMetrics] 即将递增每日计数 - 规则: {ruleId}, 用户: {userId}, 作用域: {scope}
[CustomizedReply.UpdateTrackingMetrics] ✓ 每日计数已递增 - 规则: {ruleId}
```

**作用**: 追踪规则执行后的计数更新

---

## 🔍 完整的调用链日志示例

### 场景: 用户第一次触发规则 "投喂"

```
【规则匹配】
[CustomizedReply] ✓ Trigger matched for rule #8
[CustomizedReply] Checking rule #8: Trigger='投喂', MatchType=0, Conditions=1

【开始条件检查】
[CustomizedReply.CheckAllConditions] 开始检查 1 个条件
[CustomizedReply.CheckAllConditions] 检查条件: DailyUsageLimit (Value: '1', Value2: '按用户', IsInverted: false)
[CustomizedReply.CheckSingleCondition] 开始检查 DailyUsageLimit

【检查每日限制】
[CustomizedReply.CheckDailyLimit] 参数解析成功: limitCount=1
[CustomizedReply.CheckDailyLimit] 即将检查每日限额 - 规则: 投喂, 用户: 12345678, 作用域: 按用户, 限额: 1
[CheckDailyLimit] 检查开始 - 规则: 投喂, 用户: 12345678, 作用域: 按用户, 限额: 1
[CheckDailyLimit] 生成的 key: 投喂_12345678, 今日日期: 2026-04-01
[CheckDailyLimit] 未找到历史记录，这是首次触发
[CheckDailyLimit] ✓ 返回 true (首次触发，未超限)
[CustomizedReply.CheckDailyLimit] 检查结果: ✓ 通过 (可继续使用)

【条件判定】
[CustomizedReply.CheckSingleCondition] 检查结果 (反相前): true
[CustomizedReply.CheckSingleCondition] 最终结果: true
[CustomizedReply.CheckAllConditions] ✓ 条件通过: DailyUsageLimit
[CustomizedReply.CheckAllConditions] ✓ 所有条件都通过
[CustomizedReply] ✓ All conditions passed for rule #8

【执行规则】
[CustomizedReply] Selected reply: '投喂成功！增加 XXX 饱食度'

【更新计数】
[CustomizedReply.UpdateTrackingMetrics] 即将递增每日计数 - 规则: 投喂, 用户: 12345678, 作用域: 按用户
[IncrementDailyCount] 递增开始 - 规则: 投喂, 用户: 12345678, 作用域: 按用户
[IncrementDailyCount] 生成的 key: 投喂_12345678, 今日日期: 2026-04-01
[IncrementDailyCount] 无历史记录或日期不同，从 0 开始计数
[IncrementDailyCount] 计数递增: 0 -> 1
[IncrementDailyCount] ✓ 增加后的计数: 1 (日期: 2026-04-01)
[CustomizedReply.UpdateTrackingMetrics] ✓ 每日计数已递增 - 规则: 投喂

【返回回复】
[CustomizedReply] ✓ Group:123456789 User:12345678 - Returning Intercepted=true
```

### 场景: 用户第二次触发规则 "投喂" (应被阻止)

```
【规则匹配】
[CustomizedReply] ✓ Trigger matched for rule #8

【开始条件检查】
[CustomizedReply.CheckAllConditions] 开始检查 1 个条件
[CustomizedReply.CheckAllConditions] 检查条件: DailyUsageLimit (Value: '1', Value2: '按用户', IsInverted: false)
[CustomizedReply.CheckSingleCondition] 开始检查 DailyUsageLimit

【检查每日限制】
[CustomizedReply.CheckDailyLimit] 即将检查每日限额 - 规则: 投喂, 用户: 12345678, 作用域: 按用户, 限额: 1
[CheckDailyLimit] 检查开始 - 规则: 投喂, 用户: 12345678, 作用域: 按用户, 限额: 1
[CheckDailyLimit] 生成的 key: 投喂_12345678, 今日日期: 2026-04-01
[CheckDailyLimit] 找到历史记录 - 记录日期: 2026-04-01, 当前计数: 1
[CheckDailyLimit] 次数比较: 1 < 1 = false
[CheckDailyLimit] ✗ 返回 false (已达到每日限额 1，已使用 1 次)
[CustomizedReply.CheckDailyLimit] 检查结果: ✗ 失败 (已达限额)

【条件判定】
[CustomizedReply.CheckSingleCondition] 检查结果 (反相前): false
[CustomizedReply.CheckSingleCondition] 最终结果: false
[CustomizedReply.CheckAllConditions] ✗ 条件检查失败: DailyUsageLimit
[CustomizedReply] ✗ Conditions not met for rule #8

【返回结果】
Rule #8 NOT matched (条件未通过)
```

---

## 🐛 常见问题诊断

### 问题 1: 日志中没有看到任何 CheckDailyLimit 日志

**可能原因**:
1. ❌ 规则中没有配置 DailyUsageLimit 条件
2. ❌ 条件的 type 拼写错误（应该是 `DailyUsageLimit`）
3. ❌ condition.Value 为空或无法解析为数字

**排查步骤**:
1. 检查 data.json 中的规则配置
   ```json
   "conditions": [{
     "type": "DailyUsageLimit"   // 注意大小写
     "value": "1",
     "value2": "按用户"
   }]
   ```
2. 查看日志是否有 `[CustomizedReply.CheckAllConditions] 开始检查 X 个条件`
3. 如果没有，说明规则没有配置条件

### 问题 2: CheckDailyLimit 显示 true，但规则仍然被执行多次

**可能原因**:
1. ❌ IncrementDailyCount 没有被调用
2. ❌ UpdateTrackingMetrics 中的 DailyUsageLimit 条件判断有问题
3. ❌ 作用域参数不匹配

**排查步骤**:
1. 搜索日志中的 `[CustomizedReply.UpdateTrackingMetrics] 即将递增每日计数`
2. 如果没有找到，说明条件类型匹配失败
3. 检查日志中的：
   ```
   [CheckDailyLimit] 次数比较: X < Y = true/false
   ```
   这决定了是否允许执行

### 问题 3: 日期重置不工作

**症状**: 第二天规则应该重置但没有重置

**排查步骤**:
1. 查找日志 `[CheckDailyLimit] ⚠️ 日期已更改`
2. 检查日期格式是否正确
3. 验证系统时间是否正确

---

## 📝 调试步骤

1. **启用 LogSender 输出到文件**
   - 确保 LogSender 配置正确
   - 运行程序并触发规则

2. **收集日志**
   - 查找所有包含 `[CheckDailyLimit]` 的日志行
   - 查找所有包含 `[IncrementDailyCount]` 的日志行

3. **分析日志流程**
   - 追踪消息触发的完整路径
   - 注意 key 的生成（应该是 "规则名_用户ID" 或 "规则名_*"）
   - 检查计数的增加

4. **验证数据存储**
   - 检查 data/user/ 目录中是否有 DailyLimitTracking 数据
   - 文件内容格式应该是：`2026-04-01,1` (日期,计数)

---

## ✨ 新增日志格式说明

| 日志前缀 | 含义 | 位置 |
|---------|------|------|
| `[CheckDailyLimit]` | MessageProcessor 层的每日限制检查 | MDiceV2.Core |
| `[IncrementDailyCount]` | MessageProcessor 层的计数递增 | MDiceV2.Core |
| `[CustomizedReply.CheckDailyLimit]` | Mod 层的每日限制检查 | CustomizedReply Mod |
| `[CustomizedReply.CheckAllConditions]` | Mod 层的全部条件检查 | CustomizedReply Mod |
| `[CustomizedReply.CheckSingleCondition]` | Mod 层的单个条件检查 | CustomizedReply Mod |
| `[CustomizedReply.UpdateTrackingMetrics]` | Mod 层的计数更新 | CustomizedReply Mod |

---

## 🎯 关键检查点

| 检查项 | 正常日志 | 异常迹象 |
|--------|--------|--------|
| **规则配置** | `开始检查 1 个条件` | `开始检查 0 个条件` |
| **条件识别** | `检查条件: DailyUsageLimit` | 没有这行日志 |
| **参数解析** | `限额: 1` | `参数无效` |
| **首次触发** | `未找到历史记录` | - |
| **计数检查** | `当前计数: 0` | `当前计数: {异常值}` |
| **次数比较** | `次数比较: 0 < 1 = true` | 比较结果错误 |
| **计数递增** | `计数递增: 0 -> 1` | 没有这行日志 |
| **第二次触发** | `次数比较: 1 < 1 = false` | 返回 true |

---

## 🔧 后续测试

使用以下日志行来验证修复是否生效：

1. **第一次触发**:
   ```
   [CustomizedReply.UpdateTrackingMetrics] ✓ 每日计数已递增
   [IncrementDailyCount] ✓ 增加后的计数: 1
   ```

2. **第二次触发（应被阻止）**:
   ```
   [CheckDailyLimit] 次数比较: 1 < 1 = false
   [CheckDailyLimit] ✓ 返回 false (已达到每日限额)
   [CustomizedReply] ✗ Conditions not met for rule #8
   ```

3. **日志中出现正确的作用域**:
   ```
   [CheckDailyLimit] 生成的 key: 投喂_12345678
   [CheckDailyLimit] 生成的 key: 投喂_*  (全局时)
   ```

---

**修复完成日期**: 2026-04-01  
**日志监控就绪**: ✅
