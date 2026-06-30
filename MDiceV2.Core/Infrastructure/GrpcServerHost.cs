using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.AspNetCore.Server;
using MDiceV2.Abstractions;
using MDiceV2.Models;
using MDiceV2.Core.Infrastructure.Configurers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// 真实 gRPC 服务器宿主 - 使用 ASP.NET Core Kestrel
/// 在后台线程运行，不阻塞 UI 线程
/// 监听指定端口并处理客户端请求
/// </summary>
public class GrpcServerHost
{
    private readonly string _serverPassword;
    private readonly SyncConfigManager _configSyncManager;
    private readonly ConfigSyncServer _configSyncServer;
    private IHost? _webHost;
    private bool _isRunning;
    private CancellationTokenSource? _hostCancellationTokenSource;
    private Task? _hostTask;
    private Action<List<(string, string)>>? _onConfigAppliedCallback;

    public bool IsRunning => _isRunning;
    
    // ✅ 【新增】公开服务器实例 - 用于订阅配置更新事件
    public ConfigSyncServer ConfigServer => _configSyncServer;

    public GrpcServerHost(string serverPassword, SyncConfigManager? configSyncManager = null, Func<Dictionary<string, string>>? configProvider = null)
    {
        _serverPassword = serverPassword ?? throw new ArgumentNullException(nameof(serverPassword));
        _configSyncManager = configSyncManager ?? new SyncConfigManager();
        _configSyncServer = new ConfigSyncServer(serverPassword, configProvider);
        _isRunning = false;
        _onConfigAppliedCallback = null;
    }

    /// <summary>
    /// 注册配置应用成功后的回调（由MainViewModel调用以更新UI）
    /// </summary>
    public void RegisterConfigAppliedCallback(Action<List<(string, string)>> callback)
    {
        _onConfigAppliedCallback = callback;
        LogInfo("[GrpcServerHost] 配置应用回调已注册");
    }

