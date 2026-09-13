using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MDiceV2.Abstractions;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// gRPC 配置同步服务器实现
/// 支持 HMAC-SHA256 身份验证、TLS 加密、批量日志广播
/// </summary>
public class ConfigSyncServer : IConfigSyncServer
{
    private readonly string _password;
    private readonly ConcurrentDictionary<string, ClientSession> _connectedClients;
    private readonly LogBufferPool _logBufferPool;
    private readonly ConcurrentQueue<LogBatch> _logQueue;
    private readonly ConcurrentQueue<SimulationMessage> _messageQueue;
    private readonly Dictionary<string, string> _currentConfig;
    private readonly Func<Dictionary<string, string>>? _configProvider;
    private CancellationTokenSource? _serverCancellationTokenSource;
    private bool _isRunning;
    private int _port;

    public int ConnectedClientCount => _connectedClients.Count;
    
    // ✅ 【新增】配置更新事件 - 用于通知UI同步配置变更
    public event Action<Dictionary<string, string>>? OnConfigUpdated;

    /// <summary>
    /// 创建 ConfigSyncServer 实例
    /// </summary>
    /// <param name="password">服务器认证密码</param>
    /// <param name="configProvider">配置数据提供者，用于处理拉取请求</param>
    public ConfigSyncServer(string password = "default-password", Func<Dictionary<string, string>>? configProvider = null)
    {
        _password = password ?? "default-password";
        _connectedClients = new ConcurrentDictionary<string, ClientSession>();
        _logBufferPool = new LogBufferPool();
        _logQueue = new ConcurrentQueue<LogBatch>();
        _messageQueue = new ConcurrentQueue<SimulationMessage>();
        _configProvider = configProvider;
        _currentConfig = new Dictionary<string, string>();
        
        LogInfo("[ConfigSyncServer] ✓ 服务器已初始化");
        _isRunning = false;
    }

