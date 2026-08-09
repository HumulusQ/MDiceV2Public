using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Narrative Projection Engine - 叙事投影引擎
/// 
/// 职责：从 Runtime State 动态投影到 Prompt，决定 AI 是否像"活人"
/// 
/// 核心问题：
/// - "哪些东西该进入 Prompt？"
/// - 而不是："如何记忆"
/// 
/// 投影原则：
/// - Runtime 唯一：一个真实世界
/// - Projection 多样：多个观察角度
/// - 动态投影：根据当前上下文调整投影内容
/// 
/// 投影维度：
/// 1. Narrative Momentum（叙事动量）- 故事推进感
/// 2. Character Agency（角色能动性）- 决策能力
/// 3. Emotional Continuity（情绪连续性）- 情感连贯
/// 4. Temporal Coherence（时间连贯性）- 时间线完整
/// 5. Spatial Awareness（空间感知）- 场景理解
/// 6. Social Context（社交上下文）- 关系网络
/// 
/// 这是 Narrative Runtime 的核心，决定了：
/// - AI 是否像"活人"
/// - 是否有连续感
/// - 是否记得重点
/// - 是否有故事推进感
/// </summary>
public class NarrativeProjectionEngine
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly SalienceRanking _salienceRanking;
    private readonly EpisodicMemory _episodicMemory;
    private readonly ObjectiveLayer _objectiveLayer;
    private readonly EntityCanonicalizer _entityCanonicalizer;

    public NarrativeProjectionEngine(
        IModContext context,
        ChatDatabase db,
        EventLog eventLog,
        SalienceRanking salienceRanking,
        EpisodicMemory episodicMemory,
        ObjectiveLayer objectiveLayer,
        EntityCanonicalizer entityCanonicalizer)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _salienceRanking = salienceRanking;
        _episodicMemory = episodicMemory;
        _objectiveLayer = objectiveLayer;
        _entityCanonicalizer = entityCanonicalizer;
    }

    /// <summary>
    /// 投影配置
    /// 控制投影的强度和范围
    /// </summary>
    public class ProjectionConfig
    {
        /// <summary>
        /// 叙事动量权重（0~1）
        /// 控制故事推进感的强度
        /// </summary>
        public double NarrativeMomentumWeight { get; set; } = 0.8;

        /// <summary>
        /// 角色能动性权重（0~1）
        /// 控制决策能力的强度
        /// </summary>
        public double CharacterAgencyWeight { get; set; } = 0.7;

        /// <summary>
        /// 情绪连续性权重（0~1）
        /// 控制情感连贯的强度
        /// </summary>
        public double EmotionalContinuityWeight { get; set; } = 0.6;

        /// <summary>
        /// 时间连贯性权重（0~1）
        /// 控制时间线完整的强度
        /// </summary>
        public double TemporalCoherenceWeight { get; set; } = 0.5;

        /// <summary>
        /// 空间感知权重（0~1）
        /// 控制场景理解的强度
        /// </summary>
        public double SpatialAwarenessWeight { get; set; } = 0.9;

        /// <summary>
        /// 社交上下文权重（0~1）
        /// 控制关系网络的强度
        /// </summary>
        public double SocialContextWeight { get; set; } = 0.7;

        /// <summary>
        /// 最大 Token 预算
        /// </summary>
        public int MaxTokenBudget { get; set; } = 2000;
    }

    /// <summary>
    /// 投影结果
    /// 包含所有维度的投影内容
    /// </summary>
    public class ProjectionResult
    {
        /// <summary>
        /// 叙事动量投影
        /// </summary>
        public string NarrativeMomentum { get; set; } = "";

        /// <summary>
        /// 角色能动性投影
        /// </summary>
        public string CharacterAgency { get; set; } = "";

        /// <summary>
        /// 情绪连续性投影
        /// </summary>
        public string EmotionalContinuity { get; set; } = "";

        /// <summary>
        /// 时间连贯性投影
        /// </summary>
        public string TemporalCoherence { get; set; } = "";

        /// <summary>
        /// 空间感知投影
        /// </summary>
        public string SpatialAwareness { get; set; } = "";

        /// <summary>
        /// 社交上下文投影
        /// </summary>
        public string SocialContext { get; set; } = "";

        /// <summary>
        /// 估算的 Token 数量
        /// </summary>
        public int EstimatedTokens { get; set; } = 0;
    }

    /// <summary>
    /// 执行动态叙事投影
    /// 从 Runtime State 投影到 Prompt
    /// </summary>
    public async Task<ProjectionResult> ProjectAsync(
        TrpgScope scope,
        string characterId,
        string currentSceneId,
        List<string> presentEntities,
        ProjectionConfig? config = null)
    {
        config ??= new ProjectionConfig();

        var result = new ProjectionResult();

        // 1. 叙事动量投影
        if (config.NarrativeMomentumWeight > 0)
        {
            result.NarrativeMomentum = await ProjectNarrativeMomentumAsync(
                scope, characterId, config.NarrativeMomentumWeight);
        }

        // 2. 角色能动性投影
        if (config.CharacterAgencyWeight > 0)
        {
            result.CharacterAgency = await ProjectCharacterAgencyAsync(
                scope, characterId, config.CharacterAgencyWeight);
        }

        // 3. 情绪连续性投影
        if (config.EmotionalContinuityWeight > 0)
        {
            result.EmotionalContinuity = await ProjectEmotionalContinuityAsync(
                scope, characterId, config.EmotionalContinuityWeight);
        }

        // 4. 时间连贯性投影
        if (config.TemporalCoherenceWeight > 0)
        {
            result.TemporalCoherence = await ProjectTemporalCoherenceAsync(
                scope, characterId, config.TemporalCoherenceWeight);
        }

        // 5. 空间感知投影
        if (config.SpatialAwarenessWeight > 0)
        {
            result.SpatialAwareness = await ProjectSpatialAwarenessAsync(
                scope, characterId, currentSceneId, presentEntities, config.SpatialAwarenessWeight);
        }

        // 6. 社交上下文投影
        if (config.SocialContextWeight > 0)
        {
            result.SocialContext = await ProjectSocialContextAsync(
                scope, characterId, presentEntities, config.SocialContextWeight);
        }

        // 估算 Token 数量
        result.EstimatedTokens = EstimateTokens(result);

        // 如果超出预算，进行裁剪
        if (result.EstimatedTokens > config.MaxTokenBudget)
        {
            result = TrimProjection(result, config.MaxTokenBudget);
        }

        return result;
    }

    /// <summary>
    /// 投影叙事动量
    /// 故事推进感，让 AI 知道故事正在往哪走
    /// </summary>
    private async Task<string> ProjectNarrativeMomentumAsync(TrpgScope scope, string characterId, double weight)
    {
        var objectives = await _objectiveLayer.GetActiveObjectivesAsync(scope, characterId);

        var sb = new StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【叙事动量（Narrative Momentum）】");
        sb.AppendLine("========================");
        sb.AppendLine($"权重: {weight:F2}");

        // 当前目标
        if (objectives.Count > 0)
        {
            sb.AppendLine("\n当前目标:");
            foreach (var obj in objectives.Take(3))
            {
                sb.AppendLine($"  • {obj.Description} (优先级: {obj.Priority})");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 投影角色能动性
    /// 决策能力，让 AI 知道可以做什么
    /// </summary>
    private async Task<string> ProjectCharacterAgencyAsync(TrpgScope scope, string characterId, double weight)
    {
        var memories = await _episodicMemory.GetMemoriesAsync(scope, characterId);
        var objectives = await _objectiveLayer.GetActiveObjectivesAsync(scope, characterId);

        var sb = new StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【角色能动性（Character Agency）】");
        sb.AppendLine("========================");
        sb.AppendLine($"权重: {weight:F2}");

        // 可行动项
        sb.AppendLine("\n可行动项:");
        if (objectives.Count > 0)
        {
            foreach (var obj in objectives.Take(3))
            {
                sb.AppendLine($"  • {obj.Description}");
            }
        }
        else
        {
            sb.AppendLine("  • 无明确目标，自由行动");
        }

        // 能力限制
        sb.AppendLine("\n能力限制:");
        sb.AppendLine("  • 只能影响当前场景内的实体");
        sb.AppendLine("  • 不能改变已经发生的事件");
        sb.AppendLine("  • 不能知道未发现的信息");

        return sb.ToString();
    }

    /// <summary>
    /// 投影情绪连续性
    /// 情感连贯，让 AI 保持情绪稳定
    /// </summary>
    private async Task<string> ProjectEmotionalContinuityAsync(TrpgScope scope, string characterId, double weight)
    {
        var memories = await _episodicMemory.GetMemoriesAsync(scope, characterId);
        var emotionalMemories = memories.Where(m => m.MemoryType == EpisodicMemory.MemoryType.Emotional).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【情绪连续性（Emotional Continuity）】");
        sb.AppendLine("========================");
        sb.AppendLine($"权重: {weight:F2}");

        if (emotionalMemories.Count > 0)
        {
            sb.AppendLine("\n当前情绪状态:");
            foreach (var mem in emotionalMemories.Take(3))
            {
                var intensity = mem.Metadata.TryGetValue("intensity", out var val) ? Convert.ToDouble(val) : 0.5;
                sb.AppendLine($"  • {mem.Content} (强度: {intensity:F2})");
            }
        }
        else
        {
            sb.AppendLine("\n当前情绪状态: 平静");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 投影时间连贯性
    /// 时间线完整，让 AI 理解时间流逝
    /// </summary>
    private async Task<string> ProjectTemporalCoherenceAsync(TrpgScope scope, string characterId, double weight)
    {
        var recentEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var lastEvent = recentEvents.LastOrDefault();

        var sb = new StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【时间连贯性（Temporal Coherence）】");
        sb.AppendLine("========================");
        sb.AppendLine($"权重: {weight:F2}");

        if (lastEvent != null)
        {
            var timeSinceLast = DateTime.UtcNow - lastEvent.Timestamp;
            sb.AppendLine($"\n时间流逝: {FormatTimeSpan(timeSinceLast)}");
            sb.AppendLine($"最后事件: {lastEvent.EventType} - {lastEvent.Result}");
        }
        else
        {
            sb.AppendLine("\n时间流逝: 刚开始");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 投影空间感知
    /// 场景理解，让 AI 理解当前环境
    /// </summary>
    private async Task<string> ProjectSpatialAwarenessAsync(
        TrpgScope scope, string characterId, string currentSceneId, List<string> presentEntities, double weight)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【空间感知（Spatial Awareness）】");
        sb.AppendLine("========================");
        sb.AppendLine($"权重: {weight:F2}");

        sb.AppendLine($"\n当前场景: {currentSceneId}");
        sb.AppendLine($"在场实体: {string.Join(", ", presentEntities)}");

        return sb.ToString();
    }

    /// <summary>
    /// 投影社交上下文
    /// 关系网络，让 AI 理解人际关系
    /// </summary>
    private async Task<string> ProjectSocialContextAsync(
        TrpgScope scope, string characterId, List<string> presentEntities, double weight)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【社交上下文（Social Context）】");
        sb.AppendLine("========================");
        sb.AppendLine($"权重: {weight:F2}");

        sb.AppendLine("\n在场关系:");
        foreach (var entity in presentEntities.Take(5))
        {
            sb.AppendLine($"  • {entity}: 关系未知");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 估算 Token 数量
    /// </summary>
    private int EstimateTokens(ProjectionResult result)
    {
        var totalLength = result.NarrativeMomentum.Length +
                         result.CharacterAgency.Length +
                         result.EmotionalContinuity.Length +
                         result.TemporalCoherence.Length +
                         result.SpatialAwareness.Length +
                         result.SocialContext.Length;
        
        // 粗略估算：中文字符 ≈ 1.5 tokens
        return (int)(totalLength * 1.5);
    }

    /// <summary>
    /// 裁剪投影以适应 Token 预算
    /// </summary>
    private ProjectionResult TrimProjection(ProjectionResult result, int maxTokens)
    {
        // 简单裁剪策略：按权重从低到高裁剪
        // 这里简化处理，直接截断每个部分
        var targetLength = maxTokens / 1.5 / 6; // 平均分配

        result.NarrativeMomentum = TruncateString(result.NarrativeMomentum, (int)targetLength);
        result.CharacterAgency = TruncateString(result.CharacterAgency, (int)targetLength);
        result.EmotionalContinuity = TruncateString(result.EmotionalContinuity, (int)targetLength);
        result.TemporalCoherence = TruncateString(result.TemporalCoherence, (int)targetLength);
        result.SpatialAwareness = TruncateString(result.SpatialAwareness, (int)targetLength);
        result.SocialContext = TruncateString(result.SocialContext, (int)targetLength);

        result.EstimatedTokens = EstimateTokens(result);
        return result;
    }

    /// <summary>
    /// 截断字符串
    /// </summary>
    private string TruncateString(string str, int maxLength)
    {
        if (str.Length <= maxLength)
            return str;
        return str.Substring(0, maxLength) + "...";
    }

    /// <summary>
    /// 格式化时间跨度
    /// </summary>
    private string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
            return "刚刚";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24)
            return $"{(int)span.TotalHours} 小时前";
        return $"{(int)span.TotalDays} 天前";
    }
}
