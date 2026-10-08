# 每日次数限制详细日志修复总结

**完成日期**: 2026-04-01  
**修复范围**: 添加详细的日志记录以诊断每日次数限制问题

---

## 📌 修改概述

为了诊断为什么每日次数限制修复没有生效，已在以下方法中添加了详细的日志记录到 LogSender：

### 修改文件

1. **MDiceV2.Core/Models/MessageProcessor.cs**
   - CheckDailyLimit() 方法
   - IncrementDailyCount() 方法

2. **Mods/CustomizedReply/CustomizedReplyMod.cs**
   - CheckAllConditions() 方法
   - CheckSingleCondition() 方法
   - CheckDailyLimit() 方法（CustomizedReply 层）
   - UpdateTrackingMetrics() 方法

---

## 📊 添加的日志详情

### 1. MessageProcessor.CheckDailyLimit() 

**添加位置**: MDiceV2.Core/Models/MessageProcessor.cs (约 1348 行)

**日志记录**:

| 日志 | 何时输出 | 包含信息 |
|------|--------|--------|
| `[CheckDailyLimit] 检查开始` | 方法开始 | 规则名、用户ID、作用域、限额 |
| `[CheckDailyLimit] 生成的 key` | 生成键后 | key 值、今日日期 |
| `[CheckDailyLimit] 找到历史记录` | 有历史数据 | 记录日期、当前计数 |
| `[CheckDailyLimit] ⚠️ 日期已更改` | 需要重置 | 从哪个日期到哪个日期 |
| `[CheckDailyLimit] 次数比较: X < Y = Z` | 进行比较 | 具体数值和结果 |
| `[CheckDailyLimit] ✓ 返回 true` | 通过检查 | 原因（新的一天/未超限/首次触发） |
| `[CheckDailyLimit] ✗ 返回 false` | 失败（已超限） | 已用次数和限额 |

**示例输出**:
```
[CheckDailyLimit] 检查开始 - 规则: 投喂, 用户: 12345678, 作用域: 按用户, 限额: 1
[CheckDailyLimit] 生成的 key: 投喂_12345678, 今日日期: 2026-04-01
[CheckDailyLimit] 找到历史记录 - 记录日期: 2026-04-01, 当前计数: 1
[CheckDailyLimit] 次数比较: 1 < 1 = false
[CheckDailyLimit] ✗ 返回 false (已达到每日限额 1，已使用 1 次)
```

---

### 2. MessageProcessor.IncrementDailyCount()

**添加位置**: MDiceV2.Core/Models/MessageProcessor.cs (约 1380 行)

**日志记录**:

| 日志 | 何时输出 | 包含信息 |
|------|--------|--------|
| `[IncrementDailyCount] 递增开始` | 方法开始 | 规则名、用户ID、作用域 |
| `[IncrementDailyCount] 生成的 key` | 生成键后 | key 值、今日日期 |
| `[IncrementDailyCount] 增加前的计数` | 有历史记录 | 增加前的计数 |
| `[IncrementDailyCount] 无历史记录` | 首次调用 | 说明从 0 开始 |
| `[IncrementDailyCount] 日期已更改` | 需要重置 | 旧日期和旧计数 |
| `[IncrementDailyCount] 计数递增: X -> Y` | 执行递增 | 递增前后的数值 |
| `[IncrementDailyCount] ✓ 增加后的计数` | 成功完成 | 最终计数和日期 |
| `[IncrementDailyCount] ✗ 增加失败` | 出现错误 | 错误描述 |

**示例输出**:
```
[IncrementDailyCount] 递增开始 - 规则: 投喂, 用户: 12345678, 作用域: 按用户
[IncrementDailyCount] 生成的 key: 投喂_12345678, 今日日期: 2026-04-01
[IncrementDailyCount] 无历史记录或日期不同，从 0 开始计数
[IncrementDailyCount] 计数递增: 0 -> 1
[IncrementDailyCount] ✓ 增加后的计数: 1 (日期: 2026-04-01)
```

---

### 3. CustomizedReplyMod.CheckAllConditions()

**添加位置**: Mods/CustomizedReply/CustomizedReplyMod.cs (约 2600 行)

**日志记录**:

| 日志 | 何时输出 | 包含信息 |
|------|--------|--------|
| `[CustomizedReply.CheckAllConditions] 开始检查 N 个条件` | 方法开始 | 条件数量 |
| `[CustomizedReply.CheckAllConditions] 检查条件: TYPE` | 每个条件 | 条件类型、Value、Value2、IsInverted |
| `[CustomizedReply.CheckAllConditions] ✗ 条件检查失败` | 任何条件失败 | 失败的条件类型 |
| `[CustomizedReply.CheckAllConditions] ✓ 条件通过` | 条件成功 | 通过的条件类型 |
| `[CustomizedReply.CheckAllConditions] ✓ 所有条件都通过` | 全部通过 | - |

