using System;
using System.Collections.Generic;
using System.Linq;

namespace AIMod.Trpg;

internal static class NarrativeMemoryHeuristics
{
    public static NarrativeMemoryNode CreateFromTimelineNode(
        TimelineNode node,
        IEnumerable<string>? knownEntities = null)
    {
        var arcTags = InferArcTags(node.Content, Enumerable.Empty<string>());
        return new NarrativeMemoryNode
        {
            Summary = node.Content ?? "",
            NarrativeWeight = Math.Clamp(node.Importance / 10.0f, 0.1f, 1.0f),
            EmotionalWeight = InferEmotionalWeight(node.Content),
            RelationshipImpact = InferRelationshipImpact(node.Content, arcTags),
            GoalImpact = InferGoalImpact(node.Content, arcTags),
            MysteryWeight = InferMysteryWeight(node.Content, arcTags),
            IsResolved = false,
            InvolvedEntities = InferInvolvedEntities(node.Content, knownEntities),
            ArcTags = arcTags,
            Timestamp = node.CreatedAt,
            SourceEventId = node.EventSequence
        };
    }

    public static List<string> InferInvolvedEntities(string? content, IEnumerable<string>? knownEntities)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfValid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (trimmed.Length < 2 || trimmed.Length > 40) return;
            result.Add(trimmed);
        }

        var text = content ?? "";
        foreach (var entity in knownEntities ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(entity) &&
                text.Contains(entity.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                AddIfValid(entity);
            }
        }

        return result.ToList();
    }

    public static List<string> InferArcTags(string? content, IEnumerable<string>? existingTags = null)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string tag)
        {
            if (!string.IsNullOrWhiteSpace(tag))
                tags.Add(tag.Trim());
        }

        foreach (var tag in existingTags ?? Enumerable.Empty<string>())
            Add(tag);

        var text = content ?? "";

        if (ContainsAny(text, "背叛", "欺骗", "隐瞒", "决裂", "和解", "信任", "关系"))
            Add("relationship");

        if (ContainsAny(text, "目标", "任务", "委托", "誓言", "计划", "追踪", "寻找", "完成", "失败"))
            Add("goal");

        if (ContainsAny(text, "秘密", "谜", "真相", "线索", "密室", "异常", "失踪", "诅咒"))
            Add("mystery");

        if (ContainsAny(text, "战斗", "袭击", "受伤", "死亡", "牺牲", "逃亡"))
            Add("conflict");

        if (ContainsAny(text, "契约", "王国", "教会", "公会", "组织", "政治"))
            Add("world_state");

        return tags.ToList();
    }

    public static float InferEmotionalWeight(string? content)
    {
        var text = content ?? "";

        if (ContainsAny(text, "死亡", "牺牲", "背叛", "绝望", "恐惧", "痛苦", "崩溃"))
            return -0.8f;

        if (ContainsAny(text, "和解", "信任", "胜利", "拯救", "承诺", "希望"))
            return 0.7f;

        if (ContainsAny(text, "愤怒", "争吵", "威胁", "冲突"))
            return -0.5f;

        return 0.2f;
    }

    public static float InferRelationshipImpact(string? content, IReadOnlyCollection<string> tags)
    {
        var text = content ?? "";

        if (tags.Contains("relationship", StringComparer.OrdinalIgnoreCase) ||
            ContainsAny(text, "关系", "信任", "背叛", "和解", "决裂", "承诺", "隐瞒"))
        {
            return 0.7f;
        }

        return 0.2f;
    }

    public static float InferGoalImpact(string? content, IReadOnlyCollection<string> tags)
    {
        var text = content ?? "";

        if (tags.Contains("goal", StringComparer.OrdinalIgnoreCase) ||
            ContainsAny(text, "目标", "任务", "委托", "计划", "寻找", "追踪", "完成", "失败"))
        {
            return 0.7f;
        }

        return 0.2f;
    }

    public static float InferMysteryWeight(string? content, IReadOnlyCollection<string> tags)
    {
        var text = content ?? "";

        if (tags.Contains("mystery", StringComparer.OrdinalIgnoreCase) ||
            ContainsAny(text, "秘密", "谜", "真相", "线索", "异常", "失踪", "诅咒"))
        {
            return 0.7f;
        }

        return 0.2f;
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
