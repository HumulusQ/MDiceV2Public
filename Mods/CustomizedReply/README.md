# CustomizedReply Mod 开发者指南

## 概述

`CustomizedReply` 是一个完整的 MDiceV2 Mod 示例，展示如何为机器人实现自定义回复功能。

这个示例涵盖了 Mod 开发的所有关键方面：
- 实现 `IModPlugin` 接口
- 接收并处理群消息
- 使用 `IModContext` 与宿主程序交互
- 支持多种匹配方式（精确、正则表达式、模糊）
- 完整的错误处理和日志记录

**本示例代码包含大量注释，旨在作为 Mod 开发的官方参考实现。**

---

## 快速开始

### 1. 项目结构

```
Mods/
  CustomizedReply/
    ├── mod.json                    # Mod 元数据配置
    ├── data.json                   # 规则库数据（运行时加载）
    ├── CustomizedReply.csproj      # 项目文件
    ├── CustomizedReplyMod.cs       # 主实现类
    └── README.md                   # 本文件
```

### 2. 编译项目

在 Visual Studio 中或命令行编译：

```bash
dotnet build Mods/CustomizedReply/CustomizedReply.csproj
```

输出：`Mods/CustomizedReply/bin/Debug/net10.0-windows/CustomizedReply.dll`

### 3. 部署 Mod

部署步骤（供最终用户）：

1. 在程序的 `data/mods/` 目录创建文件夹：`CustomizedReply`
2. 复制以下文件到该文件夹：
   - `CustomizedReply.dll` （编译后的 DLL）
   - `mod.json` （Mod 配置）
   - `data.json` （规则库数据）

3. 重启 MDiceV2 程序
4. 在"Mod 管理"面板中启用 CustomizedReply Mod
5. 测试：在群聊中发送"你好"，机器人应该回复"你好呀！"等

---

## 文件详解

### mod.json - Mod 元数据配置

此文件定义了 Mod 的基本信息，宿主程序在加载 Mod 时读取此文件。

```json
{
  "id": "com.example.customreply",          // Mod 唯一标识符（建议使用反向域名格式）
  "name": "Custom Reply System",             // 显示名称
  "version": "1.0.0",                        // 语义化版本号
  "author": "Example Author",                // 作者名
  "description": "...",                      // Mod 描述
  "dllFileName": "CustomizedReply.dll",      // DLL 相对路径（相对于 mod 文件夹）
  "pluginClassName": "CustomizedReply.CustomizedReplyMod",  // 完整类名
  "priority": 100,                           // 执行优先级（高值优先）
  "modType": "dll",                          // Mod 类型（dll 或 lua，预留 lua 支持）
  "supportHotReload": false,                 // DLL Mod 不支持热卸载
  "apiVersion": "1.0"                        // 所需 API 版本
}
```

**关键字段说明：**

- **id**: 必须全局唯一，推荐使用 `com.author.modname` 格式
- **priority**: 当多个 Mod 处理同一消息时，高优先级的 Mod 会先执行。建议：
  - 100-150: 普通 Mod
  - 150-200: 重要系统级 Mod
  - 50-100: 低优先级 Mod
- **dllFileName**: 可以是 `CustomizedReply.dll` 或 `bin/CustomizedReply.dll`
- **pluginClassName**: 如果省略，宿主程序会自动查找实现 `IModPlugin` 的类

### CustomizedReplyMod.cs - 主实现类

此文件包含 Mod 的核心逻辑，实现 `IModPlugin` 接口。

#### 关键接口方法

**OnLoad()**
- 调用时机：程序启动时
- 用途：初始化 Mod，加载规则库
- 在本示例中：从 `data.json` 加载回复规则

**OnEnable()**
- 调用时机：用户启用 Mod 时
- 用途：准备 Mod 处理消息
- 与 OnLoad 的区别：可能被多次调用

**OnDisable()**
- 调用时机：用户禁用 Mod 时
- 用途：停止处理消息，但保留资源（不卸载 DLL）

**OnUnload()**
- 调用时机：程序关闭时
- 用途：清理资源、保存数据

**OnGroupMessage(long groupId, long userId, string content, bool isAted)**
- 调用时机：接收到群消息时
- 返回值说明：
  - `null`: 不处理此消息，继续传递给其他处理器
  - `ModMessageResult`: 已处理此消息
    - `Intercepted`: 是否拦截消息
    - `Reply`: 要发送的回复内容
    - `StopPropagation`: 是否阻止其他 Mod 处理

