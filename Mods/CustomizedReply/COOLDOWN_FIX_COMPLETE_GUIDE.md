# CustomizedReply 冷却时间模式完整修复指南

**最后更新**: 2026-04-01  
**状态**: ✅ 修复完成 / 🧪 测试中

---

## 📋 问题诊断

### 冷却时间工作流程

```
消息到达
    ↓
触发词匹配 ✓
    ↓
检查冷却条件:
  ├─ 按用户模式 (key: "rule_id_{userId}")
  │   └─ 每个用户有独立的冷却时间
  └─ 全局模式 (key: "rule_id_*")
      └─ 所有用户共享同一个冷却时间
    ↓
冷却条件通过 → ✓ 继续执行
冷却条件失败 → ✗ 规则不匹配，返回 null
    ↓
规则执行 (脚本/回复)
    ↓
更新冷却时间戳
```

---

## 🔧 代码修复清单

### ✅ 已完成的修复

#### 1. **CheckCooldown 方法** [CustomizedReplyMod.cs#2651-2689]
```csharp
// ✅ 现在正确解析 "秒_按用户" 或 "秒_全局" 格式
string unit = "秒";
string scope = "按用户";

if (!string.IsNullOrEmpty(unitAndScope))
{
    var parts = unitAndScope.Split('_', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length >= 2)
    {
        unit = parts[0];
        scope = parts[1];  // ✅ 正确读取作用域
    }
}
```

#### 2. **UpdateTrackingMetrics 方法** [CustomizedReplyMod.cs#2545-2580]
```csharp
// ✅ 冷却时间更新时正确传递作用域
msgProc.UpdateCooldownTimestamp(_rule.Trigger, userId, scope, durationSeconds);
_context?.Log(LogLevel.Info, $"[CustomizedReply] Updated cooldown timestamp for rule: {_rule.Trigger} (scope: {scope})");
```

#### 3. **MessageProcessor 方法** [MDiceV2.Core/Models/MessageProcessor.cs]
- `CheckCooldown()` - ✅ 支持作用域判断
- `UpdateCooldownTimestamp()` - ✅ 支持作用域存储
- `cooldownTracking` - ✅ 使用 key 模式区分作用域
  - 按用户: `"rule_id_{userId}"`
  - 全局: `"rule_id_*"`

---

## 🧪 测试流程

### 测试环境准备

1. **确保 data.json 已更新** (rule_009, rule_010)
   ```bash
   Mods/CustomizedReply/data.json ✓
   ```

2. **构建项目**
   ```bash
   dotnet build MDiceV2.sln -c Debug
   ```

3. **启动调试版本**
   ```bash
   运行 "debug-build-clean" 任务
   然后 "run-debug" 任务
   ```

---

### 测试 1: 按用户冷却模式

**规则配置** (rule_009):
```json
{
  "trigger": "签到",
  "conditions": [{
    "type": "TimeCooldown",
    "value": "30",
    "value2": "秒_按用户"
  }]
}
```

**测试步骤**:

| 步骤 | 操作 | 预期结果 | 实际结果 |
|------|------|--------|--------|
| 1 | 用户 A 输入 "签到" | ✅ 回复成功 | |
| 2 | 用户 A 在 5 秒内再次输入 "签到" | ❌ 无响应（冷却中） | |
| 3 | 用户 B 立即输入 "签到" | ✅ 回复成功 | |
| 4 | 等待 30 秒后，用户 A 再次输入 "签到" | ✅ 回复成功 | |
| 5 | 用户 B 在 5 秒内再次输入 "签到" | ❌ 无响应（冷却中） | |

**验证日志**:
```log
[CustomizedReply] Updated cooldown timestamp for rule: 签到 (scope: 按用户)
[CustomizedReply] ✗ Conditions not met for rule  (冷却禁止)
```

**通过条件**: ✅ 所有 5 个步骤的结果都符合预期

---

### 测试 2: 全局冷却模式

**规则配置** (rule_010):
```json
{
  "trigger": "抽奖",
  "conditions": [{
    "type": "TimeCooldown",
    "value": "5",
    "value2": "分钟_全局"
  }]
}
```

**测试步骤**:

| 步骤 | 操作 | 预期结果 | 实际结果 |
|------|------|--------|--------|
| 1 | 用户 A 输入 "抽奖" | ✅ 回复成功 | |
| 2 | 用户 B 立即输入 "抽奖"（< 1秒） | ❌ 无响应（全局冷却） | |
| 3 | 用户 C 输入 "抽奖"（< 1秒） | ❌ 无响应（全局冷却） | |
| 4 | 等待 5 分钟，任何用户输入 "抽奖" | ✅ 回复成功 | |
| 5 | 其他用户再次输入 "抽奖"（< 1秒） | ❌ 无响应（新的全局冷却开始） | |

