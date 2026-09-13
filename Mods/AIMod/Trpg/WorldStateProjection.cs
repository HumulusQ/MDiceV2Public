using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// World State Projection - 世界状态投影层
/// 
/// 职责：从 EventLog 重建当前世界状态
/// 
/// 这是 Event Sourcing 模式的核心：
/// - EventLog = 唯一不可变历史（真相源）
/// - Projection = 从事件重建的当前状态（缓存）
/// 
/// 投影层不应该直接修改，而是通过追加事件来更新
/// </summary>
public class WorldStateProjection
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly EntityCanonicalizer _entityCanonicalizer;
    private readonly ObjectiveLayer _objectiveLayer;

    public WorldStateProjection(
        IModContext context, 
        ChatDatabase db, 
        EventLog eventLog,
        EntityCanonicalizer entityCanonicalizer,
        ObjectiveLayer objectiveLayer)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _entityCanonicalizer = entityCanonicalizer;
        _objectiveLayer = objectiveLayer;
    }

    /// <summary>
    /// 投影当前世界状态
    /// 从 EventLog 重建完整的世界状态
    /// </summary>
    public async Task<ProjectedWorldState> ProjectCurrentStateAsync(TrpgScope scope, string characterId)
    {
        // 1. 获取所有事件
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        
        // 2. 获取所有实体
        var allEntities = await _entityCanonicalizer.GetAllEntitiesAsync(scope);
        
        // 3. 获取所有目标
        var allObjectives = await _objectiveLayer.GetActiveObjectivesAsync(scope, characterId);
        
        // 4. 投影场景状态
        var currentScene = ProjectCurrentScene(allEvents);
        
        // 5. 投影在场实体
        var presentEntities = ProjectPresentEntities(allEvents, allEntities);
        
        // 6. 投影实体关系
        var entityRelationships = ProjectEntityRelationships(allEvents, allEntities);
        
        // 7. 投影世界标志
        var worldFlags = ProjectWorldFlags(allEvents);
        
        // 8. 投影实体状态
        var entityStates = ProjectEntityStates(allEvents, allEntities);
        
        // 9. 投影物品状态
        var itemStates = ProjectItemStates(allEvents);
        
        // 10. 投影场景状态
        var sceneStates = ProjectSceneStates(allEvents);
        
        return new ProjectedWorldState
        {
            CurrentSceneId = currentScene,
            PresentEntities = presentEntities,
            EntityRelationships = entityRelationships,
            WorldFlags = worldFlags,
            ActiveObjectives = allObjectives.Select(o => o.Description).ToList(),
            EntityStates = entityStates,
            ItemStates = itemStates,
            SceneStates = sceneStates,
            LastEventId = allEvents.Count > 0 ? allEvents.Last().EventId : 0,
            ProjectedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 投影当前场景
    /// 从事件中推断当前场景
    /// </summary>
    private string ProjectCurrentScene(List<WorldEvent> events)
    {
        // 查找最新的场景切换事件
        var sceneTransitionEvents = events
            .Where(e => e.EventType == "scene_transition" || e.EventType == "location_change")
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        if (sceneTransitionEvents.Count > 0)
        {
            var latestEvent = sceneTransitionEvents.First();
            // 从 payload 中提取场景ID
            if (latestEvent.Payload.TryGetValue("scene_id", out var sceneId))
                return sceneId.ToString() ?? "scene_default";
        }

        return "scene_default";
    }

    /// <summary>
    /// 投影在场实体
    /// 从事件中推断当前在场的实体
    /// </summary>
    private List<string> ProjectPresentEntities(List<WorldEvent> events, List<EntityCanonicalRecord> allEntities)
    {
        var presentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // 按时间顺序处理事件
        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            // 处理实体进入事件
            if (evt.EventType == "entity_enter" && evt.SourceEntityId != null)
            {
                presentSet.Add(evt.SourceEntityId);
            }
            
            // 处理实体离开事件
            if (evt.EventType == "entity_exit" && evt.SourceEntityId != null)
            {
                presentSet.Remove(evt.SourceEntityId);
            }
            
            // 处理场景切换事件（清空在场实体）
            if (evt.EventType == "scene_transition")
            {
                presentSet.Clear();
                if (evt.Payload.TryGetValue("present_entities", out var entities))
                {
                    if (entities is JsonArray entitiesArray)
                    {
                        foreach (var entity in entitiesArray)
                        {
                            presentSet.Add(entity.ToString());
                        }
                    }
                }
            }
        }
        
        return presentSet.ToList();
    }

    /// <summary>
    /// 投影实体关系
    /// 从事件中推断实体之间的关系
    /// </summary>
    private Dictionary<string, Dictionary<string, int>> ProjectEntityRelationships(
        List<WorldEvent> events, 
        List<EntityCanonicalRecord> allEntities)
    {
        var relationships = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        
        // 初始化所有实体的关系字典
        foreach (var entity in allEntities)
        {
            relationships[entity.EntityId] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        
        // 按时间顺序处理关系变化事件
        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            if (evt.EventType == "relationship_change" && evt.SourceEntityId != null && evt.TargetEntityId != null)
            {
                var sourceId = evt.SourceEntityId;
                var targetId = evt.TargetEntityId;
                
                if (!relationships.ContainsKey(sourceId))
                    relationships[sourceId] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                
                if (!relationships.ContainsKey(targetId))
                    relationships[targetId] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                
                // 从 payload 中提取关系变化
                if (evt.Payload.TryGetValue("metric", out var metric) && 
                    evt.Payload.TryGetValue("delta", out var delta))
                {
                    var metricName = metric.ToString() ?? "trust";
                    var deltaValue = delta is JsonValue val && val.TryGetValue<int>(out var d) ? d : 0;
                    
                    if (!relationships[sourceId].ContainsKey(targetId))
                        relationships[sourceId][targetId] = 0;
                    
                    relationships[sourceId][targetId] += deltaValue;
                }
            }
        }
        
        return relationships;
    }

    /// <summary>
    /// 投影世界标志
    /// 从事件中推断当前的世界状态标志
    /// </summary>
    private Dictionary<string, object> ProjectWorldFlags(List<WorldEvent> events)
    {
        var flags = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        
        // 按时间顺序处理标志变化事件
        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            if (evt.EventType == "world_flag_change")
            {
                // 从 payload 中提取标志变化
                foreach (var kvp in evt.Payload)
                {
                    flags[kvp.Key] = kvp.Value;
                }
            }
        }
        
        return flags;
    }

    /// <summary>
    /// 投影实体状态
    /// 从事件中推断实体的当前状态
    /// </summary>
    private Dictionary<string, EntityState> ProjectEntityStates(List<WorldEvent> events, List<EntityCanonicalRecord> allEntities)
    {
        var states = new Dictionary<string, EntityState>(StringComparer.OrdinalIgnoreCase);
        
        // 初始化所有实体的状态
        foreach (var entity in allEntities)
        {
            states[entity.EntityId] = new EntityState
            {
                Status = "alive",
                IsAlive = true,
                Location = ""
            };
        }
        
        // 按时间顺序处理实体状态变化事件
        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            if (evt.EventType == "entity_state_change" && evt.SourceEntityId != null)
            {
                var entityId = evt.SourceEntityId;
                if (!states.ContainsKey(entityId))
                    states[entityId] = new EntityState();
                
                // 从 payload 中提取状态变化
                if (evt.Payload.TryGetValue("health", out var health))
                {
                    if (health is JsonValue val && val.TryGetValue<int>(out var hp))
                        states[entityId].Health = hp;
                }
                
                if (evt.Payload.TryGetValue("status", out var status))
                {
                    states[entityId].Status = status.ToString() ?? "alive";
                    states[entityId].IsAlive = states[entityId].Status != "dead";
                }
                
                if (evt.Payload.TryGetValue("location", out var location))
                {
                    states[entityId].Location = location.ToString() ?? "";
                }
            }
            
            // 处理 NPC 死亡事件
            if (evt.EventType == "npc_death" && evt.SourceEntityId != null)
            {
                var entityId = evt.SourceEntityId;
                if (!states.ContainsKey(entityId))
                    states[entityId] = new EntityState();
                
                states[entityId].Status = "dead";
                states[entityId].IsAlive = false;
                states[entityId].Health = 0;
            }
        }
        
        return states;
    }

    /// <summary>
    /// 投影物品状态
    /// 从事件中推断物品的当前状态
    /// </summary>
    private Dictionary<string, ItemState> ProjectItemStates(List<WorldEvent> events)
    {
        var states = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var itemStates = new Dictionary<string, ItemState>(StringComparer.OrdinalIgnoreCase);
        
        // 按时间顺序处理物品相关事件
        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            if (evt.EventType == "item_acquisition" || evt.EventType == "item_transfer")
            {
                // 从 payload 中提取物品信息
                if (evt.Payload.TryGetValue("item_id", out var itemId))
                {
                    var itemIdStr = itemId.ToString();
                    if (!string.IsNullOrWhiteSpace(itemIdStr))
                    {
                        if (!itemStates.ContainsKey(itemIdStr))
                            itemStates[itemIdStr] = new ItemState();
                        
                        if (evt.Payload.TryGetValue("owner_id", out var ownerId))
                            itemStates[itemIdStr].OwnerId = ownerId.ToString() ?? "";
                        
                        if (evt.Payload.TryGetValue("location", out var location))
                            itemStates[itemIdStr].Location = location.ToString() ?? "";
                    }
                }
            }
        }
        
        return itemStates;
    }

    /// <summary>
    /// 投影场景状态
    /// 从事件中推断场景的当前状态
    /// </summary>
    private Dictionary<string, ProjectedSceneState> ProjectSceneStates(List<WorldEvent> events)
    {
        var states = new Dictionary<string, ProjectedSceneState>(StringComparer.OrdinalIgnoreCase);
        
        // 按时间顺序处理场景相关事件
        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            if (evt.EventType == "scene_state_change" && evt.SceneId != null)
            {
                var sceneId = evt.SceneId;
                if (!states.ContainsKey(sceneId))
                    states[sceneId] = new ProjectedSceneState();
                
                // 从 payload 中提取场景状态变化
                if (evt.Payload.TryGetValue("description", out var description))
                    states[sceneId].Description = description.ToString() ?? "";
                
                if (evt.Payload.TryGetValue("is_accessible", out var accessible))
                {
                    if (accessible is JsonValue val && val.TryGetValue<bool>(out var isAccessible))
                        states[sceneId].IsAccessible = isAccessible;
                }
            }
        }
        
        return states;
    }

    /// <summary>
    /// 增量投影
    /// 只处理指定事件ID之后的事件，用于增量更新
    /// </summary>
    public async Task<ProjectedWorldState> ProjectIncrementalAsync(
        TrpgScope scope, 
        string characterId, 
        long fromEventId)
    {
        // 获取增量事件
        var incrementalEvents = await _eventLog.ReplayEventsAsync(scope, fromEventId, null);
        
        if (incrementalEvents.Count == 0)
        {
            // 没有新事件，返回空投影
            return new ProjectedWorldState
            {
                ProjectedAt = DateTime.UtcNow,
                LastEventId = fromEventId
            };
        }
        
        // 获取完整投影
        var fullProjection = await ProjectCurrentStateAsync(scope, characterId);
        
        return fullProjection;
    }
}

