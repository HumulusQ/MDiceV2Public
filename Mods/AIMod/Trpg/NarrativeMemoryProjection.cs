using System;
using System.Collections.Generic;
using System.Linq;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Projects narrative memory nodes into the current cognitive memory view.
/// </summary>
public class NarrativeMemoryProjection
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;

    public NarrativeMemoryProjection(IModContext context, ChatDatabase db)
    {
        _context = context;
        _db = db;
    }

    public async Task<List<NarrativeMemoryNode>> GetHighValueMemoriesAsync(
        TrpgScope scope,
        string characterId,
        int maxCount = 20)
    {
        var allMemories = await _db.QueryNarrativeMemoryNodesAsync(scope, characterId);
        var currentFoldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);

        return allMemories
            .OrderByDescending(node => node.CalculateNarrativeScore(currentFoldCount))
            .Take(maxCount)
            .ToList();
    }

    public async Task<List<NarrativeMemoryNode>> GetEntityMemoriesAsync(
        TrpgScope scope,
        string characterId,
        string entityId)
    {
        var allMemories = await _db.QueryNarrativeMemoryNodesAsync(scope, characterId);
        var currentFoldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);

        return allMemories
            .Where(node => node.InvolvedEntities.Contains(entityId, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(node => node.CalculateNarrativeScore(currentFoldCount))
            .ToList();
    }

    public async Task<List<NarrativeMemoryNode>> GetArcMemoriesAsync(
        TrpgScope scope,
        string characterId,
        string arcTag)
    {
        var allMemories = await _db.QueryNarrativeMemoryNodesAsync(scope, characterId);
        var currentFoldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);

        return allMemories
            .Where(node => node.ArcTags.Contains(arcTag, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(node => node.CalculateNarrativeScore(currentFoldCount))
            .ToList();
    }

    public async Task<List<NarrativeMemoryNode>> GetUnresolvedMemoriesAsync(
        TrpgScope scope,
        string characterId)
    {
        var allMemories = await _db.QueryNarrativeMemoryNodesAsync(scope, characterId);
        var currentFoldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);

        return allMemories
            .Where(node => !node.IsResolved)
            .OrderByDescending(node => node.CalculateNarrativeScore(currentFoldCount))
            .ToList();
    }

    public string GenerateNarrativeMemorySummary(List<NarrativeMemoryNode> memories)
    {
        if (memories.Count == 0)
            return "无";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【重要记忆】");
        sb.AppendLine("========================");

        foreach (var memory in memories)
            sb.AppendLine($"- {memory.Summary}");

        return sb.ToString();
    }
}
