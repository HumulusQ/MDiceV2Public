using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Narrative Entropy Manager - 叙事熵管理
/// 
/// 职责：防止叙事系统无限膨胀，维护系统长期稳定性
/// 
/// 核心机制：
/// - Event Aging: 旧事件逐渐压缩
/// - Edge Decay: 弱关联逐渐衰减
/// - Arc Consolidation: 完成的剧情弧折叠
/// - Entity Consolidation: 低活跃 NPC 聚合
/// - Historical Distillation: 历史转化为世界事实
/// 
/// 目标：
/// - 防止 Graph 无限膨胀
/// - 防止事件污染
/// - 防止因果失控
/// - 防止 token 崩塌
/// </summary>
public class NarrativeEntropyManager
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly CausalGraph _causalGraph;
    private readonly TemporalLayering _temporalLayering;
    private readonly EntityCanonicalizer _entityCanonicalizer;

    public NarrativeEntropyManager(
        IModContext context, 
        ChatDatabase db, 
        EventLog eventLog,
        CausalGraph causalGraph,
        TemporalLayering temporalLayering,
        EntityCanonicalizer entityCanonicalizer)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _causalGraph = causalGraph;
        _temporalLayering = temporalLayering;
        _entityCanonicalizer = entityCanonicalizer;
    }

    /// <summary>
    /// 执行完整的熵管理流程
    /// </summary>
    public async Task ManageEntropyAsync(TrpgScope scope, string characterId)
    {
        _context.Log(LogLevel.Info, "[AIMod:TRPG] NarrativeEntropyManager: 开始熵管理流程");

        // 1. Event Aging
        await ApplyEventAgingAsync(scope, characterId);

        // 2. Edge Decay
        await _causalGraph.ApplyEdgeDecayAsync(scope, characterId);

        // 3. Arc Consolidation
        await ApplyArcConsolidationAsync(scope, characterId);

        // 4. Entity Consolidation
        await ApplyEntityConsolidationAsync(scope, characterId);

        // 5. Historical Distillation
        await ApplyHistoricalDistillationAsync(scope, characterId);

        _context.Log(LogLevel.Info, "[AIMod:TRPG] NarrativeEntropyManager: 熵管理流程完成");
    }

    /// <summary>
    /// Event Aging: 旧事件逐渐压缩（基于FoldCount）
    /// </summary>
    private async Task ApplyEventAgingAsync(TrpgScope scope, string characterId)
    {
        // 获取当前FoldCount
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count == 0) return;
        var currentFoldCount = memories[0].FoldCount;

        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var agedCount = 0;

        foreach (var evt in allEvents)
        {
            // 检查事件是否已压缩
            if (evt.EventType.Contains("compressed") || evt.EventType == "event_aged")
                continue;

            // 基于FoldCount决定是否压缩（3200次折叠以上，约80天）
            // Event Aging应该更慢，避免高频聊天噪音变成世界真相
            var eventFoldCount = evt.Payload.TryGetValue("fold_count", out var fc) ? Convert.ToInt32(fc) : 0;
            var foldsSinceEvent = currentFoldCount - eventFoldCount;

            if (foldsSinceEvent > 3200)
            {
                // 创建压缩事件
                var compressedEvent = new WorldEvent
                {
                    EventType = "event_aged",
                    Timestamp = evt.Timestamp,
                    Result = $"旧事件已压缩: {evt.EventType}",
                    Payload = new Dictionary<string, object>
                    {
                        { "original_event_id", evt.EventId },
                        { "original_event_type", evt.EventType },
                        { "folds_since", foldsSinceEvent }
                    }
                };

                await _eventLog.AppendEventAsync(scope, compressedEvent);
                agedCount++;
            }
        }

        if (agedCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] NarrativeEntropyManager: Event Aging 完成，压缩了 {agedCount} 个旧事件 (FoldCount={currentFoldCount})");
        }
    }

    /// <summary>
    /// Arc Consolidation: 完成的剧情弧折叠（基于FoldCount）
    /// </summary>
    private async Task ApplyArcConsolidationAsync(TrpgScope scope, string characterId)
    {
        // 获取当前FoldCount
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count == 0) return;
        var currentFoldCount = memories[0].FoldCount;

        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);

        // 按场景分组
        var sceneGroups = allEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SceneId))
            .GroupBy(e => e.SceneId)
            .ToList();

        var consolidatedCount = 0;

        foreach (var group in sceneGroups)
        {
            var sceneId = group.Key ?? "unknown";
            var events = group.OrderBy(e => e.Timestamp).ToList();

            // 检查场景是否已完成（最近事件超过1200次折叠，约30天）
            // Arc Consolidation应该更慢，避免剧情arc还没展开完就被压缩
            if (events.Count > 0)
            {
                var lastEvent = events.Last();
                var lastEventFoldCount = lastEvent.Payload.TryGetValue("fold_count", out var fc) ? Convert.ToInt32(fc) : 0;
                var foldsSinceLastEvent = currentFoldCount - lastEventFoldCount;

                if (foldsSinceLastEvent > 1200 && events.Count > 10)
                {
                    // 场景已完成，折叠剧情弧
                    var arcEvent = new WorldEvent
                    {
                        EventType = "arc_consolidated",
                        SceneId = sceneId,
                        Timestamp = lastEvent.Timestamp,
                        Result = $"场景 {sceneId} 的剧情弧已折叠（{events.Count} 个事件）",
                        Payload = new Dictionary<string, object>
                        {
                            { "scene_id", sceneId },
                            { "event_count", events.Count },
                            { "folds_since", foldsSinceLastEvent }
                        }
                    };

                    await _eventLog.AppendEventAsync(scope, arcEvent);
                    consolidatedCount++;
                }
            }
        }

        if (consolidatedCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] NarrativeEntropyManager: Arc Consolidation 完成，折叠了 {consolidatedCount} 个剧情弧 (FoldCount={currentFoldCount})");
        }
    }

    /// <summary>
    /// Entity Consolidation: 低活跃 NPC 聚合（基于FoldCount）
    /// </summary>
    private async Task ApplyEntityConsolidationAsync(TrpgScope scope, string characterId)
    {
        // 获取当前FoldCount
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count == 0) return;
        var currentFoldCount = memories[0].FoldCount;

        var allEntities = await _entityCanonicalizer.GetAllEntitiesAsync(scope);
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);

        var consolidatedCount = 0;

        foreach (var entity in allEntities)
        {
            // 计算实体的活跃度
            var entityEvents = allEvents
                .Where(e => e.SourceEntityId == entity.EntityId || e.TargetEntityId == entity.EntityId)
                .ToList();

            if (entityEvents.Count == 0)
                continue;

            var lastEvent = entityEvents.OrderByDescending(e => e.Timestamp).First();
            var lastEventFoldCount = lastEvent.Payload.TryGetValue("fold_count", out var fc) ? Convert.ToInt32(fc) : 0;
            var foldsSinceLastEvent = currentFoldCount - lastEventFoldCount;

            // 如果实体超过800次折叠未活跃（约20天），标记为低活跃
            // Entity Consolidation应该比Arc Consolidation更快，因为NPC可能离开场景
            if (foldsSinceLastEvent > 800)
            {
                // 创建实体聚合事件
                var consolidationEvent = new WorldEvent
                {
                    EventType = "entity_consolidated",
                    SourceEntityId = entity.EntityId,
                    Timestamp = DateTime.UtcNow,
                    Result = $"实体 {entity.EntityId} 标记为低活跃",
                    Payload = new Dictionary<string, object>
                    {
                        { "entity_id", entity.EntityId },
                        { "folds_since", foldsSinceLastEvent }
                    }
                };

                await _eventLog.AppendEventAsync(scope, consolidationEvent);
                consolidatedCount++;
            }
        }

        if (consolidatedCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] NarrativeEntropyManager: Entity Consolidation 完成，聚合了 {consolidatedCount} 个低活跃实体 (FoldCount={currentFoldCount})");
        }
    }

    /// <summary>
    /// Historical Distillation: 历史转化为世界事实（基于FoldCount）
    /// </summary>
    private async Task ApplyHistoricalDistillationAsync(TrpgScope scope, string characterId)
    {
        // 获取当前FoldCount
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count == 0) return;
        var currentFoldCount = memories[0].FoldCount;

        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);

        // 提取关键历史事件
        var keyEvents = allEvents
            .Where(e => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "npc_death",
                "world_flag_change",
                "objective_change",
                "faction_change"
            }.Contains(e.EventType))
            .Where(e => {
                var eventFoldCount = e.Payload.TryGetValue("fold_count", out var fc) ? Convert.ToInt32(fc) : 0;
                var foldsSinceEvent = currentFoldCount - eventFoldCount;
                return foldsSinceEvent > 10000;  // 10000次折叠以上（约250天）
            })
            .ToList();

        var distilledCount = 0;

        foreach (var evt in keyEvents)
        {
            // 检查是否已蒸馏
            var existingDistilled = allEvents.Any(e =>
                e.EventType == "historical_distilled" &&
                e.Payload.TryGetValue("original_event_id", out var originalId) &&
                originalId.ToString() == evt.EventId.ToString());

            if (existingDistilled)
                continue;

            // 创建蒸馏事件
            var distilledEvent = new WorldEvent
            {
                EventType = "historical_distilled",
                Timestamp = DateTime.UtcNow,
                Result = $"历史事件蒸馏: {evt.EventType}",
                Payload = new Dictionary<string, object>
                {
                    { "original_event_id", evt.EventId },
                    { "original_event_type", evt.EventType },
                    { "folds_since", currentFoldCount },
                    { "fact", evt.Result }
                }
            };

            await _eventLog.AppendEventAsync(scope, distilledEvent);
            distilledCount++;
        }

        if (distilledCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] NarrativeEntropyManager: Historical Distillation 完成，蒸馏了 {distilledCount} 个历史事件 (FoldCount={currentFoldCount})");
        }
    }

    /// <summary>
    /// 获取熵管理统计信息
    /// </summary>
    public async Task<EntropyStats> GetStatsAsync(TrpgScope scope, string characterId)
    {
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var allEdges = await _db.GetAllCausalEdgesAsync(scope);
        var allEntities = await _entityCanonicalizer.GetAllEntitiesAsync(scope);

        var stats = new EntropyStats
        {
            TotalEvents = allEvents.Count,
            TotalEdges = allEdges.Count,
            TotalEntities = allEntities.Count,
            CompressedEvents = allEvents.Count(e => e.EventType.Contains("compressed")),
            ConsolidatedArcs = allEvents.Count(e => e.EventType == "arc_consolidated"),
            ConsolidatedEntities = allEvents.Count(e => e.EventType == "entity_consolidated"),
            DistilledEvents = allEvents.Count(e => e.EventType == "historical_distilled")
        };

        return stats;
    }
}

