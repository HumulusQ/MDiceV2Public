using System;
using System.Collections.Generic;
using MDiceV2.Interfaces;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;

namespace MDiceV2.Core.Mod;

/// <summary>
/// Mod 上下文实现
/// 实现 IModContext 接口，为 Mod 提供与宿主程序交互的能力
/// 
/// 通过此类，Mod 可以：
/// - 发送群消息和私聊消息
/// - 获取用户信息
/// - 记录日志
/// - 查询程序状态
/// - 访问数据持久化服务
/// </summary>
public class ModContextImpl : IModContext
{
    /// <summary>
    /// 消息分发器（用于发送消息和获取用户信息）
    /// </summary>
    private readonly MessageDistribution _messageDistribution;

    /// <summary>
    /// Mod ID（用于日志标识）
    /// 在每条日志前添加 "[ModId]" 前缀
    /// </summary>
    private readonly string _modId;

    /// <summary>
    /// 是否处于模拟模式
    /// 模拟模式下消息不会实际发送，仅在 UI 中显示
    /// </summary>
    public bool IsSimulationMode => _messageDistribution.SimulationSwitch;

    /// <summary>
    /// 已注册的 command reply 监听器
    /// </summary>
    private readonly List<Action<long, long, string>> _commandReplyListeners = new();

