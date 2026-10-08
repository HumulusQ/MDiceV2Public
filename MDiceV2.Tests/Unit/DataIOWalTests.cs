using System.Data.SQLite;
using FluentAssertions;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class DataIOWalTests
{
    [Fact]
    public void Opening_database_recovers_and_truncates_wal_left_by_interrupted_run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdice-wal-recovery-test-{Guid.NewGuid():N}");
        var liveRoot = Path.Combine(root, "live");
        var recoveredRoot = Path.Combine(root, "recovered");
        Directory.CreateDirectory(liveRoot);
        Directory.CreateDirectory(recoveredRoot);
        var livePath = Path.Combine(liveRoot, "test.db");
        var recoveredPath = Path.Combine(recoveredRoot, "test.db");
        DataIO? recovered = null;

        try
        {
            using (var connection = new SQLiteConnection($"Data Source={livePath};Version=3;Pooling=False;"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA wal_autocheckpoint=0;
                    CREATE TABLE DataStore (key TEXT PRIMARY KEY, value TEXT, updated_at INTEGER DEFAULT 0);
                    INSERT INTO DataStore (key, value, updated_at) VALUES ('recover-me', 'preserved', 1);
                    """;
                command.ExecuteNonQuery();

                File.Copy(livePath, recoveredPath);
                File.Copy(livePath + "-wal", recoveredPath + "-wal");
            }

            recovered = new DataIO(recoveredPath);
            recovered.ReadData("DataStore", "recover-me").Should().Be("preserved");
            var recoveredWalSize = File.Exists(recoveredPath + "-wal")
                ? new FileInfo(recoveredPath + "-wal").Length
                : 0;
            recoveredWalSize.Should().Be(0, "startup should checkpoint a WAL from an interrupted run");
        }
        finally
        {
            recovered?.Close();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Repeated_unchanged_saves_do_not_grow_wal_and_close_truncates_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdice-wal-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "test.db");
        var walPath = databasePath + "-wal";
        DataIO? data = null;

        try
        {
            data = new DataIO(databasePath);
            data.SaveData("Settings", "same", "value");
            data.SaveBlob("Binary", "same", [1, 2, 3, 4]);
            data.Close();
            data = null;

            data = new DataIO(databasePath);
            for (var i = 0; i < 5_000; i++)
            {
                data.SaveData("Settings", "same", "value");
                data.SaveBlob("Binary", "same", [1, 2, 3, 4]);
            }

            data.SaveDataBatch("Settings", Enumerable.Repeat(("same", "value"), 500));
            data.SaveDataBatchIfChanged("Settings", [("same", "value")]).Should().Be(0);

            var liveWalSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
            liveWalSize.Should().BeLessThan(64 * 1024, "unchanged values should not append WAL frames");

            data.SaveData("Settings", "same", "changed");
            data.Close();
            data = null;

            var closedWalSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
            closedWalSize.Should().Be(0, "a clean shutdown should checkpoint and truncate the WAL");

            using var connection = new SQLiteConnection($"Data Source={databasePath};Version=3;Pooling=False;");
            connection.Open();
            using var command = new SQLiteCommand("PRAGMA quick_check;", connection);
            command.ExecuteScalar()?.ToString().Should().Be("ok");
        }
        finally
        {
            data?.Close();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
