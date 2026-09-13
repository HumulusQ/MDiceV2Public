using System.Collections.Generic;
using System.Text.RegularExpressions;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg.SemanticGraph;

public sealed class SemanticGraphRecallService
{
    private static readonly Regex TermRegex = new(@"[\p{IsCJKUnifiedIdeographs}]{2,12}|[A-Za-z0-9_\-]{3,}", RegexOptions.Compiled);
    private const int MaxQueryTokens = 12;
    private const int MaxExpansionDepth = 2;
    private const int MaxExploredNodes = 200;

    private readonly SemanticGraphRepository _repository;
    private readonly IModContext _context;

    public SemanticGraphRecallService(SemanticGraphRepository repository, IModContext context)
    {
        _repository = repository;
        _context = context;
    }

    public async Task<GraphRecallResult> BuildEvidencePackAsync(
        TrpgScope scope,
        string characterId,
        string latestText,
        IReadOnlyList<ChatHistoryEntry> recentHistory,
        int maxResults = 8)
    {
        var queryText = BuildQueryText(latestText, recentHistory);
        var terms = ExtractTerms(queryText);
        if (terms.Count == 0)
            return new GraphRecallResult();

        try
        {
            var tokenCounts = await _repository.GetTokenNodeCountsAsync(scope, terms);
            var surfaceKinds = new[]
            {
                SemanticGraphNodeKind.Token,
                SemanticGraphNodeKind.Name,
                SemanticGraphNodeKind.Topic,
                SemanticGraphNodeKind.Scene,
                SemanticGraphNodeKind.EntityAnchor
            };

            var seeds = await _repository.FindSurfaceNodesAsync(scope, terms, surfaceKinds, characterId);
            var seedList = seeds
                .GroupBy(node => node.Id)
                .Select(group => group.First())
                .ToList();

            var nodeCache = seedList.ToDictionary(node => node.Id);
            var visitedBest = new Dictionary<long, double>();
            var frontier = new PriorityQueue<FrontierState, double>();
            var hitStates = new Dictionary<long, MemoryHitState>();

            foreach (var seed in seedList)
            {
                var rarity = RarityWeight(tokenCounts.TryGetValue(seed.Text, out var count) ? count : 0);
                var activation = BuildSeedActivation(seed, rarity, queryText);
                if (activation <= 0)
                    continue;

                visitedBest[seed.Id] = activation;
                frontier.Enqueue(
                    new FrontierState(seed.Id, activation, 0, seed.Text, seed.Text, false),
                    -activation);
            }

            var explored = 0;
            while (frontier.Count > 0 && explored < MaxExploredNodes)
            {
                var state = frontier.Dequeue();
                if (visitedBest.TryGetValue(state.NodeId, out var bestSeen) && state.Activation + 1e-6 < bestSeen)
                    continue;

                if (!nodeCache.TryGetValue(state.NodeId, out var currentNode))
                {
                    await HydrateNodesAsync(scope, nodeCache, new[] { state.NodeId });
                    if (!nodeCache.TryGetValue(state.NodeId, out currentNode))
                        continue;
                }

                explored++;
                if (currentNode.NodeKind == SemanticGraphNodeKind.Memory || state.Depth >= MaxExpansionDepth)
                    continue;

                var outgoing = await _repository.GetOutgoingEdgesAsync(scope, currentNode.Id, characterId, 32);
                var incoming = await _repository.GetIncomingEdgesAsync(scope, currentNode.Id, characterId, 32);
                var edges = outgoing
                    .Concat(incoming)
                    .GroupBy(edge => edge.Id)
                    .Select(group => group.First())
                    .ToList();

                if (edges.Count == 0)
                    continue;

                var nextIds = edges
                    .Select(edge => edge.SourceNodeId == currentNode.Id ? edge.TargetNodeId : edge.SourceNodeId)
                    .Where(id => id > 0 && id != currentNode.Id)
                    .Distinct()
                    .ToList();
                await HydrateNodesAsync(scope, nodeCache, nextIds);

                var degreePenalty = DegreePenalty(edges.Count);
                foreach (var edge in edges)
                {
                    var nextId = edge.SourceNodeId == currentNode.Id ? edge.TargetNodeId : edge.SourceNodeId;
                    if (!nodeCache.TryGetValue(nextId, out var nextNode))
                        continue;

                    var nextDepth = state.Depth + 1;
                    var edgeKindWeight = GetEdgeKindWeight(edge.EdgeKind, nextNode.NodeKind);
                    if (edgeKindWeight <= 0)
                        continue;

                    var nextActivation = state.Activation
                        * Math.Max(0.05, edge.Weight)
                        * edgeKindWeight
                        * degreePenalty
                        * DepthDecay(nextDepth);
                    if (nextActivation < 0.03)
                        continue;

                    var nextPath = ExtendPath(state.Path, nextNode.Text);
                    var isWeak = state.HasWeakAssociation || IsWeakExpansionEdge(edge.EdgeKind);

                    if (nextNode.NodeKind == SemanticGraphNodeKind.Memory)
                    {
                        RegisterMemoryHit(hitStates, nextNode, nextActivation, nextPath, state.SeedToken, isWeak);
                        continue;
                    }

                    if (nextDepth >= MaxExpansionDepth)
                        continue;

                    if (visitedBest.TryGetValue(nextId, out var seenActivation) && nextActivation <= seenActivation * 0.97)
                        continue;

                    visitedBest[nextId] = nextActivation;
                    frontier.Enqueue(
                        new FrontierState(nextId, nextActivation, nextDepth, nextPath, state.SeedToken, isWeak),
                        -nextActivation);
                }
            }

            if (hitStates.Count < maxResults)
            {
                var fallbackMemories = await _repository.SearchMemoryNodesAsync(scope, terms, characterId, maxResults * 2);
                foreach (var memory in fallbackMemories)
                {
                    var matchedTerms = terms
                        .Where(term => Contains(memory.Summary, term) || Contains(memory.Text, term))
                        .Take(3)
                        .ToList();
                    if (matchedTerms.Count == 0)
                        continue;

                    var fallbackScore = 0.12
                        + matchedTerms.Count * 0.08
                        + NormalizeImportance(memory.Importance) * 0.12;
                    RegisterMemoryHit(
                        hitStates,
                        memory,
                        fallbackScore,
                        string.Join("/", matchedTerms),
                        matchedTerms.First(),
                        false);
                }
            }

            var hits = hitStates.Values
                .OrderByDescending(state => state.BuildRankScore())
                .ThenByDescending(state => state.Memory.Importance)
                .Take(Math.Clamp(maxResults, 1, 8))
                .Select(state => new GraphRecallHit
                {
                    MemoryNode = state.Memory,
                    Score = Math.Clamp(state.BuildDisplayScore(), 0, 0.99),
                    Paths = state.Paths.Take(2).ToList(),
                    HasWeakAssociation = state.HasWeakAssociation
                })
                .ToList();

            return new GraphRecallResult { Hits = hits };
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Semantic graph recall failed: {ex.Message}");
            return new GraphRecallResult();
        }
    }

