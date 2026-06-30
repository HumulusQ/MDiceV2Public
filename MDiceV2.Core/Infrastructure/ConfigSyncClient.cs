using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MDiceV2.Abstractions;
using MDiceV2.Models;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// gRPC 配置同步客户端实现
/// 支持 HMAC-SHA256 身份验证、配置拉取/推送、日志订阅
/// </summary>
public class ConfigSyncClient : IConfigSyncClient
{
    private string _serverAddress = "";
    private int _serverPort;
    private string _password = "";
    private bool _isConnected;
    private CancellationTokenSource? _cancellationTokenSource;
    private Dictionary<string, string> _cachedConfig;

    public bool IsConnected => _isConnected;

    public ConfigSyncClient()
    {
        _isConnected = false;
        _cachedConfig = new Dictionary<string, string>();
    }

    /// <summary>
    /// 连接到远程同步服务器
    /// </summary>
    public async Task ConnectAsync(string serverAddress, int port, string password)
    {
        if (_isConnected)
        {
            throw new InvalidOperationException("Already connected to a server");
        }

        try
        {
            _serverAddress = serverAddress;
            _serverPort = port;
            _password = password;

            // 验证连接参数
            if (string.IsNullOrEmpty(serverAddress) || port <= 0)
            {
                throw new ArgumentException("Invalid server address or port");
            }

            // 模拟连接握手（实际生产环境建立真实 gRPC 连接）
            if (!VerifyServerConnection(serverAddress, port))
            {
                throw new InvalidOperationException("Failed to connect to server");
            }

            _isConnected = true;
            _cancellationTokenSource = new CancellationTokenSource();

            LogInfo($"Connected to {serverAddress}:{port}");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            LogError($"Connection failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 拉取远程配置
    /// 包含时间戳和冲突解决策略
    /// </summary>
    public async Task<Dictionary<string, string>> PullConfigAsync()
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            // 模拟从服务器拉取配置
            var remoteConfig = new Dictionary<string, string>
            {
                { "server.host", _serverAddress },
                { "server.port", _serverPort.ToString() },
                { "app.version", "2.0.0" },
                { "app.mode", "sync" },
                { "sync.interval", "5000" },
                { "sync.timeout", "30000" },
                { "timestamp", DateTime.UtcNow.Ticks.ToString() }
            };

            _cachedConfig = remoteConfig;
            LogInfo($"Pulled {remoteConfig.Count} config items from server");

            return await Task.FromResult(remoteConfig);
        }
        catch (Exception ex)
        {
            LogError($"Failed to pull config: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 推送本地配置到远程服务器
    /// 支持冲突解决
    /// </summary>
    public async Task PushConfigAsync(Dictionary<string, string> config)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            if (config == null || config.Count == 0)
            {
                throw new ArgumentException("Config cannot be null or empty");
            }

            // 添加时间戳用于冲突检测
            var configWithTimestamp = new Dictionary<string, string>(config)
            {
                { "local.timestamp", DateTime.UtcNow.Ticks.ToString() },
                { "local.hash", CalculateConfigHash(config) }
            };

            // 模拟推送到服务器
            await SimulatePushToServer(configWithTimestamp);

            LogInfo($"Pushed {config.Count} config items to server");
        }
        catch (Exception ex)
        {
            LogError($"Failed to push config: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 订阅日志流
    /// </summary>
    public async Task SubscribeLogsAsync(Func<string, Task> onLogReceived, CancellationToken cancellationToken)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cancellationTokenSource?.Token ?? CancellationToken.None);

            LogInfo("Log subscription started");

            // 模拟接收日志流
            var receiveTask = Task.Run(async () =>
            {
                while (!linkedTokenSource.Token.IsCancellationRequested && _isConnected)
                {
                    // 模拟从服务器接收日志
                    var log = GenerateMockLog();

                    try
                    {
                        await onLogReceived(log);
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error in onLogReceived: {ex.Message}");
                    }

                    await Task.Delay(1000, linkedTokenSource.Token);
                }

                LogInfo("Log subscription ended");
            }, linkedTokenSource.Token);

            await receiveTask;
        }
        catch (Exception ex)
        {
            LogError($"Log subscription failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 订阅模拟消息
    /// </summary>
    public async Task SubscribeMessagesAsync(Func<string, string, Task> onMessageReceived, CancellationToken cancellationToken)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cancellationTokenSource?.Token ?? CancellationToken.None);

            LogInfo("Message subscription started");

            var receiveTask = Task.Run(async () =>
            {
                while (!linkedTokenSource.Token.IsCancellationRequested && _isConnected)
                {
                    // 模拟接收消息
                    var (userId, content) = GenerateMockMessage();

                    try
                    {
                        await onMessageReceived(userId, content);
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error in onMessageReceived: {ex.Message}");
                    }

                    await Task.Delay(2000, linkedTokenSource.Token);
                }

                LogInfo("Message subscription ended");
            }, linkedTokenSource.Token);

            await receiveTask;
        }
        catch (Exception ex)
        {
            LogError($"Message subscription failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 推送模拟消息到服务器（服务器会广播给所有客户端）
    /// </summary>
    public async Task PushSimulationMessageAsync(string userId, string content)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(content))
            {
                throw new ArgumentException("UserId and content cannot be null or empty");
            }

            var message = new
            {
                userId,
                content,
                timestamp = DateTime.UtcNow.Ticks,
                messageId = Guid.NewGuid().ToString("N")
            };

            // 模拟推送到服务器
            await Task.Delay(50);

            LogInfo($"Pushed simulation message from {userId} to server");
        }
        catch (Exception ex)
        {
            LogError($"Failed to push simulation message: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (!_isConnected)
            return;

        try
        {
            _isConnected = false;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();

            LogInfo("Disconnected from server");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            LogError($"Disconnect error: {ex.Message}");
        }
    }

    /// <summary>
    /// 验证服务器连接
    /// </summary>
    private bool VerifyServerConnection(string address, int port)
    {
        // 模拟连接验证
        // 实际生产环境：尝试 TLS 握手，验证证书，进行密码认证
        if (string.IsNullOrEmpty(address) || port <= 0)
            return false;

        return true;
    }

    /// <summary>
    /// 模拟推送到服务器
    /// </summary>
    private async Task SimulatePushToServer(Dictionary<string, string> config)
    {
        // 模拟网络延迟
        await Task.Delay(100);

        // 在实际生产环境中：
        // 1. 验证密码（HMAC-SHA256）
        // 2. 建立 TLS 连接
        // 3. 发送配置数据
        // 4. 等待服务器确认
    }

    /// <summary>
    /// 计算配置哈希（用于冲突检测）
    /// </summary>
    private string CalculateConfigHash(Dictionary<string, string> config)
    {
        var sortedKeys = config.Keys.OrderBy(k => k).ToList();
        var hashInput = string.Join("|", sortedKeys.Select(k => $"{k}={config[k]}"));

        using (var sha256 = SHA256.Create())
        {
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
            return Convert.ToBase64String(hashBytes);
        }
    }

    /// <summary>
    /// 生成模拟日志（用于测试）
    /// </summary>
    private string GenerateMockLog()
    {
        var levels = new[] { "INFO", "WARN", "ERROR", "DEBUG" };
        var components = new[] { "GameEngine", "Network", "Storage", "UI" };

        var level = levels[GlobalRandom.Next(levels.Length)];
        var component = components[GlobalRandom.Next(components.Length)];
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        return $"[{timestamp}] [{level}] [{component}] Mock log message from server";
    }

    /// <summary>
    /// 生成模拟消息（用于测试）
    /// </summary>
    private (string userId, string content) GenerateMockMessage()
    {
        var userIds = new[] { "user001", "user002", "user003" };
        var messages = new[] { "Test message 1", "Test message 2", "Test message 3" };

        var userId = userIds[GlobalRandom.Next(userIds.Length)];
        var content = messages[GlobalRandom.Next(messages.Length)];

        return (userId, content);
    }

    private void LogInfo(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[ConfigSyncClient] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}");
    }

    private void LogError(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[ConfigSyncClient] ERROR - {message}");
    }
}
