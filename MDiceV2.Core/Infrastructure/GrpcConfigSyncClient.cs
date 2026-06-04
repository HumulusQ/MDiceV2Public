using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using MDiceV2.Abstractions;
using MDiceV2.Models;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// 真实 gRPC 配置同步客户端实现
/// 使用 Grpc.Net.Client 连接到远程 ConfigSyncService
/// </summary>
public class GrpcConfigSyncClient : IConfigSyncClient
{
    private GrpcChannel? _channel;
    private Mdv2.Remotesync.ConfigSyncService.ConfigSyncServiceClient? _client;
    private string _serverAddress = string.Empty;
    private int _serverPort;
    private string _password = string.Empty;
    private bool _isConnected;
    private CancellationTokenSource? _cancellationTokenSource;
    private string _clientId = string.Empty;

        public bool IsConnected => _isConnected;

    public GrpcConfigSyncClient()
    {
        _isConnected = false;
    }

    /// <summary>
    /// 连接到远程同步服务器
    /// </summary>
    public async Task ConnectAsync(string serverAddress, int port, string password)
    {
        // 打印完整的连接参数用于调试
        LogInfo("========== [客户端连接参数] ==========");
        LogInfo($"[GrpcConfigSyncClient] 目标服务器地址: {serverAddress}");
        LogInfo($"[GrpcConfigSyncClient] 目标服务器端口: {port}");
        LogInfo($"[GrpcConfigSyncClient] 连接密钥: {(password.Length > 8 ? password.Substring(0, 8) + "..." : "***")}");
        LogInfo($"[GrpcConfigSyncClient] 密钥长度: {password.Length}");
        LogInfo("====================================");

        if (_isConnected)
        {
            LogWarn("[GrpcConfigSyncClient] 已连接到服务器，断开旧连接");
            await DisconnectAsync();
        }

        try
        {
            _serverAddress = serverAddress;
            _serverPort = port;
            _password = password;

            // 验证连接参数
            if (string.IsNullOrEmpty(serverAddress) || port <= 0 || string.IsNullOrEmpty(password))
            {
                LogError("[GrpcConfigSyncClient] ❌ 无效的连接参数");
                throw new ArgumentException("Invalid server address, port, or password");
            }

            LogInfo($"[GrpcConfigSyncClient] ✓ 参数验证通过");

            // 构建 gRPC 服务器 URL
            string url = serverAddress.StartsWith("http://") || serverAddress.StartsWith("https://") 
                ? serverAddress 
                : $"http://{serverAddress}:{port}";

            LogInfo($"[GrpcConfigSyncClient] gRPC URL: {url}");

            // 创建 gRPC Channel + ✅ 配置 KeepAlive 保活
            LogDebug("[GrpcConfigSyncClient] 创建 GrpcChannel...");
            
            // 🆕 创建 HttpClientHandler 并启用 KeepAlive
            var handler = new SocketsHttpHandler
            {
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),      // 每 30 秒发送一次 ping
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),    // ping 超时 10 秒
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };

            var channelOptions = new GrpcChannelOptions
            {
                MaxReceiveMessageSize = 10 * 1024 * 1024, // 10 MB
                MaxSendMessageSize = 10 * 1024 * 1024,
                DisposeHttpClient = true,
                HttpHandler = handler  // 🆕 传入自定义 handler
            };

            _channel = GrpcChannel.ForAddress(url, channelOptions);
            LogInfo("[GrpcConfigSyncClient] ✓ GrpcChannel 创建成功");

            _client = new Mdv2.Remotesync.ConfigSyncService.ConfigSyncServiceClient(_channel);
            LogInfo("[GrpcConfigSyncClient] ✓ 客户端实例创建成功");

            _cancellationTokenSource = new CancellationTokenSource();

            // 执行认证 - 带重试逻辑
            LogDebug("[GrpcConfigSyncClient] 计算密码哈希...");
            var passwordHash = ComputePasswordHash(password);
            LogDebug($"[GrpcConfigSyncClient] ✓ 密码哈希: {(passwordHash.Length > 8 ? passwordHash.Substring(0, 8) + "..." : "***")}");

