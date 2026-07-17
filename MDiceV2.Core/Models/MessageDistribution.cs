using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;
using MDiceV2.Core.GameBattle;
using MDiceV2.Abstractions;

namespace MDiceV2.Models;

/// <summary>
/// 消息分发器
/// 负责处理OneBot消息的分发和回复
/// </summary>
public partial class MessageDistribution : ObservableObject
{
    /// <summary>
    /// 单例实例
    /// </summary>
    public static MessageDistribution? Instance { get; private set; }

    private static readonly object _instanceLock = new object();
    private static bool _subscribed = false;

    /// <summary>
    /// WebSocket连接实例
    /// </summary>
    public WSconnection WSconnection { get; set; } = new();

    /// <summary>
    /// UI线程分发器 - 用于跨线程UI更新
    /// </summary>
    private readonly MDiceV2.Abstractions.IDispatcher? _dispatcher;

    /// <summary>
    /// 模拟模式开关
    /// </summary>
    [ObservableProperty]
    private bool simulationSwitch = false;

    /// <summary>
    /// 用户焦点状态字典 string:userId -> string:focusType
    /// 用于管理用户当前处于哪种特殊处理状态
    /// </summary>
    public Dictionary<string, string> UserFocusStates { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// 群消息事件委托
    /// </summary>
    public Action<long, long, string, bool, bool>? OnGroupMessage;

    /// <summary>
    /// 私聊消息事件委托
    /// </summary>
    public Action<long, string>? OnPrivateMessage;

    /// <summary>
    /// 群请求事件委托
    /// </summary>
    public Action<long, long, string, string>? OnGroupRequest;

    /// <summary>
    /// 好友请求事件委托
    /// </summary>
    public Action<long, string, string>? OnFriendRequest;

    /// <summary>
    /// 群文件上传事件委托
    /// </summary>
    public Action<long, long, string>? OnGroupUpload;

    /// <summary>
    /// OneBot 文件消息事件委托
    /// </summary>
    public Action<OneBotFileInfo>? OnFileMessage;

    /// <summary>
    /// 专用于群文件人物卡导入确认的焦点消息。处理器负责决定何时清除焦点。
    /// </summary>
    public Action<Msg, string>? OnCharacterCardImportConfirmation;

    /// <summary>
    /// Database-import confirmation for the focused master user.
    /// </summary>
    public Action<Msg, string>? OnDatabaseImportConfirmation;

    /// <summary>
    /// 群管理员变动事件委托
    /// </summary>
    public Action<long, long, bool>? OnGroupAdmin;

    /// <summary>
    /// 群成员减少事件委托
    /// </summary>
    public Action<long, long, string>? OnGroupDecrease;

    /// <summary>
    /// 群成员增加事件委托
    /// </summary>
    public Action<long, long, string>? OnGroupIncrease;

    /// <summary>
    /// 群禁言事件委托
    /// </summary>
    public Action<long, long, int>? OnGroupBan;

    /// <summary>
    /// 加好友成功事件委托
    /// </summary>
    public Action<long>? OnFriendAdd;

    /// <summary>
    /// 群消息撤回事件委托
    /// </summary>
    public Action<long, long, long>? OnGroupRecall;

    /// <summary>
    /// 好友消息撤回事件委托
    /// </summary>
    public Action<long, long>? OnFriendRecall;

    /// <summary>
    /// 通知事件委托
    /// </summary>
    public Action<string, long, string, JsonElement>? OnNotify;

    /// <summary>
    /// 群名片变更事件委托
    /// </summary>
    public Action<long, long, string>? OnGroupCard;

    /// <summary>
    /// 消息回复已发送事件委托（内容, 原始消息上下文）
    /// 供 Mod 拦截 command handler 的 reply 输出
    /// </summary>
    public Action<string, Msg>? OnReplySent;

    /// <summary>
    /// 离线文件事件委托
    /// </summary>
    public Action<long, string>? OnOfflineFile;

    /// <summary>
    /// 客户端状态事件委托
    /// </summary>
    public Action<long, string>? OnClientStatus;

    /// <summary>
    /// 精华消息事件委托
    /// </summary>
    public Action<long, long, string>? OnEssence;

    /// <summary>
    /// 缓存的机器人自身信息
    /// </summary>
    private long _cachedSelfUserId = 0;
    private string _cachedSelfNickname = string.Empty;

    /// <summary>
    /// 用户信息缓存字典
    /// Key: userId, Value: UserInfo
    /// </summary>
    private Dictionary<long, UserInfo> _userInfoCache = new();

    /// <summary>
    /// 群名片缓存字典
    /// Key: (groupId, userId), Value: 群名片名称
    /// </summary>
    private Dictionary<(long groupId, long userId), string> _groupCardCache = new();

    /// <summary>
    /// 构造函数
    /// 使用单例模式确保只有一个实例
    /// </summary>
    public MessageDistribution(MDiceV2.Abstractions.IDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
        lock (_instanceLock)
        {
            if (Instance == null)
            {
                Instance = this;
                // 订阅全局消息队列的OneBot消息事件（仅订阅一次）
                if (GlobalMessageQueue.Instance != null && !_subscribed)
                {
                    GlobalMessageQueue.Instance.OneBotMessageQueued += HandleQueuedOneBotMessage;
                    _subscribed = true;
                    Log.InfoFormat("[MessageDistribution] 已订阅全局消息队列事件");
                }

                // 初始化事件处理
                OnGroupMessage += (groupId, userId, message, isAted, shouldIgnore) =>
                    HandleMessage(groupId, userId, message, MessageSource.group, SimulationSwitch, isAted, shouldIgnore);

                OnPrivateMessage += (userId, message) =>
                    HandleMessage(0, userId, message, MessageSource.privatechat, SimulationSwitch);

                Log.InfoFormat("[MessageDistribution] 单例实例已初始化");
            }
            else
            {
                Log.Warn("[MessageDistribution] 尝试创建多个MessageDistribution实例，仅返回现有实例");
            }
        }
    }

    /// <summary>
    /// 获取单例实例
    /// </summary>
    public static MessageDistribution GetInstance()
    {
        if (Instance == null)
        {
            lock (_instanceLock)
            {
                if (Instance == null)
                {
                    Log.InfoFormat("[MessageDistribution] Creating new MessageDistribution singleton instance...");
                    Instance = new MessageDistribution();
                    Log.InfoFormat("[MessageDistribution] New MessageDistribution singleton instance created (hashcode: {0})", Instance.GetHashCode());
                }
            }
        }
        else
        {
            Log.InfoFormat("[MessageDistribution] Returning existing MessageDistribution singleton instance (hashcode: {0})", Instance.GetHashCode());
        }
        return Instance;
    }

    /// <summary>
    /// 处理消息
    /// </summary>
    private void HandleMessage(long groupId, long userId, string message, MessageSource messageSource,
                              bool isSimulationMode, bool isAted = false, bool shouldIgnore = false)
    {
        Log.Error($"[HandleMessage] 原始消息: '{message}' IsAted:{isAted} ShouldIgnore:{shouldIgnore}");
        
        try
        {
            Msg? msg = null;
            try
            {
                msg = new Msg(groupId, userId, message, messageSource, isSimulationMode, isAted, shouldIgnore);
            }
            catch (Exception ex)
            {
                Log.Error($"[HandleMessage] 创建消息对象时出错 (User:{userId}, Group:{groupId}): {ex.Message}");
                Log.Error($"[HandleMessage] 堆栈跟踪: {ex.StackTrace}");
                return; // 无法创建消息对象，无法继续处理
            }

            // 检查用户是否处于焦点状态
            string userIdStr = userId.ToString();
            if (UserFocusStates.TryGetValue(userIdStr, out string? focusType))
            {
                Log.Warn("User is in focus state");
                // 根据焦点类型进行特殊处理
                try
                {
                    HandleFocusedMessage(userIdStr, focusType, msg, message, isSimulationMode);
                }
                catch (Exception ex)
                {
                    Log.Error($"[HandleMessage] 处理焦点消息时出错 (User:{userId}, FocusType:{focusType}): {ex.Message}");
                    Log.Error($"[HandleMessage] 堆栈跟踪: {ex.StackTrace}");
                    // 尝试清除焦点状态，以便用户可以继续交互
                    try
                    {
                        ClearUserFocus(userIdStr);
                    }
                    catch (Exception clearEx)
                    {
                        Log.Error($"[HandleMessage] 清除焦点状态时出错: {clearEx.Message}");
                    }
                }
            }
            else
            {
                // 正常消息处理流程
                if (MessageProcessor != null)
                {
                    try
                    {
                        MessageProcessor.OnHandleMessage(msg);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[HandleMessage] MessageProcessor处理消息时出错 (User:{userId}, Group:{groupId}): {ex.Message}");
                        Log.Error($"[HandleMessage] 堆栈跟踪: {ex.StackTrace}");
                        // 消息处理失败，但不中断消息接收
                    }
                }
                else
                {
                    Log.Warn("MessageProcessor实例为空，无法处理消息");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[HandleMessage] 处理消息时发生致命错误 (User:{userId}, Group:{groupId}): {ex.Message}");
            Log.Error($"[HandleMessage] 堆栈跟踪: {ex.StackTrace}");
            // 外层catch块确保程序不会因任何未预期的错误而停止
        }
    }

    /// <summary>
    /// 处理队列中的OneBot消息
    /// 使用 Task.Run 异步分发，避免阻塞消息队列处理
    /// </summary>
    private void HandleQueuedOneBotMessage(object jsonObj)
    {
        // 将消息分发转移到线程池线程，避免阻塞全局消息队列处理
        _ = Task.Run(() =>
        {
            try
            {
                if (jsonObj is not JsonElement json)
                    return;

                if (!json.TryGetProperty("post_type", out var postTypeProperty))
                    return;

                var postType = postTypeProperty.GetString();
                switch (postType)
                {
                    case "message":
                        HandleMessageEvent(json);
                        break;
                    case "notice":
                        HandleNoticeEvent(json);
                        break;
                    case "request":
                        HandleRequestEvent(json);
                        break;
                    default:
                        Log.InfoFormat($"未处理的post_type: {postType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageDistribution] 处理OneBot消息时发生异常: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 处理消息事件
    /// </summary>
    private void HandleMessageEvent(JsonElement json)
    {
        var messageType = json.TryGetProperty("message_type", out var messageTypeElement)
            ? messageTypeElement.GetString()
            : string.Empty;

        if (messageType == "group")
        {
            var groupId = json.TryGetProperty("group_id", out var gid) ? gid.GetInt64() : 0;
            var userId = json.TryGetProperty("user_id", out var uid) ? uid.GetInt64() : 0;
            var messageElement = json.TryGetProperty("message", out var msgElement) ? msgElement : default;
            DispatchFileMessagesFromMessageElement(messageElement, "group_message", userId, groupId);
            var message = ParseMessageContent(messageElement);
            var selfInfo = GetSelfInfo();
            var (cleanedMessage, isAted, shouldIgnore) = CleanAndCheckLeadingMentions(message, selfInfo.UserId.ToString(), selfInfo.Nickname);

            Log.Error($"[群消息] 群:{groupId} 用户:{userId} 原始:{message} 清理后:{cleanedMessage} IsAted:{isAted} ShouldIgnore:{shouldIgnore}");
            OnGroupMessage?.Invoke(groupId, userId, cleanedMessage, isAted, shouldIgnore);
        }
        else if (messageType == "private")
        {
            var userId = json.TryGetProperty("user_id", out var uid) ? uid.GetInt64() : 0;
            var messageElement = json.TryGetProperty("message", out var msgElement) ? msgElement : default;
            DispatchFileMessagesFromMessageElement(messageElement, "private_message", userId, 0);
            var message = ParseMessageContent(messageElement);

            Log.InfoFormat($"[好友消息] 用户:{userId} 内容:{message}");

            OnPrivateMessage?.Invoke(userId, message);
        }
    }

    /// <summary>
    /// 处理通知事件
    /// </summary>
    private void HandleNoticeEvent(JsonElement json)
    {
        var noticeType = json.GetProperty("notice_type").GetString();

        switch (noticeType)
        {
            case "group_upload":
                var groupIdGu = json.TryGetProperty("group_id", out var gid) ? gid.GetInt64() : 0;
                var userIdGu = json.TryGetProperty("user_id", out var uid) ? uid.GetInt64() : 0;
                var fileGu = "";
                if (json.TryGetProperty("file", out var file))
                {
                    // file 可能是字符串或对象，需要安全处理
                    try
                    {
                        if (file.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            fileGu = file.GetString() ?? "";
                        }
                        else if (file.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            // 如果是对象，尝试获取其中的相关字段
                            if (file.TryGetProperty("file_name", out var fileName))
                            {
                                fileGu = fileName.GetString() ?? "";
                            }
                            else if (file.TryGetProperty("name", out var name))
                            {
                                fileGu = name.GetString() ?? "";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[群文件上传] 解析file字段失败: {ex.Message}");
                    }
                }
                Log.InfoFormat($"[群文件上传] 群:{groupIdGu} 用户:{userIdGu} 文件:{fileGu}");
                OnGroupUpload?.Invoke(groupIdGu, userIdGu, fileGu);
                if (json.TryGetProperty("file", out var uploadFileElement))
                {
                    DispatchSingleFileMessage(uploadFileElement, "group_upload", userIdGu, groupIdGu);
                }
                break;

            case "group_admin":
                var groupIdGa = json.TryGetProperty("group_id", out gid) ? gid.GetInt64() : 0;
                var userIdGa = json.TryGetProperty("user_id", out uid) ? uid.GetInt64() : 0;
                var setGa = json.TryGetProperty("sub_type", out var subType) && subType.GetString() == "set";
                Log.InfoFormat($"[群管理员变动] 群:{groupIdGa} 用户:{userIdGa} 设置:{setGa}");
                OnGroupAdmin?.Invoke(groupIdGa, userIdGa, setGa);
                break;

            // 其他通知事件处理...
            default:
                Log.InfoFormat($"未处理的notice_type: {noticeType}");
                break;
        }
    }

    private void DispatchFileMessagesFromMessageElement(JsonElement messageElement, string sourceKind, long userId, long groupId)
    {
        try
        {
            foreach (var fileInfo in OneBotFileInfo.ExtractFromMessageSegments(messageElement, sourceKind, userId, groupId))
            {
                DispatchOneBotFileInfo(fileInfo);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[OneBot文件] 提取 message 文件段失败: {ex.Message}");
        }
    }

    private void DispatchSingleFileMessage(JsonElement fileElement, string sourceKind, long userId, long groupId)
    {
        try
        {
            var fileInfo = OneBotFileInfo.FromJsonElement(sourceKind, userId, groupId, fileElement);
            DispatchOneBotFileInfo(fileInfo);
        }
        catch (Exception ex)
        {
            Log.Warn($"[OneBot文件] 提取文件对象失败: {ex.Message}");
        }
    }

    private void DispatchOneBotFileInfo(OneBotFileInfo fileInfo)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileInfo.FileId) &&
                string.IsNullOrWhiteSpace(fileInfo.FileName) &&
                string.IsNullOrWhiteSpace(fileInfo.Path) &&
                string.IsNullOrWhiteSpace(fileInfo.Url))
            {
                return;
            }

            Log.Normal($"[OneBot文件] source={fileInfo.SourceKind}, user={fileInfo.UserId}, group={fileInfo.GroupId}, file={fileInfo.FileName}, size={fileInfo.FileSize}");
            OnFileMessage?.Invoke(fileInfo);
        }
        catch (Exception ex)
        {
            Log.Warn($"[OneBot文件] 分发文件事件失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理请求事件
    /// </summary>
    private void HandleRequestEvent(JsonElement json)
    {
        var requestType = json.GetProperty("request_type").GetString();

        if (requestType == "group")
        {
            var groupId = json.TryGetProperty("group_id", out var gid) ? gid.GetInt64() : 0;
            var userId = json.TryGetProperty("user_id", out var uid) ? uid.GetInt64() : 0;
            var comment = json.TryGetProperty("comment", out var comm) ? comm.GetString() : "";
            var flag = json.TryGetProperty("flag", out var flg) ? flg.GetString() : null;
            var subType = json.TryGetProperty("sub_type", out var st) ? st.GetString() ?? "add" : "add";

            Log.InfoFormat($"[加群请求] 群:{groupId} 用户:{userId} 类型:{subType} 附言:{comment}");

            // 自动同意群邀请
            AutoApproveGroupRequest(groupId, userId, flag ?? "", subType, comment ?? "");

            OnGroupRequest?.Invoke(groupId, userId, comment ?? "", flag ?? "");
        }
        else if (requestType == "friend")
        {
            var userId = json.TryGetProperty("user_id", out var uid) ? uid.GetInt64() : 0;
            var comment = json.TryGetProperty("comment", out var comm) ? comm.GetString() : "";
            var flag = json.TryGetProperty("flag", out var flg) ? flg.GetString() : null;

            Log.InfoFormat($"[加好友申请] 用户:{userId} 附言:{comment}");

            // 自动同意好友申请
            AutoApproveFriendRequest(userId, flag ?? "", comment ?? "");

            OnFriendRequest?.Invoke(userId, comment ?? "", flag ?? "");
        }
    }

    private BasicConfig? GetBasicConfigSnapshot()
    {
        var processor = MessageProcessor ?? MessageProcessor.Instance;
        return processor?.GetBasicConfig();
    }

    /// <summary>
    /// 自动处理群邀请
    /// 仅对邀请请求自动同意（sub_type="invite"）
    /// 忽略用户申请（sub_type="add"）
    /// </summary>
    private void AutoApproveGroupRequest(long groupId, long userId, string flag, string subType, string comment)
    {
        var config = GetBasicConfigSnapshot();
        if (config == null)
        {
            Log.Warn("[加群邀请] MessageProcessor未初始化，无法读取基础配置");
            return;
        }

        if (!config.ApproveGroupJoinRequest)
        {
            Log.InfoFormat("[加群邀请] 未启用自动同意群邀请，忽略群{0} 用户{1}", groupId, userId);
            return;
        }

        // 只处理邀请请求，其他类型（如申请）直接忽略
        if (subType != "invite")
        {
            Log.Warn($"[加群邀请] 忽略非邀请类型的群请求：群{groupId} 类型{subType} 用户{userId}");
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var request = new Dictionary<string, object>
                {
                    ["action"] = "set_group_add_request",
                    ["params"] = new Dictionary<string, object>
                    {
                        ["flag"] = flag,
                        ["sub_type"] = "invite",
                        ["approve"] = true,
                        ["reason"] = ""
                    }
                };

                var response = await WSconnection.SendRequestAndAwaitResponseAsync(request);
                if (response != null && response.Value.TryGetProperty("status", out var status) && status.GetString() == "ok")
                {
                    Log.Normal($"[自动同意] 已同意来自用户 {userId} 邀请机器人加入群 {groupId}");
                    
                    // 如果启用了SendGroupJoinReport，发送记录到Master/MasterGroup
                    SendGroupJoinReportIfEnabled(groupId, userId, comment);
                }
                else
                {
                    Log.Warn($"[自动同意] 同意邀请失败: 群{groupId} 邀请人{userId}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[自动同意] 处理邀请时出错: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 自动处理好友申请
    /// 只同意机器人自身QQ号的好友申请
    /// 其他所有申请一律不处理
    /// </summary>
    private void AutoApproveFriendRequest(long userId, string flag, string comment)
    {
        try
        {
            var config = GetBasicConfigSnapshot();
            if (config == null)
            {
                Log.Warn("[好友申请] MessageProcessor未初始化，无法读取基础配置");
                return;
            }

            if (!config.ApproveFriendJoinRequest)
            {
                Log.InfoFormat("[好友申请] 未启用自动同意好友申请，忽略用户{0}", userId);
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    var request = new Dictionary<string, object>
                    {
                        ["action"] = "set_friend_add_request",
                        ["params"] = new Dictionary<string, object>
                        {
                            ["flag"] = flag,
                            ["approve"] = true,
                            ["remark"] = ""
                        }
                    };

                    var response = await WSconnection.SendRequestAndAwaitResponseAsync(request);
                    if (response != null && response.Value.TryGetProperty("status", out var status) && status.GetString() == "ok")
                    {
                        Log.Normal($"[自动同意] 已同意来自用户 {userId} 的好友申请");
                        
                        // 如果启用了SendFriendJoinReport，发送记录到Master/MasterGroup
                        SendFriendJoinReportIfEnabled(userId, comment);
                    }
                    else
                    {
                        Log.Warn($"[自动同意] 同意好友申请失败: 用户{userId}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[自动同意] 处理好友申请时出错: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[自动同意] 启动好友申请处理任务失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析消息内容
    /// 关键修复：保留 at 类型的段，将其转换为 CQ:at 码格式
    /// </summary>
    private string ParseMessageContent(JsonElement messageElement)
    {
        if (messageElement.ValueKind == JsonValueKind.String)
        {
            return messageElement.GetString() ?? "";
        }
        else if (messageElement.ValueKind == JsonValueKind.Array)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var segment in messageElement.EnumerateArray())
            {
                if (segment.TryGetProperty("type", out var type))
                {
                    var typeStr = type.GetString();
                    
                    if (typeStr == "text" &&
                        segment.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("text", out var text))
                    {
                        sb.Append(text.GetString());
                    }
                    else if (typeStr == "at" &&
                             segment.TryGetProperty("data", out var atData) &&
                             atData.TryGetProperty("qq", out var qq))
                    {
                        // 【关键修复】保留 at 段，转换为 CQ 码格式
                        var qqValue = qq.GetString() ?? "";
                        sb.Append($"[CQ:at,qq={qqValue}]");
                    }
                }
            }
            return sb.ToString();
        }
        return messageElement.ToString() ?? "";
    }

    /// <summary>
    /// 清理并检查消息头部的@段
    /// 功能1：删除头部连续的@（CQ码和纯文本格式），返回清理后的内容
    /// 功能2：检查是否被@、是否应该忽略
    /// 
    /// shouldIgnore 逻辑：头部存在@，且所有@都不包括机器自身 → shouldIgnore=true
    /// 这意味着此消息是针对其他账号的，机器人不必响应
    /// </summary>
    private (string cleaned, bool isAtBot, bool shouldIgnore) CleanAndCheckLeadingMentions(string raw, string botId, string botNickname = "")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, false, false);
        }

        int index = 0;
        bool isAtBot = false;
        bool hasAnyAt = false;

        while (index < raw.Length)
        {
            // 跳过前导空白
            while (index < raw.Length && char.IsWhiteSpace(raw[index]))
            {
                index++;
            }

            if (index >= raw.Length)
            {
                break;
            }

            // 遇到 . 字符（命令开始），停止扫描，保护命令参数
            if (raw[index] == '.')
            {
                break;
            }

            // 处理 [CQ:at,qq=xxx] 格式
            if (raw[index] == '[' && raw.IndexOf("[CQ:at", index, StringComparison.OrdinalIgnoreCase) == index)
            {
                int end = raw.IndexOf(']', index);
                if (end == -1)
                {
                    break;
                }

                var segment = raw.Substring(index, end - index + 1);
                var qq = ExtractQqFromCq(segment);
                if (!string.IsNullOrEmpty(qq))
                {
                    hasAnyAt = true;
                    Log.Error($"Found CQ at mention to QQ:{qq}****{botId}");
                    if (qq == botId)
                    {
                        isAtBot = true;
                    }
                }

                index = end + 1;
                continue;
            }

            // 处理文本形式的@（如 @机器人名称）
            if (raw[index] == '@' && !string.IsNullOrEmpty(botNickname))
            {
                int nextIndex = index + 1;
                // 检查@后面是否跟着机器人的昵称
                if (nextIndex + botNickname.Length <= raw.Length)
                {
                    string potentialNickname = raw.Substring(nextIndex, botNickname.Length);
                    if (potentialNickname == botNickname)
                    {
                        // 检查昵称后面是否为空白或消息边界
                        int afterNickname = nextIndex + botNickname.Length;
                        if (afterNickname >= raw.Length || char.IsWhiteSpace(raw[afterNickname]))
                        {
                            hasAnyAt = true;
                            isAtBot = true;
                            Log.Error($"Found text at mention to bot nickname: {botNickname}");
                            index = afterNickname;
                            continue;
                        }
                    }
                }
                // 如果不匹配机器人昵称，则记录有@但不是机器人
                hasAnyAt = true;
                Log.Error($"Found text at mention but not to bot");
                index++;
                continue;
            }

            // 遇到其他字符则停止扫描
            break;
        }

        // 返回清理后的内容
        string cleaned = raw.Substring(index).TrimStart();
        // shouldIgnore: 头部有@，且所有@都不是机器人
        bool shouldIgnore = hasAnyAt && !isAtBot;
        return (cleaned, isAtBot, shouldIgnore);
    }

    private string? ExtractQqFromCq(string cqSegment)
    {
        var match = Regex.Match(cqSegment, @"\[CQ:at,[^\]]*?qq=(\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return null;
    }


    /// <summary>
    /// 回复消息
    /// </summary>
    public void Reply(string content, Msg msg)
    {
        var perfMonitor = new PerformanceMonitor($"Reply_{msg.UserId}");
        perfMonitor.MarkStage(11, "Reply_Start");

        string combinedContent = content;
        if (!string.IsNullOrWhiteSpace(msg.ReplyPrefix))
        {
            string prefix = msg.ReplyPrefix;
            if (!prefix.EndsWith("\n", StringComparison.Ordinal) && !prefix.EndsWith("\r\n", StringComparison.Ordinal))
            {
                prefix += "\n";
            }
            combinedContent = prefix + content;
        }

        string refined_content = MessageProcessor.RefineMsg(combinedContent, msg);
        perfMonitor.CheckpointInStage(11, "Content_Refined");
        if (msg.IsSimulationMode)
        {
            perfMonitor.MarkStage(11, "SimulationMode_Start");
            // 模拟模式回复 - 在聊天界面显示
            // 获取MainViewModel实例并添加机器人回复消息
            var mainViewModel = GetMainViewModel();
            if (mainViewModel != null)
            {
                var botMessage = new Message
                {
                    Text = refined_content,
                    IsFromUser = false, // 机器人回复
                    Timestamp = DateTime.Now
                };
                if (_dispatcher != null)
                {
                    _dispatcher.Post(() => mainViewModel.Messages.Add(botMessage));
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => mainViewModel.Messages.Add(botMessage));
                }
            }
            perfMonitor.MarkStage(11, "SimulationMode_Complete");
        }
        else if (WSconnection.IsWsConnected)
        {
            perfMonitor.MarkStage(11, "WebSocket_Start");
            // WebSocket连接模式 - 发送到服务器
            if (msg.Source == MessageSource.group)
            {
                WSconnection.SendGroupMessage(msg.GroupId, refined_content);
                perfMonitor.CheckpointInStage(11, "GroupMessageSent");
                Log.Warn($"Sent group message to GroupId:{msg.GroupId} Content:'{refined_content}', original:'{content}'");
            }
            else if (msg.Source == MessageSource.privatechat)
            {
                WSconnection.SendPrivateMessage(msg.UserId, refined_content);
                perfMonitor.CheckpointInStage(11, "PrivateMessageSent");
            }
            perfMonitor.MarkStage(11, "WebSocket_Complete");
        }
        else
        {
            perfMonitor.MarkStage(11, "LocalMode");
            // 本地模式 - 在聊天界面显示回复（用于测试）
            // 这是一个临时的实现，用于本地测试机器人功能
            // 在实际部署时，应该通过WebSocket发送到服务器
        }

        try
        {
            OnReplySent?.Invoke(refined_content, msg);
        }
        catch { /* 事件回调异常不应阻断正常回复 */ }

        perfMonitor.Complete();
    }

    /// <summary>
    /// 回复转发消息
    /// </summary>
    public void ReplyForward(List<(string timestamp, long userId, string senderName, string content)> entries, Msg msg)
    {
        Log.InfoFormat($"ReplyForward called with {entries.Count} entries, IsSimulationMode={msg.IsSimulationMode}");

        if (msg.IsSimulationMode)
        {
            // 模拟模式转发 - 创建合并气泡消息
            var mainViewModel = GetMainViewModel();
            if (mainViewModel != null)
            {
                // 创建一条合并转发消息，而不是逐条创建普通消息
                var forwardContents = new List<string>();
                foreach (var entry in entries)
                {
                    forwardContents.Add(entry.content);
                }

                var forwardMessage = new Message
                {
                    Text = "[转发消息]",  // 合并气泡的标题
                    IsFromUser = false,   // 机器人回复
                    IsForwardMessage = true,  // 标记为合并转发消息
                    ForwardContent = forwardContents,  // 设置转发内容列表
                    Timestamp = DateTime.Now
                };

                Log.InfoFormat($"Simulation mode forward message: {forwardContents.Count} segments");

                if (_dispatcher != null)
                {
                    _dispatcher.Post(() => mainViewModel.Messages.Add(forwardMessage));
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => mainViewModel.Messages.Add(forwardMessage));
                }
            }
        }
        else if (WSconnection.IsWsConnected && msg.Source == MessageSource.group)
        {
            var messages = new List<Dictionary<string, object>>();
            foreach (var entry in entries)
            {
                var contentSegments = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["data"] = new Dictionary<string, object>
                        {
                            ["text"] = entry.content
                        }
                    }
                };

                var node = new Dictionary<string, object>
                {
                    ["type"] = "node",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["user_id"] = entry.userId,
                        ["nickname"] = entry.senderName,
                        ["content"] = contentSegments
                    }
                };
                messages.Add(node);
            }

            WSconnection.SendGroupForwardMessage(msg.GroupId, messages);
        }
        else
        {
            Log.Warn("ReplyForward: WebSocket not connected or not group message");
        }
    }

    /// <summary>
    /// 获取用户信息
    /// 优先级：缓存 > OneBot API > 默认值
    /// 异步获取信息并更新缓存，但立即返回（可能返回默认值）
    /// </summary>
    public UserInfo GetUserInfo(long userId, bool isSimulationMode = false)
    {
        // 模拟模式返回模拟用户信息
        if (isSimulationMode)
        {
            return new UserInfo(userId, $"模拟用户_{userId}");
        }

        // 检查缓存
        if (_userInfoCache.TryGetValue(userId, out var cachedInfo))
        {
            return cachedInfo;
        }

        // 同步返回默认值，异步后台获取真实信息并更新缓存
        var defaultUserInfo = new UserInfo(userId, $"用户_{userId}");
        _userInfoCache[userId] = defaultUserInfo;

        Log.InfoFormat($"[GetUserInfo] 用户 {userId} 信息未缓存，等待获取");
        var cachedInfo2 = FetchAndCacheUserInfo(userId);

        return cachedInfo2;
    }

    /// <summary>
    /// 异步获取并缓存用户信息
    /// </summary>
    private UserInfo FetchAndCacheUserInfo(long userId)
    {
        try
        {
            var result = WSconnection.GetStrangerInfoAsync(userId).Result;
            UserInfo userInfo;
            if (result.HasValue)
            {
                var data = result.Value;
                string nickname = "未知用户";

                if (data.TryGetProperty("data", out var dataObj))
                {
                    if (dataObj.TryGetProperty("nickname", out var nicknameElem))
                    {
                        nickname = nicknameElem.GetString() ?? "未知用户";
                    }
                }

                userInfo = new UserInfo(userId, nickname);
                _userInfoCache[userId] = userInfo;
                return userInfo;
            }
            else
            {
                Log.Warn($"[GetUserInfo] 获取用户 {userId} 信息失败，使用默认值");
                return new UserInfo(userId, $"用户_{userId}");
            }
            
        }
        catch (Exception ex)
        {
            Log.Error($"[GetUserInfo] 获取用户 {userId} 的信息失败: {ex.Message}");
            return new UserInfo(userId, $"用户_{userId}");
        }
    }

    /// <summary>
    /// 获取指定用户在群中的名片名称
    /// 当前实现暂返回null，需要在处理群名片事件时更新缓存
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="isSimulationMode">是否为模拟模式</param>
    /// <returns>群名片名称，如果未设置则返回null</returns>
    public string? GetGroupCardName(long userId, bool isSimulationMode = false)
    {
        try
        {
            // 尝试从缓存获取群名片
            // 注意：当前实现暂未能自动获取群名片，需要通过 OnGroupCard 事件来更新缓存
            // 实现方式：在处理 OnGroupCard 事件时调用 SetGroupCardName 方法
            
            // 由于目前无法直接通过API获取群名片，暂从本地缓存查询
            // 后续可扩展为支持通过 WSconnection 获取
            
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"[GetGroupCardName] 获取用户 {userId} 群名片失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 设置/更新用户的群名片缓存
    /// 在处理群名片变更事件时调用此方法
    /// </summary>
    /// <param name="groupId">群ID</param>
    /// <param name="userId">用户ID</param>
    /// <param name="cardName">群名片名称</param>
    public void SetGroupCardName(long groupId, long userId, string cardName)
    {
        try
        {
            var key = (groupId, userId);
            if (string.IsNullOrWhiteSpace(cardName))
            {
                _groupCardCache.Remove(key);
            }
            else
            {
                _groupCardCache[key] = cardName;
            }
            Log.Normal($"[SetGroupCardName] 已更新用户 {userId} 在群 {groupId} 的群名片: {cardName}");
        }
        catch (Exception ex)
        {
            Log.Error($"[SetGroupCardName] 设置群名片失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取自身信息
    /// 返回缓存的机器人信息，如果缓存还未初始化则返回默认值
    /// </summary>
    /// <summary>
    /// 获取自身信息
    /// 返回缓存的机器人信息，如果缓存还未初始化则返回默认值
    /// </summary>
    public UserInfo GetSelfInfo()
    {
        // 返回缓存的信息，或使用默认值
        if (!string.IsNullOrEmpty(_cachedSelfNickname) && _cachedSelfUserId > 0)
        {
            return new UserInfo(_cachedSelfUserId, _cachedSelfNickname);
        }

        // 缓存还未初始化，返回默认值
        return new UserInfo(_cachedSelfUserId > 0 ? _cachedSelfUserId : 1001, _cachedSelfNickname ?? "机器人");
    }

    /// <summary>
    /// 初始化机器人自身信息
    /// 应在 WSconnection 连接成功后调用（包括启动连接和手动reconnect）
    /// 需要传入当前的 WSconnection 实例，确保使用正确的连接对象
    /// </summary>
    public void InitializeSelfInfo(WSconnection wsConnection)
    {
        if (wsConnection != null)
        {
            InitializeSelfInfoAsync(wsConnection);
        }
    }

    /// <summary>
    /// 异步初始化机器人自身信息
    /// 在 WSconnection 连接成功后调用，缓存机器人账号ID和昵称
    /// 使用传入的 WSconnection 实例获取登录信息
    /// </summary>
    private void InitializeSelfInfoAsync(WSconnection wsConnection)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (wsConnection.IsWsConnected)
                {
                    var loginInfo = await wsConnection.GetLoginInfoAsync();
                    if (loginInfo != null && loginInfo.Value.TryGetProperty("data", out var data))
                    {
                        if (data.TryGetProperty("user_id", out var userIdEl))
                        {
                            _cachedSelfUserId = userIdEl.GetInt64();
                        }
                        if (data.TryGetProperty("nickname", out var nicknameEl))
                        {
                            _cachedSelfNickname = nicknameEl.GetString() ?? "机器人";
                        }
                        Log.Normal($"[自身信息] 已初始化机器人ID: {_cachedSelfUserId}, 昵称: {_cachedSelfNickname}");
                    }
                }
                else
                {
                    Log.Warn("[自身信息] WebSocket 未连接，无法初始化机器人自身信息");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[自身信息] 初始化机器人信息失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 上传群文件
    /// </summary>
    public void UploadGroupFile(long groupId, string filePath, string name = "")
    {
        if (WSconnection.IsWsConnected)
        {
            WSconnection.UploadGroupFile(groupId, filePath, name);
        }
    }

    /// <summary>
    /// 获取群成员信息
    /// </summary>
    public async Task<JsonElement?> GetGroupMemberInfo(long groupId, long userId, Action<JsonElement> callback)
    {
        if (WSconnection.IsWsConnected)
        {
            var result = await WSconnection.GetGroupMemberInfoAsync(groupId, userId);
            if (result.HasValue)
            {
                callback(result.Value);
            }
            return result;
        }
        callback(default);
        return null;
    }

    /// <summary>
    /// 检查日志关闭权限
    /// </summary>
    public async void CheckLogClosePermission(long groupId, long userId, Action<bool> callback)
    {
        if (WSconnection.IsWsConnected)
        {
            try
            {
                var result = await WSconnection.GetGroupMemberInfoAsync(groupId, userId);
                if (result.HasValue)
                {
                    // 检查 API 返回状态码
                    if (result.Value.TryGetProperty("retcode", out var retcodeProperty))
                    {
                        int retcode = retcodeProperty.GetInt32();
                        if (retcode != 0)
                        {
                            Log.Error($"获取群成员信息失败: retcode={retcode}");
                            callback(false);
                            return;
                        }
                    }

                    // 从 data 对象中提取角色信息
                    if (result.Value.TryGetProperty("data", out var dataElement) &&
                        dataElement.TryGetProperty("role", out var roleProperty))
                    {
                        var role = roleProperty.GetString();
                        bool hasPermission = role == "owner" || role == "admin";
                        Log.Normal($"[权限检查] 用户 {userId} 在群 {groupId} 的角色: {role}, 权限结果: {hasPermission}");
                        callback(hasPermission);
                        return;
                    }
                    else
                    {
                        Log.Error($"响应缺少 data 或 role 字段");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"检查用户权限失败: {ex.Message}");
            }
        }

        // 如果无法获取用户信息，默认无权限
        callback(false);
    }

    /// <summary>
    /// 检查机器人在指定群中的权限（是否为群主或管理员）
    /// </summary>
    public void CheckBotGroupPermission(long groupId, Action<bool> callback)
    {
        var selfInfo = GetSelfInfo();
        CheckLogClosePermission(groupId, selfInfo.UserId, callback);
    }

    /// <summary>
    /// 同意加群申请
    /// </summary>
    public void ApproveGroupRequest(string flag)
    {
        if (WSconnection.IsWsConnected)
        {
            WSconnection.ApproveGroupRequest(flag);
            Log.InfoFormat($"同意加群申请: flag={flag}");
        }
    }

    /// <summary>
    /// 同意加好友申请
    /// </summary>
    public void ApproveFriendRequest(string flag)
    {
        if (WSconnection.IsWsConnected)
        {
            WSconnection.ApproveFriendRequest(flag);
            Log.InfoFormat($"同意加好友申请: flag={flag}");
        }
    }

    /// <summary>
    /// 设置群成员名片
    /// </summary>
    public void SetGroupCard(long groupId, long userId, string card)
    {
        if (WSconnection.IsWsConnected)
        {
            WSconnection.SetGroupCard(groupId, userId, card);
            Log.Normal($"已设置群 {groupId} 中用户 {userId} 的名片为: {card}");
        }
    }

    /// <summary>
    /// MainViewModel引用，用于模拟模式回复
    /// </summary>
    public MDiceV2.Core.UI.ViewModels.MainViewModel? MainViewModel { get; set; }

    /// <summary>
    /// MessageProcessor引用，用于设置回复
    /// </summary>
    public MessageProcessor? MessageProcessor { get; set; }

    /// <summary>
    /// 获取MainViewModel实例
    /// </summary>
    private MDiceV2.Core.UI.ViewModels.MainViewModel? GetMainViewModel()
    {
        // 优先使用注入的MainViewModel引用
        if (MainViewModel != null)
        {
            return MainViewModel;
        }

        // 从MessageProcessor获取MainViewModel引用
        if (MessageProcessor?.MainViewModel != null)
        {
            return MessageProcessor.MainViewModel;
        }

        // 通过Avalonia的应用实例获取MainViewModel
        // 假设MainViewModel是作为DataContext设置在主窗口上的
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MDiceV2.Core.UI.ViewModels.MainViewModel mainViewModel)
        {
            return mainViewModel;
        }

        // 如果上面的方法不工作，可以尝试通过单例模式或其他方式
        // 这里先返回null，具体实现需要根据应用的架构调整
        Log.Warn("无法获取MainViewModel实例用于模拟模式回复");
        return null;
    }

    /// <summary>
    /// 处理焦点状态下的消息
    /// </summary>
    private void HandleFocusedMessage(string userId, string focusType, Msg msg, string message, bool isSimulationMode = false)
    {
        if (focusType.StartsWith(CharacterCards.CharacterCardFileImportCoordinator.FocusPrefix, StringComparison.Ordinal))
        {
            OnCharacterCardImportConfirmation?.Invoke(msg, message);
            return;
        }

        if (focusType.StartsWith(DatabaseImportCoordinator.FocusPrefix, StringComparison.Ordinal))
        {
            OnDatabaseImportConfirmation?.Invoke(msg, message);
            return;
        }

        // 处理人物卡删除确认焦点
        if (focusType.StartsWith("com_del_confirm:"))
        {
            HandleComDelConfirmFocus(userId, focusType, msg, message, isSimulationMode);
            return;
        }

        switch (focusType)
        {
            case "carddecision":
                Log.Warn($"[HandleFocusedMessage] 处理卡牌决策焦点 for user {userId}");
                HandleCardDecisionFocus(userId, msg, message, isSimulationMode);
                break;
            default:
                Log.Warn($"未知的焦点类型: {focusType}");
                // 清除未知焦点状态
                ClearUserFocus(userId);
                break;
        }
    }

    /// <summary>
    /// 处理人物卡删除确认焦点
    /// </summary>
    private void HandleComDelConfirmFocus(string userId, string focusType, Msg msg, string message, bool isSimulationMode = false)
    {
        // 从焦点类型中提取要删除的卡名
        string cardName = focusType.Substring("com_del_confirm:".Length);
        string userMessage = message.Trim().ToLowerInvariant();

        if (userMessage == "y" || userMessage == "yes")
        {
            // 执行删除
            if (MessageProcessor != null)
            {
                bool deleted = MessageProcessor.DeleteCharacterCard(long.Parse(userId), cardName);
                if (deleted)
                {
                    Reply($"人物卡 '{cardName}' 已成功删除。", msg);
                }
                else
                {
                    Reply($"删除人物卡 '{cardName}' 失败，可能已被删除或发生错误。", msg);
                }
            }
            else
            {
                Reply("MessageProcessor 未初始化，无法执行删除操作。", msg);
            }
        }
        else
        {
            Reply("操作已取消。", msg);
        }

        // 清除焦点状态
        ClearUserFocus(userId);
    }

    /// <summary>
    /// 为 duel 命令创建转发消息节点
    /// </summary>
    private (string timestamp, long userId, string senderName, string content) CreateDuelForwardNode(string content)
    {
        var selfInfo = GetSelfInfo();
        var botId = selfInfo?.UserId ?? 1001;
        var botName = selfInfo?.Nickname ?? "机器人";
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return (timestamp, botId, botName, content);
    }

    /// <summary>
    /// duel 指令转发消息回复（支持群组和私聊）
    /// 群组使用 OneBot 11 转发格式，私聊 fallback 到普通消息
    /// </summary>
    private void ReplyDuelForward(List<string> messageContents, Msg msg)
    {
        if (msg.Source == MessageSource.group && WSconnection?.IsWsConnected == true)
        {
            // 群组：转换为转发节点列表
            var forwardNodes = messageContents
                .Select(content => CreateDuelForwardNode(content))
                .ToList();
            ReplyForward(forwardNodes, msg);
        }
        else
        {
            // 私聊或未连接：回退为普通消息（每条分别发送）
            foreach (var content in messageContents)
            {
                Reply(content, msg);
            }
        }
    }

    /// <summary>
    /// 处理卡牌决策焦点状态
    /// </summary>
    private void HandleCardDecisionFocus(string userId, Msg msg, string message, bool isSimulationMode = false)
    {
        // 获取用户的游戏状态
        var gameState = LoadUserGameState(userId);
        if (gameState == null)
        {
            Log.Warn($"[HandleCardDecisionFocus] 无法加载游戏状态 for user {userId}");
            Reply("无法找到你的游戏数据，请使用 .duel 指令重新开始游戏。", msg);
            ClearUserFocus(userId);
            return;
        }

        if (gameState.IsGameOver)
        {
            Log.Warn($"[HandleCardDecisionFocus] 游戏已标记为结束 for user {userId}");
            Reply("你的游戏已经结束，请使用 .duel 指令重新开始游戏。", msg);
            ClearUserFocus(userId);
            return;
        }

        // 创建TurnManager处理决策
        var turnManager = new MDiceV2.Core.GameBattle.TurnManager(gameState);

        // 【关键】记录初始回合数，用于检测回合是否切换
        int initialTurn = gameState.CurrentTurn;

        // 解析用户输入
        var trimmedMessage = message.Trim().ToLower();
        bool isValidInput = false;
        List<string> messages = new List<string>();
        Log.InfoFormat($"[HandleCardDecisionFocus]： {gameState.IsProcessingHandAction} ");
        
        // 检查是否为搜索命令：s+关键词（空格可选或多个）
        if (trimmedMessage.StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            string keyword = trimmedMessage.Substring(1).Trim();
            
            if (string.IsNullOrEmpty(keyword))
            {
                // 关键词为空，提示用法
                Reply("请输入搜索关键词，格式：s 关键词或s关键词", msg);
                return;
            }

            // 从RuleDataIO的"duel"规则表中查询
            string? searchResult = MessageProcessor?.RuleDataIO?.ReadData("duel", keyword);
            if (!string.IsNullOrEmpty(searchResult))
            {
                Reply($"{keyword}: {searchResult}", msg);
            }
            else
            {
                Reply($"未找到 duel 中的 {keyword}", msg);
            }
            return;
        }
        
        // 检查是否处于手牌操作阶段
        if (gameState.IsProcessingHandAction)
        {
            // 手牌操作模式：支持 1.1, 2.y, 3.n, 0, end 等格式
            if (trimmedMessage == "0" || trimmedMessage == "end")
            {
                // 跳过回合
                messages = turnManager.SkipTurnWithHand();
                isValidInput = true;
            }
            else
            {
                // 手牌使用命令
                messages = turnManager.UseCardFromHand(trimmedMessage);
                
                // 【关键】检查是否返回了格式错误标记
                bool hasFormatError = messages.Any(m => m == "[CARD_FORMAT_ERROR]");
                if (hasFormatError)
                {
                    // 格式错误，直接清除焦点，不显示详细错误信息
                    Reply("手牌操作失误，请重新输入 .duel 来进入游戏。", msg);
                    ClearUserFocus(userId);
                    return;
                }
                
                // 不是格式错误才认为是有效输入
                isValidInput = true;
            }

            if (isValidInput)
            {
                // 【改进】检查是否发生了回合切换，仅在回合切换时记录
                // 回合切换说明已经执行了完整的 EndTurn → StartTurn 流程
                if (gameState.CurrentTurn > initialTurn)
                {
                    Log.InfoFormat($"[HandleCardDecisionFocus] 回合切换检测：{initialTurn} → {gameState.CurrentTurn}");
                    MessageProcessor?.RecordDuelTurn(long.Parse(userId));
                    
                    // 【关键检查】检查游戏是否因回合限制被强制结束
                    // 注意：此时IsGameOver可能刚被设置为true（在RecordDuelTurn中）
                    if (gameState.IsGameOver)
                    {
                        messages.Add("#累了啦，今天就到这里了~~！");
                        var finalMessage = string.Join("\n", messages);
                        ReplyDuelForward(new List<string> { finalMessage }, msg);
                        ClearUserFocus(userId);
                        return;
                    }
                }
                
                // 检查是否还有手牌需要处理，如果有手牌则保持焦点状态
                bool hasMoreHandCards = gameState.Player2.HandCards.Count > 0 && gameState.IsProcessingHandAction;
                if (!hasMoreHandCards)
                {
                    // 没有更多手牌，清除焦点状态
                    ClearUserFocus(userId);
                }
                // 如果还有手牌，焦点状态会继续保留，等待用户输入下一个手牌指令
            }
            else
            {
                // 手牌操作失误，清除焦点状态并提示重新开始游戏
                Reply("手牌操作失误，请重新输入 .duel 来进入游戏。", msg);
                ClearUserFocus(userId);
                return;
            }

            // 将所有消息合并为一条消息发送，中间用换行分隔
            var combinedMessage = string.Join("\n", messages);
            ReplyDuelForward(new List<string> { combinedMessage }, msg);
            return;
        }

        // 没有在手牌操作阶段，游戏应该已结束或状态不正常
        Reply("游戏状态异常，请重新开始游戏。", msg);
        ClearUserFocus(userId);
    }

    /// <summary>
    /// 加载用户游戏状态（从内存中获取）
    /// </summary>
    private GameState? LoadUserGameState(string userId)
    {
        return MessageProcessor?.LoadUserGameState(userId) ?? null;
    }

    /// <summary>
    /// 设置用户焦点状态
    /// </summary>
    public void SetUserFocus(string userId, string focusType)
    {
        UserFocusStates[userId] = focusType;
        Log.InfoFormat($"用户 {userId} 进入焦点状态: {focusType}");
    }

    /// <summary>
    /// 清除用户焦点状态
    /// </summary>
    public void ClearUserFocus(string userId)
    {
        if (UserFocusStates.Remove(userId))
        {
            Log.InfoFormat($"用户 {userId} 退出焦点状态");
        }
    }

    /// <summary>
    /// 获取用户焦点状态
    /// </summary>
    public string? GetUserFocus(string userId)
    {
        return UserFocusStates.TryGetValue(userId, out string? focusType) ? focusType : null;
    }

    /// <summary>
    /// 处理模拟消息
    /// </summary>
    public void HandleSimulationMessage(string message, MessageSource messageSource, bool isSimulationMode, long userId = 1001, long groupId = 0)
    {
        HandleMessage(groupId, userId, message, messageSource, isSimulationMode);
    }

    /// <summary>
    /// 退出群聊
    /// </summary>
    public void LeaveGroup(long groupId)
    {
        if (WSconnection.IsWsConnected)
        {
            WSconnection.LeaveGroup(groupId);
            Log.InfoFormat($"退出群聊: {groupId}");
        }
    }

    /// <summary>
    /// 如果启用了SendGroupJoinReport，发送群加入记录到Master群
    /// </summary>
    private void SendGroupJoinReportIfEnabled(long groupId, long userId, string comment)
    {
        try
        {
            var config = GetBasicConfigSnapshot();
            if (config == null)
            {
                Log.Warn("[群加入报告] MessageProcessor未初始化，无法发送报告");
                return;
            }

            if (!config.SendGroupJoinReport)
            {
                Log.InfoFormat("[群加入报告] 未启用群加入报告，忽略用户{0} 群{1}", userId, groupId);
                return;
            }

            string reportMessage = string.Format(
                GlobalFeedbackMessages.FeedbackTemplates["GroupJoinApproved"],
                userId,
                groupId,
                comment
            );

            if (!string.IsNullOrEmpty(config.Master) && long.TryParse(config.Master, out long masterId))
            {
                WSconnection.SendPrivateMessage(masterId, reportMessage);
                Log.Normal($"[群加入报告] 已发送私聊报告到 Master {masterId}，用户ID: {userId}");
            }

            if (!string.IsNullOrEmpty(config.MasterGroup) && long.TryParse(config.MasterGroup, out long masterGroupId))
            {
                    WSconnection.SendGroupMessage(masterGroupId, reportMessage);
                    Log.Normal($"[群加入报告] 已发送群组报告到 MasterGroup {masterGroupId}，用户ID: {userId}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[群加入报告] 发送群加入报告时出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 如果启用了SendFriendJoinReport，发送好友加入记录到MasterGroup
    /// </summary>
    private void SendFriendJoinReportIfEnabled(long userId, string comment)
    {
        try
        {
            var config = GetBasicConfigSnapshot();
            if (config == null)
            {
                Log.Warn("[好友加入报告] MessageProcessor未初始化，无法发送报告");
                return;
            }
            
            // 检查是否启用了报告
            if (!config.SendFriendJoinReport)
            {
                Log.InfoFormat($"[好友加入报告] 未启用好友加入报告，忽略用户 {userId}");
                return;
            }

            // 生成报告消息
            try
            {
                string reportMessage = string.Format(
                    GlobalFeedbackMessages.FeedbackTemplates["FriendRequestApprovedReport"],
                    userId,
                    string.IsNullOrEmpty(comment) ? "[自动同意]" : comment
                );

                if (!string.IsNullOrEmpty(config.Master) && long.TryParse(config.Master, out long masterId))
                {
                    WSconnection.SendPrivateMessage(masterId, reportMessage);
                }

                if (!string.IsNullOrEmpty(config.MasterGroup) && long.TryParse(config.MasterGroup, out long masterGroupId))
                {
                    WSconnection.SendGroupMessage(masterGroupId, reportMessage);
                    Log.Normal($"[好友加入报告] 已发送好友申请报告到群 {masterGroupId}，用户ID: {userId}");
                }
            }
            catch (KeyNotFoundException ex)
            {
                Log.Error($"[好友加入报告] 模板 'FriendRequestApprovedReport' 不存在: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[好友加入报告] 发送好友加入报告时出错: {ex.Message}");
        }
    }
}
