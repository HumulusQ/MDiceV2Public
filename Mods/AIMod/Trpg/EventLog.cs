using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 第三层：Immutable Event Log - 不可变事件流
/// 职责：作为唯一事实来源，所有状态从事件流重建
/// </summary>
public class EventLog
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly NarrativeRendererRegistry _renderer;

    // 优化的JSON序列化配置：不转义非ASCII字符，减少token消耗
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public EventLog(IModContext context, ChatDatabase db)
    {
        _context = context;
        _db = db;
        _renderer = new NarrativeRendererRegistry();
    }

    /// <summary>
    /// 追加事件
    /// </summary>
    public async Task<long> AppendEventAsync(TrpgScope scope, WorldEvent worldEvent)
    {
        worldEvent.WorldId = scope.WorldId;
        var eventId = await _db.InsertEventLogAsync(scope, worldEvent);
        worldEvent.EventId = eventId;
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] EventLog: 追加事件 - EventId={eventId}, Type={worldEvent.EventType}");
        return eventId;
    }

    /// <summary>
    /// 重放事件（从指定事件ID开始）
    /// </summary>
    public async Task<List<WorldEvent>> ReplayEventsAsync(TrpgScope scope, long fromEventId, long? toEventId = null)
    {
        return await _db.QueryEventLogAsync(scope, fromEventId, toEventId);
    }

    /// <summary>
    /// 查询实体相关事件
    /// </summary>
    public async Task<List<WorldEvent>> QueryEventsByEntityAsync(TrpgScope scope, string entityId)
    {
        return await _db.QueryEventsByEntityAsync(scope, entityId);
    }

    /// <summary>
    /// 查询场景相关事件
    /// </summary>
    public async Task<List<WorldEvent>> QueryEventsBySceneAsync(TrpgScope scope, string sceneId)
    {
        return await _db.QueryEventsBySceneAsync(scope, sceneId);
    }

    /// <summary>
    /// 查询指定类型的事件
    /// </summary>
    public async Task<List<WorldEvent>> QueryEventsByTypeAsync(TrpgScope scope, string eventType)
    {
        return await _db.QueryEventsByTypeAsync(scope, eventType);
    }

    /// <summary>
    /// 获取最新事件
    /// </summary>
    public async Task<WorldEvent?> GetLatestEventAsync(TrpgScope scope)
    {
        var events = await _db.QueryEventLogAsync(scope, 0, null, 1);
        return events.FirstOrDefault();
    }

    /// <summary>
    /// 生成事件摘要字符串（用于 Prompt）
    /// 使用叙事渲染层，隐藏系统内部结构
    /// </summary>
    public string GenerateEventsSummaryString(List<WorldEvent> events, int maxCount = 20)
    {
        if (events.Count == 0)
            return "无事件记录";

        var sb = new StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【事件流摘要】");
        sb.AppendLine("========================");

        var summaryEvents = BuildPromptSummaryEvents(events);
        if (summaryEvents.Count == 0)
            return "无高语义事件（技术状态事件已折叠）";

        // 按叙事得分排序（而非时间）
        var currentTime = DateTime.UtcNow;
        var scoredEvents = summaryEvents
            .Select(evt => new { Event = evt, Score = _renderer.CalculateNarrativeScore(evt, currentTime) })
            .OrderByDescending(x => x.Score)
            .Take(maxCount)
            .Select(x => x.Event)
            .ToList();

        foreach (var evt in scoredEvents)
        {
            // 使用叙事渲染器生成叙事句子
            var narrative = _renderer.RenderEvent(evt);
            sb.AppendLine($"• {narrative}");
        }

        if (summaryEvents.Count > maxCount)
        {
            sb.AppendLine($"... (共 {summaryEvents.Count} 条事件，按叙事重要性显示 {maxCount} 条)");
        }

        return sb.ToString();
    }

    private List<WorldEvent> BuildPromptSummaryEvents(List<WorldEvent> events)
    {
        var filtered = new List<WorldEvent>(events.Count);
        WorldEvent? lastIncludedSceneTransition = null;

        foreach (var evt in events)
        {
            if (string.Equals(evt.EventType, "state_transaction", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(evt.EventType, "scene_transition", StringComparison.OrdinalIgnoreCase))
            {
                var sceneId = ExtractSceneId(evt);
                var lastSceneId = lastIncludedSceneTransition != null ? ExtractSceneId(lastIncludedSceneTransition) : null;
                if (lastIncludedSceneTransition != null &&
                    string.Equals(sceneId, lastSceneId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lastIncludedSceneTransition = evt;
            }

            filtered.Add(evt);
        }

        return filtered;
    }

    private static string? ExtractSceneId(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("scene_id", out var payloadSceneId))
            return payloadSceneId?.ToString();

        if (!string.IsNullOrWhiteSpace(evt.SceneId))
            return evt.SceneId;

        return evt.Location;
    }

    /// <summary>
    /// 验证事件回放的一致性
    /// 检查因果链是否完整，时间顺序是否正确
    /// </summary>
    public (bool IsValid, List<string> Errors) ValidateEventReplay(List<WorldEvent> events)
    {
        var errors = new List<string>();
        
        if (events.Count == 0)
            return (true, errors);

        // 检查事件ID严格递增
        for (int i = 1; i < events.Count; i++)
        {
            if (events[i].EventId <= events[i - 1].EventId)
            {
                errors.Add($"事件ID不递增: Event_{events[i - 1].EventId} -> Event_{events[i].EventId}");
            }
        }

        // 检查时间顺序
        for (int i = 1; i < events.Count; i++)
        {
            if (events[i].Timestamp < events[i - 1].Timestamp)
            {
                errors.Add($"时间顺序错误: Event_{events[i - 1].EventId} ({events[i - 1].Timestamp}) -> Event_{events[i].EventId} ({events[i].Timestamp})");
            }
        }

        // 检查因果链完整性
        var allEventIds = events.Select(e => e.EventId).ToHashSet();
        foreach (var evt in events)
        {
            foreach (var consequenceId in evt.Consequences)
            {
                if (!allEventIds.Contains(consequenceId))
                {
                    errors.Add($"因果链断裂: Event_{evt.EventId} 引用不存在的后果事件 Event_{consequenceId}");
                }
                else
                {
                    var consequenceEvent = events.FirstOrDefault(e => e.EventId == consequenceId);
                    if (consequenceEvent != null && consequenceEvent.Timestamp < evt.Timestamp)
                    {
                        errors.Add($"因果时间倒置: Event_{evt.EventId} ({evt.Timestamp}) 的后果 Event_{consequenceId} ({consequenceEvent.Timestamp}) 发生在之前");
                    }
                }
            }
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// 建立因果连接：将后果事件ID添加到源事件的 Consequences 列表
    /// </summary>
    public async Task LinkCausalChainAsync(TrpgScope scope, long sourceEventId, long consequenceEventId)
    {
        var sourceEvent = (await _db.QueryEventLogAsync(scope, sourceEventId, sourceEventId)).FirstOrDefault();
        if (sourceEvent == null)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 无法建立因果连接：源事件 Event_{sourceEventId} 不存在");
            return;
        }

        if (sourceEvent.Consequences.Contains(consequenceEventId))
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] 因果连接已存在: Event_{sourceEventId} -> Event_{consequenceEventId}");
            return;
        }

        sourceEvent.Consequences.Add(consequenceEventId);
        
        // 更新数据库
        await _db.UpdateEventConsequencesAsync(scope, sourceEventId, sourceEvent.Consequences);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] 建立因果连接: Event_{sourceEventId} -> Event_{consequenceEventId}");
    }
}
