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
}
