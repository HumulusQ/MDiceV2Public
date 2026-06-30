using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIMod.Trpg;

public static class TimelineContentCleaner
{
    private static readonly Regex LeadingMarkerRegex = new(
        @"^(?:(?:[-*•]\s+)|(?:\d+\s*[\.、]\s*)|(?:L[0-3]\s*:\s*))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InlineLayerRegex = new(
        @"^(?:L[0-3]\s*:\s*)+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled);

    public static string Clean(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var cleaned = content.Trim();
        var previous = string.Empty;
        while (!string.Equals(previous, cleaned, StringComparison.Ordinal))
        {
            previous = cleaned;
            cleaned = LeadingMarkerRegex.Replace(cleaned, "").Trim();
            cleaned = InlineLayerRegex.Replace(cleaned, "").Trim();
        }

        cleaned = WhitespaceRegex.Replace(cleaned, " ").Trim();
        return cleaned;
    }

    public static string NormalizeForComparison(string? content)
    {
        var cleaned = Clean(content);
        if (string.IsNullOrWhiteSpace(cleaned))
            return "";

        return new string(cleaned
            .Where(ch => !char.IsWhiteSpace(ch)
                         && !char.IsPunctuation(ch)
                         && ch != '【'
                         && ch != '】')
            .ToArray())
            .ToLowerInvariant();
    }

    public static bool AreNearDuplicates(string? left, string? right)
    {
        var normalizedLeft = NormalizeForComparison(left);
        var normalizedRight = NormalizeForComparison(right);
        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            return false;

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
               || normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase)
               || normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase);
    }
}