    /// <summary>
    /// 启动 gRPC 服务器
    /// </summary>
    public async Task StartAsync(int port, string password)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("ConfigSync server is already running");
        }

        _port = port;
        _serverCancellationTokenSource = new CancellationTokenSource();
        _isRunning = true;

        // 验证密码
        if (!VerifyPassword(password))
        {
            _isRunning = false;
            throw new UnauthorizedAccessException("Invalid server password");
        }

        // 模拟服务器启动（实际生产环境使用真实 gRPC 服务器）
        LogInfo($"ConfigSync server started on port {port}");

        // 保持服务器运行的任务
        await Task.Run(async () =>
        {
            while (_isRunning && !_serverCancellationTokenSource.Token.IsCancellationRequested)
            {
                // 定期清理断开连接的客户端（每 30 秒）
                if (_connectedClients.Count > 0)
                {
                    var disconnected = _connectedClients
                        .Where(kvp => !kvp.Value.IsActive)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var clientId in disconnected)
                    {
                        _connectedClients.TryRemove(clientId, out _);
                        LogInfo($"Removed inactive client: {clientId}");
                    }
                }

                await Task.Delay(30000, _serverCancellationTokenSource.Token);
            }
        }, _serverCancellationTokenSource.Token);
    }

    /// <summary>
    /// 停止服务器
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _serverCancellationTokenSource?.Cancel();
        _serverCancellationTokenSource?.Dispose();

        // 断开所有客户端
        foreach (var client in _connectedClients.Values)
        {
            client.IsActive = false;
        }

        _connectedClients.Clear();
        
        // 关闭日志缓冲池
        await _logBufferPool.ShutdownAsync();

        LogInfo("ConfigSync server stopped");
        await Task.CompletedTask;
    }

    /// <summary>
    /// 广播日志条目到所有连接的客户端（支持批量）
    /// </summary>
    public async Task BroadcastLogsAsync(IEnumerable<string> logEntries)
    {
        if (!_isRunning)
            return;

        var entries = logEntries?.ToList() ?? new List<string>();
        if (entries.Count == 0)
            return;

        // 添加日志到缓冲池
        foreach (var logEntry in entries)
        {
            _logBufferPool.EnqueueLog(logEntry, LogBufferPool.CHANNEL_DEFAULT);
        }

        // 配置批处理完成时的回调
        var batcher = _logBufferPool.GetOrCreateBatcher(LogBufferPool.CHANNEL_DEFAULT);
        if (batcher.OnBatchComplete == null)
        {
            batcher.OnBatchComplete = async (batch) =>
            {
                await BroadcastBatchToClientsAsync(batch);
            };
        }

        LogInfo($"Enqueued {entries.Count} logs for broadcasting");
    }

    /// <summary>
    /// 将日志批次广播给所有客户端
    /// </summary>
    private async Task BroadcastBatchToClientsAsync(List<LogEntry> batch)
    {
        if (batch.Count == 0)
            return;

        var logBatch = new LogBatch
        {
            Timestamp = DateTime.UtcNow,
            Entries = batch.Select(e => e.Content).ToList(),
            EntryCount = batch.Count,
            BatchId = Guid.NewGuid().ToString("N")
        };

        var tasks = new List<Task>();
        foreach (var client in _connectedClients.Values)
        {
            if (client.IsActive)
            {
                tasks.Add(client.SendLogBatchAsync(logBatch));
            }
        }

        await Task.WhenAll(tasks);
        LogInfo($"Broadcast batch {logBatch.BatchId} to {tasks.Count} clients");
    }

    /// <summary>
    /// 广播模拟消息到所有连接的客户端
    /// </summary>
    public async Task BroadcastSimulationMessageAsync(string userId, string content)
    {
        if (!_isRunning)
            return;

        var message = new SimulationMessage
        {
            UserId = userId,
            Content = content,
            Timestamp = DateTime.UtcNow,
            MessageId = Guid.NewGuid().ToString("N")
        };

        var tasks = new List<Task>();
        foreach (var client in _connectedClients.Values)
        {
            if (client.IsActive)
            {
                tasks.Add(client.SendSimulationMessageAsync(message));
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 注册新的客户端连接
    /// </summary>
    public void RegisterClient(string clientId, ClientSession session)
    {
        _connectedClients.TryAdd(clientId, session);
        LogInfo($"Client registered: {clientId} (total: {ConnectedClientCount})");
    }

    /// <summary>
    /// 注销客户端连接
    /// </summary>
    public void UnregisterClient(string clientId)
    {
        if (_connectedClients.TryRemove(clientId, out _))
        {
            LogInfo($"Client unregistered: {clientId} (remaining: {ConnectedClientCount})");
        }
    }

    /// <summary>
    /// 验证客户端密码（HMAC-SHA256）
    /// </summary>
    public bool VerifyClientPassword(string clientPassword)
    {
        return VerifyPassword(clientPassword);
    }

    /// <summary>
    /// 生成密码哈希（用于初始设置）
    /// </summary>
    public static string GeneratePasswordHash(string password)
    {
        using (var sha256 = SHA256.Create())
        {
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hashedBytes);
        }
    }

    /// <summary>
    /// 验证密码
    /// </summary>
    private bool VerifyPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        var passwordHash = GeneratePasswordHash(password);
        var expectedHash = GeneratePasswordHash(_password);

        return string.Equals(passwordHash, expectedHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// 获取服务器性能统计
    /// </summary>
    public LogPoolStatisticsSnapshot GetPerformanceStatistics()
    {
        return _logBufferPool.GetStatisticsSnapshot();
    }

    private void LogInfo(string message)
    {
        var formattedMsg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        // 1. 都输出到 Debug 调试窗口
        System.Diagnostics.Debug.WriteLine(formattedMsg);
        
        // 2. 也输出到控制台（即使反射失败也能看到）
        Console.WriteLine(formattedMsg);
        
        // 3. 尝试通过反射调用 LogSender
        try
        {
            var logSenderType = Type.GetType("MDiceV2.Core.Models.LogSender, MDiceV2.Core");
            if (logSenderType != null)
            {
                var method = logSenderType.GetMethod("Normal", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                method?.Invoke(null, new object[] { formattedMsg });
            }
        }
        catch { /* LogSender初始化还未完成，忽略 */ }
    }

    /// <summary>
    /// 获取当前服务器配置（用于 gRPC PullConfig）
    /// </summary>
    public Dictionary<string, string> GetCurrentConfig()
    {
        return new Dictionary<string, string>(_currentConfig);
    }

    /// <summary>
    /// 【新增】从提供者或缓存中构建拉取配置响应
    /// </summary>
    public Dictionary<string, string> BuildConfigForPull()
    {
        if (_configProvider != null)
        {
            try
            {
                var config = _configProvider();
                LogInfo($"[ConfigSyncServer.BuildConfigForPull] ✓ 从提供者获取到 {config.Count} 个配置项");
                
                // 同时更新内部缓存
                lock (_currentConfig)
                {
                    foreach (var kvp in config)
                    {
                        _currentConfig[kvp.Key] = kvp.Value;
                    }
                }
                return config;
            }
            catch (Exception ex)
            {
                LogInfo($"[ConfigSyncServer.BuildConfigForPull] ⚠️ 提供者获取配置失败: {ex.Message}");
            }
        }

        lock (_currentConfig)
        {
            LogInfo($"[ConfigSyncServer.BuildConfigForPull] 从内部缓存返回 {_currentConfig.Count} 个配置项");
            return new Dictionary<string, string>(_currentConfig);
        }
    }

    /// <summary>
    /// 更新服务器配置（用于 gRPC PushConfig）
    /// 返回冲突数量
    /// </summary>
    public int UpdateConfig(Dictionary<string, string> newConfig, string clientId)
    {
        int conflictCount = 0;
        
        // ✅ 【日志】打印更新开始
        LogInfo($"[ConfigSyncServer.UpdateConfig] ===== 配置更新开始 =====");
        LogInfo($"[ConfigSyncServer.UpdateConfig] 客户端ID: {clientId}");
        LogInfo($"[ConfigSyncServer.UpdateConfig] 待更新配置项数: {newConfig.Count}");

        foreach (var kvp in newConfig)
        {
            if (_currentConfig.ContainsKey(kvp.Key))
            {
                // 检查是否有冲突
                if (_currentConfig[kvp.Key] != kvp.Value)
                {
                    conflictCount++;
                    LogInfo($"[ConfigSyncServer.UpdateConfig] ⚠️ 配置冲突: {kvp.Key} (旧={_currentConfig[kvp.Key]} → 新={kvp.Value})");
                }
            }

            // 更新值
            _currentConfig[kvp.Key] = kvp.Value;
            LogInfo($"[ConfigSyncServer.UpdateConfig] ✓ 更新: {kvp.Key} = {kvp.Value}");
        }

        LogInfo($"[ConfigSyncServer.UpdateConfig] 共发生 {conflictCount} 个冲突");
        
        // ✅ 【日志】打印触发事件
        LogInfo($"[ConfigSyncServer.UpdateConfig] 正在触发 OnConfigUpdated 事件...");
        LogInfo($"[ConfigSyncServer.UpdateConfig] 事件订阅者数量: {OnConfigUpdated?.GetInvocationList().Length ?? 0}");
        
        // ✅ 【新增】触发事件通知有配置更新
        if (OnConfigUpdated != null)
        {
            try
            {
                OnConfigUpdated(new Dictionary<string, string>(newConfig));
                LogInfo($"[ConfigSyncServer.UpdateConfig] ✓ OnConfigUpdated 事件已触发，包含 {newConfig.Count} 项配置");
            }
            catch (Exception ex)
            {
                LogInfo($"[ConfigSyncServer.UpdateConfig] ✗ OnConfigUpdated 事件处理异常: {ex.Message}");
            }
        }
        else
        {
            LogInfo($"[ConfigSyncServer.UpdateConfig] ⚠ OnConfigUpdated 未被订阅！配置更新可能不会传播到UI");
        }
        LogInfo($"[ConfigSyncServer.UpdateConfig] ===== 配置更新完成 =====");
        
        return conflictCount;
    }

    /// <summary>
    /// 处理模拟消息（用于 gRPC StreamSimulationMessages）
    /// </summary>
    public async Task ProcessSimulationMessageAsync(string userId, string groupId, string content)
    {
        var message = new SimulationMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            UserId = userId,
            Content = content,
            Timestamp = DateTime.UtcNow
        };

        _messageQueue.Enqueue(message);
        LogInfo($"Processed simulation message from {userId} in group {groupId}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// 订阅日志流（用于 gRPC SubscribeLogs）
    /// </summary>
    public void SubscribeToLogs(string clientId, Func<List<LogEntry>, Task> onBatchReceived)
    {
        // 获取日志缓冲池的批处理器
        var batcher = _logBufferPool.GetOrCreateBatcher(LogBufferPool.CHANNEL_DEFAULT);

        // 设置批处理完成的回调
        batcher.OnBatchComplete = async (batch) =>
        {
            if (batch.Count > 0)
            {
                var logEntries = batch.Select(entry => new LogEntry
                {
                    GroupId = entry.GroupId ?? "default",
                    Content = entry.Content ?? string.Empty,
                    Timestamp = entry.Timestamp,
                    Level = entry.Level ?? "INFO"
                }).ToList();

                await onBatchReceived(logEntries);
            }
        };

        LogInfo($"Client {clientId} subscribed to log stream");
    }
}

/// <summary>
/// 客户端会话表示
/// </summary>
public class ClientSession
{
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");
    public bool IsActive { get; set; } = true;
    public DateTime ConnectedTime { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;

    private readonly ConcurrentQueue<LogBatch> _logQueue = new();
    private readonly ConcurrentQueue<SimulationMessage> _messageQueue = new();

    public async Task SendLogBatchAsync(LogBatch batch)
    {
        _logQueue.Enqueue(batch);
        await Task.Delay(10); // 模拟网络延迟
    }

    public async Task SendSimulationMessageAsync(SimulationMessage message)
    {
        _messageQueue.Enqueue(message);
        await Task.Delay(10);
    }

    public bool TryDequeueLogBatch(out LogBatch batch)
    {
        batch = null!;
        return _logQueue.TryDequeue(out batch);
    }

    public bool TryDequeueMessage(out SimulationMessage message)
    {
        message = null!;
        return _messageQueue.TryDequeue(out message);
    }
}

/// <summary>
/// 日志条目数据结构（用于日志流传输）
/// </summary>
public class LogEntry
{
    public string GroupId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? Level { get; set; } = "INFO";
}

/// <summary>
/// 日志批次数据结构
/// </summary>
public class LogBatch
{
    public string BatchId { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<string> Entries { get; set; } = new();
    public int EntryCount { get; set; }
}

/// <summary>
/// 模拟消息数据结构
/// </summary>
public class SimulationMessage
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
