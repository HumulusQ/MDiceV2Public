# MDiceV2 Mod 系统快速参考和部署指南

## 文件位置快速导航

### 核心接口（开发者必读）

| 文件 | 位置 | 用途 |
|------|------|------|
| `IModPlugin.cs` | `MDiceV2.Interfaces/Mod/` | Mod 实现的基础接口 |
| `IModContext.cs` | `MDiceV2.Interfaces/Mod/` | Mod 与宿主交互的 API |
| `IModMetadata.cs` | `MDiceV2.Interfaces/Mod/` | Mod 元数据接口 |
| `ModMessageResult.cs` | `MDiceV2.Interfaces/Mod/` | 消息处理结果类型 |

### 官方示例 Mod

| 文件 | 位置 | 用途 |
|------|------|------|
| `CustomizedReplyMod.cs` | `Mods/CustomizedReply/` | **完整示例实现（必读）** |
| `mod.json` | `Mods/CustomizedReply/` | Mod 配置文件示例 |
| `data.json` | `Mods/CustomizedReply/` | Mod 规则库数据示例 |
| `README.md` | `Mods/CustomizedReply/` | Mod 开发详细教程 |
| `CustomizedReply.csproj` | `Mods/CustomizedReply/` | 项目配置示例 |

### 系统文档

| 文件 | 位置 | 用途 |
|------|------|------|
| `MOD_DEVELOPMENT_GUIDE.md` | `Mods/` | **系统架构完整指南** |
| `IMPLEMENTATION_SUMMARY.md` | `Mods/` | 本次实现的总结 |
| 此文件 | `Mods/` | 快速参考和部署指南 |

---

## 编译构建

### 编译 Interfaces 库

```bash
cd C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2
dotnet build MDiceV2.Interfaces/MDiceV2.Interfaces.csproj -c Debug
```

**输出**: `MDiceV2.Interfaces\bin\Debug\net10.0-windows\MDiceV2.Interfaces.dll`

### 编译 CustomizedReply 示例 Mod

```bash
dotnet build Mods/CustomizedReply/CustomizedReply.csproj -c Debug
```

**输出**: `Mods\CustomizedReply\bin\Debug\net10.0-windows\CustomizedReply.dll`

### 编译所有 Mod（一键编译）

```bash
# 创建脚本 build_mods.bat
@echo off
cd /d %~dp0
for /d %%D in (Mods\*) do (
    echo Building %%D...
    dotnet build "%%D\*.csproj" -c Debug || exit /b 1
)
echo All mods built successfully!
```

---

## 部署步骤（用户指南）

### 步骤 1: 创建 Mod 目录

```bash
mkdir data\mods\CustomizedReply
```

### 步骤 2: 复制文件

将以下文件复制到 `data\mods\CustomizedReply\`：

- ✅ `CustomizedReply.dll` （从 `Mods\CustomizedReply\bin\Debug\net10.0-windows\` 复制）
- ✅ `mod.json` （从 `Mods\CustomizedReply\` 复制）
- ✅ `data.json` （从 `Mods\CustomizedReply\` 复制）

### 步骤 3: 重启程序

启动 MDiceV2 程序。Mod 应该自动加载。

### 步骤 4: 在 UI 中启用

在"Mod 管理"面板中找到"Custom Reply System"，点击启用按钮。

### 步骤 5: 测试

在群聊中发送"你好"，机器人应该回复"你好呀！"等内容。

---

## 快速开发指南

### 最小化 Mod 实现

```csharp
using MDiceV2.Interfaces.Mod;

public class MySimpleModPlugin : IModPlugin
{
    private readonly IModContext _context;
    
    public MySimpleModPlugin(IModContext context) => _context = context;
    
    public string ModId => "com.example.simple";
    public string ModName => "Simple Mod";
    public string Version => "1.0.0";
    public string Author => "You";
    public string Description => "A simple mod";
    
    public void OnLoad() { _context.Log(LogLevel.Info, "Mod loaded"); }
    public void OnEnable() { }
    public void OnDisable() { }
    public void OnUnload() { }
    
    public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
    {
        if (content == "test")
            return ModMessageResult.Intercept("Test reply!");
        return null;
    }
    
    public ModMessageResult? OnPrivateMessage(long userId, string content) => null;
}
```

### 项目模板创建

```bash
# 在 Mods 目录创建新项目
cd Mods
dotnet new classlib -n MyMod -f net10.0-windows
cd MyMod

# 添加依赖
dotnet add reference ../../MDiceV2.Interfaces/MDiceV2.Interfaces.csproj

# 复制 mod.json 和 data.json
copy ../CustomizedReply/mod.json
copy ../CustomizedReply/data.json
```

---

## 关键概念速查

### 三种匹配方式

```csharp
// 精确匹配
MatchType.Exact
// 消息内容 == 触发词
"你好" matches "你好" ✅
"你好啊" matches "你好" ❌

// 正则表达式
MatchType.Regex
// 使用 Regex.IsMatch()
"^(早|早安)" matches "早上好" ✅
"^(早|早安)" matches "你好早上" ❌

