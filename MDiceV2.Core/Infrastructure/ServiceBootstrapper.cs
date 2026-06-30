using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MDiceV2.Abstractions;
using MDiceV2.Models;
using MDiceV2.Core.Mod;
using MDiceV2.Core.Infrastructure.Configurers;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Grpc.Net.Client;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// 服务启动引导程序
/// 根据启动模式配置和初始化所有依赖项
/// 是应用DI容器的中央配置点
/// </summary>
public static class ServiceBootstrapper
{
    /// <summary>
    /// 当前的启动模式（用于重启和更新时判断应该启动哪个可执行文件）
    /// </summary>
    public static StartupMode CurrentStartupMode { get; private set; } = StartupMode.UI;

    /// <summary>
    /// 构建完整的服务提供程序
    /// </summary>
    public static IServiceProvider BuildServices(StartupMode mode)
    {
        // 保存当前启动模式（用于重启和更新时判断应该启动哪个可执行文件）
        CurrentStartupMode = mode;

        var services = new ServiceCollection();

        // 注册日志记录服务 - 所有需要 ILogger<T> 的类都能使用
        services.AddLogging(logging =>
        {
            // 无头模式下配置控制台输出
            if (mode == StartupMode.Console)
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            }
        });

        // 注册平台特定的Dispatcher实现
        switch (mode)
        {
            case StartupMode.UI:
                services.AddSingleton<IDispatcher, AvaloniaDispatcher>();
                break;

            case StartupMode.Console:
            case StartupMode.RemoteSyncServer:
                services.AddSingleton<IDispatcher, ConsoleDispatcher>();
                break;

            case StartupMode.RemoteSyncClient:
                services.AddSingleton<IDispatcher, AvaloniaDispatcher>();
                break;
        }

        // 注册消息通道实现
        switch (mode)
        {
            case StartupMode.UI:
                // UI模式下使用WebSocket
                services.AddSingleton<IMessageChannel, WebSocketChannel>();
                break;

            case StartupMode.Console:
                // Console模式：不注册IMessageChannel，由Program.cs直接管理WSconnection
                // services.AddSingleton<IMessageChannel, WebSocketChannel>();  // 禁用：避免重复连接
                break;

            case StartupMode.RemoteSyncServer:
                // 服务器模式下使用模拟消息源
                services.AddSingleton<IMessageChannel, MockMessageChannel>();
                break;

            case StartupMode.RemoteSyncClient:
                // 客户端模式下使用gRPC代理通道
                services.AddSingleton<IMessageChannel, GrpcProxyChannel>();
                services.AddSingleton<IConfigSyncClient, GrpcConfigSyncClient>();
                break;
        }

        // 注册远程同步服务（根据启动模式）
        switch (mode)
        {
            case StartupMode.RemoteSyncServer:
                // 服务器模式：注册真实 gRPC 服务器宿主
                services.AddSingleton<GrpcServerHost>();
                break;

            case StartupMode.RemoteSyncClient:
                // 客户端模式：GrpcConfigSyncClient 已在上面注册
                break;

            default:
                // UI 和 Console 模式下：延迟注册（在下面手动创建）
                break;
        }

        // 共用的核心服务
        services.AddSingleton<DataIO>();
        services.AddSingleton<RuleDataIO>();
        // GlobalFeedbackMessages is a static class, no factory needed

        // MessageProcessor依赖注入（替代单例）
        services.AddSingleton<MessageProcessor>();

        // 其他基础设施服务
        services.AddSingleton<TRPGLogManager>();
        services.AddSingleton<ModEventBridge>();

        // 注册配置应用器系统
        services.AddSingleton<ConfigApplierRegistry>();
        services.AddSingleton<BasicConfigurer>();
        services.AddSingleton<FeedbackTemplateConfigurer>();
        services.AddSingleton<HelpMessageConfigurer>();
        
        // ✅ 【重构】使用工厂方法创建 ConfigSyncServer，移除对 Registry 的硬编码依赖
        services.AddSingleton<IConfigSyncServer>(provider =>
        {
            // 注意：具体的数据提供者会在 MainViewModel 中被重新赋值或直接在 UI 启动时初始化新的 Host
            return new ConfigSyncServer("default-password", null);
        });
        services.AddSingleton(provider => (ConfigSyncServer)provider.GetRequiredService<IConfigSyncServer>());

