using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIMod.Trpg.SemanticGraph;

public sealed class SemanticGraphWriter
{
    private readonly SemanticGraphRepository _repository;

    public SemanticGraphWriter(SemanticGraphRepository repository)
    {
        _repository = repository;
    }

    public async Task<SemanticGraphWriteResult> WriteCandidatesAsync(
        TrpgScope scope,
        string characterId,
        IReadOnlyList<GraphMemoryCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var result = new SemanticGraphWriteResult();
        var killFloor = await _repository.GetKillFloorAsync(scope);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var summary = (candidate.Summary ?? "").Trim();
            var assigned = Math.Clamp(candidate.AssignedImportance, 0, 100);
            if (string.IsNullOrWhiteSpace(summary) || assigned <= 0)
                continue;

            var contentHash = BuildContentHash(scope, characterId, candidate);
            var existingId = await _repository.FindMemoryNodeIdByHashAsync(scope, characterId, contentHash);

            var memoryNode = new SemanticGraphNode
            {
                CharacterId = characterId,
                NodeKind = SemanticGraphNodeKind.Memory,
                Text = BuildMemoryTitle(summary),
                Summary = summary,
                AssignedImportance = assigned,
                Importance = killFloor + assigned,
                SourceScope = "GraphFold",
                SourceMessageIds = JsonSerializer.Serialize(NormalizeTokens(candidate.SourceMessageIds)),
                RawExcerpt = JsonSerializer.Serialize(BuildRawExcerptList(candidate.RawExcerpt)),
                ContentHash = contentHash,
                Metadata = JsonSerializer.Serialize(new { stance = candidate.Stance ?? "" }),
                CreatedAt = DateTime.UtcNow,
                LastActivatedAt = DateTime.UtcNow,
                ActivationCount = 1
            };

            var memoryId = await _repository.UpsertNodeAsync(scope, memoryNode);
            if (existingId > 0)
                result.ReusedMemoryCount++;
            else
                result.InsertedMemoryCount++;

            var tokenStats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            result.SurfaceNodeCount += await UpsertSurfaceEdgesAsync(
                scope,
                characterId,
                memoryId,
                candidate.NameTokens,
                SemanticGraphNodeKind.Name,
                SemanticGraphEdgeKind.Mentions,
                0.90,
                summary,
                tokenStats,
                includeSpeakerEdge: true,
                result);

            result.SurfaceNodeCount += await UpsertSurfaceEdgesAsync(
                scope,
                characterId,
                memoryId,
                candidate.SurfaceTokens,
                SemanticGraphNodeKind.Token,
                SemanticGraphEdgeKind.Mentions,
                0.80,
                summary,
                tokenStats,
                includeSpeakerEdge: false,
                result);

            result.SurfaceNodeCount += await UpsertSurfaceEdgesAsync(
                scope,
                characterId,
                memoryId,
                candidate.TopicTokens,
                SemanticGraphNodeKind.Topic,
                SemanticGraphEdgeKind.About,
                0.95,
                summary,
                tokenStats,
                includeSpeakerEdge: false,
                result);

            result.SurfaceNodeCount += await UpsertSurfaceEdgesAsync(
                scope,
                characterId,
                memoryId,
                candidate.SceneTokens,
                SemanticGraphNodeKind.Scene,
                SemanticGraphEdgeKind.InScene,
                0.60,
                summary,
                tokenStats,
                includeSpeakerEdge: false,
                result);

            if (existingId == 0 && tokenStats.Count > 0)
                await _repository.IncrementTokenStatsAsync(scope, tokenStats);
        }

