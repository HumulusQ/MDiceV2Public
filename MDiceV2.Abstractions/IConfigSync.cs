namespace MDiceV2.Abstractions;

/// <summary>
/// 配置同步冲突解决策略
/// </summary>
public enum SyncConflictStrategy
{
    /// <summary>
    /// 本地时间戳更新 > 远程时间戳，则保留本地版本
    /// </summary>
    LocalWins,
    
    /// <summary>
    /// 远程时间戳更新 > 本地时间戳，则覆盖为远程版本
    /// </summary>
    RemoteWins,
    
    /// <summary>
    /// 时间戳相同时保留本地版本
    /// </summary>
    LocalWinsOnTie
}

/// <summary>
/// gRPC配置同步客户端接口
/// 用于Console/UI应用连接到远程服务器同步配置
/// </summary>
public interface IConfigSyncClient
{
    /// <summary>
    /// 连接到远程同步服务器
    /// </summary>
    Task ConnectAsync(string serverAddress, int port, string password);

    /// <summary>
    /// 拉取远程配置
    /// </summary>
    Task<Dictionary<string, string>> PullConfigAsync();

    /// <summary>
    /// 推送本地配置到远程服务器
    /// </summary>
    Task PushConfigAsync(Dictionary<string, string> config);

    /// <summary>
    /// 订阅日志流（实时接收来自远程的日志）
    /// </summary>
    Task SubscribeLogsAsync(Func<string, Task> onLogReceived, CancellationToken cancellationToken);

    /// <summary>
    /// 推送模拟消息到所有连接的客户端
    /// </summary>
    Task PushSimulationMessageAsync(string userId, string content);

    /// <summary>
    /// 获取连接状态
    /// </summary>
    bool IsConnected { get; }
}

/// <summary>
/// gRPC配置同步服务器接口
/// 用于在Console应用中启用，接受远程客户端的同步请求
/// </summary>
public interface IConfigSyncServer
{
    /// <summary>
    /// 启动gRPC服务器，监听指定端口
    /// </summary>
    Task StartAsync(int port, string password);

    /// <summary>
    /// 停止gRPC服务器
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 广播日志条目到所有连接的客户端
    /// 支持批量打包发送，避免网络开销
    /// </summary>
    Task BroadcastLogsAsync(IEnumerable<string> logEntries);

    /// <summary>
    /// 广播模拟消息到所有连接的客户端
    /// </summary>
    Task BroadcastSimulationMessageAsync(string userId, string content);

    /// <summary>
    /// 获取连接的客户端数
    /// </summary>
    int ConnectedClientCount { get; }
}
