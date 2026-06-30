namespace AIMod.Trpg.SemanticGraph;

public sealed class GraphPruner
{
    private readonly SemanticGraphRepository _repository;

    public GraphPruner(SemanticGraphRepository repository)
    {
        _repository = repository;
    }

    public async Task AdvanceKillFloorAsync(TrpgScope scope)
    {
        var floor = await _repository.GetKillFloorAsync(scope);
        await _repository.SetKillFloorAsync(scope, floor + 0.03);
    }

    public async Task PruneAsync(TrpgScope scope)
    {
        await _repository.PruneBelowKillFloorAsync(scope);
        await _repository.DeleteEdgesAttachedToDeletedNodesAsync(scope);
        await _repository.DeleteOrphanSurfaceNodesAsync(scope);
        await _repository.RebuildTokenStatsAsync(scope);
    }
}
