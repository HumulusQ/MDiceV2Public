using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Tests.Fixtures;
using Xunit;

namespace MDiceV2.Tests.Integration;

/// <summary>
/// 基础集成测试 - 覆盖关键跨组件交互
/// </summary>
public class BasicIntegrationTests
{
    [Fact]
    public void LogBufferPool_WithMultipleBatchers_ProcessesIndependently()
    {
        // Arrange
        var pool = new LogBufferPool();

        // Act
        pool.EnqueueLog("System log", LogBufferPool.CHANNEL_SYSTEM);
        pool.EnqueueLog("Network log", LogBufferPool.CHANNEL_NETWORK);
        pool.EnqueueLog("Game log", LogBufferPool.CHANNEL_GAME);

        var stats = pool.PoolStatistics;

        // Assert
        stats.TotalLogsEnqueued.Should().Be(3);
        pool.ChannelCount.Should().Be(3);

        // Cleanup
        pool.Dispose();
    }

    [Fact]
    public async Task LogBatcher_ReceivesLogsFromPool()
    {
        // Arrange
        var batcher = new LogBatcher(5, 1000);
        int receivedCount = 0;

        // Act
        batcher.Start();
        for (int i = 0; i < 3; i++)
        {
            await batcher.EnqueueAsync($"Log {i}");
            receivedCount++;
        }

        await batcher.FlushNowAsync();
        var stats = batcher.GetStatistics();

        // Assert
        stats.TotalEnqueued.Should().Be(receivedCount);

        // Cleanup
        await batcher.StopAsync();
        batcher.Dispose();
    }

    [Fact]
    public async Task LogBufferPool_FlushAll_CompletesSuccessfully()
    {
        // Arrange
        var pool = new LogBufferPool();
        pool.EnqueueLog("Log 1", "ch1");
        pool.EnqueueLog("Log 2", "ch2");

        // Act
        await pool.FlushAllAsync();
        var snapshot = pool.GetStatisticsSnapshot();

        // Assert
        snapshot.TotalLogsEnqueued.Should().Be(2);

        // Cleanup
        pool.Dispose();
    }

    [Fact]
    public void LogBufferPool_MultipleBatchers_AreIsolated()
    {
        // Arrange
        var pool = new LogBufferPool();

        // Act
        var batcher1 = pool.GetOrCreateBatcher("ch1");
        var batcher2 = pool.GetOrCreateBatcher("ch2");

        // Assert
        batcher1.Should().NotBeSameAs(batcher2);
        pool.ChannelCount.Should().Be(2);

        // Cleanup
        pool.Dispose();
    }

    [Fact]
    public async Task ConfigSyncServer_CanStartAndStop()
    {
        // Arrange
        var password = TestFixtures.GenerateTestToken();
        var server = new ConfigSyncServer(password);  // 将密码传给构造函数
        var port = TestFixtures.GetAvailablePort();

        // Act
        await server.StartAsync(port, password);  // 使用相同的密码
        var initialCount = server.ConnectedClientCount;
        
        // Assert
        initialCount.Should().Be(0);

        // Cleanup
        await server.StopAsync();
    }

    [Fact]
    public async Task LogBatcher_Statistics_AreValid()
    {
        // Arrange
        var batcher = new LogBatcher(5, 1000);
        var batchProcessed = new ManualResetEvent(false);

        batcher.OnBatchComplete = async (batch) =>
        {
            batchProcessed.Set();
            await Task.CompletedTask;
        };

        // Act
        batcher.Start();
        for (int i = 0; i < 10; i++)
        {
            await batcher.EnqueueAsync($"Log {i}");
        }

        // 等待批处理完成，最多 2 秒
        _ = batchProcessed.WaitOne(2000);

        var stats = batcher.GetStatistics();

        // Assert
        stats.TotalEnqueued.Should().BeGreaterThanOrEqualTo(10);
        stats.BatchCount.Should().BeGreaterThanOrEqualTo(1, "At least one batch should have been processed");

        // Cleanup
        await batcher.StopAsync();
        batcher.Dispose();
    }
}
