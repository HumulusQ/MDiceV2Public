# MDiceV2 Mod 系统架构指南

## 系统概述

MDiceV2 Mod 系统采用**插件架构**设计，允许开发者创建独立的 DLL 模块来扩展机器人功能。

### 核心特性

- **隔离性**: Mod 作为独立 DLL 加载，相互独立
- **优先级**: 支持 Mod 执行优先级，控制处理顺序
- **生命周期管理**: 完整的 OnLoad/OnEnable/OnDisable/OnUnload 钩子
- **消息拦截**: Mod 可拦截并处理群消息和私聊消息
- **API 暴露**: 通过 `IModContext` 暴露有限的宿主程序 API
- **依赖注入**: 使用构造函数注入方式传递上下文

### 架构图

```
┌─────────────────────────────────────────────────────────────┐
│                    MDiceV2 宿主程序                          │
│                                                               │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ MessageDistribution (消息分发)                         │   │
│  │ - 接收 OneBot 消息                                    │   │
│  │ - 调用 ModEventBridge.InvokeGroupMessage()            │   │
│  │ - 若未被拦截，调用 MessageProcessor 处理             │   │
│  └──────────────────────────────────────────────────────┘   │
│                           ↓                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ ModEventBridge (Mod 事件分发器) [TODO: 待实现]        │   │
│  │ - 按优先级排序 Mod 列表                              │   │
│  │ - 循环调用 Mod.OnGroupMessage()                      │   │
│  │ - 检查 StopPropagation 决定是否继续                  │   │
│  └──────────────────────────────────────────────────────┘   │
│                           ↓                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ Mod 1 (优先级 200)         Mod 2 (优先级 100)         │   │
│  │ ┌─────────────────────┐   ┌─────────────────────┐   │   │
│  │ │ OnGroupMessage()    │   │ OnGroupMessage()    │   │   │
│  │ │ ├─ 匹配规则        │   │ ├─ 匹配规则        │   │   │
│  │ │ ├─ 发送回复        │   │ ├─ 发送回复        │   │   │
│  │ │ └─ 返回结果        │   │ └─ 返回结果        │   │   │
│  │ └─────────────────────┘   └─────────────────────┘   │   │
│  └──────────────────────────────────────────────────────┘   │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```

---

## 文件结构

### 顶层项目结构

```
MDiceV2.sln
├── MDiceV2.Interfaces/           # 接口定义（Mod 必须引用）
│   └── Mod/
│       ├── IModPlugin.cs         # Mod 接口
│       ├── IModContext.cs        # 上下文接口
│       ├── IModMetadata.cs       # 元数据接口
│       └── ModMessageResult.cs   # 消息结果类
│
├── MDiceV2.Core/                 # 核心实现
│   └── Models/
│       └── MessageDistribution.cs # 消息分发器（需要集成 ModEventBridge）
│
├── MDiceV2.Launcher/             # 启动器
│
└── Mods/                         # Mod 开发示例
    └── CustomizedReply/          # 自定义回复 Mod 示例
        ├── CustomizedReply.csproj
        ├── CustomizedReplyMod.cs
        ├── mod.json              # Mod 配置
        ├── data.json             # 规则库数据
        └── README.md             # Mod 开发指南
```

### Mod 部署目录结构

```
data/
  mods/
    CustomizedReply/              # Mod 标识符作为文件夹名
      ├── .disabled               # 禁用标记文件（可选）
      ├── mod.json                # Mod 元数据
      ├── data.json               # Mod 数据
      ├── CustomizedReply.dll     # Mod DLL
      ├── config.json             # Mod 配置（可选）
      └── resources/              # 资源文件夹（可选）
          └── icon.png
```

---

## 接口详解

### IModPlugin 接口

每个 Mod 都必须实现此接口。

```csharp
public interface IModPlugin
{
    // 标识信息
    string ModId { get; }          // 如 "com.example.customreply"
    string ModName { get; }        // 显示名称
    string Version { get; }        // 语义化版本
    string Author { get; }         // 作者名
    string Description { get; }    // Mod 描述

    // 生命周期钩子
    void OnLoad();                 // 程序启动时（一次）
    void OnEnable();               // 用户启用时（可多次）
    void OnDisable();              // 用户禁用时（可多次）
    void OnUnload();               // 程序关闭时（一次）

    // 消息处理
    ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted);
    ModMessageResult? OnPrivateMessage(long userId, string content);
}
```

### IModContext 接口

宿主程序通过此接口向 Mod 提供功能。

```csharp
public interface IModContext
{
    // 消息发送
    void SendGroupMessage(long groupId, string content);
    void SendPrivateMessage(long userId, string content);

    // 信息查询
    (long UserId, string Nickname) GetUserInfo(long userId);

    // 日志记录
    void Log(LogLevel level, string message);

    // 状态查询
    bool IsSimulationMode { get; }
}
```

### ModMessageResult 类

Mod 通过此类告诉宿主程序如何处理消息。

