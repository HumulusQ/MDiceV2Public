using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Temporal Compression Engine - 时间分层蒸馏引擎
/// 
/// 职责：实现时间分层存储和动态叙事蒸馏，长期续写的核心
/// 
/// 分层结构（动态蒸馏）：
/// - Hot Layer（热层）: 最近 1 小时，完整事件（全细节）
/// - Warm Layer（温层）: 最近 1 天，Scene Arc（场景级压缩）
/// - Cold Layer（冷层）: 最近 1 周，Story Arc（剧情弧压缩）
/// - Historical Layer（历史层）: 最近 1 月，世界历史
/// - Fossil Layer（化石层）: 永久保存，仅世界观改变、核心创伤、长期关系、阵营历史
/// 
/// 蒸馏策略：
/// - 事件老化：旧事件自动压缩
/// - 弧压缩：相似事件合并为剧情弧
/// - 实体聚合：低活跃实体信息整合
/// - 历史蒸馏：长期事件转化为世界事实
/// 
/// 目标：
/// - 防止 EventLog 无限膨胀
/// - 维持长期续写稳定
/// - 控制 Token 消耗
/// - 保持叙事连贯性
/// </summary>
public class TemporalLayering
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly HierarchicalTimeline _hierarchicalTimeline;
    private readonly NarrativeEntropyManager _entropyManager;

    public TemporalLayering(
        IModContext context,
        ChatDatabase db,
        EventLog eventLog,
        HierarchicalTimeline hierarchicalTimeline,
        NarrativeEntropyManager? entropyManager = null)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _hierarchicalTimeline = hierarchicalTimeline;
        
        // NarrativeEntropyManager 需要更多依赖，这里简化处理
        if (entropyManager != null)
        {
            _entropyManager = entropyManager;
        }
        else
        {
            // 创建最小依赖版本
            var causalGraph = new CausalGraph(context, db, eventLog);
            var entityCanonicalizer = new EntityCanonicalizer(context, db);
            _entropyManager = new NarrativeEntropyManager(context, db, eventLog, causalGraph, this, entityCanonicalizer);
        }
    }

    /// <summary>
    /// 时间层级定义（基于FoldCount，1天=40次折叠）
    /// </summary>
    public enum TemporalLayer
    {
        Hot,         // 热层：最近 40 次折叠（1天），完整事件（全细节）
        Warm,        // 温层：最近 280 次折叠（7天），Scene Arc（场景级压缩）
        Cold,        // 冷层：最近 1200 次折叠（30天），Story Arc（剧情弧压缩）
        Historical,  // 历史层：最近 3600 次折叠（90天），世界历史
        Fossil       // 化石层：永久保存，仅世界观改变、核心创伤、长期关系、阵营历史
    }

    /// <summary>
    /// 获取指定层级的事件
    /// </summary>
    public async Task<List<WorldEvent>> GetEventsByLayerAsync(TrpgScope scope, string characterId, TemporalLayer layer)
    {
        // 获取当前FoldCount
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count == 0) return new List<WorldEvent>();
        var currentFoldCount = memories[0].FoldCount;

        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var cutoffFoldCount = GetCutoffFoldCount(layer);

        return allEvents
            .Where(e => {
                var eventFoldCount = e.Payload.TryGetValue("fold_count", out var fc) ? Convert.ToInt32(fc) : 0;
                return currentFoldCount - eventFoldCount <= cutoffFoldCount;
            })
            .OrderBy(e => e.Timestamp)
            .ToList();
    }

    /// <summary>
    /// 获取层级截止FoldCount（1天=40次折叠）
    /// </summary>
    private int GetCutoffFoldCount(TemporalLayer layer)
    {
        return layer switch
        {
            TemporalLayer.Hot => 40,         // 1天
            TemporalLayer.Warm => 280,       // 7天
            TemporalLayer.Cold => 1200,      // 30天
            TemporalLayer.Historical => 3600, // 90天
            TemporalLayer.Fossil => int.MaxValue,
            _ => 0
        };
    }

    /// <summary>
    /// 自动分层和压缩
    /// 检查各层级，自动将旧事件压缩到更高层级
    /// </summary>
    public async Task AutoLayerAndCompressAsync(TrpgScope scope, string characterId)
    {
        // 获取当前FoldCount
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count == 0) return;
        var currentFoldCount = memories[0].FoldCount;

        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);

        // 检查 Hot Layer
        await CheckAndCompressLayerAsync(scope, allEvents, TemporalLayer.Hot, characterId, currentFoldCount);

        // 检查 Warm Layer
        await CheckAndCompressLayerAsync(scope, allEvents, TemporalLayer.Warm, characterId, currentFoldCount);

        // 检查 Cold Layer
        await CheckAndCompressLayerAsync(scope, allEvents, TemporalLayer.Cold, characterId, currentFoldCount);

        // 检查 Historical Layer
        await CheckAndCompressLayerAsync(scope, allEvents, TemporalLayer.Historical, characterId, currentFoldCount);
    }

    /// <summary>
    /// 检查并压缩指定层级
    /// </summary>
    private async Task CheckAndCompressLayerAsync(TrpgScope scope, List<WorldEvent> allEvents, TemporalLayer layer, string characterId, int currentFoldCount)
    {
        var cutoffFoldCount = GetCutoffFoldCount(layer);
        var layerEvents = allEvents.Where(e => {
            var eventFoldCount = e.Payload.TryGetValue("fold_count", out var fc) ? Convert.ToInt32(fc) : 0;
            return currentFoldCount - eventFoldCount > cutoffFoldCount;
        }).ToList();

        if (layerEvents.Count == 0)
            return;

        // 根据层级选择压缩策略
        switch (layer)
        {
            case TemporalLayer.Hot:
                // 压缩到 Warm Layer
                await CompressToWarmAsync(scope, layerEvents, characterId);
                break;
            
            case TemporalLayer.Warm:
                // 压缩到 Cold Layer
                await CompressToColdAsync(scope, layerEvents, characterId);
                break;
            
            case TemporalLayer.Cold:
                // 压缩到 Historical Layer
                await CompressToHistoricalAsync(scope, layerEvents, characterId);
                break;
            
            case TemporalLayer.Historical:
                // 压缩到 Fossil Layer
                await CompressToFossilAsync(scope, layerEvents, characterId);
                break;
            
            case TemporalLayer.Fossil:
                // Fossil Layer 不再压缩
                break;
        }
    }

    /// <summary>
    /// 压缩到 Mid-term Layer
    /// </summary>
    private async Task CompressToWarmAsync(TrpgScope scope, List<WorldEvent> events, string characterId)
    {
        // 按场景分组压缩
        var sceneGroups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.SceneId))
            .GroupBy(e => e.SceneId)
            .ToList();

        foreach (var group in sceneGroups)
        {
            var sceneId = group.Key;
            var sceneEvents = group.ToList();
            
            if (sceneEvents.Count < 5)
                continue; // 事件太少，不压缩

            var compressedEvent = new WorldEvent
            {
                EventType = "mid_term_compressed",
                SceneId = sceneId,
                Timestamp = sceneEvents.Max(e => e.Timestamp),
                Result = $"场景 {sceneId} 的 {sceneEvents.Count} 个事件压缩到 Mid-term Layer",
                Payload = new Dictionary<string, object>
                {
                    { "original_event_count", sceneEvents.Count },
                    { "time_range", $"{sceneEvents.Min(e => e.Timestamp):o} ~ {sceneEvents.Max(e => e.Timestamp):o}" },
                    { "layer", "mid_term" }
                }
            };

            await _eventLog.AppendEventAsync(scope, compressedEvent);
        }

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] TemporalLayering: 压缩 {events.Count} 个事件到 Mid-term Layer");
    }

    /// <summary>
    /// 压缩到 Arc Layer
    /// </summary>
    private async Task CompressToColdAsync(TrpgScope scope, List<WorldEvent> events, string characterId)
    {
        // 按剧情弧分组（基于事件类型）
        var arcGroups = events
            .GroupBy(e => GetArcType(e.EventType))
            .ToList();

        foreach (var group in arcGroups)
        {
            var arcType = group.Key;
            var arcEvents = group.ToList();
            
            if (arcEvents.Count < 10)
                continue;

            var compressedEvent = new WorldEvent
            {
                EventType = "arc_compressed",
                Timestamp = arcEvents.Max(e => e.Timestamp),
                Result = $"{arcType} 剧情弧的 {arcEvents.Count} 个事件压缩到 Arc Layer",
                Payload = new Dictionary<string, object>
                {
                    { "arc_type", arcType },
                    { "original_event_count", arcEvents.Count },
                    { "time_range", $"{arcEvents.Min(e => e.Timestamp):o} ~ {arcEvents.Max(e => e.Timestamp):o}" },
                    { "layer", "arc" }
                }
            };

            await _eventLog.AppendEventAsync(scope, compressedEvent);
        }

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] TemporalLayering: 压缩 {events.Count} 个事件到 Arc Layer");
    }

    /// <summary>
    /// 压缩到 Historical Layer
    /// </summary>
    private async Task CompressToHistoricalAsync(TrpgScope scope, List<WorldEvent> events, string characterId)
    {
        // 只保留关键事件
        var keyEvents = events
            .Where(e => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "npc_death",
                "objective_change",
                "relationship_change",
                "discovery"
            }.Contains(e.EventType))
            .ToList();

        if (keyEvents.Count == 0)
            return;

        var compressedEvent = new WorldEvent
        {
            EventType = "historical_compressed",
            Timestamp = keyEvents.Max(e => e.Timestamp),
            Result = $"{keyEvents.Count} 个关键事件压缩到 Historical Layer",
            Payload = new Dictionary<string, object>
            {
                { "key_event_count", keyEvents.Count },
                { "time_range", $"{keyEvents.Min(e => e.Timestamp):o} ~ {keyEvents.Max(e => e.Timestamp):o}" },
                { "layer", "historical" }
            }
        };

        await _eventLog.AppendEventAsync(scope, compressedEvent);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] TemporalLayering: 压缩 {events.Count} 个事件到 Historical Layer");
    }

    /// <summary>
    /// 压缩到 Fossil Layer
    /// </summary>
    private async Task CompressToFossilAsync(TrpgScope scope, List<WorldEvent> events, string characterId)
    {
        // 只保留世界观改变、核心创伤、长期关系、阵营历史
        var fossilEvents = events
            .Where(e => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "world_flag_change",
                "npc_death",
                "faction_change"
            }.Contains(e.EventType))
            .ToList();

        if (fossilEvents.Count == 0)
            return;

        var compressedEvent = new WorldEvent
        {
            EventType = "fossil_compressed",
            Timestamp = fossilEvents.Max(e => e.Timestamp),
            Result = $"{fossilEvents.Count} 个化石事件保存到 Fossil Layer",
            Payload = new Dictionary<string, object>
            {
                { "fossil_event_count", fossilEvents.Count },
                { "time_range", $"{fossilEvents.Min(e => e.Timestamp):o} ~ {fossilEvents.Max(e => e.Timestamp):o}" },
                { "layer", "fossil" }
            }
        };

        await _eventLog.AppendEventAsync(scope, compressedEvent);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] TemporalLayering: 压缩 {events.Count} 个事件到 Fossil Layer");
    }

    /// <summary>
    /// 获取剧情弧类型
    /// </summary>
    private string GetArcType(string eventType)
    {
        return eventType.ToLower() switch
        {
            "combat" => "战斗",
            "discovery" => "探索",
            "dialogue" => "对话",
            "npc_death" => "死亡",
            "relationship_change" => "关系",
            _ => "其他"
        };
    }

    /// <summary>
    /// 生成分层时间轴字符串
    /// </summary>
    public string ToPromptString(TrpgScope scope, string characterId, int maxTokens = 2000)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【分层时间轴】");
        sb.AppendLine("========================");

        // 根据可用 token 决定显示哪些层级
        if (maxTokens > 500)
        {
            var hotEvents = GetEventsByLayerAsync(scope, characterId, TemporalLayer.Hot).Result;
            sb.AppendLine("--- Hot Layer (最近1小时) ---");
            foreach (var evt in hotEvents.TakeLast(5))
            {
                sb.AppendLine($"  [{evt.EventType}] {evt.Result}");
            }
        }

        if (maxTokens > 1000)
        {
            var warmEvents = GetEventsByLayerAsync(scope, characterId, TemporalLayer.Warm).Result;
            sb.AppendLine("\n--- Warm Layer (最近1天) ---");
            sb.AppendLine($"  {warmEvents.Count} 个事件（已压缩）");
        }

        if (maxTokens > 1500)
        {
            var coldEvents = GetEventsByLayerAsync(scope, characterId, TemporalLayer.Cold).Result;
            sb.AppendLine("\n--- Cold Layer (最近1周) ---");
            sb.AppendLine($"  {coldEvents.Count} 个事件（已压缩）");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 获取层级统计信息
    /// </summary>
    public async Task<Dictionary<TemporalLayer, int>> GetLayerStatsAsync(TrpgScope scope, string characterId)
    {
        var stats = new Dictionary<TemporalLayer, int>();
        
        foreach (TemporalLayer layer in Enum.GetValues(typeof(TemporalLayer)))
        {
            var events = await GetEventsByLayerAsync(scope, characterId, layer);
            stats[layer] = events.Count;
        }

        return stats;
    }
}
