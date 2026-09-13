using System;
using System.Collections.Generic;
using System.Linq;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Narrative Gravity Engine - 叙事引力引擎
/// 
/// 职责：计算和更新事件的叙事引力，决定哪些事件应该长期保留
/// 
/// 核心思想：
/// - 时间 ≠ 重要性
/// - 不是按时间压缩，而是按叙事引力压缩
/// - 真正长期存在的不是"事件文本"，而是"结构性影响"
/// 
/// 引力分析维度：
/// 1. 事件长期影响分析
/// 2. 主线依赖分析
/// 3. 目标依赖分析
/// 4. 角色身份影响
/// 5. 世界状态影响
/// 6. 伏笔回流强化
/// 
/// 动态特性：
/// - 允许历史重新获得重量
/// - 事件重要性会随着伏笔兑现、目标变化、情感强化而变化
/// - 类似人类回忆："原来那天他说的话那么重要"
/// </summary>
public class NarrativeGravityEngine
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly CausalGraph _causalGraph;
    private readonly ObjectiveLayer _objectiveLayer;

    public NarrativeGravityEngine(
        IModContext context,
        ChatDatabase db,
        EventLog eventLog,
        CausalGraph causalGraph,
        ObjectiveLayer objectiveLayer)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _causalGraph = causalGraph;
        _objectiveLayer = objectiveLayer;
    }

    /// <summary>
    /// 计算事件的叙事引力
    /// </summary>
    public async Task<NarrativeWeight> CalculateGravityAsync(TrpgScope scope, string characterId, WorldEvent evt)
    {
        var weight = NarrativeWeight.CreateDefault();

        // 1. 时间衰减
        weight.TemporalDecay = CalculateTemporalDecay(evt);

        // 2. 叙事引力（核心）
        weight.NarrativeGravity = await CalculateNarrativeGravityAsync(scope, characterId, evt);

        // 3. 情绪权重
        weight.EmotionalWeight = CalculateEmotionalWeight(evt);

        // 4. 剧情依赖
        weight.PlotDependency = await CalculatePlotDependencyAsync(scope, characterId, evt);

        // 5. 伏笔潜力
        weight.ForeshadowPotential = await CalculateForeshadowPotentialAsync(scope, characterId, evt);

        // 6. 身份影响
        weight.IdentityImpact = CalculateIdentityImpact(evt);

        // 7. 目标相关性
        weight.ObjectiveRelevance = await CalculateObjectiveRelevanceAsync(scope, characterId, evt);

        return weight;
    }

    /// <summary>
    /// 计算时间衰减
    /// </summary>
    private float CalculateTemporalDecay(WorldEvent evt)
    {
        var age = DateTime.UtcNow - evt.Timestamp;
        
        // 越新的事件越高
        if (age < TimeSpan.FromMinutes(30))
            return 1.0f;
        if (age < TimeSpan.FromHours(1))
            return 0.9f;
        if (age < TimeSpan.FromHours(6))
            return 0.7f;
        if (age < TimeSpan.FromDays(1))
            return 0.5f;
        if (age < TimeSpan.FromDays(7))
            return 0.3f;
        if (age < TimeSpan.FromDays(30))
            return 0.1f;
        
        return 0.05f;
    }

    /// <summary>
    /// 计算叙事引力（核心维度）
    /// 事件持续影响未来叙事的能力
    /// </summary>
    private async Task<float> CalculateNarrativeGravityAsync(TrpgScope scope, string characterId, WorldEvent evt)
    {
        var gravity = 0.0f;

        // 1. 因果链影响：如果事件有后果，说明是重要节点
        if (evt.Consequences != null && evt.Consequences.Count > 0)
        {
            gravity += 0.3f;
        }

        // 2. 因果图谱位置：如果事件是因果链的起点或终点
        // TODO: 需要扩展 CausalGraph API
        // 简化处理：仅基于事件类型和后果判断
        if (evt.Consequences != null && evt.Consequences.Count > 2)
            gravity += 0.2f;

        // 3. 事件类型权重
        gravity += GetEventTypeGravityWeight(evt.EventType);

        return Math.Min(gravity, 1.0f);
    }

    /// <summary>
    /// 获取事件类型的引力权重
    /// </summary>
    private float GetEventTypeGravityWeight(string eventType)
    {
        return eventType.ToLower() switch
        {
            "objective_change" => 0.9f,
            "world_rule_change" => 0.95f,
            "core_secret_reveal" => 0.95f,
            "main_relationship_change" => 0.9f,
            "faction_change" => 0.85f,
            "core_trauma" => 0.9f,
            "discovery" => 0.7f,
            "item_acquisition" => 0.6f,
            "npc_death" => 0.8f,
            "scene_transition" => 0.5f,
            "combat" => 0.4f,
            "dialogue" => 0.2f,
            _ => 0.1f
        };
    }

    /// <summary>
    /// 计算情绪权重
    /// </summary>
    private float CalculateEmotionalWeight(WorldEvent evt)
    {
        return evt.EventType.ToLower() switch
        {
            "core_trauma" => 1.0f,
            "npc_death" => 0.9f,
            "combat" => 0.8f,
            "main_relationship_change" => 0.9f,
            "dialogue" => 0.4f,
            _ => 0.2f
        };
    }

    /// <summary>
    /// 计算剧情依赖
    /// </summary>
    private async Task<float> CalculatePlotDependencyAsync(TrpgScope scope, string characterId, WorldEvent evt)
    {
        var dependency = 0.0f;

        // 1. 检查事件是否影响当前目标
        var objectives = await _objectiveLayer.GetActiveObjectivesAsync(scope, characterId);
        if (objectives.Any(o => evt.Payload.ContainsKey("objective")))
            dependency += 0.5f;

        return Math.Min(dependency, 1.0f);
    }

    /// <summary>
    /// 计算伏笔潜力
    /// </summary>
    private async Task<float> CalculateForeshadowPotentialAsync(TrpgScope scope, string characterId, WorldEvent evt)
    {
        var potential = 0.0f;

        // 1. 检查事件是否被因果图谱引用为伏笔
        // TODO: 需要扩展 CausalGraph API
        // 简化处理：基于事件类型判断
        if (evt.EventType.ToLower() == "discovery" || 
            evt.EventType.ToLower() == "core_secret_reveal")
            potential += 0.6f;

        return Math.Min(potential, 1.0f);
    }

    /// <summary>
    /// 计算身份影响
    /// </summary>
    private float CalculateIdentityImpact(WorldEvent evt)
    {
        return evt.EventType.ToLower() switch
        {
            "core_trauma" => 1.0f,
            "main_relationship_change" => 0.9f,
            "faction_change" => 0.85f,
            "identity_reveal" => 0.95f,
            _ => 0.1f
        };
    }

    /// <summary>
    /// 计算目标相关性
    /// </summary>
    private async Task<float> CalculateObjectiveRelevanceAsync(TrpgScope scope, string characterId, WorldEvent evt)
    {
        var objectives = await _objectiveLayer.GetActiveObjectivesAsync(scope, characterId);
        if (objectives.Count == 0)
            return 0.0f;

        var maxRelevance = 0.0f;
        var eventText = $"{evt.EventType} {evt.Result} {string.Join(" ", evt.Payload.Values)}".ToLower();

        foreach (var objective in objectives)
        {
            var objectiveText = objective.Description.ToLower();
            var keywords = objectiveText.Split(new[] { ' ', '，', ',', '。', '.' }, StringSplitOptions.RemoveEmptyEntries);
            
            var matchCount = keywords.Count(k => eventText.Contains(k));
            var relevance = (float)matchCount / keywords.Length;
            
            if (relevance > maxRelevance)
                maxRelevance = relevance;
        }

        return maxRelevance;
    }

    /// <summary>
    /// 动态提升事件引力
    /// 当发现某事件是伏笔时，回溯提升其 Narrative Gravity
    /// </summary>
    public async Task BoostGravityAsync(TrpgScope scope, string characterId, long eventId, float boostAmount)
    {
        // 获取事件
        var events = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var evt = events.FirstOrDefault(e => e.EventId == eventId);
        if (evt == null)
            return;

        // 重新计算引力
        var weight = await CalculateGravityAsync(scope, characterId, evt);
        
        // 提升引力
        weight.NarrativeGravity = Math.Min(weight.NarrativeGravity + boostAmount, 1.0f);
        weight.ForeshadowPotential = Math.Min(weight.ForeshadowPotential + boostAmount, 1.0f);

        // TODO: 需要扩展 ChatDatabase 以支持存储 NarrativeWeight
        // 当前仅记录日志，实际存储需要数据库支持
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] NarrativeGravity: 提升事件引力 (EventId={eventId}, NewGravity={weight.NarrativeGravity:F2})");
    }

    /// <summary>
    /// 批量计算所有事件的引力
    /// </summary>
    public async Task<Dictionary<long, NarrativeWeight>> CalculateAllGravityAsync(TrpgScope scope, string characterId)
    {
        var events = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var weights = new Dictionary<long, NarrativeWeight>();

        foreach (var evt in events)
        {
            var weight = await CalculateGravityAsync(scope, characterId, evt);
            weights[evt.EventId] = weight;
        }

        return weights;
    }
}
