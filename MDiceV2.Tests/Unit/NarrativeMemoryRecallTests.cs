using AIMod.Trpg;
using FluentAssertions;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class NarrativeMemoryRecallTests
{
    [Fact]
    public void ArchiveToGraph_MetadataInference_CreatesUsableNarrativeNode()
    {
        var node = new TimelineNode
        {
            Content = "爱丽丝发现主教隐瞒了密室中的真相，两人的信任出现裂痕。",
            Importance = 8,
            EventSequence = 42,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var narrative = NarrativeMemoryHeuristics.CreateFromTimelineNode(node, new[] { "爱丽丝", "主教" });

        narrative.NarrativeWeight.Should().BeGreaterThanOrEqualTo(0.8f);
        narrative.InvolvedEntities.Should().Contain("爱丽丝");
        narrative.InvolvedEntities.Should().Contain("主教");
        narrative.ArcTags.Should().Contain(tag => tag == "relationship" || tag == "mystery");
        narrative.RelationshipImpact.Should().BeGreaterThanOrEqualTo(0.6f);
        narrative.MysteryWeight.Should().BeGreaterThanOrEqualTo(0.6f);
    }

    [Fact]
    public void NarrativeWeight_StronglyAffectsBaseScore_ForOldNodes()
    {
        const int currentFoldCount = 3600;
        var important = new NarrativeMemoryNode
        {
            CreatedFoldCount = 0,
            NarrativeWeight = 1.0f,
            IsResolved = true
        };
        var minor = new NarrativeMemoryNode
        {
            CreatedFoldCount = 0,
            NarrativeWeight = 0.1f,
            IsResolved = true
        };

        important.CalculateNarrativeScore(currentFoldCount).Should().BeGreaterThan(minor.CalculateNarrativeScore(currentFoldCount) + 0.2f);
        important.CalculateNarrativeScore(currentFoldCount).Should().BeGreaterThan(0.3f);
    }

    [Fact]
    public void OldImportantNode_BeatsNewIrrelevantNode()
    {
        var now = new DateTime(2026, 5, 27, 0, 0, 0, DateTimeKind.Utc);
        const int currentFoldCount = 2400;
        var queryTokens = TrpgContextPipeline.ExtractNarrativeIntentTermsForTest("爱丽丝和主教的真相是什么");
        var present = new HashSet<string>(new[] { "爱丽丝", "主教" }, StringComparer.OrdinalIgnoreCase);
        var oldImportantNode = new NarrativeMemoryNode
        {
            Id = 1,
            Timestamp = now.AddDays(-60),
            CreatedFoldCount = 0,
            NarrativeWeight = 0.9f,
            Summary = "爱丽丝发现主教隐瞒了密室中的真相。",
            InvolvedEntities = new List<string> { "爱丽丝", "主教" },
            MysteryWeight = 0.8f
        };
        var newIrrelevantNode = new NarrativeMemoryNode
        {
            Id = 2,
            Timestamp = now,
            CreatedFoldCount = currentFoldCount,
            NarrativeWeight = 0.2f,
            Summary = "路人买了一袋苹果"
        };

        var oldScore = NarrativeMemoryRecallScorer.ScoreNarrativeNode(oldImportantNode, queryTokens, present, currentFoldCount);
        var newScore = NarrativeMemoryRecallScorer.ScoreNarrativeNode(newIrrelevantNode, queryTokens, present, currentFoldCount);

        oldScore.FinalScore.Should().BeGreaterThan(newScore.FinalScore);
    }

    [Fact]
    public void ResolvedHistoricalFact_CanStillEnterTopEight()
    {
        var now = new DateTime(2026, 5, 27, 0, 0, 0, DateTimeKind.Utc);
        const int currentFoldCount = 3000;
        var queryTokens = TrpgContextPipeline.ExtractNarrativeIntentTermsForTest("教会现在为什么有审判权");
        var historicalNode = new NarrativeMemoryNode
        {
            Id = 100,
            IsResolved = true,
            Timestamp = now.AddDays(-75),
            CreatedFoldCount = 0,
            NarrativeWeight = 0.95f,
            GoalImpact = 0.8f,
            Summary = "王城政变结束后，教会获得了审判权。",
            ArcTags = new List<string> { "world_state" }
        };
        var candidates = Enumerable.Range(0, 10)
            .Select(i => new NarrativeMemoryNode
            {
                Id = i + 1,
                Timestamp = now.AddDays(-i),
                CreatedFoldCount = currentFoldCount - i,
                NarrativeWeight = 0.15f,
                Summary = $"无关日常记录 {i}"
            })
            .Append(historicalNode)
            .Select(n => NarrativeMemoryRecallScorer.ScoreNarrativeNode(n, queryTokens, Array.Empty<string>(), currentFoldCount));

        var selected = NarrativeMemoryRecallScorer.SelectTopScores(candidates, 8);

        selected.Select(x => x.Node.Id).Should().Contain(100);
    }

    [Fact]
    public void NarrativeSelection_DeduplicatesBySourceEventOrId_NotFormattedText()
    {
        var now = new DateTime(2026, 5, 27, 0, 0, 0, DateTimeKind.Utc);
        const int currentFoldCount = 10;
        var first = new NarrativeMemoryNode
        {
            Id = 1,
            SourceEventId = 10,
            Timestamp = now,
            NarrativeWeight = 0.8f,
            Summary = "相似摘要",
            ArcTags = new List<string> { "mystery" }
        };
        var second = new NarrativeMemoryNode
        {
            Id = 2,
            SourceEventId = 11,
            Timestamp = now.AddMinutes(-1),
            NarrativeWeight = 0.8f,
            Summary = "相似摘要",
            ArcTags = new List<string> { "mystery" }
        };

        var selected = NarrativeMemoryRecallScorer.SelectTopScores(new[] { first, second }
            .Select(n => NarrativeMemoryRecallScorer.ScoreNarrativeNode(n, new[] { "相似摘要" }, Array.Empty<string>(), currentFoldCount)), 8);

        selected.Should().HaveCount(2);
    }

    [Fact]
    public void SemanticIndex_FiltersTimelineAndPreservesRecallOrder()
    {
        var oldKeywordMemory = new MemoryNode
        {
            Id = 1,
            NodeType = "semantic",
            Summary = "girl hid the silver key in her left boot and mentioned the old badge",
            Keywords = "silver-key left-boot old-badge",
            Importance = 0.4,
            Heat = 0.0,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RawExcerpt = "[\"old semantic excerpt\"]"
        };
        var recentTimeline = new MemoryNode
        {
            Id = 2,
            NodeType = "timeline",
            Summary = "timeline: Bruce continued moving through the alley",
            Keywords = "flow recap timeline Bruce",
            Importance = 0.95,
            Heat = 1.0,
            CreatedAt = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc),
            RawExcerpt = "[\"timeline excerpt must not leak\"]"
        };

        var lines = TrpgContextPipeline.BuildSemanticIndexLinesForTest(new List<MemoryNode>
        {
            oldKeywordMemory,
            recentTimeline
        });

        lines.Should().HaveCount(1);
        lines[0].Should().Contain("silver key");
        lines[0].Should().NotContain("timeline: Bruce");
        TrpgContextPipeline.IsSemanticRecallNode(recentTimeline).Should().BeFalse();
    }

    [Fact]
    public void MemoryRecallScore_ExactKeywordMatchBeatsPureRecency()
    {
        var oldExactKeywordScore = ChatDatabase.CalculateMemoryRecallScoreForTest(
            keywordScore: 1.0,
            embeddingScore: 0.0,
            entityScore: 0.0,
            sceneScore: 0.0,
            importanceScore: 0.4,
            recencyScore: 0.0);
        var newRecencyOnlyScore = ChatDatabase.CalculateMemoryRecallScoreForTest(
            keywordScore: 0.0,
            embeddingScore: 0.0,
            entityScore: 0.0,
            sceneScore: 0.0,
            importanceScore: 0.95,
            recencyScore: 1.0);

        oldExactKeywordScore.Should().BeGreaterThan(newRecencyOnlyScore);
    }
}
