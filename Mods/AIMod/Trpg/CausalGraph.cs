using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Causal Graph - 因果图谱
/// 
/// 职责：维护事件之间的因果关联，支持伏笔、长期关系、跨场景联系
/// 
/// Graph 不是主结构，而是 Timeline 的侧向连接系统
/// 
/// 边类型：
/// - Temporal Edge: before, after, simultaneous
/// - Causal Edge: causes, reveals, enables, blocks, foreshadows
/// - Semantic Edge: same_entity, same_topic, same_location
/// 
/// 目标：
/// - 维护伏笔
/// - 维护长期关系
/// - 维护跨场景联系
/// - 维护因果关联
/// - 维护隐藏线索
/// </summary>
public class CausalGraph
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;

    public CausalGraph(IModContext context, ChatDatabase db, EventLog eventLog)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
    }

    /// <summary>
    /// 边类型
    /// </summary>
    public enum EdgeType
    {
        // Temporal Edges
        Before,
        After,
        Simultaneous,
        
        // Causal Edges
        Causes,
        Reveals,
        Enables,
        Blocks,
        Foreshadows,
        
        // Semantic Edges
        SameEntity,
        SameTopic,
        SameLocation
    }

    /// <summary>
    /// 因果边
    /// </summary>
    public class CausalEdge
    {
        public string WorldId { get; set; } = "";
        public long GroupId { get; set; } = 0;
        public string CharacterId { get; set; } = "";
        public long SourceEventId { get; set; }
        public long TargetEventId { get; set; }
        public EdgeType EdgeType { get; set; }
        public double Weight { get; set; } = 1.0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int CreatedFoldCount { get; set; } = 0;  // 创建时的折叠计数
    }

    /// <summary>
    /// 添加因果边
    /// </summary>
    public async Task AddEdgeAsync(TrpgScope scope, long sourceEventId, long targetEventId, EdgeType edgeType, double weight = 1.0, string characterId = "")
    {
        // 获取当前FoldCount
        int currentFoldCount = 0;
        if (!string.IsNullOrEmpty(characterId))
        {
            var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
            if (memories.Count > 0)
            {
                currentFoldCount = memories[0].FoldCount;
            }
        }

        var edge = new CausalEdge
        {
            WorldId = scope.WorldId,
            GroupId = scope.GroupId,
            CharacterId = characterId ?? "",
            SourceEventId = sourceEventId,
            TargetEventId = targetEventId,
            EdgeType = edgeType,
            Weight = weight,
            CreatedFoldCount = currentFoldCount
        };

        await _db.InsertCausalEdgeAsync(scope, edge);

        // 同时更新 EventLog 中的 Consequences 字段
        if (edgeType == EdgeType.Causes || edgeType == EdgeType.Foreshadows)
        {
            await _eventLog.LinkCausalChainAsync(scope, sourceEventId, targetEventId);
        }

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] CausalGraph: 添加边 {edgeType} - Event_{sourceEventId} -> Event_{targetEventId} (FoldCount={currentFoldCount})");
    }

    /// <summary>
    /// 获取事件的所有出边
    /// </summary>
    public async Task<List<CausalEdge>> GetOutgoingEdgesAsync(TrpgScope scope, long eventId)
    {
        return await _db.GetCausalEdgesBySourceAsync(scope, eventId);
    }

    /// <summary>
    /// 获取事件的所有入边
    /// </summary>
    public async Task<List<CausalEdge>> GetIncomingEdgesAsync(TrpgScope scope, long eventId)
    {
        return await _db.GetCausalEdgesByTargetAsync(scope, eventId);
    }

    /// <summary>
    /// 获取事件的因果链
    /// 递归获取所有因果相关的事件
    /// </summary>
    public async Task<List<long>> GetCausalChainAsync(TrpgScope scope, long eventId, int maxDepth = 5)
    {
        var visited = new HashSet<long>();
        var result = new List<long>();
        
        await TraverseCausalChainAsync(scope, eventId, visited, result, maxDepth, 0);
        
        return result;
    }

    /// <summary>
    /// 递归遍历因果链
    /// </summary>
    private async Task TraverseCausalChainAsync(TrpgScope scope, long eventId, HashSet<long> visited, List<long> result, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth || visited.Contains(eventId))
            return;

        visited.Add(eventId);
        result.Add(eventId);

        var outgoingEdges = await GetOutgoingEdgesAsync(scope, eventId);
        foreach (var edge in outgoingEdges)
        {
            if (edge.EdgeType == EdgeType.Causes || edge.EdgeType == EdgeType.Foreshadows)
            {
                await TraverseCausalChainAsync(scope, edge.TargetEventId, visited, result, maxDepth, currentDepth + 1);
            }
        }
    }

    /// <summary>
    /// 自动构建因果图谱
    /// 从 EventLog 中自动推断因果关系
    /// </summary>
    public async Task AutoBuildGraphAsync(TrpgScope scope, string characterId)
    {
        var allEvents = (await _eventLog.ReplayEventsAsync(scope, 0, null))
            .Where(e => string.Equals(e.SourceEntityId, characterId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(e.TargetEntityId, characterId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.EventId)
            .ToList();
        
        // 按时间顺序处理事件
        for (int i = 0; i < allEvents.Count; i++)
        {
            var currentEvent = allEvents[i];
            
            // 检查与前一个事件的时间关系
            if (i > 0)
            {
                var prevEvent = allEvents[i - 1];
                var timeDiff = (currentEvent.Timestamp - prevEvent.Timestamp).TotalMinutes;
                
                if (timeDiff < 1)
                {
                    // 同时发生
                    await AddEdgeAsync(scope, prevEvent.EventId, currentEvent.EventId, EdgeType.Simultaneous, characterId: characterId);
                }
                else
                {
                    // 先后发生
                    await AddEdgeAsync(scope, prevEvent.EventId, currentEvent.EventId, EdgeType.Before, characterId: characterId);
                }
            }

            // 检查实体关系
            if (!string.IsNullOrWhiteSpace(currentEvent.SourceEntityId))
            {
                // 查找同一实体的前序事件
                var sameEntityEvents = allEvents
                    .Where(e => e.EventId != currentEvent.EventId && 
                               e.SourceEntityId == currentEvent.SourceEntityId)
                    .TakeLast(5)
                    .ToList();

                foreach (var sameEvent in sameEntityEvents)
                {
                    await AddEdgeAsync(scope, sameEvent.EventId, currentEvent.EventId, EdgeType.SameEntity, characterId: characterId);
                }
            }

            // 检查场景关系
            if (!string.IsNullOrWhiteSpace(currentEvent.SceneId))
            {
                // 查找同一场景的前序事件
                var sameSceneEvents = allEvents
                    .Where(e => e.EventId != currentEvent.EventId && 
                               e.SceneId == currentEvent.SceneId)
                    .TakeLast(10)
                    .ToList();

                foreach (var sameEvent in sameSceneEvents)
                {
                    await AddEdgeAsync(scope, sameEvent.EventId, currentEvent.EventId, EdgeType.SameLocation, characterId: characterId);
                }
            }

            // 检查因果关系
            if (currentEvent.EventType == "discovery")
            {
                // 发现事件通常由探索导致
                var explorationEvents = allEvents
                    .Where(e => e.EventId < currentEvent.EventId && 
                               e.EventType == "dialogue" && 
                               e.Timestamp > currentEvent.Timestamp.AddHours(-1))
                    .TakeLast(3)
                    .ToList();

                foreach (var expEvent in explorationEvents)
                {
                    await AddEdgeAsync(scope, expEvent.EventId, currentEvent.EventId, EdgeType.Reveals, characterId: characterId);
                }
            }

            if (currentEvent.EventType == "npc_death")
            {
                // 死亡事件通常由战斗导致
                var combatEvents = allEvents
                    .Where(e => e.EventId < currentEvent.EventId && 
                               e.EventType == "combat" && 
                               e.Timestamp > currentEvent.Timestamp.AddHours(-1))
                    .TakeLast(3)
                    .ToList();

                foreach (var combatEvent in combatEvents)
                {
                    await AddEdgeAsync(scope, combatEvent.EventId, currentEvent.EventId, EdgeType.Causes, characterId: characterId);
                }
            }
        }

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] CausalGraph: 自动构建因果图谱完成");
    }

    /// <summary>
    /// 生成因果图谱字符串
    /// </summary>
    public string ToPromptString(TrpgScope scope, long eventId, int maxEdges = 10)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【因果图谱】");
        sb.AppendLine("========================");

        var outgoingEdges = GetOutgoingEdgesAsync(scope, eventId).Result.Take(maxEdges).ToList();
        
        if (outgoingEdges.Count == 0)
        {
            sb.AppendLine("无因果关联");
            return sb.ToString();
        }

        sb.AppendLine($"Event_{eventId} 的因果关联:");
        foreach (var edge in outgoingEdges)
        {
            sb.AppendLine($"  -> Event_{edge.TargetEventId} ({edge.EdgeType}, 权重: {edge.Weight})");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 获取图谱统计信息
    /// </summary>
    public async Task<CausalGraphStats> GetStatsAsync(TrpgScope scope)
    {
        var allEdges = await _db.GetAllCausalEdgesAsync(scope);
        
        var stats = new CausalGraphStats
        {
            TotalEdges = allEdges.Count,
            EdgeTypeCounts = new Dictionary<EdgeType, int>()
        };

        foreach (var edge in allEdges)
        {
            if (!stats.EdgeTypeCounts.ContainsKey(edge.EdgeType))
                stats.EdgeTypeCounts[edge.EdgeType] = 0;
            stats.EdgeTypeCounts[edge.EdgeType]++;
        }

        return stats;
    }

    /// <summary>
    /// 边衰减机制（基于FoldCount）
    /// 根据边的折叠次数和类型衰减权重
    /// </summary>
    public async Task ApplyEdgeDecayAsync(TrpgScope scope, string characterId)
    {
        // 获取当前FoldCount
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count == 0) return;
        var currentFoldCount = memories[0].FoldCount;

        var allEdges = await _db.GetAllCausalEdgesAsync(scope, characterId);
        var decayedCount = 0;

        foreach (var edge in allEdges)
        {
            var foldsSinceCreated = currentFoldCount - edge.CreatedFoldCount;
            var decayFactor = CalculateDecayFactor(edge.EdgeType, foldsSinceCreated);

            if (decayFactor < 0.1)
            {
                // 权重过低，删除边
                await _db.DeleteCausalEdgeAsync(scope, edge.SourceEventId, edge.TargetEventId, characterId);
                decayedCount++;
            }
            else if (decayFactor < 1.0)
            {
                // 衰减权重
                edge.Weight *= decayFactor;
                await _db.UpdateCausalEdgeWeightAsync(scope, edge.SourceEventId, edge.TargetEventId, edge.Weight, characterId);
                decayedCount++;
            }
        }

        if (decayedCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] CausalGraph: 边衰减完成，处理了 {decayedCount} 条边 (FoldCount={currentFoldCount})");
        }
    }

    /// <summary>
    /// 计算衰减因子（基于FoldCount）
    /// </summary>
    private double CalculateDecayFactor(EdgeType edgeType, int foldsSinceCreated)
    {
        // 不同类型的边有不同的衰减速率（以折叠次数为单位，1天=40次折叠）
        // 因果链必须比普通记忆寿命更长，因为世界逻辑比聊天内容更重要
        var halfLife = edgeType switch
        {
            EdgeType.Foreshadows => 400,   // 伏笔衰减慢：10天=400次折叠
            EdgeType.Causes => 180,         // 因果关系中等：4.5天=180次折叠
            EdgeType.Reveals => 120,        // 揭示关系较快：3天=120次折叠
            EdgeType.SameEntity => 80,     // 实体关系：2天=80次折叠
            EdgeType.SameLocation => 40,    // 场景关系：1天=40次折叠
            EdgeType.Before => 25,          // 时间关系：0.6天=25次折叠
            EdgeType.Simultaneous => 15,    // 同时关系：0.4天=15次折叠
            _ => 180
        };

        // 指数衰减
        var decayFactor = Math.Pow(0.5, foldsSinceCreated / (double)halfLife);
        return decayFactor;
    }
}

/// <summary>
/// 因果图谱统计信息
/// </summary>
public class CausalGraphStats
{
    /// <summary>
    /// 总边数
    /// </summary>
    public int TotalEdges { get; set; }

    /// <summary>
    /// 各类型边数量
    /// </summary>
    public Dictionary<CausalGraph.EdgeType, int> EdgeTypeCounts { get; set; } = new();
}
