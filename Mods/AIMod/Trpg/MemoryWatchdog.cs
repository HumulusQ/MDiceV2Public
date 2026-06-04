using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AIMod;

namespace AIMod.Trpg;

/// <summary>
/// 语义节点生成器（Watchdog）
/// 当未归档 ChatHistory 的 Token 数超过阈值（默认 4000）时：
///   1. 取最旧的 200~300 条历史
///   2. 调用 AI 生成 5~10 个语义节点（JSON）
///   3. 本地解析并写入 SQLite
///   4. 删除旧历史，仅保留最近 1~2 条
/// </summary>
public class MemoryWatchdog
{
    private readonly ChatDatabase _db;
    private readonly PromptAssembler _assembler;
    private readonly IModContext _context;
    private readonly TrpgPlayerConfig _config;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly Func<string, Task<float[]?>> _embeddingCaller;
    private readonly TrpgStateCache? _stateCache;
    private readonly SemanticDistiller? _semanticDistiller;
    private readonly LlmCallTracker? _llmCallTracker;

    public MemoryWatchdog(
        ChatDatabase db,
        PromptAssembler assembler,
        IModContext context,
        TrpgPlayerConfig config,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        Func<string, Task<float[]?>>? embeddingCaller = null,
        TrpgStateCache? stateCache = null,
        SemanticDistiller? semanticDistiller = null,
        LlmCallTracker? llmCallTracker = null)
    {
        _db = db;
        _assembler = assembler;
        _context = context;
        _config = config;
        _apiCaller = apiCaller;
        _embeddingCaller = embeddingCaller ?? (text => Task.FromResult<float[]?>(null));
        _stateCache = stateCache;
        _semanticDistiller = semanticDistiller;
        _llmCallTracker = llmCallTracker;
    }

    /// <summary>
    /// 检查并执行语义节点生成。返回 true 表示执行了生成操作。
    /// </summary>
    public async Task<bool> CheckAndFoldAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        await _db.DecayMemoryHeatAsync(scope, characterId);

        var activeEntries = await _db.GetActiveHistoryAsync(scope, characterId);
        var activeTokens = await _db.GetActiveTokenCountAsync(scope, characterId);
        var thresholdEntries = Math.Max(1, _config.RecentHistoryCount);
        var thresholdTokens = Math.Max(1, _config.TokenThreshold);
        var countToFold = Math.Max(1, _config.HistoryFoldCount);

