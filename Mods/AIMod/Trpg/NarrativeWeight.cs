using System;

namespace AIMod.Trpg;

/// <summary>
/// Narrative Weight - 叙事质量
/// 
/// 职责：定义事件的多维度重要性评分
/// 
/// 核心思想：
/// - 时间 ≠ 重要性
/// - 不是按时间压缩，而是按叙事引力压缩
/// - 真正长期存在的不是"事件文本"，而是"结构性影响"
/// 
/// 评分维度：
/// - TemporalDecay：时间衰减（越新越高）
/// - NarrativeGravity：叙事引力（持续影响未来叙事的能力）
/// - EmotionalWeight：情绪权重（情感强度）
/// - PlotDependency：剧情依赖（对主线的影响）
/// - ForeshadowPotential：伏笔潜力（未来成为伏笔的可能性）
/// - IdentityImpact：身份影响（对角色身份的影响）
/// - ObjectiveRelevance：目标相关性（对当前目标的相关性）
/// </summary>
public class NarrativeWeight
{
    /// <summary>
    /// 时间衰减（0~1）
    /// 越新的事件越高，用于基础的时间权重
    /// </summary>
    public float TemporalDecay { get; set; } = 1.0f;

    /// <summary>
    /// 叙事引力（0~1）
    /// 事件持续影响未来叙事的能力
    /// 这是核心维度，决定事件是否应该长期保留
    /// </summary>
    public float NarrativeGravity { get; set; } = 0.0f;

    /// <summary>
    /// 情绪权重（0~1）
    /// 事件的情绪强度
    /// </summary>
    public float EmotionalWeight { get; set; } = 0.0f;

    /// <summary>
    /// 剧情依赖（0~1）
    /// 事件对主线剧情的影响程度
    /// </summary>
    public float PlotDependency { get; set; } = 0.0f;

    /// <summary>
    /// 伏笔潜力（0~1）
    /// 事件未来成为伏笔的可能性
    /// </summary>
    public float ForeshadowPotential { get; set; } = 0.0f;

    /// <summary>
    /// 身份影响（0~1）
    /// 事件对角色身份的影响
    /// </summary>
    public float IdentityImpact { get; set; } = 0.0f;

    /// <summary>
    /// 目标相关性（0~1）
    /// 事件对当前目标的相关性
    /// </summary>
    public float ObjectiveRelevance { get; set; } = 0.0f;

    /// <summary>
    /// 计算综合权重
    /// 使用加权平均，NarrativeGravity 权重最高
    /// </summary>
    public float CalculateTotalWeight()
    {
        // NarrativeGravity 是核心，权重最高
        return (TemporalDecay * 0.1f) +
               (NarrativeGravity * 0.35f) +
               (EmotionalWeight * 0.1f) +
               (PlotDependency * 0.2f) +
               (ForeshadowPotential * 0.1f) +
               (IdentityImpact * 0.1f) +
               (ObjectiveRelevance * 0.05f);
    }

    /// <summary>
    /// 判断事件是否应该长期保留
    /// </summary>
    public bool ShouldPersist()
    {
        // 如果叙事引力极高，应该长期保留
        if (NarrativeGravity > 0.8f)
            return true;

        // 如果剧情依赖极高，应该长期保留
        if (PlotDependency > 0.9f)
            return true;

        // 如果身份影响极高，应该长期保留
        if (IdentityImpact > 0.9f)
            return true;

        // 如果综合权重极高，应该长期保留
        return CalculateTotalWeight() > 0.7f;
    }

    /// <summary>
    /// 判断事件是否为高引力节点
    /// </summary>
    public bool IsHighGravityNode()
    {
        return NarrativeGravity > 0.7f;
    }

    /// <summary>
    /// 创建默认权重
    /// </summary>
    public static NarrativeWeight CreateDefault()
    {
        return new NarrativeWeight
        {
            TemporalDecay = 1.0f,
            NarrativeGravity = 0.0f,
            EmotionalWeight = 0.0f,
            PlotDependency = 0.0f,
            ForeshadowPotential = 0.0f,
            IdentityImpact = 0.0f,
            ObjectiveRelevance = 0.0f
        };
    }

    /// <summary>
    /// 创建高引力权重（用于重要事件）
    /// </summary>
    public static NarrativeWeight CreateHighGravity()
    {
        return new NarrativeWeight
        {
            TemporalDecay = 1.0f,
            NarrativeGravity = 0.9f,
            EmotionalWeight = 0.7f,
            PlotDependency = 0.8f,
            ForeshadowPotential = 0.6f,
            IdentityImpact = 0.5f,
            ObjectiveRelevance = 0.7f
        };
    }

    /// <summary>
    /// 创建临时权重（用于普通事件）
    /// </summary>
    public static NarrativeWeight CreateEphemeral()
    {
        return new NarrativeWeight
        {
            TemporalDecay = 1.0f,
            NarrativeGravity = 0.1f,
            EmotionalWeight = 0.2f,
            PlotDependency = 0.0f,
            ForeshadowPotential = 0.1f,
            IdentityImpact = 0.0f,
            ObjectiveRelevance = 0.0f
        };
    }
}