**OnPrivateMessage(long userId, string content)**
- 调用时机：接收到私聊消息时
- 本示例：不实现（返回 null）
- 扩展：可在此实现 Mod 管理命令

#### 三种匹配方式

1. **精确匹配 (Exact)**
   ```
   触发词: "你好"
   匹配: 消息内容必须完全等于 "你好"
   ```

2. **正则表达式 (Regex)**
   ```
   触发词: "^(早|早安|早上)"
   匹配: 消息开头为 "早" 或 "早安" 或 "早上"
   ```

3. **模糊匹配 (Fuzzy)**
   ```
   触发词: "谢谢"
   匹配: 消息中任何位置包含 "谢谢"
   ```

### data.json - 规则库数据

此文件定义了所有回复规则，Mod 在 `OnLoad()` 时从此文件加载。

```json
{
  "replies": [
    {
      "trigger": "你好",
      "matchType": "exact",
      "replies": [
        "你好呀！",
        "嗨～"
      ]
    }
  ]
}
```

**规则结构：**

- `trigger`: 触发词或正则表达式模式
- `matchType`: 匹配方式（"exact"、"regex"、"fuzzy"）
- `replies`: 可能的回复列表（随机选择一条）

---

## 开发 Mod 的关键概念

### 1. 构造函数注入

```csharp
public class CustomizedReplyMod : IModPlugin
{
    private readonly IModContext _context;
    
    // 宿主程序通过反射调用此构造函数
    public CustomizedReplyMod(IModContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }
}
```

**IModContext 提供的功能：**

```csharp
// 发送消息
_context.SendGroupMessage(groupId, "回复内容");
_context.SendPrivateMessage(userId, "回复内容");

// 获取用户信息
var (uid, nickname) = _context.GetUserInfo(userId);

// 记录日志
_context.Log(LogLevel.Info, "日志内容");

// 检查模拟模式
if (_context.IsSimulationMode) { ... }
```

### 2. 生命周期管理

Mod 的生命周期如下：

```
程序启动
    ↓
加载 DLL，反射查找 IModPlugin 实现
    ↓
构造实例（注入 IModContext）
    ↓
调用 OnLoad()  ← 一次性初始化
    ↓
用户启用 Mod
    ↓
调用 OnEnable()
    ↓
消息到达 → OnGroupMessage() / OnPrivateMessage()  ← 可能多次
    ↓
用户禁用 Mod
    ↓
调用 OnDisable()  ← 但 DLL 仍在内存中
    ↓
程序关闭
    ↓
调用 OnUnload()  ← 清理资源
    ↓
卸载 DLL
```

### 3. 优先级和消息拦截

当多个 Mod 都要处理同一消息时：

1. 按 `mod.json` 中的 `priority` 从高到低排序
2. 按顺序调用每个 Mod 的 `OnGroupMessage()`
3. 如果 Mod 返回 `non-null` 且 `StopPropagation=true`，停止传递给后续 Mod

```csharp
// 拦截消息，阻止传递给其他 Mod
return ModMessageResult.Intercept(reply, stopPropagation: true);

// 拦截消息，但允许其他 Mod 继续处理
return ModMessageResult.Intercept(reply, stopPropagation: false);

// 不拦截，仅发送回复（继续传递给其他处理器）
return ModMessageResult.Reply(reply);

// 不处理此消息
return null;
```

### 4. 错误处理

Mod 应该对所有可能的异常进行处理，避免影响宿主程序：

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    try
    {
        // Mod 逻辑
        return ProcessMessage(content);
    }
    catch (Exception ex)
    {
        // 记录错误，但继续运行
        _context.Log(LogLevel.Error, $"处理消息时发生错误: {ex.Message}");
        return null;  // 不处理此消息
    }
}
```

---

## 扩展示例

### 示例 1: 实现私聊管理命令

```csharp
public ModMessageResult? OnPrivateMessage(long userId, string content)
{
    if (content.StartsWith("!customreply help"))
    {
        var helpText = @"CustomizedReply Mod 命令:
!customreply add <触发词> <回复内容>  - 添加规则
!customreply delete <触发词>         - 删除规则
!customreply list                   - 列出所有规则
!customreply export                 - 导出规则库";
        
        _context.SendPrivateMessage(userId, helpText);
        return ModMessageResult.InterceptSilent();
    }
    
    return null;
}
```

### 示例 2: 条件回复

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    // 仅在被 @时回复
    if (isAted && content == "你好")
    {
        return ModMessageResult.Intercept("你好！你@了我～");
    }
    
    return null;
}
```