        // 配置化窗口：条数或 token 任一达到阈值时触发折叠
        if (activeEntries.Count < thresholdEntries && activeTokens < thresholdTokens)
        {
            _context.Log(LogLevel.Debug,
                $"[AIMod:TRPG] 历史未达折叠阈值 (Group={groupId}, Char={characterId}, Count={activeEntries.Count}, ActiveTokens={activeTokens}, ThresholdCount={thresholdEntries}, ThresholdTokens={thresholdTokens})");
            return false;
        }

        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] 历史折叠触发 (Group={groupId}, Char={characterId}, Count={activeEntries.Count}, ActiveTokens={activeTokens}, ThresholdCount={thresholdEntries}, ThresholdTokens={thresholdTokens}, FoldCount={countToFold})");

        var toFold = activeEntries
            .OrderBy(e => e.CreatedAt)
            .Take(countToFold)
            .ToList();

        if (toFold.Count == 0) return false;

        try
        {
            var pack = await BuildFoldContextPackAsync(scope, characterId, toFold);
            var foldView = pack.ForCombinedMemoryFoldView(toFold);
            var messages = CombinedMemoryFoldRequest.BuildMessages(foldView);

            _context.Log(LogLevel.Info,
                $"[AIMod:TRPG] CombinedMemoryFoldRequest LLM 调用开始 | Group={groupId} | Char={characterId} | 输入历史条数={toFold.Count} | 请求Tokens≈{foldView.Length / 4}");

            var response = await CallTrackedAsync(scope, characterId, messages, "MemoryWatchdog", "CombinedMemoryFoldRequest");

            var parseSuccess = CombinedMemoryFoldParser.TryParse(response, out var foldResult, out var parseError);
            var responsePreview = TrimForLog(response ?? "null", 200);
            
            if (!parseSuccess)
            {
                _context.Log(LogLevel.Warn, 
                    $"[AIMod:TRPG] CombinedMemoryFoldResult parse failed | error={parseError} | " +
                    $"response_preview={responsePreview} | NOT删除历史，等待下次折叠重试");
                var repaired = TryRepairJsonLocally(response)
                    ?? await TryRepairJsonAsync(scope, characterId, response);
                if (repaired != null)
                {
                    parseSuccess = CombinedMemoryFoldParser.TryParse(repaired, out foldResult, out parseError);
                    if (parseSuccess)
                    {
                        _context.Log(LogLevel.Info,
                            $"[AIMod:TRPG] CombinedMemoryFoldResult JSON repair succeeded | Char={characterId}");
                        // 继续正常流程
                    }
                    else
                    {
                        _context.Log(LogLevel.Warn,
                            $"[AIMod:TRPG] JSON repair failed too | error={parseError} | NOT删除历史");
                        return false;
                    }
                }
                else
                {
                    // repair 不可用或失败
                    return false;
                }
            }

            var icCount = foldResult.CharacterIcMemoryCandidates.Count;
            var plCount = foldResult.PlayerTableMemoryCandidates.Count;
            var timelineCount = foldResult.TimelineSummary.Count;
            var objectiveUpdateCount = foldResult.ObjectiveUpdates.Count;
            var eventCount = foldResult.TableEventCandidates.Count;
            
            _context.Log(LogLevel.Info,
                $"[AIMod:TRPG] CombinedMemoryFoldDiagnostics | current_character_id={characterId} | " +
                $"parse_success=true | ic_candidate_count={icCount} | pl_candidate_count={plCount} | " +
                $"timeline_count={timelineCount} | objective_update_count={objectiveUpdateCount} | table_event_count={eventCount} | " +
                $"raw_json_has_character_ic_memory_candidates={icCount > 0}");

            if (icCount == 0 && plCount == 0 && timelineCount == 0 && objectiveUpdateCount == 0 && eventCount == 0)
            {
                _context.Log(LogLevel.Warn, 
                    "[AIMod:TRPG] CombinedMemoryFoldResult 为空，跳过折叠，NOT删除历史");
                return false;
            }

            // 若IC候选为空但有PL候选或时间线，检查是否需要IC-only repair
            if (icCount == 0 && (plCount > 0 || timelineCount > 0))
            {
                var hasCharacterDirectFeedback = toFold.Any(e => e.Content?.Contains("你") ?? false);
                if (hasCharacterDirectFeedback)
                {
                    _context.Log(LogLevel.Warn,
                        $"[AIMod:TRPG] IC candidates为空但有折叠窗口反馈，触发IC-only repair请求 | Group={groupId} | Char={characterId}");
                    
                    var icRepairResult = await TryIcOnlyRepairAsync(scope, characterId, toFold);
                    var icRepairSuccess = icRepairResult != null && icRepairResult.CharacterIcMemoryCandidates.Count > 0;
                    
                    _context.Log(LogLevel.Info,
                        $"[AIMod:TRPG] CombinedMemoryFoldDiagnostics | ic_only_repair_attempted=true | " +
                        $"ic_only_repair_success={icRepairSuccess} | " +
                        $"ic_only_repair_candidate_count={icRepairResult?.CharacterIcMemoryCandidates.Count ?? 0} | " +
                        $"history_deleted=false");

                    if (icRepairSuccess && icRepairResult != null)
                    {
                        // 将 repair 结果的 IC candidates 合并回 foldResult
                        foldResult.CharacterIcMemoryCandidates = icRepairResult.CharacterIcMemoryCandidates;
                        icCount = foldResult.CharacterIcMemoryCandidates.Count;
                        _context.Log(LogLevel.Info,
                            $"[AIMod:TRPG] IC-only repair succeeded, merged {icCount} IC candidates");
                        // 继续正常落库流程
                    }
                    else
                    {
                        _context.Log(LogLevel.Warn,
                            $"[AIMod:TRPG] IC-only repair failed, NOT删除历史 | Char={characterId}");
                        return false;
                    }
                }
            }

            await PersistCombinedFoldResultAsync(scope, characterId, toFold, foldResult);

            // 增加所有现有记忆节点的折叠计数
            await _db.IncrementFoldCountAsync(scope, characterId);
            if (_config.EnableAffectiveTags)
            {
                var foldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);
                await new AffectiveTagController(_db, _context)
                    .DecayStatesAsync(scope, characterId, sceneChanged: false, currentFoldCount: foldCount);
            }

            // 仅在成功持久化后才删除旧历史
            var idsToDelete = toFold.Select(e => e.Id).ToList();
            await _db.DeleteHistoryEntriesAsync(scope, idsToDelete);

            _context.Log(LogLevel.Info,
                $"[AIMod:TRPG] CombinedMemoryFold 已落库，删除 {idsToDelete.Count} 条旧历史");

            // 触发语义蒸馏（如果可用）
            if (_semanticDistiller != null)
            {
                await _semanticDistiller.CheckAndDistillAsync(scope, characterId);
            }

            return true;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] CombinedMemoryFold 失败: {ex.Message}");
            return false;
        }
    }

    private async Task<TrpgAgentContextPack> BuildFoldContextPackAsync(TrpgScope scope, string characterId, List<ChatHistoryEntry> toFold)
    {
        var activeHistory = await _db.GetActiveHistoryAsync(scope, characterId);
        var visibleTimeline = await _db.GetVisibleTimelineNodesAsync(scope, characterId);
        var runtimeState = await ResolveFoldRuntimeStateAsync(scope, characterId);
        var foldSceneId = NormalizeSceneId(runtimeState.CurrentSceneId);
        var latestSceneSnapshot = await TryGetLatestSceneSnapshotAsync(scope, foldSceneId);
        var sceneDesc = await ResolveFoldSceneDescriptionAsync(scope, runtimeState, latestSceneSnapshot);
        var currentSceneText = BuildFoldCurrentSceneText(sceneDesc, runtimeState.PresentEntities);
        var sceneSnapshotText = BuildFoldSceneSnapshotText(foldSceneId, runtimeState, latestSceneSnapshot, sceneDesc);
        var latestObjectiveText = ResolveLatestFoldObjectiveText(runtimeState, activeHistory);
        var objectives = await new ObjectiveLayer(_context, _db).GenerateActionableObjectivesStringAsync(
            scope,
            characterId,
            string.IsNullOrWhiteSpace(foldSceneId) ? null : foldSceneId,
            latestObjectiveText,
            maxCount: 5);
        var foldRelevantTimeline = BuildFoldRelevantTimeline(visibleTimeline, foldSceneId);
        var inventoryItems = await _db.GetActiveInventoryItemsAsync(scope, characterId);
        var inventoryState = inventoryItems.Count == 0
            ? "无"
            : string.Join("\n", inventoryItems.Select(item => $"- {item.DisplayName} x{item.Quantity:g}{item.Unit} [{item.State}]"));
        var activeAffectiveTags = _config.EnableAffectiveTags
            ? await _db.GetActiveAffectiveTagStatesAsync(scope, characterId, 8)
            : new List<AffectiveTagState>();
        var affectiveState = AffectiveTagController.FormatForPrompt(activeAffectiveTags);
        if (string.IsNullOrWhiteSpace(affectiveState))
            affectiveState = "无";
        var query = string.Join("\n", toFold.Select(x => x.Content));
        var characterMemory = await _db.GetCharacterMemoriesAsync(scope, characterId, limit: 12);
        var playerTableMemory = await _db.SearchPlayerTableMemoryNodesAsync(scope, query, limit: 12);

        return await new TrpgAgentContextPackBuilder(_db, _context).BuildAsync(
            scope,
            new AiCharacterEntry { CharacterId = characterId, WorldId = scope.WorldId, GroupId = scope.GroupId, TeamName = scope.TeamName, OwnerUserId = scope.OwnerUserId },
            runtimeState,
            activeHistory,
            currentSceneText,
            sceneSnapshotText,
            objectives,
            inventoryState,
            affectiveState,
            visibleTimeline,
            characterMemory,
            playerTableMemory,
            foldCurrentSceneText: currentSceneText,
            foldCurrentSceneId: foldSceneId,
            foldObjectives: objectives,
            foldRelevantTimeline: foldRelevantTimeline);
    }

    private async Task PersistCombinedFoldResultAsync(
        TrpgScope scope,
        string characterId,
        List<ChatHistoryEntry> toFold,
        CombinedMemoryFoldResult result)
    {
        var batchRawExcerpts = toFold
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Content?.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .ToList();
        var fallbackSourceIds = toFold.Select(e => e.Id.ToString()).ToList();
        var aiCharacter = await _db.GetAiCharacterAsync(scope, characterId);
        var currentDisplayName = aiCharacter?.DisplayName ?? "";
        var currentFoldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);
        var existingVisibleTimeline = await _db.GetVisibleTimelineNodesAsync(scope, characterId);
        var insertedTimeline = new List<TimelineNode>();

        var insertedIcCount = 0;
        var droppedIcCount = 0;
        var dropReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in result.CharacterIcMemoryCandidates)
        {
            // 【修复】先对 candidate.Summary 原文执行 LooksLikeRawTranscript，再做 SanitizeMemorySummary
            // 不能先截断到 200 字再判断 raw-like（截断后长度 ≤200 会绕过 LooksLikeRawTranscript 的 ≤220 检查）
            var isRawLike = LooksLikeRawTranscript(candidate.Summary);
            var rawSummary = SanitizeMemorySummary(candidate.Summary);
            if (string.IsNullOrWhiteSpace(rawSummary))
            {
                RegisterDrop(dropReasons, "EmptySummary");
                droppedIcCount++;
                continue;
            }

            var resolveResult = ResolveCandidateCharacterId(candidate.CharacterId, characterId, currentDisplayName);
            if (!resolveResult.IsResolved)
            {
                RegisterDrop(dropReasons, resolveResult.Reason);
                droppedIcCount++;
                continue;
            }

            if (candidate.Confidence < 0.2)
            {
                RegisterDrop(dropReasons, "LowConfidence");
                droppedIcCount++;
                continue;
            }

            if (isRawLike)
            {
                _context.Log(LogLevel.Warn,
                    $"[AIMod:TRPG] Reject raw-like memory summary | audience=CharacterIC | length={rawSummary.Length} | preview={TrimForLog(rawSummary, 120)}");
                RegisterDrop(dropReasons, "RawTranscriptLikeSummary");
                droppedIcCount++;
            }

            var sourceIds = candidate.SourceMessageIds.Count > 0 ? candidate.SourceMessageIds : fallbackSourceIds;
            var rawExcerpts = BuildRawExcerpts(candidate.RawExcerpt, batchRawExcerpts);
            var summaryForLtm = isRawLike ? "折叠窗口原文存档" : rawSummary;
            var embedding = await _embeddingCaller($"{candidate.Keywords} {summaryForLtm}");
            var normalizedNodeType = NormalizeMemoryType(candidate.NodeType);
            var metadataJson = BuildCandidateMetadataJson(candidate, sourceIds);

            if (!isRawLike)
            {
                await _db.InsertCharacterMemoryAsync(
                    scope,
                    resolveResult.CharacterId,
                    normalizedNodeType,
                    rawSummary,
                    Clamp(candidate.Confidence, 0.1, 1.0),
                    relatedEventId: null,
                    relatedEntityId: null,
                    metadataJson: metadataJson,
                    isFoundational: false,
                    foldCount: currentFoldCount);
            }

            await _db.InsertCharacterMemoryNodeAsync(
                scope,
                resolveResult.CharacterId,
                candidate.Keywords,
                summaryForLtm,
                normalizedNodeType,
                Clamp(candidate.Importance, 0.1, 1.0),
                embedding,
                Clamp(candidate.Confidence, 0.1, 1.0),
                rawExcerpts,
                sourceIds,
                MemorySourceScope.IC,
                metadata: metadataJson);

            if (!isRawLike)
                insertedIcCount++;
        }

        foreach (var candidate in result.PlayerTableMemoryCandidates)
        {
            // 【修复】先对原文执行 LooksLikeRawTranscript，再做 SanitizeMemorySummary
            var isRawLikePl = LooksLikeRawTranscript(candidate.Summary);
            var rawSummary = SanitizeMemorySummary(candidate.Summary);
            if (string.IsNullOrWhiteSpace(rawSummary))
                continue;

            var summaryForLtm = rawSummary;
            if (isRawLikePl)
            {
                _context.Log(LogLevel.Warn,
                    $"[AIMod:TRPG] Reject raw-like memory summary | audience=PlayerTable | length={candidate.Summary?.Length ?? 0} | preview={TrimForLog(candidate.Summary ?? "", 120)}");
                summaryForLtm = "折叠窗口原文存档";
            }

            var sourceIds = candidate.SourceMessageIds.Count > 0 ? candidate.SourceMessageIds : fallbackSourceIds;
            var rawExcerpts = BuildRawExcerpts(candidate.RawExcerpt, batchRawExcerpts);
            var embedding = await _embeddingCaller($"{candidate.Keywords} {summaryForLtm}");
            var normalizedNodeType = NormalizeMemoryType(candidate.NodeType);
            await _db.InsertPlayerTableMemoryNodeAsync(
                scope,
                candidate.Keywords,
                summaryForLtm,
                normalizedNodeType,
                Clamp(candidate.Importance, 0.1, 1.0),
                embedding,
                Clamp(candidate.Confidence, 0.1, 1.0),
                rawExcerpts,
                sourceIds,
                MemorySourceScope.PL);
        }

        foreach (var candidate in result.TimelineSummary)
        {
            var cleanedSummary = TimelineContentCleaner.Clean(candidate.Summary);
            if (string.IsNullOrWhiteSpace(cleanedSummary))
                continue;
            if (!TimelineWriter.LooksLikeConcreteNarrativeContent(cleanedSummary))
            {
                _context.Log(LogLevel.Debug,
                    $"[AIMod:TRPG] Drop non-concrete fold timeline summary | Char={characterId} | preview={TrimForLog(cleanedSummary, 120)}");
                continue;
            }
            var layer = Enum.TryParse<TimelineLayer>(candidate.Level, true, out var parsedLayer)
                ? parsedLayer
                : TimelineLayer.L3;
            var duplicate = existingVisibleTimeline
                .Concat(insertedTimeline)
                .Where(n => n.Status == TimelineNodeStatus.Visible)
                .Where(n => n.Layer == layer)
                .FirstOrDefault(n => TimelineContentCleaner.AreNearDuplicates(n.Content, cleanedSummary));
            if (duplicate != null)
                continue;
            var node = new TimelineNode
            {
                Id = Guid.NewGuid().ToString("N"),
                CharacterId = characterId,
                Layer = layer,
                Content = cleanedSummary,
                SceneId = "fold_rollup",
                Status = TimelineNodeStatus.Visible,
                Importance = Math.Clamp(candidate.Importance, 1, 10),
                Foreshadowing = candidate.Foreshadowing,
                EventSequence = await _db.GetNextEventSequenceAsync(scope, characterId),
                CreatedAt = DateTime.UtcNow
            };
            await _db.InsertTimelineNodeAsync(scope, node);
            insertedTimeline.Add(node);
        }

        foreach (var candidate in result.TableEventCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Result) && string.IsNullOrWhiteSpace(candidate.TableChanges))
                continue;
            var evt = new WorldEvent
            {
                EventType = string.IsNullOrWhiteSpace(candidate.EventType) ? "table_event" : candidate.EventType,
                Actors = SplitCsv(candidate.Actors),
                Location = candidate.Location,
                Result = candidate.Result,
                WorldChanges = string.IsNullOrWhiteSpace(candidate.TableChanges)
                    ? new List<string>()
                    : new List<string> { candidate.TableChanges },
                Timestamp = DateTime.UtcNow,
                Payload = new Dictionary<string, object>
                {
                    { "source", "CombinedMemoryFoldRequest" },
                    { "table_changes", candidate.TableChanges },
                    { "source_message_ids", candidate.SourceMessageIds }
                }
            };
            await _db.InsertEventLogAsync(scope, evt);
        }

        if (result.ObjectiveUpdates.Count > 0)
            await new ObjectiveLayer(_context, _db).ApplyObjectiveUpdatesAsync(scope, characterId, result.ObjectiveUpdates);

        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] CombinedMemoryFoldDiagnostics (Persist) | " +
            $"current_character_id={characterId} | " +
            $"current_character_display_name={currentDisplayName} | " +
            $"ic_candidate_count={result.CharacterIcMemoryCandidates.Count} | " +
            $"inserted_character_ic_count={insertedIcCount} | " +
            $"dropped_character_ic_count={droppedIcCount} | " +
            $"drop_reasons={FormatDropReasons(dropReasons)} | " +
            $"history_deleted=true");
    }

    private static CombinedMemoryFoldResult BuildMinimalFoldResult(string characterId, List<ChatHistoryEntry> toFold)
    {
        // 根据新的设计要求：parse失败时不生成低质量记忆，而是返回空结果
        // 这样会导致折叠中止，历史不会被删除，等待下次折叠重新尝试
        return new CombinedMemoryFoldResult
        {
            CharacterIcMemoryCandidates = new(),
            PlayerTableMemoryCandidates = new(),
            TimelineSummary = new(),
            ObjectiveUpdates = new(),
            TableEventCandidates = new()
        };
    }

    private static List<string> BuildRawExcerpts(string rawExcerpt, List<string> fallback)
    {
        if (!string.IsNullOrWhiteSpace(rawExcerpt))
            return new List<string> { rawExcerpt };
        return fallback;
    }

    private static List<string> SplitCsv(string text)
        => string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static double Clamp(double value, double min, double max)
        => Math.Max(min, Math.Min(max, value));

    private static void RegisterDrop(Dictionary<string, int> dropReasons, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;
        dropReasons.TryGetValue(reason, out var count);
        dropReasons[reason] = count + 1;
    }

    private static string FormatDropReasons(Dictionary<string, int> dropReasons)
    {
        if (dropReasons.Count == 0)
            return "none";
        return string.Join(",", dropReasons
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}:{kvp.Value}"));
    }

    private static string SanitizeMemorySummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return "";
        var trimmed = summary.Trim();
        trimmed = Regex.Replace(trimmed, @"\s+", " ");
        trimmed = Regex.Replace(trimmed, @"(^|\n)\s*\[[^\]]+\]\s*[:：]", "$1", RegexOptions.Multiline);
        trimmed = Regex.Replace(trimmed, @"(^|\n)\s*[A-Za-z0-9_\-\u4e00-\u9fff]+\s*[:：]", "$1", RegexOptions.Multiline);
        if (trimmed.Length > 200)
            trimmed = trimmed.Substring(0, 200).TrimEnd();
        return trimmed;
    }

    private static bool LooksLikeRawTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (text.Length <= 220)
            return false;
        var matches = Regex.Matches(text, @"(\[GM-|\[PL-|\[OOC-|\[[^\]]+\]\s*[:：])", RegexOptions.IgnoreCase);
        return matches.Count >= 2;
    }

    private static string TrimForLog(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "...";
    }

    private static string NormalizeMemoryType(string? nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
            return "event";
        var normalized = nodeType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "event" => "event",
            "fact" => "fact",
            "scene" => "scene",
            "item" => "item",
            "threat" => "threat",
            "relationship" => "relationship",
            "emotion" => "emotion",
            "other" => "other",
            _ => normalized
        };
    }

    private Task<TrpgRuntimeState> ResolveFoldRuntimeStateAsync(TrpgScope scope, string characterId)
    {
        if (_stateCache != null && _stateCache.TryGet(scope, characterId, out var cached))
        {
            var state = CloneRuntimeState(cached);
            if (!state.PresentEntities.Contains(characterId, StringComparer.OrdinalIgnoreCase))
                state.PresentEntities.Add(characterId);
            return Task.FromResult(state);
        }

        var fallback = new TrpgRuntimeState
        {
            CurrentSceneId = "",
            LatestGmNarrative = "",
            PresentEntities = new List<string> { characterId }
        };
        return Task.FromResult(fallback);
    }

    private async Task<SceneSnapshotExtended?> TryGetLatestSceneSnapshotAsync(TrpgScope scope, string? sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            return null;

        try
        {
            return await _db.GetLatestSceneSnapshotAsync(scope, sceneId);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ResolveFoldSceneDescriptionAsync(
        TrpgScope scope,
        TrpgRuntimeState state,
        SceneSnapshotExtended? snapshot)
    {
        var sceneDesc = state.SceneState?.Description;
        if (!string.IsNullOrWhiteSpace(sceneDesc))
            return sceneDesc;

        if (!string.IsNullOrWhiteSpace(state.CurrentSceneId))
        {
            sceneDesc = await _db.GetSceneBaseDescAsync(scope, state.CurrentSceneId);
            if (!string.IsNullOrWhiteSpace(sceneDesc))
                return sceneDesc;
        }

        if (snapshot?.SceneFlags != null
            && snapshot.SceneFlags.TryGetValue("scene_description", out var sceneDescription)
            && !string.IsNullOrWhiteSpace(sceneDescription?.ToString()))
        {
            return sceneDescription.ToString();
        }

        return null;
    }

    private static string BuildFoldCurrentSceneText(string? sceneDesc, IReadOnlyList<string> presentEntities)
    {
        if (string.IsNullOrWhiteSpace(sceneDesc))
            return "当前场景未知";

        var names = presentEntities
            .Select(ExtractNameFromEntityId)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();

        if (names.Count == 0)
            return sceneDesc.Trim();

        return $"{sceneDesc.Trim()}\n在场：{string.Join("、", names)}";
    }

    private static string BuildFoldSceneSnapshotText(
        string? sceneId,
        TrpgRuntimeState state,
        SceneSnapshotExtended? snapshot,
        string? sceneDesc)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(sceneId))
            lines.Add($"场景 ID: {sceneId}");

        if (!string.IsNullOrWhiteSpace(sceneDesc))
            lines.Add($"场景描述: {sceneDesc.Trim()}");

        var present = state.PresentEntities
            .Select(ExtractNameFromEntityId)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (present.Count == 0 && snapshot?.PresentEntityIds != null)
        {
            present = snapshot.PresentEntityIds
                .Select(ExtractNameFromEntityId)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (present.Count > 0)
            lines.Add($"在场: {string.Join("、", present)}");

        return lines.Count == 0 ? "无" : string.Join("\n", lines);
    }

    private static string ResolveLatestFoldObjectiveText(TrpgRuntimeState state, IReadOnlyList<ChatHistoryEntry> activeHistory)
    {
        if (!string.IsNullOrWhiteSpace(state.LatestGmNarrative))
            return state.LatestGmNarrative;

        return activeHistory
            .OrderByDescending(entry => entry.CreatedAt)
            .Select(entry => entry.Content?.Trim())
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content))
            ?? "";
    }

    private static List<TimelineNode> BuildFoldRelevantTimeline(IReadOnlyList<TimelineNode> visibleTimeline, string? sceneId)
    {
        var visible = visibleTimeline
            .Where(node => node.Status == TimelineNodeStatus.Visible)
            .Where(node => node.Layer is TimelineLayer.L1 or TimelineLayer.L2 or TimelineLayer.L3)
            .Where(node => !string.IsNullOrWhiteSpace(node.Content))
            .OrderBy(node => node.EventSequence)
            .ToList();
        if (visible.Count == 0)
            return new List<TimelineNode>();

        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            var sameScene = visible
                .Where(node => string.Equals(node.SceneId, sceneId, StringComparison.OrdinalIgnoreCase))
                .TakeLast(12)
                .ToList();
            if (sameScene.Count > 0)
                return sameScene;
        }

        return visible.TakeLast(8).ToList();
    }

    private static string NormalizeSceneId(string? sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            return "";

        var normalized = sceneId.Trim();
        if (normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("scene_unknown", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("fold_active_scene", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return normalized;
    }

    private static TrpgRuntimeState CloneRuntimeState(TrpgRuntimeState source)
    {
        return new TrpgRuntimeState
        {
            CurrentSceneId = source.CurrentSceneId,
            PreviousSceneId = source.PreviousSceneId,
            PresentEntities = source.PresentEntities.ToList(),
            PlayerStatus = source.PlayerStatus,
            UpdatedAt = source.UpdatedAt,
            LatestGmNarrative = source.LatestGmNarrative,
            LatestSituationSummary = source.LatestSituationSummary,
            LatestFacts = source.LatestFacts.ToList(),
            LatestEvents = source.LatestEvents.ToList(),
            LastExtractionAt = source.LastExtractionAt,
            SceneState = source.SceneState == null
                ? null
                : new SceneState
                {
                    SceneId = source.SceneState.SceneId,
                    Description = source.SceneState.Description,
                    Properties = new Dictionary<string, object>(source.SceneState.Properties),
                    UpdatedAt = source.SceneState.UpdatedAt
                },
            WorldState = source.WorldState
        };
    }

    private static string ExtractNameFromEntityId(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "";

        var trimmed = entityId.Trim();
        var slash = trimmed.LastIndexOf('/');
        var leaf = slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        var underscore = leaf.LastIndexOf('_');
        if (underscore >= 0 && underscore < leaf.Length - 1)
            return leaf[(underscore + 1)..];
        return leaf;
    }

    private static (bool IsResolved, string CharacterId, string Reason) ResolveCandidateCharacterId(
        string? candidateCharacter,
        string currentCharacterId,
        string currentCharacterDisplayName)
    {
        if (string.IsNullOrWhiteSpace(candidateCharacter))
            return (true, currentCharacterId, "");

        if (string.Equals(candidateCharacter, currentCharacterId, StringComparison.OrdinalIgnoreCase))
            return (true, currentCharacterId, "");

        if (!string.IsNullOrWhiteSpace(currentCharacterDisplayName)
            && string.Equals(candidateCharacter, currentCharacterDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return (true, currentCharacterId, "");
        }

        return (false, "", string.IsNullOrWhiteSpace(candidateCharacter) ? "MissingCharacterId" : "CharacterIdMismatch");
    }

    private static string BuildCandidateMetadataJson(CharacterIcMemoryCandidate candidate, List<string> sourceIds)
    {
        var metadata = new Dictionary<string, object>
        {
            { "source", "CombinedMemoryFoldRequest" },
            { "keywords", candidate.Keywords ?? "" },
            { "node_type", candidate.NodeType ?? "" },
            { "ic_evidence", candidate.IcEvidence ?? "" },
            { "source_message_ids", sourceIds },
            { "raw_excerpt", candidate.RawExcerpt ?? "" }
        };
        return JsonSerializer.Serialize(metadata);
    }

    private async Task<MemoryNode?> LegacyDisabledTimelineRollupNodeAsync(List<ChatHistoryEntry> toFold)
    {
        if (toFold.Count == 0) return null;

        var first = toFold.Min(x => x.CreatedAt);
        var last = toFold.Max(x => x.CreatedAt);

        // 构建历史记录文本，用于 AI 提取结构化事件
        var historyText = string.Join("\n", toFold
            .OrderBy(x => x.CreatedAt)
            .Select(x => $"[{x.SpeakerName}]: {x.Content?.Trim()}"));

        // 调用 AI 生成结构化事件列表
        var events = await LegacyDisabledWorldEventsAsync(historyText, first, last);

        string summary;
        string keywords;

        if (events.Count == 0)
        {
            // 如果 AI 调用失败，回退到简单概括
            summary = await LegacyDisabledTimelineSummaryAsync(historyText, first, last);
            var speakers = toFold
                .Select(x => x.SpeakerName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8);
            keywords = string.Join(" ", new[] { "流程", "回顾", "时间线" }.Concat(speakers));

            return new MemoryNode
            {
                Summary = summary,
                Keywords = keywords,
                NodeType = "timeline",
                Importance = 0.95,
                RawExcerpt = "[]"
            };
        }

        // 构建事件列表摘要
        summary = $"时间线（{first:MM-dd HH:mm}~{last:MM-dd HH:mm}）：\n{string.Join("\n", events.Select((e, i) => $"{i + 1}. {e.ToPromptString()}"))}";

        // 提取所有参与者和关键词
        var allActors = events.SelectMany(e => e.Actors).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        keywords = string.Join(" ", new[] { "流程", "回顾", "时间线" }.Concat(allActors));

        // 保留所有原文切片，让 AI 在检索时主动调用
        var allExcerpts = toFold
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Content?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        // 将所有原文切片保存为 JSON 数组
        var rawExcerptJson = allExcerpts.Count > 0
            ? JsonSerializer.Serialize(allExcerpts)
            : "[]";

        return new MemoryNode
        {
            Summary = summary,
            Keywords = keywords,
            NodeType = "timeline",
            Importance = 0.95,
            RawExcerpt = rawExcerptJson
        };
    }

    private async Task<string> LegacyDisabledTimelineSummaryAsync(string historyText, DateTime first, DateTime last)
    {
        var prompt = $@"请概括以下 TRPG 对话记录的时间线，要求：
1. 提取关键事件（重要对话、行动、场景变化）
2. 简洁明了，每条不超过 30 字
3. 按时间顺序编号
4. 忽略闲聊和无关内容

时间范围：{first:MM-dd HH:mm}~{last:MM-dd HH:mm}

对话记录：
{historyText}

请输出时间线概括（格式：1. 事件描述）：";

        var messages = new List<ChatMessage>
        {
            new ChatMessage("user", prompt)
        };

        var response = "";
        if (string.IsNullOrWhiteSpace(response))
        {
            // 如果 AI 调用失败，回退到简单拼接
            return $"时间线（{first:MM-dd HH:mm}~{last:MM-dd HH:mm}）：AI 概括失败，使用原始记录";
        }

        return response.Trim();
    }

    private async Task<List<WorldEvent>> LegacyDisabledWorldEventsAsync(string historyText, DateTime first, DateTime last)
    {
        var prompt = $@"请从以下 TRPG 对话记录中提取结构化事件列表，要求：
1. 识别关键事件（场景转换、战斗、发现、物品获取、NPC死亡、关系变化等）
2. 每个事件包含：event_type, actors, location, result, world_changes
3. event_type 可选值：scene_transition, combat, dialogue, discovery, item_acquisition, npc_death, relationship_change
4. 忽略闲聊和无关内容
5. 输出 JSON 数组格式

时间范围：{first:MM-dd HH:mm}~{last:MM-dd HH:mm}

对话记录：
{historyText}

请输出结构化事件列表（JSON 数组格式）：";

        var messages = new List<ChatMessage>
        {
            new ChatMessage("user", prompt)
        };

        var response = "";
        if (string.IsNullOrWhiteSpace(response))
        {
            return new List<WorldEvent>();
        }

        try
        {
            // 尝试解析 JSON 数组
            var json = response.Trim();
            if (json.StartsWith("```json"))
                json = json.Substring(7);
            else if (json.StartsWith("```"))
                json = json.Substring(3);
            if (json.EndsWith("```"))
                json = json.Substring(0, json.Length - 3);
            json = json.Trim();

            var array = JsonSerializer.Deserialize<JsonArray>(json);
            if (array == null) return new List<WorldEvent>();

            var events = new List<WorldEvent>();
            foreach (var item in array)
            {
                if (item is JsonObject obj)
                {
                    var worldEvent = new WorldEvent
                    {
                        EventType = obj["event_type"]?.ToString() ?? "dialogue",
                        Actors = obj["actors"] is JsonArray actorsArray
                            ? actorsArray.Select(x => x.ToString()).ToList()
                            : new List<string>(),
                        Location = obj["location"]?.ToString() ?? "",
                        Result = obj["result"]?.ToString() ?? "",
                        WorldChanges = obj["world_changes"] is JsonArray changesArray
                            ? changesArray.Select(x => x.ToString()).ToList()
                            : new List<string>()
                    };
                    events.Add(worldEvent);
                }
            }

            return events;
        }
        catch
        {
            // JSON 解析失败，返回空列表
            return new List<WorldEvent>();
        }
    }

    /// <summary>
    /// 规则层映射 category 到 importance，避免 AI 直接给 importance 导致漂移
    /// 离散化 importance：0.1闲聊、0.3普通动作、0.5有后续价值、0.8重大线索、1.0改变世界状态
    /// </summary>
    private static double MapCategoryToImportance(string category)
    {
        return category.ToLower() switch
        {
            "npc_death" => 1.0,        // 改变世界状态
            "scene_change" => 0.5,      // 有后续价值
            "combat" => 0.8,            // 重大线索
            "dialogue" => 0.1,          // 闲聊
            "discovery" => 0.5,         // 有后续价值
            "emotion" => 0.1,           // 闲聊
            "item" => 0.3,              // 普通动作
            "relationship" => 0.5,     // 有后续价值
            "choice" => 0.8,            // 重大线索
            "boss" => 1.0,              // 改变世界状态
            _ => 0.1                    // 闲聊（默认）
        };
    }

    /// <summary>
    /// 规范化 NodeType：将旧类型映射到新类型
    /// 新类型：FACT, EVENT, NPC_STATE, RELATIONSHIP, WORLD_STATE, GOAL
    /// </summary>
    private static string NormalizeNodeType(string nodeType, string category)
    {
        var normalized = nodeType.Trim().ToUpperInvariant();

        // 如果已经是新类型，直接返回
        if (normalized is "FACT" or "EVENT" or "NPC_STATE" or "RELATIONSHIP" or "WORLD_STATE" or "GOAL")
            return normalized;

        // 旧类型映射
        var categoryLower = category.ToLowerInvariant();
        return normalized switch
        {
            "FACT" or "fact" => "FACT",
            "EVENT" or "event" => "EVENT",
            "INTERPRETATION" or "interpretation" => "NPC_STATE",
            "TIMELINE" or "timeline" => "EVENT",
            _ => categoryLower switch
            {
                "npc_death" => "WORLD_STATE",
                "scene_change" => "EVENT",
                "combat" => "EVENT",
                "dialogue" => "EVENT",
                "discovery" => "FACT",
                "emotion" => "NPC_STATE",
                "item" => "FACT",
                "relationship" => "RELATIONSHIP",
                "choice" => "EVENT",
                "boss" => "WORLD_STATE",
                _ => "FACT"  // 默认为 FACT
            }
        };
    }

    /// <summary>
    /// 本地解析 AI 返回的语义节点 JSON
    /// </summary>
    private List<MemoryNode> ParseSemanticNodes(string jsonText)
    {
        var nodes = new List<MemoryNode>();
        try
        {
            // 清理 Markdown 代码块包装
            var cleaned = jsonText.Trim();
            if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(7);
            else if (cleaned.StartsWith("```"))
                cleaned = cleaned.Substring(3);
            if (cleaned.EndsWith("```"))
                cleaned = cleaned.Substring(0, cleaned.Length - 3);
            cleaned = cleaned.Trim();

            var array = JsonSerializer.Deserialize<JsonArray>(cleaned);
            if (array == null) return nodes;

            foreach (var item in array)
            {
                if (item is JsonObject obj)
                {
                    var summary = obj["summary"]?.ToString() ?? "";
                    var keywords = obj["keywords"]?.ToString() ?? "";
                    var nodeType = obj["type"]?.ToString() ?? "event";
                    var category = obj["category"]?.ToString() ?? "other";

                    // 规则层映射 importance，不使用 AI 的 importance
                    var importance = MapCategoryToImportance(category);

                    var confidence = 1.0;
                    if (obj["confidence"] is JsonValue confVal && confVal.TryGetValue<double>(out var conf))
                        confidence = conf;

                    // 规范化 NodeType：将旧类型映射到新类型
                    nodeType = NormalizeNodeType(nodeType, category);

                    // 对于 FACT 类型，从 actors/location/facts 构建更丰富的 keywords
                    if (nodeType == "FACT")
                    {
                        var actors = obj["actors"] as JsonArray;
                        var location = obj["location"]?.ToString() ?? "";
                        var facts = obj["facts"] as JsonArray;

                        var enrichedKeywords = new List<string>(keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                        if (actors != null)
                        {
                            foreach (var actor in actors)
                                enrichedKeywords.Add(actor.ToString());
                        }
                        if (!string.IsNullOrWhiteSpace(location))
                            enrichedKeywords.Add(location);
                        if (facts != null)
                        {
                            foreach (var fact in facts)
                                enrichedKeywords.Add(fact.ToString());
                        }
                        keywords = string.Join(" ", enrichedKeywords);
                    }

                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        nodes.Add(new MemoryNode
                        {
                            Summary = summary,
                            Keywords = keywords,
                            NodeType = nodeType,
                            Importance = importance,
                            Confidence = confidence
                        });
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 节点 JSON 解析失败: {ex.Message}");
        }
        return nodes;
    }

    /// <summary>
    /// IC-only LLM repair：折叠窗口有当前角色 IC 材料但 LLM 未产出 IC candidates 时，单独请求补 IC 记忆
    /// </summary>
    private async Task<CombinedMemoryFoldResult?> TryIcOnlyRepairAsync(TrpgScope scope, string characterId, List<ChatHistoryEntry> toFold)
    {
        if (_apiCaller == null) return null;
        try
        {
            var historyText = string.Join("\n", toFold.OrderBy(e => e.CreatedAt)
                .Select(e => $"[{e.SpeakerName}]: {e.Content?.Trim()}"));
            var prompt = $$"""你正在为折叠窗口中的当前角色提取IC记忆。只输出JSON，不要markdown。当前折叠角色的GM第二人称"你"默认指当前角色。OOC/PL/其他角色私有视角不得进入。summary必须短语义摘要不得复制原文。{"character_ic_memory_candidates":[{"summary":"","keywords":"","node_type":"event","importance":0.5,"confidence":0.7,"source_message_ids":[],"raw_excerpt":"","ic_evidence":"","character_id":""}]} 折叠窗口原文：{{historyText}}""";

            var messages = new List<ChatMessage>
            {
                new("system", AimodPromptPrefixes.BackendCommonPrefixV1),
                new("user", prompt)
            };

            var response = await CallTrackedAsync(scope, characterId, messages, "MemoryWatchdog", "CombinedMemoryFoldIcOnlyRepair");

            if (string.IsNullOrWhiteSpace(response)) return null;
            var parseSuccess = CombinedMemoryFoldParser.TryParse(response, out var result, out _);
            if (parseSuccess && result.CharacterIcMemoryCandidates.Count > 0) return result;
            return null;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] IC-only repair failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 尝试 LLM JSON repair，只修格式不重新总结
    /// </summary>
    private async Task<string?> TryRepairJsonAsync(TrpgScope scope, string characterId, string? response)
    {
        if (_apiCaller == null || string.IsNullOrWhiteSpace(response) || response.Length < 10)
            return null;

        try
        {
            var repairPrompt = "以下 JSON 格式有误，请修复使其成为合法 JSON，只输出修复后的 JSON，不要任何解释：\n\n" + response;
            var messages = new List<ChatMessage>
            {
                new("system", "你是一个 JSON 修复器。只输出合法的 JSON，不要任何解释或 markdown。"),
                new("user", repairPrompt)
            };
            var repaired = await CallTrackedAsync(scope, characterId, messages, "MemoryWatchdog", "CombinedMemoryFoldJsonRepair");
            if (string.IsNullOrWhiteSpace(repaired)) return null;

            // 提取 JSON
            var cleaned = repaired.Trim();
            if (cleaned.StartsWith("```")) cleaned = cleaned.Substring(cleaned.IndexOf('\n') + 1).Trim();
            if (cleaned.EndsWith("```")) cleaned = cleaned.Substring(0, cleaned.LastIndexOf("```")).Trim();

            _context.Log(LogLevel.Info,
                $"[AIMod:TRPG] JSON repair attempted | Char={characterId} | response_len={response.Length} | repaired_len={cleaned.Length}");
            return cleaned;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] JSON repair failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> CallTrackedAsync(TrpgScope scope, string characterId, List<ChatMessage> messages, string agentName, string requestKind)
    {
        if (_llmCallTracker != null)
            return await _llmCallTracker.CallAsync(scope, characterId, messages, agentName, requestKind, _apiCaller);

        return await _apiCaller.Invoke(messages);
    }

    private static string? TryRepairJsonLocally(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var json = ExtractJsonObject(response);
        return string.IsNullOrWhiteSpace(json) ? null : json;
    }

    private static string? ExtractJsonObject(string raw)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = cleaned.IndexOf('\n');
            if (firstBreak >= 0)
                cleaned = cleaned[(firstBreak + 1)..].Trim();
            if (cleaned.EndsWith("```", StringComparison.Ordinal))
                cleaned = cleaned[..cleaned.LastIndexOf("```", StringComparison.Ordinal)].Trim();
        }

        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        return cleaned[start..(end + 1)];
    }

}
