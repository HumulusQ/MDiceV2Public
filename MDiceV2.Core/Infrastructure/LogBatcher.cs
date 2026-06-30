using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// 日志批处理器 - 优化日志广播性能
/// 支持自动批处理、定时刷新、大小限制刷新
/// </summary>
public class LogBatcher : IDisposable
{
    // 配置常数
    private const int DEFAULT_BATCH_SIZE = 100;          // 默认批次大小
    private const int DEFAULT_FLUSH_INTERVAL_MS = 5000;  // 5秒刷新间隔
    private const int MAX_BATCH_SIZE = 500;              // 最大批次大小
    private const int MIN_FLUSH_INTERVAL_MS = 1000;      // 最小刷新间隔 1秒

    // 日志缓冲队列
    private readonly ConcurrentQueue<LogEntry> _logQueue;
    
    // 配置
    private readonly int _batchSize;
    private readonly int _flushIntervalMs;
    
    // 状态管理
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning;
    private Task? _flushTask;
    
    // 性能统计
    private readonly LogStatistics _statistics;

    /// <summary>
    /// 日志批处理完成的回调委托
    /// </summary>
    public Func<List<LogEntry>, Task>? OnBatchComplete { get; set; }

    /// <summary>
    /// 获取当前队列中的日志数
    /// </summary>
    public int PendingLogCount => _logQueue.Count;

    /// <summary>
    /// 获取性能统计信息
    /// </summary>
    public LogStatistics Statistics => _statistics;

    public LogBatcher(int batchSize = DEFAULT_BATCH_SIZE, int flushIntervalMs = DEFAULT_FLUSH_INTERVAL_MS)
    {
        // Allow batch sizes down to 1 for testing flexibility; clamp to maximum
        _batchSize = Math.Min(Math.Max(batchSize, 1), MAX_BATCH_SIZE);
        _flushIntervalMs = Math.Max(flushIntervalMs, MIN_FLUSH_INTERVAL_MS);
        _logQueue = new ConcurrentQueue<LogEntry>();
        _statistics = new LogStatistics();
        _isRunning = false;
    }

    /// <summary>
    /// 启动日志批处理
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _cancellationTokenSource = new CancellationTokenSource();

        _flushTask = Task.Run(async () => await FlushLoopsAsync(_cancellationTokenSource.Token));
        
