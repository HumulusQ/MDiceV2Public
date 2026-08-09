using System;
using System.Collections.Generic;
using System.Linq;

namespace AIMod.Trpg;

/// <summary>
/// Salience Classification System - 重要性分类系统
/// 
/// 职责：对事件进行重要性分类，决定其保留策略
/// 
/// 分类原则：
/// - 不是按时间压缩，而是按叙事引力压缩
/// - 保留叙事重力，压缩叙事噪声
/// 
/// 事件分类：
/// - Type A：Ephemeral（临时）- 玩笑、普通动作，快速蒸发
/// - Type B：Contextual（上下文）- 当前场景重要，场景结束后淡化
/// - Type C：Narrative（叙事）- 形成剧情推进，长期保留
/// - Type D：Foundational（基础骨架）- 永不蒸发，主目标、世界规则、核心秘密
/// </summary>
public class SalienceClassification
{
    /// <summary>
    /// 事件重要性类型
    /// </summary>
    public enum SalienceType
    {
        /// <summary>
        /// 临时事件
        /// 玩笑、普通动作、闲聊
        /// 快速蒸发，不长期保留
        /// </summary>
        Ephemeral,

        /// <summary>
        /// 上下文事件
        /// 当前场景重要
        /// 场景结束后淡化
        /// </summary>
        Contextual,

        /// <summary>
        /// 叙事事件
        /// 形成剧情推进
        /// 长期保留
        /// </summary>
        Narrative,

        /// <summary>
        /// 基础骨架事件
        /// 永不蒸发
        /// 主目标、世界规则、核心秘密、主关系、阵营结构、核心创伤
        /// </summary>
        Foundational
    }

    /// <summary>
    /// 分类结果
    /// </summary>
    public class ClassificationResult
    {
        public SalienceType Type { get; set; }
        public string Reason { get; set; } = "";
        public float Confidence { get; set; } = 0.0f;
    }

    /// <summary>
    /// 对事件进行分类
    /// </summary>
    public ClassificationResult ClassifyEvent(WorldEvent evt, NarrativeWeight weight)
    {
        // 1. 检查是否为基础骨架事件
        if (IsFoundationalEvent(evt, weight))
        {
            return new ClassificationResult
            {
                Type = SalienceType.Foundational,
                Reason = "基础骨架事件：主目标、世界规则、核心秘密、主关系、阵营结构、核心创伤",
                Confidence = 0.9f
            };
        }

        // 2. 检查是否为叙事事件
        if (IsNarrativeEvent(evt, weight))
        {
            return new ClassificationResult
            {
                Type = SalienceType.Narrative,
                Reason = "叙事事件：形成剧情推进，长期保留",
                Confidence = 0.8f
            };
        }

        // 3. 检查是否为上下文事件
        if (IsContextualEvent(evt, weight))
        {
            return new ClassificationResult
            {
                Type = SalienceType.Contextual,
                Reason = "上下文事件：当前场景重要，场景结束后淡化",
                Confidence = 0.7f
            };
        }

        // 4. 默认为临时事件
        return new ClassificationResult
        {
            Type = SalienceType.Ephemeral,
            Reason = "临时事件：玩笑、普通动作，快速蒸发",
            Confidence = 0.6f
        };
    }

    /// <summary>
    /// 判断是否为基础骨架事件
    /// </summary>
    private bool IsFoundationalEvent(WorldEvent evt, NarrativeWeight weight)
    {
        // 基础骨架事件特征：
        // - 叙事引力极高（> 0.8）
        // - 剧情依赖极高（> 0.9）
        // - 身份影响极高（> 0.9）
        // - 特定事件类型

        if (weight.NarrativeGravity > 0.8f)
            return true;

        if (weight.PlotDependency > 0.9f)
            return true;

        if (weight.IdentityImpact > 0.9f)
            return true;

        // 特定事件类型
        var foundationalTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "objective_change",
            "world_rule_change",
            "core_secret_reveal",
            "main_relationship_change",
            "faction_change",
            "core_trauma"
        };

        return foundationalTypes.Contains(evt.EventType);
    }

    /// <summary>
    /// 判断是否为叙事事件
    /// </summary>
    private bool IsNarrativeEvent(WorldEvent evt, NarrativeWeight weight)
    {
        // 叙事事件特征：
        // - 叙事引力较高（> 0.5）
        // - 剧情依赖较高（> 0.6）
        // - 伏笔潜力较高（> 0.5）

        if (weight.NarrativeGravity > 0.5f)
            return true;

        if (weight.PlotDependency > 0.6f)
            return true;

        if (weight.ForeshadowPotential > 0.5f)
            return true;

        // 特定事件类型
        var narrativeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "discovery",
            "item_acquisition",
            "npc_death",
            "relationship_change",
            "scene_transition",
            "combat",
            "narrative",
            "event",        // InfoExtractor 输出的事件标签
            "state_transaction"  // 状态变更事务事件
        };

        return narrativeTypes.Contains(evt.EventType);
    }

    /// <summary>
    /// 判断是否为上下文事件
    /// </summary>
    private bool IsContextualEvent(WorldEvent evt, NarrativeWeight weight)
    {
        // 上下文事件特征：
        // - 与当前场景相关
        // - 情绪权重中等（> 0.3）
        // - 目标相关性中等（> 0.3）

        if (weight.EmotionalWeight > 0.3f)
            return true;

        if (weight.ObjectiveRelevance > 0.3f)
            return true;

        // 特定事件类型
        var contextualTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dialogue",
            "observation",
            "interaction"
        };

        return contextualTypes.Contains(evt.EventType);
    }

    /// <summary>
    /// 获取事件的保留策略
    /// </summary>
    public string GetRetentionPolicy(SalienceType type)
    {
        return type switch
        {
            SalienceType.Ephemeral => "快速蒸发，场景结束后删除",
            SalienceType.Contextual => "场景结束后淡化，保留摘要",
            SalienceType.Narrative => "长期保留，定期压缩",
            SalienceType.Foundational => "永不蒸发，永久保留结构性影响",
            _ => "未知策略"
        };
    }

    /// <summary>
    /// 批量分类事件
    /// </summary>
    public List<ClassificationResult> ClassifyEvents(List<WorldEvent> events, Func<WorldEvent, NarrativeWeight> weightCalculator)
    {
        var results = new List<ClassificationResult>();

        foreach (var evt in events)
        {
            var weight = weightCalculator(evt);
            var result = ClassifyEvent(evt, weight);
            results.Add(result);
        }

        return results;
    }
}