/// <summary>
/// 投影的世界状态
/// </summary>
public class ProjectedWorldState
{
    /// <summary>
    /// 当前场景ID
    /// </summary>
    public string CurrentSceneId { get; set; } = "scene_default";
    
    /// <summary>
    /// 在场实体列表
    /// </summary>
    public List<string> PresentEntities { get; set; } = new();
    
    /// <summary>
    /// 实体关系：SourceEntityId -> TargetEntityId -> RelationshipValue
    /// </summary>
    public Dictionary<string, Dictionary<string, int>> EntityRelationships { get; set; } = new();
    
    /// <summary>
    /// 世界标志
    /// </summary>
    public Dictionary<string, object> WorldFlags { get; set; } = new();
    
    /// <summary>
    /// 活跃目标列表
    /// </summary>
    public List<string> ActiveObjectives { get; set; } = new();
    
    /// <summary>
    /// 实体状态：EntityId -> EntityState
    /// </summary>
    public Dictionary<string, EntityState> EntityStates { get; set; } = new();
    
    /// <summary>
    /// 物品状态：ItemId -> ItemState
    /// </summary>
    public Dictionary<string, ItemState> ItemStates { get; set; } = new();
    
    /// <summary>
    /// 场景状态：SceneId -> ProjectedSceneState
    /// </summary>
    public Dictionary<string, ProjectedSceneState> SceneStates { get; set; } = new();
    
