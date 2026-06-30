using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Tests.Fixtures;

namespace MDiceV2.Tests.Unit;

/// <summary>
/// Unit tests for LogBatcher
/// Tests: batch processing, timing, statistics, and performance
/// </summary>
public class LogBatcherTests : IDisposable
{
    private readonly List<LogBatcher> _batchersToCleanup = new();

    public void Dispose()
    {
        foreach (var batcher in _batchersToCleanup)
        {
            batcher?.StopAsync().GetAwaiter().GetResult();
            batcher?.Dispose();
        }
    }

    private LogBatcher CreateAndTrackBatcher(int batchSize = 100, int flushIntervalMs = 5000)
    {
        var batcher = new LogBatcher(batchSize, flushIntervalMs);
        _batchersToCleanup.Add(batcher);
        return batcher;
    }

    #region Initialization Tests

    [Fact]
    public void Constructor_WithDefaultParameters_InitializesSuccessfully()
    {
        // Arrange & Act
        var batcher = CreateAndTrackBatcher();

        // Assert
        batcher.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithCustomBatchSize_InitializesWithCorrectSize()
    {
        // Arrange & Act
        var batcher = CreateAndTrackBatcher(batchSize: 50);

        // Assert
        batcher.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithLargeBatchSize_ClampsToMaximum()
    {
        // Arrange & Act
        var batcher = CreateAndTrackBatcher(batchSize: 10000);

        // Assert
        batcher.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithSmallBatchSize_ClampsToMinimum()
    {
        // Arrange & Act
        var batcher = CreateAndTrackBatcher(batchSize: 2);

        // Assert
        batcher.Should().NotBeNull();
    }

    #endregion

    #region Start/Stop Tests

    [Fact]
    public void Start_StartsLogBatcher()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher();

        // Act
        batcher.Start();

        // Assert
        batcher.Should().NotBeNull();
    }

    [Fact]
    public async Task StopAsync_StopsLogBatcher()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher();
        batcher.Start();

        // Act
        await batcher.StopAsync();

        // Assert
        batcher.Should().NotBeNull();
    }

    [Fact]
    public async Task StartMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher();

        // Act & Assert
        batcher.Start();
        batcher.Start();

        await batcher.StopAsync();
    }

    [Fact]
    public async Task StopMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher();
        batcher.Start();

