using MDiceV2.Interfaces.Mod;
using System.Linq;

namespace AIMod.Trpg;

public sealed class TrpgAgentContextPackBuilder
{
    private readonly ChatDatabase _db;
    private readonly IModContext _context;
    private readonly EntitySalienceService? _entitySalienceService;

    public TrpgAgentContextPackBuilder(ChatDatabase db, IModContext context, EntitySalienceService? entitySalienceService = null)
    {
        _db = db;
        _context = context;
        _entitySalienceService = entitySalienceService;
    }

    public async Task<TrpgAgentContextPack> BuildAsync(
        TrpgScope scope,
        AiCharacterEntry aiChar,
        TrpgRuntimeState state,
        IReadOnlyList<ChatHistoryEntry> activeHistory,
        string currentSceneText,
        string sceneSnapshot,
        string objectives,
        string inventoryState,
        string affectiveState,
        IReadOnlyList<TimelineNode> visibleTimelineNodes,
        IReadOnlyList<EpisodicMemory.CharacterMemory>? characterIcMemory = null,
        IReadOnlyList<MemoryNode>? playerTableMemory = null,
        string? foldCurrentSceneText = null,
        string? foldCurrentSceneId = null,
        string? foldObjectives = null,
        IReadOnlyList<TimelineNode>? foldRelevantTimeline = null)
    {
        var entities = await _db.GetAllEntityCanonicalAsync(scope);
        var presentIds = state.PresentEntities ?? new List<string>();
        var presentEntities = entities
            .Where(e => presentIds.Contains(e.EntityId, StringComparer.OrdinalIgnoreCase)
                        || presentIds.Contains(e.CurrentDisplayName, StringComparer.OrdinalIgnoreCase)
                        || e.Aliases.Any(a => presentIds.Contains(a, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        // 从当前 GM 文本中提取直接提及的实体名
        var mentionedNames = ExtractMentionedEntityNames(currentSceneText, entities);

        // EntitySalience 输入前筛选：获取高相关已知实体候选
        var knownCandidates = new List<EntityCanonicalRecord>();
        var relatedEntities = new List<EntityCanonicalRecord>();
        if (_entitySalienceService != null)
        {
            var candidateIds = await _entitySalienceService.GetEntityCandidatesForExtractorAsync(
                scope,
                presentEntityIds: presentIds,
                directMentions: mentionedNames,
                limit: 12);
            knownCandidates = entities
                .Where(e => candidateIds.Contains(e.EntityId, StringComparer.OrdinalIgnoreCase)
                         && !presentIds.Contains(e.EntityId, StringComparer.OrdinalIgnoreCase)
                         && !presentIds.Contains(e.CurrentDisplayName, StringComparer.OrdinalIgnoreCase))
                .ToList();
            relatedEntities = entities
                .Where(e => e.PersistentFacts.Any(f => f.IsActive && f.Salience > 0.5))
                .Except(knownCandidates)
                .Except(presentEntities)
                .Take(6)
                .ToList();
        }

        var icMemory = characterIcMemory?.ToList()
            ?? await _db.GetCharacterMemoriesAsync(scope, aiChar.CharacterId, limit: 12);
        var plMemory = playerTableMemory?.ToList()
            ?? await _db.SearchPlayerTableMemoryNodesAsync(scope, currentSceneText, limit: 12);

        var activeSkeleton = SelectActiveTimeline(visibleTimelineNodes, state.CurrentSceneId);
        var resolvedFoldSceneId = string.IsNullOrWhiteSpace(foldCurrentSceneId) ? state.CurrentSceneId : foldCurrentSceneId.Trim();
        var resolvedFoldTimeline = foldRelevantTimeline?.ToList()
            ?? SelectActiveTimeline(visibleTimelineNodes, resolvedFoldSceneId, maxNodes: 12);
        var history = activeHistory.OrderBy(h => h.CreatedAt).ToList();
        var fullHistory = history.TakeLast(80).ToList();
        var recentHistory = history.TakeLast(20).ToList();

        var pack = new TrpgAgentContextPack
        {
            Scope = scope,
            WorldId = scope.WorldId,
            GroupId = scope.GroupId,
            CharacterId = aiChar.CharacterId,
            CurrentSceneId = state.CurrentSceneId,
            CurrentSceneText = currentSceneText,
            SceneSnapshot = sceneSnapshot,
            PresentEntities = presentEntities,
            EntityCanonicalRecords = entities,
            KnownEntityCandidates = knownCandidates,
            MentionedEntityNames = mentionedNames,
            RelatedEntities = relatedEntities,
            CurrentObjectives = objectives,
            ActiveTimelineSkeleton = activeSkeleton,
            FoldCurrentSceneId = resolvedFoldSceneId,
            FoldCurrentSceneText = string.IsNullOrWhiteSpace(foldCurrentSceneText) ? currentSceneText : foldCurrentSceneText.Trim(),
            FoldObjectives = string.IsNullOrWhiteSpace(foldObjectives) ? objectives : foldObjectives.Trim(),
            FoldRelevantTimeline = resolvedFoldTimeline
                .Where(n => n.Status == TimelineNodeStatus.Visible)
                .OrderBy(n => n.EventSequence)
                .ToList(),
            FullTimelineForExtractor = visibleTimelineNodes.OrderBy(n => n.EventSequence).ToList(),
            RecentActiveHistory = recentHistory,
            FullRelevantHistoryForExtractor = fullHistory,
            CharacterICMemory = icMemory,
            PlayerTableMemory = plMemory,
            FactualAwareness = BuildFactualAwarenessForAction(currentSceneText, presentEntities, activeSkeleton),
            InventoryState = inventoryState,
            AffectiveState = affectiveState,
            IdentityHints = BuildIdentityHints(entities, presentEntities),
            InventoryItemsCount = CountInventoryItems(inventoryState)
        };

        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] ActionContextCharacterICDiagnostics | Group={scope.GroupId} | Char={aiChar.CharacterId} | fetched_character_ic_count={pack.CharacterICMemoryCount}");

        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] ActionContext factual awareness | Group={scope.GroupId} | Char={aiChar.CharacterId} | facts_count={pack.FactualAwareness.Count}");

        _context.Log(LogLevel.Debug,
            $"[AIMod:TRPG] TrpgAgentContextPack built | World={scope.WorldId} Group={scope.GroupId} Char={aiChar.CharacterId} " +
            $"timeline={pack.TimelineNodesCount} ic_memory={pack.CharacterICMemoryCount} pl_memory={pack.PlayerTableMemoryCount} history={pack.RecentHistoryCount}");
        return pack;
    }

    private static List<string> BuildFactualAwarenessForAction(
        string currentSceneText,
        IReadOnlyList<EntityCanonicalRecord> presentEntities,
        IReadOnlyList<TimelineNode> activeTimeline)
    {
        var facts = new List<string>();
        var sceneText = currentSceneText?.Trim();
        var presentNames = presentEntities
            .Select(e => e.CurrentDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (!string.IsNullOrWhiteSpace(sceneText) && sceneText != "无")
        {
            facts.Add(presentNames.Count > 0
                ? $"{sceneText} 当前在场的有{string.Join("、", presentNames)}。"
                : sceneText);
        }
        else if (presentNames.Count > 0)
        {
            facts.Add($"当前在场的有{string.Join("、", presentNames)}。");
        }

        // 清洗timeline content：跳过含有多个前缀或明显原文拼贴的条目
        var timelineLines = activeTimeline.Take(3)
            .Where(n => !ContainsMultipleTimelinePrefixes(n.Content) && !LooksLikeRawChatRecord(n.Content))
            .Select(n => n.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
        foreach (var line in timelineLines)
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("全员可行动阶段", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Contains("等待反应", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Contains("继续行动", StringComparison.OrdinalIgnoreCase))
                facts.Add(trimmed);
        }

        return facts
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    /// <summary>
    /// 检查timeline content是否含有多个角色/前缀标记（[GM-], [PL-], [OOC-], [角色名]等）
    /// 这些内容不适合作为事实性认知
    /// </summary>
    private static bool ContainsMultipleTimelinePrefixes(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        var prefixMatches = System.Text.RegularExpressions.Regex.Matches(
            content,
            @"(\[GM-|\[PL-|\[OOC-|\[[^\]]+\]\s*[:：])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // 如果有2个或以上的前缀，视为拼贴原文
        return prefixMatches.Count >= 2;
    }

    /// <summary>
    /// 检查是否看起来像原始聊天记录（多行、含多个说话者）
    /// </summary>
    private static bool LooksLikeRawChatRecord(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        // 多行且含有模式似乎是聊天记录的内容
        var lineCount = content.Count(c => c == '\n');
        var hasNameColons = System.Text.RegularExpressions.Regex.Matches(
            content,
            @"[A-Za-z0-9_\-\u4e00-\u9fff]+\s*[:：]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        
        return lineCount >= 2 && hasNameColons >= 2;
    }

    private static List<TimelineNode> SelectActiveTimeline(IReadOnlyList<TimelineNode> nodes, string currentSceneId, int maxNodes = 24)
    {
        var visibleNodes = nodes
            .Where(n => n.Status == TimelineNodeStatus.Visible)
            .Where(n => n.Layer is TimelineLayer.L1 or TimelineLayer.L2 or TimelineLayer.L3)
            .Where(n => !string.IsNullOrWhiteSpace(n.Content))
            .ToList();
        if (visibleNodes.Count == 0)
            return new List<TimelineNode>();

        var sameScene = visibleNodes
            .Where(n => string.Equals(n.SceneId, currentSceneId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sameScene.Count > 0)
        {
            return sameScene
                .OrderBy(n => n.Layer)
                .ThenByDescending(n => n.Importance)
                .ThenByDescending(n => n.EventSequence)
                .Take(maxNodes)
                .OrderBy(n => n.EventSequence)
                .ToList();
        }

        if (IsInvalidSceneId(currentSceneId))
        {
            return visibleNodes
                .OrderByDescending(n => n.EventSequence)
                .Take(Math.Min(maxNodes, 10))
                .OrderBy(n => n.EventSequence)
                .ToList();
        }

        return visibleNodes
            .OrderBy(n => n.Layer)
            .ThenByDescending(n => n.Importance)
            .ThenByDescending(n => n.EventSequence)
            .Take(Math.Min(maxNodes, 12))
            .OrderBy(n => n.EventSequence)
            .ToList();
    }

    private static bool IsInvalidSceneId(string? sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            return true;

        var normalized = sceneId.Trim();
        return normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("scene_unknown", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("fold_active_scene", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从 GM 文本中提取直接提及的实体名
    /// 匹配所有已知实体的 DisplayName 和 Aliases
    /// </summary>
    private static List<string> ExtractMentionedEntityNames(string gmText, IReadOnlyList<EntityCanonicalRecord> allEntities)
    {
        if (string.IsNullOrWhiteSpace(gmText))
            return new List<string>();
        var mentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in allEntities)
        {
            if (gmText.Contains(entity.CurrentDisplayName, StringComparison.OrdinalIgnoreCase))
                mentioned.Add(entity.EntityId);
            foreach (var alias in entity.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias) && gmText.Contains(alias, StringComparison.OrdinalIgnoreCase))
                    mentioned.Add(entity.EntityId);
            }
        }
        return mentioned.ToList();
    }

    private static List<string> BuildIdentityHints(
        IReadOnlyList<EntityCanonicalRecord> entities,
        IReadOnlyList<EntityCanonicalRecord> presentEntities)
    {
        return presentEntities
            .Concat(entities.Where(e => e.IdentityStatus == EntityIdentityStatus.Tentative))
            .DistinctBy(e => e.EntityId)
            .Take(20)
            .Select(e => $"{e.CurrentDisplayName}: aliases={string.Join(",", e.Aliases)}; status={e.IdentityStatus}")
            .ToList();
    }

    private static int CountInventoryItems(string inventoryState)
    {
        if (string.IsNullOrWhiteSpace(inventoryState) || inventoryState.Trim() == "无")
            return 0;
        return inventoryState.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
