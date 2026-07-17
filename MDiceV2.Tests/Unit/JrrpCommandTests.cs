using System.Reflection;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class JrrpCommandTests
{
    private const long TestGroupId = 10001;
    private const long TestUserId = 20002;

    private static readonly MethodInfo GetOrCreateDailyJrrpResultMethod =
        typeof(MessageProcessor).GetMethod(
            "GetOrCreateDailyJrrpResult",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GetOrCreateDailyJrrpResult not found.");

    private static readonly MethodInfo HandleJrrpCommandMethod =
        typeof(MessageProcessor).GetMethod(
            "HandleJrrpCommand",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("HandleJrrpCommand not found.");

    private static readonly PropertyInfo DataIoProperty =
        typeof(MessageProcessor).GetProperty(nameof(MessageProcessor.DataIO))
        ?? throw new InvalidOperationException("DataIO property not found.");

    [Fact]
    public void SameUserAndDate_ReusesFirstResult()
    {
        using var database = new TemporaryDatabase();
        var processor = CreateProcessor(database.DataIO);
        var date = new DateOnly(2026, 7, 16);
        int factoryCalls = 0;

        string first = GetOrCreate(processor, TestUserId, date, () =>
        {
            factoryCalls++;
            return "今日运势：88";
        });
        string second = GetOrCreate(processor, TestUserId, date, () =>
        {
            factoryCalls++;
            return "今日运势：1";
        });

        Assert.Equal("今日运势：88", first);
        Assert.Equal(first, second);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void PersistedResult_IsReusedAfterProcessorRestart()
    {
        string databasePath = TemporaryDatabase.CreatePath(out string directory);
        try
        {
            var date = new DateOnly(2026, 7, 16);
            using (var firstDatabase = new TemporaryDatabase(databasePath, directory, ownsDirectory: false))
            {
                var firstProcessor = CreateProcessor(firstDatabase.DataIO);
                Assert.Equal(
                    "今日运势：66",
                    GetOrCreate(firstProcessor, TestUserId, date, () => "今日运势：66"));
            }

            int factoryCalls = 0;
            using (var secondDatabase = new TemporaryDatabase(databasePath, directory, ownsDirectory: false))
            {
                var secondProcessor = CreateProcessor(secondDatabase.DataIO);
                string result = GetOrCreate(secondProcessor, TestUserId, date, () =>
                {
                    factoryCalls++;
                    return "今日运势：2";
                });

                Assert.Equal("今日运势：66", result);
                Assert.Equal(0, factoryCalls);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NewDate_GeneratesNewResult()
    {
        using var database = new TemporaryDatabase();
        var processor = CreateProcessor(database.DataIO);

        string first = GetOrCreate(
            processor,
            TestUserId,
            new DateOnly(2026, 7, 16),
            () => "今日运势：25");
        string nextDay = GetOrCreate(
            processor,
            TestUserId,
            new DateOnly(2026, 7, 17),
            () => "今日运势：91");

        Assert.Equal("今日运势：25", first);
        Assert.Equal("今日运势：91", nextDay);
    }

    [Fact]
    public void CommandRepliesWithStoredResult()
    {
        using var database = new TemporaryDatabase();
        var processor = CreateProcessor(database.DataIO);
        var replies = new List<string>();
        var distribution = new MessageDistribution();
        distribution.OnReplySent += (content, _) => replies.Add(content);
        distribution.MessageProcessor = processor;
        processor.MessageDistribution = distribution;

        var today = DateOnly.FromDateTime(DateTime.Now);
        GetOrCreate(processor, TestUserId, today, () => "今日运势：固定结果");

        var msg = new Msg(TestGroupId, TestUserId, ".jrrp", MessageSource.group);
        HandleJrrpCommandMethod.Invoke(processor, new object[] { string.Empty, msg });

        Assert.Equal("今日运势：固定结果", Assert.Single(replies));
    }

    private static MessageProcessor CreateProcessor(DataIO dataIO)
    {
        var processor = new MessageProcessor();
        DataIoProperty.SetValue(processor, dataIO);
        return processor;
    }

    private static string GetOrCreate(
        MessageProcessor processor,
        long userId,
        DateOnly date,
        Func<string> resultFactory)
    {
        return (string)(GetOrCreateDailyJrrpResultMethod.Invoke(
            processor,
            new object[] { userId, date, resultFactory })
            ?? throw new InvalidOperationException("Daily jrrp result was null."));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory;
        private readonly bool _ownsDirectory;

        public TemporaryDatabase()
        {
            string path = CreatePath(out _directory);
            _ownsDirectory = true;
            DataIO = new DataIO(path);
        }

        public TemporaryDatabase(string path, string directory, bool ownsDirectory)
        {
            _directory = directory;
            _ownsDirectory = ownsDirectory;
            DataIO = new DataIO(path);
        }

        public DataIO DataIO { get; }

        public static string CreatePath(out string directory)
        {
            directory = Path.Combine(Path.GetTempPath(), $"mdice-jrrp-tests-{Guid.NewGuid():N}");
            return Path.Combine(directory, "test.db");
        }

        public void Dispose()
        {
            DataIO.Close();
            if (_ownsDirectory && Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
