using System.Linq;
using System.Text;

namespace AIMod.Trpg;

public sealed class TrpgAgentContextPack
{
    public TrpgScope Scope { get; set; } = new();
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string CurrentSceneId { get; set; } = "";
    public string CurrentSceneText { get; set; } = "无";
    public string SceneSnapshot { get; set; } = "无";
    public List<EntityCanonicalRecord> PresentEntities { get; set; } = new();
    public List<EntityCanonicalRecord> EntityCanonicalRecords { get; set; } = new();
    /// <summary>高相关已知实体候选（不代表在场）</summary>
    public List<EntityCanonicalRecord> KnownEntityCandidates { get; set; } = new();
    /// <summary>当前文本直接提及实体名（不代表在场）</summary>
    public List<string> MentionedEntityNames { get; set; } = new();
    /// <summary>目标/时间线相关实体（不代表在场）</summary>
    public List<EntityCanonicalRecord> RelatedEntities { get; set; } = new();
    public string CurrentObjectives { get; set; } = "无";
    public List<TimelineNode> ActiveTimelineSkeleton { get; set; } = new();
    public string FoldCurrentSceneId { get; set; } = "";
    public string FoldCurrentSceneText { get; set; } = "无";
    public string FoldObjectives { get; set; } = "无";
    public List<TimelineNode> FoldRelevantTimeline { get; set; } = new();
    public List<TimelineNode> FullTimelineForExtractor { get; set; } = new();
    public List<ChatHistoryEntry> RecentActiveHistory { get; set; } = new();
    public List<ChatHistoryEntry> FullRelevantHistoryForExtractor { get; set; } = new();
    public List<EpisodicMemory.CharacterMemory> CharacterICMemory { get; set; } = new();
    public List<MemoryNode> PlayerTableMemory { get; set; } = new();
    public List<string> FactualAwareness { get; set; } = new();
    public string InventoryState { get; set; } = "无";
    public string AffectiveState { get; set; } = "无";
    public string GraphRecallEvidence { get; set; } = "无";
    public string ThoughtText { get; set; } = "无";
    public string EmotionText { get; set; } = "无";
    public List<string> IdentityHints { get; set; } = new();

    public int TimelineNodesCount => ActiveTimelineSkeleton.Count;
    public int CharacterICMemoryCount => CharacterICMemory.Count;
    public int PlayerTableMemoryCount => PlayerTableMemory.Count;
    public int RecentHistoryCount => RecentActiveHistory.Count;
    public int InventoryItemsCount { get; set; }

    public string ForInfoExtractorFullView()
    {
        var sb = new StringBuilder();
        AppendLineBlock(sb, "当前场景", CurrentSceneText);
        AppendLineBlock(sb, "场景认知缓存", SceneSnapshot);
        // 分区输出实体
        AppendEntities(sb, "【当前确认在场实体】", PresentEntities);
        AppendMentionedNames(sb, "【当前文本直接提及实体】（不代表在场）", MentionedEntityNames);
        AppendEntities(sb, "【高相关已知实体候选（用于身份判断，不代表在场）】", KnownEntityCandidates);
        AppendEntities(sb, "【相关实体（目标/时间线相关，不代表在场）】", RelatedEntities);
        AppendTimeline(sb, "活跃与完整时间线骨架", FullTimelineForExtractor);
        AppendHistory(sb, "最近较长历史", FullRelevantHistoryForExtractor);
        AppendCharacterMemories(sb, "角色 IC 记忆", CharacterICMemory);
        AppendMemory(sb, "PL 桌面记忆", PlayerTableMemory);
        AppendLineBlock(sb, "当前目标", CurrentObjectives);
        AppendLineBlock(sb, "物品状态", InventoryState);
        AppendLineBlock(sb, "情感状态", AffectiveState);
        AppendLineBlock(sb, "身份线索", IdentityHints.Count == 0 ? "无" : string.Join("\n", IdentityHints));
        return sb.ToString().TrimEnd();
    }

    public string ForActionContextView()
    {
        return new StructuredActionContextRenderer().Render(this);
    }

    public string ForCombinedMemoryFoldView(IReadOnlyList<ChatHistoryEntry> toFold)
    {
        var sb = new StringBuilder();
        
        // 【当前折叠角色】块放在最前面
        var charBlock = new StringBuilder();
        charBlock.AppendLine($"CharacterId: {CharacterId}");
        if (!string.IsNullOrWhiteSpace(Scope?.TeamName))
            charBlock.AppendLine($"TeamName: {Scope.TeamName}");
        charBlock.AppendLine();
        charBlock.AppendLine("本折叠窗口是为该角色构建的。GM消息中的'你'默认指向该角色，除非消息明确标记为OOC/PL/其他角色私有视角。");
        charBlock.AppendLine("该角色自己的Narrative/Speech/Action默认属于该角色IC经历。");
        AppendLineBlock(sb, "当前折叠角色", charBlock.ToString().TrimEnd());
        
        AppendLineBlock(sb, "当前场景", FoldCurrentSceneText);
        AppendTimeline(sb, "当前活跃 L1/L2/L3 时间线骨架", FoldRelevantTimeline);
        AppendEntities(sb, "当前确认在场实体", PresentEntities);
        AppendCharacterMemories(sb, "当前角色 IC 记忆摘要", CharacterICMemory.Take(10));
        AppendMemory(sb, "同团共享 PL 桌面摘要", PlayerTableMemory.Take(12));
        AppendEntities(sb, "当前实体和别名", EntityCanonicalRecords);
        AppendLineBlock(sb, "当前目标", FoldObjectives);
        AppendLineBlock(sb, "当前物品状态摘要", InventoryState);
        AppendLineBlock(sb, "IC/PL 判定优先级规则", 
            "1. OOC/PL/其他角色私有视角 → player_table_memory_candidates\n" +
            "2. GM对当前折叠角色的第二人称叙述 → character_ic_memory_candidates\n" +
            "3. GM对当前折叠角色行动的反馈 → character_ic_memory_candidates\n" +
            "4. 当前折叠角色自己的IC行动/台词 → character_ic_memory_candidates\n" +
            "5. 当前角色亲眼可见的公开场景事实 → character_ic_memory_candidates\n" +
            "6. 只有无法判断受众且不是当前角色直接经历时，才写player_table_memory_candidates");
        AppendHistory(sb, "被折叠原文", toFold);
        return sb.ToString().TrimEnd();
    }

