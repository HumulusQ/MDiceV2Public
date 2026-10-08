# CustomizedReply 脚本UID获取错误修复报告

**修复日期**: 2026-03-31  
**问题**: 脚本实例不存在:default  
**状态**: ✅ 已修复

---

## 问题分析

### 症状
用户在使用CustomizedReply Mod时，虽然脚本加载成功（日志显示）：
```
[04:22:41] [INFO] [core] [RefreshScriptInstances] ✓ EXISTING instance: 3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d <- feedpet.lua
```

但调用脚本函数时仍然报错：
```
[FuncError: ScriptExecutor not initialized]
错误，脚本实例不存在:default
```

### 根本原因

#### 1. 标签解析层（MessageProcessor）
在 `MessageProcessor.cs` 中，`ParseTagWithTolerance()` 方法使用**空格**作为主分隔符：

```csharp
// 输入: "func: 3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:MainProcess"
// 解析结果:
//   tagType = "func"
//   tagContent = "3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:MainProcess"
```

#### 2. 脚本函数执行层（CustomizedReplyMod）
在 `CustomizedReplyMod.cs` 第246-276行，代码当时的逻辑：

**旧代码问题**:
```csharp
int colonIndex = funcSpec.IndexOf(':');
if (colonIndex <= 0)  // ❌ 如果格式错误
{
    // 返回错误，但代码继续执行后续逻辑
    string scriptInstanceUid = "default";  // ❌ 硬编码为"default"
    // 这个永远不存在的UID导致脚本执行失败
}
```

**核心问题**:
- 硬编码的 `"default"` UID 在脚本实例列表中不存在
- 即使脚本已正确初始化（具有真实UID），也会因为UID不匹配而失败

---

## 修复方案

### 修改1: CustomizedReplyMod.cs (行246-276)

**改动内容**:
1. 保留冒号（:）作为UID和函数名的分隔符
2. 添加UID非空验证
3. 改进错误消息和日志

**关键代码**:
```csharp
string scriptInstanceUid = funcSpec.Substring(0, colonIndex).Trim();
string functionName = funcSpec.Substring(colonIndex + 1).Trim();

// ✨ 新增：验证scriptInstanceUid不为空
if (string.IsNullOrWhiteSpace(scriptInstanceUid))
{
    _context.Log(LogLevel.Error, $"[CustomizedReply] 脚本实例UID为空。格式应为：<func: scriptuid:functionname()>，收到：{funcSpec}");
    return $"[错误] 脚本实例UID不能为空";
}
```

**日志改进**:
```csharp
// 执行前日志
_context.Log(LogLevel.Debug, $"[CustomizedReply] 正在执行脚本函数 - UID: {scriptInstanceUid}, 函数: {functionName}");

// 执行后日志
_context.Log(LogLevel.Info, $"[CustomizedReply] ✓ 脚本函数执行成功: {scriptInstanceUid}:{functionName} -> {result}");
```

### 修改2: data.json (rule_008)

**规则回复格式更新**:

| 版本 | 回复格式 |
|------|---------|
| 旧（有问题） | `"<output:0>"` |
| 新（已修复） | `"<func: 3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:MainProcess>"` |

**原因**: `<output:0>` 是占位符格式，不是脚本函数调用。正确的格式是：
- `<func: scriptuid:functionname>`

---

## 技术细节

### 标签处理流程

```
消息 "投喂"
    ↓
[CustomizedReplyMod] 规则匹配，获取回复: "<func: 3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:MainProcess>"
    ↓
[MessageProcessor.RefineMsg] 发现 <func: ...> 标签
    ↓
[ParseTagWithTolerance] 使用空格分割
    tagType = "func"
    tagContent = "3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:MainProcess"
    ↓
[scriptFunctionExecutor 委托]
    // funcSpec = "3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:MainProcess"
    colonIndex = funcSpec.IndexOf(':')  // 找到第一个冒号
    scriptInstanceUid = "3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d"  // ✅ 正确！
    functionName = "MainProcess"
    ↓
[ScriptExecutor.ExecuteFunction] 使用正确的UID执行
    ↓
✅ 脚本成功执行，返回结果
```

### 正确的标签格式

```
<func: scriptuid:functionname>
 ↑    ↑ ↑       ↑
 |    | |       |___ 函数名（冒号分隔）
 |    | |__________ 脚本实例UID
 |    |____________ MessageProcessor的空格分隔符
 |_________________ 标签类型
```

**有效示例**:
- `<func: 3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:MainProcess>`
- `<func: feedpet_v1:execute>`
- `<func: calculator:calculate()>` （括号会被自动移除）

**无效示例**（会报错）:
- `<func:MainProcess>` ❌ 缺少UID
- `<func: MainProcess>` ❌ 只有函数名，no UID
- `<func:3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:>` ❌ 函数名为空

---

## 验证步骤

### 1. 编译
```bash
cd c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2
dotnet build Mods/CustomizedReply/CustomizedReply.csproj -c Debug
# ✅ 编译成功，无错误
```

### 2. 构建调试版本
运行 `debug-build-clean` 任务

### 3. 测试
1. 启动应用
2. 加载CustomizedReply Mod
3. 在群里发送 "投喂"
4. 日志应显示:
   ```
   [CustomizedReply] 正在执行脚本函数 - UID: 3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d, 函数: MainProcess
   [CustomizedReply] ✓ 脚本函数执行成功: 3b6b44f2-2fc8-42b5-aa62-c6cb6bcab74d:MainProcess -> [返回值]
   ```
5. ❌ 不应再出现 "脚本实例不存在:default"

---

## 相关代码位置

| 文件 | 行号 | 说明 |
|------|------|------|
| CustomizedReplyMod.cs | 246-276 | scriptFunctionExecutor委托 |
| CustomizedReplyMod.cs | 241 | MessageProcessor初始化 |
| data.json | 164-183 | rule_008规则定义 |
| MessageProcessor.cs | 468-483 | ParseTagWithTolerance方法 |
| MessageProcessor.cs | 580-605 | scriptFunctionExecutor调用 |

---

## 相关文件引用

- [feedpet.lua](./scripts/feedpet.lua) - 投喂脚本实现
- [data.json](./data.json) - 规则库配置
- [CustomizedReplyMod.cs](./CustomizedReplyMod.cs) - Mod主类

---

## 对用户的建议

### 创建新的脚本规则时

1. **获取脚本实例UID**
   - 在Mod UI中查看脚本文件的UID
   - 或在日志中查看：`[RefreshScriptInstances] ✓ EXISTING instance: [UID] <- scriptname.lua`

2. **编写回复内容**
   ```json
   "replies": [
     "<func: [scriptuid]:MainProcess>",
     "备用回复（如果脚本执行失败）"
   ]
   ```

3. **指定脚本在规则中**
   ```json
   "scriptInstanceUid": "[相同的UID]",
   "scriptFilePath": "scriptname.lua"
   ```

### 调试脚本函数调用

查看日志输出：
```
[DEBUG] [CustomizedReply] 正在执行脚本函数 - UID: xxx, 函数: MainProcess
[INFO]  [CustomizedReply] ✓ 脚本函数执行成功: xxx:MainProcess -> result
```

如果看到这些日志说明UID获取正确。

---

## 改进历史

| 日期 | 版本 | 改进 |
|------|------|------|
| 2026-03-30 | v1.0 | 初始实现，存在UID硬编码问题 |
| 2026-03-31 | v1.1 | ✅ 修复硬编码"default"，添加UID验证和日志 |

