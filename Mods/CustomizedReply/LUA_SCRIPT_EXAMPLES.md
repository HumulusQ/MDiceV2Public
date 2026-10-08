# CustomizedReply Mod - Lua脚本使用指南与示例

## 概述

CustomizedReply Mod现在支持**Lua脚本编程**，允许用户创建复杂的自定义回复逻辑。脚本可以访问消息上下文信息，并通过**Mod全局存储**来维护持久数据。

---

## 示例脚本详解

### 脚本 1：喂食惠惠 (Feed Aqua)

**功能说明：**
- 维护一个全局魔力值池
- 每次触发时随机增长 2D6 (2-12) 点魔力值
- 所有用户共享同一魔力池

**存储数据结构：**
- **Key:** `mana_ex` (全局，所有用户共享)
- **Value:** 当前魔力值（字符串格式的整数）

**工作流程：**
```
消息 "喂食惠惠" → 
  读取 mana_ex (当前值) →
  掷 2D6 骰子 →
  计算新魔力 = 当前 + 骰子结果 →
  保存 mana_ex →
  返回反馈信息
```

**关键代码片段：**
```lua
-- 获取当前魔力值
local current_mana = tonumber(mod_storage_read("mana_ex") or "0")

-- 掷两个6面骰子
local dice1 = math.random(1, 6)  -- 结果范围 1-6
local dice2 = math.random(1, 6)  -- 结果范围 1-6
local gain = dice1 + dice2        -- 增加的魔力 2-12

-- 计算新值并保存
local new_mana = current_mana + gain
mod_storage_write("mana_ex", tostring(new_mana))
```

**示例输出：**
```
✨ 喂了惠惠一口食物！
🎲 掷骰结果: 3 + 5 = +8 魔力
📊 当前魔力值: 42
⚡ 小贴士: 每有5点魔力就可以进行一次爆炸！
```

---

### 脚本 2：爆炸技能 (Explosion Skill)

**功能说明：**
- 消耗魔力值进行伤害掷骰计算
- 每5点魔力值可投掷1个D10
- 记录每个用户的最佳伤害值
- 对比历史记录，显示是否打破纪录

**存储数据结构：**
- **Key:** `mana_ex` (全局)
  - **Value：** 当前魔力值（清零后为0）
  
- **Key:** `bestrecord_[user_id]` (用户级)
  - **Value：** 该用户的最佳伤害记录（例：bestrecord_12345）

**工作流程：**
```
消息 "爆炸" →
  读取 mana_ex →
  计算 D10数量 = floor(魔力 / 5) →
  检查是否 >= 5 (不足则返回失败) →
  清零 mana_ex →
  掷 N 个 D10，计算总伤害 →
  读取用户历史最佳记录 →
  更新记录（如果超越） →
  返回详细反馈
```

**关键代码片段：**
```lua
-- 计算投掷数
local current_mana = tonumber(mod_storage_read("mana_ex") or "0")
local dice_count = math.floor(current_mana / 5)

-- 验证魔力
if dice_count == 0 then
    return "❌ 魔力不足！需要至少 5 点"
end

-- 清零魔力
mod_storage_write("mana_ex", "0")

-- 投掷N个D10
local total = 0
for i = 1, dice_count do
    total = total + math.random(1, 10)
end

-- 更新最佳记录
local record_key = "bestrecord_" .. user_id
local old_best = tonumber(mod_storage_read(record_key) or "0")
if total > old_best then
    mod_storage_write(record_key, tostring(total))
end
```

**示例输出 1（新纪录）：**
```
💥 EXPLOSION! 爆炸！
🎲 掷骰结果 (4个D10): 10, 7, 9, 8
⚡ 总伤害: 34
🏆 新纪录达成！(击败了之前的 28 点纪录)
👤 用户 张三 的成就: 34 伤害值
```

**示例输出 2（未突破纪录）：**
```
💥 EXPLOSION! 爆炸！
🎲 掷骰结果 (2个D10): 7, 5
⚡ 总伤害: 12
📊 最佳纪录: 34 点
💪 来吧，突破 35 点的纪录吧！
```

---

## 脚本API参考

### 全局变量

脚本执行时自动注入：

```lua
user_id       -- 数字：发送消息的用户QQ号
user_name     -- 字符串：用户昵称
group_id      -- 数字：群号（私聊时为0）
group_name    -- 字符串：群名称
message       -- 字符串：原始消息内容
default_reply -- 字符串：规则的默认回复
timestamp     -- 数字：消息时间戳（毫秒）
is_ated       -- 布尔值：是否被@了
```

### 全局函数

#### 读取存储值

```lua
local value = mod_storage_read(key: string): string
```

- 从Mod全局存储中读取值
- 参数必须是字符串key名
- 返回字符串，不存在时返回空字符串

```lua
local mana = tonumber(mod_storage_read("mana_ex") or "0")
```

#### 写入存储值

```lua
mod_storage_write(key: string, value: string): void
```

- 向Mod全局存储写入值
- 两个参数都必须是字符串
- 数字需要用 `tostring()` 转换

```lua
mod_storage_write("mana_ex", tostring(current_mana))
```

---

## 常用编程模式

### 模式 1：简单计数器

```lua
function MainProcess()
    local key = "visit_count"
    local count = (tonumber(mod_storage_read(key) or "0") + 1)
    mod_storage_write(key, tostring(count))
    return "你是第 " .. count .. " 个访问者"
end
```

### 模式 2：用户级数据

