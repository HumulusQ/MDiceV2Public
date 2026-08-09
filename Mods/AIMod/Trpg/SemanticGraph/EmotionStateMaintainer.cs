using System.Text;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg.SemanticGraph;

public sealed class EmotionStateMaintainer
{
    private readonly IModContext _context;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly LlmCallTracker? _llmCallTracker;

    public EmotionStateMaintainer(
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
        string oldEmotionText,
        string currentThoughtText,
        GraphRecallResult graphRecall,
        string latestText,
        IReadOnlyList<ChatHistoryEntry> recentHistory,
        CancellationToken cancellationToken = default)
    {
        var fallback = NormalizeFallback(oldEmotionText);
        var messages = new List<ChatMessage>
        {
            new("system", AimodPromptPrefixes.BackendCommonPrefixV1),
            new("user", BuildPrompt(oldEmotionText, currentThoughtText, graphRecall, latestText, recentHistory))
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
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] EmotionStateMaintainer failed: {ex.Message}");
            return fallback;
        }
    }

    private static string BuildPrompt(
        string oldEmotionText,
        string currentThoughtText,
        GraphRecallResult graphRecall,
        string latestText,
        IReadOnlyList<ChatHistoryEntry> recentHistory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你正在维护一个 TRPG 角色的“即时情感叙述”文本区。");
        sb.AppendLine("它记录角色当前的情绪残留、态度、压力、警觉、信任或怀疑，以及这些感受如何影响语气与表达倾向。");
        sb.AppendLine("它不是世界事实，不要写行动计划。");
        sb.AppendLine();
        sb.AppendLine("规则：");
        sb.AppendLine("- 只输出自然语言段落，不要 JSON，不要列表，不要解释。");
        sb.AppendLine("- 不要使用数字打分、标签或强度等级。");
        sb.AppendLine("- 可以删除已经淡化的情绪，合并重复情绪，补充新的情绪残留。");
        sb.AppendLine("- 不要把怀疑、恐惧、亲近等情绪写成已确认事实。");
        sb.AppendLine("- 不要写行动计划。");
        sb.AppendLine("- 长度控制在 80 到 240 字。");
        sb.AppendLine();
        sb.AppendLine("[旧情感叙述]");
        sb.AppendLine(string.IsNullOrWhiteSpace(oldEmotionText) ? "无" : oldEmotionText.Trim());
        sb.AppendLine();
        sb.AppendLine("[当前心理活动]");
        sb.AppendLine(string.IsNullOrWhiteSpace(currentThoughtText) ? "无" : currentThoughtText.Trim());
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
        sb.AppendLine("请直接重写新的即时情感叙述。");
        return sb.ToString();
    }

    private Task<string?> CallTrackedAsync(TrpgScope scope, string characterId, List<ChatMessage> messages)
    {
        if (_llmCallTracker != null)
            return _llmCallTracker.CallAsync(scope, characterId, messages, "EmotionStateMaintainer", "UpdateEmotionState", _apiCaller);

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