    /// <summary>
    /// 启动 gRPC 服务器（真实实现）
    /// 在后台任务中运行 ASP.NET Core 最小主机
    /// </summary>
    public async Task StartAsync(int port)
    {
        try
        {
            if (_isRunning)
            {
                LogWarn($"[GrpcServerHost] gRPC 服务器已在运行于端口 {port}");
                return;
            }

            LogInfo($"[GrpcServerHost] 准备启动 gRPC 服务器，监听端口: {port}");

            _hostCancellationTokenSource = new CancellationTokenSource();

            // 在后台线程上启动 ASP.NET Core 最小主机
            _hostTask = Task.Run(async () =>
            {
                try
                {
                    var hostBuilder = Host.CreateDefaultBuilder()
                        .ConfigureWebHostDefaults(webBuilder =>
                        {
                            webBuilder
                                .UseKestrel(options =>
                                {
                                    options.ListenAnyIP(port, listenOptions =>
                                    {
                                        listenOptions.Protocols = HttpProtocols.Http2;
                                    });
                                })
                                .ConfigureServices(services =>
                                {
                                    services.AddGrpc(options =>
                                    {
                                        // Register the exception interceptor to log detailed error information
                                        options.Interceptors.Add<GrpcExceptionInterceptor>();
                                    });
                                    services.AddSingleton<GrpcExceptionInterceptor>();
                                    services.AddSingleton(_configSyncManager);
                                    services.AddSingleton(_configSyncServer);
                                    
                                    // Register ConfigSyncServiceImpl with its dependencies through a factory
                                    services.AddSingleton<ConfigSyncServiceImpl>(provider =>
                                    {
                                        var configServer = provider.GetRequiredService<ConfigSyncServer>();
                                        
                                        // ✅ 创建 ConfigSyncService 实例 (不再依赖 Registry)
                                        var service = new ConfigSyncServiceImpl(_serverPassword, configServer);
                                        return service;
                                    });
                                })
                                .Configure(app =>
                                {
                                    app.UseRouting();
                                    app.UseEndpoints(endpoints =>
                                    {
                                        endpoints.MapGrpcService<ConfigSyncServiceImpl>();
                                    });
                                });
                        });

                    _webHost = hostBuilder.Build();
                    
                    LogInfo($"[GrpcServerHost] ✓ 配置 Kestrel 监听 0.0.0.0:{port}，协议: HTTP/2");
                    LogInfo($"[GrpcServerHost] ✓ ASP.NET Core 主机已创建");
                    LogInfo($"[GrpcServerHost] ✓ 已注册 ConfigSyncService 端点");
                    LogInfo($"[GrpcServerHost] ✓ 启动 gRPC 服务器...");

                    await _webHost.RunAsync(_hostCancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    LogInfo("[GrpcServerHost] gRPC 服务器已停止（取消令牌）");
                }
                catch (Exception ex)
                {
                    LogError($"[GrpcServerHost] ❌ gRPC 服务器异常: {ex.Message}");
                    LogError($"[GrpcServerHost] 堆栈: {ex.StackTrace}");
                }
            }, _hostCancellationTokenSource.Token);

            _isRunning = true;
            
            // 等待服务器完全启动（Kestrel 需要时间初始化）
            // 使用多次短延迟以检测启动完成或失败
            bool serverStartupConfirmed = false;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(250); // 每个周期 250ms
                
                // 检查后台任务是否已崩溃
                if (_hostTask?.IsFaulted == true)
                {
                    LogError("[GrpcServerHost] ❌ 后台服务器任务失败");
                    if (_hostTask.Exception != null)
                    {
                        LogError($"[GrpcServerHost] 异常: {_hostTask.Exception.InnerException?.Message}");
                    }
                    _isRunning = false;
                    throw new InvalidOperationException("Server startup failed");
                }
                
                // 如果经过了 3 秒以上，认为启动完成
                if (i >= 12) // 12 * 250ms = 3 秒
                {
                    serverStartupConfirmed = true;
                    break;
                }
            }

            if (serverStartupConfirmed)
            {
                LogInfo($"[GrpcServerHost] ✅ gRPC 服务器已启动，监听端口: {port}");
                
                // 打印服务器配置信息用于调试
                LogInfo("========== [服务器配置信息] ==========");
                LogInfo($"[GrpcServerHost] 监听地址: 0.0.0.0");
                LogInfo($"[GrpcServerHost] 监听端口: {port}");
                LogInfo($"[GrpcServerHost] 协议: HTTP/2 (gRPC)");
                LogInfo($"[GrpcServerHost] 服务名: ConfigSyncService");
                LogInfo($"[GrpcServerHost] 服务密钥: {(_serverPassword.Length > 8 ? _serverPassword.Substring(0, 8) + "..." : "***")}");
                LogInfo($"[GrpcServerHost] 运行状态: Running");
                LogInfo($"[GrpcServerHost] 启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                LogInfo("====================================");
            }
            else
            {
                LogError($"[GrpcServerHost] ❌ gRPC 服务器启动超时");
                _isRunning = false;
                throw new TimeoutException("Server startup timeout");
            }
        }
        catch (Exception ex)
        {
            LogError($"[GrpcServerHost] ❌ 启动服务器失败: {ex.Message}");
            LogError($"[GrpcServerHost] 堆栈: {ex.StackTrace}");
            _isRunning = false;
            throw;
        }
    }

    /// <summary>
    /// 停止 gRPC 服务器
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            if (!_isRunning)
            {
                LogWarn("[GrpcServerHost] 服务器未运行");
                return;
            }

            LogInfo("[GrpcServerHost] 正在停止 gRPC 服务器...");

            // 发送取消令牌
            _hostCancellationTokenSource?.Cancel();

            // 停止 WebHost
            if (_webHost != null)
            {
                await _webHost.StopAsync();
                _webHost.Dispose();
                _webHost = null;
                LogInfo("[GrpcServerHost] ✓ ASP.NET Core 主机已停止");
            }

            // 等待后台任务完成
            if (_hostTask != null)
            {
                try
                {
                    await _hostTask.ConfigureAwait(false);
                    LogInfo("[GrpcServerHost] ✓ 后台任务已完成");
                }
                catch (OperationCanceledException)
                {
                    // 预期的 - 取消令牌触发
                }
                catch (Exception ex)
                {
                    LogError($"[GrpcServerHost] 后台任务异常: {ex.Message}");
                }
            }

            _isRunning = false;
            LogInfo("[GrpcServerHost] ✓ gRPC 服务器已完全停止");
        }
        catch (Exception ex)
        {
            LogError($"[GrpcServerHost] ❌ 停止服务器失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 获取配置同步服务器实例
    /// </summary>
    public ConfigSyncServer GetConfigServer() => _configSyncServer;

    private void LogInfo(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        LogSender.Normal(formatted);
    }

    private void LogWarn(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        LogSender.Warn(formatted);
    }

    private void LogError(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        LogSender.Error(formatted);
    }
}