**验证日志**:
```log
[CustomizedReply] Updated cooldown timestamp for rule: 抽奖 (scope: 全局)
key = "抽奖_*"  (全局冷却使用 * 作为 userId 占位符)
```

**通过条件**: ✅ 所有 5 个步骤的结果都符合预期

---

### 测试 3: 混合条件（冷却 + 每日限制）

**规则配置**:
```json
{
  "trigger": "打卡",
  "conditions": [
    {
      "type": "TimeCooldown",
      "value": "10",
      "value2": "秒_按用户"
    },
    {
      "type": "DailyUsageLimit",
      "value": "3",
      "value2": "按用户"
    }
  ]
}
```

**测试步骤**:

| 步骤 | 操作 | 预期结果 | 原因 |
|------|------|--------|------|
| 1 | 用户 A 输入 "打卡" (次数: 0) | ✅ 回复成功 (次数: 1) | 冷却✓，每日✓ |
| 2 | 5 秒内再次输入 (次数: 1) | ❌ 无响应 | 冷却中 |
| 3 | 10 秒后输入 (次数: 1) | ✅ 回复成功 (次数: 2) | 冷却✓，每日✓ |
| 4 | 10 秒后输入 (次数: 2) | ✅ 回复成功 (次数: 3) | 冷却✓，每日✓ |
| 5 | 10 秒后输入 (次数: 3) | ❌ 无响应 | 每日限制已达 3 次 |
| 6 | 第二天输入 (次数: 0) | ✅ 回复成功 (次数: 1) | 每日计数重置 |

**通过条件**: ✅ 冷却和每日限制都独立工作

---

## 🐛 故障排除

### 问题: 冷却时间不生效（用户能重复触发）

**可能原因**:

1. **配置格式错误**
   ```json
   // ❌ 错误
   "value2": "秒"
   
   // ❌ 错误
   "value2": "按用户"
   
   // ✅ 正确
   "value2": "秒_按用户"
   ```
   **检查**: data.json 中所有 TimeCooldown 条件的 value2 格式

2. **作用域解析失败**
   ```csharp
   // 调试日志检查
   [CustomizedReply] Updated cooldown timestamp for rule: {ruleId} (scope: ???)
   ```
   如果 scope 显示为空或错误，检查 value2 的分割逻辑

3. **MessageProcessor 未初始化**
   ```csharp
   var msgProc = MessageProcessor.Instance;
   if (msgProc == null)
       return true;  // ⚠️ 此时冷却被忽略
   ```
   **检查**: MessageProcessor.Instance 是否在应用启动时被正确创建

