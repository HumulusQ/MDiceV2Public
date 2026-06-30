namespace AIMod.Trpg.SemanticGraph;

public sealed class SemanticGraphRepository
{
    private readonly ChatDatabase _db;

    public SemanticGraphRepository(ChatDatabase db)
    {
        _db = db;
    }

    public Task<long> UpsertNodeAsync(TrpgScope scope, SemanticGraphNode node)
        => _db.UpsertSemanticGraphNodeAsync(scope, node);

    public Task<long> UpsertSurfaceNodeAsync(TrpgScope scope, string nodeKind, string text, string characterId = "")
        => _db.UpsertSemanticSurfaceNodeAsync(scope, nodeKind, text, characterId);

    public Task<long> FindMemoryNodeIdByHashAsync(TrpgScope scope, string characterId, string contentHash)
        => _db.FindSemanticMemoryNodeIdByHashAsync(scope, characterId, contentHash);

    public Task UpsertEdgeAsync(TrpgScope scope, long sourceId, long targetId, string edgeKind, double weight, string evidence, string characterId = "")
        => _db.UpsertSemanticGraphEdgeAsync(scope, sourceId, targetId, edgeKind, weight, evidence, characterId);

    public Task<List<SemanticGraphNode>> FindSurfaceNodesAsync(TrpgScope scope, IEnumerable<string> texts, IEnumerable<string> kinds, string characterId = "")
        => _db.FindSemanticSurfaceNodesAsync(scope, texts, kinds, characterId);

    public Task<List<SemanticGraphNode>> SearchMemoryNodesAsync(TrpgScope scope, IEnumerable<string> terms, string characterId, int limit)
        => _db.SearchSemanticMemoryNodesAsync(scope, terms, characterId, limit);

    public Task<List<SemanticGraphNode>> GetNodesByIdsAsync(TrpgScope scope, IEnumerable<long> ids)
        => _db.GetSemanticNodesByIdsAsync(scope, ids);

    public Task<List<SemanticGraphEdge>> GetOutgoingEdgesAsync(TrpgScope scope, long sourceNodeId, string characterId, int limit)
        => _db.GetSemanticOutgoingEdgesAsync(scope, sourceNodeId, characterId, limit);

    public Task<List<SemanticGraphEdge>> GetIncomingEdgesAsync(TrpgScope scope, long targetNodeId, string characterId, int limit)
        => _db.GetSemanticIncomingEdgesAsync(scope, targetNodeId, characterId, limit);

    public Task<Dictionary<string, int>> GetTokenNodeCountsAsync(TrpgScope scope, IEnumerable<string> tokens)
        => _db.GetSemanticTokenNodeCountsAsync(scope, tokens);

    public Task IncrementTokenStatsAsync(TrpgScope scope, IEnumerable<string> tokens)
        => _db.IncrementSemanticTokenStatsAsync(scope, tokens);

    public Task ReplaceTokenStatsAsync(TrpgScope scope, IReadOnlyDictionary<string, int> tokenCounts)
        => _db.ReplaceSemanticTokenStatsAsync(scope, tokenCounts);

    public Task<double> GetKillFloorAsync(TrpgScope scope)
        => _db.GetSemanticKillFloorAsync(scope);

    public Task SetKillFloorAsync(TrpgScope scope, double value)
        => _db.SetSemanticKillFloorAsync(scope, value);

    public Task<int> PruneBelowKillFloorAsync(TrpgScope scope)
        => _db.PruneSemanticGraphBelowKillFloorAsync(scope);

    public Task<int> DeleteEdgesAttachedToDeletedNodesAsync(TrpgScope scope)
        => _db.DeleteSemanticEdgesAttachedToDeletedNodesAsync(scope);

    public Task<int> DeleteOrphanSurfaceNodesAsync(TrpgScope scope)
        => _db.DeleteSemanticOrphanSurfaceNodesAsync(scope);

    public Task RebuildTokenStatsAsync(TrpgScope scope)
        => _db.RebuildSemanticTokenStatsAsync(scope);
}

public sealed class CharacterInnerStateStore
{
    private readonly ChatDatabase _db;

    public CharacterInnerStateStore(ChatDatabase db)
    {
        _db = db;
    }

    public Task<CharacterInnerState> GetAsync(TrpgScope scope, string characterId)
        => _db.GetCharacterInnerStateAsync(scope, characterId);

    public Task SaveAsync(TrpgScope scope, string characterId, string thoughtText, string emotionText)
        => _db.UpsertCharacterInnerStateAsync(scope, characterId, thoughtText, emotionText);
}
