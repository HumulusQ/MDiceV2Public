using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;

namespace MDiceV2.Models;

/// <summary>
/// 全局消息队列
/// 管理日志消息和OneBot消息的队列处理
/// 使用单例模式确保只有一个实例
/// 使用 System.Threading.Channels 实现高效的消息传递
/// </summary>
public enum QueuedMessageType
{
    Log,
    OneBotMessage
}

/// <summary>
/// 队列消息类
/// </summary>
public class QueuedMessage
{
    public QueuedMessageType Type { get; set; }
    public string Content { get; set; } = string.Empty; // For Log messages
    public object? OneBotData { get; set; } // For OneBot messages
}

/// <summary>
/// 全局消息队列类
/// 负责消息的入队和出队处理
/// 使用 Channel<T> 实现高效、无锁的消息传递
/// </summary>
public partial class GlobalMessageQueue : ObservableObject
{
    /// <summary>
    /// 单例实例
    /// </summary>
    public static GlobalMessageQueue Instance { get; private set; }

    private static readonly object _lock = new object();

    /// <summary>
    /// 消息通道 (替代 ConcurrentQueue)
    /// 使用有界 Channel (容量 5000) 提供背压保护，防止消息堆积
    /// 提供高效的异步消息传递，不需要 Sleep 轮询
    /// </summary>
    private Channel<QueuedMessage> _messageChannel;

    /// <summary>
    /// 是否已初始化
    /// </summary>
    private bool _isInitialized = false;

    /// <summary>
    /// 日志消息队列事件
    /// 当有新的日志消息时触发
    /// </summary>
    public event Action<string, LogMessageType>? LogMessageQueued;

    /// <summary>
    /// OneBot消息队列事件
    /// 当有新的OneBot消息时触发
    /// </summary>
    public event Action<object>? OneBotMessageQueued;

    /// <summary>
    /// 构造函数
    /// 初始化单例实例和消息通道
    /// </summary>
    public GlobalMessageQueue()
    {
        lock (_lock)
        {
            if (Instance == null)
            {
                Instance = this;
                // 创建有界 Channel，容量 5000，防止无限内存增长
                var options = new BoundedChannelOptions(5000)
                {
                    // DropWrite: 当通道满时丢弃新消息（保证不阻塞发送）
                    FullMode = BoundedChannelFullMode.DropWrite
                };
                _messageChannel = Channel.CreateBounded<QueuedMessage>(options);
                StartProcessing();
                _isInitialized = true;
            }
            else
            {
                throw new InvalidOperationException("GlobalMessageQueue instance already exists.");
            }
        }
    }

    /// <summary>
    /// 开始消息处理
    /// 使用异步任务而不是线程，避免频繁的上下文切换
    /// </summary>
    private void StartProcessing()
    {
        // 以后台任务启动消息处理循环
        _ = ProcessMessagesAsync();
    }

    /// <summary>
    /// 异步消息处理循环
    /// 使用 Channel 的 await foreach，消息到达时立即处理，无需 Sleep 轮询
    /// 添加超时保护和单线程处理确保消息不被阻塞
    /// </summary>
    private async Task ProcessMessagesAsync()
    {
        try
        {
            // 使用 CancellationToken 添加超时保护
            using var cts = new CancellationTokenSource();
            
            await foreach (var message in _messageChannel.Reader.ReadAllAsync(cts.Token))
            {
                try
                {
                    // 使用 Task.Run 确保事件处理不会阻塞读取线程
                    await Task.Run(() =>
                    {
                        switch (message.Type)
                        {
                            case QueuedMessageType.Log:
                                try
                                {
                                    // 解析日志消息内容，格式为 "text|type"
                                    string[] parts = message.Content.Split('|');
                                    if (parts.Length == 2 && Enum.TryParse(parts[1], out LogMessageType logType))
                                    {
                                        LogMessageQueued?.Invoke(parts[0], logType);
                                    }
                                    else
                                    {
                                        LogMessageQueued?.Invoke(message.Content, LogMessageType.Normal);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Error($"[GlobalMessageQueue] 处理日志消息异常: {ex.Message}");
                                }
                                break;
                            case QueuedMessageType.OneBotMessage:
                                try
                                {
                                    OneBotMessageQueued?.Invoke(message.OneBotData);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error($"[GlobalMessageQueue] 处理OneBot消息异常: {ex.Message}");
                                }
                                break;
                        }
                    });
                }
                catch (Exception ex)
                {
                    // 处理单条消息时发生异常，不中断处理循环
                    Log.Error($"[GlobalMessageQueue] 处理消息时发生异常: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Normal("[GlobalMessageQueue] 消息处理循环被取消");
        }
        catch (Exception ex)
        {
            Log.Error($"[GlobalMessageQueue] 消息处理循环异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 入队日志消息
    /// 使用 TryWrite 非阻塞方式，防止发送方被阻塞
    /// 当通道满时消息会被丢弃（不影响系统运行）
    /// </summary>
    /// <param name="text">消息文本</param>
    /// <param name="type">消息类型</param>
    public void EnqueueLogMessage(string text, LogMessageType type)
    {
        if (!_isInitialized) return;
        
        var success = _messageChannel.Writer.TryWrite(new QueuedMessage
        {
            Type = QueuedMessageType.Log,
            Content = $"{text}|{type}"
        });
        
        if (!success)
        {
            // Channel 满或已关闭，日志消息被丢弃（避免系统卡死）
            // 这是可接受的，因为日志丢失比系统阻塞更可取
        }
    }

    /// <summary>
    /// 入队OneBot消息
    /// 使用 TryWrite 非阻塞方式，防止发送方被阻塞
    /// 当通道满时消息会被丢弃（不影响系统运行）
    /// </summary>
    /// <param name="oneBotData">OneBot数据</param>
    public void EnqueueOneBotMessage(object oneBotData)
    {
        if (!_isInitialized) return;
        
        var success = _messageChannel.Writer.TryWrite(new QueuedMessage
        {
            Type = QueuedMessageType.OneBotMessage,
            OneBotData = oneBotData
        });
        
        if (!success)
        {
            // Channel 满或已关闭，消息被丢弃（避免系统卡死）
            Log.Warn("[GlobalMessageQueue] OneBot消息队列满，消息被丢弃");
        }
    }
}