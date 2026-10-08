# AIMod 指令升级实装计划 v2.0（最终版）

**日期**: 2026-05-28  
**范围**: `.ai model` 指令 + Token计数 + 百万Token警告  
**验收标准**: aimod与程序本体完全解耦

---

## 📋 目录

1. [需求摘要](#需求摘要)
2. [现有结构](#现有结构)
3. [设计方案](#设计方案)
4. [实装阶段](#实装阶段)
5. [代码实现](#代码实现)
6. [测试场景](#测试场景)
7. [验收清单](#验收清单)

---

## 需求摘要

### 用户需求
1. **`.ai model` 指令** - 显示所有可用的AI模型提供商和模型列表（按提供商分类）
2. **模型选择** - 用户通过数字选择提供商和模型，进入focus状态交互
3. **Token计数** - 自动追踪用户的AI Token使用量
4. **百万Token通知** - 当用户使用达到1,000,000 token时，发送私聊提示
5. **解耦验证** - aimod必须与程序本体保持解耦

### 业务价值
- 用户可以灵活切换AI服务提供商和模型
- 自动提醒用户控制成本
- 支持多提供商策略（负载均衡、成本优化）

---

## 现有结构

### 支持的AI提供商（4个）
```
1. Gemini (Google)
   - gemini-2.5-flash
   - gemini-2.0-flash

2. ZhipuAI (智谱AI)
   - glm-4.7-flash

3. SiliconFlow (硅基流动)
   - Qwen/Qwen3-8B

4. DeepSeek
   - deepseek-chat
```

### 现有用户设置
```csharp
public class UserApiSetting
{
    public string ApiKey { get; set; } = "";
    public string SubApiKey { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

### 现有指令
- `.ai api <key>` - 设置主API Key
- `.ai subapi <key>` - 设置轻量API Key
- `.ai api clear` - 清除主API
- `.ai subapi clear` - 清除轻量API
- `.ai api show` - 显示当前API状态

### Focus状态机制
- 管理位置: `MessageDistribution.UserFocusStates` (Dictionary<userId, focusType>)
- 设置方法: `MessageDistribution.SetUserFocus(userId, focusType)`
- 清除方法: `MessageDistribution.ClearUserFocus(userId)`
- 查询方法: `MessageDistribution.GetUserFocus(userId)`

---

## 设计方案

### 1️⃣ 数据模型扩展

#### A. UserApiSetting 新增字段

```csharp
public class UserApiSetting
{
    // 现有字段
    public string ApiKey { get; set; } = "";
    public string SubApiKey { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // ===== 新增字段 =====
    /// <summary>
    /// 选中的提供商索引
    /// 0=Gemini, 1=ZhipuAI, 2=SiliconFlow, 3=DeepSeek
    /// </summary>
    public int SelectedProviderIndex { get; set; } = 0;
    
    /// <summary>
    /// 选中的模型索引（该提供商内的位置）
    /// </summary>
    public int SelectedModelIndex { get; set; } = 0;
    
    /// <summary>
    /// 当前累计Token使用数
    /// </summary>
    public long TokenUsageCount { get; set; } = 0;
    
    /// <summary>
    /// 上次百万Token警告的时间戳（避免重复警告）
    /// </summary>
    public DateTime LastTokenWarningAt { get; set; } = DateTime.MinValue;
}
```

#### B. 新增配置类

```csharp
/// <summary>
/// AI提供商定义
/// </summary>
public class AIProvider
{
    /// <summary>提供商ID，用于系统识别</summary>
    public string Id { get; set; }  // "gemini", "zhipu", "siliconflow", "deepseek"
    
    /// <summary>提供商显示名称</summary>
    public string DisplayName { get; set; }  // "🔵 Google Gemini"
    
    /// <summary>该提供商的所有模型</summary>
    public List<AIModel> Models { get; set; }
}

/// <summary>
/// AI模型定义
/// </summary>
public class AIModel
{
    /// <summary>模型显示名称（带版本/特性说明）</summary>
    public string DisplayName { get; set; }  // "Gemini 2.5 Flash (fastest)"
    
    /// <summary>模型在API中的实际ID</summary>
    public string ModelId { get; set; }  // "gemini-2.5-flash"
}

/// <summary>
/// 模型选择的临时状态
/// 用于记录用户在多步选择过程中的中间状态
/// </summary>
public class ModelSelectionState
{
    /// <summary>用户选择的提供商索引</summary>
    public int SelectedProviderIndex { get; set; }
    
    /// <summary>状态创建时间（用于超时判断）</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

### 2️⃣ 指令交互流程

```
┌─────────────────────────────────────────┐
│   用户: .ai model                       │
└─────────────────────────────────────────┘
                ↓
┌─────────────────────────────────────────┐
│ 系统显示提供商列表                      │
│ 1. 🔵 Google Gemini                    │
│ 2. 🟣 ZhipuAI (智谱)                   │
│ 3. ⚡ SiliconFlow                      │
│ 4. 🔴 DeepSeek                         │
│ focus="ai_model_select_provider"        │
└─────────────────────────────────────────┘
                ↓
┌─────────────────────────────────────────┐
│ 用户输入: 1 (选择Gemini)                │
└─────────────────────────────────────────┘
                ↓
┌─────────────────────────────────────────┐
│ 系统显示Gemini的模型列表               │
│ 1. Gemini 2.5 Flash (fastest)          │
│ 2. Gemini 2.0 Flash                    │
│ focus="ai_model_select_model"           │
└─────────────────────────────────────────┘
                ↓
┌─────────────────────────────────────────┐
│ 用户输入: 1 (选择Gemini 2.5 Flash)     │
└─────────────────────────────────────────┘
                ↓
┌─────────────────────────────────────────┐
│ 系统确认选择                            │
│ ✅ 提供商: 🔵 Google Gemini            │
│ ✅ 模型: Gemini 2.5 Flash               │
│ 💡 请发送: .ai api <你的Gemini API_KEY>│
│ focus=None (清除)                       │
│ SelectedProviderIndex=0, ModelIndex=0   │
└─────────────────────────────────────────┘
```

### 3️⃣ Token计数逻辑

```
API 调用
  ↓
获取响应
  ↓
解析 usage.prompt_tokens + usage.completion_tokens
  ↓
调用 UpdateTokenUsage(userId, promptTokens, completionTokens)
  ↓
累加 TokenUsageCount
  ↓
检查: TokenUsageCount >= 1,000,000 AND 未警告过?
  ├─ 是: 发送私聊警告 + 记录 LastTokenWarningAt
  └─ 否: 继续
  ↓
保存到 UserApiSettings.json
```

### 4️⃣ 解耦设计验证

| 组件 | 实现方式 | 验证依据 |
|------|--------|--------|
| **模型定义** | aimod内部 AVAILABLE_PROVIDERS | ✅ 无外部依赖 |
| **私聊通知** | `_context.SendPrivateMessage()` | ✅ 使用IModContext接口 |
| **Focus管理** | `MessageDistribution.SetUserFocus()` | ✅ 通过单例访问 |
| **Token存储** | UserApiSettings.json（aimod私有） | ✅ 独立存储 |
| **编译** | AIMod.csproj独立编译 | ✅ 无主程序依赖 |

---

## 实装阶段

### 🔵 阶段1：数据模型（2-3小时）

**任务**:
1. 在 `AIMod.cs` 末尾添加4个新类
   - 扩展 `UserApiSetting` (+4字段)
   - 新增 `AIProvider` 类
   - 新增 `AIModel` 类
   - 新增 `ModelSelectionState` 类

2. 在 `AIMod` 类中添加静态字段
   - `private static readonly List<AIProvider> AVAILABLE_PROVIDERS`
   - `private readonly Dictionary<long, ModelSelectionState> _modelSelectionStates`

3. 初始化 AVAILABLE_PROVIDERS 静态列表

**影响范围**: 数据类定义，无行为改变

---

### 🟢 阶段2：指令路由与主函数（3-4小时）

**任务**:
1. 改写 `OnPrivateMessage()` 方法
   - 重构现有的路由逻辑
   - 新增 `.ai model` 指令分支

2. 实现4个新方法
   - `HandleAiModelCommand()` - 显示列表并进入focus
   - `HandleModelProviderSelection()` - 处理提供商选择
   - `HandleModelModelSelection()` - 处理模型选择
   - `ShowModelSelectionMenu()` - 辅助方法（展示列表）

**焦点**:
- Focus状态管理正确
- 临时状态保存与清理
- 用户输入验证

---

### 🟠 阶段3：Token计数（2-3小时）

**任务**:
1. 实现 `UpdateTokenUsage()` 方法
   - 累加token数
   - 检查百万阈值
   - 发送警告

2. 在所有API调用处添加token更新
   - `CallOpenAiCompatibleApi()` 
   - `GetGeminiResponse()`
   - `GetZhipuAIResponse()`
   - `GetSiliconFlowResponse()`
   - `GetDeepSeekResponse()`
   - `CallEmbeddingApiAsync()`

3. 处理缺失token信息的情况
   - 从响应头提取
   - 或进行估算

**关键**: 需要获取userId上下文传入UpdateTokenUsage()

---

### 🟡 阶段4：数据持久化（1-2小时）

**任务**:
1. 升级 `LoadUserApiSettings()` 方法
   - 添加JSON版本检查
   - 新字段取默认值（向后兼容）
   - 处理迁移逻辑

2. 升级 `SaveUserApiSettings()` 方法
   - 确保新字段被保存

3. 修复 `UpdateUserApiSetting()` 使用
   - 确保调用时保留原有字段值

**向后兼容**: 旧JSON自动升级，无数据丢失

---

### 🔵 阶段5：完整测试（2-3小时）

**测试场景**:
```
✓ 基础流程: .ai model → 选provider → 选model → 保存
✓ 提供商列表: 显示4个提供商
✓ 模型列表: 每个提供商显示其模型
✓ 输入验证: 无效输入处理
✓ 回退/取消: back/quit逻辑
✓ Token计数: API调用后准确累加
✓ 百万警告: 达到阈值发送私聊
✓ 数据持久化: 重启后配置保留
✓ 解耦验证: 无主程序调用
```

---

## 代码实现

### 第一部分：类定义和静态初始化

**位置**: `Mods/AIMod/AIMod.cs` 末尾

```csharp
// ===== 模型配置类 =====

/// <summary>
/// AI提供商定义
/// </summary>
public class AIProvider
{
    public string Id { get; set; }
    public string DisplayName { get; set; }
    public List<AIModel> Models { get; set; }
}

/// <summary>
/// AI模型定义
/// </summary>
public class AIModel
{
    public string DisplayName { get; set; }
    public string ModelId { get; set; }
}

/// <summary>
/// 模型选择的临时状态
/// </summary>
public class ModelSelectionState
{
    public int SelectedProviderIndex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ===== 扩展用户设置 =====

public class UserApiSetting
{
    public string ApiKey { get; set; } = "";
    public string SubApiKey { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    public int SelectedProviderIndex { get; set; } = 0;
    public int SelectedModelIndex { get; set; } = 0;
    public long TokenUsageCount { get; set; } = 0;
    public DateTime LastTokenWarningAt { get; set; } = DateTime.MinValue;
}

public class ActiveGroupApiContext
{
    // ... existing code ...
}
```

### 第二部分：在AIMod类中添加字段

**位置**: `AIMod` 类的字段声明区

```csharp
private readonly ConcurrentDictionary<long, UserApiSetting> _userApiSettings = new();

// ===== 新增字段 =====
/// <summary>用户模型选择的临时状态（中间态）</summary>
private readonly Dictionary<long, ModelSelectionState> _modelSelectionStates = new();

/// <summary>静态初始化的模型列表</summary>
private static readonly List<AIProvider> AVAILABLE_PROVIDERS = new()
{
    new AIProvider
    {
        Id = "gemini",
        DisplayName = "🔵 Google Gemini",
        Models = new List<AIModel>
        {
            new AIModel { DisplayName = "Gemini 2.5 Flash (fastest)", ModelId = "gemini-2.5-flash" },
            new AIModel { DisplayName = "Gemini 2.0 Flash", ModelId = "gemini-2.0-flash" },
        }
    },
    new AIProvider
    {
        Id = "zhipu",
        DisplayName = "🟣 ZhipuAI (智谱)",
        Models = new List<AIModel>
        {
            new AIModel { DisplayName = "GLM-4.7 Flash (recommended)", ModelId = "glm-4.7-flash" },
        }
    },
    new AIProvider
    {
        Id = "siliconflow",
        DisplayName = "⚡ SiliconFlow",
        Models = new List<AIModel>
        {
            new AIModel { DisplayName = "Qwen 3 8B", ModelId = "Qwen/Qwen3-8B" },
        }
    },
    new AIProvider
    {
        Id = "deepseek",
        DisplayName = "🔴 DeepSeek",
        Models = new List<AIModel>
        {
            new AIModel { DisplayName = "DeepSeek Chat", ModelId = "deepseek-chat" },
        }
    }
};
```

### 第三部分：指令处理

**位置**: 改写 `OnPrivateMessage()` 和新增方法

```csharp
public ModMessageResult? OnPrivateMessage(long userId, string content)
{
    if (string.IsNullOrWhiteSpace(content))
        return null;

    var text = content.Trim();
    if (text.StartsWith("。"))
        text = "." + text[1..];

    var parts = text.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2)
        return null;

    var root = parts[0].TrimStart('.').ToLowerInvariant();
    var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
    
    if (root != "ai")
        return null;

    // ===== 新的路由逻辑 =====
    return sub switch
    {
        "api" => HandleAiApiCommand(userId, parts.Length > 2 ? parts[2] : ""),
        "subapi" => HandleAiSubapiCommand(userId, parts.Length > 2 ? parts[2] : ""),
        "model" => HandleAiModelCommand(userId, parts.Length > 2 ? parts[2] : ""),
        _ => null
    };
}

private string? HandleAiApiCommand(long userId, string args)
{
    // ... existing code from original OnPrivateMessage ...
}

private string? HandleAiSubapiCommand(long userId, string args)
{
    // ... existing code from original OnPrivateMessage ...
}

/// <summary>
/// 处理 .ai model 指令
/// </summary>
private string? HandleAiModelCommand(long userId, string args)
{
    var msgDistribution = MessageDistribution.GetInstance();
    var focusType = msgDistribution?.GetUserFocus(userId.ToString());
    
    if (!string.IsNullOrEmpty(focusType))
    {
        if (focusType == "ai_model_select_provider")
            return HandleModelProviderSelection(userId, args);
        else if (focusType == "ai_model_select_model")
            return HandleModelModelSelection(userId, args);
    }

    // 初始状态：显示提供商列表，进入focus
    var displayText = new StringBuilder();
    displayText.AppendLine("🎯 可选的AI模型提供商：");
    displayText.AppendLine();
    
    for (int i = 0; i < AVAILABLE_PROVIDERS.Count; i++)
    {
        var provider = AVAILABLE_PROVIDERS[i];
        displayText.AppendLine($"{i + 1}. {provider.DisplayName}");
    }
    
    displayText.AppendLine();
    displayText.AppendLine("📌 请输入提供商编号（1-4）");
    displayText.AppendLine("💬 或输入 quit 取消");

    msgDistribution?.SetUserFocus(userId.ToString(), "ai_model_select_provider");
    
    return displayText.ToString();
}

/// <summary>
/// 处理提供商选择
/// </summary>
private string? HandleModelProviderSelection(long userId, string input)
{
    var msgDistribution = MessageDistribution.GetInstance();
    
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        msgDistribution?.ClearUserFocus(userId.ToString());
        return "✖️ 已取消模型选择";
    }

    if (!int.TryParse(input.Trim(), out var providerIdx) || 
        providerIdx < 1 || providerIdx > AVAILABLE_PROVIDERS.Count)
    {
        return "❌ 无效的提供商编号，请输入1-4的数字";
    }

    var selectedProvider = AVAILABLE_PROVIDERS[providerIdx - 1];
    
    // 保存中间状态
    _modelSelectionStates[userId] = new ModelSelectionState 
    { 
        SelectedProviderIndex = providerIdx - 1 
    };

    // 显示该提供商的模型列表
    var displayText = new StringBuilder();
    displayText.AppendLine($"✅ 选中提供商：{selectedProvider.DisplayName}");
    displayText.AppendLine();
    displayText.AppendLine("📦 可选的模型：");
    displayText.AppendLine();
    
    for (int i = 0; i < selectedProvider.Models.Count; i++)
    {
        var model = selectedProvider.Models[i];
        displayText.AppendLine($"{i + 1}. {model.DisplayName}");
    }
    
    displayText.AppendLine();
    displayText.AppendLine("📌 请输入模型编号");
    displayText.AppendLine("💬 或输入 back 返回，输入 quit 取消");

    msgDistribution?.SetUserFocus(userId.ToString(), "ai_model_select_model");
    
    return displayText.ToString();
}

/// <summary>
/// 处理模型选择
/// </summary>
private string? HandleModelModelSelection(long userId, string input)
{
    var msgDistribution = MessageDistribution.GetInstance();
    
    if (input.Equals("back", StringComparison.OrdinalIgnoreCase))
    {
        msgDistribution?.SetUserFocus(userId.ToString(), "ai_model_select_provider");
        return HandleAiModelCommand(userId, "");
    }

    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        msgDistribution?.ClearUserFocus(userId.ToString());
        _modelSelectionStates.Remove(userId);
        return "✖️ 已取消模型选择";
    }

    if (!_modelSelectionStates.TryGetValue(userId, out var state))
    {
        msgDistribution?.ClearUserFocus(userId.ToString());
        return "❌ 选择状态已过期，请重新开始";
    }

    var selectedProvider = AVAILABLE_PROVIDERS[state.SelectedProviderIndex];
    
    if (!int.TryParse(input.Trim(), out var modelIdx) || 
        modelIdx < 1 || modelIdx > selectedProvider.Models.Count)
    {
        return $"❌ 无效的模型编号，请输入1-{selectedProvider.Models.Count}的数字";
    }

    var selectedModel = selectedProvider.Models[modelIdx - 1];

    // 显示确认信息
    var displayText = new StringBuilder();
    displayText.AppendLine("✅ 确认选择：");
    displayText.AppendLine($"提供商：{selectedProvider.DisplayName}");
    displayText.AppendLine($"模型：{selectedModel.DisplayName}");
    displayText.AppendLine();
    displayText.AppendLine("🔐 需要您的 API Key 来完成配置");
    displayText.AppendLine($"请发送：.ai api <你的{selectedProvider.DisplayName}API_KEY>");
    displayText.AppendLine();
    displayText.AppendLine("💡 完成后将自动切换到此模型");

    // 保存选择
    UpdateUserApiSetting(userId, s =>
    {
        s.SelectedProviderIndex = state.SelectedProviderIndex;
        s.SelectedModelIndex = modelIdx - 1;
    });

    // 清除focus和临时状态
    msgDistribution?.ClearUserFocus(userId.ToString());
    _modelSelectionStates.Remove(userId);

    return displayText.ToString();
}
```

### 第四部分：Token计数

**位置**: 在AIMod类中添加新方法

```csharp
/// <summary>
/// 更新用户的Token使用数，并在达到百万时发送警告
/// </summary>
private void UpdateTokenUsage(long userId, int promptTokens, int completionTokens)
{
    var tokenCount = promptTokens + completionTokens;
    
    var setting = GetUserApiSetting(userId);
    var previousCount = setting.TokenUsageCount;
    setting.TokenUsageCount += tokenCount;
    
    _context.Log(LogLevel.Info, 
        $"[AIMod] User {userId} token usage: +{tokenCount} (prev: {previousCount}, total: {setting.TokenUsageCount})");
    
    // 检查是否跨越百万Token阈值
    if (previousCount < 1000000 && setting.TokenUsageCount >= 1000000)
    {
        // 发送私聊警告
        var warningMsg = new StringBuilder();
        warningMsg.AppendLine("⚠️ 重要提示：您的AI Token使用已达到 1,000,000！");
        warningMsg.AppendLine();
        warningMsg.AppendLine($"当前累计使用量：{setting.TokenUsageCount:N0} tokens");
        warningMsg.AppendLine();
        warningMsg.AppendLine("请核查花费，如果检查到不正常用量，请联系骰主");
        
        _context.SendPrivateMessage(userId, warningMsg.ToString());
        
        setting.LastTokenWarningAt = DateTime.UtcNow;
        
        _context.Log(LogLevel.Warn, $"[AIMod] Million token warning sent to user {userId}");
    }
    
    UpdateUserApiSetting(userId, s =>
    {
        s.TokenUsageCount = setting.TokenUsageCount;
        if (setting.LastTokenWarningAt > DateTime.MinValue)
            s.LastTokenWarningAt = setting.LastTokenWarningAt;
    });
}

/// <summary>
/// 从API响应提取token信息并更新
/// </summary>
private void ExtractAndUpdateTokenUsage(long userId, JsonElement responseBody)
{
    try
    {
        if (responseBody.TryGetProperty("usage", out var usageElement))
        {
            var promptTokens = usageElement.TryGetProperty("prompt_tokens", out var pt) 
                ? pt.GetInt32() : 0;
            var completionTokens = usageElement.TryGetProperty("completion_tokens", out var ct) 
                ? ct.GetInt32() : 0;
            
            if (promptTokens > 0 || completionTokens > 0)
            {
                UpdateTokenUsage(userId, promptTokens, completionTokens);
            }
        }
    }
    catch (Exception ex)
    {
        _context.Log(LogLevel.Error, $"[AIMod] Error extracting token usage: {ex.Message}");
    }
}
```

### 第五部分：数据持久化升级

**位置**: 升级 `LoadUserApiSettings()` 方法

```csharp
private void LoadUserApiSettings()
{
    if (!File.Exists(_userApiSettingsPath))
        return;

    try
    {
        var json = File.ReadAllText(_userApiSettingsPath);
        var doc = JsonDocument.Parse(json);
        
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (long.TryParse(prop.Name, out var userId))
            {
                var obj = prop.Value;
                var setting = new UserApiSetting
                {
                    ApiKey = obj.TryGetProperty("ApiKey", out var ak) 
                        ? ak.GetString() ?? "" : "",
                    SubApiKey = obj.TryGetProperty("SubApiKey", out var sak) 
                        ? sak.GetString() ?? "" : "",
                    UpdatedAt = obj.TryGetProperty("UpdatedAt", out var ua) 
                        ? DateTime.Parse(ua.GetString() ?? "") 
                        : DateTime.UtcNow,
                    
                    // 新字段（带默认值，向后兼容）
                    SelectedProviderIndex = obj.TryGetProperty("SelectedProviderIndex", out var sp)
                        ? sp.GetInt32()
                        : 0,
                    SelectedModelIndex = obj.TryGetProperty("SelectedModelIndex", out var sm)
                        ? sm.GetInt32()
                        : 0,
                    TokenUsageCount = obj.TryGetProperty("TokenUsageCount", out var tc)
                        ? tc.GetInt64()
                        : 0L,
                    LastTokenWarningAt = obj.TryGetProperty("LastTokenWarningAt", out var ltw)
                        ? DateTime.Parse(ltw.GetString() ?? "")
                        : DateTime.MinValue,
                };
                _userApiSettings[userId] = setting;
            }
        }
        
        _context.Log(LogLevel.Info, 
            $"[AIMod] Loaded {_userApiSettings.Count} user API settings (with new fields)");
    }
    catch (Exception ex)
    {
        _context.Log(LogLevel.Error, $"[AIMod] Error loading user API settings: {ex.Message}");
    }
}
```

---

## 测试场景

### 场景1：完整模型选择流程

```
步骤1: 用户发送 ".ai model"
预期: 显示4个提供商列表，进入focus="ai_model_select_provider"

步骤2: 用户发送 "1"（选Gemini）
预期: 显示Gemini的2个模型，focus="ai_model_select_model"

步骤3: 用户发送 "1"（选Gemini 2.5 Flash）
预期: 显示确认信息，清除focus
预期: SelectedProviderIndex=0, SelectedModelIndex=0

步骤4: 用户发送 ".ai api YOUR_KEY"
预期: 保存API Key

验证: UserApiSettings.json中该用户的配置已更新
```

### 场景2：取消流程

```
步骤1: 用户发送 ".ai model"
步骤2: 用户发送 "quit"
预期: 返回"已取消模型选择"，清除focus

验证: UserFocusStates中无该用户条目
```

### 场景3：回退流程

```
步骤1: 用户发送 ".ai model"
步骤2: 用户发送 "2"（选ZhipuAI）
步骤3: 用户发送 "back"
预期: 返回到提供商列表，focus="ai_model_select_provider"

步骤4: 用户发送 "1"（改选Gemini）
预期: 显示Gemini模型列表
```

### 场景4：百万Token警告

```
步骤1: 模拟用户API调用，TokenUsageCount: 999,900
步骤2: API返回 usage.prompt_tokens=50, completion_tokens=100
预期: UpdateTokenUsage() 被调用
预期: TokenUsageCount: 1,000,050 (> 1,000,000)
预期: 发送私聊警告到用户
预期: LastTokenWarningAt 被设置

步骤3: 再次调用API
预期: Token继续累加
预期: 不再发送警告（LastTokenWarningAt已设置）

验证: UserApiSettings.json中LastTokenWarningAt字段被保存
```

### 场景5：数据持久化

```
步骤1: 用户进行模型选择和API Key设置
步骤2: 调用多次API，TokenUsageCount达到500,000

步骤3: 关闭程序 → LoadUserApiSettings()
预期: 读取JSON，新字段被正确加载
预期: SelectedProviderIndex、TokenUsageCount等保留

验证: 所有新字段数据无丢失
```

### 场景6：输入验证

```
步骤1: 用户发送 ".ai model"（focus=provider_select）
步骤2: 用户发送 "5"（超出范围）
预期: 返回"无效的提供商编号"

步骤3: 用户发送 "abc"（非数字）
预期: 返回"无效的提供商编号"

步骤4: 用户发送 "0"（边界）
预期: 返回"无效的提供商编号"
```

---

## 验收清单

### 功能验收

- [ ] `.ai model` 指令可执行，显示完整提供商列表
- [ ] 用户输入提供商编号后显示对应模型列表
- [ ] 用户输入模型编号后显示确认信息
- [ ] 选择被正确保存到 UserApiSetting
- [ ] `quit` 和 `back` 命令可正常工作
- [ ] 输入验证正确（无效输入提示清晰）

### Token计数验收

- [ ] API调用后 TokenUsageCount 正确累加
- [ ] 百万Token阈值检测准确
- [ ] 私聊警告发送正确（仅发送一次）
- [ ] 警告内容清晰、有用
- [ ] Token数据正确持久化

### 解耦验收

- [ ] AIMod 可独立编译通过
- [ ] 无对主程序内部类的直接调用
- [ ] 仅使用 IModContext 接口进行通信
- [ ] 通过 MessageDistribution.GetInstance() 获取单例（不是依赖注入但可接受）
- [ ] Token 数据存储在 UserApiSettings.json（AIMod私有）

### 数据持久化验收

- [ ] 新字段在 JSON 中正确保存
- [ ] 旧 JSON 文件可自动升级（新字段取默认值）
- [ ] 重启程序后所有配置保留
- [ ] 无数据丢失

### 代码质量

- [ ] 代码格式统一（与AIMod.cs现有代码一致）
- [ ] 注释完整（中英混合，功能清晰）
- [ ] 异常处理完善
- [ ] 性能无下降
- [ ] 无内存泄漏（字典及时清理）

---

## 相关源码位置

| 模块 | 文件路径 |
|------|--------|
| AIMod主类 | `Mods/AIMod/AIMod.cs` |
| Focus状态管理 | `MDiceV2.Core/Models/MessageDistribution.cs` |
| IModContext接口 | `MDiceV2.Interfaces/Mod/IModContext.cs` |
| 用户配置存储 | `UserApiSettings.json`（运行时生成） |

---

## 附录：常见问题

### Q1: 如何避免状态超时？

**A**: `ModelSelectionState` 包含 `CreatedAt` 时间戳。可在验证时检查：
```csharp
if (DateTime.UtcNow - state.CreatedAt > TimeSpan.FromMinutes(5))
{
    msgDistribution?.ClearUserFocus(userId.ToString());
    _modelSelectionStates.Remove(userId);
    return "选择超时，请重新开始";
}
```

### Q2: 如何处理模型列表的更新？

**A**: 将 `AVAILABLE_PROVIDERS` 改为可热更新的格式（从配置文件加载），或在 `OnLoad()` 中初始化。

### Q3: Token计数如何处理embedding API？

**A**: Embedding API 通常也返回 `usage` 字段，可使用同一个 `ExtractAndUpdateTokenUsage()` 方法。

### Q4: 如果用户从未设置过模型，会怎样？

**A**: `SelectedProviderIndex` 默认为0（Gemini），`SelectedModelIndex` 默认为0（Gemini 2.5 Flash）。系统会使用这些默认值。

### Q5: Token计数精度如何保证？

**A**: 
1. 每次API调用立即更新（不延迟）
2. 从API响应的 `usage` 字段精确获取
3. 持久化保存到 JSON（单线程写入）
4. 定期验证可加入日志audit

---

## 更新历史

| 版本 | 日期 | 内容 |
|------|------|------|
| v1.0 | 2026-05-28 | 初版计划 |
| v2.0 | 2026-05-28 | 完整实装方案，含代码示例 |

---

**制作者**: AI Assistant  
**审核状态**: 待审核  
**优先级**: High  
**预计工期**: 10-15小时