```csharp
public class ModMessageResult
{
    public bool Intercepted { get; set; }        // 是否拦截
    public string? Reply { get; set; }           // 回复内容
    public bool StopPropagation { get; set; }   // 是否阻止传播

    // 便捷工厂方法
    public static ModMessageResult Intercept(string reply, bool stopPropagation = true);
    public static ModMessageResult Reply(string content);
}
```

---

## 加载和执行流程

### 1. 初始化阶段（程序启动）

```csharp
// 宿主程序执行（ModelPluginLoader）
1. 扫描 data/mods/ 目录
2. 对每个子文件夹：
   a. 读取 mod.json，验证格式和必要字段
   b. 使用 Assembly.Load() 或 Assembly.LoadFrom() 加载 DLL
   c. 反射查找实现 IModPlugin 的类
   d. 获取构造函数：ctor = type.GetConstructor(new[] { typeof(IModContext) })
   e. 调用 ctor.Invoke(new object[] { modContext }) 创建实例
   f. 调用 instance.OnLoad()
3. 保存所有加载的 Mod 实例到 ModEventBridge
4. 按 mod.json 中的 priority 排序
```

### 2. 启用阶段（用户操作）

```csharp
// 用户在 ModManagerPanel 启用 Mod
ModManagerViewModel.HandleEnableModCommand()
  ↓
ModEventBridge.EnableMod(modId)
  ↓
mod.OnEnable()
  ↓
设置 mod.Enabled = true，开始处理消息
```

### 3. 消息处理阶段（运行时）

```csharp
// 群消息到达
MessageDistribution.HandleMessageEvent()
  ↓
OnGroupMessage?.Invoke(groupId, userId, message, isAted, shouldIgnore)
  ↓
MessageDistribution.HandleMessage()
  ↓
ModEventBridge.InvokeGroupMessage(groupId, userId, content, isAted)
  ↓
// 循环所有已启用的 Mod（按优先级排序）
for each mod in sortedMods:
  result = mod.OnGroupMessage(...)
  
  if result != null:
    if result.Reply != null:
      messageDistribution.Reply(result.Reply, msg)
    
    if result.StopPropagation:
      break  // 停止调用后续 Mod
    
    if result.Intercepted:
      return  // 不调用 MessageProcessor
  ↓
MessageProcessor.OnHandleMessage(msg)  // 如果未被拦截
```

### 4. 禁用阶段（用户操作）

```csharp
// 用户禁用 Mod
ModManagerViewModel.HandleDisableModCommand()
  ↓
ModEventBridge.DisableMod(modId)
  ↓
mod.OnDisable()
  ↓
设置 mod.Enabled = false，停止处理消息
// 注：DLL 仍在内存中，资源未释放
```

### 5. 卸载阶段（程序关闭）

```csharp
// 程序关闭
Application.Exit()
  ↓
ModEventBridge.UnloadAll()
  ↓
for each mod:
  mod.OnUnload()
  ↓
清空 ModEventBridge 中的 Mod 列表
```

---

## 优先级和执行顺序

### 优先级规则

- Mod 按 `mod.json` 中的 `priority` 字段排序
- 值越大优先级越高，越先执行
- 同优先级时，加载顺序不定

### 优先级建议

```
0-50     - 低优先级 Mod（日志、监控等）
50-100   - 普通 Mod（常规功能）
100-200  - 高优先级 Mod（重要功能、权限检查）
200+     - 系统级 Mod（不建议使用）
```

### 执行链示例

假设有两个 Mod：

```
Mod A: priority=150, CustomizedReply
Mod B: priority=100, Logger
```

消息流程：

```
消息到达: "你好"
  ↓
Mod A (priority=150) OnGroupMessage("你好")
  ├─ 匹配规则 "你好"
  └─ 返回 Intercept("你好呀！", stopPropagation=true)
  ↓
停止执行 Mod B，返回到 MessageDistribution
  ↓
消息被拦截，不调用 MessageProcessor
```

---

## Mod 开发流程

### 步骤 1: 创建项目

```bash
# 创建类库项目
dotnet new classlib -n MyAwesomeMod -f net10.0-windows

# 添加对 MDiceV2.Interfaces 的引用
dotnet add reference ../MDiceV2.Interfaces/MDiceV2.Interfaces.csproj
```

### 步骤 2: 实现 IModPlugin

```csharp
using MDiceV2.Interfaces.Mod;

public class MyAwesomeModPlugin : IModPlugin
{
    private readonly IModContext _context;
    
    public MyAwesomeModPlugin(IModContext context)
    {
        _context = context;
    }
    
    public string ModId => "com.example.awesome";
    public string ModName => "My Awesome Mod";
    public string Version => "1.0.0";
    public string Author => "Your Name";
    public string Description => "An awesome mod";
    
    public void OnLoad() { /* 初始化 */ }
    public void OnEnable() { /* 启用 */ }
    public void OnDisable() { /* 禁用 */ }
    public void OnUnload() { /* 清理 */ }
    
    public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
    {
        if (content == "awesome")
        {
            return ModMessageResult.Intercept("This mod is awesome!");
        }
        return null;
    }
    
    public ModMessageResult? OnPrivateMessage(long userId, string content)
    {
        return null;
    }
}
```

