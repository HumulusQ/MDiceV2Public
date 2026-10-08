# 🚀 快速参考指南

## 脚本编辑功能快速开始

### 1️⃣ 打开脚本编辑模式
- 在"回复内容"标题右侧点击 **[◉]** 按钮
- UI 自动切换到脚本编辑界面

### 2️⃣ 编写脚本 (直接输入)
```lua
function MainProcess()
    if string.find(message, "你好") then
        return "你好！"
    else
        return default_reply
    end
end
```

### 3️⃣ 或加载脚本文件
- 点击 **"从文件加载 Lua 脚本"** 按钮
- 选择 `.lua` 文件
- 内容自动加载

### 4️⃣ 保存并测试
- 点击 **"覆写"** 保存规则
- 发送消息触发规则即可测试

---

## 🎮 脚本全局变量速查表

| 变量名 | 类型 | 说明 | 示例 |
|--------|------|------|------|
| `user_id` | number | 用户QQ号 | `123456789` |
| `user_name` | string | 用户昵称 | `"小明"` |
| `group_id` | number | 群号 | `987654321` |
| `group_name` | string | 群名 | `"测试群"` |
| `message` | string | 消息内容 | `"你好"` |
| `default_reply` | string | 默认回复 | `"你好啊"` |
| `timestamp` | number | 时间戳 | `1707029082` |
| `is_ated` | boolean | 是否被@ | `true/false` |

---

## 💡 常用脚本片段

### 条件判断
```lua
if string.find(message, "你好") then
    return "你好！"
elseif string.find(message, "谢谢") then
    return "不客气"
else
    return default_reply
end
```

### 字符串操作
```lua
-- 转大写
local msg = string.upper(message)

-- 获取消息长度
local len = string.len(message)

-- 提取子串
local sub = string.sub(message, 1, 5)

-- 字符串拼接
return "你说的是: " .. message
```

### 随机选择
```lua
local replies = {"回复1", "回复2", "回复3"}
local idx = math.random(1, #replies)
return replies[idx]
```

### 基于用户的回复
```lua
if user_id == 123456789 then
    return "主人好！✨"
else
    return "你好，" .. user_name .. "！"
end
```

### 时间相关
```lua
local hour = tonumber(os.date("%H"))
if hour < 12 then
    return "早上好！☀️"
elseif hour < 18 then
    return "下午好！☀️"
else
    return "晚上好！🌙"
end
```

---

## 🎯 使用<output>标签

### 模板方式
**回复内容**: `"感谢 <output> 的消息！"`  
**脚本返回**: `"小明"`  
**最终回复**: `"感谢 小明 的消息！"`

### 不使用标签
**回复内容**: 空（或任意内容，会被忽略）  
**脚本返回**: `"这是完整的回复"`  
**最终回复**: `"这是完整的回复"`

---

## ⚠️ 常见错误及解决

| 错误 | 原因 | 解决方法 |
|------|------|--------|
| 脚本不执行 | 缺少MainProcess函数 | 确保有 `function MainProcess()` |
| 返回nil | 脚本没有return | 添加 `return "内容"` |
| 空回复 | 返回空字符串 | 确保返回非空字符串 |
| 变量undefined | 变量名错误 | 检查变量名是否正确（区分大小写） |

---

## 🔧 调试技巧

### 方法1：简化脚本
```lua
function MainProcess()
    return "脚本执行正常"
end
```

### 方法2：返回变量值
```lua
function MainProcess()
    return "消息: " .. message .. ", 用户: " .. user_name
end
```

### 方法3：条件测试
```lua
function MainProcess()
    if string.find(message, "测试") then
        return "找到测试关键词"
    else
        return "没有找到"
    end
end
```

---

## 📝 脚本模板

### 最小化模板
```lua
function MainProcess()
    return default_reply
end
```

### 完整模板
```lua
-- CustomizedReply Lua 脚本
-- 触发条件：[在UI中配置]

function MainProcess()
    -- TODO: 添加你的逻辑
    
    -- 可用变量：
    -- user_id, user_name
    -- group_id, group_name  
    -- message, default_reply
    -- timestamp, is_ated
    
    return default_reply
end
```

---

## 🚫 禁止和限制

❌ **不支持的操作**
- 文件系统访问（出于安全考虑）
- 网络请求（脚本为离线执行）
- 系统命令执行
- 无限循环（会导致卡顿）

✅ **支持的操作**
- 字符串操作
- 数值计算
- 逻辑判断
- 表操作
- 标准库函数

---

## 🔗 相关资源

| 资源 | 用途 |
|------|------|
| [SCRIPT_GUIDE.md](SCRIPT_GUIDE.md) | 完整的脚本使用指南 |
| [IMPLEMENTATION_SUMMARY.md](IMPLEMENTATION_SUMMARY.md) | 技术实现细节 |
| [Lua官方文档](https://www.lua.org/manual/) | Lua语言参考 |
| [Lua 5.1手册](https://www.lua.org/manual/5.1/) | 详细的API文档 |

---

## ❓ 常见问题

**Q: 脚本文件大小有限制吗？**  
A: 当前无特殊限制，但建议保持代码简洁

**Q: 能调用外部Lua库吗？**  
A: 暂不支持，仅标准Lua库

**Q: 多个脚本能组合使用吗？**  
A: 不支持，一个规则一个脚本

**Q: 脚本执行超时会怎样？**  
A: 会返回原始回复内容（降级处理）

---

**版本**: 1.1.0  
**最后更新**: 2026年2月4日