```lua
function MainProcess()
    local level_key = "level_" .. user_id  -- 使用user_id创建用户级key
    local level = tonumber(mod_storage_read(level_key) or "1")
    
    level = level + 1
    mod_storage_write(level_key, tostring(level))
    
    return user_name .. " 升到 " .. level .. " 级了！"
end
```

### 模式 3：条件判断

```lua
function MainProcess()
    if message == "帮助" then
        return "这是帮助信息"
    elseif string.find(message, "特定词") then
        return "检测到特定词汇"
    else
        return default_reply  -- 使用默认回复
    end
end
```

### 模式 4：数类型转换（重要）

```lua
function MainProcess()
    -- 从存储读出的总是字符串
    local str_val = mod_storage_read("number_key")
    
    -- 需要转换为数字才能进行计算
    local num_val = tonumber(str_val) or 0
    
    -- 计算后要转回字符串才能保存
    num_val = num_val + 10
    mod_storage_write("number_key", tostring(num_val))
    
    return "新值: " .. num_val
end
```

---

## 数据存储设计

### 命名规范建议

为避免key冲突，建议使用前缀区分：

```
counter_xxx      -- 全局计数器（如 counter_total_calls）
state_xxx        -- 全局状态（如 state_game_running）
level_[user_id]  -- 用户等级（如 level_12345）
score_[user_id]  -- 用户积分（如 score_67890）
best_[user_id]   -- 用户最佳（如 best_damage_999）
```

### 存储容量

- 每个Key-Value对都持久化到磁盘
- 存储大小理论上无限制（取决于硬盘空间）
- 程序关闭时自动保存
- 程序启动时自动加载

---

## 调试技巧

### 技巧 1：通过返回值传递调试信息

```lua
function MainProcess()
    local val = mod_storage_read("test")
    -- 返回值会显示在群里，用于调试
    return "DEBUG: 读取到 [" .. (val or "nil") .. "]"
end
```

### 技巧 2：检查数据完整性

```lua
function MainProcess()
    local val = mod_storage_read("key")
    
    if val == "" then
        return "⚠️ Key不存在或值为空"
    end
    
    local num = tonumber(val)
    if not num then
        return "⚠️ 值 [" .. val .. "] 不能转换为数字"
    end
    
    return "✅ 值正确: " .. num
end
```

### 技巧 3：异常捕获

```lua
function MainProcess()
    local ok, result = pcall(function()
        -- 可能出错的代码
        local val = tonumber(mod_storage_read("key"))
        return val * 2
    end)
    
    if ok then
        return "成功: " .. result
    else
        return "错误: " .. tostring(result)
    end
end
```

---

## 常见陷阱

### 陷阱 1：忘记类型转换

```lua
-- ❌ 错误
local val = mod_storage_read("counter")  -- 返回字符串 "23"
local result = val + 10  -- 错误！字符串不能相加

-- ✅ 正确
local val = tonumber(mod_storage_read("counter"))
local result = val + 10  -- 现在可以正确计算
```

### 陷阱 2：数据格式混乱

```lua
-- ❌ 容易出错的做法
mod_storage_write("numbers", "1,2,3,4")    -- 手动拼接
local values = string.split(...)  -- 需要额外处理

-- ✅ 更好的做法（如有JSON库）
local json = require("json")
mod_storage_write("numbers", json.encode({1,2,3,4}))
```

### 陷阱 3：并发问题

```lua
-- ⚠️ 不安全（如果并发执行）
local val = tonumber(mod_storage_read("counter"))
val = val + 1
mod_storage_write("counter", tostring(val))

-- ✅ 虽然不完美，但Mod框架已处理大部分并发问题
```

---

## 实际应用场景

### 场景 1：签到系统

```lua
function MainProcess()
    local key = "checkin_" .. user_id .. "_" .. os.date("%Y%m%d")
    
    if mod_storage_read(key) ~= "" then
        return user_name .. " 今天已经签到过了～"
    end
    
    mod_storage_write(key, "done")
    
    -- 统计签到天数
    local count_key = "checkin_count_" .. user_id
    local count = tonumber(mod_storage_read(count_key) or "0") + 1
    mod_storage_write(count_key, tostring(count))
    
    return "✅ 签到成功！累计签到 " .. count .. " 天"
end
```

### 场景 2：互动游戏进度

```lua
function MainProcess()
    local progress_key = "game_progress_" .. user_id
    local progress = tonumber(mod_storage_read(progress_key) or "0")
    
    if message == "开始游戏" then
        if progress == 0 then
            mod_storage_write(progress_key, "1")
            return "游戏开始，请输入答案"
        else
            return "游戏已进行中..."
        end
    elseif message == "答案123" then
        if progress == 1 then
            progress = progress + 1
            mod_storage_write(progress_key, tostring(progress))
            return "答案正确！继续...(进度: " .. progress .. ")"
        else
            return "游戏未开始或已完成"
        end
    end
    
    return "输入 '开始游戏' 来玩"
end
```

---

## 已知限制

1. **Lua库支持有限**
   - 标准库大部分可用（math, string, table 等）
   - 需要JSON库，当前版本不包含（可自行扩展）

2. **性能考虑**
   - 读操作很快（内存缓存）
   - 写操作会触发磁盘I/O，避免过度使用
   - 避免在循环中频繁读写

3. **执行超时**
   - 脚本应该快速执行（毫秒级）
   - 避免无限循环或递归

---

## 下一步

查看 [data.json](./data.json) 中的两个完整示例：
- **喂食惠惠**: 展示全局数据管理
- **爆炸技能**: 展示用户级数据+逻辑组合

根据这些例子，你可以创建自己的脚本！
