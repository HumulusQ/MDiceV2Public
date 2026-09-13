using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AIMod.Trpg.SemanticGraph;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

public class TrpgContextPipeline
{
    private readonly ChatDatabase _db;
    private readonly TrpgStateCache _stateCache;
    private readonly TrpgPlayerConfig _config;
    private readonly Func<List<ChatMessage>, Task<string?>>? _apiCaller;
    private readonly Func<string, Task<float[]?>> _embeddingCaller;
    private readonly IModContext _context;
    private readonly LlmCallTracker? _llmCallTracker;
    private readonly ObjectiveLayer _objectiveLayer;
    private readonly EntityCanonicalizer _entityCanonicalizer;
    private readonly EventLog _eventLog;
    private readonly SceneSnapshotManager _sceneSnapshotManager;
    private readonly HierarchicalTimeline _hierarchicalTimeline;
    private readonly EpisodicMemory _episodicMemory;
    private readonly SalienceRanking _salienceRanking;
    private readonly NarrativeMemoryProjection _narrativeMemoryProjection;
    private readonly TimelineViewRenderer _timelineViewRenderer;
    private readonly SemanticGraphRecallService _semanticGraphRecall;
    private readonly CharacterInnerStateStore _innerStateStore;
    private static readonly ConcurrentDictionary<string, NarrativeCompileCacheEntry> NarrativeCompileCache = new();