### 示例 3: 基于时间的回复

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    if (content == "现在几点")
    {
        var currentTime = DateTime.Now.ToString("HH:mm:ss");
        return ModMessageResult.Intercept($"现在是 {currentTime}");
    }
    
    return null;
}
```

### 示例 4: 调用 OneBot API 获取群信息

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    if (content == "群名片")
    {
        var (uid, nickname) = _context.GetUserInfo(userId);
        return ModMessageResult.Intercept($"你的昵称是: {nickname}");
    }
    
    return null;
}
```

---

## 最佳实践

### 1. 日志记录

在关键步骤记录日志，便于调试：

```csharp
_context.Log(LogLevel.Debug, "进入 OnGroupMessage");
_context.Log(LogLevel.Info, "规则库加载完成");
_context.Log(LogLevel.Warn, "配置文件不存在");
_context.Log(LogLevel.Error, "发生异常: ...");
```

### 2. 配置文件管理

使用 JSON 文件存储 Mod 配置和数据，便于用户修改：

```csharp
// 在 OnLoad() 中加载
var json = File.ReadAllText(configPath);
var config = JsonDocument.Parse(json);

// 在 OnUnload() 中保存
var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(configPath, json);
```

### 3. 性能优化

对于频繁执行的操作，使用缓存和预编译：

```csharp
// 在 OnLoad() 时编译正则表达式
_compiledRegex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

// 在 OnGroupMessage() 中重用
if (_compiledRegex.IsMatch(content)) { ... }
```

### 4. 线程安全

Mod 的方法可能被多个线程调用，确保线程安全：

```csharp
// 如果 _replyRules 在 OnGroupMessage() 中被修改，需要加锁
private readonly object _lock = new object();

public ModMessageResult? OnGroupMessage(...)
{
    lock (_lock)
    {
        // 访问 _replyRules
    }
}
```

### 5. 避免创建重依赖

不要在 Mod 中引用 MDiceV2.Core 或 MDiceV2.Launcher，仅引用 MDiceV2.Interfaces：

```xml
<!-- ✅ 正确：仅引用 Interfaces -->
<ProjectReference Include="...\MDiceV2.Interfaces\MDiceV2.Interfaces.csproj" />

<!-- ❌ 避免：引用 Core 会导致 Mod 过大且依赖过多 -->
<ProjectReference Include="...\MDiceV2.Core\MDiceV2.Core.csproj" />
```

---

## 常见问题

### Q: 如何测试 Mod？

A: 
1. 编译项目生成 DLL
2. 部署到 `data/mods/CustomizedReply/`
3. 启动 MDiceV2 并在 Mod 管理面板中启用
4. 在群聊或聊天面板中发送测试消息
5. 观察日志输出查看 Mod 是否正常工作

### Q: 如何调试 Mod？

A:
1. 在 Visual Studio 中设置断点
2. 将 Mod DLL 附加到 MDiceV2 进程（需要配置调试器）
3. 或在 `OnLoad()` 等方法中调用 `System.Diagnostics.Debugger.Break()`
4. 或使用 `_context.Log()` 记录变量值进行调试

### Q: Mod 可以访问群成员列表吗？

A: 当前 `IModContext` 不提供此功能。如需扩展，可修改宿主程序中的 `IModContext` 实现以添加新方法。

### Q: 禁用 Mod 后重新启用，规则库会重新加载吗？

A: 不会。DLL 在内存中保留，`OnEnable()` 只是设置 `_isEnabled=true`。规则库在 `OnLoad()` 时加载并一直保留。重新启用会立即生效。

### Q: 支持 Lua 脚本 Mod 吗？

A: 当前不支持。但在 `mod.json` 中预留了 `"modType": "lua"` 字段以支持未来扩展。Lua Mod 会有自己的加载器和执行引擎。

---

## 总结

这个示例展示了如何创建一个功能完整的 MDiceV2 Mod：

✅ 实现 `IModPlugin` 接口  
✅ 通过构造函数注入获得 `IModContext`  
✅ 在 `OnGroupMessage()` 中处理消息  
✅ 支持多种匹配方式  
✅ 完整的错误处理和日志记录  
✅ 规范的代码注释和文档  

开发者可以基于此示例快速开发自己的 Mod。有问题或建议，请提交 Issue。