    public static List<string> ExtractTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return ExtractTermCandidates(text)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Term.Length)
            .Select(candidate => candidate.Term)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueryTokens)
            .ToList();
    }

    private static List<TermCandidate> ExtractTermCandidates(string text)
    {
        var candidates = new Dictionary<string, TermCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TermRegex.Matches(text))
        {
            var term = match.Value.Trim();
            if (term.Length < 2)
                continue;

            if (!candidates.TryGetValue(term, out var candidate))
            {
                candidate = new TermCandidate(term);
                candidates[term] = candidate;
            }

            candidate.OccurrenceCount++;
            candidate.Score += Math.Min(term.Length, 8) * 0.08;
            candidate.Score += ContainsCjk(term) ? 0.16 : 0.08;
            if (IsQuoted(text, term))
                candidate.Score += 0.18;
            if (char.IsUpper(term[0]))
                candidate.Score += 0.06;
        }

        foreach (var candidate in candidates.Values)
            candidate.Score += Math.Min(candidate.OccurrenceCount - 1, 3) * 0.09;

        return candidates.Values.ToList();
    }

    private async Task HydrateNodesAsync(
        TrpgScope scope,
        Dictionary<long, SemanticGraphNode> nodeCache,
        IReadOnlyList<long> nodeIds)
    {
        var missingIds = nodeIds
            .Where(id => id > 0 && !nodeCache.ContainsKey(id))
            .Distinct()
            .ToList();
        if (missingIds.Count == 0)
            return;

        var nodes = await _repository.GetNodesByIdsAsync(scope, missingIds);
        foreach (var node in nodes)
            nodeCache[node.Id] = node;
    }

    private static void RegisterMemoryHit(
        Dictionary<long, MemoryHitState> hitStates,
        SemanticGraphNode memory,
        double activation,
        string path,
        string seedToken,
        bool weakAssociation)
    {
        if (!hitStates.TryGetValue(memory.Id, out var state))
        {
            state = new MemoryHitState(memory);
            hitStates[memory.Id] = state;
        }

        state.Memory = memory;
        state.ActivationTotal += activation;
        state.HasWeakAssociation |= weakAssociation;

        if (!string.IsNullOrWhiteSpace(path) && !state.PathSet.Contains(path))
        {
            state.PathSet.Add(path);
            state.Paths.Add(path);
        }

        if (!string.IsNullOrWhiteSpace(seedToken))
            state.SeedTokens.Add(seedToken);
    }

    private static double BuildSeedActivation(SemanticGraphNode seed, double rarity, string queryText)
    {
        var quotedBonus = IsQuoted(queryText, seed.Text) ? 0.12 : 0.0;
        var kindWeight = seed.NodeKind switch
        {
            var kind when kind == SemanticGraphNodeKind.Name => 0.96,
            var kind when kind == SemanticGraphNodeKind.Topic => 0.92,
            var kind when kind == SemanticGraphNodeKind.Token => 0.86,
            var kind when kind == SemanticGraphNodeKind.EntityAnchor => 0.82,
            var kind when kind == SemanticGraphNodeKind.Scene => 0.72,
            _ => 0.68
        };

        return kindWeight * (0.55 + rarity * 0.45 + quotedBonus);
    }

    private static string BuildQueryText(string latestText, IReadOnlyList<ChatHistoryEntry> recentHistory)
    {
        var recent = recentHistory
            .OrderBy(entry => entry.CreatedAt)
            .TakeLast(6)
            .Select(entry => entry.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content));
        return string.Join("\n", recent.Append(latestText ?? ""));
    }

    private static string ExtendPath(string currentPath, string nextText)
    {
        if (string.IsNullOrWhiteSpace(nextText))
            return currentPath;
        if (string.IsNullOrWhiteSpace(currentPath))
            return nextText.Trim();
        if (currentPath.EndsWith(nextText, StringComparison.OrdinalIgnoreCase))
            return currentPath;
        return $"{currentPath}->{nextText.Trim()}";
    }

    private static double GetEdgeKindWeight(string edgeKind, string nextNodeKind)
    {
        if (string.Equals(nextNodeKind, SemanticGraphNodeKind.Memory, StringComparison.OrdinalIgnoreCase))
        {
            return edgeKind switch
            {
                var kind when kind == SemanticGraphEdgeKind.About => 1.00,
                var kind when kind == SemanticGraphEdgeKind.Mentions => 0.94,
                var kind when kind == SemanticGraphEdgeKind.Speaker => 0.82,
                var kind when kind == SemanticGraphEdgeKind.InScene => 0.74,
                var kind when kind == SemanticGraphEdgeKind.CoOccurs => 0.56,
                var kind when kind == SemanticGraphEdgeKind.AliasHint => 0.52,
                var kind when kind == SemanticGraphEdgeKind.SameScene => 0.48,
                _ => 0.0
            };
        }

        return edgeKind switch
        {
            var kind when kind == SemanticGraphEdgeKind.CoOccurs => 0.60,
            var kind when kind == SemanticGraphEdgeKind.AliasHint => 0.58,
            var kind when kind == SemanticGraphEdgeKind.SameScene => 0.50,
            _ => 0.0
        };
    }

    private static bool IsWeakExpansionEdge(string edgeKind)
        => edgeKind is SemanticGraphEdgeKind.CoOccurs or SemanticGraphEdgeKind.AliasHint or SemanticGraphEdgeKind.SameScene;

    private static double RarityWeight(int nodeCount)
        => nodeCount <= 0 ? 1.0 : 1.0 / Math.Sqrt(nodeCount);

    private static double DegreePenalty(int degree)
        => degree <= 0 ? 1.0 : 1.0 / Math.Sqrt(degree);

    private static double DepthDecay(int depth)
        => Math.Pow(0.72, Math.Max(0, depth - 1));

    private static double NormalizeImportance(double importance)
        => Math.Clamp(importance / 100.0, 0, 1);

    private static double RecentActivationBonus(DateTime? lastActivatedAt)
    {
        if (lastActivatedAt == null)
            return 0.0;

        var ageDays = Math.Max(0, (DateTime.UtcNow - lastActivatedAt.Value).TotalDays);
        return Math.Exp(-ageDays / 14.0);
    }

    private static bool Contains(string haystack, string needle)
        => !string.IsNullOrWhiteSpace(haystack)
           && !string.IsNullOrWhiteSpace(needle)
           && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCjk(string text)
        => text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

    private static bool IsQuoted(string text, string term)
        => text.Contains($"\"{term}\"", StringComparison.Ordinal)
           || text.Contains($"\u201C{term}\u201D", StringComparison.Ordinal)
           || text.Contains($"\u2018{term}\u2019", StringComparison.Ordinal)
           || text.Contains($"\u300A{term}\u300B", StringComparison.Ordinal);

    private sealed class FrontierState
    {
        public FrontierState(long nodeId, double activation, int depth, string path, string seedToken, bool hasWeakAssociation)
        {
            NodeId = nodeId;
            Activation = activation;
            Depth = depth;
            Path = path;
            SeedToken = seedToken;
            HasWeakAssociation = hasWeakAssociation;
        }

        public long NodeId { get; }
        public double Activation { get; }
        public int Depth { get; }
        public string Path { get; }
        public string SeedToken { get; }
        public bool HasWeakAssociation { get; }
    }

    private sealed class MemoryHitState
    {
        public MemoryHitState(SemanticGraphNode memory)
        {
            Memory = memory;
        }

        public SemanticGraphNode Memory { get; set; }
        public double ActivationTotal { get; set; }
        public bool HasWeakAssociation { get; set; }
        public List<string> Paths { get; } = new();
        public HashSet<string> PathSet { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SeedTokens { get; } = new(StringComparer.OrdinalIgnoreCase);

        public double BuildRankScore()
        {
            var importanceBonus = NormalizeImportance(Memory.Importance) * 0.20;
            var recencyBonus = RecentActivationBonus(Memory.LastActivatedAt) * 0.10;
            var multiPathBonus = Math.Min(0.18, Math.Max(0, SeedTokens.Count - 1) * 0.06 + Math.Max(0, Paths.Count - 1) * 0.03);
            return ActivationTotal + importanceBonus + recencyBonus + multiPathBonus;
        }

        public double BuildDisplayScore()
        {
            var rankScore = BuildRankScore();
            return rankScore / (1.0 + rankScore);
        }
    }

    private sealed class TermCandidate
    {
        public TermCandidate(string term)
        {
            Term = term;
        }

        public string Term { get; }
        public int OccurrenceCount { get; set; }
        public double Score { get; set; }
    }
}
