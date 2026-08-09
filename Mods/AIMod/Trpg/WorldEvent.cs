using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AIMod.Trpg;

/// <summary>
/// 世界事件：结构化事件记录，替代聊天压缩的时间线
/// </summary>
public class WorldEvent
{
    public string WorldId { get; set; } = "";

    /// <summary>
    /// 事件ID（严格递增序号，用于 Event Log）
    /// </summary>
    public long EventId { get; set; }

    /// <summary>
    /// 事件类型：scene_transition, combat, dialogue, discovery, item_acquisition, npc_death, relationship_change, npc_identity_reveal, objective_change
    /// </summary>
    public string EventType { get; set; } = "";

    /// <summary>
    /// 参与角色列表
    /// </summary>
    public List<string> Actors { get; set; } = new List<string>();

    /// <summary>
    /// 事件发生位置
    /// </summary>
    public string Location { get; set; } = "";

    /// <summary>
    /// 事件结果
    /// </summary>
    public string Result { get; set; } = "";

    /// <summary>
    /// 世界状态变化列表
    /// </summary>
    public List<string> WorldChanges { get; set; } = new List<string>();

    /// <summary>
    /// 事件后果（因果链，用于 Narrative Runtime）
    /// 记录此事件导致的后续事件ID列表
    /// </summary>
    public List<long> Consequences { get; set; } = new List<long>();

    /// <summary>
    /// 事件时间
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 来源实体ID（用于 Event Log）
    /// </summary>
    public string? SourceEntityId { get; set; }

    /// <summary>
    /// 目标实体ID（用于 Event Log）
    /// </summary>
    public string? TargetEntityId { get; set; }

    /// <summary>
    /// 场景ID（用于 Event Log）
    /// </summary>
    public string? SceneId { get; set; }

    /// <summary>
    /// 事件负载（用于 Event Log，存储结构化数据）
    /// </summary>
    public Dictionary<string, object> Payload { get; set; } = new();

    // ═══════════════════════════════════════════
    //  语义元数据（Semantic Metadata）
    //  由 LLM 在语义蒸馏阶段生成并固化
    // ═══════════════════════════════════════════

    /// <summary>
    /// 语义摘要：LLM 对事件的叙事性总结
    /// 替代硬编码的 ExtractSummaryFromEvent
    /// </summary>
    public string? SemanticSummary { get; set; }

    /// <summary>
    /// 叙事权重：事件在叙事中的重要性（0~1）
    /// 由 LLM 评估，用于 Story Spine 筛选
    /// </summary>
    public double NarrativeWeight { get; set; } = 0.0;

    /// <summary>
    /// 叙事标签：事件的语义分类（如"冲突"、"揭示"、"转折"）
    /// 用于 Arc Consolidation 和语义检索
    /// </summary>
    public List<string> NarrativeTags { get; set; } = new();

    /// <summary>
    /// 情绪权重：事件的情绪强度（-1~1，负为负面，正为正面）
    /// 用于情感弧追踪
    /// </summary>
    public double EmotionalWeight { get; set; } = 0.0;

    /// <summary>
    /// 剧情弧归属：事件所属的剧情弧标识
    /// 用于 Arc Consolidation 和 Story Spine 分组
    /// </summary>
    public string? ArcAffinity { get; set; }

    /// <summary>
    /// 是否已语义蒸馏：标记该事件是否已通过 LLM 语义蒸馏
    /// </summary>
    public bool IsSemanticallyDistilled { get; set; } = false;

    /// <summary>
    /// 序列化为 JSON
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    /// <summary>
    /// 从 JSON 反序列化
    /// </summary>
    public static WorldEvent? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorldEvent>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 生成结构化描述（用于 Prompt）
    /// </summary>
    public string ToPromptString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[事件类型: {EventType}]");
        sb.AppendLine($"参与角色: {string.Join(", ", Actors)}");
        sb.AppendLine($"位置: {Location}");
        sb.AppendLine($"结果: {Result}");
        if (WorldChanges.Count > 0)
        {
            sb.AppendLine("世界变化:");
            foreach (var change in WorldChanges)
            {
                sb.AppendLine($"  - {change}");
            }
        }
        return sb.ToString();
    }
}
