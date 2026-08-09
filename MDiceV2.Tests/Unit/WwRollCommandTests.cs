using System.Reflection;
using System.Text.RegularExpressions;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class WwRollCommandTests
{
    private static readonly PropertyInfo DataIoProperty =
        typeof(MessageProcessor).GetProperty(nameof(MessageProcessor.DataIO))
        ?? throw new InvalidOperationException("DataIO property not found.");

    private static readonly MethodInfo LoadUserDataMethod =
        typeof(MessageProcessor).GetMethod("LoadUserData", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LoadUserData not found.");

    [Fact]
    public void ThresholdAboveTen_DisablesAddDice()
    {
        var harness = new WwRollCommandHarness();

        harness.Execute("1a11");

        string reply = Assert.Single(harness.Replies);
        Assert.Contains("加骰阈值≥11（不加骰）", reply);
        Assert.Contains("加骰0", reply);
        Assert.DoesNotContain("第2轮", reply);
    }

    [Fact]
    public void TotalWithoutModifier_DoesNotRepeatTheSameValueInAnEquation()
    {
        var harness = new WwRollCommandHarness();

        harness.Execute("1a11");

        string reply = Assert.Single(harness.Replies);
        Assert.Matches(@"总计成功度: \d+\s*$", reply);
        Assert.DoesNotContain(" = ", reply);
    }

    [Fact]
    public void TotalWithModifier_StillShowsCalculation()
    {
        var harness = new WwRollCommandHarness();

        harness.Execute("1a11+2");

        string reply = Assert.Single(harness.Replies);
        Assert.Matches(@"总计成功度: \d+ \+2 = \d+\s*$", reply);
    }

    [Fact]
    public void ExplicitThreshold_IsCachedForThatUserOnly()
    {
        var harness = new WwRollCommandHarness();
        const long otherUserId = 30003;

        harness.Execute("1a11");
        harness.Replies.Clear();

        harness.Execute("1");
        Assert.Contains("加骰阈值≥11（不加骰）", Assert.Single(harness.Replies));

        harness.Replies.Clear();
        harness.Execute("1", otherUserId);
        Assert.DoesNotContain("加骰阈值", Assert.Single(harness.Replies));
    }

    [Fact]
    public void ExplicitThreshold_IsLoadedFromPersistedUserData()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mdice-ww-tests-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "test.db");

        try
        {
            using (var dataIO = new TestDataIO(databasePath))
            {
                var firstProcessor = CreateProcessor(dataIO.Value);
                var firstHarness = new WwRollCommandHarness(firstProcessor);
                firstHarness.Execute("1a11");
            }

            using (var dataIO = new TestDataIO(databasePath))
            {
                var restartedProcessor = CreateProcessor(dataIO.Value);
                LoadUserDataMethod.Invoke(restartedProcessor, null);

                var restartedHarness = new WwRollCommandHarness(restartedProcessor);
                restartedHarness.Execute("1");

                Assert.Contains("加骰阈值≥11（不加骰）", Assert.Single(restartedHarness.Replies));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static MessageProcessor CreateProcessor(DataIO dataIO)
    {
        var processor = new MessageProcessor();
        DataIoProperty.SetValue(processor, dataIO);
        return processor;
    }

    private sealed class WwRollCommandHarness
    {
        private const long TestGroupId = 10001;
        private const long TestUserId = 20002;

        private static readonly MethodInfo HandleWwRollMethod =
            typeof(MessageProcessor).GetMethod("HandleWwRoll", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleWwRoll not found.");

        public MessageProcessor Processor { get; }
        public List<string> Replies { get; } = new();

        public WwRollCommandHarness(MessageProcessor? processor = null)
        {
            Processor = processor ?? new MessageProcessor();
            var distribution = new MessageDistribution();
            distribution.OnReplySent += (content, _) => Replies.Add(content);
            distribution.MessageProcessor = Processor;
            Processor.MessageDistribution = distribution;
        }

        public void Execute(string args, long userId = TestUserId)
        {
            var msg = new Msg(TestGroupId, userId, $".ww{args}", MessageSource.group);
            HandleWwRollMethod.Invoke(Processor, new object[] { args, msg });
        }
    }

    private sealed class TestDataIO : IDisposable
    {
        public TestDataIO(string path)
        {
            Value = new DataIO(path);
        }

        public DataIO Value { get; }

        public void Dispose()
        {
            Value.Close();
        }
    }
}
