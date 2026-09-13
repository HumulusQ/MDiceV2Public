using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MDiceV2.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// gRPC基础设施引导程序 - 在UI和Console版本中共享
/// 负责创建和初始化ConfigSyncDispatcher、SyncConfigManager、GrpcServerHost等
/// 版本特定的处理器注册由各版本自行实现
/// </summary>
public static class GrpcBootstrapper
{
    /// <summary>
    /// 创建配置同步调度器（不注册处理器）
    /// </summary>
    public static ConfigSyncDispatcher CreateDispatcher()
    {
        return new ConfigSyncDispatcher(NullLogger<ConfigSyncDispatcher>.Instance);
    }

    /// <summary>
    /// 创建同步配置管理器
    /// </summary>
    public static SyncConfigManager CreateSyncManager()
    {
        return new SyncConfigManager();
    }

    /// <summary>
    /// 创建gRPC服务器
    /// </summary>
    /// <param name="localKey">本地服务器密钥</param>
    /// <param name="syncManager">同步配置管理器</param>
    /// <param name="configProvider">配置提供函数 - 导出当前配置用于Pull请求响应</param>
    /// <returns>GrpcServerHost实例</returns>
    public static GrpcServerHost CreateServer(
        string localKey,
        SyncConfigManager syncManager,
        Func<Dictionary<string, string>> configProvider)
    {
        return new GrpcServerHost(localKey, syncManager, configProvider);
    }

    /// <summary>
    /// 初始化gRPC服务器并启动监听
    /// 在两个版本中通用的初始化逻辑
    /// </summary>
    /// <param name="grpcServerHost">gRPC服务器实例</param>
    /// <param name="dispatcher">配置派发器</param>
    /// <param name="listeningPort">监听端口</param>
    /// <param name="onServerStarted">服务器启动完成后的回调（可选，用于更新UI等）</param>
    public static async Task InitializeServerAsync(
        GrpcServerHost grpcServerHost,
        ConfigSyncDispatcher dispatcher,
        int listeningPort,
        Action? onServerStarted = null)
    {
        try
        {
            // 订阅配置更新事件 - 派发接收到的配置
            grpcServerHost.ConfigServer.OnConfigUpdated += async (updatedConfig) =>
            {
                if (dispatcher != null)
                {
                    await dispatcher.DispatchBatchAsync(updatedConfig);
                }
            };

            // 启动服务器
            await grpcServerHost.StartAsync(listeningPort);

            // 服务器启动成功，调用回调（用于UI更新等）
            onServerStarted?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"[GrpcBootstrapper] 初始化gRPC服务器失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 停止gRPC服务器
    /// </summary>
    public static async Task StopServerAsync(GrpcServerHost? grpcServerHost)
    {
        if (grpcServerHost != null)
        {
            try
            {
                await grpcServerHost.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"[GrpcBootstrapper] 停止gRPC服务器时出错: {ex.Message}");
            }
        }
    }
}
