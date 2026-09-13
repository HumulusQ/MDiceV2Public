using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AIMod.Trpg;

public static class AiOutputEchoGuard
{
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<long, List<RecentAiOutput>> RecentOutputs = new();

    public static void Mark(long groupId, string content, string sourceCharacterId, string sourceDisplayName)
    {
        var normalized = Normalize(content);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var list = RecentOutputs.GetOrAdd(groupId, _ => new List<RecentAiOutput>());
        lock (list)
        {
            CleanupLocked(list);
            list.Add(new RecentAiOutput
            {
                Content = normalized,
                SourceCharacterId = sourceCharacterId,
                SourceDisplayName = sourceDisplayName,
                CreatedAtUtc = DateTime.UtcNow
            });
        }
    }

    public static RecentAiOutput? FindRecent(long groupId, string content)
    {
        var normalized = Normalize(content);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (!RecentOutputs.TryGetValue(groupId, out var list))
            return null;

        lock (list)
        {
            CleanupLocked(list);
            return list
                .LastOrDefault(x => string.Equals(x.Content, normalized, StringComparison.Ordinal));
        }
    }

    private static void CleanupLocked(List<RecentAiOutput> list)
    {
        var cutoff = DateTime.UtcNow - EchoWindow;
        list.RemoveAll(x => x.CreatedAtUtc < cutoff);
    }

    private static string Normalize(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var lines = content
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0);

        return string.Join("\n", lines);
    }
}

public sealed class RecentAiOutput
{
    public string Content { get; set; } = "";
    public string SourceCharacterId { get; set; } = "";
    public string SourceDisplayName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}
