using AIMod.Trpg;
using FluentAssertions;
using MDiceV2.Interfaces;
using MDiceV2.Interfaces.Mod;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class NarrativeAndDistillerRegressionTests
{
    [Fact]
    public void NarrativeRecall_HighBaseUnrelatedNode_IsNotEligibleWithoutQueryRelevance()
    {
        const int currentFoldCount = 3000;
        var queryTokens = new[] { "silver", "key", "left", "boot" };
        var related = new NarrativeMemoryNode
        {
            Id = 1,
            Timestamp = DateTime.UtcNow.AddDays(-90),
            CreatedFoldCount = 0,
            NarrativeWeight = 0.35f,
            Summary = "The girl said the silver key was hidden in the left boot.",
            ArcTags = new List<string> { "silver-key" }
        };
        var unrelatedHighBase = new NarrativeMemoryNode
        {
            Id = 2,
            Timestamp = DateTime.UtcNow,
            CreatedFoldCount = currentFoldCount,
            NarrativeWeight = 1.0f,
            MysteryWeight = 1.0f,
            IsResolved = false,
            Summary = "The dock ledger mystery remains unresolved.",
            ArcTags = new List<string> { "ledger" }
        };

        var selected = NarrativeMemoryRecallScorer.SelectTopScores(new[] { unrelatedHighBase, related }
            .Select(n => NarrativeMemoryRecallScorer.ScoreNarrativeNode(n, queryTokens, Array.Empty<string>(), currentFoldCount)), 8);

        selected.Select(x => x.Node.Id).Should().Contain(1);
        selected.Select(x => x.Node.Id).Should().NotContain(2);
        selected[0].Node.Id.Should().Be(1);
    }

    [Fact]
    public void NarrativeRecall_RecencyCannotBeatExactTokenMatch()
    {
        const int currentFoldCount = 3000;
        var oldExact = new NarrativeMemoryNode
        {
            Id = 1,
            Timestamp = DateTime.UtcNow.AddDays(-120),
            CreatedFoldCount = 0,
            NarrativeWeight = 0.25f,
            Summary = "The silver key is in the left boot."
        };
        var recentUnrelated = new NarrativeMemoryNode
        {
            Id = 2,
            Timestamp = DateTime.UtcNow,
            CreatedFoldCount = currentFoldCount,
            NarrativeWeight = 0.9f,
            Summary = "The weather changed near the dock."
        };

        var oldScore = NarrativeMemoryRecallScorer.ScoreNarrativeNode(
            oldExact,
            new[] { "silver", "key", "left", "boot" },
            Array.Empty<string>(),
            currentFoldCount);
        var recentScore = NarrativeMemoryRecallScorer.ScoreNarrativeNode(
            recentUnrelated,
            new[] { "silver", "key", "left", "boot" },
            Array.Empty<string>(),
            currentFoldCount);

        oldScore.FinalScore.Should().BeGreaterThan(recentScore.FinalScore);
        oldScore.IsEligible.Should().BeTrue();
        recentScore.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void SemanticDistiller_ParseEventIdKey_AcceptsNumericAndEventPrefix()
    {
        SemanticDistiller.TryParseEventIdKey("123", out var numeric).Should().BeTrue();
        numeric.Should().Be(123);

        SemanticDistiller.TryParseEventIdKey("Event_123", out var prefixed).Should().BeTrue();
        prefixed.Should().Be(123);

        SemanticDistiller.TryParseEventIdKey("#123", out var hashPrefixed).Should().BeTrue();
        hashPrefixed.Should().Be(123);
    }

    [Fact]
    public async Task EventLogQueries_HydrateSemanticDistillationFields()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aimod_hydrate_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new ChatDatabase(dbPath, new TestModContext());
            await db.InitializeSchemaAsync();
            var scope = TrpgScope.Create(1, 2, "team", "campaign", "world-test");
            var evt = new WorldEvent
            {
                EventType = "discovery",
                Timestamp = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc),
                Payload = new Dictionary<string, object> { ["item"] = "silver key" },
                Consequences = new List<long>()
            };

            var eventId = await db.InsertEventLogAsync(scope, evt);
            evt.EventId = eventId;
            evt.SemanticSummary = "The silver key clue was distilled.";
            evt.NarrativeWeight = 0.7;
            evt.NarrativeTags = new List<string> { "clue", "silver-key" };
            evt.EmotionalWeight = 0.1;
            evt.ArcAffinity = "key-arc";
            evt.IsSemanticallyDistilled = true;

            await db.UpdateEventSemanticMetadataAsync(scope, eventId, evt);

            var replayed = await db.QueryEventLogAsync(scope, 0, null, 10);
            var loaded = replayed.Single(e => e.EventId == eventId);

            loaded.IsSemanticallyDistilled.Should().BeTrue();
            loaded.SemanticSummary.Should().Be("The silver key clue was distilled.");
            loaded.NarrativeTags.Should().Contain(new[] { "clue", "silver-key" });
            loaded.ArcAffinity.Should().Be("key-arc");

            var undistilled = await db.QueryUndistilledEventsAsync(scope, 10);
            undistilled.Select(e => e.EventId).Should().NotContain(eventId);
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private sealed class TestModContext : IModContext
    {
        public bool IsSimulationMode => true;

        public void SendGroupMessage(long groupId, string content) { }
        public void SendPrivateMessage(long userId, string content) { }
        public (long UserId, string Nickname) GetUserInfo(long userId) => (userId, $"user-{userId}");
        public void Log(LogLevel level, string message) { }
        public INavigationPanelRegistry? GetNavigationPanelRegistry() => null;
        public void ExecuteCommand(long groupId, long userId, string command) { }
        public void RegisterCommandReplyListener(Action<long, long, string> listener) { }
        public int? GetUserAuthLevel(long userId) => null;
        public bool IsBotEnabled(long groupId) => true;
    }
}