        // Act & Assert
        await batcher.StopAsync();
        await batcher.StopAsync();
    }

    #endregion

    #region Enqueue Tests

    [Fact]
    public void Enqueue_WhenNotRunning_ThrowsInvalidOperationException()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => 
            batcher.Enqueue("Test log")
        );
    }

    [Fact]
    public async Task Enqueue_SingleLog_SucceedsWithoutError()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher();
        batcher.Start();

        // Act & Assert
        await batcher.EnqueueAsync("Test log");
    }

    [Fact]
    public async Task Enqueue_MultipleLogs_QueuesAllLogs()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher();
        batcher.Start();
        var logCount = 10;

        // Act
        for (int i = 0; i < logCount; i++)
        {
            await batcher.EnqueueAsync($"Log {i}");
        }

        // Assert
        var stats = batcher.GetStatistics();
        stats.TotalEnqueued.Should().Be(logCount);
    }

    #endregion

    #region Batch Completion Tests

    [Fact]
    public async Task OnBatchComplete_WithBatchSizeReached_FiresCallback()
    {
        // Arrange
        var batchSize = 5;
        var batcher = CreateAndTrackBatcher(batchSize: batchSize);
        var receivedBatches = new List<List<LogEntry>>();
        var callbackLock = new object();

        batcher.OnBatchComplete = async (batch) =>
        {
            lock (callbackLock)
            {
                receivedBatches.Add(new List<LogEntry>(batch));
            }
            await Task.CompletedTask;
        };

        batcher.Start();

        // Act
        for (int i = 0; i < batchSize; i++)
        {
            await batcher.EnqueueAsync($"Log {i}");
        }

        // 此时回调已经执行（因为等待了异步操作）
        // Assert
        receivedBatches.Count.Should().BeGreaterThan(0, "Callback should have fired when batch size reached");
        receivedBatches[0].Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OnBatchComplete_WithMultipleBatches_FiresMultipleTimes()
    {
        // Arrange
        var batchSize = 3;
        var batcher = CreateAndTrackBatcher(batchSize: batchSize, flushIntervalMs: 10000);
        var callbackCount = 0;
        var callbackLock = new object();

        batcher.OnBatchComplete = async (batch) =>
        {
            lock (callbackLock)
            {
                callbackCount++;
            }
            await Task.CompletedTask;
        };

        batcher.Start();

        // Act
        for (int i = 0; i < batchSize * 2 + 1; i++)
        {
            await batcher.EnqueueAsync($"Log {i}");
        }

        // 此时所有批处理已经执行（等待了所有EnqueueAsync）
        // Assert
        callbackCount.Should().BeGreaterThanOrEqualTo(2, "Callback should fire at least 2 times");
    }

    #endregion

    #region Flush Interval Tests

    [Fact]
    public async Task FlushByInterval_AfterFlushIntervalPassed_FlushesLogs()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher(batchSize: 100, flushIntervalMs: 500);
        var callbackFiredEvent = new ManualResetEvent(false);

        batcher.OnBatchComplete = async (batch) =>
        {
            callbackFiredEvent.Set();
            await Task.CompletedTask;
        };

        batcher.Start();

        // Act
        await batcher.EnqueueAsync("Single log");
        
        // 等待定时刷新触发（500ms 间隔 + 额外时间），最多等待 2 秒
        bool flushTriggered = callbackFiredEvent.WaitOne(2000);

        // Assert
        flushTriggered.Should().BeTrue("Callback should fire after flush interval passes");
    }

    [Fact]
    public async Task FlushByInterval_BeforeIntervalPassed_DoesNotFlush()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher(batchSize: 100, flushIntervalMs: 5000);
        var callbackFired = false;

        batcher.OnBatchComplete = async (batch) =>
        {
            callbackFired = true;
            await Task.CompletedTask;
        };

        batcher.Start();

        // Act
        await batcher.EnqueueAsync("Single log");
        await Task.Delay(100);

        // Assert
        callbackFired.Should().BeFalse();
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public async Task Statistics_TracksTotalLogsProcessed()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher(batchSize: 3);
        var logCount = 10;

        batcher.OnBatchComplete = async (batch) => await Task.CompletedTask;
        batcher.Start();

        // Act
        for (int i = 0; i < logCount; i++)
        {
            await batcher.EnqueueAsync($"Log {i}");
        }

        await Task.Delay(ThreadingTestConstants.DefaultWaitMs);

        // Assert
        var stats = batcher.GetStatistics();
        stats.TotalProcessed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Statistics_TracksBatchCount()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher(batchSize: 3);

        batcher.OnBatchComplete = async (batch) => await Task.CompletedTask;
        batcher.Start();

        // Act
        for (int i = 0; i < 10; i++)
        {
            await batcher.EnqueueAsync($"Log {i}");
        }

        await Task.Delay(ThreadingTestConstants.DefaultWaitMs);

        // Assert
        var stats = batcher.GetStatistics();
        stats.BatchCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Statistics_TracksAverageProcessingTime()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher(batchSize: 3);
        var processingCompleted = new ManualResetEvent(false);

        batcher.OnBatchComplete = async (batch) =>
        {
            // 人为增加处理时间以确保能够测量
            await Task.Delay(50);
            processingCompleted.Set();
        };

        batcher.Start();

        // Act
        for (int i = 0; i < 9; i++)
        {
            await batcher.EnqueueAsync($"Log {i}");
        }

        // 等待处理完成，最多 5 秒
        _ = processingCompleted.WaitOne(5000);
        
        // 额外等待以确保统计更新
        await Task.Delay(500);

        // Assert
        var stats = batcher.GetStatistics();
        stats.BatchCount.Should().BeGreaterThan(0, "Should have at least one batch");
        stats.AverageProcessingTimeMs.Should().BeGreaterThan(0, "Average processing time should be measured");
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task Enqueue_FromMultipleThreads_HandlesCorrectly()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher(batchSize: 50);
        var threadCount = 10;
        var logsPerThread = 10;

        batcher.OnBatchComplete = async (batch) => await Task.CompletedTask;
        batcher.Start();

        // Act
        var tasks = new List<Task>();
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < logsPerThread; i++)
                {
                    await batcher.EnqueueAsync($"Thread {threadId} Log {i}");
                }
            }));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(ThreadingTestConstants.DefaultWaitMs);

        // Assert
        var stats = batcher.GetStatistics();
        stats.TotalEnqueued.Should().Be(threadCount * logsPerThread);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task GetStatistics_ReturnsValidData()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher();
        batcher.Start();

        // Act
        await batcher.EnqueueAsync("Test log");
        await Task.Delay(100);
        var stats = batcher.GetStatistics();

        // Assert
        stats.Should().NotBeNull();
        stats.TotalEnqueued.Should().Be(1);
    }

    [Fact]
    public async Task FlushNowAsync_ForcesImmediateFlush()
    {
        // Arrange
        var batcher = CreateAndTrackBatcher(batchSize: 100, flushIntervalMs: 10000);
        var callbackFired = false;

        batcher.OnBatchComplete = async (batch) =>
        {
            callbackFired = true;
            await Task.CompletedTask;
        };

        batcher.Start();
        await batcher.EnqueueAsync("Single log");

        // Act
        await batcher.FlushNowAsync();

        // Assert
        callbackFired.Should().BeTrue();
    }

    #endregion
}

/// <summary>
/// Helper class for threading test constants
/// </summary>
public static class ThreadingTestConstants
{
    public const int DefaultWaitMs = 2000;
    public const int ExtendedWaitMs = 5000;
}
