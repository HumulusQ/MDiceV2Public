using System;
using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class RollPickModeTests
{
    [Theory]
    [InlineData("4#d20", 4, "None", 2, "d20")]
    [InlineData(" 4#d20", 4, "None", 2, "d20")]
    [InlineData(".b d20", 1, "Bonus", 2, "d20")]
    [InlineData(".p d20", 1, "Penalty", 2, "d20")]
    [InlineData(".b 4#d20", 4, "Bonus", 2, "d20")]
    [InlineData(".p 4#d20", 4, "Penalty", 2, "d20")]
    [InlineData("3#.b d20", 3, "Bonus", 2, "d20")]
    [InlineData("3#.p d20", 3, "Penalty", 2, "d20")]
    public void PrefixParser_AllowsRepeatAndModifierInEitherOrder(
        string input,
        int expectedRepeatCount,
        string expectedPickMode,
        int expectedPickCount,
        string expectedRemaining)
    {
        var harness = new RollCommandHarness();
        var (repeatCount, pickMode, pickCount, remaining) = harness.ParsePrefixes(input);

        repeatCount.Should().Be(expectedRepeatCount);
        pickMode.Should().Be(expectedPickMode);
        pickCount.Should().Be(expectedPickCount);
        remaining.Should().Be(expectedRemaining);
    }

    [Theory]
    [InlineData(".r.b", "奖励骰2次")]
    [InlineData(".r.p", "惩罚骰2次")]
    [InlineData(".r.b3", "奖励骰3次")]
    [InlineData(".r.p3", "惩罚骰3次")]
    [InlineData(".r.b d20", "奖励骰2次")]
    [InlineData(".r.p d20", "惩罚骰2次")]
    [InlineData(".r.b3 d20", "奖励骰3次")]
    [InlineData(".r.p3 d20", "惩罚骰3次")]
    [InlineData(".r.b d20+2", "奖励骰2次")]
    [InlineData(".r.p d20+2", "惩罚骰2次")]
    [InlineData(".r.b3 d20+5", "奖励骰3次")]
    [InlineData(".r.p3 d20+5", "惩罚骰3次")]
    [InlineData(".r.b3 2d6+3", "奖励骰3次")]
    [InlineData(".r.p3 2d6+3", "惩罚骰3次")]
    public void PickModeFormats_WithExplicitDice_AreHandled(string command, string expectedDetailPrefix)
    {
        var harness = new RollCommandHarness();

        harness.Execute(command);

        harness.Replies.Should().NotBeEmpty();
        harness.Replies.Last().Should().Contain(expectedDetailPrefix);
        harness.Replies.Last().Should().NotContain("不支持省略 d");
    }

    [Theory]
    [InlineData(".r.b3 20")]
    [InlineData(".r.p3 20")]
    [InlineData(".r.b +2")]
    [InlineData(".r.p +2")]
    [InlineData(".r.b3+2")]
    [InlineData(".r.p3+2")]
    public void PickModeFormats_WithoutExplicitDice_AreRejected(string command)
    {
        var harness = new RollCommandHarness();

        harness.Execute(command);

        harness.Replies.Should().NotBeEmpty();
        harness.Replies.Last().Should().Contain("不支持省略 d");
        harness.Replies.Last().Should().Contain(".r.b3 d20");
        harness.Replies.Last().Should().Contain(".r.b d20+2");
    }

    [Theory]
    [InlineData(".r4#d20")]
    [InlineData(".r 4#d20")]
    public void StandardRepeatSyntax_StillWorks(string command)
    {
        var harness = new RollCommandHarness();

        harness.Execute(command);

        harness.Replies.Should().NotBeEmpty();
        harness.Replies.Last().Should().Contain("执行了掷骰");
        harness.Replies.Last().Should().NotContain("不支持省略 d");
    }

    [Theory]
    [InlineData(".r.b 4#d20", "奖励骰2次", 4)]
    [InlineData(".r.p 4#d20", "惩罚骰2次", 4)]
    [InlineData(".r3#.b d20", "奖励骰2次", 3)]
    [InlineData(".r3#.p d20", "惩罚骰2次", 3)]
    public void PickModeWithRepeat_AppliesToWholeCommand(string command, string expectedPickPhrase, int expectedRepeats)
    {
        var harness = new RollCommandHarness();

        harness.Execute(command);

        harness.Replies.Should().NotBeEmpty();
        var reply = harness.Replies.Last();
        reply.Should().NotContain("不支持省略 d");
        reply.Should().Contain(expectedPickPhrase);
        reply.Split(expectedPickPhrase, StringSplitOptions.None).Length.Should().Be(expectedRepeats + 1);
    }

    [Theory]
    [InlineData(".r d20")]
    [InlineData(".r 1d100")]
    [InlineData(".r 2d6+3")]
    [InlineData(".r")]
    public void StandardRollFormats_StillWork(string command)
    {
        var harness = new RollCommandHarness();

        harness.Execute(command);

        harness.Replies.Should().NotBeEmpty();
        harness.Replies.Last().Should().NotContain("不支持省略 d");
    }

    private sealed class RollCommandHarness
    {
        private const long TestGroupId = 10001;
        private const long TestUserId = 20002;

        private static readonly MethodInfo HandleRollMethod =
            typeof(MessageProcessor).GetMethod("HandleRoll", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleRoll not found.");

        public MessageProcessor Processor { get; }
        public List<string> Replies { get; } = new();

        public RollCommandHarness()
        {
            Processor = new MessageProcessor();

            var distribution = new MessageDistribution();
            distribution.OnReplySent += (content, _) => Replies.Add(content);
            distribution.MessageProcessor = Processor;
            Processor.MessageDistribution = distribution;
        }

        public (int repeatCount, string pickMode, int pickCount, string remaining) ParsePrefixes(string input)
        {
            var method = typeof(MessageProcessor).GetMethod("TryParseRollCommandPrefixes", BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException("TryParseRollCommandPrefixes not found.");

            object[] arguments =
            {
                input,
                0,
                Activator.CreateInstance(typeof(MessageProcessor)
                    .GetNestedType("RollPickMode", BindingFlags.NonPublic) ?? throw new InvalidOperationException("RollPickMode not found."))!,
                0,
                string.Empty
            };

            var parsed = (bool)method.Invoke(Processor, arguments)!;
            parsed.Should().BeTrue();

            return (
                (int)arguments[1],
                arguments[2].ToString() ?? string.Empty,
                (int)arguments[3],
                arguments[4]?.ToString() ?? string.Empty);
        }

        public void Execute(string command)
        {
            if (!command.StartsWith(".r", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Command must start with .r", nameof(command));
            }

            string args = command.Length > 2 ? command[2..] : string.Empty;
            var msg = new Msg(TestGroupId, TestUserId, command, MessageSource.group);
            HandleRollMethod.Invoke(Processor, new object[] { args, msg });
        }
    }
}
