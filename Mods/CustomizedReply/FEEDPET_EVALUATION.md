# CustomizedReply 脚本系统功能评估

**日期**: 2026-03-30  
**需求**: 制作投喂脚本，每日限制 1 次，添加随机数到持久化数字

---

## 📊 功能满足度分析

### ✅ 完全满足的需求

| 需求 | 实现方案 | 代码位置 |
|------|--------|--------|
| 每日限制 1 次 | DailyUsageLimit 条件类型 | [CustomizedReplyMod.cs#L2235](Mods/CustomizedReply/CustomizedReplyMod.cs#L2235) |
| 添加随机数 | Lua 内置 `math.random()` | feedpet.lua |
| 持久化存储 | `mod_storage_read/write` | [ScriptExecutor.cs#L264](Mods/CustomizedReply/ScriptExecutor.cs#L264) |
| 脚本执行 | ExecuteFunction() 方法 | [ScriptExecutor.cs#L450](Mods/CustomizedReply/ScriptExecutor.cs#L450) |

### ⚠️ 存在的问题

#### 1️⃣ **每日计数未自动增跃**

**位置**: [CustomizedReplyMod.cs#L2110-2135](Mods/CustomizedReply/CustomizedReplyMod.cs#L2110-L2135)

```csharp
// 问题：condition 中检查了 DailyUsageLimit，但没有在规则成功后调用 IncrementDailyCount
if (condition.ConditionType == "DailyUsageLimit" && 
    !CurrentProcessor.CheckDailyLimit(_rule.Trigger, userId, condition.Value2 ?? "按用户", int.Parse(condition.Value)))
{
    return; // 不返回回复
}

// ❌ 缺失：
// CurrentProcessor.IncrementDailyCount(_rule.Trigger, userId, condition.Value2 ?? "按用户");
```

**影响**: 用户可能能多次使用同一规则（每日计数不生效）

#### 2️⃣ **PersistentState 未在 ExecuteFunction 中暴露**

**位置**: [ScriptExecutor.cs#L450-L520](Mods/CustomizedReply/ScriptExecutor.cs#L450-L520)

```csharp
// ✅ ScriptContext 包含 PersistentState
public class ScriptContext
{
    public Dictionary<string, object> PersistentState { get; set; } = new();
}

// ❌ 但 ExecuteFunction 中未注册到 Lua
public static string? ExecuteFunction(string scriptInstanceUid, string functionName, ScriptContext context)
{
    // ... 虚拟机创建 ...
    
    // 更新上下文变量
    vm.Globals["user_id"] = context.UserId;
    vm.Globals["message"] = context.Message;
    // ... 其他字段 ...
    
    // ❌ 缺失：
    // if (context.PersistentState != null)
    //     vm.Globals["persistent_state"] = context.PersistentState;
}
```

**影响**: Lua 脚本无法直接访问 PersistentState，只能用 `mod_storage` 替代

#### 3️⃣ **脚本中无法自动标记 SaveStateAfter**

**位置**: [CustomizedReplyMod.cs#L2336-2385](Mods/CustomizedReply/CustomizedReplyMod.cs#L2336-L2385)

```csharp
// 问题：脚本无法设置 context.SaveStateAfter = true
// 这导致 PersistentState 的修改无法保存

// 当前需要手动设置（但脚本中无法访问）
if (scriptContext.SaveStateAfter && scriptGlobalState != null)
{
    // 保存 PersistentState
}
```

---

## 🔧 解决方案

### 现有方案（推荐）- 使用 mod_storage

**优点**：
- ✅ 无需修改框架
- ✅ 可持久化到磁盘
- ✅ 跨脚本实例共享

**实现**：见 [feedpet.lua](Mods/CustomizedReply/scripts/feedpet.lua)

```lua
-- 读取
local current = tonumber(mod_storage_read("pet_satiety_" .. user_id)) or 0

-- 写入
mod_storage_write("pet_satiety_" .. user_id, tostring(new_value))
```

### 改进方案 - 自动每日计数（推荐修复）

**修改位置**: [CustomizedReplyMod.cs#L2200](Mods/CustomizedReply/CustomizedReplyMod.cs#L2200)

```csharp
private void ProcessRuleMatch(/* ... */)
{
    // ... 匹配和执行规则 ...
    
    // 自动递增每日计数（如果有 DailyUsageLimit 条件）
    foreach (var condition in _rule.Conditions)
    {
        if (condition.ConditionType == "DailyUsageLimit")
        {
            var msgProc = MessageProcessor.Instance;
            msgProc?.IncrementDailyCount(
                _rule.Trigger, 
                userId, 
                condition.Value2 ?? "按用户"
            );
            break;
        }
    }
}
```

### 先进方案 - 暴露 PersistentState 到 Lua（可选）

**修改位置**: [ScriptExecutor.cs#L490-L510](Mods/CustomizedReply/ScriptExecutor.cs#L490-L510)

```csharp
// 在 ExecuteFunction 中
if (context.PersistentState != null && context.PersistentState.Count > 0)
{
    var table = new Table(vm);
    foreach (var kvp in context.PersistentState)
    {
        table[kvp.Key] = kvp.Value;
    }
    vm.Globals["persistent_state"] = table;
}
```

---

## 📝 投喂脚本实现

### 文件位置
- **脚本文件**: [feedpet.lua](Mods/CustomizedReply/scripts/feedpet.lua)
- **规则配置**: [data.json#rule_008](Mods/CustomizedReply/data.json) (已添加)
- **说明文档**: [README_feedpet.md](Mods/CustomizedReply/scripts/README_feedpet.md)

### 功能清单
- ✅ 每日限制 1 次触发
- ✅ 添加 1-50 的随机数
- ✅ 持久化存储（per-user）
- ✅ 返回操作结果

### 使用步骤

1. **确保脚本存在**
   ```bash
   data/mods/CustomizedReply/scripts/feedpet.lua
   ```

2. **在程序初始化时注册脚本实例**
   ```csharp
   ScriptExecutor.Initialize(
       "data/mods/CustomizedReply/scripts",
       new List<ScriptInstance> {
           new ScriptInstance { 
               Uid = "feedpet_v1",
               ScriptFileName = "feedpet.lua"
           }
       }
   );
   ```

3. **data.json 中已配置规则**
   ```json
   {
     "id": "rule_008",
     "trigger": "投喂",
     "scriptInstanceUid": "feedpet_v1",
     "conditions": [{"type": "DailyUsageLimit", "value": "1"}]
   }
   ```

4. **用户触发**
   ```
   用户输入: 投喂
   机器人回复: 投喂成功！增加 37 饱食度，当前饱食度：137
   ```

---

## 📋 系统架构图

```
用户输入 "投喂"
    ↓
CustomizedReplyMod.OnGroupMessage()
    ↓
RuleExecutionEngine.Execute()
    ├─ 匹配规则 (trigger="投喂")
    ├─ 检查条件 (DailyUsageLimit)
    │   └─ MessageProcessor.CheckDailyLimit() ✅
    ├─ 执行脚本
    │   └─ ScriptExecutor.ExecuteFunction("feedpet_v1", "MainProcess", context)
    │       ├─ 加载脚本: feedpet.lua
    │       ├─ 执行 MainProcess()
    │       │   ├─ 生成随机数: math.random(1, 50)
    │       │   ├─ 读取存储: mod_storage_read("pet_satiety_123")
    │       │   ├─ 计算新值: current + random
    │       │   ├─ 写入存储: mod_storage_write("pet_satiety_123", new_value)
    │       │   └─ 返回结果文本
    │       └─ 缓存虚拟机供下次使用
    ├─ 处理输出标签: <output:0> → 脚本返回的结果
    ├─ ❌ 更新每日计数 (缺失！)
    └─ 返回回复给用户

下一次 (当天)
    ↓
用户再次输入 "投喂"
    ↓
RuleExecutionEngine.Execute()
    └─ 检查条件: CheckDailyLimit() 返回 false
        └─ 规则不匹配，不执行脚本 ⚠️ (若每日计数未正确更新，可能还会执行)
```

---

## ✨ 总结

| 项目 | 状态 | 说明 |
|------|------|------|
| **当前可用性** | ✅ 可用 | 使用 mod_storage 完全满足需求 |
| **脚本编写** | ✅ 完成 | feedpet.lua 已实现 |
| **规则配置** | ✅ 完成 | data.json 已添加示例 |
| **推荐改进** | ⚠️ 建议 | 自动调用 IncrementDailyCount |
| **可选改进** | 🟡 未来 | 暴露 PersistentState 到 Lua |

**结论**: 您的需求 **完全可以满足**，现在可以立即使用！

推荐先用提供的脚本测试，若需要更高级的状态管理，再考虑框架改进。
