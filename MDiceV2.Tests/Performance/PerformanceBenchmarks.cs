using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using MDiceV2.Core.Infrastructure;

namespace MDiceV2.Tests.Performance;

/// <summary>
/// LogBatcher 性能基准测试
/// 衡量 throughput、latency 和内存使用
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
public class LogBatcherBenchmarks
{
    private LogBatcher _batcher;

    [GlobalSetup]
    public void Setup()
    {
        _batcher = new LogBatcher(100, 5000);
        _batcher.Start();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _batcher.StopAsync();
        _batcher.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("Throughput")]
    public void EnqueueSingleLog()
    {
        _batcher.Enqueue("Test log message");
    }

    [Benchmark]
    [BenchmarkCategory("Throughput")]
    public void Enqueue100Logs()
    {
        for (int i = 0; i < 100; i++)
        {
            _batcher.Enqueue($"Log message {i}");
        }
    }

    [Benchmark]
    [BenchmarkCategory("Statistics")]
    public LogBatcherStatistics GetStatistics()
    {
        return _batcher.GetStatistics();
    }
}

/// <summary>
/// LogBufferPool 性能基准
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
public class LogBufferPoolBenchmarks
{
    private LogBufferPool _pool;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new LogBufferPool();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("ChannelOperations")]
    public void EnqueueToSingleChannel()
    {
        _pool.EnqueueLog("Test log", "test-channel");
    }

    [Benchmark]
    [BenchmarkCategory("ChannelOperations")]
    public void EnqueueToMultipleChannels()
    {
        _pool.EnqueueLog("Log 1", LogBufferPool.CHANNEL_SYSTEM);
        _pool.EnqueueLog("Log 2", LogBufferPool.CHANNEL_NETWORK);
        _pool.EnqueueLog("Log 3", LogBufferPool.CHANNEL_GAME);
        _pool.EnqueueLog("Log 4", LogBufferPool.CHANNEL_ERROR);
    }

    [Benchmark]
    [BenchmarkCategory("Statistics")]
    public LogPoolStatisticsSnapshot GetStatisticsSnapshot()
    {
        return _pool.GetStatisticsSnapshot();
    }
}

/// <summary>
/// ConfigSync 网络操作性能基准（模拟）
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
public class ConfigSyncBenchmarks
{
    private ConfigSyncServer _server;
    private ConfigSyncClient _client;
    private int _testPort;
    private string _testPassword;

    [GlobalSetup]
    public async Task Setup()
    {
        _testPort = 9999;  // 固定端口用于基准测试
        _testPassword = "bench-test-password";
        _server = new ConfigSyncServer();
        _client = new ConfigSyncClient();

        await _server.StartAsync(_testPort, _testPassword);
        await _client.ConnectAsync("localhost", _testPort, _testPassword);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        try { await _client.DisconnectAsync(); } catch { }
        try { await _server.StopAsync(); } catch { }
    }

    [Benchmark]
    [BenchmarkCategory("ConfigOperations")]
    public async Task PullConfig()
    {
        await _client.PullConfigAsync();
    }

    [Benchmark]
    [BenchmarkCategory("ConfigOperations")]
    public async Task PushSmallConfig()
    {
        var config = new Dictionary<string, string>
        {
            { "key1", "value1" },
            { "key2", "value2" },
        };
        await _client.PushConfigAsync(config);
    }
}
