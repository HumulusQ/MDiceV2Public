using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class InitiativeCommandTests
{
    [Theory]
    [InlineData(".ri.b", "奖励骰2次", "d20", "")]
    [InlineData(".ri.p", "惩罚骰2次", "d20", "")]
    [InlineData(".ri.b3 20", "奖励骰3次", "d20", "")]
    [InlineData(".ri.p3 20", "惩罚骰3次", "d20", "")]
    [InlineData(".ri.b +2", "奖励骰2次", "d20+2", "")]
    [InlineData(".ri.p +2", "惩罚骰2次", "d20+2", "")]
    [InlineData(".ri.b3+2", "奖励骰3次", "d20+2", "")]
    [InlineData(".ri.p3+2", "惩罚骰3次", "d20+2", "")]
    [InlineData(".ri.b3 d20+5 张三", "奖励骰3次", "d20+5", "张三")]
    [InlineData(".ri.p3 d20+5 张三", "惩罚骰3次", "d20+5", "张三")]
    public void AdvantageFormats_AddExpectedInitiativeEntry(
        string command,
        string expectedDetailPrefix,
        string expectedExpression,
        string expectedName)
    {
        var harness = new InitiativeCommandHarness();

        harness.Execute(command);

        harness.Replies.Should().NotBeEmpty();
        harness.Replies.Last().Should().Contain(expectedDetailPrefix);

        var entries = harness.GetEntries();
        entries.Should().ContainSingle();

        var entry = entries[0];
        if (string.IsNullOrEmpty(expectedName))
        {
            entry.Name.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            entry.Name.Should().Be(expectedName);
        }
        entry.DiceExpression.Should().Be(expectedExpression);
        entry.RollDetail.Should().Contain(expectedDetailPrefix);
    }

    [Theory]
    [InlineData(".ri.b 4#20", "奖励骰2次", 4)]
    [InlineData(".ri.p 4#20", "惩罚骰2次", 4)]
    [InlineData(".ri3#.b 20", "奖励骰2次", 3)]
    [InlineData(".ri3#.p 20", "惩罚骰2次", 3)]
    public void RepeatAndAdvantageCanAppearInEitherOrder(
        string command,
        string expectedDetailPrefix,
        int expectedRepeatCount)
    {
        var harness = new InitiativeCommandHarness();

        harness.Execute(command);

        harness.Replies.Should().NotBeEmpty();
        harness.Replies.Last().Should().Contain(expectedDetailPrefix);
        harness.Replies.Last().Should().Contain($"x{expectedRepeatCount}");

        var entries = harness.GetEntries();
        entries.Should().HaveCount(expectedRepeatCount);
        entries.Should().OnlyContain(entry => entry.DiceExpression == "d20");
    }

    [Fact]
    public void ExistingRiCommands_KeepWorkingAfterAdvantageSupport()
    {
        var harness = new InitiativeCommandHarness();

        harness.Execute(".ri");
        harness.GetEntries().Should().HaveCount(1);
        harness.Replies.Last().Should().Contain("已加入先攻列表");

        harness.Execute(".ri#+3");
        harness.GetEntries().Should().HaveCount(2);
        harness.Replies.Last().Should().Contain("已加入先攻列表");

        harness.Execute(".ri3#+3");
        harness.GetEntries().Should().HaveCount(5);
        harness.Replies.Last().Should().Contain("投掷先攻 x3");
    }

    [Fact]
    public void ListRemoveAndClear_StillOperateOnSharedInitiativeList()
    {
        var harness = new InitiativeCommandHarness();

        harness.Execute(".ri.b3 d20+5 张三");
        harness.Execute(".ri.p3 d20+5 李四");

        harness.Execute(".ri list");
        harness.Replies.Last().Should().Contain("张三").And.Contain("李四");

        harness.Execute(".ri remove 张三");
        harness.Replies.Last().Should().Contain("已移除 张三");
        harness.GetEntries().Select(entry => entry.Name).Should().NotContain("张三");

        harness.Execute(".ri clear");
        harness.Replies.Last().Should().Contain("先攻列表已清空");
        harness.GetEntries().Should().BeEmpty();

        harness.Execute(".ri list");
        harness.Replies.Last().Should().Be(GlobalFeedbackMessages.FeedbackTemplates["InitiativeListEmpty"]);
    }

    private sealed class InitiativeCommandHarness
    {
        private const long TestGroupId = 10001;
        private const long TestUserId = 20002;

        private static readonly MethodInfo HandleInitiativeCommandMethod =
            typeof(MessageProcessor).GetMethod("HandleInitiativeCommand", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleInitiativeCommand not found.");

        private static readonly FieldInfo GroupInitiativeListsField =
            typeof(MessageProcessor).GetField("groupInitiativeLists", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("groupInitiativeLists not found.");

        public MessageProcessor Processor { get; }
        public List<string> Replies { get; } = new();

        public InitiativeCommandHarness()
        {
            Processor = new MessageProcessor();

            var distribution = new MessageDistribution();
            distribution.OnReplySent += (content, _) => Replies.Add(content);
            distribution.MessageProcessor = Processor;
            Processor.MessageDistribution = distribution;
        }

        public void Execute(string command)
        {
            var msg = new Msg(TestGroupId, TestUserId, command, MessageSource.group);
            HandleInitiativeCommandMethod.Invoke(Processor, new object[] { string.Empty, msg });
        }

        public List<InitiativeListEntry> GetEntries()
        {
            var lists = (ConcurrentDictionary<long, InitiativeList>)GroupInitiativeListsField.GetValue(Processor)!;
            return lists.TryGetValue(TestGroupId, out var list)
                ? list.GetAll()
                : new List<InitiativeListEntry>();
        }
    }
}