**示例输出**:
```
[CustomizedReply.CheckAllConditions] 开始检查 1 个条件
[CustomizedReply.CheckAllConditions] 检查条件: DailyUsageLimit (Value: '1', Value2: '按用户', IsInverted: false)
[CustomizedReply.CheckAllConditions] ✓ 条件通过: DailyUsageLimit
[CustomizedReply.CheckAllConditions] ✓ 所有条件都通过
```

---

### 4. CustomizedReplyMod.CheckSingleCondition()

**添加位置**: Mods/CustomizedReply/CustomizedReplyMod.cs (约 2628 行)

**日志记录**:

| 日志 | 何时输出 | 包含信息 |
|------|--------|--------|
| `[CustomizedReply.CheckSingleCondition] 开始检查 TYPE` | 方法开始 | 条件类型 |
| `[CustomizedReply.CheckSingleCondition] 检查结果 (反相前)` | 获得结果 | 原始的 true/false |
| `[CustomizedReply.CheckSingleCondition] 应用反相匹配` | 有 IsInverted | 从 X 到 Y |
| `[CustomizedReply.CheckSingleCondition] 最终结果` | 方法结束 | 最终的 true/false |

**示例输出**:
```
[CustomizedReply.CheckSingleCondition] 开始检查 DailyUsageLimit
[CustomizedReply.CheckSingleCondition] 检查结果 (反相前): true
[CustomizedReply.CheckSingleCondition] 最终结果: true
```

---

### 5. CustomizedReplyMod.CheckDailyLimit()

**添加位置**: Mods/CustomizedReply/CustomizedReplyMod.cs (约 2650 行)

**日志记录**:

| 日志 | 何时输出 | 包含信息 |
|------|--------|--------|
| `[CustomizedReply.CheckDailyLimit] 参数解析失败` | 参数错误 | - |
| `[CustomizedReply.CheckDailyLimit] MessageProcessor.Instance 为 null` | 初始化问题 | - |
| `[CustomizedReply.CheckDailyLimit] 即将检查每日限额` | 方法开始 | 规则、用户、作用域、限额 |
| `[CustomizedReply.CheckDailyLimit] 检查结果` | 方法完成 | ✓ 通过 或 ✗ 失败 |

**示例输出**:
```
[CustomizedReply.CheckDailyLimit] 即将检查每日限额 - 规则: 投喂, 用户: 12345678, 作用域: 按用户, 限额: 1
[CustomizedReply.CheckDailyLimit] 检查结果: ✓ 通过 (可继续使用)
```

---

### 6. CustomizedReplyMod.UpdateTrackingMetrics()

**添加位置**: Mods/CustomizedReply/CustomizedReplyMod.cs (约 2540 行)

**日志记录**:

| 日志 | 何时输出 | 包含信息 |
|------|--------|--------|
| `[CustomizedReply.UpdateTrackingMetrics] 即将递增每日计数` | 规则执行后 | 规则、用户、作用域 |
| `[CustomizedReply.UpdateTrackingMetrics] ✓ 每日计数已递增` | 执行完成 | 规则名 |

**示例输出**:
```
[CustomizedReply.UpdateTrackingMetrics] 即将递增每日计数 - 规则: 投喂, 用户: 12345678, 作用域: 按用户
[CustomizedReply.UpdateTrackingMetrics] ✓ 每日计数已递增 - 规则: 投喂
```

---

## 🔍 日志分析流程

### 完整的成功流程

```
【消息接收】
[CustomizedReply] OnGroupMessage called

【规则匹配】
[CustomizedReply] Checking rule #8: Trigger='投喂'

【条件检查】
[CustomizedReply.CheckAllConditions] 开始检查 1 个条件
[CustomizedReply.CheckAllConditions] 检查条件: DailyUsageLimit (Value: '1', Value2: '按用户', IsInverted: false)

【单个条件检查】
[CustomizedReply.CheckSingleCondition] 开始检查 DailyUsageLimit

【每日限制检查】
[CustomizedReply.CheckDailyLimit] 即将检查每日限额 - 规则: 投喂, 用户: 12345678, 作用域: 按用户, 限额: 1

【MessageProcessor 层检查】
[CheckDailyLimit] 检查开始 - 规则: 投喂, 用户: 12345678, 作用域: 按用户, 限额: 1
[CheckDailyLimit] 生成的 key: 投喂_12345678, 今日日期: 2026-04-01
[CheckDailyLimit] 未找到历史记录，这是首次触发
[CheckDailyLimit] ✓ 返回 true (首次触发，未超限)

【返回条件检查结果】
[CustomizedReply.CheckDailyLimit] 检查结果: ✓ 通过 (可继续使用)
[CustomizedReply.CheckSingleCondition] 检查结果 (反相前): true
[CustomizedReply.CheckSingleCondition] 最终结果: true
[CustomizedReply.CheckAllConditions] ✓ 条件通过: DailyUsageLimit
[CustomizedReply.CheckAllConditions] ✓ 所有条件都通过

【规则执行】
[CustomizedReply] ✓ All conditions passed for rule #8
[CustomizedReply] Selected reply: '投喂成功！增加 XXX 饱食度'

【计数更新】
[CustomizedReply.UpdateTrackingMetrics] 即将递增每日计数 - 规则: 投喂, 用户: 12345678, 作用域: 按用户
[IncrementDailyCount] 递增开始 - 规则: 投喂, 用户: 12345678, 作用域: 按用户
[IncrementDailyCount] 生成的 key: 投喂_12345678, 今日日期: 2026-04-01
[IncrementDailyCount] 无历史记录或日期不同，从 0 开始计数
[IncrementDailyCount] 计数递增: 0 -> 1
[IncrementDailyCount] ✓ 增加后的计数: 1 (日期: 2026-04-01)
[CustomizedReply.UpdateTrackingMetrics] ✓ 每日计数已递增 - 规则: 投喂

【回复发送】
[CustomizedReply] ✓ Group:123456789 User:12345678 - Returning Intercepted=true
```

