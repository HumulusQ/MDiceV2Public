## 投喂脚本使用说明

### 概述
投喂脚本实现了一个简单的宠物饱食度系统，每日限制使用一次。

### 功能特性
- ✅ 每日限制：每个用户每天最多使用 1 次
- ✅ 随机增长：每次增加 1-50 的随机饱食度
- ✅ 持久化存储：使用 `mod_storage_read/write` 保存数据
- ✅ 用户隔离：不同用户的饱食度独立存储

### 配置

#### 1. 脚本注册
在程序初始化时，需要注册脚本实例：
```csharp
ScriptExecutor.Initialize(
    scriptsDirectory: "data/mods/CustomizedReply/scripts",
    instances: new List<ScriptInstance> {
        new ScriptInstance { 
            Uid = "feedpet_v1",
            ScriptFileName = "feedpet.lua"
        }
    }
);
```

#### 2. 规则配置 (data.json)
```json
{
  "id": "rule_008",
  "description": "投喂宠物 - 每日限制 1 次",
  "trigger": "投喂",
  "matchType": "exact",
  "replies": ["<output:0>"],
  "scriptInstanceUid": "feedpet_v1",
  "scriptFilePath": "feedpet.lua",
  "isScriptEditMode": false,
  "scriptCalls": ["MainProcess"],
  "conditions": [
    {
      "type": "DailyUsageLimit",
      "value": "1",
      "value2": "按用户",
      "isInverted": false
    }
  ]
}
```

### 使用示例

**命令**：`投喂`

**响应**：
```
投喂成功！
增加 37 饱食度
当前饱食度：137
```

### 数据存储

每个用户的饱食度存储在 Mod 全局存储中：
- **键格式**: `pet_satiety_{user_id}`
- **值格式**: 数字字符串（如 "137"）
- **作用域**: 按用户存储（不同用户独立）

### 查询当前饱食度

若要查看用户当前的饱食度，可以创建一个查询脚本：
```lua
function MainProcess()
    local user_id = tostring(user_id)
    local store_key = "pet_satiety_" .. user_id
    local current = mod_storage_read(store_key) or "0"
    
    return "当前饱食度：" .. current
end
```

### 每日限制工作原理

1. **检查阶段** (CheckDailyLimit)
   - 系统在消息处理前检查用户今天是否已使用过此规则
   - 若未使用或已过午夜，则通过检查

2. **执行阶段**
   - 脚本运行，增加饱食度

3. **记录阶段** (IncrementDailyCount)
   - 系统记录用户在今天使用了此规则
   - 明天午夜后计数器自动重置

### 限制与注意

⚠️ **当前缺失的功能**：
- PersistentState 未暴露给 Lua（但可用 mod_storage 替代）
- 每日计数需在规则成功后手动调用 IncrementDailyCount

✅ **建议的改进**：
- 在 ExecuteScript 完成后自动调用 IncrementDailyCount
- 将 PersistentState 正式暴露到 Lua 环境

### 故障排除

**问题**：每天可以使用多次
- **原因**：可能每日计数未正确保存
- **解决**：检查 IncrementDailyCount 是否被调用

**问题**：饱食度重置了
- **原因**：存储键不一致或被覆盖
- **解决**：检查存储键是否正确

**问题**：显示 "[脚本错误]"
- **原因**：Lua 脚本执行异常
- **解决**：检查脚本语法和 mod_storage 函数调用
