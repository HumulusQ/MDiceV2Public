using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// 日志缓冲池 - 管理多个日志通道的批处理
/// 支持不同优先级、通道隔离、资源管理
/// </summary>
public class LogBufferPool : IDisposable
{
    // 预定义的日志通道
    public const string CHANNEL_DEFAULT = "default";
    public const string CHANNEL_SYSTEM = "system";
    public const string CHANNEL_NETWORK = "network";
    public const string CHANNEL_GAME = "game";
    public const string CHANNEL_ERROR = "error";

    // 默认配置
    private const int DEFAULT_BATCH_SIZE = 100;
    private const int DEFAULT_FLUSH_INTERVAL = 5000;
    private const int MAX_CHANNELS = 10;

    private readonly ConcurrentDictionary<string, LogBatcher> _batchers;
    private readonly ReaderWriterLockSlim _poolLock;
    private readonly LogPoolStatistics _poolStatistics;
    private bool _disposed;

    /// <summary>
    /// 池中的活跃通道数
    /// </summary>
    public int ChannelCount => _batchers.Count;

    /// <summary>
    /// 获取池的统计信息
    /// </summary>
    public LogPoolStatistics PoolStatistics => _poolStatistics;

    public LogBufferPool()
    {
        _batchers = new ConcurrentDictionary<string, LogBatcher>();
        _poolLock = new ReaderWriterLockSlim();
        _poolStatistics = new LogPoolStatistics();
    }