        var serviceProvider = services.BuildServiceProvider();

        // 初始时注册核心 Configurer 到 ConfigApplierRegistry
        var registry = serviceProvider.GetRequiredService<ConfigApplierRegistry>();
        registry.Register(serviceProvider.GetRequiredService<BasicConfigurer>());
        registry.Register(serviceProvider.GetRequiredService<FeedbackTemplateConfigurer>());
        registry.Register(serviceProvider.GetRequiredService<HelpMessageConfigurer>());

        // ✅ 【统一初始化】确保 GlobalMessageQueue 单例在所有启动模式下都被创建
        // 这在中央位置处理，避免在多个地方（App.cs、Program.cs等）重复初始化
        // GlobalMessageQueue 本身不涉及 UI，在所有模式下都是安全的
        try
        {
            if (GlobalMessageQueue.Instance == null)
            {
                _ = new GlobalMessageQueue();
                Log.InfoFormat("[ServiceBootstrapper] ✓ GlobalMessageQueue 单例已初始化 (模式: {0})", mode);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[ServiceBootstrapper] 初始化 GlobalMessageQueue 失败: {ex.Message}");
            throw;
        }

        return serviceProvider;
    }

    /// <summary>
    /// 验证所有必需的服务都已正确注册
    /// 在启动时调用以捕获配置错误
    /// </summary>
    public static void ValidateServices(IServiceProvider serviceProvider)
    {
        try
        {
            var mode = CurrentStartupMode;

            // 尝试解析所有关键服务
            _ = serviceProvider.GetRequiredService<IDispatcher>();
            if (mode != StartupMode.Console)
            {
                _ = serviceProvider.GetRequiredService<IMessageChannel>();
            }
            else
            {
                Log.InfoFormat("[ServiceBootstrapper] Console模式由Program直接管理WSconnection，跳过IMessageChannel验证");
            }
            _ = serviceProvider.GetRequiredService<DataIO>();
            _ = serviceProvider.GetRequiredService<MessageProcessor>();

            Log.InfoFormat("[ServiceBootstrapper] 所有必需的服务已成功注册 (模式: {0})", mode);
        }
        catch (InvalidOperationException ex)
        {
            Log.Error($"[ServiceBootstrapper] 服务配置验证失败: {ex.Message}");
            throw;
        }
    }
}

// ============== Dispatcher实现 ==============

/// <summary>
/// Avalonia UI线程调度器实现
/// 用于UI模式（有UI界面）
/// </summary>
public class AvaloniaDispatcher : IDispatcher
{
#if !CONSOLE_MODE
    public void Post(Action action)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    public Task PostAsync(Func<Task> action)
    {
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
    }

    public Task<T> PostAsync<T>(Func<Task<T>> action)
    {
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
    }
#else
    public void Post(Action action) => throw new NotSupportedException("Avalonia不支持Console模式");
    public Task PostAsync(Func<Task> action) => throw new NotSupportedException("Avalonia不支持Console模式");
    public Task<T> PostAsync<T>(Func<Task<T>> action) => throw new NotSupportedException("Avalonia不支持Console模式");
#endif
}

/// <summary>
/// Console/无头模式调度器实现
/// 直接同步执行，不涉及UI线程
/// </summary>
public class ConsoleDispatcher : IDispatcher
{
    public void Post(Action action)
    {
        action();
    }

    public async Task PostAsync(Func<Task> action)
    {
        await action();
    }

    public async Task<T> PostAsync<T>(Func<Task<T>> action)
    {
        return await action();
    }
}

// ============== 消息通道实现 ==============

/// <summary>
/// WebSocket消息通道实现
/// 包装现有的WSConnection逻辑，支持实时消息通信
/// </summary>
public class WebSocketChannel : IMessageChannel
{
    private System.Net.WebSockets.ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;
    private string _url = string.Empty;

