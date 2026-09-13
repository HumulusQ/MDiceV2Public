using System.Text.Json;
using System.Text.Json.Nodes;
using AIMod;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg.SemanticGraph;

public sealed class SemanticGraphFoldExtractor
{
    private readonly IModContext _context;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly LlmCallTracker? _llmCallTracker;

    public SemanticGraphFoldExtractor(
        IModContext context,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        LlmCallTracker? llmCallTracker = null)
    {
        _context = context;
        _apiCaller = apiCaller;
        _llmCallTracker = llmCallTracker;
    }

    public async Task<GraphMemoryFoldResult> ExtractAsync(
        TrpgScope scope,
        string characterId,
        IReadOnlyList<ChatHistoryEntry> foldWindow,
        CancellationToken cancellationToken = default)
    {
        if (foldWindow.Count == 0)
            return new GraphMemoryFoldResult();

        var prompt = BuildPrompt(characterId, foldWindow);
        var messages = new List<ChatMessage>
        {
            new("system", AimodPromptPrefixes.BackendCommonPrefixV1),
            new("user", prompt)
        };

        try
        {
            using var _ = LlmCallTracker.PushAmbientTurnContext(
                $"graph-fold:{scope.GroupId}:{characterId}",
                $"graph-fold:{foldWindow.First().Id}-{foldWindow.Last().Id}",
                "SemanticGraphFoldExtractor");

            var response = await CallTrackedAsync(scope, characterId, messages);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(response))
            {
                return new GraphMemoryFoldResult
                {
                    ParseFailed = true,
                    Error = "empty response"
                };
            }

            var result = TryParse(response, out var parsed, out var error)
                ? parsed
                : new GraphMemoryFoldResult
                {
                    ParseFailed = true,
                    RawResponse = response,
                    Error = error
                };

            if (result.ParseFailed)
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] SemanticGraphFoldExtractor parse failed: {result.Error}");

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] SemanticGraphFoldExtractor failed: {ex.Message}");
            return new GraphMemoryFoldResult
            {
                ParseFailed = true,
                Error = ex.Message
            };
        }
    }

    private static string BuildPrompt(string characterId, IReadOnlyList<ChatHistoryEntry> foldWindow)
    {
        var transcript = string.Join(
            "\n",
            foldWindow
                .OrderBy(entry => entry.CreatedAt)
                .Select(entry => $"- id={entry.Id}; speaker={entry.SpeakerName}; type={entry.MessageType}; text={entry.Content}"));

        return $$"""
你正在为 TRPG 角色生成长期语义记忆候选。

当前折叠角色：{{characterId}}

只输出合法 JSON，不要 markdown，不要解释，不要额外字段。

输出格式：
{
  "memory_candidates": [
    {
      "summary": "",
      "surface_tokens": [],
      "name_tokens": [],
      "topic_tokens": [],
      "scene_tokens": [],
      "assigned_importance": 0,
      "source_message_ids": [],
      "raw_excerpt": "",
      "stance": ""
    }
  ]
}

规则：
- summary 必须保留信息来源语气，例如 “GM确认”、“NPC声称”、“角色怀疑”、“PL讨论”。
- 只保留值得长期保存的剧情、人物、地点、线索、物品、关系、传闻。
- 不输出 timeline、quest、情感标签、实体合并判断。
- 不把弱联想写成已确认事实。
- token 只保留检索用核心词。
- 每条 memory 最多 3 个 surface_tokens，最多 2 个 name_tokens，最多 2 个 topic_tokens，最多 1 个 scene_tokens。
- assigned_importance 范围 0-100。
- 如果没有值得长期保存的信息，输出空数组。
- 输出必须是 JSON。

折叠窗口：
{{transcript}}
""";
    }

    private Task<string?> CallTrackedAsync(TrpgScope scope, string characterId, List<ChatMessage> messages)
    {
        if (_llmCallTracker != null)
            return _llmCallTracker.CallAsync(scope, characterId, messages, "MemoryWatchdog", "SemanticGraphFoldExtract", _apiCaller);

        return _apiCaller(messages);
    }

    private static bool TryParse(string response, out GraphMemoryFoldResult result, out string error)
    {
        result = new GraphMemoryFoldResult
        {
            RawResponse = response ?? ""
        };
        error = "";

        try
        {
            var json = ExtractJson(response);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
            {
                error = "root is not an object";
                return false;
            }

            if (root["memory_candidates"] is not JsonArray memoryCandidates)
            {
                error = "memory_candidates missing";
                return false;
            }

            foreach (var node in memoryCandidates.OfType<JsonObject>())
            {
                var candidate = new GraphMemoryCandidate
                {
                    Summary = GetString(node, "summary"),
                    SurfaceTokens = GetStringArray(node, "surface_tokens", 3),
                    NameTokens = GetStringArray(node, "name_tokens", 2),
                    TopicTokens = GetStringArray(node, "topic_tokens", 2),
                    SceneTokens = GetStringArray(node, "scene_tokens", 1),
                    AssignedImportance = Math.Clamp(GetInt(node, "assigned_importance"), 0, 100),
                    SourceMessageIds = GetStringArray(node, "source_message_ids", 12),
                    RawExcerpt = GetString(node, "raw_excerpt"),
                    Stance = GetString(node, "stance")
                };

                if (string.IsNullOrWhiteSpace(candidate.Summary))
                    continue;

                result.MemoryCandidates.Add(candidate);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ExtractJson(string response)
    {
        var cleaned = (response ?? "").Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstLineBreak = cleaned.IndexOf('\n');
            if (firstLineBreak >= 0)
                cleaned = cleaned[(firstLineBreak + 1)..].Trim();
            if (cleaned.EndsWith("```", StringComparison.Ordinal))
                cleaned = cleaned[..^3].Trim();
        }

        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start >= 0 && end > start)
            return cleaned[start..(end + 1)];
        return cleaned;
    }

    private static string GetString(JsonObject node, string propertyName)
        => node[propertyName]?.GetValue<string>()?.Trim() ?? "";

    private static int GetInt(JsonObject node, string propertyName)
    {
        if (node[propertyName] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var intValue))
            return intValue;
        if (value.TryGetValue<double>(out var doubleValue))
            return (int)Math.Round(doubleValue);
        if (int.TryParse(value.ToString(), out var parsed))
            return parsed;
        return 0;
    }

    private static List<string> GetStringArray(JsonObject node, string propertyName, int maxCount)
    {
        if (node[propertyName] is not JsonArray array)
            return new List<string>();

        return array
            .Select(item => item?.GetValue<string>()?.Trim() ?? "")
            .Where(item => item.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
    }
}
