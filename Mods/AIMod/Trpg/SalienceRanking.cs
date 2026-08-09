using System;
using System.Collections.Generic;
using System.Linq;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Salience Ranking - 重要性排序引擎
/// 
/// 职责：动态重要性管理，解决"信息太多"问题
/// 
/// 核心问题：
/// - 不是问"什么相关？"
/// - 而是问"什么现在最重要？"
/// 
/// 评分维度（动态综合）：
/// - 当前目标相关性（权重: 0.25）
/// - 当前场景相关性（权重: 0.20）
/// - 情绪强度（权重: 0.15）
/// - 伏笔热度（权重: 0.15）
/// - 最近引用频率（权重: 0.10）
/// - 剧情推进价值（权重: 0.15）
/// 
/// 这是 Narrative Runtime 的核心，用于：
/// - 动态重要性管理
/// - Prompt 内容筛选
/// - 投影复杂度控制
/// </summary>
public class SalienceRanking
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly ObjectiveLayer _objectiveLayer;
    private readonly CausalGraph _causalGraph;

    public SalienceRanking(
        IModContext context,
        ChatDatabase db,
        EventLog eventLog,
        ObjectiveLayer objectiveLayer,
        CausalGraph? causalGraph = null)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _objectiveLayer = objectiveLayer;
        _causalGraph = causalGraph ?? new CausalGraph(context, db, eventLog);
    }

    /// <summary>
    /// 事件重要性评分
    /// </summary>
    public class EventSalience
    {
        public WorldEvent Event { get; set; } = new();
        public double Score { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// 对事件进行重要性排序
    /// </summary>
    public async Task<List<EventSalience>> RankEventsAsync(TrpgScope scope, string characterId, List<WorldEvent> events, List<string>? currentEntities = null, string? currentSceneId = null)
    {
        var activeObjectives = await _objectiveLayer.GetActiveObjectivesAsync(scope, characterId);
        var rankedEvents = new List<EventSalience>();

        foreach (var evt in events)
        {
            var salience = new EventSalience
            {
                Event = evt,
                Score = CalculateEventSalience(evt, activeObjectives, currentEntities, currentSceneId),
                Reason = ExplainSalience(evt, activeObjectives, currentEntities, currentSceneId)
            };

            rankedEvents.Add(salience);
        }

        // 按评分降序排序
        return rankedEvents.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// 计算事件重要性评分（动态综合）
    /// 综合考虑：目标、场景、情绪、伏笔、引用频率、剧情推进价值
    /// </summary>
    private double CalculateEventSalience(WorldEvent evt, List<QuestObjective> activeObjectives, List<string>? currentEntities, string? currentSceneId)
    {
        var score = 0.0;

        // 1. 当前目标相关性 (权重: 0.25)
        score += CalculateObjectiveRelevance(evt, activeObjectives) * 0.25;

        // 2. 当前场景相关性 (权重: 0.20)
        score += CalculateSceneRelevance(evt, currentSceneId) * 0.20;

        // 3. 情绪强度 (权重: 0.15)
        score += CalculateEmotionalIntensity(evt) * 0.15;

        // 4. 伏笔热度 (权重: 0.15)

        // 5. 最近引用频率 (权重: 0.10)
        score += CalculateRecencyFrequency(evt) * 0.10;

        // 6. 剧情推进价值 (权重: 0.15)
        score += CalculateNarrativeValue(evt) * 0.15;

        return score;
    }

    /// <summary>
    /// 计算场景相关性
    /// </summary>
    private double CalculateSceneRelevance(WorldEvent evt, string? currentSceneId)
    {
        if (string.IsNullOrWhiteSpace(currentSceneId))
            return 0.5;

        if (evt.Payload.TryGetValue("scene_id", out var sceneId) && sceneId?.ToString() == currentSceneId)
            return 1.0;

        return 0.3;
    }

    /// <summary>
    /// 计算情绪强度
    /// </summary>
    private double CalculateEmotionalIntensity(WorldEvent evt)
    {
        // 根据事件类型判断情绪强度
        return evt.EventType.ToLower() switch
        {
            "npc_death" => 0.9,
            "combat" => 0.8,
            "relationship_change" => 0.7,
            "discovery" => 0.6,
            "dialogue" => 0.4,
            _ => 0.3
        };
    }

    /// <summary>
    /// 计算最近引用频率
    /// </summary>
    private double CalculateRecencyFrequency(WorldEvent evt)
    {
        var age = DateTime.UtcNow - evt.Timestamp;
        if (age < TimeSpan.FromMinutes(30))
            return 1.0;
        if (age < TimeSpan.FromHours(1))
            return 0.8;
        if (age < TimeSpan.FromHours(6))
            return 0.6;
        if (age < TimeSpan.FromDays(1))
            return 0.4;
        return 0.2;
    }

    /// <summary>
    /// 计算剧情推进价值
    /// </summary>
    private double CalculateNarrativeValue(WorldEvent evt)
    {
        // 如果事件有后果，说明是重要节点
        if (evt.Consequences != null && evt.Consequences.Count > 0)
            return 1.0;

        // 根据事件类型判断剧情价值
        return evt.EventType.ToLower() switch
        {
            "objective_change" => 0.9,
            "scene_transition" => 0.8,
            "discovery" => 0.7,
            "item_acquisition" => 0.6,
            _ => 0.4
        };
    }

    /// <summary>
    /// 计算目标相关性
    /// </summary>
    private double CalculateObjectiveRelevance(WorldEvent evt, List<QuestObjective> activeObjectives)
    {
        if (activeObjectives.Count == 0)
            return 0.0;

        var maxRelevance = 0.0;

        foreach (var objective in activeObjectives)
        {
            var relevance = 0.0;

            // 检查事件是否直接完成目标
            if (evt.EventType == "objective_change" && evt.Result.Contains(objective.Description, StringComparison.OrdinalIgnoreCase))
            {
                relevance = 1.0;
            }

            // 检查事件是否与目标相关
            if (evt.Payload.TryGetValue("objective", out var evtObjective) && 
                evtObjective.ToString() == objective.Description)
            {
                relevance = 0.8;
            }

            // 检查事件是否推进目标
            if (evt.EventType == "discovery" || evt.EventType == "item_acquisition")
            {
                relevance = 0.5;
            }

            if (relevance > maxRelevance)
                maxRelevance = relevance;
        }

        return maxRelevance;
    }

    /// <summary>
    /// 计算实体相关性
    /// </summary>
    private double CalculateEntityRelevance(WorldEvent evt, List<string>? currentEntities)
    {
        if (currentEntities == null || currentEntities.Count == 0)
            return 0.0;

        var relevance = 0.0;

        // 检查事件是否涉及在场实体
        if (evt.SourceEntityId != null && currentEntities.Contains(evt.SourceEntityId, StringComparer.OrdinalIgnoreCase))
        {
            relevance = 0.8;
        }

        if (evt.TargetEntityId != null && currentEntities.Contains(evt.TargetEntityId, StringComparer.OrdinalIgnoreCase))
        {
            relevance = Math.Max(relevance, 0.8);
        }

        // 检查事件是否发生在当前场景
        if (evt.SceneId != null && currentEntities.Any())
        {
            relevance = Math.Max(relevance, 0.5);
        }

        return relevance;
    }

    /// <summary>
    /// 计算时间相关性
    /// </summary>
    private double CalculateTemporalRelevance(WorldEvent evt)
    {
        var age = DateTime.UtcNow - evt.Timestamp;
        
        // 最近事件权重高
        if (age < TimeSpan.FromHours(1))
            return 1.0;
        else if (age < TimeSpan.FromHours(6))
            return 0.8;
        else if (age < TimeSpan.FromDays(1))
            return 0.6;
        else if (age < TimeSpan.FromDays(7))
            return 0.4;
        else
            return 0.2;
    }

    /// <summary>
    /// 解释重要性评分
    /// </summary>
    private string ExplainSalience(WorldEvent evt, List<QuestObjective> activeObjectives, List<string>? currentEntities, string? currentSceneId)
    {
        var reasons = new List<string>();

        if (CalculateObjectiveRelevance(evt, activeObjectives) > 0.5)
            reasons.Add("目标相关");

            reasons.Add("伏笔相关");

        if (CalculateEntityRelevance(evt, currentEntities) > 0.5)
            reasons.Add("实体相关");

        if (CalculateTemporalRelevance(evt) > 0.8)
            reasons.Add("最近事件");

        return reasons.Count > 0 ? string.Join(", ", reasons) : "一般事件";
    }

    /// <summary>
    /// 对记忆进行重要性排序
    /// </summary>
    public async Task<List<EpisodicMemory.CharacterMemory>> RankMemoriesAsync(TrpgScope scope, string characterId, List<EpisodicMemory.CharacterMemory> memories)
    {
        var rankedMemories = memories.Select(memory => new
        {
            Memory = memory,
            Score = CalculateMemorySalience(memory)
        });

        return rankedMemories.OrderByDescending(r => r.Score).Select(r => r.Memory).ToList();
    }

    /// <summary>
    /// 计算记忆重要性评分
    /// </summary>
    private double CalculateMemorySalience(EpisodicMemory.CharacterMemory memory)
    {
        var score = 0.0;

        // 置信度权重
        score += memory.Confidence * 0.3;

        // 记忆类型权重
        var typeWeight = memory.MemoryType switch
        {
            EpisodicMemory.MemoryType.Episodic => 0.8,
            EpisodicMemory.MemoryType.Semantic => 0.7,
            EpisodicMemory.MemoryType.Suspicion => 0.6,
            EpisodicMemory.MemoryType.Emotional => 0.5,
            EpisodicMemory.MemoryType.Rumor => 0.3,
            EpisodicMemory.MemoryType.FalseBelief => 0.2,
            _ => 0.5
        };
        score += typeWeight * 0.4;

        // 访问频率权重
        var accessAge = DateTime.UtcNow - memory.LastAccessed;
        var accessWeight = accessAge < TimeSpan.FromDays(1) ? 1.0 : 
                           accessAge < TimeSpan.FromDays(7) ? 0.7 : 
                           accessAge < TimeSpan.FromDays(30) ? 0.4 : 0.2;
        score += accessWeight * 0.3;

        return score;
    }

    /// <summary>
    /// 获取 Top-N 重要事件
    /// </summary>
    public async Task<List<WorldEvent>> GetTopSalientEventsAsync(TrpgScope scope, string characterId, int topN = 10, List<string>? currentEntities = null, string? currentSceneId = null)
    {
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var rankedEvents = await RankEventsAsync(scope, characterId, allEvents, currentEntities, currentSceneId);
        
        return rankedEvents.Take(topN).Select(r => r.Event).ToList();
    }

    /// <summary>
    /// 生成重要性排序报告
    /// </summary>
    public string GenerateSalienceReport(List<EventSalience> rankedEvents)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【事件重要性排序】");
        sb.AppendLine("========================");

        for (int i = 0; i < rankedEvents.Count; i++)
        {
            var salience = rankedEvents[i];
            sb.AppendLine($"{i + 1}. [Event_{salience.Event.EventId}] {salience.Event.EventType} - 评分: {salience.Score:F2} ({salience.Reason})");
        }

        return sb.ToString();
    }
}
