using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AIMod.Trpg;

/// <summary>
/// 状态增量提取器：使用 AI 从 GM 输入中提取场景与人物变动（Delta）
/// </summary>
public class StateDeltaExtractor
{
    private readonly IModContext _context;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly LlmCallTracker? _llmCallTracker;

    public StateDeltaExtractor(
        IModContext context,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        LlmCallTracker? llmCallTracker = null)
    {
        _context = context;
        _apiCaller = apiCaller;
        _llmCallTracker = llmCallTracker;
    }

    /// <summary>
    /// 从 GM 输入中提取状态增量
    /// </summary>
    public async Task<StateDelta?> ExtractDeltaAsync(long groupId, string characterId, string latestGmText, List<ChatHistoryEntry> recentHistory)
    {
        var prompt = BuildDeltaPrompt(latestGmText, recentHistory);
        var messages = new List<ChatMessage>
        {
            new("system", $"{AimodPromptPrefixes.BackendCommonPrefixV1}\n\n你是一个 TRPG 状态分析助手。仅从 GM 叙述中提取场景与人物变动，输出严格 JSON，不要包含任何其他文字。"),
            new("user", prompt)
        };

        try
        {
            var scope = TrpgScope.Create(0, groupId, "legacy-state-delta");
            var json = await (_llmCallTracker ?? throw new InvalidOperationException("LlmCallTracker is required for AIMod LLM calls."))
                .CallAsync(scope, characterId, messages, "StateDeltaExtractor", "SceneEntityDelta", _apiCaller);
            if (string.IsNullOrWhiteSpace(json))
            {
                _context.Log(LogLevel.Warn, "[AIMod:TRPG] StateDelta AI 返回空，将回退到正则提取");
                return null;
            }

            var delta = ParseDeltaJson(json);
            if (delta == null)
            {
                _context.Log(LogLevel.Warn, "[AIMod:TRPG] StateDelta JSON 解析失败，将回退到正则提取");
                return null;
            }

            _context.Log(LogLevel.Info, $"[AIMod:TRPG] StateDelta 提取成功 (Group={groupId}, Char={characterId}): LocationUpdated={delta.LocationUpdated}, Enter={delta.EntitiesEnter.Count}, Exit={delta.EntitiesExit.Count}");
            return delta;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] StateDelta AI 调用失败: {ex.Message}，将回退到正则提取");
            return null;
        }
    }

    private string BuildDeltaPrompt(string latestGmText, List<ChatHistoryEntry> recentHistory)
    {
        var historyText = string.Join("\n", recentHistory.TakeLast(5).Select(e => e.Content));
        return $@"从以下 GM 叙述中提取场景与人物变动（仅输出变更，不要总结完整状态）。

========================
【最近上下文】
========================
{historyText}

========================
【最新 GM 叙述】
========================
{latestGmText}

========================
【输出格式】
========================
{{
  ""location_updated"": false,
  ""new_location"": null,
  ""entities_enter"": [],
  ""entities_exit"": []
}}

========================
【提取规则】
========================
- location_updated: 仅当场景明确切换时为 true（如“来到”、“进入”、“抵达”等）。
- new_location: 场景名称（简洁，不超过 20 字）。
- entities_enter: 新进入场景的人物/实体名称列表（仅包含本条叙述中明确出现的）。
- entities_exit: 离开场景的人物/实体名称列表（仅包含本条叙述中明确离开的）。
- 不要把环境描述、物品、抽象概念当作实体（如“黑暗”、“迷雾”、“车轮行驶”等）。";
    }

    private StateDelta? ParseDeltaJson(string jsonText)
    {
        try
        {
            var cleaned = jsonText.Trim();
            if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(7);
            else if (cleaned.StartsWith("```"))
                cleaned = cleaned.Substring(3);
            if (cleaned.EndsWith("```"))
                cleaned = cleaned.Substring(0, cleaned.Length - 3);
            cleaned = cleaned.Trim();

            var node = JsonSerializer.Deserialize<JsonNode>(cleaned);
            if (node == null) return null;

            return new StateDelta
            {
                LocationUpdated = node["location_updated"]?.GetValue<bool>() ?? false,
                NewLocation = node["new_location"]?.ToString(),
                EntitiesEnter = node["entities_enter"]?.AsArray().Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() ?? new(),
                EntitiesExit = node["entities_exit"]?.AsArray().Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() ?? new()
            };
        }
        catch (JsonException ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] StateDelta JSON 解析异常: {ex.Message}");
            return null;
        }
    }
}
