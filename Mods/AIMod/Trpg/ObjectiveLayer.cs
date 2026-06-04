using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 第一层：Objective Layer - 目标层管理
/// 职责：管理目标生命周期，并在进入 ActionAgent 前做展示层过滤。
/// </summary>
public class ObjectiveLayer
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;

    public ObjectiveLayer(IModContext context, ChatDatabase db)
    {
        _context = context;
        _db = db;
    }

    public async Task AddObjectiveAsync(TrpgScope scope, string characterId, string description, QuestPriority priority = QuestPriority.Normal, string? sourceSceneId = null)
    {
        var normalized = NormalizeObjectiveText(description);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var existing = await FindBestMatchingObjectiveAsync(scope, characterId, normalized, sourceSceneId, includeClosed: false);
        if (existing != null)
        {
            existing.Description = PreferLonger(existing.Description, description.Trim());
            existing.Priority = MaxPriority(existing.Priority, priority);
            existing.Status = QuestStatus.Active;
            existing.HiddenFromPrompt = false;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.LastTouchedAt = existing.UpdatedAt;
            if (!string.IsNullOrWhiteSpace(sourceSceneId))
                existing.LastMentionedSceneId = sourceSceneId;
            await _db.UpdateQuestAsync(scope, characterId, existing);
            return;
        }

        await _db.InsertQuestAsync(scope, characterId, description.Trim(), QuestStatus.Active, priority, sourceSceneId);
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] ObjectiveLayer: 添加任务 - {description} (priority={priority})");
    }

    public async Task CompleteObjectiveAsync(TrpgScope scope, string characterId, string description, string? currentSceneId = null)
        => await UpdateObjectiveStatusByMatchAsync(scope, characterId, description, QuestStatus.Completed, hideFromPrompt: true, currentSceneId);

    public async Task AbandonObjectiveAsync(TrpgScope scope, string characterId, string description, string? currentSceneId = null)
        => await UpdateObjectiveStatusByMatchAsync(scope, characterId, description, QuestStatus.Abandoned, hideFromPrompt: true, currentSceneId);

    public async Task SupersedeObjectiveAsync(TrpgScope scope, string characterId, string match, string? replacementDescription, QuestPriority replacementPriority, string? currentSceneId = null)
    {
        var existing = await FindBestMatchingObjectiveAsync(scope, characterId, match, currentSceneId, includeClosed: false);
        if (existing != null)
        {
            existing.Status = QuestStatus.Superseded;
            existing.HiddenFromPrompt = true;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.LastTouchedAt = existing.UpdatedAt;
            if (!string.IsNullOrWhiteSpace(currentSceneId))
                existing.LastMentionedSceneId = currentSceneId;
            await _db.UpdateQuestAsync(scope, characterId, existing);
        }

        if (!string.IsNullOrWhiteSpace(replacementDescription))
            await AddObjectiveAsync(scope, characterId, replacementDescription, replacementPriority, currentSceneId);
    }

    public async Task TouchObjectiveAsync(TrpgScope scope, string characterId, string description, string? currentSceneId = null)
    {
        var existing = await FindBestMatchingObjectiveAsync(scope, characterId, description, currentSceneId, includeClosed: false);
        if (existing == null)
            return;

        existing.LastTouchedAt = DateTime.UtcNow;
        existing.UpdatedAt = existing.LastTouchedAt;
        existing.HiddenFromPrompt = false;
        if (!string.IsNullOrWhiteSpace(currentSceneId))
            existing.LastMentionedSceneId = currentSceneId;
        await _db.UpdateQuestAsync(scope, characterId, existing);
    }

    public async Task<List<QuestObjective>> GetActiveObjectivesAsync(TrpgScope scope, string characterId)
    {
        return await _db.GetActiveQuestsAsync(scope, characterId);
    }

    public async Task ApplyObjectiveUpdatesAsync(TrpgScope scope, string characterId, IEnumerable<ObjectiveUpdate> updates, string? currentSceneId = null)
    {
        foreach (var update in updates.Where(IsMeaningfulObjectiveUpdate))
        {
            var priority = ParsePriority(update.Priority);
            switch ((update.Action ?? "").Trim().ToLowerInvariant())
            {
                case "add":
                    await AddObjectiveAsync(scope, characterId, update.Description, priority, currentSceneId);
                    break;
                case "complete":
                    await CompleteObjectiveAsync(scope, characterId, update.Match, currentSceneId);
                    break;
                case "abandon":
                    await AbandonObjectiveAsync(scope, characterId, update.Match, currentSceneId);
                    break;
                case "supersede":
                    await SupersedeObjectiveAsync(scope, characterId, update.Match, update.Description, priority, currentSceneId);
                    break;
                case "touch":
                    await TouchObjectiveAsync(scope, characterId, update.Match, currentSceneId);
                    break;
            }
        }
    }

    public string GenerateObjectivesString(List<QuestObjective> objectives)
    {
        if (objectives.Count == 0)
            return "无当前目标";

        var sb = new StringBuilder();
        foreach (var obj in objectives)
            sb.AppendLine($"{FormatPriority(obj.Priority)} {obj.Description}".Trim());
        return sb.ToString().TrimEnd();
    }

    public async Task<string> GenerateActionableObjectivesStringAsync(
        TrpgScope scope,
        string characterId,
        string? currentSceneId,
        string? latestText,
        int maxCount = 5)
    {
        var allObjectives = await _db.GetQuestsAsync(scope, characterId);
        var filtered = allObjectives
            .Where(IsPromptEligible)
            .OrderByDescending(obj => ComputeObjectiveScore(obj, currentSceneId, latestText))
            .ThenByDescending(obj => obj.LastTouchedAt)
            .ThenByDescending(obj => obj.UpdatedAt)
            .Take(Math.Clamp(maxCount, 1, 5))
            .ToList();

        return GenerateObjectivesString(filtered);
    }

    private async Task UpdateObjectiveStatusByMatchAsync(
        TrpgScope scope,
        string characterId,
        string description,
        QuestStatus status,
        bool hideFromPrompt,
        string? currentSceneId)
    {
        var existing = await FindBestMatchingObjectiveAsync(scope, characterId, description, currentSceneId, includeClosed: false);
        if (existing == null)
        {
            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] ObjectiveLayer: 未匹配到目标，跳过状态更新 - {description}");
            return;
        }

        existing.Status = status;
        existing.HiddenFromPrompt = hideFromPrompt;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.LastTouchedAt = existing.UpdatedAt;
        existing.CompletedAt = status == QuestStatus.Completed ? DateTime.UtcNow : null;
        if (!string.IsNullOrWhiteSpace(currentSceneId))
            existing.LastMentionedSceneId = currentSceneId;
        await _db.UpdateQuestAsync(scope, characterId, existing);
    }

    private async Task<QuestObjective?> FindBestMatchingObjectiveAsync(
        TrpgScope scope,
        string characterId,
        string match,
        string? currentSceneId,
        bool includeClosed)
    {
        var normalizedMatch = NormalizeObjectiveText(match);
        if (string.IsNullOrWhiteSpace(normalizedMatch))
            return null;

        var candidates = await _db.GetQuestsAsync(scope, characterId);
        if (!includeClosed)
            candidates = candidates.Where(obj => obj.Status == QuestStatus.Active || obj.Status == QuestStatus.Stale).ToList();

        return candidates
            .Select(obj => new { Objective = obj, Score = ScoreObjectiveMatch(obj, normalizedMatch, currentSceneId) })
            .Where(x => x.Score >= 0.35)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Objective.LastTouchedAt)
            .ThenByDescending(x => x.Objective.UpdatedAt)
            .Select(x => x.Objective)
            .FirstOrDefault();
    }

    private static bool IsPromptEligible(QuestObjective objective)
    {
        return objective.Status == QuestStatus.Active
               && !objective.HiddenFromPrompt
               && !string.IsNullOrWhiteSpace(objective.Description);
    }

    private static double ComputeObjectiveScore(QuestObjective objective, string? currentSceneId, string? latestText)
    {
        var score = (int)objective.Priority * 10;

        if (!string.IsNullOrWhiteSpace(currentSceneId))
        {
            if (string.Equals(objective.LastMentionedSceneId, currentSceneId, StringComparison.OrdinalIgnoreCase))
                score += 20;
            else if (string.Equals(objective.SourceSceneId, currentSceneId, StringComparison.OrdinalIgnoreCase))
                score += 12;
        }

        score += Math.Min(15, Math.Max(0, (int)(DateTime.UtcNow - objective.LastTouchedAt).TotalHours * -1 + 15));

        if (!string.IsNullOrWhiteSpace(latestText))
        {
            var overlap = CountKeywordOverlap(NormalizeObjectiveText(objective.Description), NormalizeObjectiveText(latestText));
            score += overlap * 3;
        }

        return score;
    }

    private static double ScoreObjectiveMatch(QuestObjective objective, string normalizedMatch, string? currentSceneId)
    {
        var normalizedObjective = NormalizeObjectiveText(objective.Description);
        if (string.IsNullOrWhiteSpace(normalizedObjective))
            return 0;

        if (string.Equals(normalizedObjective, normalizedMatch, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var score = 0.0;
        if (normalizedObjective.Contains(normalizedMatch, StringComparison.OrdinalIgnoreCase)
            || normalizedMatch.Contains(normalizedObjective, StringComparison.OrdinalIgnoreCase))
            score += 0.55;

        score += Math.Min(0.35, CountKeywordOverlap(normalizedObjective, normalizedMatch) * 0.08);

        if (!string.IsNullOrWhiteSpace(currentSceneId)
            && (string.Equals(objective.LastMentionedSceneId, currentSceneId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(objective.SourceSceneId, currentSceneId, StringComparison.OrdinalIgnoreCase)))
            score += 0.1;

        return score;
    }

    private static bool IsMeaningfulObjectiveUpdate(ObjectiveUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Action))
            return false;

        var action = update.Action.Trim().ToLowerInvariant();
        return action switch
        {
            "add" => !string.IsNullOrWhiteSpace(update.Description),
            "complete" or "abandon" or "touch" => !string.IsNullOrWhiteSpace(update.Match),
            "supersede" => !string.IsNullOrWhiteSpace(update.Match),
            _ => false
        };
    }

    private static string NormalizeObjectiveText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Trim();
        normalized = Regex.Replace(normalized, @"\[[^\]]+\]", " ");
        normalized = normalized.Replace("，", " ").Replace("。", " ").Replace("；", " ").Replace("、", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim().ToLowerInvariant();
    }

    private static int CountKeywordOverlap(string left, string right)
    {
        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        return leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        return Regex.Split(text, @"\s+")
            .Select(token => token.Trim())
            .Where(token => token.Length >= 2);
    }

    private static QuestPriority ParsePriority(string? raw)
    {
        return (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "urgent" => QuestPriority.Critical,
            "high" => QuestPriority.High,
            "low" => QuestPriority.Low,
            _ => QuestPriority.Normal
        };
    }

    private static QuestPriority MaxPriority(QuestPriority left, QuestPriority right)
        => (QuestPriority)Math.Max((int)left, (int)right);

    private static string PreferLonger(string left, string right)
        => right.Length > left.Length ? right : left;

    private static string FormatPriority(QuestPriority priority)
    {
        return priority switch
        {
            QuestPriority.Critical => "[紧急]",
            QuestPriority.High => "[高]",
            QuestPriority.Normal => "[普通]",
            QuestPriority.Low => "[低]",
            _ => ""
        };
    }
}
