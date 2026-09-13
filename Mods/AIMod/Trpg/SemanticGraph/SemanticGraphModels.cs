using System.Text;

namespace AIMod.Trpg.SemanticGraph;

public static class SemanticGraphNodeKind
{
    public const string Memory = "memory";
    public const string Token = "token";
    public const string Name = "name";
    public const string Topic = "topic";
    public const string Scene = "scene";
    public const string EntityAnchor = "entity_anchor";
}

public static class SemanticGraphEdgeKind
{
    public const string Mentions = "MENTIONS";
    public const string About = "ABOUT";
    public const string InScene = "IN_SCENE";
    public const string CoOccurs = "CO_OCCURS";
    public const string Speaker = "SPEAKER";
    public const string AliasHint = "ALIAS_HINT";
    public const string SameScene = "SAME_SCENE";
}

public sealed class SemanticGraphNode
{
    public long Id { get; set; }
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string NodeKind { get; set; } = SemanticGraphNodeKind.Memory;
    public string Text { get; set; } = "";
    public string Summary { get; set; } = "";
    public double Importance { get; set; }
    public double AssignedImportance { get; set; }
    public string SourceScope { get; set; } = "";
    public string SourceMessageIds { get; set; } = "[]";
    public string RawExcerpt { get; set; } = "[]";
    public string ContentHash { get; set; } = "";
    public string Metadata { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastActivatedAt { get; set; }
    public int ActivationCount { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class SemanticGraphEdge
{
    public long Id { get; set; }
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public long SourceNodeId { get; set; }
    public long TargetNodeId { get; set; }
    public string EdgeKind { get; set; } = SemanticGraphEdgeKind.Mentions;
    public double Weight { get; set; } = 1.0;
    public string Evidence { get; set; } = "";
    public string SourceMessageIds { get; set; } = "[]";
    public string Metadata { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastReinforcedAt { get; set; }
    public int ReinforceCount { get; set; }
}

public sealed class CharacterInnerState
{
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string ThoughtText { get; set; } = "";
    public string EmotionText { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static CharacterInnerState Empty(TrpgScope scope, string characterId) => new()
    {
        WorldId = scope.WorldId,
        GroupId = scope.GroupId,
        CharacterId = characterId,
        ThoughtText = "无",
        EmotionText = "无"
    };
}

public sealed class GraphMemoryCandidate
{
    public string Summary { get; set; } = "";
    public List<string> SurfaceTokens { get; set; } = new();
    public List<string> NameTokens { get; set; } = new();
    public List<string> TopicTokens { get; set; } = new();
    public List<string> SceneTokens { get; set; } = new();
    public int AssignedImportance { get; set; }
    public List<string> SourceMessageIds { get; set; } = new();
    public string RawExcerpt { get; set; } = "";
    public string Stance { get; set; } = "";
}

public sealed class GraphMemoryFoldResult
{
    public bool ParseFailed { get; set; }
    public List<GraphMemoryCandidate> MemoryCandidates { get; set; } = new();
    public string RawResponse { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class SemanticGraphWriteResult
{
    public int InsertedMemoryCount { get; set; }
    public int ReusedMemoryCount { get; set; }
    public int SurfaceNodeCount { get; set; }
    public int EdgeUpsertCount { get; set; }
}

public sealed class GraphRecallHit
{
    public SemanticGraphNode MemoryNode { get; set; } = new();
    public double Score { get; set; }
    public List<string> Paths { get; set; } = new();
    public bool HasWeakAssociation { get; set; }
}

public sealed class GraphRecallResult
{
    public List<GraphRecallHit> Hits { get; set; } = new();

    public string ToPromptString(int maxHits = 8)
    {
        if (Hits.Count == 0)
            return "无";

        var sb = new StringBuilder();
        foreach (var hit in Hits.Take(maxHits))
        {
            var summary = string.IsNullOrWhiteSpace(hit.MemoryNode.Summary)
                ? hit.MemoryNode.Text
                : hit.MemoryNode.Summary;
            if (summary.Length > 120)
                summary = summary[..120] + "...";

            sb.AppendLine($"- {summary}");
            if (hit.Paths.Count > 0)
                sb.AppendLine($"  命中路径: {string.Join("; ", hit.Paths.Take(2))}");
            sb.AppendLine($"  重要性: {hit.MemoryNode.Importance:F0}; 匹配度: {hit.Score:F2}");
            if (hit.HasWeakAssociation)
                sb.AppendLine("  注意: 弱联想路径不代表身份已被确认。");
        }

        return sb.ToString().TrimEnd();
    }
}
