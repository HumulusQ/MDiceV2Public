namespace MDiceV2.Abstractions;

/// <summary>
/// 表示接收到的消息事件参数
/// </summary>
public class MessageReceivedEventArgs : EventArgs
{
    public required string Source { get; set; }          // 消息源标识（如群ID）
    public required string UserId { get; set; }          // 用户ID
    public required string Content { get; set; }         // 消息内容
    public required bool IsSimulationMode { get; set; }  // 是否为模拟模式
    public DateTime ReceivedTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 抽象消息通道接口
/// 支持WebSocket、模拟、远程gRPC等多种消息源
/// </summary>
public interface IMessageChannel
{
    /// <summary>
    /// 当接收到消息时触发
    /// </summary>
    event EventHandler<MessageReceivedEventArgs>? OnMessageReceived;

    /// <summary>
    /// 连接到消息源
    /// </summary>
    Task ConnectAsync(string url);

    /// <summary>
    /// 断开连接
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// 发送回复消息
    /// </summary>
    Task SendReplyAsync(string groupId, string userId, string message);

    /// <summary>
    /// 获取连接状态
    /// </summary>
    bool IsConnected { get; }
}