/// <summary>
/// 熵管理统计信息
/// </summary>
public class EntropyStats
{
    /// <summary>
    /// 总事件数
    /// </summary>
    public int TotalEvents { get; set; }

    /// <summary>
    /// 总边数
    /// </summary>
    public int TotalEdges { get; set; }

    /// <summary>
    /// 总实体数
    /// </summary>
    public int TotalEntities { get; set; }

    /// <summary>
    /// 压缩事件数
    /// </summary>
    public int CompressedEvents { get; set; }

    /// <summary>
    /// 折叠剧情弧数
    /// </summary>
    public int ConsolidatedArcs { get; set; }

    /// <summary>
    /// 聚合实体数
    /// </summary>
    public int ConsolidatedEntities { get; set; }

    /// <summary>
    /// 蒸馏事件数
    /// </summary>
    public int DistilledEvents { get; set; }

    /// <summary>
    /// 计算熵值（简化版）
    /// </summary>
    public double CalculateEntropy()
    {
        // 简化的熵计算：基于事件、边、实体的数量
        var eventEntropy = Math.Log(TotalEvents + 1);
        var edgeEntropy = Math.Log(TotalEdges + 1);
        var entityEntropy = Math.Log(TotalEntities + 1);
        
        return eventEntropy + edgeEntropy + entityEntropy;
    }
}
