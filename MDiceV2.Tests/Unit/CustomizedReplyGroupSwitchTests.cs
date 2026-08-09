using System.Reflection;
using System.Text.Json;
using CustomizedReply;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Moq;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class CustomizedReplyGroupSwitchTests
{
    private const long GroupId = 123456;
    private const long UserId = 654321;

    private static readonly FieldInfo IsEnabledField =
        typeof(CustomizedReplyMod).GetField("_isEnabled", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CustomizedReply _isEnabled field not found.");

    private static readonly MethodInfo LoadGroupDataMethod =
        typeof(MessageProcessor).GetMethod("LoadGroupData", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MessageProcessor.LoadGroupData not found.");

    private static readonly PropertyInfo DataIoProperty =
        typeof(MessageProcessor).GetProperty(nameof(MessageProcessor.DataIO))
        ?? throw new InvalidOperationException("MessageProcessor.DataIO property not found.");

    [Fact]
    public void NonAdministrator_CannotChangeGroupSwitch()
    {
        bool enabled = true;
        var context = CreateContext(() => enabled, value => enabled = value);
        var mod = CreateEnabledMod(context.Object);

        var result = mod.OnGroupMessage(GroupId, UserId, ".cr off", isAted: false);

        Assert.NotNull(result);
        Assert.Contains("权限不足", result.Reply);
        Assert.True(enabled);
        context.Verify(x => x.SetGroupFeatureEnabled(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void GroupAdministrator_CanDisableAndReenableReplies()
    {
        bool enabled = true;
        var context = CreateContext(() => enabled, value => enabled = value);
        context.Setup(x => x.IsGroupAdministrator(GroupId, UserId)).Returns(true);
        var mod = CreateEnabledMod(context.Object);
        mod.AddRuleDirectly(new ReplyRule
        {
            Trigger = "hello",
            MatchType = CustomizedReply.MatchType.Exact,
            Replies = new List<string> { "world" }
        });

        var disableResult = mod.OnGroupMessage(GroupId, UserId, ".customreply off", isAted: false);

        Assert.NotNull(disableResult);
        Assert.Contains("已关闭", disableResult.Reply);
        Assert.False(enabled);
        Assert.Null(mod.OnGroupMessage(GroupId, UserId, "hello", isAted: false));

        var enableResult = mod.OnGroupMessage(GroupId, UserId, ".cr on", isAted: false);

        Assert.NotNull(enableResult);
        Assert.Contains("已开启", enableResult.Reply);
        Assert.True(enabled);
        Assert.Equal("world", mod.OnGroupMessage(GroupId, UserId, "hello", isAted: false)?.Reply);
    }

    [Fact]
    public void DiceAdministrator_CanChangeGroupSwitch()
    {
        bool enabled = true;
        var context = CreateContext(() => enabled, value => enabled = value);
        context.Setup(x => x.IsDiceAdministrator(UserId)).Returns(true);
        var mod = CreateEnabledMod(context.Object);

        var result = mod.OnGroupMessage(GroupId, UserId, ".cr off", isAted: false);

        Assert.NotNull(result);
        Assert.Contains("已关闭", result.Reply);
        Assert.False(enabled);
    }

    [Fact]
    public void Switch_IsStoredInsideGroupData_AndReloaded()
    {
        using var database = new TemporaryDatabase();
        var firstProcessor = CreateProcessor(database.DataIO);

        Assert.True(firstProcessor.IsGroupFeatureEnabled(GroupId, "customizedReply"));
        firstProcessor.SetGroupFeatureEnabled(GroupId, "customizedReply", enabled: false);

        string persisted = database.DataIO.ReadData("GroupData", GroupId.ToString())
            ?? throw new InvalidOperationException("GroupData record was not persisted.");
        using (var document = JsonDocument.Parse(persisted))
        {
            Assert.False(document.RootElement
                .GetProperty("FeatureSwitches")
                .GetProperty("customizedreply")
                .GetBoolean());
        }

        var reloadedProcessor = CreateProcessor(database.DataIO);
        LoadGroupDataMethod.Invoke(reloadedProcessor, null);

        Assert.False(reloadedProcessor.IsGroupFeatureEnabled(GroupId, "customizedReply"));
    }

    private static Mock<IModContext> CreateContext(Func<bool> readState, Action<bool> writeState)
    {
        var context = new Mock<IModContext>();
        context
            .Setup(x => x.IsGroupFeatureEnabled(GroupId, "customizedReply", true))
            .Returns(readState);
        context
            .Setup(x => x.SetGroupFeatureEnabled(GroupId, "customizedReply", It.IsAny<bool>()))
            .Callback<long, string, bool>((_, _, value) => writeState(value));
        context.Setup(x => x.IsGroupAdministrator(GroupId, UserId)).Returns(false);
        context.Setup(x => x.IsDiceAdministrator(UserId)).Returns(false);
        return context;
    }

    private static CustomizedReplyMod CreateEnabledMod(IModContext context)
    {
        var mod = new CustomizedReplyMod(context);
        IsEnabledField.SetValue(mod, true);
        return mod;
    }

    private static MessageProcessor CreateProcessor(DataIO dataIO)
    {
        var processor = new MessageProcessor();
        DataIoProperty.SetValue(processor, dataIO);
        return processor;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory;

        public TemporaryDatabase()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"mdice-custom-reply-tests-{Guid.NewGuid():N}");
            DataIO = new DataIO(Path.Combine(_directory, "test.db"));
        }

        public DataIO DataIO { get; }

        public void Dispose()
        {
            DataIO.Close();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
