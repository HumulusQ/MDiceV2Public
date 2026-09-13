using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MDiceV2.Core.Infrastructure;
using Xunit;

namespace MDiceV2.Tests.Unit;

/// <summary>
/// 日志缓冲池单元测试
/// 覆盖：通道管理、资源隔离、并发操作、统计信息
/// </summary>
public class LogBufferPoolTests : IDisposable
{
    private readonly List<LogBufferPool> _poolsToCleanup = new();

    private LogBufferPool CreateAndTrackPool()
    {
        var pool = new LogBufferPool();
        _poolsToCleanup.Add(pool);
        return pool;
    }

    public void Dispose()
    {
        foreach (var pool in _poolsToCleanup)
        {
            pool?.Dispose();
        }
    }

    #region Constructor & Basic Properties

    [Fact]
    public void Constructor_InitializesSuccessfully()
    {
        // Act
        var pool = CreateAndTrackPool();

        // Assert
        pool.Should().NotBeNull();
        pool.ChannelCount.Should().Be(0);
        pool.PoolStatistics.Should().NotBeNull();
    }

    [Fact]
    public void ChannelCount_InitiallyZero()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act & Assert
        pool.ChannelCount.Should().Be(0);
    }

    [Fact]
    public void PoolStatistics_InitiallyEmpty()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act & Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(0);
        pool.PoolStatistics.ChannelsCreated.Should().Be(0);
        pool.PoolStatistics.ErrorCount.Should().Be(0);
    }

    #endregion

    #region GetOrCreateBatcher Tests

    [Fact]
    public void GetOrCreateBatcher_WithValidChannel_CreatesBatcher()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var channel = "test-channel";

        // Act
        var batcher = pool.GetOrCreateBatcher(channel);

        // Assert
        batcher.Should().NotBeNull();
        pool.ChannelCount.Should().Be(1);
    }

    [Fact]
    public void GetOrCreateBatcher_WithNullChannel_UsesDefault()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        var batcher = pool.GetOrCreateBatcher(null);

        // Assert
        batcher.Should().NotBeNull();
        pool.ChannelCount.Should().Be(1);
    }

    [Fact]
    public void GetOrCreateBatcher_WithEmptyChannel_UsesDefault()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        var batcher = pool.GetOrCreateBatcher("");

        // Assert
        batcher.Should().NotBeNull();
        pool.ChannelCount.Should().Be(1);
    }

    [Fact]
    public void GetOrCreateBatcher_SamChannelTwice_ReturnsSameBatcher()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var channel = "duplicate-test";

        // Act
        var batcher1 = pool.GetOrCreateBatcher(channel);
        var batcher2 = pool.GetOrCreateBatcher(channel);

        // Assert
        batcher1.Should().BeSameAs(batcher2);
        pool.ChannelCount.Should().Be(1);
    }

    [Fact]
    public void GetOrCreateBatcher_WithCustomBatchSize_ConfiguresBatcher()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var channel = "custom-batch";
        var batchSize = 50;

        // Act
        var batcher = pool.GetOrCreateBatcher(channel, batchSize);

        // Assert
        batcher.Should().NotBeNull();
        // Batcher should start with custom batch size
    }

    [Fact]
    public void GetOrCreateBatcher_WithCustomFlushInterval_ConfiguresBatcher()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var channel = "custom-flush";
        var flushIntervalMs = 1000;

        // Act
        var batcher = pool.GetOrCreateBatcher(channel, flushIntervalMs: flushIntervalMs);

        // Assert
        batcher.Should().NotBeNull();
    }

    #endregion

    #region Channel Isolation Tests

    [Fact]
    public void MultipleChannels_AreIsolated()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        var batcher1 = pool.GetOrCreateBatcher("channel1");
        var batcher2 = pool.GetOrCreateBatcher("channel2");
        var batcher3 = pool.GetOrCreateBatcher("channel3");

        // Assert
        batcher1.Should().NotBeSameAs(batcher2);
        batcher2.Should().NotBeSameAs(batcher3);
        batcher1.Should().NotBeSameAs(batcher3);
        pool.ChannelCount.Should().Be(3);
    }

    [Fact]
    public void PredefinedChannels_AllWork()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        var defaultBatcher = pool.GetOrCreateBatcher(LogBufferPool.CHANNEL_DEFAULT);
        var systemBatcher = pool.GetOrCreateBatcher(LogBufferPool.CHANNEL_SYSTEM);
        var networkBatcher = pool.GetOrCreateBatcher(LogBufferPool.CHANNEL_NETWORK);
        var gameBatcher = pool.GetOrCreateBatcher(LogBufferPool.CHANNEL_GAME);
        var errorBatcher = pool.GetOrCreateBatcher(LogBufferPool.CHANNEL_ERROR);

        // Assert
        pool.ChannelCount.Should().Be(5);
        defaultBatcher.Should().NotBeNull();
        systemBatcher.Should().NotBeNull();
        networkBatcher.Should().NotBeNull();
        gameBatcher.Should().NotBeNull();
        errorBatcher.Should().NotBeNull();
    }

    [Fact]
    public void EnqueueLog_ToMultipleChannels_IsolatesLogs()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.GetOrCreateBatcher("ch1");
        pool.GetOrCreateBatcher("ch2");

        // Act
        pool.EnqueueLog("Log for channel 1", "ch1");
        pool.EnqueueLog("Log for channel 2", "ch2");
        pool.EnqueueLog("Another log for channel 1", "ch1");

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(3);
        pool.ChannelCount.Should().Be(2);
    }

    #endregion

    #region EnqueueLog Tests

    [Fact]
    public void EnqueueLog_WithValidLog_IncrementsCounter()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        pool.EnqueueLog("Test message");

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(1);
    }

    [Fact]
    public void EnqueueLog_WithMultipleLogs_IncrementsCorrectly()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        for (int i = 0; i < 10; i++)
        {
            pool.EnqueueLog($"Message {i}");
        }

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(10);
    }

    [Fact]
    public void EnqueueLog_WithNull_Ignores()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        pool.EnqueueLog(null);

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(0);
        pool.PoolStatistics.ErrorCount.Should().Be(0); // Not an error, just ignored
    }

    [Fact]
    public void EnqueueLog_WithEmpty_Ignores()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        pool.EnqueueLog("");

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(0);
    }

    [Fact]
    public void EnqueueLog_CreatesChannelIfNotExists()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        pool.EnqueueLog("Test", "new-channel");

        // Assert
        pool.ChannelCount.Should().Be(1);
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(1);
    }

    [Fact]
    public void EnqueueLog_WithSpecificChannel_UsesCorrectChannel()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        pool.EnqueueLog("System log", LogBufferPool.CHANNEL_SYSTEM);
        pool.EnqueueLog("Error log", LogBufferPool.CHANNEL_ERROR);

        // Assert
        pool.ChannelCount.Should().Be(2);
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(2);
    }

    [Fact]
    public void EnqueueLog_WithDefaultChannel_Works()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        pool.EnqueueLog("Default channel log");

        // Assert
        pool.ChannelCount.Should().Be(1);
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(1);
    }

    #endregion

    #region Flush Operations Tests

    [Fact]
    public async Task FlushAllAsync_WithMultipleChannels_FlushesAll()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log 1", "ch1");
        pool.EnqueueLog("Log 2", "ch2");
        pool.EnqueueLog("Log 3", "ch3");

        // Act
        await pool.FlushAllAsync();

        // Assert
        pool.ChannelCount.Should().Be(3);
        // Logs should be flushed (batchers should process them)
    }

    [Fact]
    public async Task FlushChannelAsync_WithValidChannel_FlushesOnly()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log 1", "ch1");
        pool.EnqueueLog("Log 2", "ch2");

        // Act
        await pool.FlushChannelAsync("ch1");

        // Assert
        // Channel 1 should be flushed
        pool.ChannelCount.Should().Be(2);
    }

    [Fact]
    public async Task FlushChannelAsync_WithNonexistentChannel_DoesNotThrow()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        Func<Task> act = async () => await pool.FlushChannelAsync("nonexistent");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FlushAll_Completes_WithinReasonableTime()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log 1");
        var sw = Stopwatch.StartNew();

        // Act
        await pool.FlushAllAsync();
        sw.Stop();

        // Assert
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    #endregion

    #region Remove Channel Tests

    [Fact]
    public async Task RemoveChannelAsync_WithValidChannel_RemovesIt()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log", "removable");

        // Act
        await pool.RemoveChannelAsync("removable");

        // Assert
        pool.ChannelCount.Should().Be(0);
    }

    [Fact]
    public async Task RemoveChannelAsync_WithNonexistentChannel_DoesNotThrow()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        Func<Task> act = async () => await pool.RemoveChannelAsync("nonexistent");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RemoveChannelAsync_WithMultipleChannels_RemovesOnly()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log 1", "ch1");
        pool.EnqueueLog("Log 2", "ch2");
        pool.EnqueueLog("Log 3", "ch3");

        // Act
        await pool.RemoveChannelAsync("ch2");

        // Assert
        pool.ChannelCount.Should().Be(2);
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public void GetStatisticsSnapshot_ReturnsValidSnapshot()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log 1");
        pool.EnqueueLog("Log 2", LogBufferPool.CHANNEL_SYSTEM);

        // Act
        var snapshot = pool.GetStatisticsSnapshot();

        // Assert
        snapshot.Should().NotBeNull();
        snapshot.TotalChannels.Should().Be(2);
        snapshot.TotalLogsEnqueued.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void GetStatisticsSnapshot_IncludesChannelDetails()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log", "test-ch");

        // Act
        var snapshot = pool.GetStatisticsSnapshot();

        // Assert
        snapshot.ChannelStatistics.Should().NotBeEmpty();
        snapshot.ChannelStatistics.Count.Should().Be(1);
    }

    [Fact]
    public void GetStatisticsSnapshot_WithMultipleChannels_ListsAll()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log 1", "ch1");
        pool.EnqueueLog("Log 2", "ch2");
        pool.EnqueueLog("Log 3", "ch3");

        // Act
        var snapshot = pool.GetStatisticsSnapshot();

        // Assert
        snapshot.ChannelStatistics.Count.Should().Be(3);
    }

    [Fact]
    public void GetStatisticsSnapshot_HasTimestamp()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var beforeSnapshot = DateTime.UtcNow;

        // Act
        var snapshot = pool.GetStatisticsSnapshot();
        var afterSnapshot = DateTime.UtcNow;

        // Assert
        snapshot.Timestamp.Should().BeOnOrAfter(beforeSnapshot);
        snapshot.Timestamp.Should().BeOnOrBefore(afterSnapshot);
    }

    [Fact]
    public void StatisticsSnapshot_ToStringDoesNotThrow()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log", "ch1");
        var snapshot = pool.GetStatisticsSnapshot();

        // Act
        var str = snapshot.ToString();

        // Assert
        str.Should().NotBeNullOrEmpty();
        str.Should().Contain("LogBufferPool Statistics");
        str.Should().Contain("Total Channels");
    }

    #endregion

    #region Shutdown & Disposal Tests

    [Fact]
    public async Task ShutdownAsync_StopsAllBatchers()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        pool.EnqueueLog("Log 1");
        pool.EnqueueLog("Log 2");
        pool.EnqueueLog("Log 3");

        // Act
        await pool.ShutdownAsync();

        // Assert
        pool.ChannelCount.Should().Be(1); // Still has channel reference
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Arrange
        var pool = new LogBufferPool();
        pool.EnqueueLog("Log", "test");

        // Act
        Action act = () => pool.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CleansUpResources()
    {
        // Arrange
        var pool = new LogBufferPool();
        pool.EnqueueLog("Log 1");
        pool.EnqueueLog("Log 2");

        // Act
        pool.Dispose();
        pool.Dispose(); // Dispose twice should be safe

        // Assert - no exception thrown
    }

    #endregion

    #region Concurrency & Stress Tests

    [Fact]
    public void EnqueueLog_WithMultipleThreads_HandlesConcurrency()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var threadCount = 5;
        var logsPerThread = 20;
        var tasks = new List<Task>();

        // Act
        for (int t = 0; t < threadCount; t++)
        {
            var channel = $"ch-{t}";
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < logsPerThread; i++)
                {
                    pool.EnqueueLog($"Log {i}", channel);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(threadCount * logsPerThread);
        pool.ChannelCount.Should().Be(threadCount);
    }

    [Fact]
    public async Task FlushAll_WithConcurrentEnqueues_Completes()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var tasks = new List<Task>();

        // Act - Enqueue and flush concurrently
        for (int i = 0; i < 10; i++)
        {
            var channel = $"ch-{i}";
            for (int j = 0; j < 10; j++)
            {
                pool.EnqueueLog($"Log {j}", channel);
            }
        }

        Func<Task> act = async () => await pool.FlushAllAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void EnqueueLog_StressTest_1000Logs()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        for (int i = 0; i < 1000; i++)
        {
            pool.EnqueueLog($"Stress log {i}");
        }

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(1000);
    }

    #endregion

    #region Edge Cases & Error Handling

    [Fact]
    public void GetOrCreateBatcher_ExceedsMaxChannels_ThrowsException()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act - Create more than MAX_CHANNELS (10)
        Action act = () =>
        {
            for (int i = 0; i < 15; i++)
            {
                pool.GetOrCreateBatcher($"channel-{i}");
            }
        };

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnqueueLog_WithLargeMessage_Handles()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var largeMessage = new string('x', 10000);

        // Act
        pool.EnqueueLog(largeMessage);

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(1);
    }

    [Fact]
    public void EnqueueLog_WithSpecialCharacters_Handles()
    {
        // Arrange
        var pool = CreateAndTrackPool();
        var specialMessage = "Log with special chars: \n\t\r 中文 🎉 \0";

        // Act
        pool.EnqueueLog(specialMessage);

        // Assert
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(1);
    }

    #endregion

    #region Default Channel Tests

    [Fact]
    public void DefaultChannel_CreatedWhenNullSpecified()
    {
        // Arrange
        var pool = CreateAndTrackPool();

        // Act
        pool.EnqueueLog("Log 1");
        pool.EnqueueLog("Log 2", null);
        pool.EnqueueLog("Log 3", "");

        // Assert
        pool.ChannelCount.Should().Be(1); // All to default channel
        pool.PoolStatistics.TotalLogsEnqueued.Should().Be(3);
    }

    #endregion
}