    /// <summary>
    /// 当前由本 Mod 发起的、尚未收到 reply 的命令
    /// </summary>
    private readonly HashSet<(long GroupId, long UserId)> _pendingModCommands = new();

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="messageDistribution">消息分发器实例</param>
    /// <param name="modId">Mod 的唯一标识符</param>
    public ModContextImpl(MessageDistribution messageDistribution, string modId)
    {
        _messageDistribution = messageDistribution 
            ?? throw new ArgumentNullException(nameof(messageDistribution));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));

        // 订阅 Reply 事件，拦截由本 Mod 发起的命令的 reply 输出
        _messageDistribution.OnReplySent += OnReplySentHandler;
    }

    /// <summary>
    /// Reply 事件处理器：仅转发由 ExecuteCommand 发起的命令的 reply
    /// </summary>
    private void OnReplySentHandler(string content, Msg msg)
    {
        var key = (msg.GroupId, msg.UserId);
        lock (_pendingModCommands)
        {
            if (!_pendingModCommands.Contains(key)) return;
        }

        foreach (var listener in _commandReplyListeners)
        {
            try
            {
                listener(msg.GroupId, msg.UserId, content);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"CommandReplyListener error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 发送群消息
    /// 
    /// 实现细节：
    /// 1. 在模拟模式下：通过Reply发送（显示在UI中）
    /// 2. 在正常模式下：直接通过WSconnection.SendGroupMessage发送
    /// 注意：为确保消息能够正确发送，使用直接API而非Reply函数
    /// </summary>
    public void SendGroupMessage(long groupId, string content)
    {
        try
        {
            if (IsSimulationMode)
            {
                // 模拟模式：创建消息对象并通过 Reply 发送
                var msg = new MDiceV2.Models.Msg(
                    groupId: groupId,
                    userId: 1001,  // 使用系统账号ID而非0
                    content: content,
                    source: MDiceV2.Models.MessageSource.group,
                    isSimulationMode: true
                );
                _messageDistribution.Reply(content, msg);
            }
            else
            {
                // WebSocket模式：直接发送
                if (_messageDistribution.WSconnection != null && _messageDistribution.WSconnection.IsWsConnected)
                {
                    _messageDistribution.WSconnection.SendGroupMessage(groupId, content);
                }
                else
                {
                    Log(LogLevel.Warn, $"WebSocket not connected, message to group {groupId} may not be sent");
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to send group message: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送私聊消息
    /// 
    /// 实现细节：
    /// 1. 在模拟模式下：通过Reply发送（显示在UI中）
    /// 2. 在正常模式下：直接通过WSconnection.SendPrivateMessage发送
    /// 注意：为确保消息能够正确发送，使用直接API而非Reply函数
    /// </summary>
    public void SendPrivateMessage(long userId, string content)
    {
        try
        {
            if (IsSimulationMode)
            {
                // 模拟模式：创建消息对象并通过 Reply 发送
                var msg = new MDiceV2.Models.Msg(
                    groupId: 0,
                    userId: userId,
                    content: content,
                    source: MDiceV2.Models.MessageSource.privatechat,
                    isSimulationMode: true
                );
                _messageDistribution.Reply(content, msg);
            }
            else
            {
                // WebSocket模式：直接发送
                if (_messageDistribution.WSconnection != null && _messageDistribution.WSconnection.IsWsConnected)
                {
                    _messageDistribution.WSconnection.SendPrivateMessage(userId, content);
                }
                else
                {
                    Log(LogLevel.Warn, $"WebSocket not connected, message to user {userId} may not be sent");
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to send private message: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取用户信息
    /// 
    /// 当前实现：
    /// - 返回缓存的用户信息或基础信息
    /// - 如果用户信息不可用，返回基础信息（ID + 默认昵称）
    /// 
    /// 未来扩展：
    /// - 可以调用 OneBot API 获取实时用户信息
    /// - 实现用户信息缓存
    /// </summary>
    public (long UserId, string Nickname) GetUserInfo(long userId)
    {
        try
        {
            var userInfo = _messageDistribution.GetUserInfo(userId, IsSimulationMode);
            return (userInfo.UserId, userInfo.Nickname ?? $"User_{userId}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, $"Failed to get user info for {userId}: {ex.Message}");
            return (userId, $"User_{userId}");
        }
    }

    /// <summary>
    /// 记录日志
    /// 
    /// 日志格式：[ModId] Message
    /// 例如：[com.example.customreply] Mod loaded successfully
    /// 
    /// 日志级别对应关系：
    /// - Debug: 调试信息 → 映射到 Log.Normal()
    /// - Info: 普通信息 → 映射到 Log.Normal()
    /// - Warn: 警告信息 → 映射到 Log.Warn()
    /// - Error: 错误信息 → 映射到 Log.Error()
    /// - Fatal: 严重错误 → 映射到 Log.Error()
    /// </summary>
    public void Log(LogLevel level, string message)
    {
        var formattedMessage = $"[{_modId}] {message}";

        try
        {
            switch (level)
            {
                case LogLevel.Debug:
                case LogLevel.Info:
                    MDiceV2.Models.Log.Normal(formattedMessage);
                    break;
                case LogLevel.Warn:
                    MDiceV2.Models.Log.Warn(formattedMessage);
                    break;
                case LogLevel.Error:
                case LogLevel.Fatal:
                    MDiceV2.Models.Log.Error(formattedMessage);
                    break;
                default:
                    MDiceV2.Models.Log.Normal(formattedMessage);
                    break;
            }
        }
        catch
        {
            // 如果日志系统出错，静默失败
            // 不应该因为日志失败而中断 Mod 执行
        }
    }

    /// <summary>
    /// 获取导航面板注册表服务
    /// Mod 通过此服务将其 UI 面板注册到主窗口导航栏
    /// </summary>
    /// <remarks>
    /// 返回全局 NavigationPanelRegistry 单例实例
    /// 如果主窗口未初始化，返回 null（目前总是返回实例）
    /// </remarks>
    public INavigationPanelRegistry? GetNavigationPanelRegistry()
    {
        try
        {
            return NavigationPanelRegistry.Instance;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to get navigation panel registry: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 执行程序本体的指令（绕过 Mod 处理，直接调用 command handler）
    /// </summary>
    public void ExecuteCommand(long groupId, long userId, string command)
    {
        try
        {
            var processor = _messageDistribution.MessageProcessor;
            if (processor == null)
            {
                Log(LogLevel.Error, "MessageProcessor is null, cannot execute command");
                return;
            }

            var msg = new Msg(
                groupId: groupId,
                userId: userId,
                content: command,
                source: MDiceV2.Models.MessageSource.group,
                isSimulationMode: IsSimulationMode
            );

            var key = (groupId, userId);
            lock (_pendingModCommands)
            {
                _pendingModCommands.Add(key);
            }
            try
            {
                processor.ExecuteCommand(msg);
            }
            finally
            {
                lock (_pendingModCommands)
                {
                    _pendingModCommands.Remove(key);
                }
            }
            Log(LogLevel.Debug, $"ExecuteCommand called: {command}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to execute command '{command}': {ex.Message}");
        }
    }

    /// <summary>
    /// 注册命令 reply 监听器
    /// </summary>
    public void RegisterCommandReplyListener(Action<long, long, string> listener)
    {
        if (listener == null) return;
        lock (_commandReplyListeners)
        {
            _commandReplyListeners.Add(listener);
        }
    }

    /// <summary>
    /// 获取用户的权限等级（白名单等级）
    /// </summary>
    /// <param name="userId">用户QQ号</param>
    /// <returns>
    /// - null：用户未设置权限等级（使用默认权限）
    /// - 0：用户在白名单中（完全授权）
    /// - 1-9：逐级降低的权限等级
    /// </returns>
    public int? GetUserAuthLevel(long userId)
    {
        try
        {
            var processor = _messageDistribution.MessageProcessor;
            if (processor == null)
            {
                Log(LogLevel.Warn, "MessageProcessor is null, cannot query user auth level");
                return null;
            }
            return processor.GetUserAuthLevel(userId);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to get user auth level for {userId}: {ex.Message}");
            return null;
        }
    }

    public bool IsBotEnabled(long groupId)
    {
        try
        {
            var processor = _messageDistribution.MessageProcessor;
            if (processor == null)
            {
                Log(LogLevel.Warn, "MessageProcessor is null, cannot query bot enabled state");
                return true; // 默认启用
            }
            return processor.IsBotEnabled(groupId);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to get bot enabled state for {groupId}: {ex.Message}");
            return true; // 默认启用
        }
    }
}
