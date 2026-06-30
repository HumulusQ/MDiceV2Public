using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Archives high-value timeline material into long-term memory and narrative memory.
/// </summary>
public class ArchiveToGraph
{
    private readonly ChatDatabase _db;
    private readonly EpisodicMemory _episodicMemory;
    private readonly IModContext _context;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;

    public ArchiveToGraph(
        ChatDatabase db,
        EpisodicMemory episodicMemory,
        IModContext context,
        Func<List<ChatMessage>, Task<string?>> apiCaller)
    {
        _db = db;
        _episodicMemory = episodicMemory;
        _context = context;
        _apiCaller = apiCaller;
    }

    public async Task ArchiveNodesAsync(TrpgScope scope, string characterId, List<TimelineNode> l1Nodes)
    {
        var groupId = scope.GroupId;
        var allNodes = new List<TimelineNode>(l1Nodes);
        foreach (var l1 in l1Nodes)
        {
            var children = await _db.GetTimelineChildNodesAsync(scope, l1.Id);
            allNodes.AddRange(children);
        }

        var archived = 0;

        foreach (var node in allNodes)
        {
            if (node.Importance >= 7)
            {
                _context.Log(LogLevel.Debug,
                    $"[AIMod:TRPG] Archive important timeline node | Content={TrimForLog(node.Content, 60)} | Importance={node.Importance}");

                await _episodicMemory.AddMemoryAsync(scope, characterId,
                    EpisodicMemory.MemoryType.Episodic, node.Content, confidence: 0.9);
                archived++;
            }

            if (node.Foreshadowing)
            {
                _context.Log(LogLevel.Debug,
                    $"[AIMod:TRPG] Archive foreshadowing timeline node | Content={TrimForLog(node.Content, 60)}");

                await _episodicMemory.AddMemoryAsync(scope, characterId,
                    EpisodicMemory.MemoryType.Semantic, $"[未解谜团] {node.Content}", confidence: 0.95);
                archived++;

                await WriteNarrativeMemoryNodeAsync(scope, characterId, node);
            }
        }

        if (archived > 0)
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] ArchiveToGraph: archived {archived} items to Graph (Group={groupId}, Char={characterId})");
    }

    private async Task WriteNarrativeMemoryNodeAsync(TrpgScope scope, string characterId, TimelineNode node)
    {
        try
        {
            var knownEntities = await BuildKnownEntityTermsAsync(scope);
            var narrativeNode = NarrativeMemoryHeuristics.CreateFromTimelineNode(node, knownEntities);

            _context.Log(LogLevel.Debug,
                $"[AIMod:TRPG] Create narrative node | Summary={TrimForLog(node.Content, 60)} | " +
                $"NarrativeWeight={narrativeNode.NarrativeWeight:F2} | RelationshipImpact={narrativeNode.RelationshipImpact:F2} | " +
                $"GoalImpact={narrativeNode.GoalImpact:F2} | MysteryWeight={narrativeNode.MysteryWeight:F2} | " +
                $"Entities={string.Join(",", narrativeNode.InvolvedEntities)} | Tags={string.Join(",", narrativeNode.ArcTags)} | " +
                $"EventSequence={node.EventSequence}");

            await _db.InsertNarrativeMemoryNodeAsync(scope, characterId, narrativeNode);

            _context.Log(LogLevel.Info, $"[AIMod:TRPG] Narrative node saved | CharacterId={characterId} | Summary={node.Content}");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Narrative node write failed: {ex.Message}");
        }
    }

    private async Task<List<string>> BuildKnownEntityTermsAsync(TrpgScope scope)
    {
        try
        {
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entities = await _db.GetAllEntityCanonicalAsync(scope);
            foreach (var entity in entities)
            {
                Add(entity.EntityId);
                Add(entity.CurrentDisplayName);
                foreach (var alias in entity.Aliases ?? new List<string>())
                    Add(alias);
            }

            return terms.ToList();

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var trimmed = value.Trim();
                if (trimmed.Length >= 2 && trimmed.Length <= 40)
                    terms.Add(trimmed);
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] ArchiveToGraph known entity lookup skipped: {ex.Message}");
            return new List<string>();
        }
    }

    private static string TrimForLog(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
