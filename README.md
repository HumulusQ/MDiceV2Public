# MDiceV2

> 面向 QQ / OneBot 生态的可扩展 TRPG 骰娘与跑团辅助工具。

MDiceV2 是一个以 **C# / .NET 10 / Avalonia** 为主要技术栈开发的 TRPG 骰娘项目。它通过 OneBot WebSocket 与 QQ 机器人端连接，在基础掷骰之外提供人物卡、CoC/ET 检定、SAN、先攻、跑团日志、牌堆、团队管理、人物卡文件导入，以及可扩展的 Mod 系统。

当前主程序版本线为 **0.3.1 beta**，项目主要面向 **Windows x64**。

- 仓库：<https://github.com/HumulusQ/MDiceV2Public>
- Releases：<https://github.com/HumulusQ/MDiceV2Public/releases>

## 功能概览

### 🎲 骰点与检定

- 标准骰式与算式，例如 `1d100`、`2d6+3`、带括号的表达式等。
- 通用 `.r` 掷骰，支持暗骰以及奖励骰 / 惩罚骰模式。
- CoC7 / ET 人物检定与人物卡技能读取。
- CoC SAN 检定并自动更新人物卡中的理智值。
- 先攻投掷、排序、增删与持久化。
- CoC / DND / ET 等模式的角色属性生成入口。

### 🧑‍💼 人物卡

- 使用 `.st` 创建或更新人物卡技能。
- 多人物卡管理与当前人物卡查看。
- 支持 `.mdice.html`、`.mdice` 等人物卡文件的群文件导入流程。
- 导入前会要求上传者确认；单个自动导入文件上限为 5 MiB。
- 内置便携式 CoC7 调查员人物卡页面，并包含对应的更新支持。

### 📜 跑团辅助

- 群跑团日志记录、列表、回顾与导出。
- 先攻列表管理。
- 牌堆与抽牌指令。
- 团队 / 角色相关管理功能。
- 名称绑定、群名片同步、代执行等辅助能力。
- GUI 内置模拟消息模式，方便在不实际发送 QQ 消息的情况下测试指令。

### 🔌 OneBot 连接

MDiceV2 内置 OneBot WebSocket 客户端，通过 WebSocket 接收事件并调用 OneBot API 发送群消息、私聊消息、群文件等。

默认 WebSocket 地址为：

```text
ws://localhost:8080
```

实际地址可以在程序配置中修改并持久化保存。

### 🧩 Mod 系统

MDiceV2 提供独立的 `MDiceV2.Interfaces` 插件 API。DLL Mod 通过 `mod.json` 描述自身，并实现 `IModPlugin` 接口。

Mod 生命周期包括：

```text
发现 Mod → OnLoad → OnEnable → 消息处理 → OnDisable → OnUnload
```

Mod 可以：

- 处理或拦截群聊 / 私聊消息；
- 发送群消息与私聊消息；
- 调用主程序已有指令；
- 注册自己的命令；
- 在主界面注册自定义导航面板；
- 访问宿主提供的权限、配置与持久化能力。

程序运行时可通过 Mod Manager 查看、启用或禁用已经加载的 Mod。

## 仓库中的主要 Mod

| Mod | 状态 / 用途 |
| --- | --- |
| `CustomizedReply` | 自定义回复系统，支持精确、正则和模糊匹配，并带独立管理界面。 |
| `AIMod` | AI/TRPG 扩展，可连接 Google Gemini、ZhipuAI、SiliconFlow、DeepSeek 等模型服务，并包含面向跑团场景的上下文、记忆与状态管理代码。 |
| `ABot` | Battle Orchestration Toolkit 脚本解释器，包括 Lexer、Parser、字节码与 VM。部分执行能力依赖 C++/CLI 层；依赖缺失时会降级而不是直接使宿主崩溃。 |
| `ETBattleRelay` | ET Battle Engine 的房间发现、WebRTC 信令、心跳、身份恢复及 WebSocket 转发模块。默认不对公网监听，需要单独配置反向代理后使用。 |

> `AIMod`、`ABot`、`ETBattleRelay` 等属于可选扩展；运行 MDiceV2 的基础骰娘功能并不要求启用所有这些模块。

## 常用指令

MDiceV2 的普通指令以 `.` 开头。可以在程序中使用 `.help` 查看内置帮助。

| 指令 | 说明 | 示例 |
| --- | --- | --- |
| `.r` | 通用掷骰 | `.r 2d6+3` |
| `.rh` / `.r h` | 暗骰 | `.rh 1d100` |
| `.st` | 创建 / 更新人物卡技能 | `.st(调查员)侦查70 聆听60` |
| `.sc` | SAN 检定并自动更新理智 | `.sc 1/1d6` |
| `.cc` | CoC7 / ET 等模式的通用检定 | `.cc{coc7}(调查员)侦查70` |
| `.com` | 查看当前人物卡 | `.com` |
| `.gc` | 生成角色属性 | `.gc coc 3` |
| `.ri` | 投掷并管理先攻 | `.ri+d20+2 调查员` |
| `.log` | 跑团日志管理 | `.log on 模组名` |
| `.draw` / `.deck` | 抽牌与牌堆管理 | `.draw` |
| `.jrrp` | 今日人品 | `.jrrp` |
| `.bot` | 查看、开启或关闭当前会话中的机器人响应 | `.bot` |

