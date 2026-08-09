using System.Reflection;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class BotCommandTests
{
    private const long GroupId = 41001;
    private const long UserId = 42002;

    private static readonly MethodInfo HandleBotMethod =
        typeof(MessageProcessor).GetMethod(
            "HandleBot",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("HandleBot not found.");

    [Fact]
    public void PrivateBareBot_ReturnsPrivateStatusWithoutTrustDetails()
    {
        var (processor, replies) = CreateHarness();
        var message = new Msg(0, UserId, ".bot", MessageSource.privatechat);

        Invoke(processor, string.Empty, message);

        var reply = Assert.Single(replies);
        Assert.Contains("当前状态", reply);
        Assert.DoesNotContain("信任度", reply);
        Assert.DoesNotContain("使用 .bot on/off", reply);
    }

    [Fact]
    public void PrivateBotToggle_IsRejectedAndDoesNotChangePrivateState()
    {
        var (processor, replies) = CreateHarness();
        var message = new Msg(0, UserId, ".bot off", MessageSource.privatechat);

        Invoke(processor, "off", message);

        Assert.True(processor.IsBotEnabled(-UserId));
        Assert.Contains("群聊", Assert.Single(replies));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GroupBotToggle_AllowsWhitelistLevelsZeroAndOne(int authLevel)
    {
        var (processor, _) = CreateHarness();
        var message = new Msg(GroupId, UserId, ".bot off", MessageSource.group)
        {
            UserAuthLevel = authLevel,
            IsGroupAdmin = false
        };

        Invoke(processor, "off", message);

        Assert.False(processor.IsBotEnabled(GroupId));
    }

    [Fact]
    public void GroupBotToggle_AllowsMaster()
    {
        var (processor, _) = CreateHarness();
        var message = new Msg(GroupId, UserId, ".bot off", MessageSource.group)
        {
            IsMasterAccount = true,
            IsGroupAdmin = false
        };

        Invoke(processor, "off", message);

        Assert.False(processor.IsBotEnabled(GroupId));
    }

    [Fact]
    public void GroupBotToggle_RejectsHigherWhitelistLevel()
    {
        var (processor, replies) = CreateHarness();
        var message = new Msg(GroupId, UserId, ".bot off", MessageSource.group)
        {
            UserAuthLevel = 2,
            IsGroupAdmin = false
        };

        Invoke(processor, "off", message);

        Assert.True(processor.IsBotEnabled(GroupId));
        Assert.Contains("群管理员", Assert.Single(replies));
    }

    private static (MessageProcessor Processor, List<string> Replies) CreateHarness()
    {
        var processor = new MessageProcessor();
        var distribution = new MessageDistribution
        {
            MessageProcessor = processor
        };
        var replies = new List<string>();
        distribution.OnReplySent += (content, _) => replies.Add(content);
        processor.MessageDistribution = distribution;
        return (processor, replies);
    }

    private static void Invoke(MessageProcessor processor, string args, Msg message)
    {
        HandleBotMethod.Invoke(processor, new object[] { args, message });
    }
}