    public event EventHandler<MessageReceivedEventArgs>? OnMessageReceived;
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(string url)
    {
        try
        {
            _url = url;
            _webSocket = new System.Net.WebSockets.ClientWebSocket();
            _cancellationTokenSource = new CancellationTokenSource();

            await _webSocket.ConnectAsync(new Uri(url), _cancellationTokenSource.Token);
            IsConnected = true;

            // 启动接收循环
            _ = ReceiveLoopAsync();

            Log.InfoFormat($"[WebSocketChannel] 已连接到: {url}");
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Log.Error($"[WebSocketChannel] 连接失败: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_webSocket?.State == System.Net.WebSockets.WebSocketState.Open)
            {
                _cancellationTokenSource?.Cancel();
                await _webSocket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
            }

            _webSocket?.Dispose();
            _cancellationTokenSource?.Dispose();
            IsConnected = false;

            Log.InfoFormat("[WebSocketChannel] 已断开连接");
        }
        catch (Exception ex)
        {
            Log.Warn($"[WebSocketChannel] 断开连接时出错: {ex.Message}");
        }
    }

    public async Task SendReplyAsync(string groupId, string userId, string message)
    {
        if (!IsConnected || _webSocket == null)
        {
            Log.Warn("[WebSocketChannel] 未连接，无法发送消息");
            return;
        }

        try
        {
            var payload = new
            {
                groupId,
                userId,
                message,
                timestamp = DateTime.UtcNow.Ticks
            };

            string json = System.Text.Json.JsonSerializer.Serialize(payload);
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);

            await _webSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                System.Net.WebSockets.WebSocketMessageType.Text,
                true,
                _cancellationTokenSource?.Token ?? CancellationToken.None);

            Log.InfoFormat($"[WebSocketChannel] 已发送消息给群{groupId}用户{userId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[WebSocketChannel] 发送消息失败: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[4096];

        try
        {
            while (IsConnected && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    _cancellationTokenSource?.Token ?? CancellationToken.None);

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                {
                    string json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    try
                    {
                        var messageData = System.Text.Json.JsonDocument.Parse(json);
                        var root = messageData.RootElement;

                        OnMessageReceived?.Invoke(this, new MessageReceivedEventArgs
                        {
                            Source = root.GetProperty("groupId").GetString() ?? string.Empty,
                            UserId = root.GetProperty("userId").GetString() ?? string.Empty,
                            Content = root.GetProperty("content").GetString() ?? string.Empty,
                            IsSimulationMode = root.TryGetProperty("isSimulation", out var sim) 
                                && sim.GetBoolean(),
                            ReceivedTime = DateTime.UtcNow
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[WebSocketChannel] 解析消息失败: {ex.Message}");
                    }
                }
                else if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await DisconnectAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.InfoFormat("[WebSocketChannel] 接收循环已取消");
        }
        catch (Exception ex)
        {
            Log.Error($"[WebSocketChannel] 接收循环出错: {ex.Message}");
            IsConnected = false;
        }
    }
}

/// <summary>
/// 模拟消息通道实现
/// 用于Console/服务器模式下的测试和离线演示
/// 支持模拟的消息接收和发送
/// </summary>
public class MockMessageChannel : IMessageChannel
{
    private System.Collections.Concurrent.ConcurrentQueue<MessageReceivedEventArgs> _messageQueue;
    private CancellationTokenSource? _cancellationTokenSource;

    public event EventHandler<MessageReceivedEventArgs>? OnMessageReceived;
    public bool IsConnected { get; private set; }

    public MockMessageChannel()
    {
        _messageQueue = new System.Collections.Concurrent.ConcurrentQueue<MessageReceivedEventArgs>();
    }

    public async Task ConnectAsync(string url)
    {
        try
        {
            IsConnected = true;
            _cancellationTokenSource = new CancellationTokenSource();

            // 启动模拟消息处理循环
            _ = ProcessMockMessagesAsync();

            Log.InfoFormat($"[MockMessageChannel] 已连接到模拟源: {url}");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Log.Error($"[MockMessageChannel] 连接失败: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
            IsConnected = false;
            Log.InfoFormat("[MockMessageChannel] 已断开连接");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Warn($"[MockMessageChannel] 断开连接时出错: {ex.Message}");
        }
    }

    public async Task SendReplyAsync(string groupId, string userId, string message)
    {
        try
        {
            Log.InfoFormat($"[MockMessageChannel] 模拟发送消息给群{groupId}用户{userId}: {message}");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error($"[MockMessageChannel] 发送消息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 用于测试的方法：模拟接收消息
    /// </summary>
    public void SimulateMessageReceived(string groupId, string userId, string content, bool isSimulation = false)
    {
        var args = new MessageReceivedEventArgs
        {
            Source = groupId,
            UserId = userId,
            Content = content,
            IsSimulationMode = isSimulation,
            ReceivedTime = DateTime.UtcNow
        };

        _messageQueue.Enqueue(args);
        Log.InfoFormat($"[MockMessageChannel] 已加入模拟消息: 群{groupId}, 用户{userId}");
    }

    private async Task ProcessMockMessagesAsync()
    {
        try
        {
            while (IsConnected && _cancellationTokenSource != null)
            {
                if (_messageQueue.TryDequeue(out var message))
                {
                    OnMessageReceived?.Invoke(this, message);
                    Log.InfoFormat($"[MockMessageChannel] 已处理模拟消息: {message.Content}");
                }

                await Task.Delay(100, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Log.InfoFormat("[MockMessageChannel] 消息处理循环已取消");
        }
        catch (Exception ex)
        {
            Log.Error($"[MockMessageChannel] 消息处理出错: {ex.Message}");
        }
    }
}

/// <summary>
/// gRPC代理消息通道实现
/// 用于远程同步客户端模式
/// 通过gRPC将消息和操作转发到远程服务器
/// </summary>
public class GrpcProxyChannel : IMessageChannel
{
    private readonly IConfigSyncClient _configClient;
    private Grpc.Net.Client.GrpcChannel? _grpcChannel;
    private string _serverUrl = string.Empty;
    private CancellationTokenSource? _cancellationTokenSource;

    public event EventHandler<MessageReceivedEventArgs>? OnMessageReceived;
    public bool IsConnected { get; private set; }

    public GrpcProxyChannel(IConfigSyncClient configClient)
    {
        _configClient = configClient;
    }

    public async Task ConnectAsync(string url)
    {
        try
        {
            _serverUrl = url;
            _cancellationTokenSource = new CancellationTokenSource();

            // 解析URL并建立gRPC连接
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = $"https://{url}";
            }

            _grpcChannel = Grpc.Net.Client.GrpcChannel.ForAddress(url);
            
            // 尝试连接以验证可达性
            var healthCheck = _grpcChannel.CreateCallInvoker();
            
            IsConnected = true;
            Log.InfoFormat($"[GrpcProxyChannel] 已连接到远程服务器: {url}");

            // 启动心跳检测
            _ = HeartbeatLoopAsync();

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Log.Error($"[GrpcProxyChannel] 连接失败: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
            if (_grpcChannel != null)
            {
                await _grpcChannel.ShutdownAsync();
                _grpcChannel.Dispose();
            }

            IsConnected = false;
            Log.InfoFormat("[GrpcProxyChannel] 已断开连接");
        }
        catch (Exception ex)
        {
            Log.Warn($"[GrpcProxyChannel] 断开连接时出错: {ex.Message}");
        }
    }

    public async Task SendReplyAsync(string groupId, string userId, string message)
    {
        if (!IsConnected)
        {
            Log.Warn("[GrpcProxyChannel] 未连接，无法发送消息");
            return;
        }

        try
        {
            // 通过gRPC将模拟消息发送到远程服务器
            await _configClient.PushSimulationMessageAsync(userId, message);
            Log.InfoFormat($"[GrpcProxyChannel] 已通过gRPC发送消息给用户{userId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[GrpcProxyChannel] 发送消息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从远程服务器拉取消息和更新
    /// 通过gRPC流式通信
    /// </summary>
    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (IsConnected && _cancellationTokenSource != null)
            {
                try
                {
                    // 心跳间隔：30秒
                    await Task.Delay(30000, _cancellationTokenSource.Token);

                    // 验证连接健康状态
                    if (_grpcChannel != null)
                    {
                        var state = _grpcChannel.State;
                        Log.InfoFormat($"[GrpcProxyChannel] 心跳检测 - 连接状态: {state}");

                        if (state != Grpc.Core.ConnectivityState.Ready)
                        {
                            Log.Warn("[GrpcProxyChannel] 连接不健康");
                            IsConnected = false;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[GrpcProxyChannel] 心跳检测失败: {ex.Message}");
                    IsConnected = false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[GrpcProxyChannel] 心跳循环出错: {ex.Message}");
        }
    }
}
