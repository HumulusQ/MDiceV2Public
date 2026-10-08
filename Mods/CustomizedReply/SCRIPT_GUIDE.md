# CustomizedReply 脚本编辑功能指南

**功能版本**: 1.1.0  
**添加日期**: 2026年2月4日  

---

## 🎯 功能概述

CustomizedReply Mod现在支持**Lua脚本编辑模式**，允许用户编写自定义脚本来动态生成回复内容。

### 核心特性

- ✅ **脚本编辑模式切换** - 在普通模式和脚本编辑模式间快速切换
- ✅ **Lua脚本支持** - 完整的Lua脚本运行环境
- ✅ **文件加载** - 从硬盘加载预先编写的Lua脚本
- ✅ **实时执行** - 规则触发时自动执行脚本的MainProcess函数
- ✅ **上下文访问** - 脚本可访问消息信息、用户信息等完整上下文
- ✅ **动态回复** - 脚本返回的内容替换<output>标签

---

## 📝 UI操作说明

### 1. 切换编辑模式

在"回复内容"标题处最右侧，有一个**脚本编辑模式切换按钮**（图标为脚本图标）。

- **点击按钮** = 切换到脚本编辑模式
- **再次点击** = 返回普通回复模式

### 2. 脚本编辑界面

在脚本编辑模式下，你会看到：

```
┌─────────────────────────────────┐
│  Lua脚本编辑框                  │
│  输入或粘贴Lua代码              │
│                                 │
│ (120行高的文本编辑框)           │
└─────────────────────────────────┘

┌────────────────────────────────────┐
│  从文件加载 Lua 脚本  [按钮]      │
└────────────────────────────────────┘

┌────────────────────────────────────┐
│  [脚本文件icon]  已选择脚本: xxx   │
│                  或                │
│                  未选择脚本文件    │
└────────────────────────────────────┘
```

### 3. 加载脚本文件

**步骤：**
1. 点击 **"从文件加载 Lua 脚本"** 按钮
2. 在文件浏览器中选择 `.lua` 文件
3. 文件内容自动加载到编辑框
4. 脚本文件名显示在下方标签中
5. 文件icon更新为成功标志

---

## 🔧 Lua脚本编写指南

### 脚本基本结构

每个Lua脚本都**必须**包含一个 `MainProcess` 函数：

```lua
-- CustomizedReply Lua 脚本示例

function MainProcess()
    -- 你的脚本逻辑
    return "回复文本"
end
```

### 可访问的全局变量

在脚本中，你可以访问以下预定义的全局变量：

| 变量名 | 类型 | 说明 | 示例 |
|--------|------|------|------|
| `user_id` | number | 发送消息的用户QQ | `123456789` |
| `user_name` | string | 用户昵称 | `"张三"` |
| `group_id` | number | 消息所在群号 | `987654321` |
| `group_name` | string | 群名称 | `"测试群"` |
| `message` | string | 收到的原始消息 | `"你好啊"` |
| `default_reply` | string | 规则的默认回复 | `"你好呀"` |
| `timestamp` | number | 消息时间戳 | `1707029082` |
| `is_ated` | boolean | 是否被@了 | `true / false` |

### 脚本示例

#### 示例 1: 简单的条件回复

```lua
function MainProcess()
    if string.find(message, "你好") then
        return "你好！很高兴见到你 ^_^"
    elseif string.find(message, "谢谢") then
        return "不客气！😊"
    else
        return default_reply
    end
end
```

#### 示例 2: 基于用户的回复

```lua
function MainProcess()
    -- 特定用户的特殊回复
    if user_id == 123456789 then
        return "主人好！✨"
    else
        return "你好，" .. user_name .. "！"
    end
end
```

#### 示例 3: 基于时间的回复

```lua
function MainProcess()
    local hour = tonumber(os.date("%H"))
    
    if hour < 12 then
        return "早上好！☀️"
    elseif hour < 18 then
        return "下午好！🌤"
    else
        return "晚上好！🌙"
    end
end
```

#### 示例 4: 使用<output>标签

在回复模板中使用 `<output>` 标签来标记脚本输出的位置：

```
回复内容设置：
"感谢 <output> 的留言！"

脚本返回：
"小明"

最终回复：
"感谢 小明 的留言！"
```

---

## 🔄 脚本执行流程

