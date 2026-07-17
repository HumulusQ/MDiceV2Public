<div align="center">


# MDiceV2

一款面向中文 TRPG 社群的模块化骰娘与跑团辅助工具。

[![Version](https://img.shields.io/badge/version-0.3.1--beta-7c5cff)](Version.props)
[![Platform](https://img.shields.io/badge/platform-Windows-0078d6)](#运行环境)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4)](https://dotnet.microsoft.com/)
[![OneBot](https://img.shields.io/badge/protocol-OneBot_11-4b9)](#连接与使用)

[下载最新版本](https://github.com/HumulusQ/MDiceV2Public/releases/latest) · [用户手册](MDiceV2手册/MDiceV2用户手册.docx) · [Mod 开发文档](Mods/MOD_DEVELOPMENT_GUIDE.md)

</div>

## 项目简介

MDiceV2 通过 OneBot 11 WebSocket 接入聊天平台，为群聊与私聊提供掷骰、规则检定、人物卡、跑团日志和团务管理等能力。项目带有 Avalonia 桌面管理界面，同时提供无头运行方式，并通过 Mod 系统扩展 AI 跑团、自定义回复与脚本化战斗等功能。

> 当前项目仍处于 Beta 阶段，功能与配置格式可能随版本更新调整。实际指令请以当前版本的 `.help` 回复和用户手册为准。

## 基本功能

### 骰子与规则检定

- 通用骰子表达式与四则运算，如 `.r 2d6+3 伤害`
- CoC 7th、ET 等规则检定，以及奖励骰、惩罚骰和连续检定
- CoC 理智检定、技能成长、临时疯狂与随机人物生成
- 先攻列表、无限流 d10 成功度等跑团工具

### 人物卡与团务管理

- 创建、更新、切换和删除人物卡
- 记录技能、理智等数据，并在检定后按规则持久化
- 群组队伍创建、成员管理、召集与技能排序
- 自定义显示名称、群名片模板、入群欢迎语和个人快捷指令

### 日志、规则书与牌堆

- 开启、停止、回顾并导出 HTML 跑团日志
- 从已加载的规则书数据库中查找条目
- 创建群组临时牌堆，支持放回抽取与不放回抽取
- 内置今日运势等实用牌堆

### 管理与运行

- 图形化配置、连接状态、运行日志与 Mod 管理界面
- OneBot 11 WebSocket 消息收发
- GUI 与无头（Console）两种启动模式
- 主程序、规则数据、人物卡资源与 Mod 更新支持
- 群组/个人权限等级、机器人开关及黑白名单相关管理

### Mod 扩展

MDiceV2 提供独立 DLL Mod 的加载、生命周期、优先级和消息拦截机制。仓库中包含以下扩展示例或实现：

| Mod | 功能 |
| --- | --- |
| `AIMod` | AI 角色、长期记忆、世界状态与 AI 跑团辅助 |
| `CustomizedReply` | 精确、模糊、正则与 Lua 脚本驱动的自定义回复 |
| `ABot` | ABOL 战斗脚本解释与回合状态管理 |

开发自己的扩展前，可先阅读 [Mod 系统架构指南](Mods/MOD_DEVELOPMENT_GUIDE.md) 与 [自定义回复示例](Mods/CustomizedReply/README.md)。

## 常用指令

所有一般指令以半角句点 `.` 开头，系统与管理员指令以 `#` 开头；指令名称不区分大小写。

| 指令 | 说明 | 示例 |
| --- | --- | --- |
| `.r` | 通用掷骰 | `.r 1d100 侦查` |
| `.st` | 创建或更新人物卡技能 | `.st(阿泽) 侦查70 聆听55` |
| `.com` | 列出、切换或删除人物卡 | `.com list` |
| `.cc` | CoC7 / ET 通用检定 | `.cc{coc7}(阿泽) 侦查` |
| `.sc` | CoC 理智检定 | `.sc 0/1d6` |
| `.ri` | 先攻与先攻列表 | `.ri+2 阿泽` |
| `.log` | 跑团日志管理 | `.log on 周末团` |
| `.rule` | 规则书查找 | `.rule(coc7) 闪避` |
| `.team` | 群组队伍管理 | `.team new 调查团` |
| `.deck` / `.draw` | 牌堆管理与抽牌 | `.draw 线索` |
| `.help` | 查看内置帮助 | `.help list` |
| `.bot` | 查看状态或开关响应 | `.bot on` |

更完整的参数、副指令、权限和注意事项请查看 [MDiceV2 用户手册](MDiceV2手册/MDiceV2用户手册.docx)。AIMod 与 ABot 指令只有在相应 Mod 已安装并成功加载后才可使用。

## 快速开始

1. 前往 [Releases](https://github.com/HumulusQ/MDiceV2Public/releases/latest) 下载最新版本并完整解压。
2. 准备兼容 OneBot 11 的聊天平台实现，并启用 WebSocket 服务。
3. 运行 `MDiceV2.Launcher.exe`，在管理界面填写连接地址、Master 帐号等基本配置。
4. 确认界面显示 WebSocket 已连接后，在聊天中发送 `.bot` 或 `.help` 验证运行状态。

请勿直接在压缩包内运行程序。更新、日志、数据库与 Mod 都需要程序目录具有写入权限。

## 连接与使用

MDiceV2 负责处理 OneBot 事件与 API 调用，本身不包含聊天平台登录实现。使用时需要配合兼容 OneBot 11 的实现，并让两端的 WebSocket 地址与端口保持一致。

- 桌面模式：直接运行 `MDiceV2.Launcher.exe`
- 无头模式：使用 `MDiceV2.Launcher.exe --headless`
- 指令帮助：在聊天中发送 `.help` 或 `.help list`
- 管理指令：仅 Master 或具备相应权限的帐号可用

## 运行环境

- Windows x64
- 发布包通常自带运行所需组件；从源码运行需要 .NET 10 SDK
- 编译完整解决方案中的 ABot 原生组件时，需要 Visual Studio 2022 C++ 工具链

## 从源码构建

```powershell
git clone https://github.com/HumulusQ/MDiceV2Public.git
cd MDiceV2Public
dotnet restore MDiceV2.sln
dotnet build MDiceV2.sln -c Release
```

仅构建桌面核心程序：

```powershell
dotnet build MDiceV2.Core/MDiceV2.Core.csproj -c Release
```

运行测试：

```powershell
dotnet test MDiceV2.Tests/MDiceV2.Tests.csproj -c Release
```

## 项目结构

```text
MDiceV2.Core/          核心消息处理、数据模型与 Avalonia 管理界面
MDiceV2.Launcher/      GUI / 无头模式启动器
MDiceV2.Console/       控制台启动与测试入口
MDiceV2.Abstractions/  跨进程与公共抽象
MDiceV2.Interfaces/    Mod API 与导航扩展接口
MDiceV2.Tests/         单元测试与集成测试
Mods/                  AIMod、CustomizedReply、ABot 与开发文档
Resources/             规则书、牌堆等资源
MDiceV2手册/           用户手册与使用注意事项
```

## 相关文档

- [用户手册](MDiceV2手册/MDiceV2用户手册.docx)
- [Mod 系统架构指南](Mods/MOD_DEVELOPMENT_GUIDE.md)
- [Mod 打包格式](Mods/MOD_PACKAGING_FORMAT.md)
- [Mod 快速参考](Mods/QUICK_REFERENCE.md)
- [CustomizedReply 使用与开发说明](Mods/CustomizedReply/README.md)

---

## 支持项目

MDiceV2 已入驻爱发电。如果这个项目对你的跑团或开发工作有所帮助，欢迎关注与支持：

### [前往爱发电支持 MDiceV2](https://ifdian.net/a/MDiceV2)