    /// <summary>
    /// 最后处理的事件ID
    /// </summary>
    public long LastEventId { get; set; }
    
    /// <summary>
    /// 投影生成时间
    /// </summary>
    public DateTime ProjectedAt { get; set; }
    
    /// <summary>
    /// 投影版本（用于缓存失效）
    /// </summary>
    public int ProjectionVersion { get; set; } = 1;
    
    /// <summary>
    /// 生成用于 Prompt 的字符串
    /// </summary>
    public string ToPromptString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【投影世界状态】");
        sb.AppendLine("========================");
        sb.AppendLine($"场景ID: {CurrentSceneId}");
        sb.AppendLine($"在场实体: {string.Join(", ", PresentEntities)}");
        
        if (WorldFlags.Count > 0)
        {
            sb.AppendLine("世界标志:");
            foreach (var flag in WorldFlags)
            {
                sb.AppendLine($"  - {flag.Key}: {flag.Value}");
            }
        }
        
        if (ActiveObjectives.Count > 0)
        {
            sb.AppendLine("活跃目标:");
            foreach (var obj in ActiveObjectives)
            {
                sb.AppendLine($"  - {obj}");
            }
        }
        
        if (EntityRelationships.Count > 0)
        {
            sb.AppendLine("实体关系:");
            foreach (var (source, targets) in EntityRelationships)
            {
                foreach (var (target, value) in targets)
                {
                    sb.AppendLine($"  - {source} -> {target}: {value}");
                }
            }
        }
        
        if (EntityStates.Count > 0)
        {
            sb.AppendLine("实体状态:");
            foreach (var (entityId, state) in EntityStates)
            {
                sb.AppendLine($"  - {entityId}: {state.Status} (HP: {state.Health}, Alive: {state.IsAlive})");
            }
        }
        
        return sb.ToString();
    }
}

/// <summary>
/// 实体状态
/// </summary>
public class EntityState
{
    public string Status { get; set; } = "alive";
    public int Health { get; set; } = 100;
    public bool IsAlive { get; set; } = true;
    public string Location { get; set; } = "";
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// 物品状态
/// </summary>
public class ItemState
{
    public string OwnerId { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsEquipped { get; set; } = false;
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// 投影场景状态
/// </summary>
public class ProjectedSceneState
{
    public string Description { get; set; } = "";
    public bool IsAccessible { get; set; } = true;
    public Dictionary<string, object> Properties { get; set; } = new();
}
