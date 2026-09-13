namespace MDiceV2.Abstractions;

/// <summary>
/// 启动模式枚举
/// 决定应用的运行方式和加载的组件
/// </summary>
public enum StartupMode
{
    /// <summary>
    /// UI应用模式（完整功能，含Avalonia UI）
    /// </summary>
    UI,
    
    /// <summary>
    /// Console无头应用模式（仅业务逻辑，可作为后台服务）
    /// </summary>
    Console,
    
    /// <summary>
    /// 远程同步服务器模式（Console + gRPC服务器）
    /// 用于接收多个客户端的配置同步请求和消息分发
    /// </summary>
    RemoteSyncServer,
    
    /// <summary>
    /// 远程同步客户端模式（UI + gRPC客户端）
    /// 用于连接到远程服务器同步配置和接收消息
    /// </summary>
    RemoteSyncClient
}