        return result;
    }

    // Legacy bridge retained only for migration/debug compatibility.
    public async Task WriteLegacyMemoryNodesAsync(
        TrpgScope scope,
        string characterId,
        IEnumerable<MemoryNode> memoryNodes,
        string sourceScope = "LegacyMemoryNodeBridge")
    {
        var killFloor = await _repository.GetKillFloorAsync(scope);
        foreach (var memory in memoryNodes)
        {
            if (string.IsNullOrWhiteSpace(memory.Summary))
                continue;

            var assigned = NormalizeAssignedImportance(memory.Importance);
            var memoryId = await _repository.UpsertNodeAsync(scope, new SemanticGraphNode
            {
                CharacterId = characterId ?? "",
                NodeKind = SemanticGraphNodeKind.Memory,
                Text = BuildMemoryTitle(memory.Summary),
                Summary = memory.Summary.Trim(),
                AssignedImportance = assigned,
                Importance = killFloor + assigned,
                SourceScope = sourceScope,
                SourceMessageIds = string.IsNullOrWhiteSpace(memory.SourceMessageIds) ? "[]" : memory.SourceMessageIds,
                RawExcerpt = string.IsNullOrWhiteSpace(memory.RawExcerpt) ? "[]" : memory.RawExcerpt,
                ContentHash = BuildLegacyContentHash(scope, characterId, memory),
                Metadata = JsonSerializer.Serialize(new
                {
                    legacyMemoryNodeId = memory.Id,
                    memory.NodeType,
                    memory.MemoryAudience,
                    memory.Confidence
                }),
                CreatedAt = DateTime.UtcNow
            });

            var surfaceTokens = ExtractLegacyTokens(memory).Take(8).ToList();
            var surfaceIds = new List<long>();
            foreach (var token in surfaceTokens.Take(5))
            {
                var kind = GuessLegacySurfaceKind(token);
                var tokenId = await _repository.UpsertSurfaceNodeAsync(scope, kind, token, characterId ?? "");
                if (tokenId <= 0)
                    continue;
                surfaceIds.Add(tokenId);
                var edgeKind = kind == SemanticGraphNodeKind.Topic ? SemanticGraphEdgeKind.About : SemanticGraphEdgeKind.Mentions;
                await _repository.UpsertEdgeAsync(scope, memoryId, tokenId, edgeKind, 0.8, memory.RawExcerpt, characterId ?? "");
            }

            for (var i = 0; i < surfaceIds.Count; i++)
            {
                for (var j = i + 1; j < surfaceIds.Count; j++)
                {
                    await _repository.UpsertEdgeAsync(scope, surfaceIds[i], surfaceIds[j], SemanticGraphEdgeKind.CoOccurs, 0.35, memory.Summary, characterId ?? "");
                    await _repository.UpsertEdgeAsync(scope, surfaceIds[j], surfaceIds[i], SemanticGraphEdgeKind.CoOccurs, 0.35, memory.Summary, characterId ?? "");
                }
            }

            await _repository.IncrementTokenStatsAsync(scope, surfaceTokens);
        }
    }

    private async Task<int> UpsertSurfaceEdgesAsync(
        TrpgScope scope,
        string characterId,
        long memoryId,
        IEnumerable<string> rawTokens,
        string nodeKind,
        string edgeKind,
        double edgeWeight,
        string evidence,
        HashSet<string> tokenStats,
        bool includeSpeakerEdge,
        SemanticGraphWriteResult result)
    {
        var surfaceIds = new List<long>();
        foreach (var token in NormalizeTokens(rawTokens))
        {
            var surfaceId = await _repository.UpsertSurfaceNodeAsync(scope, nodeKind, token, characterId);
            if (surfaceId <= 0)
                continue;

            surfaceIds.Add(surfaceId);
            tokenStats.Add(token);
            await _repository.UpsertEdgeAsync(scope, memoryId, surfaceId, edgeKind, edgeWeight, evidence, characterId);
            result.EdgeUpsertCount++;

            if (includeSpeakerEdge)
            {
                await _repository.UpsertEdgeAsync(scope, memoryId, surfaceId, SemanticGraphEdgeKind.Speaker, 0.70, evidence, characterId);
                result.EdgeUpsertCount++;
            }
        }

        for (var i = 0; i < surfaceIds.Count; i++)
        {
            for (var j = i + 1; j < surfaceIds.Count; j++)
            {
                await _repository.UpsertEdgeAsync(scope, surfaceIds[i], surfaceIds[j], SemanticGraphEdgeKind.CoOccurs, 0.25, evidence, characterId);
                await _repository.UpsertEdgeAsync(scope, surfaceIds[j], surfaceIds[i], SemanticGraphEdgeKind.CoOccurs, 0.25, evidence, characterId);
                result.EdgeUpsertCount += 2;
            }
        }

        return surfaceIds.Count;
    }

    private static string BuildContentHash(TrpgScope scope, string characterId, GraphMemoryCandidate candidate)
    {
        var payload = string.Join("\n", new[]
        {
            scope.WorldId ?? "",
            scope.GroupId.ToString(),
            characterId ?? "",
            SemanticGraphNodeKind.Memory,
            NormalizeSummary(candidate.Summary),
            JsonSerializer.Serialize(NormalizeTokens(candidate.SourceMessageIds)),
            JsonSerializer.Serialize(BuildRawExcerptList(candidate.RawExcerpt))
        });

        return HashString(payload);
    }

    private static string BuildLegacyContentHash(TrpgScope scope, string characterId, MemoryNode memory)
    {
        var payload = string.Join("\n", new[]
        {
            scope.WorldId ?? "",
            scope.GroupId.ToString(),
            characterId ?? "",
            SemanticGraphNodeKind.Memory,
            NormalizeSummary(memory.Summary),
            memory.SourceMessageIds ?? "[]",
            memory.RawExcerpt ?? "[]"
        });

        return HashString(payload);
    }

    private static string HashString(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes);
    }

    private static List<string> NormalizeTokens(IEnumerable<string> tokens)
        => tokens
            .Select(token => (token ?? "").Trim())
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> BuildRawExcerptList(string rawExcerpt)
    {
        var value = (rawExcerpt ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();
        return new List<string> { value };
    }

    private static string NormalizeSummary(string summary)
        => string.Join(" ", (summary ?? "").Trim().Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));

    private static IEnumerable<string> ExtractLegacyTokens(MemoryNode memory)
    {
        var keywordTokens = (memory.Keywords ?? "")
            .Split(new[] { ',', ';', '，', '；', '|', '/', '。', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length >= 2);
        return keywordTokens
            .Concat(SemanticGraphRecallService.ExtractTerms(memory.Summary))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string GuessLegacySurfaceKind(string token)
    {
        if (token.EndsWith("传闻", StringComparison.OrdinalIgnoreCase)
            || token.EndsWith("线索", StringComparison.OrdinalIgnoreCase)
            || token.EndsWith("谜团", StringComparison.OrdinalIgnoreCase))
            return SemanticGraphNodeKind.Topic;
        return SemanticGraphNodeKind.Token;
    }

    private static double NormalizeAssignedImportance(double importance)
    {
        if (importance <= 1.0)
            return Math.Clamp(importance * 100.0, 10, 100);
        return Math.Clamp(importance, 10, 100);
    }

    private static string BuildMemoryTitle(string summary)
    {
        var title = (summary ?? "").Trim().Replace('\n', ' ');
        return title.Length <= 48 ? title : title[..48];
    }
}
