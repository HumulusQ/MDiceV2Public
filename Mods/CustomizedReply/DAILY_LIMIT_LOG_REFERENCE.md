# 每日次数限制日志快速查询

**用途**: 快速查找和理解每日次数限制相关的日志

---

## 🔎 日志快速搜索

### 搜索关键词

**查找规则匹配**:
```
[CustomizedReply] ✓ Trigger matched for rule
```

**查找条件检查**:
```
[CustomizedReply.CheckAllConditions] 开始检查
```

**查找每日限制检查**:
```
[CheckDailyLimit] 检查开始
```

**查找计数递增**:
```
[IncrementDailyCount] 递增开始
```

**查找条件失败原因**:
```
[CheckDailyLimit] 次数比较
```

---

## 📊 日志流程速查表

### 正常流程（规则应该执行）
```
✓ [CustomizedReply] Trigger matched
✓ [CheckDailyLimit] 次数比较: X < Y = true
✓ [CheckDailyLimit] ✓ 返回 true
✓ [CustomizedReply] Selected reply
✓ [IncrementDailyCount] ✓ 增加后的计数: X
```

### 被阻止流程（规则应该被跳过）
```
✓ [CustomizedReply] Trigger matched
✓ [CheckDailyLimit] 次数比较: X < Y = false
✓ [CheckDailyLimit] ✗ 返回 false
✗ [CustomizedReply] ✗ Conditions not met
✗ No reply sent
```

### 错误流程（有问题）
```
? [CustomizedReply] Trigger matched
? No CheckDailyLimit logs found
? [CustomizedReply] Selected reply (不应该执行!)
✗ [IncrementDailyCount] NOT executed (计数未增加!)
```

---

## 🐛 常见问题速查

### 问题: 日志中没有 CheckDailyLimit

**可能原因**:
- Rule 配置中没有 DailyUsageLimit 条件
- 条件类型拼写错误（大小写）
- Value 参数为空

**检查**:
```
[CustomizedReply.CheckAllConditions] 开始检查 N 个条件
```
如果 N = 0，说明没有配置条件

---

### 问题: CheckDailyLimit 显示 true，但规则被重复执行

**可能原因**:
- IncrementDailyCount 没有被执行
- 关键日志步骤缺失

**检查清单**:
- [ ] `[SetDailyLimit] ✓ 返回 true` 或 `✗ 返回 false`
- [ ] `[IncrementDailyCount] 递增开始` (规则执行后)
- [ ] `[IncrementDailyCount] 计数递增: X -> Y`

---

### 问题: 次数限制没有按每日重置

**症状**: 第二天规则仍然被阻止

**检查**:
```
[CheckDailyLimit] ⚠️ 日期已更改 (从 YYYY-MM-DD 到 YYYY-MM-DD)，计数重置为 0
```

如果没有这行日志，说明：
- 没有第二天的请求
- 日期判断有问题
- 系统时间不对

---

## 📋 关键数值查询速查表

### 查找次数比较

搜索:
```
[CheckDailyLimit] 次数比较: X < Y = Z
```

含义:
- X = 当前计数
- Y = 限额
- Z = 是否通过 (true/false)

例子:
```
次数比较: 1 < 1 = false  → 已达限额，被阻止
次数比较: 0 < 1 = true   → 未超限，可执行
```

### 查找生成的 key

搜索:
```
[CheckDailyLimit] 生成的 key: XXX
```

格式解析:
```
投喂_12345678     → 按用户模式（用户 12345678）
投喂_*           → 全局模式（所有用户）
```

### 查找当前日期

搜索:
```
[CheckDailyLimit] 今日日期: YYYY-MM-DD
```

作用: 用于验证跨天是否正确重置

---

## ✅ 全功能验证检查列表

要验证每日次数限制功能已正确修复，需要看到这些日志：

### ✓ 必要的日志行

- [ ] `[CustomizedReply.CheckDailyLimit] 参数解析成功`
- [ ] `[CheckDailyLimit] 检查开始`
- [ ] `[CheckDailyLimit] 生成的 key:`
- [ ] `[CheckDailyLimit] 今日日期:`
- [ ] `[CheckDailyLimit] 次数比较: X < Y =`
- [ ] 第一次: `[CheckDailyLimit] 未找到历史记录`
- [ ] 第一次: `[CheckDailyLimit] ✓ 返回 true`
- [ ] 第二次: `[CheckDailyLimit] 找到历史记录`
- [ ] 第二次: `[CheckDailyLimit] ✗ 返回 false`
- [ ] `[IncrementDailyCount] 递增开始`
- [ ] `[IncrementDailyCount] 计数递增: X -> Y`
- [ ] `[IncrementDailyCount] ✓ 增加后的计数:`

### ✗ 不应该看到的日志

- [ ] `参数无效`（应该参数正常）
- [ ] `MessageProcessor.Instance 为 null`（应该正常初始化）
- [ ] 第一次触发时没有 IncrementDailyCount（应该被调用）
- [ ] 第二次触发时 CheckDailyLimit 返回 true（应该返回 false）

---

## 🎯 最小化测试验证

### 测试 1: 规则首次执行

1. **发送消息**: 用户输入触发词
2. **预期日志**:
   ```
   [CustomizedReply] ✓ Trigger matched
   [CheckDailyLimit] 未找到历史记录
   [CheckDailyLimit] ✓ 返回 true
   [IncrementDailyCount] 计数递增: 0 -> 1
   [CustomizedReply] Selected reply
   ```
3. **预期结果**: ✅ 收到回复

### 测试 2: 规则第二次执行（应被阻止）

1. **发送消息**: 用户再次输入触发词
2. **预期日志**:
   ```
   [CustomizedReply] ✓ Trigger matched
   [CheckDailyLimit] 找到历史记录
   [CheckDailyLimit] 当前计数: 1
   [CheckDailyLimit] 次数比较: 1 < 1 = false
   [CheckDailyLimit] ✗ 返回 false
   [CustomizedReply] ✗ Conditions not met
   ```
3. **预期结果**: ❌ 没有回复（正常）

---

## 💡 调试技巧

### 技巧 1: 按规则名搜索

搜索特定规则的所有日志:
```
[CustomizedReply] ... 投喂 ...
[CheckDailyLimit] ... 投喂 ...
[IncrementDailyCount] ... 投喂 ...
```

### 技巧 2: 按用户 ID 搜索

追踪特定用户:
```
用户: 12345678
```

### 技巧 3: 时间戳分析

查看日志时间戳，确认顺序:
```
10:05:30 [CustomizedReply] Selected reply
10:05:31 [IncrementDailyCount] 递增开始
10:05:32 [IncrementDailyCount] ✓ 增加后的计数: 1
```

---

## 📞 提供反馈时包含的信息

如果日志显示问题，请提供：

1. **完整的日志链**（从触发词匹配到最后）
2. **关键数值**:
   - 当前计数
   - 限额
   - 作用域
   - key 值
3. **规则配置**:
   ```json
   "conditions": [{
     "type": "DailyUsageLimit",
     "value": "?",
     "value2": "?"
   }]
   ```

示例:
```
规则名: 投喂
用户ID: 12345678
第几次: 第 1 次
预期: ✓ 执行
实际: ✓/✗
日志:
[CheckDailyLimit] 次数比较: 0 < 1 = true
```

---

**修复日期**: 2026-04-01  
**文档版本**: v1.0
