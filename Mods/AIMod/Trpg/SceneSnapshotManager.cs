using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 第四层：Scene Snapshot - 场景快照管理
/// 
/// 重要：SceneSnapshot 现在是 Projection Cache（投影缓存）
/// 不再直接写入快照，而是从 EventLog 投影生成
/// 
/// 真相源：EventLog
/// 缓存层：SceneSnapshot
/// 
/// 生成逻辑：
/// EventLog（场景相关事件）→ 投影 → SceneSnapshot
/// </summary>
public class SceneSnapshotManager
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;

    public SceneSnapshotManager(IModContext context, ChatDatabase db, EventLog? eventLog = null)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog ?? new EventLog(context, db);
    }

    /// <summary>
    /// 从 EventLog 投影生成场景快照
    /// </summary>
    public async Task<SceneSnapshotExtended?> ProjectSnapshotAsync(TrpgScope scope, string sceneId)
    {
        // 获取该场景的所有事件
        var sceneEvents = await _eventLog.QueryEventsBySceneAsync(scope, sceneId);
        
        if (sceneEvents.Count == 0)
            return null;

        // 查找最新的场景进入事件
        var enterEvents = sceneEvents
            .Where(e => e.EventType == "scene_enter" || e.EventType == "scene_transition")
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        if (enterEvents.Count == 0)
            return null;

        var latestEnterEvent = enterEvents.First();
        
        // 从事件中提取快照信息
        var snapshot = new SceneSnapshotExtended
        {
            SceneId = sceneId,
            EnteredAt = latestEnterEvent.Timestamp,
            PresentEntityIds = ExtractPresentEntities(latestEnterEvent),
            SceneGoals = ExtractSceneGoals(sceneEvents),
            OutstandingThreads = ExtractOutstandingThreads(sceneEvents),
            SceneFlags = ExtractSceneFlags(sceneEvents)
        };

        // 缓存到数据库
        await _db.InsertSceneSnapshotAsync(scope, snapshot);
        
        return snapshot;
    }

    /// <summary>
    /// 获取最新场景快照（优先从缓存，过期则重新投影）
    /// </summary>
    public async Task<SceneSnapshotExtended?> GetLatestSnapshotAsync(TrpgScope scope, string sceneId)
    {
        var cached = await _db.GetLatestSceneSnapshotAsync(scope, sceneId);
        
        // 检查缓存是否过期（5分钟）
        if (cached != null && (DateTime.UtcNow - cached.EnteredAt).TotalMinutes < 5)
        {
            return cached;
        }
        
        // 缓存过期或不存在，重新投影
        return await ProjectSnapshotAsync(scope, sceneId);
    }

    /// <summary>
    /// 从事件中提取在场实体
    /// </summary>
    private List<string> ExtractPresentEntities(WorldEvent enterEvent)
    {
        var entities = new List<string>();
        
        if (enterEvent.Payload.TryGetValue("present_entities", out var value))
        {
            if (value is JsonArray entityArray)
            {
                foreach (var entity in entityArray)
                {
                    entities.Add(entity.ToString());
                }
            }
            else if (value is string entityStr)
            {
                entities.Add(entityStr);
            }
        }
        
        // 如果 payload 中没有，从 Actors 中提取
        if (entities.Count == 0)
        {
            entities.AddRange(enterEvent.Actors);
        }
        
        return entities;
    }

    /// <summary>
    /// 从事件中提取场景目标
    /// </summary>
    private List<string> ExtractSceneGoals(List<WorldEvent> events)
    {
        var goals = new List<string>();
        
        foreach (var evt in events)
        {
            if (evt.EventType == "objective_added" && evt.Payload.TryGetValue("objective", out var objective))
            {
                var objectiveStr = objective?.ToString();
                if (!string.IsNullOrWhiteSpace(objectiveStr))
                    goals.Add(objectiveStr);
            }
            else if (evt.EventType == "objective_completed")
            {
                var completedObjective = evt.Payload.GetValueOrDefault("objective", "")?.ToString();
                if (!string.IsNullOrWhiteSpace(completedObjective))
                    goals.Remove(completedObjective);
            }
        }
        
        return goals;
    }

    /// <summary>
    /// 从事件中提取未完成线索
    /// </summary>
    private List<string> ExtractOutstandingThreads(List<WorldEvent> events)
    {
        var threads = new List<string>();
        
        foreach (var evt in events)
        {
            if (evt.EventType == "thread_added" && evt.Payload.TryGetValue("thread", out var thread))
            {
                var threadStr = thread?.ToString();
                if (!string.IsNullOrWhiteSpace(threadStr))
                    threads.Add(threadStr);
            }
            else if (evt.EventType == "thread_resolved")
            {
                var resolvedThread = evt.Payload.GetValueOrDefault("thread", "")?.ToString();
                if (!string.IsNullOrWhiteSpace(resolvedThread))
                    threads.Remove(resolvedThread);
            }
        }
        
        return threads;
    }

    /// <summary>
    /// 从事件中提取场景标志
    /// </summary>
    private Dictionary<string, object> ExtractSceneFlags(List<WorldEvent> events)
    {
        var flags = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var evt in events)
        {
            if (evt.EventType == "scene_flag_change")
            {
                foreach (var kvp in evt.Payload)
                {
                    flags[kvp.Key] = kvp.Value;
                }
            }
        }
        
        return flags;
    }

    /// <summary>
    /// 废弃：直接创建快照的方法（保留用于向后兼容，但标记为废弃）
    /// </summary>
    [Obsolete("使用 ProjectSnapshotAsync 从 EventLog 投影生成快照")]
    public async Task CreateSnapshotAsync(TrpgScope scope, SceneSnapshotExtended snapshot)
    {
        await _db.InsertSceneSnapshotAsync(scope, snapshot);
        _context.Log(LogLevel.Warn, $"[AIMod:TRPG] 直接创建快照已废弃，请使用 ProjectSnapshotAsync: {snapshot.SceneId}");
    }

    /// <summary>
    /// 生成场景快照字符串（用于 Prompt）
    /// </summary>
    public string GenerateSnapshotString(SceneSnapshotExtended snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【场景快照】");
        sb.AppendLine("========================");
        sb.AppendLine($"场景ID: {snapshot.SceneId}");
        sb.AppendLine($"进入时间: {snapshot.EnteredAt:MM-dd HH:mm}");
        
        if (snapshot.PresentEntityIds.Count > 0)
        {
            sb.AppendLine("在场实体:");
            foreach (var entityId in snapshot.PresentEntityIds)
            {
                sb.AppendLine($"  - {entityId}");
            }
        }

        if (snapshot.SceneGoals.Count > 0)
        {
            sb.AppendLine("场景目标:");
            foreach (var goal in snapshot.SceneGoals)
            {
                sb.AppendLine($"  - {goal}");
            }
        }

        if (snapshot.OutstandingThreads.Count > 0)
        {
            sb.AppendLine("未完成线索:");
            foreach (var thread in snapshot.OutstandingThreads)
            {
                sb.AppendLine($"  - {thread}");
            }
        }

        if (snapshot.SceneFlags.Count > 0)
        {
            sb.AppendLine("场景标志:");
            foreach (var flag in snapshot.SceneFlags)
            {
                sb.AppendLine($"  - {flag.Key}: {flag.Value}");
            }
        }

        return sb.ToString();
    }
}
