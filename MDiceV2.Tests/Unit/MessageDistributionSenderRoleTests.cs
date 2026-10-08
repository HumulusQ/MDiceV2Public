using System.Reflection;
using System.Text.Json;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class MessageDistributionSenderRoleTests
{
    private static readonly MethodInfo HandleMessageEventMethod =
        typeof(MessageDistribution).GetMethod(
            "HandleMessageEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MessageDistribution.HandleMessageEvent not found.");

    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("member", false)]
    public void GroupMessage_UpdatesAdministratorStateFromSenderRole(
        string role,
        bool expectedIsAdministrator)
    {
        const long groupId = 77889931;
        const long userId = 77880001;
        var distribution = new MessageDistribution
        {
            OnGroupMessage = null
        };
        (long GroupId, long UserId, bool IsAdministrator)? observed = null;
        distribution.OnGroupAdmin = (eventGroupId, eventUserId, isAdministrator) =>
            observed = (eventGroupId, eventUserId, isAdministrator);

        using var document = JsonDocument.Parse(
            $$"""
            {
              "message_type": "group",
              "group_id": {{groupId}},
              "user_id": {{userId}},
              "message": ".cr off",
              "sender": { "role": "{{role}}" }
            }
            """);

        HandleMessageEventMethod.Invoke(distribution, new object[] { document.RootElement });

        Assert.Equal((groupId, userId, expectedIsAdministrator), observed);
    }

    [Fact]
    public void GroupMessage_IgnoresUnknownSenderRole()
    {
        var distribution = new MessageDistribution
        {
            OnGroupMessage = null
        };
        bool raised = false;
        distribution.OnGroupAdmin = (_, _, _) => raised = true;

        using var document = JsonDocument.Parse(
            """
            {
              "message_type": "group",
              "group_id": 77889931,
              "user_id": 77880001,
              "message": ".cr off",
              "sender": { "role": "unexpected" }
            }
            """);

        HandleMessageEventMethod.Invoke(distribution, new object[] { document.RootElement });

        Assert.False(raised);
    }

    [Fact]
    public void GroupMessage_SentByBotIsIgnoredBeforeFocusedOrNormalDispatch()
    {
        var distribution = new MessageDistribution
        {
            OnGroupMessage = null
        };
        bool administratorEventRaised = false;
        distribution.OnGroupAdmin = (_, _, _) => administratorEventRaised = true;

        using var document = JsonDocument.Parse(
            """
            {
              "message_type": "group",
              "self_id": 77880001,
              "group_id": 77889931,
              "user_id": 77880001,
              "message": "检测到人物卡，回复 y 确认",
              "sender": { "role": "owner" }
            }
            """);

        HandleMessageEventMethod.Invoke(distribution, new object[] { document.RootElement });

        Assert.False(administratorEventRaised);
    }

    [Fact]
    public void GroupMessage_WithFileSegment_IsDispatchedOnlyAsFile_NotAsConfirmationText()
    {
        const long groupId = 77889931;
        const long userId = 77880001;
        var distribution = new MessageDistribution
        {
            OnGroupMessage = null
        };
        OneBotFileInfo? observedFile = null;
        var normalMessageRaised = false;
        distribution.OnFileMessage = file => observedFile = file;
        distribution.OnGroupMessage = (_, _, _, _, _) => normalMessageRaised = true;

        using var document = JsonDocument.Parse(
            $$"""
            {
              "message_type": "group",
              "group_id": {{groupId}},
              "user_id": {{userId}},
              "message": [
                {
                  "type": "file",
                  "data": {
                    "file_id": "card-file-id",
                    "file_name": "CoC7_舍空_2026-09-25.mdice (1).html",
                    "file_size": 12345
                  }
                }
              ]
            }
            """);

        HandleMessageEventMethod.Invoke(distribution, new object[] { document.RootElement });

        Assert.NotNull(observedFile);
        Assert.Equal("CoC7_舍空_2026-09-25.mdice (1).html", observedFile!.FileName);
        Assert.False(normalMessageRaised);
    }

}
