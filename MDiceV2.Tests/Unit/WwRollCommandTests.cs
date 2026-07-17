using System.Reflection;
using System.Text.RegularExpressions;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class WwRollCommandTests
{
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

    private sealed class WwRollCommandHarness
    {
        private const long TestGroupId = 10001;
        private const long TestUserId = 20002;

        private static readonly MethodInfo HandleWwRollMethod =
            typeof(MessageProcessor).GetMethod("HandleWwRoll", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleWwRoll not found.");

        public MessageProcessor Processor { get; } = new();
        public List<string> Replies { get; } = new();

        public WwRollCommandHarness()
        {
            var distribution = new MessageDistribution();
            distribution.OnReplySent += (content, _) => Replies.Add(content);
            distribution.MessageProcessor = Processor;
            Processor.MessageDistribution = distribution;
        }

        public void Execute(string args)
        {
            var msg = new Msg(TestGroupId, TestUserId, $".ww{args}", MessageSource.group);
            HandleWwRollMethod.Invoke(Processor, new object[] { args, msg });
        }
    }
}
