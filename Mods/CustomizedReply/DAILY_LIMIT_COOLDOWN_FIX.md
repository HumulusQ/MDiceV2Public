# CustomizedReply 每日限制和冷却时间修复报告

**修复日期**: 2026-04-01  
**问题**: 每日次数限制和冷却时间的作用域处理不正确

---

## 📋 修复内容

### ✅ 已修复的问题

#### 1. 冷却时间作用域硬编码问题
**问题**: 冷却时间条件的作用域（"按用户" 或 "全局"）被硬编码，无法灵活配置

**修复方案**:
- 更新 `Value2` 格式从 `"秒"` 改为 `"秒_按用户"` 或 `"秒_全局"`
- 解析格式: `"{单位}_{作用域}"`
  - 单位: 秒、分钟、小时
  - 作用域: 按用户、全局

**修改位置**:
- [CustomizedReplyMod.cs#CheckCooldown](CustomizedReplyMod.cs) - 检查冷却时间时正确解析作用域
- [CustomizedReplyMod.cs#UpdateTrackingMetrics](CustomizedReplyMod.cs) - 更新冷却时间戳时正确使用作用域

**代码示例**:
```csharp
// 原来（硬编码）
return msgProc.CheckCooldown(ruleId, userId, "按用户", durationSeconds);

// 修复后（从配置读取）
var parts = unitAndScope.Split('_', StringSplitOptions.RemoveEmptyEntries);
if (parts.Length >= 2)
{
    unit = parts[0];
    scope = parts[1];  // 现在正确读取作用域
}
return msgProc.CheckCooldown(ruleId, userId, scope, durationSeconds);
```

#### 2. 每日次数限制验证
**状态**: ✅ 已确认正常

**检查结果**:
- `UpdateTrackingMetrics()` 方法已存在（第2520行）
- `IncrementDailyCount()` 在规则执行成功后被正确调用
- 每日限制的作用域正确从 `condition.Value2` 读取

```csharp
msgProc.IncrementDailyCount(_rule.Trigger, userId, condition.Value2 ?? "按用户");
```

---

## 📝 配置格式说明

### 每日次数限制 (DailyUsageLimit)
```json
{
  "type": "DailyUsageLimit",
  "value": "10",              // 每日限额数
  "value2": "按用户",          // 作用域（按用户 或 全局）
  "isInverted": false
}
```

### 冷却时间 (TimeCooldown) - **新格式**
```json
{
  "type": "TimeCooldown",
  "value": "30",              // 冷却时长
  "value2": "秒_按用户",       // 格式: "单位_作用域"
  "isInverted": false
}
```

**支持的格式**:
- `"秒_按用户"` - 每个用户单独冷却
- `"秒_全局"` - 全体用户共用冷却
- `"分钟_按用户"` - 分钟级别，按用户
- `"分钟_全局"` - 分钟级别，全局
- `"小时_按用户"` - 小时级别，按用户
- `"小时_全局"` - 小时级别，全局

---

## 🧪 测试示例

### 测试 1: 个人冷却时间
**配置** (data.json 中 rule_009):
```json
{
  "id": "rule_009",
  "trigger": "签到",
  "conditions": [
    {
      "type": "TimeCooldown",
      "value": "30",
      "value2": "秒_按用户"
    }
  ]
}
```

**测试步骤**:
1. 用户A输入 "签到"
   - ✅ 成功执行，返回回复
   - 记录用户A的冷却时间戳
2. 用户A在30秒内再次输入 "签到"
   - ❌ 被冷却阻止，不返回回复
3. 用户B在同一时间输入 "签到"
   - ✅ 成功执行（独立的冷却计时）

**预期结果**: ✅ 每个用户有独立的 30 秒冷却时间

---

### 测试 2: 全局冷却时间
**配置** (data.json 中 rule_010):
```json
{
  "id": "rule_010",
  "trigger": "抽奖",
  "conditions": [
    {
      "type": "TimeCooldown",
      "value": "5",
      "value2": "分钟_全局"
    }
  ]
}
```

**测试步骤**:
1. 用户A输入 "抽奖"
   - ✅ 成功执行，返回回复
   - 记录全局冷却时间戳
2. 用户B立即输入 "抽奖"
   - ❌ 被全局冷却阻止，不返回回复
3. 5分钟后，任何用户输入 "抽奖"
   - ✅ 冷却解除，正常执行

**预期结果**: ✅ 所有用户共用一个 5 分钟的冷却时间

---

### 测试 3: 混合条件（冷却 + 每日限制）
```json
{
  "id": "rule_mix",
  "trigger": "打卡",
  "conditions": [
    {
      "type": "TimeCooldown",
      "value": "5",
      "value2": "分钟_按用户"
    },
    {
      "type": "DailyUsageLimit",
      "value": "10",
      "value2": "按用户"
    }
  ]
}
```

**行为说明**:
- 冷却时间: 每个用户5分钟只能触发1次
- 每日限制: 每个用户每天最多触发10次
- 两个条件都要满足才能执行

---

## 📊 修复验证清单

| 项目 | 修复前 | 修复后 | 验证 |
|------|-------|-------|------|
| 冷却时间作用域 | ❌ 硬编码为"按用户" | ✅ 从配置读取 | ✓ |
| 冷却时间更新 | ❌ 硬编码为"按用户" | ✅ 从配置读取 | ✓ |
| 每日限制 | ✅ 正常工作 | ✅ 正常工作 | ✓ |
| 数据格式 | - | ✅ 增加示例 | ✓ |

---

## 🚀 后续使用建议

1. **更新现有规则**: 如果有使用 TimeCooldown 的规则，需要更新 `value2` 为新格式 `"单位_作用域"`

2. **向后兼容**: 代码中包含向后兼容逻辑
   - 如果 `value2` 只包含单位（如 `"秒"`），自动使用 `"按用户"` 作为默认作用域
   - 建议逐步更新为新格式

3. **UI 更新**: 建议在管理界面中添加选择器用于选择作用域

4. **日志记录**: 冷却时间更新和检查时都会记录日志，便于调试
   ```
   [CustomizedReply] Updated cooldown timestamp for rule: 抽奖 (scope: 全局)
   ```

---

## 📍 相关代码位置

| 文件 | 行号 | 说明 |
|------|------|------|
| CustomizedReplyMod.cs | 2628 | `CheckCooldown()` 方法 |
| CustomizedReplyMod.cs | 2545 | `UpdateTrackingMetrics()` 方法中的冷却时间处理 |
| CustomizedReplyMod.cs | 2538 | `UpdateTrackingMetrics()` 方法中的每日限制处理 |
| data.json | 188 | rule_009 示例（个人冷却） |
| data.json | 204 | rule_010 示例（全局冷却） |

---

## ✨ 总结

✅ **修复完成**: 冷却时间的作用域现在支持灵活配置，可以设置为按用户或全局  
✅ **向后兼容**: 现有配置仍能正常工作，未来可逐步更新为新格式  
✅ **每日限制**: 验证无问题，已正确实现每日次数限制功能  
✅ **示例补充**: 添加了 rule_009 和 rule_010 作为参考示例