---

## ✅ 验证清单

使用以下清单验证修复是否生效：

### 首次触发规则

- [ ] 看到 `[CheckDailyLimit] 未找到历史记录`
- [ ] 看到 `[CheckDailyLimit] ✓ 返回 true`
- [ ] 看到 `[CustomizedReply] Selected reply`
- [ ] 看到 `[IncrementDailyCount] 计数递增: 0 -> 1`
- [ ] **结果**: ✅ 获得回复，计数被增加

### 第二次触发相同规则（同一天）

- [ ] 看到 `[CheckDailyLimit] 找到历史记录`
- [ ] 看到 `[CheckDailyLimit] 当前计数: 1`
- [ ] 看到 `[CheckDailyLimit] 次数比较: 1 < 1 = false`
- [ ] 看到 `[CheckDailyLimit] ✗ 返回 false`
- [ ] **不**看到 `[CustomizedReply] Selected reply`
- [ ] **不**看到 `[IncrementDailyCount] 计数递增`
- [ ] **结果**: ❌ 没有回复，计数未增加

### 第二天再次触发同一规则

- [ ] 看到 `[CheckDailyLimit] ⚠️ 日期已更改`
- [ ] 看到 `[CheckDailyLimit] 计数重置为 0`
- [ ] 看到 `[CheckDailyLimit] ✓ 返回 true`
- [ ] 看到 `[IncrementDailyCount] 计数递增: 0 -> 1`
- [ ] **结果**: ✅ 获得回复，计数重置

---

## 🎯 诊断问题

### 如果看不到 CheckDailyLimit 日志

**可能原因**:
1. 条件类型不是 "DailyUsageLimit"
2. DailyUsageLimit 条件的 Value 为空或无效
3. 条件检查提前失败（其他条件）

**检查**:
```
看是否有: [CustomizedReply.CheckAllConditions] 开始检查 N 个条件
如果 N = 0，说明没有配置条件
```

### 如果 CheckDailyLimit 返回 true，但规则被重复执行

**可能原因**:
1. IncrementDailyCount 没有被执行
2. UpdateTrackingMetrics 中的条件判断有问题

**检查**:
```
搜索: [CustomizedReply.UpdateTrackingMetrics] 即将递增每日计数
如果没有，说明这个规则没有 DailyUsageLimit 条件
```

### 如果计数没有增加

**可能原因**:
1. MessageProcessor.Instance 为 null
2. IncrementDailyCount 中的 AddOrUpdate 没有正确执行

**检查**:
```
搜索: [IncrementDailyCount] ✓ 增加后的计数
如果没有，说明出现错误或没有被调用
```

---

## 🔧 使用新日志的步骤

1. **构建项目**
   ```
   dotnet build MDiceV2.sln -c Debug
   ```

2. **启动程序**
   - 运行 Debug 版本

3. **触发规则**
   - 输入触发词

4. **收集日志**
   - 搜索包含 `[CheckDailyLimit]` 的日志行
   - 搜索包含 `[IncrementDailyCount]` 的日志行
   - 搜索包含 `CustomizedReply` 的相关日志行

5. **分析流程**
   - 按照上面的"完整成功流程"进行比对
   - 查找异常或缺失的日志行

6. **诊断问题**
   - 使用"诊断问题"部分的指南
   - 记录出现的问题日志

---

## 📚 相关文档

- [DAILY_LIMIT_DEBUG_GUIDE.md](DAILY_LIMIT_DEBUG_GUIDE.md) - 完整的调试指南
- [DAILY_LIMIT_LOG_REFERENCE.md](DAILY_LIMIT_LOG_REFERENCE.md) - 日志快速参考
- [COOLDOWN_FIX_SUMMARY.md](COOLDOWN_FIX_SUMMARY.md) - 冷却时间修复总结

---

**修复完成日期**: 2026-04-01  
**日志系统就绪**: ✅  
**诊断能力**: 完整  
