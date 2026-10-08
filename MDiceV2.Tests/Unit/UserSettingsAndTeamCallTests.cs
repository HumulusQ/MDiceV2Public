using System.Collections.Concurrent;
using System.Reflection;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class UserSettingsAndTeamCallTests
{
    private const long GroupId = 41001;
    private const long UserId = 42001;
    private static readonly BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 9999 ", 9999)]
    public void SetCommand_AcceptsValidRange(string args, int expected)
    {
        var harness = new CommandHarness();

        InvokeHandler(harness.Processor, "HandleSetCommand", args, harness.Message(".set" + args));

        Assert.Contains($"D{expected}", Assert.Single(harness.Replies));
        Assert.Equal(expected, GetDefaultDice(harness.Processor));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10000")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("20abc")]
    public void SetCommand_RejectsInvalidInputWithoutReplacingCurrentValue(string args)
    {
        var harness = new CommandHarness();
        InvokeHandler(harness.Processor, "HandleSetCommand", "20", harness.Message(".set20"));
        harness.Replies.Clear();

        InvokeHandler(harness.Processor, "HandleSetCommand", args, harness.Message(".set" + args));

        Assert.Contains("格式错误", Assert.Single(harness.Replies));
        Assert.Equal(20, GetDefaultDice(harness.Processor));
    }

    [Fact]
    public void RollWithoutSides_UsesSavedDefaultButExplicitSidesWins()
    {
        var harness = new CommandHarness();
        InvokeHandler(harness.Processor, "HandleSetCommand", "20", harness.Message(".set20"));
        harness.Replies.Clear();

        InvokeHandler(harness.Processor, "HandleRoll", "d", harness.Message(".rd"));
        Assert.Contains("1D20", Assert.Single(harness.Replies));

        harness.Replies.Clear();
        InvokeHandler(harness.Processor, "HandleRoll", "d6", harness.Message(".rd6"));
        Assert.Contains("1D6", Assert.Single(harness.Replies));
    }

    [Fact]
    public void NewCommands_AreAvailableThroughTheMainRouter()
    {
        var harness = new CommandHarness();

        harness.Processor.OnHandleMessage(harness.Message(".set 12"));
        Assert.Contains("D12", Assert.Single(harness.Replies));

        harness.Replies.Clear();
        harness.Processor.OnHandleMessage(harness.Message(".td call off"));
        Assert.Contains("已关闭", Assert.Single(harness.Replies));

        harness.Replies.Clear();
        harness.Processor.OnHandleMessage(harness.Message(".cfg"));
        string cfg = Assert.Single(harness.Replies);
        Assert.Contains("默认骰面：D12", cfg);
        Assert.Contains(".td call 状态：关闭", cfg);
    }

    [Fact]
    public void SettingsAndCallPreference_ArePersistedAndShownByCfg()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mdice-user-settings-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "test.db");

        try
        {
            using (var dataIo = new TestDataIO(databasePath))
            {
                var first = new CommandHarness(CreateProcessor(dataIo.Value));
                InvokeHandler(first.Processor, "HandleSetCommand", "42", first.Message(".set42"));
                InvokeHandler(first.Processor, "HandleTdCommand", "call off", first.Message(".td call off"));
                first.Processor.ImportCharacterCard(UserId, new MessageProcessor.CharacterSheet { Name = "Zulu" });
                first.Processor.ImportCharacterCard(UserId, new MessageProcessor.CharacterSheet { Name = "Alpha" });
            }

            using (var dataIo = new TestDataIO(databasePath))
            {
                var restarted = new CommandHarness(CreateProcessor(dataIo.Value));
                InvokeNoArg(restarted.Processor, "LoadUserData");

                InvokeHandler(restarted.Processor, "HandleConfigCommand", "", restarted.Message(".cfg"));
                string cfg = Assert.Single(restarted.Replies);
                Assert.Contains("默认骰面：D42", cfg);
                Assert.Contains("人物卡：Alpha、Zulu", cfg);
                Assert.Contains(".td call 状态：关闭", cfg);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyUserData_DefaultsToD100AndPrivateCallsEnabled()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mdice-user-legacy-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "test.db");

        try
        {
            using var dataIo = new TestDataIO(databasePath);
            dataIo.Value.SaveData("UserData", UserId.ToString(), $"{{\"UserId\":{UserId}}}");
            var harness = new CommandHarness(CreateProcessor(dataIo.Value));
            InvokeNoArg(harness.Processor, "LoadUserData");

            InvokeHandler(harness.Processor, "HandleConfigCommand", "", harness.Message(".cfg"));
            string cfg = Assert.Single(harness.Replies);
            Assert.Contains("默认骰面：D100", cfg);
            Assert.Contains("人物卡：无", cfg);
            Assert.Contains(".td call 状态：开启", cfg);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TeamCallPrivateBatch_PreservesOrderAndClampsDelays()
    {
        ResetTeamCallSendState();
        var processor = new MessageProcessor();
        var sent = new List<(long UserId, string Message)>();
        var delays = new List<TimeSpan>();
        var delayValues = new Queue<int>(new[] { 0, 9 });

        SetPrivateProperty(processor, "TeamCallPrivateSenderOverride",
            (Action<long, string>)((id, message) => sent.Add((id, message))));
        SetPrivateField(processor, "_teamCallDelaySecondsProvider",
            (Func<int>)(() => delayValues.Dequeue()));
        SetPrivateField(processor, "_teamCallDelayAsync",
            (Func<TimeSpan, CancellationToken, Task>)((delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            }));

        var task = (Task)InvokeMethod(
            processor,
            "SendTeamCallPrivateMessagesAsync",
            new List<long> { 11, 22, 33 },
            "集合",
            CancellationToken.None)!;
        await task;

        Assert.Equal(new long[] { 11, 22, 33 }, sent.Select(item => item.UserId));
        Assert.All(sent, item => Assert.Equal("集合", item.Message));
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, delays);
    }

    [Fact]
    public void TeamCallRecipients_FilterUsersWhoDisabledPrivateCalls()
    {
        var harness = new CommandHarness();
        InvokeHandler(
            harness.Processor,
            "HandleTdCommand",
            "call off",
            new Msg(GroupId, 22, ".td call off", MessageSource.group));

        var recipients = (List<long>)InvokeMethod(
            harness.Processor,
            "GetTeamCallPrivateRecipients",
            new List<long> { 11, 22, 33 })!;
        string message = (string)InvokeStaticMethod(
            "BuildTeamCallPrivateMessage",
            "测试队伍",
            "发起者")!;

        Assert.Equal(new long[] { 11, 33 }, recipients);
        Assert.Equal("队伍：测试队伍，发起者 正在召集你，集合咯。", message);
    }

    [Fact]
    public async Task ConcurrentTeamCallBatches_DoNotInterleave()
    {
        ResetTeamCallSendState();
        var firstProcessor = new MessageProcessor();
        var secondProcessor = new MessageProcessor();
        var sent = new ConcurrentQueue<long>();
        var firstMessageSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        SetPrivateProperty(firstProcessor, "TeamCallPrivateSenderOverride",
            (Action<long, string>)((id, _) =>
            {
                sent.Enqueue(id);
                firstMessageSent.TrySetResult();
            }));
        SetPrivateField(firstProcessor, "_teamCallDelaySecondsProvider", (Func<int>)(() => 1));
        SetPrivateField(firstProcessor, "_teamCallDelayAsync",
            (Func<TimeSpan, CancellationToken, Task>)((_, _) => releaseFirstBatch.Task));

        SetPrivateProperty(secondProcessor, "TeamCallPrivateSenderOverride",
            (Action<long, string>)((id, _) => sent.Enqueue(id)));
        SetPrivateField(secondProcessor, "_teamCallDelaySecondsProvider", (Func<int>)(() => 1));
        SetPrivateField(secondProcessor, "_teamCallDelayAsync",
            (Func<TimeSpan, CancellationToken, Task>)((_, _) => Task.CompletedTask));

        var firstTask = (Task)InvokeMethod(
            firstProcessor,
            "SendTeamCallPrivateMessagesAsync",
            new List<long> { 1, 2 },
            "第一批",
            CancellationToken.None)!;
        await firstMessageSent.Task;

        var secondTask = (Task)InvokeMethod(
            secondProcessor,
            "SendTeamCallPrivateMessagesAsync",
            new List<long> { 3, 4 },
            "第二批",
            CancellationToken.None)!;
        Assert.Equal(new long[] { 1 }, sent.ToArray());

        releaseFirstBatch.TrySetResult();
        await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, sent.ToArray());
    }

    private static MessageProcessor CreateProcessor(DataIO dataIo)
    {
        var processor = new MessageProcessor();
        typeof(MessageProcessor).GetProperty(nameof(MessageProcessor.DataIO))!.SetValue(processor, dataIo);
        return processor;
    }

    [Theory]
    [InlineData("coc1", 1, 4, "困难成功")]
    [InlineData("coc1", 5, 25, "大成功")]
    [InlineData("coc1", 96, 100, "大失败")]
    [InlineData("coc2", 1, 0, "大成功")]
    [InlineData("coc2", 2, 49, "极限成功")]
    [InlineData("coc2", 5, 49, "极限成功")]
    [InlineData("coc2", 5, 50, "大成功")]
    [InlineData("coc2", 6, 50, "极限成功")]
    [InlineData("coc2", 95, 49, "失败")]
    [InlineData("coc2", 96, 49, "大失败")]
    [InlineData("coc2", 96, 50, "失败")]
    [InlineData("coc2", 99, 50, "失败")]
    [InlineData("coc2", 99, 100, "成功")]
    [InlineData("coc2", 100, 100, "大失败")]
    [InlineData("coc3", 5, 0, "大成功")]
    [InlineData("coc3", 6, 0, "失败")]
    [InlineData("coc3", 95, 100, "成功")]
    [InlineData("coc3", 96, 100, "大失败")]
    public void CocRules_HonorSkillAndRollBoundaries(string rule, int roll, int skill, string expected)
        => Assert.Equal(expected, Dice.CoC7_Check(roll, skill, rule));

    [Fact]
    public void RuleSettings_AreIndependentAndInvalidInputDoesNotReplaceSelection()
    {
        var harness = new CommandHarness();
        harness.Processor.OnHandleMessage(harness.Message(".cfg rule coc2"));
        harness.Processor.OnHandleMessage(harness.Message(".cfg rule dnd1"));
        harness.Processor.OnHandleMessage(harness.Message(".cfg rule coc9"));
        Assert.Contains("格式错误", harness.Replies.Last());
        Assert.Equal("coc2", ResolveRule(harness.Processor, UserId, harness.Message(".cc"), "coc").Rule);
        Assert.Equal("dnd1", ResolveRule(harness.Processor, UserId, harness.Message(".cc"), "dnd").Rule);
        harness.Processor.OnHandleMessage(harness.Message(".cfg rule reset coc"));
        Assert.Equal("coc1", ResolveRule(harness.Processor, UserId, harness.Message(".cc"), "coc").Rule);
        Assert.Equal("dnd1", ResolveRule(harness.Processor, UserId, harness.Message(".cc"), "dnd").Rule);
    }

    [Fact]
    public void TeamRule_OverridesMembersAndVersusTargetButNotPrivateOrOtherGroup()
    {
        var h = new CommandHarness();
        InvokeHandler(h.Processor, "HandleTeamCommand", "new 测试团", h.Message(".team new 测试团"));
        InvokeHandler(h.Processor, "HandleConfigCommand", "rule coc2", h.Message(".cfg rule coc2"));
        InvokeHandler(h.Processor, "HandleTeamCommand", "rule coc3", h.Message(".team rule coc3"));
        var memberMsg = new Msg(GroupId, UserId + 1, ".cc", MessageSource.group);
        InvokeHandler(h.Processor, "HandleTeamCommand", "join 测试团", memberMsg);
        InvokeHandler(h.Processor, "HandleConfigCommand", "rule coc2", memberMsg);
        Assert.Equal("coc3", ResolveRule(h.Processor, UserId + 1, memberMsg, "coc").Rule);
        // 对抗的目标即使未加入发起者的队伍，也遵循本次对抗的强制规则。
        Assert.Equal("coc3", ResolveRule(h.Processor, UserId + 2, h.Message(".cc"), "coc", UserId).Rule);
        Assert.Equal("coc2", ResolveRule(h.Processor, UserId, new Msg(0, UserId, ".cc", MessageSource.privatechat), "coc").Rule);
        Assert.Equal("coc2", ResolveRule(h.Processor, UserId, new Msg(GroupId + 1, UserId, ".cc", MessageSource.group), "coc").Rule);
        h.Replies.Clear();
        InvokeHandler(h.Processor, "HandleTeamCommand", "rule coc1", memberMsg);
        Assert.Contains("只有队伍创建者", Assert.Single(h.Replies));
        Assert.Equal("coc3", ResolveRule(h.Processor, UserId, h.Message(".cc"), "coc").Rule);
        memberMsg.IsGroupAdmin = true;
        InvokeHandler(h.Processor, "HandleTeamCommand", "rule coc1", memberMsg);
        Assert.Equal("coc1", ResolveRule(h.Processor, UserId, h.Message(".cc"), "coc").Rule);
        InvokeHandler(h.Processor, "HandleTeamCommand", "rule reset coc", h.Message(".team rule reset coc"));
        Assert.Equal("coc2", ResolveRule(h.Processor, UserId, h.Message(".cc"), "coc").Rule);
        Assert.Equal("coc2", ResolveRule(h.Processor, UserId + 1, memberMsg, "coc").Rule);
    }

    [Fact]
    public void PersonalAndTeamRules_SurviveRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mdice-check-rules-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "test.db");
        try
        {
            using (var io = new TestDataIO(path))
            {
                var h = new CommandHarness(CreateProcessor(io.Value));
                InvokeHandler(h.Processor, "HandleConfigCommand", "rule coc2", h.Message(".cfg rule coc2"));
                InvokeHandler(h.Processor, "HandleConfigCommand", "rule dnd1", h.Message(".cfg rule dnd1"));
                InvokeHandler(h.Processor, "HandleTeamCommand", "new 测试团", h.Message(".team new 测试团"));
                InvokeHandler(h.Processor, "HandleTeamCommand", "rule coc3", h.Message(".team rule coc3"));
                InvokeHandler(h.Processor, "HandleTeamCommand", "rule dnd2", h.Message(".team rule dnd2"));
            }
            using (var io = new TestDataIO(path))
            {
                var h = new CommandHarness(CreateProcessor(io.Value));
                InvokeNoArg(h.Processor, "LoadUserData");
                InvokeNoArg(h.Processor, "LoadGroupData");
                Assert.Equal("coc3", ResolveRule(h.Processor, UserId, h.Message(".cc"), "coc").Rule);
                Assert.Equal("dnd2", ResolveRule(h.Processor, UserId, h.Message(".cc"), "dnd").Rule);
                InvokeHandler(h.Processor, "HandleTeamCommand", "rule reset coc", h.Message(".team rule reset coc"));
                Assert.Equal("coc2", ResolveRule(h.Processor, UserId, h.Message(".cc"), "coc").Rule);
                var privateMsg = new Msg(0, UserId, ".cc", MessageSource.privatechat);
                Assert.Equal("dnd1", ResolveRule(h.Processor, UserId, privateMsg, "dnd").Rule);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Help_ContainsRuleDefinitionsAndTeamPrecedence()
    {
        var h = new CommandHarness();
        h.Processor.OnHandleMessage(h.Message(".help cfg rule"));
        Assert.Contains("技能<50", h.Replies.Last());
        Assert.Contains("coc3", h.Replies.Last());
        h.Processor.OnHandleMessage(h.Message(".help team rule"));
        Assert.Contains(".team rule reset coc", h.Replies.Last());
        Assert.Contains("对抗双方", h.Replies.Last());
    }

    [Fact]
    public void LegacyDefaultHelp_IsUpgradedAndCustomHelpIsPreserved()
    {
        var method = typeof(GlobalFeedbackMessages).GetMethod("UpgradeLegacyRuleHelp", BindingFlags.NonPublic | BindingFlags.Static)!;
        string oldCfg = "【.cfg 个人设置】：显示当前默认骰面、全部人物卡名称以及 .td call 私聊接收状态。示例：.cfg。";
        string oldTeam = "【.team 队伍管理】：支持 new、add、era、join、del、call、sort、list、set 子命令。.team call 会立即在群内 @ 队伍成员，并依序私聊未关闭 .td call 的成员。";
        Assert.Contains(".cfg rule", (string)method.Invoke(null, new object[] { "cfg", oldCfg })!);
        Assert.Contains(".team rule", (string)method.Invoke(null, new object[] { "team", oldTeam })!);
        Assert.Equal("自定义帮助", method.Invoke(null, new object[] { "cfg", "自定义帮助" }));
    }

    private static (string Rule, string Source) ResolveRule(MessageProcessor p, long userId, Msg msg, string family, long? context = null)
        => ((string, string))InvokeMethod(p, "ResolveCheckRule", userId, msg, family, context!)!;

    [Theory]
    [InlineData("")]
    [InlineData(" #2 _l")]
    [InlineData(" _v @42002")]
    public void CheckCommand_UsesTeamRulesIncludingLoopRepeatsAndVersus(string suffix)
    {
        var h = new CommandHarness();
        foreach (long id in new[] { UserId, UserId + 1 })
        {
            h.Processor.ImportCharacterCard(id, new MessageProcessor.CharacterSheet
            {
                Name = "测试卡", Skills = new ConcurrentDictionary<string, int>(new Dictionary<string, int> { ["侦查"] = 100 })
            });
        }
        InvokeHandler(h.Processor, "HandleTeamCommand", "new 测试团", h.Message(".team new 测试团"));
        InvokeHandler(h.Processor, "HandleTeamCommand", "rule coc2", h.Message(".team rule coc2"));
        h.Replies.Clear();
        // 固定首个检定为96，确保实际走到 coc1/coc2 的差异点，避免概率测试。
        int seed = Enumerable.Range(0, 10000).First(s =>
        {
            var rng = new Random(s);
            return rng.Next(1, 11) == 6 && rng.Next(1, 11) == 9;
        });
        GlobalRandom.SetSeed(seed);
        h.Processor.OnHandleMessage(h.Message(".cc{coc7}(测试卡)侦查100" + suffix));
        string text = Assert.Single(h.Replies);
        Assert.Contains("96/100", text);
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"D100=(\d+)/(\d+).*?-> ([^\r\n]+)");
        Assert.True(matches.Count >= (suffix.Contains("_l") ? 3 : suffix.Contains("_v") ? 3 : 1), text);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            int roll = int.Parse(match.Groups[1].Value);
            int skill = int.Parse(match.Groups[2].Value);
            Assert.Equal(Dice.CoC7_Check(roll, skill, "coc2"), match.Groups[3].Value.Trim());
        }
    }

    private static int GetDefaultDice(MessageProcessor processor)
    {
        return (int)InvokeMethod(processor, "GetUserDefaultDice", UserId)!;
    }

    private static void InvokeHandler(MessageProcessor processor, string name, string args, Msg msg)
    {
        InvokeMethod(processor, name, args, msg);
    }

    private static void InvokeNoArg(MessageProcessor processor, string name)
    {
        InvokeMethod(processor, name);
    }

    private static object? InvokeMethod(MessageProcessor processor, string name, params object[] args)
    {
        var method = typeof(MessageProcessor).GetMethod(name, NonPublicInstance)
            ?? throw new InvalidOperationException($"{name} not found.");
        return method.Invoke(processor, args);
    }

    private static object? InvokeStaticMethod(string name, params object[] args)
    {
        var method = typeof(MessageProcessor).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} not found.");
        return method.Invoke(null, args);
    }

    private static void SetPrivateField(MessageProcessor processor, string name, object value)
    {
        var field = typeof(MessageProcessor).GetField(name, NonPublicInstance)
            ?? throw new InvalidOperationException($"{name} not found.");
        field.SetValue(processor, value);
    }

    private static void SetPrivateProperty(MessageProcessor processor, string name, object value)
    {
        var property = typeof(MessageProcessor).GetProperty(name, NonPublicInstance)
            ?? throw new InvalidOperationException($"{name} not found.");
        property.SetValue(processor, value);
    }

    private static void ResetTeamCallSendState()
    {
        var field = typeof(MessageProcessor).GetField(
            "_hasSentTeamCallPrivateMessage",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_hasSentTeamCallPrivateMessage not found.");
        field.SetValue(null, false);
    }

    private sealed class CommandHarness
    {
        public CommandHarness(MessageProcessor? processor = null)
        {
            Processor = processor ?? new MessageProcessor();
            var distribution = new MessageDistribution();
            distribution.OnReplySent += (content, _) => Replies.Add(content);
            distribution.MessageProcessor = Processor;
            Processor.MessageDistribution = distribution;
        }

        public MessageProcessor Processor { get; }
        public List<string> Replies { get; } = new();

        public Msg Message(string content) => new(GroupId, UserId, content, MessageSource.group);
    }

    private sealed class TestDataIO : IDisposable
    {
        public TestDataIO(string path) => Value = new DataIO(path);
        public DataIO Value { get; }
        public void Dispose() => Value.Close();
    }
}
