using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;

namespace MDiceV2.Models;

/// <summary>
/// WebSocket连接管理器
/// 负责与OneBot服务器建立WebSocket连接并处理消息收发
/// </summary>
public partial class WSconnection : ObservableObject
{
    /// <summary>
    /// WebSocket客户端实例
    /// </summary>
    private ClientWebSocket? _wsClient;

    /// <summary>
    /// 连接状态
    /// </summary>
    [ObservableProperty]
    private bool isWsConnected;

    /// <summary>
    /// WebSocket URL
    /// </summary>
    public static string wsUrl = "ws://localhost:8080";

    /// <summary>
    /// 连接取消令牌源
    /// </summary>
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// 待处理的请求字典
    /// Key为echo字段，Value为回调函数
    /// </summary>
    private readonly ConcurrentDictionary<string, Action<JsonElement>> _pendingRequests = new();

    /// <summary>
    /// 请求队列
    /// </summary>
    private readonly ConcurrentQueue<(string message, TaskCompletionSource<JsonElement>? tcs)> _requestQueue = new();

    /// <summary>
    /// 消息队列信号（用于通知发送任务有新消息）
    /// </summary>
    private readonly System.Threading.ManualResetEvent _messageAvailable = new System.Threading.ManualResetEvent(false);

    /// <summary>
    /// Echo计数器
    /// </summary>
    private long _echoCounter = 0;

    /// <summary>
    /// 消息接收任务
    /// </summary>
    private Task? _receiveTask;

    /// <summary>
    /// 消息发送任务
    /// </summary>
    private Task? _sendTask;

    /// <summary>
    /// 构造函数
    /// </summary>
    public WSconnection()
    {
        // 移除日志消息订阅以避免循环引用
        // 日志处理应在具体的UI组件或日志管理器中完成
    }