### 步骤 3: 创建 mod.json

```json
{
  "id": "com.example.awesome",
  "name": "My Awesome Mod",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "An awesome mod",
  "dllFileName": "MyAwesomeMod.dll",
  "pluginClassName": "MyAwesomeMod.MyAwesomeModPlugin",
  "priority": 100,
  "modType": "dll",
  "supportHotReload": false,
  "apiVersion": "1.0"
}
```

### 步骤 4: 编译和部署

```bash
# 编译
dotnet build MyAwesomeMod.csproj -c Release

# 部署到 data/mods
mkdir -p data/mods/MyAwesomeMod
cp bin/Release/net10.0-windows/MyAwesomeMod.dll data/mods/MyAwesomeMod/
cp mod.json data/mods/MyAwesomeMod/
```

### 步骤 5: 测试

1. 启动 MDiceV2
2. 在 Mod 管理面板中启用 Mod
3. 在群聊中测试功能

---

## 常见开发模式

### 模式 1: 命令响应器

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    if (content.StartsWith("!help"))
    {
        return ModMessageResult.Intercept("帮助信息...");
    }
    return null;
}
```

### 模式 2: 关键词回复器

```csharp
private Dictionary<string, string> _keywords = new()
{
    { "你好", "你好！" },
    { "谢谢", "不客气！" }
};

public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    if (_keywords.TryGetValue(content, out var reply))
    {
        return ModMessageResult.Intercept(reply);
    }
    return null;
}
```

### 模式 3: 事件监听器

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    // 记录但不拦截
    _context.Log(LogLevel.Debug, $"[{groupId}] {userId}: {content}");
    return null;  // 继续传递给其他处理器
}
```

### 模式 4: 链式处理

```csharp
public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
{
    // Mod A: 权限检查
    if (!CheckPermission(userId))
    {
        return ModMessageResult.Intercept("你没有权限使用此功能");
    }
    
    // Mod B: 速率限制
    if (IsRateLimited(userId))
    {
        return ModMessageResult.Intercept("操作过于频繁，请稍后再试");
    }
    
    // Mod C: 处理请求
    if (content.StartsWith("!")) 
    {
        return ModMessageResult.Intercept(HandleCommand(content));
    }
    
    return null;
}
```

---

## 故障排除

### 问题 1: Mod 不加载

**原因分析：**
- mod.json 格式错误或字段缺失
- pluginClassName 不正确或该类不存在
- DLL 文件不存在或路径错误
- DLL 依赖的库缺失

**解决方法：**
1. 检查 mod.json 是否有效（可用 JSON 验证器）
2. 确认 pluginClassName 与代码中的类名匹配（包括命名空间）
3. 检查 DLL 文件是否存在于指定位置
4. 查看日志输出，寻找异常信息

### 问题 2: OnGroupMessage 未被调用

**原因分析：**
- Mod 未启用（checked checkbox 但 UI 显示红色）
- Mod OnLoad() 抛出异常
- 消息来自 DM 而非群聊
- 优先级很低，前面的 Mod 拦截了消息

**解决方法：**
1. 确保在 Mod 管理面板中启用了 Mod
2. 检查日志中 OnLoad() 是否成功
3. 在群聊中发送消息，而非私聊
4. 降低 Mod 优先级，让其更晚执行

### 问题 3: 消息回复无效

**原因分析：**
- 返回的 ModMessageResult 为 null
- Reply 字符串为 null 或空
- 宿主程序模拟模式下消息可能不显示
- WebSocket 连接失败

**解决方法：**
1. 确保 OnGroupMessage() 返回 non-null 的 ModMessageResult
2. 在返回前检查 Reply 是否为 null
3. 在聊天面板中观察是否有回复
4. 检查 WebSocket 连接状态

---

## 未来计划

### 功能扩展

- [ ] **Lua 脚本 Mod** - 支持热卸载的轻量级脚本
- [ ] **Mod 依赖管理** - 支持 Mod 间的依赖声明
- [ ] **权限系统** - 精细的资源访问控制
- [ ] **Mod 配置 UI** - 在管理面板中配置 Mod
- [ ] **热更新** - 在不重启程序的情况下更新 Mod

### API 扩展

在 IModContext 中添加：
- [ ] `GetGroupInfo(groupId)` - 获取群信息
- [ ] `GetGroupMembers(groupId)` - 获取群成员列表
- [ ] `SetGroupAdmin(groupId, userId, isAdmin)` - 设置管理员
- [ ] `RecallMessage(messageId)` - 撤回消息
- [ ] `SendGroupImage(groupId, imageData)` - 发送图片

---

## 参考资源

- **示例 Mod**: `Mods/CustomizedReply/` - 完整的自定义回复 Mod
- **接口定义**: `MDiceV2.Interfaces/Mod/` - 所有接口定义
- **开发指南**: `Mods/CustomizedReply/README.md` - 详细的开发教程

---

**开发 MDiceV2 Mod 很简单，让我们一起扩展机器人的功能吧！** 🚀
