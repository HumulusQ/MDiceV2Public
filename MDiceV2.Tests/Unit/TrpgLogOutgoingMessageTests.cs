using System.Reflection;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class TrpgLogOutgoingMessageTests
{
    private static readonly FieldInfo TrpgLogManagerField =
        typeof(MessageProcessor).GetField(
            "_trpgLogManager",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_trpgLogManager not found.");

    [Fact]
    public void GroupReply_IsWrittenToActiveTrpgLog()
    {
        long groupId = 51000 + Random.Shared.Next(1, 9000);
        long starterId = 52001;
        string logName = $"outgoing-{Guid.NewGuid():N}";
        var logManager = new TRPGLogManager();
        string? logPath = null;

        try
        {
            Assert.NotEqual(LogStartResult.Failed, logManager.StartLog(groupId, starterId, logName));
            logPath = logManager.GetLogPath(groupId, logName, starterId);
            Assert.False(string.IsNullOrWhiteSpace(logPath));

            var processor = new MessageProcessor();
            TrpgLogManagerField.SetValue(processor, logManager);
            processor._logEnabledStates[groupId] = true;

            var distribution = new MessageDistribution
            {
                MessageProcessor = processor
            };
            processor.MessageDistribution = distribution;

            distribution.Reply("骰子程序回复", new Msg(
                groupId,
                53001,
                ".r 1d100",
                MessageSource.group));

            logManager.StopLog(groupId);
            Assert.Contains("骰子程序回复", File.ReadAllText(logPath!));
        }
        finally
        {
            if (logManager.IsLogRecording(groupId))
            {
                logManager.StopLog(groupId);
            }

            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public void GetLastNLogEntries_ReturnsTheTrailingEntriesInChronologicalOrder()
    {
        long groupId = 61000 + Random.Shared.Next(1, 9000);
        long starterId = 62001;
        string logName = $"review-tail-{Guid.NewGuid():N}";
        var logManager = new TRPGLogManager();
        string? logPath = null;

        try
        {
            Assert.NotEqual(LogStartResult.Failed, logManager.StartLog(groupId, starterId, logName));
            logPath = logManager.GetLogPath(groupId, logName, starterId);

            for (int i = 1; i <= 200; i++)
            {
                logManager.WriteLog(groupId, 70000 + i, $"sender-{i}", new string('x', 512) + $" message-{i}");
            }

            var entries = logManager.GetLastNLogEntries(groupId, logName, starterId, 50);

            Assert.Equal(50, entries.Count);
            Assert.Equal((long)70151, entries[0].userId);
            Assert.Equal("sender-151", entries[0].senderName);
            Assert.EndsWith("message-151", entries[0].content);
            Assert.Equal((long)70200, entries[^1].userId);
            Assert.Equal("sender-200", entries[^1].senderName);
            Assert.EndsWith("message-200", entries[^1].content);
        }
        finally
        {
            if (logManager.IsLogRecording(groupId))
            {
                logManager.StopLog(groupId);
            }

            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }
}