    /// <summary>
    /// 执行网络诊断
    /// </summary>
    private async Task PerformNetworkDiagnosticsAsync(Uri uri)
    {
        try
        {
            Log.InfoFormat($"[DEBUG] 开始网络诊断 - Host: {uri.Host}, Port: {uri.Port}");
            System.Console.WriteLine($"[WSConnection] 网络诊断: 检查 {uri.Host}:{uri.Port} 可达性...");
            System.Console.Out.Flush();

            // 检查端口是否可达 (TCP连接测试)
            using var tcpClient = new System.Net.Sockets.TcpClient();
            var connectTask = tcpClient.ConnectAsync(uri.Host, uri.Port);

            // 设置5秒超时
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Log.Error($"[DEBUG] 网络诊断失败: 无法连接到 {uri.Host}:{uri.Port} (超时)");
                System.Console.WriteLine($"[WSConnection] ✗ 网络诊断失败: {uri.Host}:{uri.Port} 无响应 (超时)");
                System.Console.Out.Flush();
                tcpClient.Close();
                return;
            }

            if (tcpClient.Connected)
            {
                Log.InfoFormat($"[DEBUG] 网络诊断成功: TCP连接到 {uri.Host}:{uri.Port} 成功");
                System.Console.WriteLine($"[WSConnection] ✓ 网络诊断成功: TCP连接到 {uri.Host}:{uri.Port} 成功");
                System.Console.Out.Flush();
                tcpClient.Close();
            }
            else
            {
                Log.Error($"[DEBUG] 网络诊断失败: TCP连接失败");
                System.Console.WriteLine($"[WSConnection] ✗ 网络诊断失败: 无法建立TCP连接");
                System.Console.Out.Flush();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DEBUG] 网络诊断异常: {ex.Message} (类型: {ex.GetType().Name})");
            System.Console.WriteLine($"[WSConnection] ⚠ 网络诊断异常: {ex.Message}");
            System.Console.Out.Flush();
        }
    }

    /// <summary>
    /// 开始连接
    /// </summary>
    public async Task StartConnection()
    {
        try
        {
            // 使用更详细的日志输出到全局日志和系统日志
            Log.InfoFormat($"开始尝试连接到 WebSocket URL: {wsUrl}");
            System.Console.WriteLine($"[WSConnection] 正在连接到: {wsUrl}");
            System.Console.Out.Flush();

            // 断开现有连接
            await DisconnectAsync();

            _wsClient = new ClientWebSocket();
            _cancellationTokenSource = new CancellationTokenSource();

            var uri = new Uri(wsUrl);
            Log.InfoFormat($"[DEBUG] 实际连接的URI: {uri.AbsoluteUri}");
            Log.InfoFormat($"[DEBUG] URI组件 - Host: {uri.Host}, Port: {uri.Port}, Scheme: {uri.Scheme}");
            Log.InfoFormat($"[DEBUG] WebSocket状态 (创建后): {_wsClient.State}");
            System.Console.WriteLine($"[WSConnection] WebSocket初始状态: {_wsClient.State}");
            System.Console.Out.Flush();

            // 预连接网络诊断
            await PerformNetworkDiagnosticsAsync(uri);

            // 设置连接超时 (15秒)
            var connectTimeout = TimeSpan.FromSeconds(15);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);

            try
            {
                timeoutCts.CancelAfter(connectTimeout);
                Log.InfoFormat($"[DEBUG] 开始WebSocket握手，超时时间: {connectTimeout.TotalSeconds}秒");
                System.Console.WriteLine($"[WSConnection] 开始WebSocket握手...");
                System.Console.Out.Flush();

                var watch = System.Diagnostics.Stopwatch.StartNew();
                await _wsClient.ConnectAsync(uri, timeoutCts.Token);
                watch.Stop();

                Log.InfoFormat($"[DEBUG] WebSocket状态 (连接后): {_wsClient.State}");
                Log.InfoFormat($"[DEBUG] 连接耗时: {watch.ElapsedMilliseconds}ms");
                System.Console.WriteLine($"[WSConnection] 握手完成，状态: {_wsClient.State}，耗时: {watch.ElapsedMilliseconds}ms");
                System.Console.Out.Flush();

                if (_wsClient.State == WebSocketState.Open)
                {
                    IsWsConnected = true;
                    Log.InfoFormat("WebSocket 连接成功建立");
                    System.Console.WriteLine("[WSConnection] ✓ WebSocket连接成功！");
                    System.Console.Out.Flush();

                    // 启动消息处理任务
                    MessageDistribution.GetInstance().InitializeSelfInfo(this);
                    _receiveTask = Task.Run(() => ReceiveMessagesAsync(_cancellationTokenSource.Token));
                    _sendTask = Task.Run(() => SendQueuedMessagesAsync(_cancellationTokenSource.Token));
                    
                    System.Console.WriteLine("[WSConnection] ✓ 消息处理任务已启动");
                    System.Console.Out.Flush();
                }
                else
                {
                    Log.Error($"WebSocket连接失败，最终状态: {_wsClient.State}");
                    System.Console.WriteLine($"[WSConnection] ✗ 连接失败 - 最终状态: {_wsClient.State}");
                    System.Console.Out.Flush();
                    IsWsConnected = false;
                }
            }
            catch (OperationCanceledException)
            {
                Log.Error($"WebSocket连接超时 ({connectTimeout.TotalSeconds}秒)");
                System.Console.WriteLine($"[WSConnection] ✗ 连接超时({connectTimeout.TotalSeconds}秒)");
                System.Console.Out.Flush();
                IsWsConnected = false;
            }
        }
        catch (UriFormatException ex)
        {
            Log.Error($"WebSocket URL格式错误: {ex.Message}");
            System.Console.WriteLine($"[WSConnection] ✗ URL格式错误: {ex.Message}");
            System.Console.Out.Flush();
            IsWsConnected = false;
        }
        catch (WebSocketException ex)
        {
            Log.Error($"WebSocket连接异常: {ex.Message} (错误码: {ex.WebSocketErrorCode})");
            System.Console.WriteLine($"[WSConnection] ✗ WebSocket异常: {ex.Message} (错误码: {ex.WebSocketErrorCode})");
            System.Console.Out.Flush();
            IsWsConnected = false;
        }
        catch (Exception ex)
        {
            Log.Error($"WebSocket连接失败: {ex.Message} (类型: {ex.GetType().Name})");
            System.Console.WriteLine($"[WSConnection] ✗ 连接失败: {ex.Message} (类型: {ex.GetType().Name})");
            System.Console.Out.Flush();
            IsWsConnected = false;
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync()
    {
        try
        {
            _cancellationTokenSource?.Cancel();

            if (_wsClient != null)
            {
                if (_wsClient.State == WebSocketState.Open)
                {
                    await _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
                }
                _wsClient.Dispose();
                _wsClient = null;
            }

            IsWsConnected = false;
            Log.InfoFormat("WebSocket 连接已断开");
        }
        catch (Exception ex)
        {
            Log.Error($"断开连接时发生错误: {ex.Message}");
        }
    }

    /// <summary>
    /// 接收消息的异步方法
    /// </summary>
    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        // Log.InfoFormat($"[DEBUG] 开始接收消息循环，WebSocket状态: {_wsClient?.State}");

        while (!cancellationToken.IsCancellationRequested && _wsClient != null &&
               _wsClient.State == WebSocketState.Open)
        {
            try
            {
                // Log.InfoFormat($"[DEBUG] 等待接收消息...");  // 移除高频日志
                var result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                // Log.InfoFormat($"[DEBUG] 收到数据，消息类型: {result.MessageType}, 大小: {result.Count}");  // 移除高频日志

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // A OneBot JSON response may be split across multiple WebSocket
                    // frames; wait for the entire message before parsing it.
                    using var content = new MemoryStream();
                    content.Write(buffer, 0, result.Count);
                    while (!result.EndOfMessage)
                    {
                        result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                        if (result.MessageType != WebSocketMessageType.Text)
                            break;
                        content.Write(buffer, 0, result.Count);
                    }

                    if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text)
                    {
                        Log.Warn("接收 WebSocket 文本分片时收到了非文本或未完成帧。");
                        break;
                    }

                    var message = Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length));

                    // 处理接收到的消息
                    await HandleReceivedMessageAsync(message);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log.Warn($"收到关闭消息，关闭状态: {result.CloseStatus}，描述: {result.CloseStatusDescription}");
                    break;
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Log.InfoFormat($"收到二进制消息，大小: {result.Count}");
                }
            }
            catch (WebSocketException ex)
            {
                Log.Error($"WebSocket接收异常: {ex.Message} (错误码: {ex.WebSocketErrorCode})");
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"接收消息时发生错误: {ex.Message} (类型: {ex.GetType().Name})");
                break;
            }
        }

        // Log.InfoFormat($"[DEBUG] 接收消息循环结束，WebSocket状态: {_wsClient?.State}");
        IsWsConnected = false;
    }

    /// <summary>
    /// 处理接收到的消息
    /// </summary>
    private async Task HandleReceivedMessageAsync(string message)
    {
        try
        {
            var json = JsonDocument.Parse(message).RootElement;

            // 检查是否是请求的响应
            if (json.TryGetProperty("echo", out var echoProperty))
            {
                var echo = echoProperty.GetString();
                if (!string.IsNullOrEmpty(echo))
                {
                    Log.InfoFormat($"Received response with echo: {echo}");

                    if (_pendingRequests.TryRemove(echo, out var callback))
                    {
                        callback(json);
                    }
                }
            }
            else
            {
                // 将消息推送到全局消息队列
                if (GlobalMessageQueue.Instance != null)
                {
                    GlobalMessageQueue.Instance.EnqueueOneBotMessage(json);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"处理接收到的消息时发生错误: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送排队消息的异步方法
    /// 使用信号而不是轮询，避免忙等待导致的 CPU 高占用
    /// </summary>
    private async Task SendQueuedMessagesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 等待消息到达或超时（10秒），避免完全阻塞
                bool signaled = _messageAvailable.WaitOne(10000);
                
                // 处理队列中的所有消息
                bool hasMessages = false;
                while (_requestQueue.TryDequeue(out var item) && !cancellationToken.IsCancellationRequested)
                {
                    hasMessages = true;
                    if (_wsClient != null && _wsClient.State == WebSocketState.Open)
                    {
                        try
                        {
                            var messageBytes = Encoding.UTF8.GetBytes(item.message);
                            await _wsClient.SendAsync(
                                new ArraySegment<byte>(messageBytes),
                                WebSocketMessageType.Text,
                                true,
                                cancellationToken);

                            Log.InfoFormat($"发送消息成功: {item.message}");
                        }
                        catch (Exception sendEx)
                        {
                            Log.Error($"发送单条消息时发生错误: {sendEx.Message}");
                        }
                    }
                    else
                    {
                        Log.Error("WebSocket未连接，无法发送排队消息");
                        break;
                    }
                }
                
                // 如果没有消息被处理，重置信号以重新等待
                if (!hasMessages && signaled)
                {
                    _messageAvailable.Reset();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"发送排队消息时发生错误: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    public void SendMessage(string message)
    {
        if (_wsClient != null && _wsClient.State == WebSocketState.Open)
        {
            _requestQueue.Enqueue((message, null));
            _messageAvailable.Set(); // 通知发送任务有新消息
        }
        else
        {
            Log.Error("WebSocket未连接，无法发送消息");
        }
    }

    /// <summary>
    /// 发送请求并等待响应
    /// </summary>
    public async Task<JsonElement?> SendRequestAndAwaitResponseAsync(Dictionary<string, object> requestJson, int timeoutMs = 15000)
    {
        if (_wsClient == null || _wsClient.State != WebSocketState.Open)
        {
            Log.Error("WebSocket未连接");
            return null;
        }

        var echo = $"echo_{_echoCounter++}_{DateTime.Now.Ticks}";
        requestJson["echo"] = echo;

        var tcs = new TaskCompletionSource<JsonElement>();
        _pendingRequests[echo] = response => tcs.SetResult(response);

        var message = JsonSerializer.Serialize(requestJson);
        _requestQueue.Enqueue((message, tcs));
        _messageAvailable.Set(); // 通知发送任务有新消息

        // 设置超时
        using var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            return await tcs.Task;
        }
        catch (TaskCanceledException)
        {
            _pendingRequests.TryRemove(echo, out _);
            Log.Error($"请求超时: echo={echo}");
            return null;
        }
    }

    /// <summary>
    /// 发送群消息
    /// </summary>
    public void SendGroupMessage(long groupId, string message)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "send_group_msg",
            ["params"] = new Dictionary<string, object>
            {
                ["group_id"] = groupId,
                ["message"] = message
            }
        };
        SendMessage(JsonSerializer.Serialize(request));
    }

    /// <summary>
    /// 发送私聊消息
    /// </summary>
    public void SendPrivateMessage(long userId, string message)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "send_private_msg",
            ["params"] = new Dictionary<string, object>
            {
                ["user_id"] = userId,
                ["message"] = message
            }
        };
        SendMessage(JsonSerializer.Serialize(request));
    }

    /// <summary>
    /// 获取群成员信息
    /// </summary>
    public async Task<JsonElement?> GetGroupMemberInfoAsync(long groupId, long userId)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "get_group_member_info",
            ["params"] = new Dictionary<string, object>
            {
                ["group_id"] = groupId,
                ["user_id"] = userId
            }
        };

        return await SendRequestAndAwaitResponseAsync(request);
    }

    /// <summary>
    /// 获取登录信息（机器人自身信息）
    /// </summary>
    public async Task<JsonElement?> GetLoginInfoAsync()
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "get_login_info",
            ["params"] = new Dictionary<string, object>()
        };

        return await SendRequestAndAwaitResponseAsync(request);
    }

    /// <summary>
    /// 获取陌生人信息
    /// </summary>
    public async Task<JsonElement?> GetStrangerInfoAsync(long userId)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "get_stranger_info",
            ["params"] = new Dictionary<string, object>
            {
                ["user_id"] = userId
            }
        };

        return await SendRequestAndAwaitResponseAsync(request);
    }

    /// <summary>
    /// 同意加群申请
    /// </summary>
    public void ApproveGroupRequest(string flag)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "set_group_add_request",
            ["params"] = new Dictionary<string, object>
            {
                ["flag"] = flag,
                ["approve"] = true
            }
        };
        SendMessage(JsonSerializer.Serialize(request));
    }

    /// <summary>
    /// 同意加好友申请
    /// </summary>
    public void ApproveFriendRequest(string flag)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "set_friend_add_request",
            ["params"] = new Dictionary<string, object>
            {
                ["flag"] = flag,
                ["approve"] = true
            }
        };
        SendMessage(JsonSerializer.Serialize(request));
    }

    /// <summary>
    /// 退出群聊
    /// </summary>
    public void LeaveGroup(long groupId)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "set_group_leave",
            ["params"] = new Dictionary<string, object>
            {
                ["group_id"] = groupId
            }
        };
        SendMessage(JsonSerializer.Serialize(request));
    }

    /// <summary>
    /// 设置群成员名片
    /// </summary>
    public void SetGroupCard(long groupId, long userId, string card)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "set_group_card",
            ["params"] = new Dictionary<string, object>
            {
                ["group_id"] = groupId,
                ["user_id"] = userId,
                ["card"] = card
            }
        };
        SendMessage(JsonSerializer.Serialize(request));
    }

    /// <summary>
    /// 上传群文件
    /// </summary>
    public void UploadGroupFile(long groupId, string filePath, string name = "")
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "upload_group_file",
            ["params"] = new Dictionary<string, object>
            {
                ["group_id"] = groupId,
                ["file"] = filePath,
                ["name"] = name
            }
        };
        SendMessage(JsonSerializer.Serialize(request));
    }

    /// <summary>
    /// 发送群合并转发消息
    /// </summary>
    public void SendGroupForwardMessage(long groupId, List<Dictionary<string, object>> messages)
    {
        var request = new Dictionary<string, object>
        {
            ["action"] = "send_group_forward_msg",
            ["params"] = new Dictionary<string, object>
            {
                ["group_id"] = groupId,
                ["messages"] = messages
            }
        };
        SendMessage(JsonSerializer.Serialize(request));
    }
}