```
┌──────────────────────────────┐
│  用户发送消息                │
└──────────────────────────────┘
              ↓
┌──────────────────────────────┐
│  规则匹配成功                │
└──────────────────────────────┘
              ↓
┌──────────────────────────────┐
│  检查是否启用脚本编辑模式    │
└──────────────────────────────┘
              ↓
        ┌─────┴─────┐
        ↓           ↓
    [是]         [否]
        ↓           ↓
   ┌─────────┐  ┌──────────┐
   │执行脚本 │  │使用默认  │
   │MainProc │  │回复内容  │
   │-ess()   │  │          │
   └────┬────┘  └──────────┘
        ↓            ↓
   ┌─────────────────────┐
   │  获取脚本返回值     │
   └────────┬────────────┘
            ↓
   ┌─────────────────────┐
   │替换<output>标签     │
   │（如果存在）         │
   └────────┬────────────┘
            ↓
   ┌─────────────────────┐
   │  发送最终回复       │
   └─────────────────────┘
```

---

## ⚙️ 脚本上下文（ScriptContext）

脚本执行器提供的完整上下文对象包含：

```csharp
public class ScriptContext
{
    public long UserId { get; set; }              // 用户ID
    public string UserName { get; set; }          // 用户昵称
    public long GroupId { get; set; }             // 群ID
    public string GroupName { get; set; }         // 群名称
    public string Message { get; set; }           // 消息内容
    public string DefaultReply { get; set; }      // 默认回复
    public long Timestamp { get; set; }           // 时间戳
    public bool IsAted { get; set; }              // 是否被@
    public Dictionary<string, object> CustomData  // 自定义数据
}
```

---

## 🛠️ 技术实现

### 脚本执行器类

**文件**: `ScriptExecutor.cs`

主要方法：
- `ExecuteScript()` - 执行脚本并返回结果
- `GenerateScriptTemplate()` - 生成脚本模板
- `ReplaceOutputTag()` - 替换<output>标签

### UI更新

**文件**: `CustomizedReplyPanel.axaml`

新增元素：
- `IsScriptEditMode` - 脚本编辑模式开关属性
- `SelectedScriptContent` - 脚本内容
- `SelectedScriptFileName` - 脚本文件名
- `ScriptFileIcon` - 文件icon

**文件**: `CustomizedReplyPanel.axaml.cs`

新增方法：
- `OnSelectScriptFile()` - 文件选择事件处理

### 数据模型

**ReplyRuleItem** 新增属性：
- `IsScriptEditMode` - 是否启用脚本模式
- `ScriptContent` - 脚本代码
- `ScriptFilePath` - 脚本文件路径

---

## ⚠️ 注意事项

1. **函数命名必须严格** - 必须使用 `MainProcess`（大小写敏感）
2. **返回值类型** - 脚本必须返回字符串类型
3. **错误处理** - 脚本执行出错时，将使用原始回复模板作为降级方案
4. **安全考虑** - 脚本运行在沙箱环境中，某些危险操作会被限制
5. **性能** - 复杂脚本可能影响响应速度，建议保持脚本简洁

---

## 📚 常用Lua函数参考

| 函数 | 说明 | 示例 |
|------|------|------|
| `string.find(s, pattern)` | 查找字符串 | `string.find(message, "hello")` |
| `string.sub(s, i, j)` | 提取子串 | `string.sub(message, 1, 5)` |
| `string.len(s)` | 字符串长度 | `string.len(message)` |
| `string.upper(s)` | 转大写 | `string.upper("hello")` |
| `string.lower(s)` | 转小写 | `string.lower("HELLO")` |
| `tonumber(s)` | 转数字 | `tonumber("123")` |
| `tostring(v)` | 转字符串 | `tostring(user_id)` |
| `os.time()` | 当前时间戳 | `os.time()` |
| `os.date(format)` | 格式化日期 | `os.date("%Y-%m-%d")` |
| `math.random()` | 随机数 | `math.random(1, 100)` |

---

## 🔍 调试技巧

1. **使用日志** - 在脚本中输出调试信息
2. **测试环境** - 在真实环境前，在脚本编辑框中测试代码
3. **简化脚本** - 从简单脚本开始，逐步添加功能
4. **检查变量** - 验证脚本能否访问到期望的变量

---

## 🚀 高级特性规划

- [ ] 脚本版本控制
- [ ] 脚本库（预定义函数集）
- [ ] 脚本性能分析
- [ ] 脚本调试器集成
- [ ] 异步脚本执行

---

## 📞 获取帮助

如遇到问题，请检查：
1. ✓ 是否有 `function MainProcess()` 定义
2. ✓ 是否返回了字符串
3. ✓ Lua语法是否正确
4. ✓ 是否正确访问了全局变量