    /// <summary>
    /// 获取或创建日志批处理器
    /// </summary>
    public LogBatcher GetOrCreateBatcher(string channel, int batchSize = DEFAULT_BATCH_SIZE, int flushIntervalMs = DEFAULT_FLUSH_INTERVAL)
    {
        if (string.IsNullOrEmpty(channel))
            channel = CHANNEL_DEFAULT;

        if (_batchers.TryGetValue(channel, out var batcher))
        {
            return batcher;
        }

        _poolLock.EnterWriteLock();
        try
        {
            if (_batchers.Count >= MAX_CHANNELS)
            {
                throw new InvalidOperationException($"Maximum channels ({MAX_CHANNELS}) reached");
            }

            var newBatcher = new LogBatcher(batchSize, flushIntervalMs);
            newBatcher.Start();

            if (_batchers.TryAdd(channel, newBatcher))
            {
                _poolStatistics.ChannelsCreated++;
                LogDebug($"Batcher created for channel: {channel}");
                return newBatcher;
            }
            else
            {
                newBatcher.Dispose();
                return _batchers[channel];
            }
        }
        finally
        {
            _poolLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 添加日志到指定通道
    /// </summary>
    public void EnqueueLog(string logContent, string channel = CHANNEL_DEFAULT)
    {
        if (string.IsNullOrEmpty(logContent))
            return;

        try
        {
            var batcher = GetOrCreateBatcher(channel);
            batcher.Enqueue(logContent);
            _poolStatistics.TotalLogsEnqueued++;
        }
        catch (Exception ex)
        {
            LogError($"Error enqueuing log: {ex.Message}");
            _poolStatistics.ErrorCount++;
        }
    }

    /// <summary>
    /// 刷新所有通道的日志
    /// </summary>
    public async Task FlushAllAsync()
    {
        var tasks = new List<Task>();

        _poolLock.EnterReadLock();
        try
        {
            foreach (var batcher in _batchers.Values)
            {
                tasks.Add(batcher.FlushNowAsync());
            }
        }
        finally
        {
            _poolLock.ExitReadLock();
        }

        await Task.WhenAll(tasks);
        LogDebug($"All {tasks.Count} channels flushed");
    }

    /// <summary>
    /// 刷新指定通道
    /// </summary>
    public async Task FlushChannelAsync(string channel)
    {
        if (_batchers.TryGetValue(channel, out var batcher))
        {
            await batcher.FlushNowAsync();
        }
    }

    /// <summary>
    /// 获取池的统计信息快照
    /// </summary>
    public LogPoolStatisticsSnapshot GetStatisticsSnapshot()
    {
        var snapshot = new LogPoolStatisticsSnapshot
        {
            Timestamp = DateTime.UtcNow,
            TotalChannels = _batchers.Count,
            TotalLogsEnqueued = _poolStatistics.TotalLogsEnqueued,
            ChannelsCreated = _poolStatistics.ChannelsCreated,
            ErrorCount = _poolStatistics.ErrorCount,
            ChannelStatistics = new List<ChannelStatistics>()
        };

        _poolLock.EnterReadLock();
        try
        {
            foreach (var kvp in _batchers)
            {
                var stats = kvp.Value.GetStatistics();
                snapshot.ChannelStatistics.Add(new ChannelStatistics
                {
                    ChannelName = kvp.Key,
                    TotalProcessed = stats.TotalProcessed,
                    PendingCount = stats.PendingCount,
                    BatchCount = stats.BatchCount,
                    AverageProcessingTimeMs = stats.AverageProcessingTimeMs,
                    Throughput = stats.Throughput
                });
            }
        }
        finally
        {
            _poolLock.ExitReadLock();
        }

        return snapshot;
    }

    /// <summary>
    /// 删除指定通道
    /// </summary>
    public async Task RemoveChannelAsync(string channel)
    {
        if (_batchers.TryRemove(channel, out var batcher))
        {
            await batcher.StopAsync();
            batcher.Dispose();
            LogDebug($"Channel removed: {channel}");
        }
    }

    /// <summary>
    /// 优雅关闭所有通道
    /// </summary>
    public async Task ShutdownAsync()
    {
        var tasks = new List<Task>();

        _poolLock.EnterReadLock();
        try
        {
            foreach (var batcher in _batchers.Values)
            {
                tasks.Add(batcher.StopAsync());
            }
        }
        finally
        {
            _poolLock.ExitReadLock();
        }

        await Task.WhenAll(tasks);
        LogDebug("All channels shut down");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 同步关闭所有通道
        ShutdownAsync().Wait(TimeSpan.FromSeconds(10));

        _poolLock?.Dispose();

        foreach (var batcher in _batchers.Values)
        {
            batcher?.Dispose();
        }

        _batchers.Clear();
    }

    private void LogDebug(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[LogBufferPool] DEBUG - {message}");
    }

    private void LogError(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[LogBufferPool] ERROR - {message}");
    }
}

/// <summary>
/// 日志池的基础统计
/// </summary>
public class LogPoolStatistics
{
    public long TotalLogsEnqueued { get; set; }
    public long ChannelsCreated { get; set; }
    public long ErrorCount { get; set; }
}

/// <summary>
/// 日志池的统计快照
/// </summary>
public class LogPoolStatisticsSnapshot
{
    public DateTime Timestamp { get; set; }
    public int TotalChannels { get; set; }
    public long TotalLogsEnqueued { get; set; }
    public long ChannelsCreated { get; set; }
    public long ErrorCount { get; set; }
    public List<ChannelStatistics> ChannelStatistics { get; set; }

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"LogBufferPool Statistics ({Timestamp:yyyy-MM-dd HH:mm:ss})",
            $"  Total Channels: {TotalChannels}",
            $"  Total Logs Enqueued: {TotalLogsEnqueued}",
            $"  Channels Created: {ChannelsCreated}",
            $"  Errors: {ErrorCount}",
            ""
        };

        foreach (var channel in ChannelStatistics)
        {
            lines.Add($"  Channel: {channel.ChannelName}");
            lines.Add($"    Total Processed: {channel.TotalProcessed}");
            lines.Add($"    Pending: {channel.PendingCount}");
            lines.Add($"    Batches: {channel.BatchCount}");
            lines.Add($"    Avg Time: {channel.AverageProcessingTimeMs:F2}ms");
            lines.Add($"    Throughput: {channel.Throughput:F0} logs/sec");
        }

        return string.Join("\n", lines);
    }
}

/// <summary>
/// 单个通道的统计信息
/// </summary>
public class ChannelStatistics
{
    public string ChannelName { get; set; }
    public long TotalProcessed { get; set; }
    public long PendingCount { get; set; }
    public long BatchCount { get; set; }
    public double AverageProcessingTimeMs { get; set; }
    public double Throughput { get; set; }
}
