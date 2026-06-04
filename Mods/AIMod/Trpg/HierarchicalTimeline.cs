using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Hierarchical Timeline - 分层时间轴
/// 
/// 职责：维护多层次的时间轴结构，解决事件碎片化和时空断裂问题
/// 
/// 分层结构：
/// - Layer A: Scene Arc（场景弧，按场景压缩）
/// - Layer B: Detailed Events（详细事件，具体对话、动作）
/// - Layer C: Raw Archive（原始档案，完整历史原文）
/// 
/// 目标：
/// - 解决剧情推进感丢失
/// - 解决事件碎片化
/// - 解决时空断裂
/// - 解决长期故事结构崩塌
/// </summary>
public class HierarchicalTimeline
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;

    public HierarchicalTimeline(IModContext context, ChatDatabase db, EventLog eventLog)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
    }

    /// <summary>
    /// 获取完整分层时间轴
    /// </summary>
    public async Task<HierarchicalTimelineData> GetTimelineAsync(TrpgScope scope, string characterId)
    {
        // Layer B: Scene Arcs
        var sceneArcs = await BuildSceneArcsAsync(scope, characterId);
        
        // Layer C: Detailed Events
        var detailedEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        
        // Layer D: Raw Archive（不常规加载，按需）
        
        return new HierarchicalTimelineData
        {
            SceneArcs = sceneArcs,
            DetailedEvents = detailedEvents,
            GeneratedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 构建场景弧
    /// 按场景压缩事件
    /// </summary>
    private async Task<List<SceneArc>> BuildSceneArcsAsync(TrpgScope scope, string characterId)
    {
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        
        // 按场景分组
        var sceneGroups = allEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SceneId))
            .GroupBy(e => e.SceneId)
            .OrderBy(g => g.Min(e => e.Timestamp))
            .ToList();

        var sceneArcs = new List<SceneArc>();
        
        foreach (var group in sceneGroups)
        {
            var sceneId = group.Key ?? "unknown";
            var events = group.OrderBy(e => e.Timestamp).ToList();
            
            var arc = new SceneArc
            {
                SceneId = sceneId,
                StartTime = events.Count > 0 ? events.First().Timestamp : DateTime.UtcNow,
                EndTime = events.Count > 0 ? events.Last().Timestamp : DateTime.UtcNow,
                EventCount = events.Count,
                Summary = GenerateSceneArcSummary(events)
            };
            
            sceneArcs.Add(arc);
        }

        return sceneArcs;
    }

    /// <summary>
    /// 生成场景弧摘要
    /// </summary>
    private string GenerateSceneArcSummary(List<WorldEvent> events)
    {
        if (events.Count == 0)
            return "无事件";

        var mainEvents = events
            .Where(e => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "discovery",
                "item_acquisition",
                "npc_death",
                "relationship_change"
            }.Contains(e.EventType))
            .ToList();

        if (mainEvents.Count == 0)
            return $"{events.Count} 个事件";

        var summary = string.Join("; ", mainEvents.Select(e => e.EventType));
        return $"{mainEvents.Count} 个关键事件: {summary}";
    }

    /// <summary>
    /// 生成用于 Prompt 的时间轴字符串
    /// 根据可用 token 动态选择层级
    /// </summary>
    public string ToPromptString(HierarchicalTimelineData timeline, int maxTokens = 2000)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【分层时间轴】");
        sb.AppendLine("========================");

        // 显示有内容的叙事事件（Layer C），作为故事脊柱的prose展开
        var narrativeEvents = timeline.DetailedEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.Result))
            .TakeLast(5)
            .ToList();

        if (narrativeEvents.Count == 0)
        {
            sb.AppendLine("无事件记录");
            return sb.ToString();
        }

        for (int i = 0; i < narrativeEvents.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {narrativeEvents[i].Result}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 压缩时间轴
    /// 将旧事件压缩到更高层级
    /// </summary>
    public async Task CompressTimelineAsync(TrpgScope scope, string characterId, DateTime cutoffTime)
    {
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        
        // 找出需要压缩的事件
        var oldEvents = allEvents
            .Where(e => e.Timestamp < cutoffTime)
            .ToList();

        if (oldEvents.Count == 0)
            return;

        // 按场景分组压缩
        var sceneGroups = oldEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SceneId))
            .GroupBy(e => e.SceneId)
            .ToList();

        foreach (var group in sceneGroups)
        {
            var sceneId = group.Key;
            var events = group.ToList();
            
            // 创建压缩事件
            var compressedEvent = new WorldEvent
            {
                EventType = "scene_arc_compressed",
                SceneId = sceneId,
                Timestamp = events.Max(e => e.Timestamp),
                Result = $"场景 {sceneId} 的 {events.Count} 个事件已压缩",
                Payload = new Dictionary<string, object>
                {
                    { "original_event_count", events.Count },
                    { "time_range", $"{events.Min(e => e.Timestamp):o} ~ {events.Max(e => e.Timestamp):o}" }
                }
            };

            // 追加压缩事件
            await _eventLog.AppendEventAsync(scope, compressedEvent);
        }

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] Timeline: 压缩了 {oldEvents.Count} 个旧事件");
    }

    /// <summary>
    /// 获取时间轴统计信息
    /// </summary>
    public TimelineStats GetStats(HierarchicalTimelineData timeline)
    {
        return new TimelineStats
        {
            SceneArcCount = timeline.SceneArcs.Count,
            DetailedEventCount = timeline.DetailedEvents.Count,
            TimeSpan = timeline.DetailedEvents.Count > 0 
                ? timeline.DetailedEvents.Last().Timestamp - timeline.DetailedEvents.First().Timestamp 
                : TimeSpan.Zero
        };
    }
}

/// <summary>
/// 分层时间轴数据
/// </summary>
public class HierarchicalTimelineData
{
    /// <summary>
    /// Layer A: Scene Arcs
    /// </summary>
    public List<SceneArc> SceneArcs { get; set; } = new();

    /// <summary>
    /// Layer C: Detailed Events
    /// </summary>
    public List<WorldEvent> DetailedEvents { get; set; } = new();

    /// <summary>
    /// 生成时间
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 场景弧
/// </summary>
public class SceneArc
{
    /// <summary>
    /// 场景ID
    /// </summary>
    public string SceneId { get; set; } = "";

    /// <summary>
    /// 开始时间
    /// </summary>
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 结束时间
    /// </summary>
    public DateTime EndTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 事件数量
    /// </summary>
    public int EventCount { get; set; }

    /// <summary>
    /// 场景弧摘要
    /// </summary>
    public string Summary { get; set; } = "";
}

/// <summary>
/// 时间轴统计信息
/// </summary>
public class TimelineStats
{
    /// <summary>
    /// 故事脊柱节点数
    /// </summary>
    public int StorySpineCount { get; set; }

    /// <summary>
    /// 场景弧数量
    /// </summary>
    public int SceneArcCount { get; set; }

    /// <summary>
    /// 详细事件数量
    /// </summary>
    public int DetailedEventCount { get; set; }

    /// <summary>
    /// 时间跨度
    /// </summary>
    public TimeSpan TimeSpan { get; set; }
}