            var authRequest = new Mdv2.Remotesync.AuthRequest
            {
                PasswordHash = passwordHash,
                Timestamp = DateTime.UtcNow.Ticks.ToString()
            };

            // 使用重试逻辑尝试认证（应对服务器还在启动的情况）
            Mdv2.Remotesync.AuthResponse? authResponse = null;
            int maxRetries = 5;
            int retryDelayMs = 1000; // 1 秒

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    LogInfo($"[GrpcConfigSyncClient] 正在发送认证请求（尝试 {attempt}/{maxRetries}）...");
                    authResponse = await _client.AuthenticateAsync(
                        authRequest,
                        cancellationToken: _cancellationTokenSource.Token);
                    
                    LogInfo($"[GrpcConfigSyncClient] ✓ 收到认证响应: Success={authResponse.Success}, Message={authResponse.Message}");
                    break; // 成功，退出重试循环
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable && attempt < maxRetries)
                {
                    LogWarn($"[GrpcConfigSyncClient] ⚠ 服务器暂不可用，{retryDelayMs}ms 后重试... ({attempt}/{maxRetries})");
                    await Task.Delay(retryDelayMs);
                    retryDelayMs = Math.Min(retryDelayMs * 2, 5000); // 指数退避，最多 5 秒
                }
            }

            if (authResponse == null)
            {
                LogError($"[GrpcConfigSyncClient] ❌ 认证请求在 {maxRetries} 次重试后仍然失败");
                throw new InvalidOperationException("Failed to authenticate after multiple retries");
            }

            if (!authResponse.Success)
            {
                LogError($"[GrpcConfigSyncClient] ❌ 认证失败: {authResponse.Message}");
                throw new InvalidOperationException($"Authentication failed: {authResponse.Message}");
            }

            _clientId = authResponse.ClientId;
            _isConnected = true;

            LogInfo($"[GrpcConfigSyncClient] ✓ 已连接并认证成功");
            LogInfo($"[GrpcConfigSyncClient] 客户端 ID: {_clientId}");
            LogInfo("===== gRPC 连接成功 =====");
        }
        catch (Exception ex)
        {
            _isConnected = false;
            await CleanupChannelAsync();
            LogError($"[GrpcConfigSyncClient] ❌ 连接异常:");
            LogError($"[GrpcConfigSyncClient] 错误: {ex.Message}");
            LogError($"[GrpcConfigSyncClient] 堆栈: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                LogError($"[GrpcConfigSyncClient] 内部异常: {ex.InnerException.Message}");
            }
            LogError("===== gRPC 连接失败 =====");
            throw;
        }
    }

    /// <summary>
    /// 从服务器拉取远程配置
    /// </summary>
    public async Task<Dictionary<string, string>> PullConfigAsync()
    {
        if (!_isConnected || _client == null)
        {
            LogError("[GrpcConfigSyncClient] ❌ 未连接到服务器，无法拉取配置");
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            LogInfo("[GrpcConfigSyncClient] ===== 拉取远程配置开始 =====");
            LogInfo("[GrpcConfigSyncClient] 正在向服务器发送拉取请求...");
            LogWarn($"[GrpcConfigSyncClient] 【诊断】ClientId: {_clientId}");

            var pullRequest = new Mdv2.Remotesync.PullConfigRequest { ClientId = _clientId };
            var response = await _client.PullConfigAsync(
                pullRequest,
                cancellationToken: _cancellationTokenSource?.Token ?? CancellationToken.None);

            LogInfo("[GrpcConfigSyncClient] ✓ 服务器响应已接收");

            if (!response.Success)
            {
                // 🆕 检测是否是会话无效错误
                if (response.Message?.Contains("无有效会话") == true || 
                    response.Message?.Contains("invalid session") == true ||
                    string.IsNullOrEmpty(_clientId))
                {
                    LogWarn("[GrpcConfigSyncClient] ⚠ 检测到会话失效，尝试重新认证...");
                    await ReAuthenticateIfNeededAsync();
                    // 递归重试一次
                    if (_isConnected && !string.IsNullOrEmpty(_clientId))
                    {
                        LogInfo("[GrpcConfigSyncClient] 重新认证成功，重试拉取配置...");
                        return await PullConfigAsync();
                    }
                }
                LogError($"[GrpcConfigSyncClient] ❌ 拉取失败: {response.Message}");
                return new Dictionary<string, string>();
            }

            var config = new Dictionary<string, string>();
            foreach (var item in response.ConfigItems)
            {
                config[item.Key] = item.Value;
            }

            // 【新增诊断日志】
            LogWarn($"[GrpcConfigSyncClient] 【诊断】✓ 拉取了 {config.Count} 个配置项");
            LogWarn($"[GrpcConfigSyncClient] 【诊断】配置项类别统计:");
            int basicCount = config.Keys.Count(k => k.StartsWith("basic."));
            int feedbackCount = config.Keys.Count(k => k.StartsWith("feedback."));
            int helpCount = config.Keys.Count(k => k.StartsWith("help."));
            LogWarn($"[GrpcConfigSyncClient] 【统计】basic.xxx: {basicCount} 项");
            LogWarn($"[GrpcConfigSyncClient] 【统计】feedback.xxx: {feedbackCount} 项");
            LogWarn($"[GrpcConfigSyncClient] 【统计】help.xxx: {helpCount} 项");
            
            LogWarn($"[GrpcConfigSyncClient] 【诊断】返回的所有配置项:");
            foreach (var kvp in config)
            {
                LogWarn($"[GrpcConfigSyncClient] 【拉取项】{kvp.Key} = {(kvp.Value?.Length > 50 ? kvp.Value.Substring(0, 50) + "..." : kvp.Value)}");
            }
            
            LogInfo("[GrpcConfigSyncClient] ===== 拉取远程配置完成 =====");
            return config;
        }
        catch (Exception ex)
        {
            LogError($"[GrpcConfigSyncClient] ❌ 拉取配置异常: {ex.Message}");
            LogError($"[GrpcConfigSyncClient] 堆栈: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 推送本地配置到远程服务器
    /// </summary>
    public async Task PushConfigAsync(Dictionary<string, string> config)
    {
        if (!_isConnected || _client == null)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            if (config == null || config.Count == 0)
            {
                throw new ArgumentException("Config cannot be null or empty");
            }

            var request = new Mdv2.Remotesync.SyncConfigRequest();

            foreach (var kvp in config)
            {
                request.ConfigItems.Add(new Mdv2.Remotesync.ConfigItem
                {
                    Key = kvp.Key,
                    Value = kvp.Value,
                    UpdatedAtTicks = DateTime.UtcNow.Ticks,
                    LastModifiedBy = _clientId
                });
            }

            var response = await _client.PushConfigAsync(
                request,
                cancellationToken: _cancellationTokenSource?.Token ?? CancellationToken.None);

            if (!response.Success)
            {
                // 🆕 检测是否是会话无效错误
                if (response.Message?.Contains("无有效会话") == true || 
                    response.Message?.Contains("invalid session") == true ||
                    string.IsNullOrEmpty(_clientId))
                {
                    await ReAuthenticateIfNeededAsync();
                    // 递归重试一次
                    if (_isConnected && !string.IsNullOrEmpty(_clientId))
                    {
                        await PushConfigAsync(config);
                        return;
                    }
                }
                throw new InvalidOperationException($"Push failed: {response.Message}");
            }
        }
        catch (Exception ex)
        {
            throw;
        }
    }

    /// <summary>
    /// 订阅日志流（服务器端流）
    /// </summary>
    public async Task SubscribeLogsAsync(Func<string, Task> onLogReceived, CancellationToken cancellationToken)
    {
        if (!_isConnected || _client == null)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            LogInfo("[GrpcConfigSyncClient] 订阅日志流开始");

            var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cancellationTokenSource?.Token ?? CancellationToken.None);

            var subscribeRequest = new Mdv2.Remotesync.SubscribeLogsRequest { ClientId = _clientId };
            using var call = _client.SubscribeLogs(
                subscribeRequest,
                cancellationToken: linkedTokenSource.Token);

            await foreach (var logBatch in call.ResponseStream.ReadAllAsync(linkedTokenSource.Token))
            {
                try
                {
                    foreach (var logEntry in logBatch.Entries)
                    {
                        var logMessage = $"[{DateTime.FromBinary(logEntry.TimestampTicks):yyyy-MM-dd HH:mm:ss.fff}] [{logEntry.Level}] [{logEntry.GroupId}] {logEntry.Content}";
                        await onLogReceived(logMessage);
                    }

                    LogDebug($"[GrpcConfigSyncClient] 收到日志批次: {logBatch.Entries.Count} 条");
                }
                catch (Exception ex)
                {
                    LogError($"[GrpcConfigSyncClient] 处理日志异常: {ex.Message}");
                }
            }

            LogInfo("[GrpcConfigSyncClient] 日志流已断开");
        }
        catch (OperationCanceledException)
        {
            LogInfo("[GrpcConfigSyncClient] 日志流订阅已取消");
        }
        catch (Exception ex)
        {
            LogError($"[GrpcConfigSyncClient] 日志流异常: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 推送模拟消息到服务器（双向流）
    /// </summary>
    public async Task PushSimulationMessageAsync(string userId, string content)
    {
        if (!_isConnected || _client == null)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(content))
            {
                throw new ArgumentException("UserId and content cannot be null or empty");
            }

            LogDebug($"[GrpcConfigSyncClient] 推送模拟消息: {userId}");

            // 注意：这个实现是简化版，实际应该在 StreamSimulationMessages 中持续发送
            // 这里仅作为方便接口使用
            using var call = _client.StreamSimulationMessages(
                cancellationToken: _cancellationTokenSource?.Token ?? CancellationToken.None);

            var message = new Mdv2.Remotesync.SimulationMessage
            {
                UserId = userId,
                GroupId = "default",
                Content = content,
                TimestampTicks = DateTime.UtcNow.Ticks
            };

            await call.RequestStream.WriteAsync(message);

            if (await call.ResponseStream.MoveNext())
            {
                var ack = call.ResponseStream.Current;
                if (ack != null && ack.Success)
                {
                    LogDebug($"[GrpcConfigSyncClient] 消息已发送并收到确认");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"[GrpcConfigSyncClient] 推送消息异常: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (!_isConnected)
        {
            LogInfo("[GrpcConfigSyncClient] ℹ 未连接，跳过断开操作");
            return;
        }

        try
        {
            LogInfo("[GrpcConfigSyncClient] ===== 断开连接开始 =====");
            
            _isConnected = false;
            _clientId = string.Empty; // 🆕 清除 ClientId，防止过期的 ID 继续被使用
            LogInfo("[GrpcConfigSyncClient] ✓ _isConnected 设置为 false，ClientId 已清除");
            
            _cancellationTokenSource?.Cancel();
            LogInfo("[GrpcConfigSyncClient] ✓ 取消令牌已发出");
            
            await CleanupChannelAsync();
            LogInfo("[GrpcConfigSyncClient] ✓ 通道资源已清理");
            
            LogInfo("[GrpcConfigSyncClient] ===== 已断开连接完成 =====");
        }
        catch (Exception ex)
        {
            LogError($"[GrpcConfigSyncClient] ❌ 断开连接异常: {ex.Message}");
            LogError($"[GrpcConfigSyncClient] 堆栈: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 🆕 当检测到会话失效时，尝试重新认证
    /// </summary>
    private async Task ReAuthenticateIfNeededAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_serverAddress) || _serverPort <= 0 || string.IsNullOrEmpty(_password))
            {
                LogError("[GrpcConfigSyncClient] ❌ 重新认证所需的连接参数缺失");
                return;
            }

            LogInfo("[GrpcConfigSyncClient] 正在重新认证...");

            // 如果当前连接已断开，需要重新建立连接
            if (!_isConnected || _client == null)
            {
                LogInfo("[GrpcConfigSyncClient] 连接已断开，正在重新建立...");
                await ConnectAsync(_serverAddress, _serverPort, _password);
                LogInfo("[GrpcConfigSyncClient] ✓ 重新建立连接成功");
                return;
            }

            // 如果连接仍然存在，尝试重新进行认证
            LogInfo("[GrpcConfigSyncClient] 连接仍然存在，尝试重新认证...");
            var passwordHash = ComputePasswordHash(_password);
            var authRequest = new Mdv2.Remotesync.AuthRequest
            {
                PasswordHash = passwordHash,
                Timestamp = DateTime.UtcNow.Ticks.ToString()
            };

            var authResponse = await _client.AuthenticateAsync(
                authRequest,
                cancellationToken: _cancellationTokenSource?.Token ?? CancellationToken.None);

            if (authResponse.Success)
            {
                _clientId = authResponse.ClientId;
                _isConnected = true;
                LogInfo($"[GrpcConfigSyncClient] ✓ 重新认证成功，新 ClientId: {_clientId}");
            }
            else
            {
                LogError($"[GrpcConfigSyncClient] ❌ 重新认证失败: {authResponse.Message}");
                _isConnected = false;
                _clientId = string.Empty;
            }
        }
        catch (Exception ex)
        {
            LogError($"[GrpcConfigSyncClient] ❌ 重新认证异常: {ex.Message}");
            LogError($"[GrpcConfigSyncClient] 堆栈: {ex.StackTrace}");
            _isConnected = false;
            _clientId = string.Empty;
        }
    }

    /// <summary>
    /// 清理 gRPC 通道资源
    /// </summary>
    private async Task CleanupChannelAsync()
    {
        try
        {
            LogInfo("[GrpcConfigSyncClient] 开始清理通道...");
            
            if (_channel != null)
            {
                LogInfo("[GrpcConfigSyncClient] ✓ 正在关闭通道...");
                await _channel.ShutdownAsync();
                LogInfo("[GrpcConfigSyncClient] ✓ 通道已关闭");
                
                _channel.Dispose();
                LogInfo("[GrpcConfigSyncClient] ✓ 通道资源已释放");
                
                _channel = null;
            }
            else
            {
                LogInfo("[GrpcConfigSyncClient] ℹ 通道为 null");
            }

            _client = null;
            LogInfo("[GrpcConfigSyncClient] ✓ 客户端已清理");
            
            _cancellationTokenSource?.Dispose();
            LogInfo("[GrpcConfigSyncClient] ✓ 取消令牌源已释放");
            
            _cancellationTokenSource = null;
            LogInfo("[GrpcConfigSyncClient] ✓ 通道清理完成");
        }
        catch (Exception ex)
        {
            LogError($"[GrpcConfigSyncClient] ❌ 清理通道异常: {ex.Message}");
            LogError($"[GrpcConfigSyncClient] 堆栈: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 计算密码哈希（HMAC-SHA256）
    /// </summary>
    private string ComputePasswordHash(string password)
    {
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(password)))
        {
            var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hashBytes);
        }
    }

    private void LogInfo(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [GRPC-INFO] {message}";
        LogSender.Normal(formatted);
    }

    private void LogWarn(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [GRPC-WARN] {message}";
        LogSender.Warn(formatted);
    }

    private void LogError(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [GRPC-ERROR] {message}";
        LogSender.Error(formatted);
    }

    private void LogDebug(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [GRPC-DEBUG] {message}";
        LogSender.Normal(formatted);
    }
}
