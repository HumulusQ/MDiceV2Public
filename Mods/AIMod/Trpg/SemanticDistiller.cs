using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 语义蒸馏器（Semantic Distiller）
/// 
/// 职责：在事件进入长期层时，调用 LLM 生成语义元数据并固化
/// 
/// 设计原则：
/// - 不是每次 prompt 重新生成，而是在特定触发点批量处理
/// - LLM 只生成语义元数据，不生成事件本身
/// - 蒸馏结果固化到 WorldEvent，避免重复计算
/// </summary>
public class SemanticDistiller
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly LlmCallTracker? _llmCallTracker;

    // 蒸馏触发阈值：每积累 50 个未蒸馏事件触发一次
    private const int DistillationBatchSize = 50;
    
    // 最大处理事件数：避免单次 LLM 调用 token 过大
    private const int MaxEventsPerDistillation = 200;

    public SemanticDistiller(
        IModContext context,
        ChatDatabase db,
        EventLog eventLog,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        LlmCallTracker? llmCallTracker = null)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _apiCaller = apiCaller;
        _llmCallTracker = llmCallTracker;
    }

    /// <summary>
    /// 检查并触发语义蒸馏
    /// 当未蒸馏事件数量达到阈值时执行
    /// </summary>
    public async Task CheckAndDistillAsync(TrpgScope scope, string characterId)
    {
        var undistilledEvents = await _db.QueryUndistilledEventsAsync(scope, MaxEventsPerDistillation);
        var firstEventId = undistilledEvents.Count > 0 ? undistilledEvents.First().EventId : 0;
        var lastEventId = undistilledEvents.Count > 0 ? undistilledEvents.Last().EventId : 0;
        var firstIds = string.Join(",", undistilledEvents.Take(10).Select(e => e.EventId));
        var triggered = undistilledEvents.Count >= DistillationBatchSize;

        _context.Log(LogLevel.Info,
            "[AIMod:TRPG:SemanticDistiller:Check] " +
            $"WorldId={scope.WorldId} | CharacterId={characterId} | " +
            $"UndistilledCount={undistilledEvents.Count} | BatchSize={DistillationBatchSize} | " +
            $"MaxEvents={MaxEventsPerDistillation} | FirstEventId={firstEventId} | LastEventId={lastEventId} | " +
            $"First10EventIds={firstIds} | Triggered={triggered} | Reason=threshold");

        if (undistilledEvents.Count < DistillationBatchSize)
            return;

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] 触发语义蒸馏：{undistilledEvents.Count} 个未蒸馏事件");

        await DistillEventsAsync(scope, undistilledEvents, characterId);
    }

    /// <summary>
    /// 对一批事件执行语义蒸馏
    /// </summary>
    private async Task DistillEventsAsync(TrpgScope scope, List<WorldEvent> events, string characterId)
    {
        if (events.Count == 0)
            return;

        // ===== 预先过滤垃圾节点 =====
        var (validEvents, garbageEvents) = SplitGarbageEvents(events);
        var garbageCount = garbageEvents.Count;
        var markedDistilledCount = 0;
        var failedWritebackIds = new List<long>();
        var narrativeCreatedCount = 0;

        if (garbageEvents.Count > 0)
            markedDistilledCount += await MarkGarbageEventsDistilledAsync(scope, garbageEvents, failedWritebackIds);

        _context.Log(LogLevel.Info,
            "[AIMod:TRPG:SemanticDistiller:Batch] " +
            $"InputCount={events.Count} | ValidCount={validEvents.Count} | GarbageCount={garbageEvents.Count} | " +
            $"EventIds={FormatEventIds(events.Select(e => e.EventId))}");

        if (validEvents.Count == 0)
        {
            _context.Log(LogLevel.Warn,
                $"[AIMod:TRPG] DistillEventsAsync: no valid events (Input={events.Count}, Garbage={garbageEvents.Count})");
            await LogSemanticDistillerWritebackAsync(scope, markedDistilledCount, failedWritebackIds);
            return;
        }
        
        if (validEvents.Count == 0)
        {
            _context.Log(LogLevel.Warn, 
                $"[AIMod:TRPG] DistillEventsAsync: 无有效事件 (总数={events.Count}, 垃圾={garbageCount})");
            
            // 即使没有有效事件，仍然标记垃圾节点为已处理
            foreach (var evt in events.Where(e => IsGarbageEvent(e)))
            {
                evt.IsSemanticallyDistilled = true;
                await _db.UpdateEventSemanticMetadataAsync(scope, evt.EventId, evt);
            }
            
            return;
        }

        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] 叙事节点 LLM 蒸馏开始 | 事件数={validEvents.Count} | " +
            $"（已过滤{garbageCount}个垃圾节点） | EventIds={string.Join(",", validEvents.Take(3).Select(e => e.EventId))}{(validEvents.Count > 3 ? "..." : "")}");

        var prompt = BuildDistillationPrompt(validEvents);
        
        if (string.IsNullOrEmpty(prompt))
        {
            _context.Log(LogLevel.Error, "[AIMod:TRPG] Prompt 构建失败");
            return;
        }
        
        var response = await CallLlmAsync(scope, characterId, prompt, "DistillEvents", "你是TRPG语义蒸馏器。你只为已记录桌面事件生成语义元数据，不补充未确认事实，不替GM判定。");
        if (string.IsNullOrEmpty(response))
        {
            _context.Log(LogLevel.Warn, "[AIMod:TRPG] 语义蒸馏 LLM 返回空响应");
            return;
        }
        
        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] 叙事节点 LLM 蒸馏完成 | 事件数={validEvents.Count}");
        
        var invalidKeys = new List<string>();
        var distillationResults = ParseDistillationResponse(response, invalidKeys);
        var unmatchedEventIds = validEvents
            .Select(e => e.EventId)
            .Where(id => !distillationResults.ContainsKey(id))
            .ToList();

        _context.Log(LogLevel.Info,
            "[AIMod:TRPG:SemanticDistiller:Parse] " +
            $"ParsedCount={distillationResults.Count} | ParsedEventIds={FormatEventIds(distillationResults.Keys)} | " +
            $"UnmatchedEventIds={FormatEventIds(unmatchedEventIds)} | InvalidKeys={string.Join(",", invalidKeys.Take(20))}");

        // 将蒸馏结果固化回事件并生成叙事记忆节点
        foreach (var evt in validEvents)
        {
            if (distillationResults.TryGetValue(evt.EventId, out var result))
            {
                // 更新 WorldEvent 语义元数据
                evt.SemanticSummary = result.SemanticSummary;
                evt.NarrativeWeight = result.NarrativeWeight;
                evt.NarrativeTags = result.NarrativeTags;
                evt.EmotionalWeight = result.EmotionalWeight;
                evt.ArcAffinity = result.ArcAffinity;
                evt.IsSemanticallyDistilled = true;

                await _db.UpdateEventSemanticMetadataAsync(scope, evt.EventId, evt);
                markedDistilledCount++;

                var resolvedCount = await _db.ResolveNarrativeMemoryNodesByEventAsync(scope, characterId, evt);
                if (resolvedCount > 0)
                {
                    _context.Log(LogLevel.Info,
                        $"[AIMod:TRPG:NarrativeMemory] Resolved narrative nodes | EventId={evt.EventId} | Count={resolvedCount}");
                }

                if (!ShouldCreateNarrativeMemoryNode(evt, result))
                {
                    _context.Log(LogLevel.Debug,
                        $"[AIMod:TRPG:NarrativeMemory] Skip narrative node | EventId={evt.EventId} | Type={evt.EventType} | Reason=no_narrative_anchor");
                    continue;
                }

                // 生成叙事记忆节点（认知层）
                var memoryNode = new NarrativeMemoryNode
                {
                    Summary = result.SemanticSummary ?? "",
                    NarrativeWeight = (float)result.NarrativeWeight,
                    EmotionalWeight = (float)result.EmotionalWeight,
                    RelationshipImpact = CalculateRelationshipImpact(evt, result),
                    GoalImpact = CalculateGoalImpact(evt, result),
                    MysteryWeight = CalculateMysteryWeight(evt, result),
                    IsResolved = CreatesResolvedNarrativeNode(evt.EventType),
                    InvolvedEntities = evt.Actors
                        .Concat(new[] { evt.SourceEntityId, evt.TargetEntityId })
                        .Where(e => !string.IsNullOrEmpty(e))
                        .Select(e => e!)
                        .Distinct()
                        .ToList(),
                    ArcTags = BuildNarrativeArcTags(evt, result),
                    Timestamp = evt.Timestamp,
                    SourceEventId = evt.EventId
                };

                await _db.InsertNarrativeMemoryNodeAsync(scope, characterId, memoryNode);
                narrativeCreatedCount++;
                
                _context.Log(LogLevel.Debug,
                    $"[AIMod:TRPG] 叙事节点已创建 | Summary={memoryNode.Summary.Substring(0, Math.Min(50, memoryNode.Summary.Length))} | Weight={memoryNode.NarrativeWeight:F2} | Emotion={memoryNode.EmotionalWeight:F2}");
            }
        }

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] 叙事节点生成完成 | 新增节点数={validEvents.Count(e => distillationResults.ContainsKey(e.EventId))} | 总处理事件数={validEvents.Count}");
        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] Narrative distillation finished | NarrativeNodesCreated={narrativeCreatedCount} | ProcessedEvents={validEvents.Count}");
        await LogSemanticDistillerWritebackAsync(scope, markedDistilledCount, failedWritebackIds);
    }

    private float CalculateRelationshipImpact(WorldEvent evt, SemanticDistillationResult result)
    {
        // 如果事件类型明确涉及关系变化
        if (evt.EventType.Equals("relationship_change", StringComparison.OrdinalIgnoreCase))
            return 0.8f;

        // 如果标签包含关系相关关键词
        if (result.NarrativeTags.Any(tag => tag.Contains("关系", StringComparison.OrdinalIgnoreCase) ||
                                         tag.Contains("背叛", StringComparison.OrdinalIgnoreCase) ||
                                         tag.Contains("信任", StringComparison.OrdinalIgnoreCase)))
            return 0.6f;

        return 0.2f;
    }

    private float CalculateGoalImpact(WorldEvent evt, SemanticDistillationResult result)
    {
        // 如果事件类型明确涉及目标
        if (evt.EventType.Equals("objective_complete", StringComparison.OrdinalIgnoreCase) ||
            evt.EventType.Equals("objective_update", StringComparison.OrdinalIgnoreCase))
            return 0.8f;

        // 如果标签包含目标相关关键词
        if (result.NarrativeTags.Any(tag => tag.Contains("目标", StringComparison.OrdinalIgnoreCase) ||
                                         tag.Contains("任务", StringComparison.OrdinalIgnoreCase)))
            return 0.6f;

        return 0.2f;
    }

    private float CalculateMysteryWeight(WorldEvent evt, SemanticDistillationResult result)
    {
        // 如果标签包含悬疑相关关键词
        if (result.NarrativeTags.Any(tag => tag.Contains("悬疑", StringComparison.OrdinalIgnoreCase) ||
                                         tag.Contains("秘密", StringComparison.OrdinalIgnoreCase) ||
                                         tag.Contains("谜团", StringComparison.OrdinalIgnoreCase)))
            return 0.7f;

        // 如果情绪权重为负（负面事件往往带有悬疑）
        if (result.EmotionalWeight < -0.3f)
            return 0.4f;

        return 0.1f;
    }

    /// <summary>
    /// 检测是否为垃圾节点
    /// 垃圾节点定义：
    /// - EventType 为 "narrative" 且无实际内容
    /// - 或参与者、位置、结果都为空
    /// - 或 Payload 为空或仅含null值
    /// </summary>
    private bool IsGarbageEvent(WorldEvent evt)
    {
        // 规则1: narrative 类型的空壳事件
        if (evt.EventType.Equals("narrative", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(evt.Result) &&
                   evt.Actors.Count == 0 &&
                   string.IsNullOrWhiteSpace(evt.Location) &&
                   evt.Payload.Count == 0;
        }

        // 规则2: 所有关键字段都为空
        if (string.IsNullOrWhiteSpace(evt.Result) &&
            evt.Actors.Count == 0 &&
            string.IsNullOrWhiteSpace(evt.Location) &&
            string.IsNullOrWhiteSpace(evt.SceneId))
        {
            // 但如果 Payload 有意义内容，仍保留
            if (evt.Payload.Count == 0 || 
                evt.Payload.All(p => p.Value == null || 
                                     (p.Value is string s && string.IsNullOrWhiteSpace(s))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 验证并统计垃圾节点
    /// </summary>
    private (List<WorldEvent> validEvents, int garbageCount) FilterGarbageEvents(List<WorldEvent> events)
    {
        var validEvents = new List<WorldEvent>();
        int garbageCount = 0;

        foreach (var evt in events)
        {
            if (IsGarbageEvent(evt))
            {
                garbageCount++;
                _context.Log(LogLevel.Debug, 
                    $"[AIMod:TRPG] 已过滤垃圾节点 | EventId={evt.EventId} | Type={evt.EventType}");
            }
            else
            {
                validEvents.Add(evt);
            }
        }

        if (garbageCount > 0)
        {
            _context.Log(LogLevel.Info, 
                $"[AIMod:TRPG] 垃圾节点过滤完成 | 总数={events.Count} | 有效={validEvents.Count} | 垃圾={garbageCount} | 过滤率={(garbageCount * 100.0 / events.Count):F1}%");
        }

        return (validEvents, garbageCount);
    }

    private (List<WorldEvent> validEvents, List<WorldEvent> garbageEvents) SplitGarbageEvents(List<WorldEvent> events)
    {
        var validEvents = new List<WorldEvent>();
        var garbageEvents = new List<WorldEvent>();

        foreach (var evt in events)
        {
            if (IsGarbageEvent(evt))
            {
                garbageEvents.Add(evt);
                _context.Log(LogLevel.Debug,
                    $"[AIMod:TRPG:SemanticDistiller] Filtered garbage event | EventId={evt.EventId} | Type={evt.EventType}");
            }
            else
            {
                validEvents.Add(evt);
            }
        }

        return (validEvents, garbageEvents);
    }

    private async Task<int> MarkGarbageEventsDistilledAsync(
        TrpgScope scope,
        List<WorldEvent> garbageEvents,
        List<long> failedWritebackIds)
    {
        var marked = 0;
        foreach (var evt in garbageEvents)
        {
            try
            {
                evt.SemanticSummary = string.IsNullOrWhiteSpace(evt.SemanticSummary)
                    ? "Low value event skipped by semantic distiller."
                    : evt.SemanticSummary;
                evt.NarrativeWeight = 0.0;
                evt.NarrativeTags ??= new List<string>();
                evt.EmotionalWeight = 0.0;
                evt.IsSemanticallyDistilled = true;
                await _db.UpdateEventSemanticMetadataAsync(scope, evt.EventId, evt);
                marked++;
            }
            catch (Exception ex)
            {
                failedWritebackIds.Add(evt.EventId);
                _context.Log(LogLevel.Warn,
                    $"[AIMod:TRPG:SemanticDistiller:Writeback] Failed to mark garbage event distilled | EventId={evt.EventId} | Error={ex.Message}");
            }
        }

        return marked;
    }

    private async Task LogSemanticDistillerWritebackAsync(
        TrpgScope scope,
        int markedDistilledCount,
        List<long> failedWritebackIds)
    {
        var remaining = await _db.QueryUndistilledEventsAsync(scope, MaxEventsPerDistillation);
        _context.Log(LogLevel.Info,
            "[AIMod:TRPG:SemanticDistiller:Writeback] " +
            $"MarkedDistilledCount={markedDistilledCount} | FailedWritebackIds={FormatEventIds(failedWritebackIds)} | " +
            $"RemainingUndistilledCount={remaining.Count} | FirstRemainingIds={FormatEventIds(remaining.Take(10).Select(e => e.EventId))}");
    }

    private static string FormatEventIds(IEnumerable<long> ids)
    {
        var list = ids.Take(30).ToList();
        if (list.Count == 0)
            return "";

        var suffix = ids.Skip(30).Any() ? "..." : "";
        return string.Join(",", list) + suffix;
    }

    private bool ShouldCreateNarrativeMemoryNode(WorldEvent evt, SemanticDistillationResult result)
    {
        if (evt == null || IsGarbageEvent(evt))
            return false;

        var type = evt.EventType?.Trim().ToLowerInvariant() ?? "";
        var summary = result.SemanticSummary ?? evt.Result ?? "";
        var tags = result.NarrativeTags ?? new List<string>();
        var arc = result.ArcAffinity ?? "";

        if (type is "scene_transition" or "flow" or "narrative")
            return ContainsNarrativeAnchor(summary, tags, arc) || ContainsNarrativeAnchor(DecodeEventContent(evt), tags, arc);

        if (type is "relationship_change"
            or "objective_change"
            or "objective_update"
            or "objective_complete"
            or "objective_failure"
            or "objective_failed"
            or "npc_identity_reveal"
            or "identity_reveal"
            or "discovery"
            or "item_acquisition"
            or "item_loss"
            or "inventory_change")
            return true;

        if (Math.Abs(result.EmotionalWeight) >= 0.45)
            return true;

        if (result.NarrativeWeight >= 0.55)
            return true;

        if (ContainsNarrativeAnchor(summary, tags, arc))
            return true;

        return ContainsNarrativeAnchor(DecodeEventContent(evt), tags, arc);
    }

    private static bool ContainsNarrativeAnchor(string? summary, List<string> tags, string? arc)
    {
        var haystack = NormalizeSemanticText($"{summary} {arc} {string.Join(" ", tags ?? new List<string>())}");
        if (haystack.Length == 0)
            return false;

        foreach (var anchor in NarrativeAnchorTerms)
        {
            var normalized = NormalizeSemanticText(anchor);
            if (normalized.Length >= 2 && haystack.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static readonly string[] NarrativeAnchorTerms =
    {
        "身份", "误解", "伪装", "别名", "错认", "承诺", "威胁", "背叛", "信任", "债务",
        "钥匙", "账本", "包裹", "证据", "画像", "会合", "秘密", "谜团", "伏笔", "未解决",
        "目标", "任务", "关系", "创伤", "恐惧", "怀疑",
        "identity", "misunderstanding", "disguise", "alias", "promise", "threat", "betrayal",
        "trust", "debt", "key", "ledger", "package", "evidence", "portrait", "rendezvous",
        "secret", "mystery", "foreshadow", "unresolved", "objective", "goal", "quest",
        "relationship", "trauma", "fear", "suspicion"
    };

    private static bool CreatesResolvedNarrativeNode(string? eventType)
    {
        var type = eventType?.Trim().ToLowerInvariant() ?? "";
        return type is "objective_complete"
            or "objective_failure"
            or "objective_failed"
            or "npc_identity_reveal"
            or "identity_reveal"
            or "mystery_reveal"
            or "item_acquisition"
            or "item_loss"
            or "item_consume"
            or "item_consumed"
            or "gm_correction";
    }

    private static List<string> BuildNarrativeArcTags(WorldEvent evt, SemanticDistillationResult result)
    {
        var tags = new List<string>();
        if (result.NarrativeTags != null)
            tags.AddRange(result.NarrativeTags.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (!string.IsNullOrWhiteSpace(result.ArcAffinity))
            tags.Add(result.ArcAffinity!);
        if (!string.IsNullOrWhiteSpace(evt.EventType))
            tags.Add(evt.EventType);

        return tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string TrimForLog(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string NormalizeSemanticText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var chars = normalized
            .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
            .ToArray();
        return new string(chars);
    }

    /// <summary>
    /// 构建语义蒸馏 Prompt（含垃圾节点过滤 + 紧凑格式）
    /// </summary>
    private string BuildDistillationPrompt(List<WorldEvent> events)
    {
        // ===== 阶段1: 过滤垃圾节点 =====
        var validEvents = events.Where(e => e != null).ToList();
        var garbageCount = 0;
        
        if (validEvents.Count == 0)
        {
            _context.Log(LogLevel.Warn, 
                "[AIMod:TRPG] BuildDistillationPrompt: 无有效事件可处理");
            return "";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("你是一个叙事分析专家。请分析以下事件列表，为每个事件生成语义元数据。");
        sb.AppendLine();
        sb.AppendLine("输出格式（JSON）：");
        sb.AppendLine("events object keys must use pure numeric event IDs such as \"123\"; do not use \"Event_123\".");
        sb.AppendLine("{");
        sb.AppendLine("  \"events\": {");
        sb.AppendLine("    \"事件ID\": {");
        sb.AppendLine("      \"semantic_summary\": \"事件的叙事性总结（1句话，避免技术术语）\",");
        sb.AppendLine("      \"narrative_weight\": 0.0-1.0,");
        sb.AppendLine("      \"narrative_tags\": [\"标签1\", \"标签2\"],");
        sb.AppendLine("      \"emotional_weight\": -1.0-1.0,");
        sb.AppendLine("      \"arc_affinity\": \"剧情弧标识（可选）\"");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("事件列表：");
        sb.AppendLine("---");

        // ===== 阶段2: 优化事件描述（解码编码内容为可读中文） =====
        foreach (var evt in validEvents)
        {
            sb.AppendLine($"[Event_{evt.EventId}]");
            
            // 使用 DecodeEventContent 来获取解码后的中文描述
            var decodedContent = DecodeEventContent(evt);
            sb.AppendLine(decodedContent);
            sb.AppendLine($"  时间: {evt.Timestamp:yyyy-MM-dd HH:mm}");
            
            sb.AppendLine();
        }

        // ===== 阶段3: 记录统计 =====
        var originalCount = events.Count;
        var finalCount = validEvents.Count;
        var tokenReduction = (garbageCount * 25); // 估计每个垃圾节点25 tokens
        
        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] Prompt构建完成 | 原始={originalCount} | 有效={finalCount} | " +
            $"过滤={garbageCount} | 已解码内容 | 估计节省={tokenReduction} tokens");

        return sb.ToString();
    }

    /// <summary>
    /// 本地化事件类型为中文
    /// </summary>
    private string LocalizeEventType(string eventType)
    {
        return eventType.ToLowerInvariant() switch
        {
            "scene_transition" => "场景转换",
            "state_transaction" => "状态事务",
            "narrative" => "叙事",
            "combat" => "战斗",
            "dialogue" => "对话",
            "discovery" => "发现",
            "item_acquisition" => "获得物品",
            "npc_death" => "NPC死亡",
            "relationship_change" => "关系变化",
            "npc_identity_reveal" => "NPC身份揭示",
            "objective_change" => "目标变化",
            "objective_complete" => "目标完成",
            "objective_update" => "目标更新",
            _ => eventType
        };
    }

    /// <summary>
    /// 解码并转换事件内容为可读的中文描述
    /// （处理编码、Base64、JSON等各种格式的事件数据）
    /// </summary>
    private string DecodeEventContent(WorldEvent evt)
    {
        var sb = new System.Text.StringBuilder();
        
        // ===== 尝试解析 Payload 中的编码内容 =====
        var decodedPayload = new Dictionary<string, string>();
        
        foreach (var kvp in evt.Payload)
        {
            try
            {
                if (kvp.Value == null)
                    continue;

                string decoded = kvp.Value.ToString()!;
                
                // 尝试 Base64 解码
                try
                {
                    if (IsBase64(decoded))
                    {
                        byte[] data = Convert.FromBase64String(decoded);
                        decoded = System.Text.Encoding.UTF8.GetString(data);
                    }
                }
                catch { /* 非 Base64 格式，保持原值 */ }
                
                decodedPayload[kvp.Key] = decoded;
            }
            catch { /* 解码失败，跳过该字段 */ }
        }
        
        // ===== 构建可读的中文描述 =====
        sb.AppendLine($"[{LocalizeEventType(evt.EventType)}]");
        
        if (evt.Actors.Count > 0)
            sb.AppendLine($"参与者: {string.Join("、", evt.Actors)}");
        
        if (!string.IsNullOrWhiteSpace(evt.Location))
            sb.AppendLine($"位置: {evt.Location}");
        
        if (!string.IsNullOrWhiteSpace(evt.Result))
            sb.AppendLine($"结果: {evt.Result}");
        
        // 添加解码后的 Payload 内容
        foreach (var kvp in decodedPayload)
        {
            var fieldName = kvp.Key.Replace("_", " ").Trim();
            sb.AppendLine($"{fieldName}: {kvp.Value}");
        }
        
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 简单的 Base64 格式检测
    /// </summary>
    private bool IsBase64(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length % 4 != 0)
            return false;
        
        try
        {
            Convert.FromBase64String(input);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 LLM 响应
    /// </summary>
    private Dictionary<long, SemanticDistillationResult> ParseDistillationResponse(string response, List<string>? invalidKeys = null)
    {
        var results = new Dictionary<long, SemanticDistillationResult>();

        try
        {
            var jsonDoc = JsonDocument.Parse(response);
            if (jsonDoc.RootElement.TryGetProperty("events", out var eventsObj))
            {
                foreach (var eventProp in eventsObj.EnumerateObject())
                {
                    if (TryParseEventIdKey(eventProp.Name, out var eventId))
                    {
                        var result = new SemanticDistillationResult
                        {
                            SemanticSummary = eventProp.Value.TryGetProperty("semantic_summary", out var ss) ? ss.GetString() : null,
                            NarrativeWeight = eventProp.Value.TryGetProperty("narrative_weight", out var nw) ? nw.GetDouble() : 0.0,
                            EmotionalWeight = eventProp.Value.TryGetProperty("emotional_weight", out var ew) ? ew.GetDouble() : 0.0,
                            ArcAffinity = eventProp.Value.TryGetProperty("arc_affinity", out var aa) ? aa.GetString() : null
                        };

                        if (eventProp.Value.TryGetProperty("narrative_tags", out var tags))
                        {
                            result.NarrativeTags = tags.EnumerateArray().Select(t => t.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                        }

                        results[eventId] = result;
                    }
                    else
                    {
                        invalidKeys?.Add(eventProp.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 语义蒸馏响应解析失败: {ex.Message}");
        }

        return results;
    }

    internal static bool TryParseEventIdKey(string key, out long eventId)
    {
        eventId = 0;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        key = key.Trim();
        if (key.StartsWith("Event_", StringComparison.OrdinalIgnoreCase))
            key = key.Substring("Event_".Length);
        if (key.StartsWith("#", StringComparison.OrdinalIgnoreCase))
            key = key.Substring(1);

        return long.TryParse(key, out eventId);
    }

    /// <summary>
    /// 触发 Arc Semantic Compression
    /// 当剧情弧需要压缩时调用
    /// </summary>
    public async Task<List<ArcSummaryNode>> CompressArcAsync(TrpgScope scope, string characterId, string arcId)
    {
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var arcEvents = allEvents
            .Where(e => e.IsSemanticallyDistilled && e.ArcAffinity == arcId)
            .OrderBy(e => e.EventId)
            .ToList();

        if (arcEvents.Count == 0)
            return new List<ArcSummaryNode>();

        var prompt = BuildArcCompressionPrompt(arcEvents);
        var response = await CallLlmAsync(scope, characterId, prompt, "ArcSemanticCompression", "你是TRPG剧情弧压缩器。你只压缩已给出的桌面事件，不补充未确认事实，不替GM判定。");
        if (string.IsNullOrEmpty(response))
        {
            _context.Log(LogLevel.Warn, "[AIMod:TRPG] Arc Compression LLM 返回空响应");
            return new List<ArcSummaryNode>();
        }
        var arcSummaries = ParseArcCompressionResponse(response);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] Arc Semantic Compression 完成：{arcId}，生成 {arcSummaries.Count} 个摘要节点");

        return arcSummaries;
    }

    /// <summary>
    /// 构建剧情弧压缩 Prompt
    /// </summary>
    private string BuildArcCompressionPrompt(List<WorldEvent> arcEvents)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("你是一个叙事编辑专家。请将以下事件序列压缩为 3-5 个关键叙事节点。");
        sb.AppendLine();
        sb.AppendLine("输出格式（JSON）：");
        sb.AppendLine("{");
        sb.AppendLine("  \"summaries\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"summary\": \"叙事总结（1-2句话，避免技术术语）\",");
        sb.AppendLine("      \"event_range\": [起始EventID, 结束EventID]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("事件序列：");
        sb.AppendLine("---");

        foreach (var evt in arcEvents)
        {
            sb.AppendLine($"[Event_{evt.EventId}] {evt.SemanticSummary ?? evt.EventType}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 解析剧情弧压缩响应
    /// </summary>
    private List<ArcSummaryNode> ParseArcCompressionResponse(string response)
    {
        var summaries = new List<ArcSummaryNode>();

        try
        {
            var jsonDoc = JsonDocument.Parse(response);
            if (jsonDoc.RootElement.TryGetProperty("summaries", out var summariesArray))
            {
                foreach (var summaryObj in summariesArray.EnumerateArray())
                {
                    var node = new ArcSummaryNode
                    {
                        Summary = summaryObj.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                        StartEventId = summaryObj.TryGetProperty("event_range", out var range) && range.ValueKind == JsonValueKind.Array 
                            ? range[0].GetInt64() 
                            : 0,
                        EndEventId = summaryObj.TryGetProperty("event_range", out range) && range.ValueKind == JsonValueKind.Array 
                            ? range[1].GetInt64() 
                            : 0
                    };
                    summaries.Add(node);
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] Arc Compression 响应解析失败: {ex.Message}");
        }

        return summaries;
    }

    private Task<string?> CallLlmAsync(TrpgScope scope, string characterId, string prompt, string requestKind, string roleInstruction)
    {
        var messages = new List<ChatMessage>
        {
            new("system", $"{AimodPromptPrefixes.BackendCommonPrefixV1}\n\n{roleInstruction}"),
            new("user", prompt)
        };

        return (_llmCallTracker ?? throw new InvalidOperationException("LlmCallTracker is required for AIMod LLM calls."))
            .CallAsync(scope, characterId, messages, "SemanticDistiller", requestKind, _apiCaller);
    }
}

/// <summary>
/// 语义蒸馏结果
/// </summary>
public class SemanticDistillationResult
{
    public string? SemanticSummary { get; set; }
    public double NarrativeWeight { get; set; }
    public List<string> NarrativeTags { get; set; } = new();
    public double EmotionalWeight { get; set; }
    public string? ArcAffinity { get; set; }
}

/// <summary>
/// 剧情弧摘要节点
/// </summary>
public class ArcSummaryNode
{
    public string Summary { get; set; } = "";
    public long StartEventId { get; set; }
    public long EndEventId { get; set; }
}
