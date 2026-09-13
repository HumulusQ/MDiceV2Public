using System.Text;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg.SemanticGraph;

public sealed class ThoughtStateMaintainer
{
    private readonly IModContext _context;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly LlmCallTracker? _llmCallTracker;

    public ThoughtStateMaintainer(
        IModContext context,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        LlmCallTracker? llmCallTracker = null)
    {
        _context = context;
        _apiCaller = apiCaller;
        _llmCallTracker = llmCallTracker;
    }

    public async Task<string> UpdateAsync(
        TrpgScope scope,
        string characterId,
        string oldThoughtText,
        GraphRecallResult graphRecall,
        string latestText,
        IReadOnlyList<ChatHistoryEntry> recentHistory,
        string currentSceneText,
        CancellationToken cancellationToken = default)
    {
        var fallback = NormalizeFallback(oldThoughtText);
        var messages = new List<ChatMessage>
        {
            new("system", AimodPromptPrefixes.BackendCommonPrefixV1),
            new("user", BuildPrompt(oldThoughtText, graphRecall, latestText, recentHistory, currentSceneText))
        };

        try
        {
            var response = await CallTrackedAsync(scope, characterId, messages);
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeResponse(response);
            return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] ThoughtStateMaintainer failed: {ex.Message}");
            return fallback;
        }
    }

    private static string BuildPrompt(
        string oldThoughtText,
        GraphRecallResult graphRecall,
        string latestText,
        IReadOnlyList<ChatHistoryEntry> recentHistory,
        string currentSceneText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你正在维护一个 TRPG 角色的“即时心理活动”文本区。");
        sb.AppendLine("它记录角色当前没有说出口的思路、行动倾向、自我提醒、短期判断与避免重复的提醒。");
        sb.AppendLine("它不是世界事实，不能替代 GM 叙述，也不要把情绪当成主内容。");
        sb.AppendLine();
        sb.AppendLine("规则：");
        sb.AppendLine("- 只输出自然语言段落，不要 JSON，不要列表，不要解释。");
        sb.AppendLine("- 可以删除过时想法，合并重复想法，补充新的短期判断与行动倾向。");
        sb.AppendLine("- 不要把未经 GM 确认的猜测写成事实。");
        sb.AppendLine("- 不要把情绪描写写成主内容。");
        sb.AppendLine("- 长度控制在 80 到 240 字。");
        sb.AppendLine();
        sb.AppendLine("[旧心理活动]");
        sb.AppendLine(string.IsNullOrWhiteSpace(oldThoughtText) ? "无" : oldThoughtText.Trim());
        sb.AppendLine();
        sb.AppendLine("[当前场景]");
        sb.AppendLine(string.IsNullOrWhiteSpace(currentSceneText) ? "无" : currentSceneText.Trim());
        sb.AppendLine();
        sb.AppendLine("[本轮联想记忆]");
        sb.AppendLine(graphRecall.ToPromptString(5));
        sb.AppendLine();
        sb.AppendLine("[最新消息]");
        sb.AppendLine(string.IsNullOrWhiteSpace(latestText) ? "无" : latestText.Trim());
        sb.AppendLine();
        sb.AppendLine("[最近对话]");
        foreach (var entry in recentHistory.OrderBy(h => h.CreatedAt).TakeLast(6))
            sb.AppendLine($"- {entry.SpeakerName}: {entry.Content}");
        sb.AppendLine();
        sb.AppendLine("请直接重写新的即时心理活动。");
        return sb.ToString();
    }

    private Task<string?> CallTrackedAsync(TrpgScope scope, string characterId, List<ChatMessage> messages)
    {
        if (_llmCallTracker != null)
            return _llmCallTracker.CallAsync(scope, characterId, messages, "ThoughtStateMaintainer", "UpdateThoughtState", _apiCaller);

        return _apiCaller(messages);
    }

    private static string NormalizeFallback(string text)
        => string.IsNullOrWhiteSpace(text) ? "无" : text.Trim();

    private static string NormalizeResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "";

        var normalized = response.Trim();
        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = normalized.IndexOf('\n');
            if (firstBreak >= 0)
                normalized = normalized[(firstBreak + 1)..].Trim();
            if (normalized.EndsWith("```", StringComparison.Ordinal))
                normalized = normalized[..^3].Trim();
        }

        normalized = normalized.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length > 240)
            normalized = normalized[..240].Trim();
        return normalized;
    }
}
