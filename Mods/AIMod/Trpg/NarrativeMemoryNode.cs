using System;
using System.Collections.Generic;

namespace AIMod.Trpg;

/// <summary>
/// Cognitive narrative memory node used for long-term story recall.
/// </summary>
public class NarrativeMemoryNode
{
    public long Id { get; set; }
    public string WorldId { get; set; } = "";
    public string Summary { get; set; } = "";
    public float NarrativeWeight { get; set; } = 0.5f;
    public float EmotionalWeight { get; set; } = 0f;
    public float RelationshipImpact { get; set; } = 0f;
    public float GoalImpact { get; set; } = 0f;
    public float MysteryWeight { get; set; } = 0f;
    public bool IsResolved { get; set; } = false;
    public List<string> InvolvedEntities { get; set; } = new();
    public List<string> ArcTags { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int CreatedFoldCount { get; set; } = 0;
    public long SourceEventId { get; set; }

    public float CalculateNarrativeScore(int currentFoldCount)
    {
        var foldsSince = Math.Max(0, currentFoldCount - CreatedFoldCount);

        var narrativeScore = Clamp01(NarrativeWeight);
        var emotionalScore = Math.Abs(ClampSigned(EmotionalWeight));
        var relationshipScore = Clamp01(RelationshipImpact);
        var goalScore = Clamp01(GoalImpact);
        var mysteryScore = Clamp01(MysteryWeight);
        var recency = CalculateRecencyScore(foldsSince, narrativeScore, relationshipScore, goalScore, mysteryScore);
        var unresolvedBonus = IsResolved ? 0f : 0.15f;

        return narrativeScore * 0.30f +
               emotionalScore * 0.18f +
               relationshipScore * 0.18f +
               goalScore * 0.14f +
               mysteryScore * 0.10f +
               recency * 0.10f +
               unresolvedBonus;
    }

    public float CalculateNarrativeScore(DateTime currentTime)
    {
        return CalculateNarrativeScore(CreatedFoldCount);
    }

    private static float CalculateRecencyScore(
        int foldsSince,
        float narrativeScore,
        float relationshipScore,
        float goalScore,
        float mysteryScore)
    {
        var importance = Math.Max(
            narrativeScore,
            Math.Max(relationshipScore, Math.Max(goalScore, mysteryScore)));

        var halfLifeFolds = importance >= 0.75f ? 40.0 * 90 :
                            importance >= 0.50f ? 40.0 * 45 :
                            importance >= 0.30f ? 40.0 * 21 :
                                                   40.0 * 7;

        return (float)Math.Exp(-Math.Log(2) * foldsSince / halfLifeFolds);
    }

    private static float Clamp01(float value)
    {
        if (float.IsNaN(value)) return 0f;
        return Math.Clamp(value, 0f, 1f);
    }

    private static float ClampSigned(float value)
    {
        if (float.IsNaN(value)) return 0f;
        return Math.Clamp(value, -1f, 1f);
    }
}