    public string ForTurnPlannerView()
    {
        var sb = new StringBuilder();
        AppendLineBlock(sb, "当前场景", CurrentSceneText);
        AppendTimeline(sb, "轻量时间线", ActiveTimelineSkeleton.Take(8));
        AppendLineBlock(sb, "当前目标", CurrentObjectives);
        return sb.ToString().TrimEnd();
    }

    public string ForTimelineWriterView()
    {
        var sb = new StringBuilder();
        AppendLineBlock(sb, "当前场景", CurrentSceneText);
        AppendTimeline(sb, "完整时间线骨架", FullTimelineForExtractor);
        AppendHistory(sb, "最近原文", RecentActiveHistory);
        return sb.ToString().TrimEnd();
    }

    internal static void AppendLineBlock(StringBuilder sb, string title, string content)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"【{title}】");
        sb.AppendLine(string.IsNullOrWhiteSpace(content) ? "无" : content.Trim());
    }

    internal static void AppendHistory(StringBuilder sb, string title, IEnumerable<ChatHistoryEntry> history)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"【{title}】");
        var count = 0;
        foreach (var entry in history)
        {
            count++;
            sb.AppendLine($"- id={entry.Id}; speaker={entry.SpeakerName}; type={entry.MessageType}; createdAt={entry.CreatedAt:o}; text={entry.Content}");
        }
        if (count == 0) sb.AppendLine("无");
    }

    internal static void AppendMemory(StringBuilder sb, string title, IEnumerable<MemoryNode> nodes)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"【{title}】");
        var count = 0;
        foreach (var node in nodes)
        {
            count++;
            sb.AppendLine($"- [{node.MemoryAudience}] {node.Summary} (keywords={node.Keywords}; confidence={node.Confidence:F2})");
        }
        if (count == 0) sb.AppendLine("无");
    }

    internal static void AppendCharacterMemories(StringBuilder sb, string title, IEnumerable<EpisodicMemory.CharacterMemory> memories)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"【{title}】");
        var count = 0;
        foreach (var memory in memories)
        {
            count++;
            sb.AppendLine($"- {memory.MemoryType}: {memory.Content} (confidence={memory.Confidence:F2})");
        }
        if (count == 0) sb.AppendLine("无");
    }

    internal static void AppendTimeline(StringBuilder sb, string title, IEnumerable<TimelineNode> nodes)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"【{title}】");
        var count = 0;
        foreach (var node in nodes)
        {
            count++;
            sb.AppendLine($"- {node.Layer}: {node.Content}");
        }
        if (count == 0) sb.AppendLine("无");
    }

    internal static void AppendEntities(StringBuilder sb, string title, IEnumerable<EntityCanonicalRecord> entities)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"【{title}】");
        var count = 0;
        foreach (var entity in entities)
        {
            count++;
            var brief = FormatEntityBrief(entity);
            sb.AppendLine($"- {brief}");
        }
        if (count == 0) sb.AppendLine("无");
    }

    internal static void AppendMentionedNames(StringBuilder sb, string title, IEnumerable<string> names)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"【{title}】");
        var list = names.ToList();
        if (list.Count > 0)
            sb.AppendLine(string.Join("、", list));
        else
            sb.AppendLine("无");
    }

    /// <summary>
    /// 格式化实体简介：CoreSummary + Top 2-3 active PersistentFacts
    /// 防膨胀：不展示全部 facts
    /// </summary>
    internal static string FormatEntityBrief(EntityCanonicalRecord entity)
    {
        var parts = new List<string> { entity.CurrentDisplayName };
        if (entity.Aliases.Count > 0)
        {
            var otherAliases = entity.Aliases
                .Where(a => !string.Equals(a, entity.CurrentDisplayName, StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToList();
            if (otherAliases.Count > 0)
                parts.Add($"(别名: {string.Join(", ", otherAliases)})");
        }
        var preferredSummary = !string.IsNullOrWhiteSpace(entity.EntityFactSummary)
            ? entity.EntityFactSummary
            : entity.CoreSummary;
        if (!string.IsNullOrWhiteSpace(preferredSummary))
            parts.Add(preferredSummary);
        var activeFacts = entity.PersistentFacts
            .Where(f => f.IsActive)
            .OrderByDescending(f => f.Salience)
            .Take(string.IsNullOrWhiteSpace(preferredSummary) ? 3 : 2)
            .ToList();
        if (activeFacts.Count > 0 && string.IsNullOrWhiteSpace(entity.EntityFactSummary))
            parts.Add($"事实: {string.Join("; ", activeFacts.Select(f => f.Fact))}");
        if (string.IsNullOrWhiteSpace(preferredSummary) && activeFacts.Count == 0)
            parts.Add($"(状态: {entity.IdentityStatus})");
        return string.Join(" | ", parts);
    }
}