// 模糊匹配
MatchType.Fuzzy
// 消息包含触发词
"谢谢" matches "谢谢你啦" ✅
"谢谢" matches "感谢你" ❌
```

### 消息处理返回值

```csharp
// 拦截消息并回复，阻止传播
return ModMessageResult.Intercept("回复内容", stopPropagation: true);

// 拦截消息不回复
return ModMessageResult.InterceptSilent();

// 只发送回复不拦截
return ModMessageResult.ReplyOnly("回复内容");

// 不处理此消息
return null;
```

### 优先级划分

```json
{
  "priority": 50      // 低优先级（日志、监控）
}

{
  "priority": 100     // 普通优先级（常规功能）
}

{
  "priority": 150     // 高优先级（重要功能、权限检查）
}

{
  "priority": 200     // 系统级（不推荐）
}
```

### 日志级别

```csharp
_context.Log(LogLevel.Debug, "调试信息");
_context.Log(LogLevel.Info, "普通信息");
_context.Log(LogLevel.Warn, "警告信息");
_context.Log(LogLevel.Error, "错误信息");
_context.Log(LogLevel.Fatal, "严重错误");
```

---

## 常见问题快速解答

| 问题 | 答案 |
|------|------|
| **Mod 如何加载？** | 程序启动时扫描 `data/mods/*/mod.json`，使用反射加载 DLL |
| **如何发送消息？** | 在 OnGroupMessage 中返回 `ModMessageResult.Intercept(content)` |
| **如何访问 Mod 数据？** | 在 OnLoad 中从文件加载，在 OnUnload 中保存 |
| **支持多个 Mod 同时处理一条消息吗？** | 支持，按优先级顺序执行，可通过 StopPropagation 中断 |
| **禁用 Mod 会卸载 DLL 吗？** | 否，仅设置 enabled=false，DLL 保留在内存中 |
| **能否在构造函数之外访问 IModContext？** | 否，仅通过构造函数注入获得，建议保存为字段 |
| **如何进行日志调试？** | 在关键位置调用 `_context.Log()` 记录信息 |
| **如何实现私聊命令？** | 在 OnPrivateMessage 中检查消息并返回结果 |

---

## 开发者清单

在创建新 Mod 前，确保检查以下项目：

- [ ] **仅引用 MDiceV2.Interfaces**，不引用 Core 或 Launcher
- [ ] **实现所有 IModPlugin 成员**，包括 IModPlugin 默认成员
- [ ] **写好 mod.json**，确保 pluginClassName 正确
- [ ] **处理异常**，在所有公开方法中 try-catch
- [ ] **记录日志**，OnLoad/OnEnable/OnDisable/OnUnload 都要记录
- [ ] **编写文档**，说明 Mod 的用途和配置方法
- [ ] **编写示例配置**，如 data.json 或 config.json
- [ ] **测试生命周期**，确保启用/禁用/重启都正常
- [ ] **性能考虑**，避免在 OnGroupMessage 做重操作
- [ ] **向后兼容**，不要依赖特定的 API 版本

---

## 代码示例库

### 示例 1: 简单的关键词回复

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    var replies = new Dictionary<string, string>
    {
        { "你好", "你好呀！" },
        { "谢谢", "不客气～" }
    };
    
    if (replies.TryGetValue(content, out var reply))
        return ModMessageResult.Intercept(reply);
    
    return null;
}
```

### 示例 2: 命令处理

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    if (content.StartsWith("!"))
    {
        var command = content.Substring(1);
        var result = ProcessCommand(command);
        return ModMessageResult.Intercept(result);
    }
    return null;
}
```

### 示例 3: 条件判断

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    // 仅对特定用户生效
    if (userId == 123456)
    {
        if (content == "特殊命令")
            return ModMessageResult.Intercept("执行成功！");
    }
    return null;
}
```

### 示例 4: 异步操作

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    try
    {
        // 不能在此直接 await，但可以返回后异步发送
        Task.Run(async () =>
        {
            var result = await FetchDataAsync(content);
            _context.SendGroupMessage(groupId, result);
        });
        
        return ModMessageResult.InterceptSilent();  // 先回复"正在处理"
    }
    catch (Exception ex)
    {
        _context.Log(LogLevel.Error, ex.Message);
        return null;
    }
}
```

---

## 下次会议议题建议

1. **实现 ModPluginLoader** - 加载和管理 Mod 生命周期
2. **实现 ModEventBridge** - 分发消息到各 Mod
3. **集成 MessageDistribution** - 在消息处理链中调用 Mod
4. **UI 集成** - 在 ModManagerPanel 中控制 Mod 启禁
5. **测试框架** - 为 Mod 系统编写单元测试

---

## 相关资源

**核心读物**（按顺序阅读）：
1. 本文件（快速参考）
2. `Mods/CustomizedReply/README.md` （详细教程）
3. `Mods/MOD_DEVELOPMENT_GUIDE.md` （系统设计）
4. `CustomizedReplyMod.cs` （代码示例）

**API 参考**：
- `IModPlugin` 接口定义
- `IModContext` 接口定义
- `ModMessageResult` 类定义

**示例项目**：
- CustomizedReply Mod（完整示例）

---

**开始开发 Mod 吧！祝你编码愉快！** 🎉