        LogDebug($"LogBatcher started (batch_size={_batchSize}, flush_interval={_flushIntervalMs}ms)");
    }

    /// <summary>
    /// 停止日志批处理
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cancellationTokenSource?.Cancel();

        // 等待刷新任务完成
        if (_flushTask != null)
        {
            try
            {
                await _flushTask;
            }
            catch (OperationCanceledException)
            {
                // 预期的异常
            }
        }

        // 刷新剩余日志
        await FlushNowAsync();
        
        LogDebug("LogBatcher stopped");
    }

    /// <summary>
    /// 添加日志条目（异步）
    /// </summary>
    public async Task EnqueueAsync(string logContent)
    {
        if (!_isRunning)
            throw new InvalidOperationException("LogBatcher is not running");

        var entry = new LogEntry
        {
            Content = logContent,
            Timestamp = DateTime.UtcNow,
            GroupId = "default",
            Level = "INFO"
        };

        _logQueue.Enqueue(entry);
        
        // 使用线程安全的原子增量操作
        Interlocked.Increment(ref _statistics._totalEnqueuedInternal);

        // 如果队列达到批次大小，立即等待刷新
        if (_logQueue.Count >= _batchSize)
        {
            await FlushNowAsync();
        }
    }

    /// <summary>
    /// 添加日志条目（同步版本，向后兼容）
    /// </summary>
    [Obsolete("使用 EnqueueAsync 代替")]
    public void Enqueue(string logContent)
    {
        EnqueueAsync(logContent).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 立即刷新所有待处理日志
    /// </summary>
    public async Task FlushNowAsync()
    {
        if (_logQueue.Count == 0)
            return;

        var batch = new List<LogEntry>();
        while (_logQueue.TryDequeue(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= MAX_BATCH_SIZE)
                break;
        }

        if (batch.Count > 0)
        {
            await ProcessBatchAsync(batch);
        }
    }

    /// <summary>
    /// 处理日志批次
    /// </summary>
    private async Task ProcessBatchAsync(List<LogEntry> batch)
    {
        if (batch.Count == 0)
            return;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 调用批处理完成回调
            if (OnBatchComplete != null)
            {
                await OnBatchComplete(batch);
            }

            stopwatch.Stop();
            
            // 使用线程安全的原子操作更新统计信息
            Interlocked.Add(ref _statistics._totalProcessedInternal, batch.Count);
            Interlocked.Add(ref _statistics._processingTimeMsInternal, stopwatch.ElapsedMilliseconds);
            Interlocked.Increment(ref _statistics._batchCountInternal);
            
            // 计算平均值（这不需要原子操作，因为是最后计算）
            var totalTime = Interlocked.Read(ref _statistics._processingTimeMsInternal);
            var batchCount = Interlocked.Read(ref _statistics._batchCountInternal);
            _statistics.AverageProcessingTimeMs = totalTime / Math.Max(batchCount, 1);

            LogDebug($"Batch processed: {batch.Count} logs in {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _statistics._errorCountInternal);
            LogError($"Error processing batch: {ex.Message}");
        }
    }

    /// <summary>
    /// 定期刷新日志的循环
    /// </summary>
    private async Task FlushLoopsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushIntervalMs, cancellationToken);

                if (_isRunning && _logQueue.Count > 0)
                {
                    await FlushNowAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // 预期的异常，循环应该退出
                break;
            }
            catch (Exception ex)
            {
                LogError($"Error in flush loop: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 获取统计信息的快照
    /// </summary>
    public LogBatcherStatistics GetStatistics()
    {
        return new LogBatcherStatistics
        {
            TotalEnqueued = _statistics.TotalEnqueued,
            TotalProcessed = _statistics.TotalProcessed,
            BatchCount = _statistics.BatchCount,
            PendingCount = _logQueue.Count,
            AverageProcessingTimeMs = _statistics.AverageProcessingTimeMs,
            TotalProcessingTimeMs = _statistics.ProcessingTimeMs,
            ErrorCount = _statistics.ErrorCount,
            Throughput = CalculateThroughput()
        };
    }

    /// <summary>
    /// 计算吞吐量（日志/秒）
    /// </summary>
    private double CalculateThroughput()
    {
        if (_statistics.ProcessingTimeMs == 0 || _statistics.TotalProcessed == 0)
            return 0;

        double secondsElapsed = _statistics.ProcessingTimeMs / 1000.0;
        return _statistics.TotalProcessed / secondsElapsed;
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
    }

    private void LogDebug(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[LogBatcher] DEBUG - {message}");
    }

    private void LogError(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[LogBatcher] ERROR - {message}");
    }
}

/// <summary>
/// 内部统计数据
/// </summary>
public class LogStatistics
{
    // 内部字段用于线程安全的原子操作
    internal long _totalEnqueuedInternal = 0;
    internal long _totalProcessedInternal = 0;
    internal long _batchCountInternal = 0;
    internal long _processingTimeMsInternal = 0;
    internal long _errorCountInternal = 0;
    
    public long TotalEnqueued 
    { 
        get => Interlocked.Read(ref _totalEnqueuedInternal);
        set => Interlocked.Exchange(ref _totalEnqueuedInternal, value);
    }
    
    public long TotalProcessed 
    { 
        get => Interlocked.Read(ref _totalProcessedInternal);
        set => Interlocked.Exchange(ref _totalProcessedInternal, value);
    }
    
    public long BatchCount 
    { 
        get => Interlocked.Read(ref _batchCountInternal);
        set => Interlocked.Exchange(ref _batchCountInternal, value);
    }
    
    public long ProcessingTimeMs 
    { 
        get => Interlocked.Read(ref _processingTimeMsInternal);
        set => Interlocked.Exchange(ref _processingTimeMsInternal, value);
    }
    
    public long ErrorCount 
    { 
        get => Interlocked.Read(ref _errorCountInternal);
        set => Interlocked.Exchange(ref _errorCountInternal, value);
    }
    
    public double AverageProcessingTimeMs { get; set; }
}

/// <summary>
/// 日志批处理器统计信息（公开）
/// </summary>
public class LogBatcherStatistics
{
    /// <summary>
    /// 总入队日志数
    /// </summary>
    public long TotalEnqueued { get; set; }

    /// <summary>
    /// 已处理的日志数
    /// </summary>
    public long TotalProcessed { get; set; }

    /// <summary>
    /// 处理的批次数
    /// </summary>
    public long BatchCount { get; set; }

    /// <summary>
    /// 待处理日志数
    /// </summary>
    public long PendingCount { get; set; }

    /// <summary>
    /// 平均处理时间（毫秒）
    /// </summary>
    public double AverageProcessingTimeMs { get; set; }

    /// <summary>
    /// 总处理时间（毫秒）
    /// </summary>
    public long TotalProcessingTimeMs { get; set; }

    /// <summary>
    /// 错误次数
    /// </summary>
    public long ErrorCount { get; set; }

    /// <summary>
    /// 吞吐量（日志/秒）
    /// </summary>
    public double Throughput { get; set; }

    public override string ToString()
    {
        return $"LogBatcher Stats:\n" +
               $"  Total Enqueued: {TotalEnqueued}\n" +
               $"  Total Processed: {TotalProcessed}\n" +
               $"  Batch Count: {BatchCount}\n" +
               $"  Pending: {PendingCount}\n" +
               $"  Avg Processing Time: {AverageProcessingTimeMs:F2}ms\n" +
               $"  Total Processing Time: {TotalProcessingTimeMs}ms\n" +
               $"  Errors: {ErrorCount}\n" +
               $"  Throughput: {Throughput:F0} logs/sec";
    }
}