此外，核心中还包含 `.ra`、`.rc`、`.ww`、`.team`、`.name`、`.cn`、`.as`、`.diy`、`.welcome` 等功能入口。

## 快速开始

### 方式一：使用 Release 包

对于普通使用者，推荐直接从 [Releases](https://github.com/HumulusQ/MDiceV2Public/releases) 下载最新的完整发布包，例如：

```text
MDiceV2.PublishV*.zip
```

解压后：

1. 启动 MDiceV2；
2. 确保你的 OneBot 实现已经运行；
3. 在 MDiceV2 中填写对应的 WebSocket 地址；
4. 建立连接；
5. 在 QQ 中发送 `.help` 或 `.bot` 确认工作状态。

如 OneBot 使用默认本机端口，可先尝试：

```text
ws://localhost:8080
```

### 方式二：从源码构建

主要宿主项目面向 `net10.0-windows` / `win-x64`。

建议准备：

- Windows x64；
- .NET 10 SDK；
- Git；
- Visual Studio / Rider / VS Code 等支持 .NET 的开发环境。

克隆仓库：

```powershell
git clone https://github.com/HumulusQ/MDiceV2Public.git
cd MDiceV2Public
```

构建核心：

```powershell
dotnet restore .\MDiceV2.Core\MDiceV2.Core.csproj
dotnet build .\MDiceV2.Core\MDiceV2.Core.csproj -c Release
```

构建 Launcher：

```powershell
dotnet build .\MDiceV2.Launcher\MDiceV2.Launcher.csproj -c Release
```

运行测试：

```powershell
dotnet test .\MDiceV2.Tests\MDiceV2.Tests.csproj -c Release
```

> Release 包拥有自己的最终目录布局；普通 `dotnet build` 的输出不一定等同于可直接分发的完整发布包。

### ABot 开发补充

`ABot/` 中还包含 C++ / C++/CLI 组件。如果你需要编译完整的 ABot 原生层，需要额外准备 Visual Studio 的 C++ 编译工具链。只开发 MDiceV2 主程序或其他纯 C# Mod 时并不需要先编译这部分。

## 项目结构

```text
MDiceV2Public/
├─ MDiceV2.Core/          # 主程序、UI、消息处理、骰点、人物卡、日志、Mod 宿主
├─ MDiceV2.Launcher/      # Windows 启动器
├─ MDiceV2.Console/       # Headless / 控制台启动入口
├─ MDiceV2.Interfaces/    # Mod 公共接口
├─ MDiceV2.Abstractions/  # 跨组件抽象与配置同步协议
├─ MDiceV2.Tests/         # 单元、集成与性能测试
├─ Mods/
│  ├─ CustomizedReply/    # 自定义回复 Mod
│  ├─ AIMod/              # AI / TRPG Mod
│  ├─ ABot/               # ABOT 解释器 Mod
│  └─ ETBattleRelay/      # ET Battle 网络 Relay
├─ ABot/                  # ABOT C++ / C++/CLI 实现
├─ Resources/             # 规则等资源
├─ data/                  # 运行数据 / SQLite 数据库
└─ MDiceV2手册/           # 用户手册及相关文档
```

## Mod 开发

一个典型 DLL Mod 目录至少包含：

```text
MyMod/
├─ MyMod.dll
└─ mod.json
```

`mod.json` 示例：

```json
{
  "id": "com.example.mymod",
  "name": "My Mod",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "Example MDiceV2 Mod",
  "dllFileName": "MyMod.dll",
  "pluginClassName": "MyMod.MyModPlugin",
  "priority": 100,
  "modType": "dll",
  "supportHotReload": false,
  "apiVersion": "1.0"
}
```

插件类需要实现：

```csharp
MDiceV2.Interfaces.Mod.IModPlugin
```

推荐同时参考：

- `MDiceV2.Interfaces/Mod/IModPlugin.cs`
- `MDiceV2.Interfaces/Mod/IModContext.cs`
- `Mods/CustomizedReply/`

`CustomizedReply` 是一个较完整的 UI + 配置 + 消息处理 Mod 示例。

## 数据与配置

MDiceV2 使用 SQLite 保存多类本地数据，包括基础设置、人物卡、先攻与其他运行状态。

部分扩展（尤其 `AIMod`）可能需要第三方服务 API Key。请不要把自己的 API Key、访问凭证或私人跑团数据提交到公共仓库。

## 当前开发状态

MDiceV2 目前仍处于 **Beta** 阶段。仓库中同时存在主线功能、测试工程、实验模块以及部分历史/构建产物，因此源码目录并不等同于最终发行包的目录结构。

如果只是想使用骰娘，优先选择 Release 中的完整发布包；如果希望开发 Mod、研究骰点逻辑或参与项目开发，再从源码开始会更合适。

## 反馈与贡献

发现 Bug、兼容性问题或有新的功能建议时，可以通过 GitHub Issues 反馈；如需提交代码变更，可以通过 Pull Request 提交。

- Issues：<https://github.com/HumulusQ/MDiceV2Public/issues>
- Pull Requests：<https://github.com/HumulusQ/MDiceV2Public/pulls>

## License

本仓库当前未在顶层提供明确的开源许可证文件。

在许可证正式补充之前，请不要默认将“代码公开可见”理解为已经获得复制、修改或再分发代码的授权。如需复用项目代码，请先与项目作者确认许可范围。