4. **冷却时间与当前时间不同步**
   - **问题**: `DateTime.UtcNow` vs `DateTime.Now`
   - **现状**: CheckCooldown 使用 UTC，需要检查一致性
   - **修复**: [查看下方](#时间同步问题修复)

---

### 问题: 全局冷却不工作（多用户仍能同时触发）

**可能原因**:

1. **作用域确实被识别为按用户**
   ```csharp
   // 调试输出
   string key = scope == "全局" ? $"{ruleId}_*" : $"{ruleId}_{userId}";
   _context?.Log(LogLevel.Info, $"[DEBUG] Cooldown key: {key}, scope: {scope}");
   ```
   **检查**: 日志中 key 是否包含 `_*` 后缀

2. **全局模式的 key 拼写错误**
   ```csharp
   // ❌ 错误
   string key = $"{ruleId}__";
   
   // ❌ 错误  
   string key = $"{ruleId}_global";
   
   // ✅ 正确
   string key = $"{ruleId}_*";
   ```

3. **冷却追踪字典在不同模块中不同步**
   - **确认**: `cooldownTracking` 是 `MessageProcessor` 的单例成员
   - **查证**: 所有调用都通过 `MessageProcessor.Instance`

---

### 问题: 每日限制在跨天时不重置

**可能原因**:

1. **时区问题** (已在 MessageProcessor 中修复)
   ```csharp
   // ✅ 修复后: 使用本地时间
   var today = DateOnly.FromDateTime(DateTime.Now);
   
   // ❌ 旧方式: 使用 UTC 时间
   var today = DateOnly.FromDateTime(DateTime.UtcNow);
   ```

2. **日期比较逻辑**
   ```csharp
   if (tracking.Date != today)
   {
       dailyLimitTracking[key] = (today, 0);
       return true; // ✅ 新的一天，重置
   }
   ```

---

## 🔍 时间同步问题修复

### 发现的问题
在 `CheckCooldown` 中使用 `DateTime.UtcNow`:
```csharp
var elapsed = DateTime.UtcNow - tracking.LastTrigger;
```

### 建议修复
统一为本地时间，确保与每日限制的时区一致：

```csharp
// MessageProcessor.cs - CheckCooldown 方法
public bool CheckCooldown(string ruleId, long userId, string scope, int cooldownSeconds)
{
    if (string.IsNullOrEmpty(ruleId) || cooldownSeconds <= 0)
        return true;

    string key = scope == "全局" ? $"{ruleId}_*" : $"{ruleId}_{userId}";

    if (cooldownTracking.TryGetValue(key, out var tracking))
    {
        // ✅ 使用 DateTime.Now (本地时间) 而非 UtcNow
        var elapsed = DateTime.Now - tracking.LastTrigger;
        return elapsed.TotalSeconds >= tracking.CooldownSeconds;
    }

    return true;
}

public void UpdateCooldownTimestamp(string ruleId, long userId, string scope, int cooldownSeconds)
{
    if (string.IsNullOrEmpty(ruleId) || cooldownSeconds <= 0)
        return;

    string key = scope == "全局" ? $"{ruleId}_*" : $"{ruleId}_{userId}";
    // ✅ 使用 DateTime.Now (本地时间)
    cooldownTracking[key] = (DateTime.Now, cooldownSeconds);
}
```

---

## 📝 调试日志解读

### 成功的冷却流程日志
```
[CustomizedReply] ✓ Trigger matched for rule #9
[CustomizedReply] ✓ All conditions passed for rule #9
[CustomizedReply] Selected reply: '签到成功！请 30 秒后再次签到～'
[CustomizedReply] Updated cooldown timestamp for rule: 签到 (scope: 按用户)
```

### 被冷却阻止的日志
```
[CustomizedReply] ✓ Trigger matched for rule #9
[CustomizedReply] ✗ Conditions not met for rule #9 (冷却未过期)
```

### 全局冷却日志
```
// 第一个用户
[CustomizedReply] Updated cooldown timestamp for rule: 抽奖 (scope: 全局)
// 其他用户（冷却中）
[CustomizedReply] ✗ Conditions not met for rule #10 (冷却未过期)
```

---

## ✨ 完整修复验证清单

| 项目 | 文件 | 行号 | 状态 |
|------|------|------|------|
| CheckCooldown 作用域解析 | CustomizedReplyMod.cs | 2651-2689 | ✅ |
| UpdateTrackingMetrics 作用域处理 | CustomizedReplyMod.cs | 2545-2580 | ✅ |
| MessageProcessor.CheckCooldown | MessageProcessor.cs | 1409 | ✅ |
| MessageProcessor.UpdateCooldownTimestamp | MessageProcessor.cs | 1432 | ✅ |
| 数据结构初始化 | MessageProcessor.cs | 205 | ✅ |
| 持久化加载 | MessageProcessor.cs | 1232 | ✅ |
| 持久化保存 | MessageProcessor.cs | 1268 | ✅ |
| data.json 示例规则 | data.json | 188-228 | ✅ |

---

## 🚀 最终测试建议

1. **单元测试** (代码层面)
   - 验证 key 拼接: `"rule_id_*"` vs `"rule_id_123"`
   - 验证时间计算：冷却是否正确过期

2. **集成测试** (功能层面)
   - 按用户模式: 3-5 个用户，多次触发，验证独立性
   - 全局模式: 多用户同时触发，验证共享性
   - 混合条件: 结合每日限制，验证两者配合

3. **边界测试**
   - 冷却时间为 0 秒 (应该被忽略)
   - 冷却时间极长 (1 小时)
   - 大量用户同时触发 (并发测试)

---

## 📮 反馈与改进

如果测试中发现问题，请检查:
1. ✓ data.json 的 value2 格式是否正确
2. ✓ 日志中的 scope 值是否符合预期
3. ✓ MessageProcessor.Instance 是否为 null
4. ✓ 时间戳是否跨越了午夜（每日重置）

---

**修复完成日期**: 2026-04-01  
**下一步**: 执行完整的测试流程，并收集反馈
