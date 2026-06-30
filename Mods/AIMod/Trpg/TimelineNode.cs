using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AIMod.Trpg;

public enum TimelineLayer { L0, L1, L2, L3 }
public enum TimelineNodeStatus { Visible, Archived }

/// <summary>
/// 分层时间轴节点
/// L0 = 篇章级   ≤10
/// L1 = 场景级   ≤15
/// L2 = 叙事行动 ≤5/L1
/// L3 = 具体动作 ≤5/L2
/// </summary>
public class TimelineNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public TimelineLayer Layer { get; set; }
    public string Content { get; set; } = "";
    public string? ParentId { get; set; }
    public string SceneId { get; set; } = "";
    public TimelineNodeStatus Status { get; set; } = TimelineNodeStatus.Visible;
    public int Importance { get; set; } = 5;
    public bool Foreshadowing { get; set; } = false;
    public int EventSequence { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// InfoExtractor 解析出的单条时间轴事件
/// </summary>
public class TimelineEventExtraction
{
    public TimelineLayer Layer { get; set; }
    public string Content { get; set; } = "";
    public string ParentKeywords { get; set; } = "";
    public int Importance { get; set; } = 5;
    public bool Foreshadowing { get; set; } = false;

    private static readonly Regex LinePattern = new(
        @"^\[(?<layer>L[0-3])\]\s*(?<content>.+?)\s*\|\|\s*(?<keywords>[^|]*?)\s*\|\|\s*importance:(?<imp>\d+)\s*\|\|\s*foreshadowing:(?<fore>true|false)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 解析单行输出 "[L2] content || keywords || importance:5 || foreshadowing:true"
    /// </summary>
    public static bool TryParse(string line, out TimelineEventExtraction result)
    {
        result = new TimelineEventExtraction();
        var m = LinePattern.Match(line.Trim());
        if (!m.Success) return false;

        result.Layer = m.Groups["layer"].Value.ToUpperInvariant() switch
        {
            "L0" => TimelineLayer.L0,
            "L1" => TimelineLayer.L1,
            "L2" => TimelineLayer.L2,
            _    => TimelineLayer.L3
        };
        result.Content = m.Groups["content"].Value.Trim();
        result.ParentKeywords = m.Groups["keywords"].Value.Trim();
        result.Importance = int.TryParse(m.Groups["imp"].Value, out var imp) ? Math.Clamp(imp, 1, 10) : 5;
        result.Foreshadowing = m.Groups["fore"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(result.Content);
    }

    /// <summary>
    /// 批量解析多行
    /// </summary>
    public static List<TimelineEventExtraction> ParseAll(string response)
    {
        var results = new List<TimelineEventExtraction>();
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParse(line, out var extraction))
                results.Add(extraction);
        }
        return results;
    }
}