    private static readonly HashSet<string> RecallNoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "我","你","他","她","它","我们","你们","他们","这个","那个","一下","一个","然后","就是","还是","以及","因为","所以",
        "的","了","是","在","和","与","及","并","但","而","或","被","把","给","到","对","从","向","于","以",
        "the","a","an","is","are","was","were","to","of","in","on","at","and","or","for","with"
    };

    public TrpgContextPipeline(
        ChatDatabase db,
        TrpgStateCache stateCache,
        TrpgPlayerConfig config,
        IModContext context,
        Func<List<ChatMessage>, Task<string?>>? apiCaller = null,
        Func<string, Task<float[]?>>? embeddingCaller = null,
        LlmCallTracker? llmCallTracker = null)
    {
        _db = db;
        _stateCache = stateCache;
        _config = config;
        _context = context;
        _apiCaller = apiCaller;
        _embeddingCaller = embeddingCaller ?? (text => Task.FromResult<float[]?>(null));
        _llmCallTracker = llmCallTracker;

        // 初始化四层架构组件
        _objectiveLayer = new ObjectiveLayer(_context, _db);
        _entityCanonicalizer = new EntityCanonicalizer(_context, _db);
        _eventLog = new EventLog(_context, _db);
        _sceneSnapshotManager = new SceneSnapshotManager(_context, _db);
        
        // 初始化新架构组件
        _hierarchicalTimeline = new HierarchicalTimeline(_context, _db, _eventLog);
        _episodicMemory = new EpisodicMemory(_context, _db, _eventLog, _config.EnableAffectiveMemoryEncoding);
        _salienceRanking = new SalienceRanking(_context, _db, _eventLog, _objectiveLayer);
        
        // 初始化叙事记忆投影
        _narrativeMemoryProjection = new NarrativeMemoryProjection(_context, _db);

        // 初始化分层时间轴渲染器
        _timelineViewRenderer = new TimelineViewRenderer(_db, _context);

        var semanticGraphRepository = new SemanticGraphRepository(_db);
        _semanticGraphRecall = new SemanticGraphRecallService(semanticGraphRepository, _context);
        _innerStateStore = new CharacterInnerStateStore(_db);
    }

    public async Task<TrpgPromptContext> BuildContextAsync(TrpgScope scope, AiCharacterEntry aiChar, string latestGmText)
    {
        var groupId = scope.GroupId;
        var state = _stateCache.GetOrCreate(scope, aiChar.CharacterId);
        var activeHistory = await _db.GetActiveHistoryAsync(scope, aiChar.CharacterId);

        // 初始化或更新 RuntimeWorldState
        if (state.WorldState == null)
        {
            state.WorldState = new RuntimeWorldState
            {
                CurrentSceneId = state.CurrentSceneId,
                CurrentLocation = state.CurrentSceneId, // 使用场景ID作为位置，避免用描述填充
                PresentCharacters = state.PresentEntities.ToList()
            };
        }
        else
        {
            // 同步场景变化
            state.WorldState.CurrentSceneId = state.CurrentSceneId;
            state.WorldState.CurrentLocation = state.CurrentSceneId; // 使用场景ID作为位置
            state.WorldState.PresentCharacters = state.PresentEntities.ToList();
        }

        if (!state.PresentEntities.Contains(aiChar.CharacterId, StringComparer.OrdinalIgnoreCase))
            state.PresentEntities.Add(aiChar.CharacterId);

        var sceneDesc = await _db.GetSceneBaseDescAsync(scope, state.CurrentSceneId);
        if (string.IsNullOrWhiteSpace(sceneDesc))
            sceneDesc = "（等待 GM 描述）";

        var hotMeta = await _db.GetCharacterHotMetaByIdsAsync(scope, state.PresentEntities);

        // 构建当前场景字符串（供 AI 注入）
        var currentSceneString = BuildCurrentSceneString(state, sceneDesc);

        var queryText = BuildRecallQuery(latestGmText, activeHistory);
        var queryEmbedding = await _embeddingCaller(queryText);
        var queryTerms = ExtractIntentTermsV2(queryText);
        var usedRelaxedSearch = false;
        var usedGetAllFallback = false;
        
        // 使用 MemoryNode 作为语义索引进行检索
        var rawRecalls = await _db.SearchMemoryNodesBySimilarityAsync(
            scope,
            aiChar.CharacterId,
            queryText,
            minSimilarity: _config.RecallMinSimilarity,
            topK: Math.Max(10, _config.RecallTopK * 2),
            queryEmbedding: queryEmbedding,
            currentEntities: state.PresentEntities,
            currentSceneId: state.CurrentSceneId);

        var rawCandidateCount = rawRecalls.Count;
        var filteredOutNodeTypes = new List<string>();
        var filterResult = FilterSemanticRecallNodes(rawRecalls);
        filteredOutNodeTypes.AddRange(filterResult.FilteredOutNodeTypes);
        var recalls = filterResult.Recalls;

        if (recalls.Count == 0)
        {
            usedRelaxedSearch = true;
            var relaxedMinSimilarity = Math.Min(_config.RecallMinSimilarity, 0.3);
            rawRecalls = await _db.SearchMemoryNodesBySimilarityAsync(
                scope,
                aiChar.CharacterId,
                queryText,
                minSimilarity: relaxedMinSimilarity,
                topK: Math.Max(10, _config.RecallTopK * 2),
                queryEmbedding: queryEmbedding,
                currentEntities: state.PresentEntities,
                currentSceneId: state.CurrentSceneId);
            rawCandidateCount += rawRecalls.Count;
            filterResult = FilterSemanticRecallNodes(rawRecalls);
            filteredOutNodeTypes.AddRange(filterResult.FilteredOutNodeTypes);
            recalls = filterResult.Recalls;
        }

        if (recalls.Count == 0)
        {
            _context.Log(
                LogLevel.Debug,
                $"[AIMod:TRPG] MemoryNode semantic recall empty | Group={scope.GroupId} | Char={aiChar.CharacterId} | GetAllMemoryNodesAsync fallback disabled");
        }

        // MemoryNode 仅用于语义索引，不作为记忆真相
        // 记忆真相由 EpisodicMemory 提供
        var selectedSemanticRecalls = recalls
            .Take(8)
            .ToList();
        LogMemoryRecallDiagnostics(
            scope,
            aiChar.CharacterId,
            queryText,
            queryTerms,
            rawCandidateCount,
            recalls.Count,
            filteredOutNodeTypes,
            usedRelaxedSearch,
            usedGetAllFallback,
            selectedSemanticRecalls);

        var recalledMemory = selectedSemanticRecalls.Count == 0
            ? "无"
            : await BuildSemanticIndexWithTruthAsync(scope, aiChar.CharacterId, selectedSemanticRecalls, queryText);
        var semanticIndexLines = BuildSemanticIndexLines(selectedSemanticRecalls);
        var rawExcerptLines = BuildRawExcerptLines(selectedSemanticRecalls);

        var aliasSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { aiChar.DisplayName };
        foreach (var entityId in state.PresentEntities)
        {
            aliasSet.Add(entityId);
            var leaf = ExtractNameFromEntityId(entityId);
            if (!string.IsNullOrWhiteSpace(leaf)) aliasSet.Add(leaf);

            var meta = hotMeta.FirstOrDefault(x => string.Equals(x.CharId, entityId, StringComparison.OrdinalIgnoreCase));
            if (meta != null && !string.IsNullOrWhiteSpace(meta.Aliases))
            {
                foreach (var token in SplitAliases(meta.Aliases))
                    aliasSet.Add(token);
            }
        }

        var npcIntegratedMemory = await BuildNpcIntegratedMemoryAsync(
            scope,
            aiChar.CharacterId,
            state.PresentEntities,
            hotMeta);

        // 构建可供检索的兴趣点关键词
        var recallKeywords = BuildRecallKeywords(selectedSemanticRecalls, state.PresentEntities);

        // ==================== 四层架构数据获取 ====================
        
        // 1. Objective Layer - 获取当前目标
        var objectivesString = await _objectiveLayer.GenerateActionableObjectivesStringAsync(
            scope,
            aiChar.CharacterId,
            state.CurrentSceneId,
            latestGmText,
            maxCount: 5);

        // 2. Canonical Entity Layer - 获取在场实体
        var allEntities = await _entityCanonicalizer.GetAllEntitiesAsync(scope);
        var presentEntitiesList = allEntities
            .Where(e => state.PresentEntities.Contains(e.EntityId, StringComparer.OrdinalIgnoreCase) || 
                       state.PresentEntities.Any(p => e.Aliases.Contains(p, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var entitiesString = _entityCanonicalizer.GenerateEntitiesString(presentEntitiesList);

        // 3. Immutable Event Log - 获取最近事件
        var recentEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var eventsString = _eventLog.GenerateEventsSummaryString(recentEvents, 15);

        // 4. Scene Snapshot - 直接从运行时状态构建快照
        var sceneSnapshotString = BuildSceneSnapshotFromState(state);


        // 6. Hierarchical Timeline - 优先使用新分层时间轴，无数据时回退旧逻辑
        var timelineString = await _timelineViewRenderer.RenderAsync(scope, aiChar.CharacterId);
        if (string.IsNullOrWhiteSpace(timelineString))
        {
            var timeline = await _hierarchicalTimeline.GetTimelineAsync(scope, aiChar.CharacterId);
            timelineString = _hierarchicalTimeline.ToPromptString(timeline, 1000);
        }

        // 7. Episodic Memory - 获取角色记忆
        var episodicMemoryString = _episodicMemory.ToPromptString(scope, aiChar.CharacterId, 5);

        // 8. Salience Ranking - 对事件进行重要性排序
        var topSalientEvents = await _salienceRanking.GetTopSalientEventsAsync(scope, aiChar.CharacterId, 5, state.PresentEntities, state.CurrentSceneId);
        var salienceReport = _salienceRanking.GenerateSalienceReport(
            await _salienceRanking.RankEventsAsync(scope, aiChar.CharacterId, recentEvents, state.PresentEntities, state.CurrentSceneId));

        // 9. Foundational Canon - 从 EpisodicMemory 查询永久骨架记忆
        var foundationalMemories = await _episodicMemory.GetFoundationalMemoriesAsync(scope, aiChar.CharacterId);
        var foundationalCanonString = _episodicMemory.FormatFoundationalMemories(foundationalMemories);

        // Narrative Compiler Step（规则编织版本）
        var relatedMemories = await BuildRelatedEpisodicMemoriesAsync(scope, aiChar.CharacterId, selectedSemanticRecalls, queryText);
        if (relatedMemories.Count == 0)
        {
            relatedMemories = (await _episodicMemory.GetMemoriesAsync(scope, aiChar.CharacterId))
            .OrderByDescending(m => m.LastAccessed)
            .ThenByDescending(m => m.Confidence)
            .Take(5)
            .ToList();
        }
        var narrativeMemoryLines = await BuildNarrativeMemoryLinesAsync(scope, aiChar.CharacterId, queryText, state.PresentEntities);
        var visibleTimelineNodes = await _db.GetVisibleTimelineNodesAsync(scope, aiChar.CharacterId);
        var l0Nodes = visibleTimelineNodes.Where(n => n.Layer == TimelineLayer.L0).ToList();
        var currentL1Nodes = visibleTimelineNodes.Where(n => n.Layer == TimelineLayer.L1 && IsSameScene(n.SceneId, state.CurrentSceneId)).ToList();
        var currentL2Nodes = visibleTimelineNodes.Where(n => n.Layer == TimelineLayer.L2 && IsSameScene(n.SceneId, state.CurrentSceneId)).ToList();
        var usedL1GlobalFallback = false;
        var usedL2GlobalFallback = false;
        if (currentL1Nodes.Count == 0)
        {
            currentL1Nodes = TakeRecentVisibleTimelineNodes(visibleTimelineNodes.Where(n => n.Layer == TimelineLayer.L1).ToList(), 16);
            usedL1GlobalFallback = currentL1Nodes.Count > 0;
        }
        if (currentL2Nodes.Count == 0)
        {
            currentL2Nodes = TakeRecentVisibleTimelineNodes(visibleTimelineNodes.Where(n => n.Layer == TimelineLayer.L2).ToList(), 16);
            usedL2GlobalFallback = currentL2Nodes.Count > 0;
        }
        _context.Log(
            LogLevel.Info,
            $"[AIMod:TRPG] Narrative compiler inputs (Group={groupId}, Char={aiChar.CharacterId}, Scene={state.CurrentSceneId}): " +
            $"Semantic={semanticIndexLines.Count}, Raw={rawExcerptLines.Count}, Episodic={relatedMemories.Count}, Narrative={narrativeMemoryLines.Count}, " +
            $"TimelineTotal={visibleTimelineNodes.Count}, L0={l0Nodes.Count}, L1={currentL1Nodes.Count}, L2={currentL2Nodes.Count}, " +
            $"L1GlobalFallback={usedL1GlobalFallback}, L2GlobalFallback={usedL2GlobalFallback}");
        var narrativeMemoryString = narrativeMemoryLines.Count == 0
            ? "无"
            : string.Join("\n", narrativeMemoryLines);
        var activeAffectiveTags = new List<AffectiveTagState>();
        var affectiveStateString = string.Empty;
        if (string.IsNullOrWhiteSpace(affectiveStateString))
            affectiveStateString = "无";
        var narrativeContext = _config.EnableNarrativeContextLlm
            ? await CompileNarrativeContextAsync(
                scope,
                aiChar.CharacterId,
                state.CurrentSceneId,
                currentSceneString,
                state.LatestSituationSummary,
                state.LatestFacts,
                state.LatestEvents,
                semanticIndexLines,
                rawExcerptLines,
                relatedMemories,
                narrativeMemoryLines,
                foundationalCanonString,
                objectivesString,
                visibleTimelineNodes,
                activeAffectiveTags)
            : "LLM 叙事编织已默认停用；Action Agent 使用结构化 ActionContext。";

        await _db.EnsureInitialInventoryImportedAsync(scope, aiChar);
        var inventoryItems = await _db.GetActiveInventoryItemsAsync(scope, aiChar.CharacterId);
        var inventoryStateString = FormatInventoryForPrompt(inventoryItems);
        var playerTableMemory = new List<MemoryNode>();
        var characterIcMemories = new List<EpisodicMemory.CharacterMemory>();
        var graphRecall = await _semanticGraphRecall.BuildEvidencePackAsync(scope, aiChar.CharacterId, queryText, activeHistory);
        var innerState = await _innerStateStore.GetAsync(scope, aiChar.CharacterId);
        var contextPack = await new TrpgAgentContextPackBuilder(_db, _context).BuildAsync(
            scope,
            aiChar,
            state,
            activeHistory,
            currentSceneString,
            sceneSnapshotString,
            string.Empty,
            inventoryStateString,
            string.Empty,
            visibleTimelineNodes,
            characterIcMemories,
            playerTableMemory);
        contextPack.GraphRecallEvidence = graphRecall.ToPromptString();
        contextPack.ThoughtText = string.IsNullOrWhiteSpace(innerState.ThoughtText) ? "无" : innerState.ThoughtText;
        contextPack.EmotionText = string.IsNullOrWhiteSpace(innerState.EmotionText) ? "无" : innerState.EmotionText;
        var structuredActionContext = contextPack.ForActionContextView();
        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] FinalActionPromptContextStats | timeline_nodes_count={contextPack.TimelineNodesCount} " +
            $"character_ic_memory_count={contextPack.CharacterICMemoryCount} player_table_memory_count={contextPack.PlayerTableMemoryCount} " +
            $"recalled_nodes_count={selectedSemanticRecalls.Count} recent_history_count={contextPack.RecentHistoryCount} " +
            $"inventory_items_count={contextPack.InventoryItemsCount} action_context_chars={structuredActionContext.Length}");

        return new TrpgPromptContext
        {
            CurrentSceneVar = currentSceneString,
            CurrentSceneId = state.CurrentSceneId,
            CurrentVisionVar = currentSceneString,  // 占位，兼容 TokenBudgeting 路径
            RecalledMemoryVar = string.Empty,
            NpcIntegratedMemoryVar = npcIntegratedMemory,
            PresentEntityIds = state.PresentEntities.ToList(),
            PresentEntityAliases = aliasSet.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ForceExtendedHistory = IsRecallIntent(latestGmText),
            RecallKeywordsVar = recallKeywords,
            WorldStateVar = state.WorldState?.ToPromptString() ?? "无",  // 后台保留
            // 四层架构字段
            ObjectivesVar = string.Empty,
            EntitiesVar = entitiesString,
            EventsVar = eventsString,
            SceneSnapshotVar = sceneSnapshotString,  // 后台保留
            // 新架构字段
            TimelineVar = timelineString,
            EpisodicMemoryVar = episodicMemoryString,
            SalienceReportVar = salienceReport,
            FoundationalCanonVar = foundationalCanonString,
            InventoryStateVar = inventoryStateString,
            AffectiveStateVar = string.Empty,
            // 叙事记忆层（认知层）
            NarrativeMemoryVar = narrativeMemoryString,
            NarrativeContextVar = narrativeContext,
            StructuredActionContextVar = structuredActionContext,
            AgentContextPack = contextPack,
            TimelineNodesCount = contextPack.TimelineNodesCount,
            CharacterICMemoryCount = contextPack.CharacterICMemoryCount,
            PlayerTableMemoryCount = contextPack.PlayerTableMemoryCount,
            RecalledNodesCount = selectedSemanticRecalls.Count,
            RecentHistoryCount = contextPack.RecentHistoryCount,
            InventoryItemsCount = contextPack.InventoryItemsCount,
            ActionContextChars = structuredActionContext.Length
        };
    }

    private string BuildSceneSnapshotFromState(TrpgRuntimeState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"场景 ID: {state.CurrentSceneId}");

        var desc = state.SceneState?.Description;
        if (!string.IsNullOrWhiteSpace(desc) && !string.Equals(desc, state.CurrentSceneId, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"场景描述: {desc}");

        if (state.PresentEntities.Count > 0)
        {
            var names = state.PresentEntities
                .Select(e => ExtractNameFromEntityId(e))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (names.Count > 0)
                sb.AppendLine($"在场: {string.Join("、", names)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatInventoryForPrompt(IReadOnlyList<CharacterInventoryItem> items)
    {
        if (items == null || items.Count == 0)
            return "无";

        var lines = new List<string>();
        foreach (var item in items)
        {
            var assumed = item.IsAssumed ? "，推定" : "";
            var qty = item.Quantity <= 0 ? "" : $" x{item.Quantity:g}{item.Unit}";
            var desc = string.IsNullOrWhiteSpace(item.Description) ? "" : $"：{item.Description}";
            lines.Add($"- {item.DisplayName}{qty} [{item.State}{assumed}]{desc}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 构建当前场景字符串（供 AI 注入）
    /// 自然语言描述，融合场景环境与在场实体
    /// </summary>
    private string BuildCurrentSceneString(TrpgRuntimeState state, string sceneDesc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(sceneDesc);

        if (state.PresentEntities.Count > 0)
        {
            var names = state.PresentEntities
                .Select(e => ExtractNameFromEntityId(e))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n)  // 排序避免随机顺序
                .ToList();

            if (names.Count > 0)
            {
                if (names.Count == 1)
                    sb.AppendLine($"在场：{names[0]}");
                else
                    sb.AppendLine($"在场：{string.Join("、", names)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildNarrativeMemoryAsync(TrpgScope scope, string characterId)
    {
        var highValueMemories = await _narrativeMemoryProjection.GetHighValueMemoriesAsync(scope, characterId, maxCount: 20);
        return _narrativeMemoryProjection.GenerateNarrativeMemorySummary(highValueMemories);
    }

    internal static bool IsSemanticRecallNode(MemoryNode node)
    {
        if (node == null) return false;

        var type = node.NodeType?.Trim();
        if (string.IsNullOrWhiteSpace(type))
            return true;

        return !string.Equals(type, "timeline", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "timeline_rollup", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "scene_transition", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "flow", StringComparison.OrdinalIgnoreCase);
    }

    internal static List<string> BuildSemanticIndexLinesForTest(List<MemoryNode> recalls)
    {
        return BuildSemanticIndexLines(recalls);
    }

    private static (List<MemoryNode> Recalls, List<string> FilteredOutNodeTypes) FilterSemanticRecallNodes(List<MemoryNode> rawRecalls)
    {
        var recalls = new List<MemoryNode>();
        var filteredOutNodeTypes = new List<string>();

        foreach (var node in rawRecalls)
        {
            if (IsSemanticRecallNode(node))
            {
                recalls.Add(node);
                continue;
            }

            filteredOutNodeTypes.Add(NormalizeNodeTypeForDiagnostics(node.NodeType));
        }

        return (recalls, filteredOutNodeTypes);
    }

    private static string NormalizeNodeTypeForDiagnostics(string? nodeType)
    {
        return string.IsNullOrWhiteSpace(nodeType) ? "<empty>" : nodeType.Trim();
    }

    private static string BuildFilteredOutNodeTypeSummary(List<string> nodeTypes)
    {
        if (nodeTypes.Count == 0)
            return "none";

        return string.Join(
            ",",
            nodeTypes
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key}:{g.Count()}"));
    }

    private void LogMemoryRecallDiagnostics(
        TrpgScope scope,
        string characterId,
        string queryText,
        List<string> queryTerms,
        int rawCandidateCount,
        int afterSemanticFilterCount,
        List<string> filteredOutNodeTypes,
        bool usedRelaxedSearch,
        bool usedGetAllFallback,
        List<MemoryNode> selected)
    {
        _context.Log(
            LogLevel.Debug,
            $"[AIMod:TRPG:MemoryRecall] Group={scope.GroupId} | Char={characterId} | Query={TrimForLog(queryText, 160)} | QueryTerms={string.Join(",", queryTerms)} | RawCandidateCount={rawCandidateCount} | AfterSemanticFilterCount={afterSemanticFilterCount} | FilteredOutNodeTypes={BuildFilteredOutNodeTypeSummary(filteredOutNodeTypes)} | UsedRelaxedSearch={usedRelaxedSearch} | UsedGetAllFallback={usedGetAllFallback} | SelectedTopK={selected.Count}");

        foreach (var node in selected)
        {
            _context.Log(
                LogLevel.Debug,
                $"[AIMod:TRPG:MemoryRecall] SelectedMemory: NodeId={node.Id} | NodeType={node.NodeType} | Summary={TrimForLog(node.Summary, 120)} | Keywords={TrimForLog(node.Keywords, 120)} | Importance={node.Importance:F2} | Heat={node.Heat:F2} | CreatedAt={node.CreatedAt:O} | WasFallback=false");
        }
    }

    private static List<string> BuildSemanticIndexLines(List<MemoryNode> recalls)
    {
        return recalls
            .Where(IsSemanticRecallNode)
            .Where(n => !string.IsNullOrWhiteSpace(n.Summary))
            .Take(8)
            .Select(n =>
            {
                var keywords = string.IsNullOrWhiteSpace(n.Keywords) ? "" : $" | 关键词: {n.Keywords}";
                return $"{n.Summary.Trim()} ({n.NodeType}, 重要度 {n.Importance:F1}{keywords})";
            })
            .Distinct()
            .ToList();
    }

    private static List<string> BuildRawExcerptLines(List<MemoryNode> recalls)
    {
        return recalls
            .Where(IsSemanticRecallNode)
            .SelectMany(n => ParseRawExcerpts(n.RawExcerpt))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .Take(8)
            .ToList();
    }

    private async Task<List<string>> BuildNarrativeMemoryLinesAsync(
        TrpgScope scope,
        string characterId,
        string queryText,
        List<string> presentEntities)
    {
        var allMemories = await _db.QueryNarrativeMemoryNodesAsync(scope, characterId);
        if (allMemories.Count == 0)
            return new List<string>();

        var queryTokens = ExtractIntentTermsV2(queryText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var phrase in ExtractChineseNgrams(queryText).Take(24))
            queryTokens.Add(phrase);

        var entitySet = presentEntities
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await AddCanonicalNarrativeRecallTermsAsync(scope, queryText, queryTokens, entitySet);

        var currentFoldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);

        var scored = allMemories
            .Where(n => !string.IsNullOrWhiteSpace(n.Summary))
            .Select(n => NarrativeMemoryRecallScorer.ScoreNarrativeNode(n, queryTokens, entitySet, currentFoldCount))
            .ToList();

        var eligible = scored.Where(x => x.IsEligible).ToList();
        var selected = NarrativeMemoryRecallScorer.SelectTopScores(scored, 8);

        if (_config.EnableNarrativeMemoryDebugLog)
        {
            _context.Log(
                LogLevel.Debug,
                "[AIMod:TRPG:NarrativeRecall] " +
                $"Query={TrimForLog(queryText, 160)} | " +
                $"QueryTokens={string.Join(",", queryTokens)} | " +
                $"PresentEntities={string.Join(",", entitySet)} | " +
                $"CurrentFoldCount={currentFoldCount} | " +
                $"CandidateCount={allMemories.Count} | " +
                $"EligibleCount={eligible.Count} | " +
                $"SelectedCount={selected.Count} | " +
                $"RenderedCount={selected.Count} | " +
                $"DroppedByThreshold={Math.Max(0, scored.Count - eligible.Count)} | " +
                "DroppedByBudget=0 | " +
                $"Top={System.Text.Json.JsonSerializer.Serialize(selected.Select(x => new
                {
                    x.Node.Id,
                    x.Node.SourceEventId,
                    x.Node.CreatedFoldCount,
                    Summary = TrimForLog(x.Node.Summary, 80),
                    x.BaseScore,
                    x.EntityScore,
                    x.TagScore,
                    x.TokenScore,
                    x.EmbeddingScore,
                    x.QueryRelevanceScore,
                    x.UnresolvedBonus,
                    x.RecencyScore,
                    x.FinalScore,
                    x.IsEligible,
                    x.Node.IsResolved,
                    x.MatchedReasons
                }))}");

            foreach (var score in selected)
            {
                _context.Log(
                    LogLevel.Debug,
                    "[AIMod:TRPG:NarrativeRecall] SelectedMemory: " +
                    $"NodeId={score.Node.Id} | SourceEventId={score.Node.SourceEventId} | " +
                    $"Summary={TrimForLog(score.Node.Summary, 100)} | BaseScore={score.BaseScore:F2} | " +
                    $"EntityScore={score.EntityScore:F2} | TagScore={score.TagScore:F2} | TokenScore={score.TokenScore:F2} | " +
                    $"EmbeddingScore={score.EmbeddingScore:F2} | FinalScore={score.FinalScore:F2} | " +
                    $"IsResolved={score.Node.IsResolved} | MatchedReasons={string.Join(",", score.MatchedReasons)} | " +
                    "Rendered=true | DropReason=");
            }
        }

        return selected
            .Select(x => FormatNarrativeMemoryLine(x.Node))
            .ToList();
    }

    private async Task AddCanonicalNarrativeRecallTermsAsync(
        TrpgScope scope,
        string queryText,
        HashSet<string> queryTokens,
        HashSet<string> entitySet)
    {
        try
        {
            var records = await _db.GetAllEntityCanonicalAsync(scope);
            foreach (var record in records)
            {
                var aliases = new List<string> { record.EntityId, record.CurrentDisplayName };
                aliases.AddRange(record.Aliases ?? new List<string>());
                aliases = aliases
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var isPresent = entitySet.Contains(record.EntityId)
                    || aliases.Any(a => entitySet.Contains(a));
                var queryMatched = aliases.Any(alias =>
                    NarrativeMemoryRecallScorer.IsLooseMatch(queryText, alias)
                    || queryTokens.Any(token => NarrativeMemoryRecallScorer.IsLooseMatch(token, alias)));

                if (!isPresent && !queryMatched)
                    continue;

                entitySet.Add(record.EntityId);
                foreach (var alias in aliases)
                {
                    entitySet.Add(alias);
                    if (alias.Length >= 2 && alias.Length <= 30)
                        queryTokens.Add(alias);
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Debug,
                $"[AIMod:TRPG:NarrativeRecall] canonical entity expansion skipped | Error={ex.Message}");
        }
    }

    private static string TrimForLog(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string FormatNarrativeMemoryLine(NarrativeMemoryNode node)
    {
        var flags = new List<string>();
        if (!node.IsResolved) flags.Add("未解决");
        if (node.MysteryWeight >= 0.5f) flags.Add("悬疑");
        if (Math.Abs(node.EmotionalWeight) >= 0.5f) flags.Add("强情绪");
        if (node.GoalImpact >= 0.5f) flags.Add("目标相关");
        if (node.RelationshipImpact >= 0.5f) flags.Add("关系影响");

        var tagText = node.ArcTags.Count > 0 ? $" | 弧: {string.Join(",", node.ArcTags.Take(3))}" : "";
        var entityText = node.InvolvedEntities.Count > 0 ? $" | 涉及: {string.Join(",", node.InvolvedEntities.Take(4))}" : "";
        var flagText = flags.Count > 0 ? $" | {string.Join(",", flags)}" : "";

        return $"{node.Summary.Trim()}{flagText}{tagText}{entityText}";
    }

    private async Task<string> BuildNpcIntegratedMemoryAsync(
        TrpgScope scope,
        string characterId,
        List<string> presentEntities,
        List<CharacterHotMetaEntry> hotMeta)
    {
        var npcEntities = presentEntities
            .Where(x => !string.Equals(x, characterId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (npcEntities.Count == 0)
            return "无";

        var sb = new StringBuilder();
        var hasAny = false;
        foreach (var npcId in npcEntities)
        {
            var entityCanonical = await _entityCanonicalizer.GetEntityAsync(scope, npcId);
            var meta = hotMeta.FirstOrDefault(x => string.Equals(x.CharId, npcId, StringComparison.OrdinalIgnoreCase));
            var aliases = SplitAliases(meta?.Aliases ?? "").ToList();
            if (!aliases.Contains(npcId, StringComparer.OrdinalIgnoreCase)) aliases.Add(npcId);
            var related = await _db.SearchNpcRelatedMemoryNodesAsync(scope, characterId, aliases, limit: 3);

            if (entityCanonical == null && related.Count == 0)
                continue;

            hasAny = true;
            var displayName = entityCanonical != null ? SafeTrim(entityCanonical.CurrentDisplayName, 300) : "未建立";
            var aliasText = entityCanonical?.Aliases.Count > 0 ? $" ({SafeTrim(string.Join(",", entityCanonical.Aliases), 100)})" : "";
            sb.AppendLine($"{npcId}: {displayName}{aliasText}");

            if (related.Count > 0)
            {
                foreach (var node in related)
                    sb.AppendLine($"  - {SafeTrim(node.Summary, 120)}");
            }
        }

        return hasAny ? sb.ToString().TrimEnd() : "无";
    }

    private static string SafeTrim(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "无";
        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
    }

    private static string BuildRecallQuery(string latestGmText, List<ChatHistoryEntry> activeHistory)
    {
        var latest = string.IsNullOrWhiteSpace(latestGmText) ? "" : latestGmText.Trim();
        var recentNarration = activeHistory
            .Where(x => !string.Equals(x.MessageType, "OOC", StringComparison.OrdinalIgnoreCase))
            .TakeLast(8)
            .Select(x => x.Content)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .TakeLast(4)
            .ToList();

        var intentTerms = ExtractIntentTermsV2(latest);
        var intentBlock = intentTerms.Count == 0
            ? latest
            : string.Join(" ", intentTerms);
        var recentSummary = string.Join("\n", recentNarration.Select(x => x.Length > 80 ? x.Substring(0, 80) : x));

        if (string.IsNullOrWhiteSpace(intentBlock) && string.IsNullOrWhiteSpace(recentSummary))
            return "";

        if (string.IsNullOrWhiteSpace(recentSummary))
            return intentBlock;

        if (string.IsNullOrWhiteSpace(intentBlock))
            return recentSummary;

        return $"[intent]\n{intentBlock}\n{intentBlock}\n[latest]\n{latest}\n[recent]\n{recentSummary}";
    }

    private static List<string> ExtractIntentTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Split(new[] { ' ', '\n', '\r', '\t', '，', ',', '。', '！', '？', '、', ':', '：', '-', '_', '[', ']', '(', ')', '（', '）', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length >= 2 && x.Length <= 20)
            .Where(x => !RecallNoiseWords.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static List<string> ExtractIntentTermsV2(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var separators = new[]
        {
            ' ', '\n', '\r', '\t', '，', ',', '。', '！', '？', '、',
            ':', '：', '-', '_', '[', ']', '(', ')', '（', '）', '"', '\''
        };
        var terms = text
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length >= 2 && x.Length <= 30)
            .Where(x => !RecallNoiseWords.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        if (terms.Count < 2)
        {
            foreach (var phrase in ExtractChineseNgrams(text))
            {
                if (!terms.Contains(phrase, StringComparer.OrdinalIgnoreCase))
                    terms.Add(phrase);

                if (terms.Count >= 24)
                    break;
            }
        }
        else
        {
            foreach (var phrase in ExtractChineseNgrams(text).Take(24))
            {
                if (!terms.Contains(phrase, StringComparer.OrdinalIgnoreCase))
                    terms.Add(phrase);
            }
        }

        return terms;
    }

    internal static List<string> ExtractNarrativeIntentTermsForTest(string text)
    {
        return ExtractIntentTermsV2(text);
    }

    private static IEnumerable<string> ExtractChineseNgrams(string text)
    {
        var chars = text
            .Where(c => c >= '\u4e00' && c <= '\u9fff')
            .ToArray();

        if (chars.Length < 2)
            yield break;

        for (var size = 4; size >= 2; size--)
        {
            for (var i = 0; i <= chars.Length - size; i++)
            {
                var s = new string(chars, i, size);
                if (RecallNoiseWords.Contains(s))
                    continue;

                yield return s;
            }
        }
    }

    private static bool IsRecallIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        return normalized.Contains("回忆", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("复盘", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("总结", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("经过", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("之前", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("先前", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("timeline", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("recap", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractNameFromEntityId(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return "";
        var idx = entityId.LastIndexOf('_');
        if (idx >= 0 && idx < entityId.Length - 1)
            return entityId[(idx + 1)..];
        return entityId;
    }

    private static IEnumerable<string> SplitAliases(string aliases)
    {
        return aliases.Split(new[] { ',', '，', ';', '；', '|', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length >= 2);
    }

    /// <summary>
    /// 构建语义索引字符串
    /// MemoryNode 现在仅作为语义索引，用于检索和 MMR 算法
    /// 记忆真相由 EpisodicMemory 提供
    /// </summary>
    private static string BuildSemanticIndexString(List<MemoryNode> recalls)
    {
        if (recalls.Count == 0)
            return "无语义索引结果";

        var sb = new StringBuilder();
        var summary = string.Join("；", recalls.Take(3).Select(n => n.Summary));
        if (recalls.Count > 3)
            summary += $" 等{recalls.Count}条";
        sb.AppendLine(summary);

        foreach (var (node, i) in recalls.Select((n, i) => (n, i)))
        {
            sb.AppendLine($"[{i + 1}] {node.Summary} ({node.NodeType}, {node.Importance:F1})");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildSemanticIndexWithTruthAsync(TrpgScope scope, string characterId, List<MemoryNode> recalls, string queryText)
    {
        var memories = await _db.GetCharacterMemoriesAsync(scope, characterId, limit: 200);
        var queryTokens = ExtractIntentTermsV2(queryText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine(BuildSemanticIndexString(recalls));

        var truthLines = new List<string>();
        foreach (var node in recalls.Take(5))
        {
            var rawTexts = ParseRawExcerpts(node.RawExcerpt);
            var nodeTokens = ExtractIntentTermsV2($"{node.Keywords} {node.Summary} {string.Join(" ", rawTexts)}")
                .Concat(queryTokens)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var hit = memories
                .Select(m =>
                {
                    var score = nodeTokens.Count(token => m.Content.Contains(token, StringComparison.OrdinalIgnoreCase));
                    return new { Memory = m, Score = score };
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Memory.Confidence)
                .FirstOrDefault();

            if (hit != null && hit.Score > 0)
            {
                truthLines.Add(hit.Memory.Content);
                continue;
            }

            if (rawTexts.Count > 0)
                truthLines.Add(rawTexts[0]);
        }

        if (truthLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[相关记忆正文]");
            foreach (var (line, idx) in truthLines.Distinct().Take(6).Select((line, idx) => (line, idx)))
                sb.AppendLine($"[{idx + 1}] {line}");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<List<EpisodicMemory.CharacterMemory>> BuildRelatedEpisodicMemoriesAsync(
        TrpgScope scope,
        string characterId,
        List<MemoryNode> recalls,
        string queryText)
    {
        if (recalls.Count == 0)
            return new List<EpisodicMemory.CharacterMemory>();

        var memories = await _db.GetCharacterMemoriesAsync(scope, characterId, limit: 200);
        var activeAffectiveTags = _config.EnableAffectiveTags
            ? await _db.GetActiveAffectiveTagStatesAsync(scope, characterId, 8)
            : new List<AffectiveTagState>();
        var queryTokens = ExtractIntentTermsV2(queryText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scored = new List<(EpisodicMemory.CharacterMemory Memory, double Score)>();

        foreach (var node in recalls.Take(8))
        {
            var rawTexts = ParseRawExcerpts(node.RawExcerpt);
            var nodeTokens = ExtractIntentTermsV2($"{node.Keywords} {node.Summary} {string.Join(" ", rawTexts)}")
                .Concat(queryTokens)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var memory in memories)
            {
                var score = nodeTokens.Count(token => memory.Content.Contains(token, StringComparison.OrdinalIgnoreCase));
                var affectiveBoost = CalculateAffectiveRecallBoost(memory, activeAffectiveTags);
                if (score > 0 || affectiveBoost > 0)
                    scored.Add((memory, score + affectiveBoost));
            }
        }

        return scored
            .GroupBy(x => x.Memory.Id)
            .Select(g => g.OrderByDescending(x => x.Score).ThenByDescending(x => x.Memory.Confidence).First())
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Memory.Confidence)
            .Select(x => x.Memory)
            .Take(5)
            .ToList();
    }

    private static List<string> ParseRawExcerpts(string rawExcerpt)
    {
        if (string.IsNullOrWhiteSpace(rawExcerpt) || rawExcerpt == "[]")
            return new List<string>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(rawExcerpt) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static double CalculateAffectiveRecallBoost(EpisodicMemory.CharacterMemory memory, List<AffectiveTagState> activeTags)
    {
        if (activeTags.Count == 0 || memory.Metadata.Count == 0)
            return 0;

        if (!memory.Metadata.TryGetValue("encoding", out var encodingObj) || encodingObj == null)
            return 0;

        var encodingJson = System.Text.Json.JsonSerializer.Serialize(encodingObj);
        var boost = 0.0;

        foreach (var tag in activeTags)
        {
            if (string.IsNullOrWhiteSpace(tag.TagType))
                continue;

            var tagMatched = encodingJson.Contains(tag.TagType, StringComparison.OrdinalIgnoreCase);
            var sourceMatched = !string.IsNullOrWhiteSpace(tag.SourceKey) &&
                encodingJson.Contains(tag.SourceKey, StringComparison.OrdinalIgnoreCase);
            var targetMatched = !string.IsNullOrWhiteSpace(tag.TargetEntityId) &&
                (encodingJson.Contains(tag.TargetEntityId, StringComparison.OrdinalIgnoreCase) ||
                 memory.Content.Contains(tag.TargetEntityId, StringComparison.OrdinalIgnoreCase));

            if (tagMatched || sourceMatched || targetMatched)
                boost += 0.35 + Math.Min(0.65, tag.Charge);
        }

        return Math.Min(2.0, boost);
    }

    private async Task<string> CompileNarrativeContextAsync(
        TrpgScope scope,
        string characterId,
        string currentSceneId,
        string sceneSummary,
        string situationSummary,
        List<string> facts,
        List<string> extractedEvents,
        List<string> semanticIndexLines,
        List<string> rawExcerptLines,
        List<EpisodicMemory.CharacterMemory> relatedMemories,
        List<string> narrativeMemoryLines,
        string foundationalCanon,
        string objectives,
        List<TimelineNode> visibleTimelineNodes,
        List<AffectiveTagState> activeAffectiveTags)
    {
        var groupId = scope.GroupId;
        var safeSceneId = string.IsNullOrWhiteSpace(currentSceneId) ? "scene_default" : currentSceneId;
        var safeSceneSummary = sceneSummary ?? "";
        semanticIndexLines ??= new List<string>();
        rawExcerptLines ??= new List<string>();
        narrativeMemoryLines ??= new List<string>();
        foundationalCanon ??= "";
        objectives ??= "";
        var currentSituation = string.IsNullOrWhiteSpace(situationSummary)
            ? safeSceneSummary.Split('\n').FirstOrDefault() ?? safeSceneSummary
            : situationSummary.Trim();
        var memoryLines = relatedMemories
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content.Trim())
            .Distinct()
            .Take(5)
            .ToList();
        var factLines = facts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .Take(8)
            .ToList();
        var eventLines = extractedEvents
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .Take(5)
            .ToList();
        var semanticLines = semanticIndexLines
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .Take(8)
            .ToList();
        var rawLines = rawExcerptLines
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .Take(6)
            .ToList();
        var narrativeLines = narrativeMemoryLines
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .Take(8)
            .ToList();
        if (_config.EnableNarrativeMemoryDebugLog)
        {
            _context.Log(LogLevel.Debug,
                "[AIMod:TRPG:NarrativeRecall] CompilerVisibility: " +
                $"CandidateLines={narrativeMemoryLines.Count} | RenderedLines={narrativeLines.Count} | " +
                $"DroppedByBudget={Math.Max(0, narrativeMemoryLines.Count - narrativeLines.Count)}");
        }
        var timelineLines = BuildCompilerTimelineLines(
            visibleTimelineNodes,
            safeSceneId,
            currentSituation,
            factLines,
            eventLines,
            objectives);
        var affectiveLines = AffectiveTagController.FormatForPrompt(activeAffectiveTags ?? new List<AffectiveTagState>())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToList();

        var digestSource = string.Join("\n", new[]
        {
            safeSceneId,
            safeSceneSummary,
            currentSituation,
            string.Join("\n", factLines),
            string.Join("\n", eventLines),
            string.Join("\n", semanticLines),
            string.Join("\n", rawLines),
            string.Join("\n", memoryLines),
            string.Join("\n", narrativeLines),
            foundationalCanon,
            objectives,
            string.Join("\n", timelineLines),
            string.Join("\n", affectiveLines)
        });
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(digestSource)));

        var cacheKey = $"{groupId}:{characterId}";
        if (NarrativeCompileCache.TryGetValue(cacheKey, out var cached)
            && string.Equals(cached.SceneId, safeSceneId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(cached.Digest, digest, StringComparison.OrdinalIgnoreCase))
        {
            return cached.CompiledText;
        }

        if (_apiCaller != null)
        {
            try
            {
                var prompt = BuildNarrativeCompilerPrompt(
                    safeSceneId,
                    safeSceneSummary,
                    currentSituation,
                    factLines,
                    eventLines,
                    semanticLines,
                    rawLines,
                    memoryLines,
                    narrativeLines,
                    foundationalCanon,
                    objectives,
                    timelineLines,
                    affectiveLines);
                var messages = new List<ChatMessage>
                {
                    new("system", $"{AimodPromptPrefixes.BackendCommonPrefixV1}\n\n你是TRPG叙事上下文编织器。你只负责把已确认的当前情景、角色认知、记忆和时间轴编织成逻辑完整的自然叙事上下文。保留所有细节、因果、时序和情绪累积，不添加推测，不替GM判定，不输出标题。"),
                    new("user", prompt)
                };
                var response = await (_llmCallTracker ?? throw new InvalidOperationException("LlmCallTracker is required for AIMod LLM calls."))
                    .CallAsync(scope, characterId, messages, "NarrativeContextCompiler", "OptionalNarrativeContext", _apiCaller);

                var woven = NormalizeNarrativeCompilerResponse(response);
                if (!string.IsNullOrWhiteSpace(woven))
                {
                    NarrativeCompileCache[cacheKey] = new NarrativeCompileCacheEntry
                    {
                        SceneId = safeSceneId,
                        Digest = digest,
                        CompiledText = woven
                    };
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Narrative compiler LLM hit (Group={groupId}, Char={characterId})");
                    return woven;
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Narrative compiler LLM failed, fallback to rule weaving: {ex.Message}");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"当前处于{safeSceneId}。{currentSituation}");

        if (factLines.Count > 0)
            sb.AppendLine($"已确认事实：{string.Join("；", factLines.Take(4))}。");

        if (eventLines.Count > 0)
            sb.AppendLine($"刚发生的变化：{string.Join("；", eventLines.Take(3))}。");

        if (timelineLines.Count > 0)
            sb.AppendLine($"分层时间轴：{string.Join("；", timelineLines.Take(8))}。");

        if (semanticLines.Count > 0)
            sb.AppendLine($"相关语义索引：{string.Join("；", semanticLines.Take(3))}。");

        if (memoryLines.Count > 0)
            sb.AppendLine($"相关记忆：{string.Join("；", memoryLines.Take(3))}。");
        else if (rawLines.Count > 0)
            sb.AppendLine($"相关原文回声：{string.Join("；", rawLines.Take(2))}。");

        if (narrativeLines.Count > 0)
            sb.AppendLine($"叙事节点：{string.Join("；", narrativeLines.Take(3))}。");

        sb.AppendLine("请基于以上连贯叙事行动，不要补充未确认事实。");

        var compiled = BuildStructuredNarrativeContext(
            safeSceneId,
            currentSituation,
            factLines,
            eventLines,
            semanticLines,
            memoryLines,
            narrativeLines,
            timelineLines,
            foundationalCanon,
            objectives,
            affectiveLines);
        NarrativeCompileCache[cacheKey] = new NarrativeCompileCacheEntry
        {
            SceneId = safeSceneId,
            Digest = digest,
            CompiledText = compiled
        };
        return compiled;
    }

    private static string BuildStructuredNarrativeContext(
        string sceneId,
        string currentSituation,
        List<string> factLines,
        List<string> eventLines,
        List<string> semanticLines,
        List<string> memoryLines,
        List<string> narrativeLines,
        List<string> timelineLines,
        string foundationalCanon,
        string objectives,
        List<string> affectiveLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[确认角色记忆]");
        AppendRequiredCompilerList(sb, "仅角色记忆", CleanContextLines(memoryLines.Take(5)).Select(x => $"- {x}").ToList(), "- 无");

        sb.AppendLine("[角色信念或怀疑]");
        var suspicionLines = memoryLines
            .Where(x => x.Contains("Suspicion", StringComparison.OrdinalIgnoreCase)
                     || x.Contains("Rumor", StringComparison.OrdinalIgnoreCase)
                     || x.Contains("FalseBelief", StringComparison.OrdinalIgnoreCase)
                     || x.Contains("CharacterBelief", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .Select(CleanContextLine)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => $"- {x}")
            .ToList();
        AppendPlainOrNone(sb, suspicionLines);

        sb.AppendLine("[叙事提示]");
        AppendPlainOrNone(sb, CleanContextLines(narrativeLines.Take(6)).Select(x => $"- {x}").ToList());

        sb.AppendLine("[时间线摘要]");
        AppendPlainOrNone(sb, CleanContextLines(timelineLines.Take(8)).Select(x => $"- {x}").ToList());

        sb.AppendLine("[当前情感框架]");
        AppendPlainOrNone(sb, affectiveLines.Take(8).ToList());

        sb.AppendLine("[世界一致性]");
        sb.AppendLine($"- 场景={CleanContextLine(sceneId)}；当前情景={CleanContextLine(currentSituation)}");
        foreach (var line in factLines.Take(5))
            sb.AppendLine($"- {CleanContextLine(line)}");
        foreach (var line in eventLines.Take(4))
            sb.AppendLine($"- {CleanContextLine(line)}");
        foreach (var line in semanticLines.Take(4))
            sb.AppendLine($"- {CleanContextLine(line)}");
        if (!string.IsNullOrWhiteSpace(foundationalCanon))
            sb.AppendLine($"- 基础事实：{CleanContextLine(foundationalCanon)}");
        if (!string.IsNullOrWhiteSpace(objectives))
            sb.AppendLine($"- 当前目标：{CleanContextLine(objectives)}");

        sb.AppendLine("[回应约束]");
        sb.AppendLine("- 只依据确认角色记忆、已标注信念或怀疑、可见场景状态和当前情感框架行动。");
        sb.AppendLine("- 叙事提示、时间线摘要、语义索引和世界事实只能做一致性支撑，不能直接变成角色已知事实。");
        sb.AppendLine("- 怀疑必须表现为怀疑；除非确认角色记忆支持，不要断言为事实。");
        sb.AppendLine("- 情感标签只指导表演，不在行动回复中重置、解决或升级长期情感状态。");

        return sb.ToString().Trim();
    }

    private static List<string> CleanContextLines(IEnumerable<string> lines)
    {
        return lines
            .Select(CleanContextLine)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CleanContextLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";

        var cleaned = line.Trim();
        cleaned = Regex.Replace(cleaned, @"\s*\((?:source=|not confirmed|latest event|world/extracted|MemoryNode|TimelineNode|NarrativeMemoryNode|CharacterMemory#|compressed continuity).*?\)", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Trim();
    }

    private static void AppendPlainOrNone(StringBuilder sb, List<string> lines)
    {
        if (lines.Count == 0)
            sb.AppendLine("- 无");
        else
            foreach (var line in lines)
                sb.AppendLine(line);
    }

    private static List<TimelineNode> TakeRecentVisibleTimelineNodes(List<TimelineNode> nodes, int limit)
    {
        if (nodes.Count == 0)
            return new List<TimelineNode>();

        return nodes
            .Where(n => n.Status == TimelineNodeStatus.Visible)
            .Where(n => !string.IsNullOrWhiteSpace(n.Content))
            .OrderByDescending(n => n.EventSequence)
            .Take(limit)
            .OrderBy(n => n.EventSequence)
            .ToList();
    }

    private static List<string> BuildCompilerTimelineLines(
        List<TimelineNode> nodes,
        string currentSceneId,
        string currentSituation,
        List<string> factLines,
        List<string> eventLines,
        string objectives)
    {
        var visible = nodes
            .Where(n => n.Status == TimelineNodeStatus.Visible)
            .Where(n => !string.IsNullOrWhiteSpace(n.Content))
            .OrderBy(n => n.EventSequence)
            .ToList();
        if (visible.Count == 0)
            return new List<string>();

        var lines = new List<string>();
        var keywords = BuildTimelineKeywordSet(currentSceneId, currentSituation, factLines, eventLines, objectives);
        var childrenByParent = visible
            .Where(n => !string.IsNullOrWhiteSpace(n.ParentId))
            .GroupBy(n => n.ParentId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.EventSequence).ToList());

        foreach (var l0 in visible.Where(n => n.Layer == TimelineLayer.L0))
            lines.Add(FormatTimelineLine(l0, 0));

        var renderedL2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var l1Nodes = visible.Where(n => n.Layer == TimelineLayer.L1).ToList();
        foreach (var l1 in l1Nodes)
        {
            lines.Add(FormatTimelineLine(l1, 0));
            var l2Children = childrenByParent.TryGetValue(l1.Id, out var children)
                ? children.Where(n => n.Layer == TimelineLayer.L2).ToList()
                : new List<TimelineNode>();

            foreach (var l2 in l2Children)
            {
                renderedL2.Add(l2.Id);
                AppendL2WithRelevantL3(lines, l2, childrenByParent, currentSceneId, keywords);
            }
        }

        var orphanL2Nodes = visible
            .Where(n => n.Layer == TimelineLayer.L2 && !renderedL2.Contains(n.Id))
            .ToList();
        foreach (var l2 in orphanL2Nodes)
            AppendL2WithRelevantL3(lines, l2, childrenByParent, currentSceneId, keywords);

        return lines;
    }

    private static void AppendL2WithRelevantL3(
        List<string> lines,
        TimelineNode l2,
        Dictionary<string, List<TimelineNode>> childrenByParent,
        string currentSceneId,
        HashSet<string> keywords)
    {
        lines.Add(FormatTimelineLine(l2, 1));
        if (!childrenByParent.TryGetValue(l2.Id, out var children))
            return;

        var l3Children = children.Where(n => n.Layer == TimelineLayer.L3).OrderBy(n => n.EventSequence).ToList();
        var expanded = l3Children.Where(n => ShouldExpandTimelineL3(n, currentSceneId, keywords)).ToList();
        foreach (var l3 in expanded)
            lines.Add(FormatTimelineLine(l3, 2));

        var foldedCount = l3Children.Count - expanded.Count;
        if (foldedCount > 0)
            lines.Add($"    - L3 已折叠 {foldedCount} 条无关当前情景的细节");
    }

    private static string FormatTimelineLine(TimelineNode node, int indentLevel)
    {
        var indent = new string(' ', Math.Max(0, indentLevel) * 2);
        var scene = string.IsNullOrWhiteSpace(node.SceneId) ? "" : $" [{node.SceneId.Trim()}]";
        var foreshadowing = node.Foreshadowing ? " [伏笔]" : "";
        return $"{indent}- {node.Layer}{scene} {node.Content.Trim()}{foreshadowing}";
    }

    private static bool ShouldExpandTimelineL3(TimelineNode node, string currentSceneId, HashSet<string> keywords)
    {
        if (node.Foreshadowing || IsSameScene(node.SceneId, currentSceneId))
            return true;

        return keywords.Any(keyword => node.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> BuildTimelineKeywordSet(
        string currentSceneId,
        string currentSituation,
        List<string> factLines,
        List<string> eventLines,
        string objectives)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTimelineKeywords(keywords, currentSceneId);
        AddTimelineKeywords(keywords, currentSituation);
        AddTimelineKeywords(keywords, objectives);
        foreach (var line in factLines)
            AddTimelineKeywords(keywords, line);
        foreach (var line in eventLines)
            AddTimelineKeywords(keywords, line);
        return keywords;
    }

    private static void AddTimelineKeywords(HashSet<string> keywords, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var separators = new[]
        {
            ' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '|', '/', '\\',
            '，', '。', '；', '：', '！', '？', '、', '（', '）', '(', ')', '[', ']', '"', '\''
        };
        foreach (var raw in text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim('-', '#', '*', ' ');
            if (token.Length >= 2 && token.Length <= 32)
                keywords.Add(token);
        }
    }

    private static bool IsSameScene(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SelectTimelineLines(List<TimelineNode> nodes, int limit)
    {
        if (nodes.Count == 0)
            return new List<string>();

        return nodes
            .Where(n => n.Status == TimelineNodeStatus.Visible)
            .Where(n => !string.IsNullOrWhiteSpace(n.Content))
            .Select(n => new
            {
                Node = n,
                Score = n.Importance
                    + (n.Foreshadowing ? 2 : 0)
                    + Math.Min(2, Math.Max(0, n.EventSequence) / 1000.0)
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Node.EventSequence)
            .Take(limit)
            .OrderBy(x => x.Node.EventSequence)
            .Select(x => x.Node.Content.Trim())
            .Distinct()
            .ToList();
    }

    private static string BuildNarrativeCompilerPrompt(
        string currentSceneId,
        string sceneSummary,
        string currentSituation,
        List<string> factLines,
        List<string> eventLines,
        List<string> semanticIndexLines,
        List<string> rawExcerptLines,
        List<string> memoryLines,
        List<string> narrativeMemoryLines,
        string foundationalCanon,
        string objectives,
        List<string> timelineLines,
        List<string> affectiveLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[当前场景]");
        sb.AppendLine($"场景ID: {currentSceneId}");
        sb.AppendLine(sceneSummary);
        sb.AppendLine();

        sb.AppendLine("[情景摘要]");
        sb.AppendLine(currentSituation);
        sb.AppendLine();

        AppendCompilerList(sb, "事实清单", factLines);
        AppendCompilerList(sb, "本轮事件", eventLines);
        AppendCompilerList(sb, "语义索引节点", semanticIndexLines);
        AppendCompilerList(sb, "相关原文切片", rawExcerptLines);
        AppendCompilerList(sb, "相关情景记忆", memoryLines);
        AppendCompilerList(sb, "叙事记忆节点", narrativeMemoryLines);
        AppendCompilerList(sb, "当前情感框架", affectiveLines);

        if (!string.IsNullOrWhiteSpace(foundationalCanon) && !string.Equals(foundationalCanon.Trim(), "无", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("[基础事实]");
            sb.AppendLine(foundationalCanon.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(objectives) && !string.Equals(objectives.Trim(), "无", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("[当前目标]");
            sb.AppendLine(objectives.Trim());
            sb.AppendLine();
        }

        AppendRequiredCompilerList(sb, "分层时间轴", timelineLines, "无可见时间轴节点。");

        sb.AppendLine("[输出要求]");
        sb.AppendLine("输出3到5句自然语言段落。只使用上方已确认信息，串联当前情景、相关记忆、目标和故事骨架。不要写行动建议，不要替GM揭示未知结果。");
        return sb.ToString();
    }

    private static void AppendCompilerList(StringBuilder sb, string title, List<string> lines)
    {
        if (lines.Count == 0)
            return;

        sb.AppendLine($"[{title}]");
        foreach (var line in lines)
            sb.AppendLine($"- {line}");
        sb.AppendLine();
    }

    private static void AppendRequiredCompilerList(StringBuilder sb, string title, List<string> lines, string emptyText)
    {
        sb.AppendLine($"[{title}]");
        if (lines.Count == 0)
            sb.AppendLine($"- {emptyText}");
        else
            foreach (var line in lines)
                sb.AppendLine(line);
        sb.AppendLine();
    }

    private static string NormalizeNarrativeCompilerResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "";

        var cleaned = response.Trim();
        if (cleaned.Equals("[NONE]", StringComparison.OrdinalIgnoreCase))
            return "";

        cleaned = cleaned.Replace("[叙事上下文]", "", StringComparison.OrdinalIgnoreCase).Trim();
        var hasStructuredContext =
            (cleaned.Contains("[确认角色记忆]", StringComparison.OrdinalIgnoreCase)
             && cleaned.Contains("[回应约束]", StringComparison.OrdinalIgnoreCase))
            || (cleaned.Contains("[ConfirmedCharacterMemory]", StringComparison.OrdinalIgnoreCase)
                && cleaned.Contains("[ResponseConstraints]", StringComparison.OrdinalIgnoreCase));

        if (!hasStructuredContext && LooksLikeMetadataDump(cleaned))
            return "";
        return cleaned.Length <= 1200 ? cleaned : cleaned[..1200].Trim();
    }

    private static bool LooksLikeMetadataDump(string text)
    {
        return text.Contains("source=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("MemoryNode semantic index", StringComparison.OrdinalIgnoreCase)
            || text.Contains("compressed continuity", StringComparison.OrdinalIgnoreCase)
            || text.Contains("world/extracted fact", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构建可供检索的兴趣点关键词
    /// </summary>
    private static string BuildRecallKeywords(List<MemoryNode> recalls, List<string> presentEntities)
    {
        if (recalls.Count == 0 && presentEntities.Count == 0)
            return "无";

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 从记忆节点中提取关键词
        foreach (var node in recalls)
        {
            var nodeKeywords = node.Keywords.Split(new[] { ' ', '，', ',', '、' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var kw in nodeKeywords)
            {
                if (kw.Length >= 2)
                    keywords.Add(kw.Trim());
            }
        }

        // 添加在场实体名称
        foreach (var entity in presentEntities)
        {
            if (entity.Length >= 2)
                keywords.Add(entity);
        }

        if (keywords.Count == 0)
            return "无";

        var topKeywords = keywords.Take(15).ToList();
        return string.Join("、", topKeywords);
    }
}

internal sealed class NarrativeCompileCacheEntry
{
    public string SceneId { get; set; } = "";
    public string Digest { get; set; } = "";
    public string CompiledText { get; set; } = "";
}
