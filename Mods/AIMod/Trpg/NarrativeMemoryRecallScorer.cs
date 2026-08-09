using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIMod.Trpg;

internal sealed record NarrativeRetrievalScore(
    NarrativeMemoryNode Node,
    float BaseScore,
    float EntityScore,
    float TagScore,
    float TokenScore,
    float EmbeddingScore,
    float QueryRelevanceScore,
    float UnresolvedBonus,
    float RecencyScore,
    float FinalScore,
    bool IsEligible,
    List<string> MatchedReasons);

internal static class NarrativeMemoryRecallScorer
{
    private const float MinQueryRelevance = 0.25f;

    public static List<NarrativeRetrievalScore> SelectTopScores(
        IEnumerable<NarrativeRetrievalScore> scored,
        int take)
    {
        return scored
            .Where(x => x.IsEligible)
            .OrderByDescending(x => x.FinalScore)
            .ThenByDescending(x => x.QueryRelevanceScore)
            .ThenByDescending(x => x.Node.Timestamp)
            .GroupBy(x => GetDedupKey(x.Node))
            .Select(g => g.First())
            .Take(take)
            .ToList();
    }

    public static NarrativeRetrievalScore ScoreNarrativeNode(
        NarrativeMemoryNode node,
        IReadOnlyCollection<string> queryTokens,
        IReadOnlyCollection<string> presentEntities,
        int currentFoldCount)
    {
        var reasons = new List<string>();
        var baseScore = Math.Clamp(node.CalculateNarrativeScore(currentFoldCount), 0f, 1f);

        var normalizedPresentEntities = presentEntities
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeText)
            .Where(x => x.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedTokens = queryTokens
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeText)
            .Where(x => x.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entityScore = 0f;
        foreach (var entity in node.InvolvedEntities ?? new List<string>())
        {
            var normalizedEntity = NormalizeText(entity);
            if (normalizedEntity.Length < 2)
                continue;

            if (normalizedPresentEntities.Contains(normalizedEntity))
            {
                entityScore += 0.45f;
                reasons.Add($"entity:{entity}");
            }
            else if (normalizedTokens.Any(t => IsLooseMatch(normalizedEntity, t)))
            {
                entityScore += 0.35f;
                reasons.Add($"query-entity:{entity}");
            }
        }

        entityScore = Math.Min(entityScore, 0.9f);

        var tagScore = 0f;
        foreach (var tag in node.ArcTags ?? new List<string>())
        {
            if (normalizedTokens.Any(t => IsLooseMatch(tag, t)))
            {
                tagScore += 0.25f;
                reasons.Add($"tag:{tag}");
            }
        }

        tagScore = Math.Min(tagScore, 0.5f);

        var tokenScore = 0f;
        var summary = NormalizeText(node.Summary);
        foreach (var token in normalizedTokens)
        {
            if (token.Length < 2)
                continue;

            if (summary.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                tokenScore += token.Length >= 3 ? 0.25f : 0.18f;
                reasons.Add($"summary-token:{token}");
            }
        }

        tokenScore = Math.Min(tokenScore, 0.8f);
        var embeddingScore = 0f;
        var queryRelevanceScore = Math.Max(Math.Max(tokenScore, entityScore), Math.Max(tagScore, embeddingScore));
        var unresolvedBonus = node.IsResolved ? 0f : 1f;
        var recencyScore = CalculateRecencyScore(node.Timestamp);
        var isEligible = queryRelevanceScore >= MinQueryRelevance
            || entityScore >= 0.35f
            || tagScore >= 0.25f
            || tokenScore >= 0.25f
            || embeddingScore >= 0.35f
            || reasons.Count > 0;

        var finalScore = (0.30f * queryRelevanceScore)
            + (0.22f * entityScore)
            + (0.18f * tokenScore)
            + (0.12f * tagScore)
            + (0.10f * baseScore)
            + (0.05f * unresolvedBonus)
            + (0.03f * recencyScore);

        return new NarrativeRetrievalScore(
            node,
            baseScore,
            entityScore,
            tagScore,
            tokenScore,
            embeddingScore,
            queryRelevanceScore,
            unresolvedBonus,
            recencyScore,
            finalScore,
            isEligible,
            reasons);
    }

    public static bool IsLooseMatch(string a, string b)
    {
        a = NormalizeText(a);
        b = NormalizeText(b);
        if (a.Length < 2 || b.Length < 2)
            return false;

        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;

        return a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static float CalculateRecencyScore(DateTime timestamp)
    {
        if (timestamp == default)
            return 0f;

        var ageDays = Math.Max(0, (DateTime.UtcNow - timestamp.ToUniversalTime()).TotalDays);
        return (float)Math.Clamp(1.0 / (1.0 + ageDays / 14.0), 0.0, 1.0);
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var chars = normalized
            .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
            .ToArray();
        return new string(chars);
    }

    private static string GetDedupKey(NarrativeMemoryNode node)
    {
        return node.SourceEventId > 0 ? $"event:{node.SourceEventId}" : $"node:{node.Id}";
    }
}
