using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;
using MDiceV2.Core.GameBattle;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Abstractions;
using static MDiceV2.Models.Dice;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MDiceV2.Models;

/// <summary>
/// 游戏阶段枚举
/// </summary>
public enum GamePhase
{
    /// <summary>
    /// 没有游戏
    /// </summary>
    NoGame,
    /// <summary>
    /// 游戏进行中
    /// </summary>
    GameOngoing,
    /// <summary>
    /// 等待用户决策
    /// </summary>
    WaitingForDecision,
    /// <summary>
    /// 游戏已结束
    /// </summary>
    GameEnded
}

public partial class MessageProcessor : ObservableObject
{
    /// <summary>
    /// 指令前缀列表（按长度从长到短排序，确保更长的前缀优先匹配）
    /// </summary>
    private readonly List<string> prefixes = new()
    { "dismiss", "welcome", "rule", "help", "jrrp", "abot", "team", "duel", "deck", "draw", "bot", "diy", "name", "com", "log", "ai",
    "cc", "ra", "rc", "st", "sc", "ti", "gc", "as", "en", "cn", "ri", "ww", "r" };

    /// <summary>
    /// 指令处理器字典（.前缀命令）
    /// </summary>
    private ConcurrentDictionary<string, Action<string, Msg>>? commandHandlers;

    /// <summary>
    /// 权限指令处理器字典（#前缀命令）
    /// </summary>
    private ConcurrentDictionary<string, Action<string, Msg>>? authCommandHandlers;

    /// <summary>
    /// 内存中的游戏状态字典 string:userId -> GameState
    /// 游戏状态在运行时保存在内存中，只在启动时加载，关闭时保存
    /// </summary>
    private ConcurrentDictionary<string, MDiceV2.Core.GameBattle.GameState> gameStates = new();



    /// <summary>
    /// 处理消息
    /// </summary>
    public void OnHandleMessage(Msg msg)
    {
        // 仅在调试模式或当前用户是调试启动者时创建性能监控器
        // 这样可以避免每条消息都创建对象的开销
        PerformanceMonitor? perfMonitor = null;
        bool isDebugMode = DebugMonitor.IsInitiator(msg.UserId);
        Log.Error($"[消息处理] {msg.Content}");
        if (isDebugMode)
        {
            perfMonitor = new PerformanceMonitor($"Msg_{msg.UserId}_{DateTime.UtcNow.Ticks}");
            perfMonitor.MarkStage(1, "MessageReceived");
        }

        // 预载权限信息，供后续指令使用
        EnsureMsgAuthInfo(msg);

        try
        {
            // 首先尝试分发给Mod处理（Mod有优先级）
            if (msg.Source == MessageSource.group)
            {
                if (_modEventBridge == null)
                {
                    Log.Warn($"[Mod处理] ModEventBridge未初始化! GroupId={msg.GroupId}, UserId={msg.UserId}, Content={msg.Content}");
                }
                else
                {
                    perfMonitor?.MarkStage(2, "ModCheck_Start");
                    Log.Normal($"[MessageFlow] before InvokeGroupMessage bridgeId={GetObjectId(_modEventBridge)}");
                    //Log.Normal($"[Mod处理] 群消息投递到ModEventBridge: GroupId={msg.GroupId}, UserId={msg.UserId}, Content={msg.Content}");
                    var modResult = _modEventBridge.InvokeGroupMessage(msg.GroupId, msg.UserId, msg.Content ?? "", msg.IsAted);
                    Log.Normal($"[MessageFlow] after InvokeGroupMessage bridgeId={GetObjectId(_modEventBridge)} intercepted={modResult?.Intercepted.ToString() ?? "null"}");
                    perfMonitor?.CheckpointInStage(2, "ModInvoked");

                    if (modResult != null)
                    {
                        Log.Normal($"[Mod处理] 获得Mod结果: Intercepted={modResult.Intercepted}, Reply={modResult.Reply}, ModId={modResult.ModId}");
                        if (modResult.Intercepted)
                        {
                            // Mod已处理此消息，发送回复并返回
                            if (!string.IsNullOrEmpty(modResult.Reply))
                            {
                                // 设置msg.ModId以便RefineMsg使用正确的存储空间
                                msg.ModId = modResult.ModId;
                                Reply(modResult.Reply, msg);
                                Log.Normal($"[Mod] ✓ 消息被Mod处理并回复 (GroupId={msg.GroupId}, UserId={msg.UserId}, Reply={modResult.Reply}, ModId={modResult.ModId})");
                            }
                            perfMonitor?.MarkStage(2, "ModCheck_Complete");
                            perfMonitor?.Complete();
                            // 如果启用了调试模式且当前是启动者，自动关闭并返回结果
                            if (isDebugMode)
                            {
                                var debugInfo = DebugMonitor.CompleteAndAutoClose();
                                if (debugInfo != null)
                                {
                                    Reply($"[#pfm 自动记录结果]\n{debugInfo}", msg);
                                }
                            }
                            return;
                        }
                    }
                    else
                    {
                        perfMonitor?.MarkStage(2, "ModCheck_End");
                        Log.Normal($"[Mod处理] Mod未处理此消息(返回null) GroupId={msg.GroupId}, UserId={msg.UserId}");
                    }
                }
            }
            else if (msg.Source == MessageSource.privatechat)
            {
                if (_modEventBridge == null)
                {
                    Log.Warn($"[Mod处理] ModEventBridge未初始化! UserId={msg.UserId}");
                }
                else
                {
                    Log.Normal($"[Mod处理] 私聊消息投递到ModEventBridge: UserId={msg.UserId}, Content={msg.Content}");
                    var modResult = _modEventBridge.InvokePrivateMessage(msg.UserId, msg.Content ?? "");

                    if (modResult != null)
                    {
                        Log.Normal($"[Mod处理] 获得Mod结果: Intercepted={modResult.Intercepted}, Reply={modResult.Reply}, ModId={modResult.ModId}");
                        if (modResult.Intercepted)
                        {
                            // Mod已处理此消息，发送回复并返回
                            if (!string.IsNullOrEmpty(modResult.Reply))
                            {
                                // 设置msg.ModId以便RefineMsg使用正确的存储空间
                                msg.ModId = modResult.ModId;
                                Reply(modResult.Reply, msg);
                                Log.Normal($"[Mod] ✓ 消息被Mod处理并回复 (UserId={msg.UserId}, Reply={modResult.Reply}, ModId={modResult.ModId})");
                            }
                            perfMonitor?.Complete();
                            // 如果启用了调试模式且当前是启动者，自动关闭并返回结果
                            if (isDebugMode)
                            {
                                var debugInfo = DebugMonitor.CompleteAndAutoClose();
                                if (debugInfo != null)
                                {
                                    Reply($"[#pfm 自动记录结果]\n{debugInfo}", msg);
                                }
                            }
                            return;
                        }
                    }
                    else
                    {
                        //Log.Normal($"[Mod处理] Mod未处理此消息(返回null) UserId={msg.UserId}");
                    }
                }
            }

            // 初始化指令处理器（如果尚未初始化）
            EnsureCommandHandlersInitialized();

            perfMonitor?.MarkStage(3, "CommandHandlerInit_Complete");

            // 将中文句号替换为英文句号（确保所有指令处理器能正常识别）
            if (!string.IsNullOrEmpty(msg.Content) && msg.Content[0] == '。')
            {
                msg.Content = '.' + msg.Content[1..];
            }
            if (!string.IsNullOrEmpty(msg.ContentLower) && msg.ContentLower[0] == '。')
            {
                msg.ContentLower = '.' + msg.ContentLower[1..];
            }

            string trimmedLowerText = (msg.ContentLower?.Trim() ?? string.Empty);

            // 记录日志（仅群聊且开启日志）
            if (msg.Source == MessageSource.group && IsLogEnabled(msg.GroupId) && _trpgLogManager != null)
            {
                perfMonitor?.MarkStage(4, "LogWrite_Start");
                var senderName = GetReasonableSenderName(msg.UserId, msg.IsSimulationMode);
                if (_trpgLogManager.IsLogStarter(msg.GroupId, msg.UserId))
                {
                    senderName = "GM";
                }
                string cleanedContent = CleanMessageContent(msg.Content ?? "");
                _trpgLogManager.WriteLog(msg.GroupId, msg.UserId, senderName, cleanedContent);
                perfMonitor?.MarkStage(4, "LogWrite_Complete");
            }

            // 自动同步群名片（.cn 功能）
            if (msg.Source == MessageSource.group)
            {
                perfMonitor?.MarkStage(4, "CardNameSync_Start");
                string switchKey = $"{msg.UserId}_{msg.GroupId}";

                if (cardNameSwitches.TryGetValue(switchKey, out var isEnabled) && isEnabled)
                {
                    // 获取用户的 cardname 模板
                    if (cardNameTemplates.TryGetValue(msg.UserId, out var template) && !string.IsNullOrWhiteSpace(template))
                    {
                        // 替换占位符得到完整的名片文本
                        string resolvedCardName = ReplaceCardNamePlaceholders(template, msg.UserId);

                        // 异步获取当前群名片并比对，如不一致则更新
                        MessageDistribution?.GetGroupMemberInfo(msg.GroupId, msg.UserId, (memberInfo) =>
                        {
                            try
                            {
                                // 从 data 对象中提取群成员信息
                                if (memberInfo.TryGetProperty("data", out var dataElement) &&
                                    dataElement.TryGetProperty("card", out var cardProperty))
                                {
                                    string currentCard = cardProperty.GetString() ?? "";

                                    // 如果名片不一致，更新群名片
                                    if (!currentCard.Equals(resolvedCardName, StringComparison.Ordinal))
                                    {
                                        MessageDistribution?.SetGroupCard(msg.GroupId, msg.UserId, resolvedCardName);
                                        Log.Normal($"[CardName] 已更新 {msg.UserId} 的群名片为: {resolvedCardName}");
                                    }
                                }
                                else
                                {
                                    Log.Warn($"[CardName] 响应缺少 data 或 card 字段");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"[CardName] 比对群名片失败: {ex.Message}");
                            }
                        });
                    }
                }
                perfMonitor?.MarkStage(4, "CardNameSync_Complete");
            }

            if (msg.ShouldIgnore)
            {
                Log.Normal($"[消息忽略] 群:{msg.GroupId} 用户:{msg.UserId} 包含其他@");
                perfMonitor?.Complete();
                // 如果启用了调试模式且当前是启动者，自动关闭并返回结果
                if (isDebugMode)
                {
                    var debugInfo = DebugMonitor.CompleteAndAutoClose();
                    if (debugInfo != null)
                    {
                        Reply($"[#pfm 自动记录结果]\n{debugInfo}", msg);
                    }
                }
                return;
            }

            perfMonitor?.MarkStage(5, "MessageFilter_Start");

            // # 前缀为带权限指令，交由独立处理
            if (trimmedLowerText.StartsWith("#", StringComparison.Ordinal))
            {
                perfMonitor?.MarkStage(5, "MessageFilter_AuthCommand");
                OnHandleAuthMessage(trimmedLowerText, msg);
                perfMonitor?.Complete();
                // 如果启用了调试模式且当前是启动者，自动关闭并返回结果
                if (isDebugMode)
                {
                    var debugInfo = DebugMonitor.CompleteAndAutoClose();
                    if (debugInfo != null)
                    {
                        Reply($"[#pfm 自动记录结果]\n{debugInfo}", msg);
                    }
                }
                return;
            }

            bool isBotCommand = trimmedLowerText.StartsWith(".bot", StringComparison.OrdinalIgnoreCase);
            long botStateKey = msg.Source == MessageSource.group ? msg.GroupId : -msg.UserId;

            // 检查群状态：如果群被关闭，但被@了，则忽略群关闭状态，继续响应
            if (!isBotCommand && !IsBotEnabled(botStateKey) && !msg.IsAted)
            {
                string disabledIgnoreCommandMessage = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["BotDisabledIgnoreCommand"], trimmedLowerText);
                Log.Normal(disabledIgnoreCommandMessage);
                perfMonitor?.Complete();
                // 如果启用了调试模式且当前是启动者，自动关闭并返回结果
                if (isDebugMode)
                {
                    var debugInfo = DebugMonitor.CompleteAndAutoClose();
                    if (debugInfo != null)
                    {
                        Reply($"[#pfm 自动记录结果]\n{debugInfo}", msg);
                    }
                }
                return;
            }

            // 检查是否为自定义指令（/前缀）
            if (trimmedLowerText.StartsWith("/", StringComparison.Ordinal))
            {
                perfMonitor?.MarkStage(5, "MessageFilter_CustomCommand");
                // 尝试匹配用户的自定义指令
                if (TryHandleCustomCommand(trimmedLowerText, msg))
                {
                    perfMonitor?.Complete();
                    // 如果启用了调试模式且当前是启动者，自动关闭并返回结果
                    if (isDebugMode)
                    {
                        var debugInfo = DebugMonitor.CompleteAndAutoClose();
                        if (debugInfo != null)
                        {
                            Reply($"[#pfm 自动记录结果]\n{debugInfo}", msg);
                        }
                    }
                    return;
                }
            }

            perfMonitor?.MarkStage(5, "MessageFilter_Complete");
            perfMonitor?.MarkStage(6, "CommandMatch_Start");

            foreach (var prefix in prefixes)
            {
                if (trimmedLowerText.StartsWith($".{prefix}", StringComparison.OrdinalIgnoreCase))
                {
                    perfMonitor?.CheckpointInStage(6, $"Matched_{prefix}");
                    Log.Normal($"[命令匹配] ✓ 匹配到前缀: {prefix}");

                    if (commandHandlers.TryGetValue(prefix, out var handler))
                    {
                        Log.Normal($"[命令匹配] ✓ 找到处理器: {prefix}");
                        perfMonitor?.MarkStage(7, "HandlerInvoke_Start");
                        AddTrustForNormalUse(msg.UserId);
                        handler(trimmedLowerText[(prefix.Length + 1)..], msg);
                        perfMonitor?.MarkStage(7, "HandlerInvoke_Complete");
                        perfMonitor?.Complete();
                        // 如果启用了调试模式且当前是启动者，自动关闭并返回结果
                        if (isDebugMode)
                        {
                            var debugInfo = DebugMonitor.CompleteAndAutoClose();
                            if (debugInfo != null)
                            {
                                Reply($"[#pfm 自动记录结果]\n{debugInfo}", msg);
                            }
                        }
                    }
                    else
                    {
                        Log.Error($"[命令匹配] ✗ 前缀 '{prefix}' 匹配但处理器未注册！commandHandlers中已有的处理器: {string.Join(", ", commandHandlers?.Keys ?? [])}");
                    }
                    return;
                }
            }

            Log.Normal($"未识别指令: {trimmedLowerText}");
        }
        catch (Exception ex)
        {
            Log.Error($"处理消息时出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理 # 前缀的带权限指令（系统命令和管理指令）
    /// </summary>
    private void OnHandleAuthMessage(string trimmedLowerText, Msg msg)
    {
        // 初始化权限指令处理器（如果尚未初始化）
        if (authCommandHandlers == null)
        {
            authCommandHandlers = new ConcurrentDictionary<string, Action<string, Msg>>();
            // 系统命令
            authCommandHandlers.TryAdd("update", HandleAuthUpdateCommand);
            authCommandHandlers.TryAdd("test", HandleAuthTestCommand);
            authCommandHandlers.TryAdd("restart", HandleAuthRestartCommand);
            authCommandHandlers.TryAdd("shutdown", HandleAuthShutdownCommand);
            // 管理命令
            authCommandHandlers.TryAdd("aa", HandleAuthAssign);
            // 调试命令
            authCommandHandlers.TryAdd("pfm", HandleDebugPerfMonitor);
        }

        // 移除#前缀，获取命令名
        string commandContent = trimmedLowerText.Substring(1);
        var parts = commandContent.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        string commandName = parts[0].ToLower();
        string args = commandContent.Length > commandName.Length
            ? commandContent.Substring(commandName.Length).Trim()
            : string.Empty;

        // 查找对应的处理器
        if (authCommandHandlers.TryGetValue(commandName, out var handler))
        {
            handler(args, msg);
        }
    }

    /// <summary>
    /// 处理 #update 命令 (action 委托)
    /// #update 或 #update main - 更新主程序
    /// #update mod - 更新 CustomizedReply mod
    /// #update aimod - 更新 AIMod
    /// </summary>
    private void HandleAuthUpdateCommand(string args, Msg msg)
    {
        // 检查权限（系统命令仅Master和1001可用）
        if (!msg.IsSystemAccount && !msg.IsMasterAccount)
        {
            Reply("❌ 权限不足！仅 Master 账号和系统账号可以使用此命令", msg);
            return;
        }

        string subcommand = args.Trim().ToLower();

        if (string.IsNullOrEmpty(subcommand) || subcommand == "main")
        {
            Reply("⏳ 正在检查主程序更新...", msg);
            // 使用异步方式调用，但等待完成后再返回
            TriggerMainUpdateAsync(msg).Wait();
        }
        else if (subcommand == "qq" || subcommand.StartsWith("qq ", StringComparison.OrdinalIgnoreCase))
        {
            HandleQqUpdateCommand(args, msg);
        }
        else if (subcommand == "mod")
        {
            Reply("⏳ 正在检查 CustomizedReply Mod 更新...", msg);
            TriggerModUpdateAsync(msg).Wait();
        }
        else if (subcommand == "aimod")
        {
            Reply("⏳ 正在检查 AIMod 更新...", msg);
            TriggerAiModUpdateAsync(msg).Wait();
        }
        else
        {
            Reply("❌ 未知的更新类型。使用: #update (主程序) / #update mod (CustomizedReply Mod) / #update aimod (AIMod)", msg);
        }
    }

    private void HandleAuthTestCommand(string args, Msg msg)
    {
        // 检查权限（系统命令仅Master和1001可用）
        if (!msg.IsSystemAccount && !msg.IsMasterAccount)
        {
            Reply("❌ 权限不足！仅 Master 账号和系统账号可以使用此命令", msg);
            return;
        }

        Reply("⏳ 正在测试更新脚本...", msg);
        // 使用异步方式调用，但等待完成后再返回
        TriggerTestUpdateAsync(msg).Wait();
    }

    /// <summary>
    /// 确保指令处理器已初始化（从 OnHandleMessage 提取，供外部直接调用）
    /// </summary>
    private void EnsureCommandHandlersInitialized()
    {
        Log.InfoFormat("[CommandInit] EnsureCommandHandlersInitialized START alreadyInitialized={0} bridgeId={1}", commandHandlers != null, GetObjectId(_modEventBridge));
        if (commandHandlers != null) return;

        commandHandlers = new ConcurrentDictionary<string, Action<string, Msg>>();
        commandHandlers.TryAdd("r", HandleRoll);
        commandHandlers.TryAdd("bot", HandleBot);
        commandHandlers.TryAdd("st", HandleSkillInsert);
        commandHandlers.TryAdd("sc", HandleSanityCheck);
        commandHandlers.TryAdd("cc", HandleCostomCheck);
        commandHandlers.TryAdd("ra", HandleRaCommand);
        commandHandlers.TryAdd("rc", HandleRcCommand);
        commandHandlers.TryAdd("log", HandleLog);
        commandHandlers.TryAdd("rule", HandleRule);
        commandHandlers.TryAdd("dismiss", HandleDismiss);
        commandHandlers.TryAdd("help", HandleHelp);
        commandHandlers.TryAdd("name", HandleNameCommand);
        commandHandlers.TryAdd("com", HandleComCommand);
        commandHandlers.TryAdd("as", HandleAsCommand);
        commandHandlers.TryAdd("duel", HandleDuelCommand);
        commandHandlers.TryAdd("diy", HandleDiyCommand);
        commandHandlers.TryAdd("ti", HandleTempInsanity);
        commandHandlers.TryAdd("gc", HandleCharacterGen);
        commandHandlers.TryAdd("team", HandleTeamCommand);
        commandHandlers.TryAdd("en", HandleEnCommand);
        commandHandlers.TryAdd("cn", HandleCardNameCommand);
        commandHandlers.TryAdd("ri", HandleInitiativeCommand);
        commandHandlers.TryAdd("draw", HandleDrawCommand);
        commandHandlers.TryAdd("deck", HandleDeckCommand);
        commandHandlers.TryAdd("jrrp", HandleJrrpCommand);
        commandHandlers.TryAdd("ww", HandleWwRoll);
        commandHandlers.TryAdd("welcome", HandleWelcomeCommand);

        // 注册 Mod 提供的指令处理器（通用框架，与具体Mod解耦）
        if (_modEventBridge != null)
        {
            try
            {
                var modCommands = _modEventBridge.GetAllCommandHandlers();
                Log.Normal($"[指令注册] 从ModEventBridge获取到 {modCommands.Count} 个Mod指令");
                Log.InfoFormat("[CommandInit] mod command count={0} commands={1}", modCommands.Count, string.Join(",", modCommands.Keys));

                foreach (var (cmdName, handler) in modCommands)
                {
                    // 创建wrapper，将 Func<string, object, string?> 转换为 Action<string, Msg>
                    // 处理器返回的内容会由MessageProcessor负责发送（通过Reply）
                    Action<string, Msg> wrappedHandler = (args, msg) =>
                    {
                        try
                        {
                            var result = handler(args, (object)msg);
                            if (!string.IsNullOrEmpty(result))
                            {
                                // 检查是否为转发消息格式 JSON
                                if (result.TrimStart().StartsWith("{\"__forward_message\":true"))
                                {
                                    // 解析转发消息格式
                                    try
                                    {
                                        Log.Normal($"[Mod指令回复] {cmdName}: 使用转发消息格式");
                                        HandleForwardMessageFormat(result, msg);
                                    }
                                    catch (Exception jsonEx)
                                    {
                                        Log.Error($"[Mod指令] 解析转发消息格式失败: {jsonEx.Message}，降级为普通消息");
                                        Reply(result, msg);
                                    }
                                }
                                else
                                {
                                    Reply(result, msg);
                                    Log.Normal($"[Mod指令回复] {cmdName}: {result}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[Mod指令执行] 处理 '{cmdName}' 时异常: {ex.Message}");
                            Reply($"[错误] 执行指令时出错: {ex.Message}", msg);
                        }
                    };

                    if (!commandHandlers.TryAdd(cmdName, wrappedHandler))
                    {
                        Log.Warn($"[指令注册] Mod指令 '{cmdName}' 与已有指令重名，将被忽略");
                    }
                    else
                    {
                        Log.Normal($"[指令注册] ✓ 成功注册Mod指令: '{cmdName}'");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[指令注册] 加载Mod指令异常: {ex.Message}\n{ex.StackTrace}");
            }
        }
        else
        {
            Log.Warn("[指令注册] ModEventBridge为null，无法加载Mod指令");
        }

        Log.InfoFormat("[CommandInit] final command keys: {0}", string.Join(",", commandHandlers.Keys));
    }

    /// <summary>
    /// 直接执行命令处理器（绕过 Mod 处理、日志、权限检查等）。
    /// 供 Mod 调用，用于让 AI 角色执行本体命令。
    /// </summary>
    public void ExecuteCommand(Msg msg)
    {
        try
        {
            if (string.IsNullOrEmpty(msg.Content))
            {
                Log.Warn("[ExecuteCommand] 命令内容为空，跳过执行");
                return;
            }

            // 确保 commandHandlers 已初始化
            EnsureCommandHandlersInitialized();

            // 将中文句号替换为英文句号
            if (msg.Content[0] == '。')
            {
                msg.Content = '.' + msg.Content[1..];
            }
            if (!string.IsNullOrEmpty(msg.ContentLower) && msg.ContentLower[0] == '。')
            {
                msg.ContentLower = '.' + msg.ContentLower[1..];
            }

            string trimmedLowerText = msg.ContentLower?.Trim() ?? string.Empty;

            // 匹配前缀并调用处理器
            foreach (var prefix in prefixes)
            {
                if (trimmedLowerText.StartsWith($".{prefix}", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Normal($"[ExecuteCommand] ✓ 匹配到前缀: {prefix}，UserId={msg.UserId}，GroupId={msg.GroupId}");

                    if (commandHandlers != null && commandHandlers.TryGetValue(prefix, out var handler))
                    {
                        AddTrustForNormalUse(msg.UserId);
                        handler(trimmedLowerText[(prefix.Length + 1)..], msg);
                        Log.Normal($"[ExecuteCommand] ✓ 指令 '{prefix}' 已执行");
                    }
                    else
                    {
                        Log.Error($"[ExecuteCommand] ✗ 前缀 '{prefix}' 匹配但处理器未注册！");
                    }
                    return;
                }
            }

            Log.Warn($"[ExecuteCommand] 未识别指令: {trimmedLowerText}");
        }
        catch (Exception ex)
        {
            Log.Error($"[ExecuteCommand] 执行命令时出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理 #restart 命令 (action 委托) - 重启应用程序
    /// </summary>
    private void HandleAuthRestartCommand(string args, Msg msg)
    {
        // 检查权限（系统命令仅Master和1001可可用）
        if (!msg.IsSystemAccount && !msg.IsMasterAccount)
        {
            Reply("❌ 权限不足！仅 Master 账号和系统账号可以使用此命令", msg);
            return;
        }

        Reply("⏳ 正在重启应用程序...", msg);

        // 使用与 UpdateManager 类似的方式：生成外部批处理脚本等待当前进程退出后再重启
        _ = Task.Run(() =>
        {
            try
            {
                Log.Normal("[系统命令] 准备通过外部脚本重启应用程序...");

                var currentProcess = Process.GetCurrentProcess();
                var pid = currentProcess.Id;

                // 根据启动模式选择重启哪个可执行文件
                var startupMode = ServiceBootstrapper.CurrentStartupMode;
                var appRootDir = GetApplicationRootDirectory();
                var exePath = startupMode == StartupMode.Console
                    ? Path.Combine(appRootDir, "MDiceV2.Console.exe")
                    : Path.Combine(appRootDir, "MDiceV2.Launcher.exe");

                if (string.IsNullOrWhiteSpace(exePath))
                {
                    Log.Error("[系统命令] 无法获取可执行文件路径，重启终止");
                    return;
                }

                var exeName = Path.GetFileName(exePath);
                var batPath = Path.Combine(Path.GetTempPath(), $"mdice_restart_{Guid.NewGuid():N}.bat");

                // 改进的脚本：使用 taskkill 判断进程是否存在，更可靠
                // taskkill /FI "PID eq {pid}" 返回 SUCCESS 表示进程存在，INFO 表示进程不存在
                var bat = "@echo off\r\n" +
                          "setlocal enabledelayedexpansion\r\n" +
                          $"set PID={pid}\r\n" +
                          $"set EXE_PATH={exePath}\r\n" +
                          "echo Waiting for application (PID !PID!) to exit...\r\n" +
                          ":loop\r\n" +
                          "tasklist /FI \"PID eq !PID!\" 2>nul | findstr /I !PID! >nul\r\n" +
                          "if %ERRORLEVEL%==0 (\r\n" +
                          "  REM Process still running\r\n" +
                          "  timeout /t 1 /nobreak >nul\r\n" +
                          "  goto loop\r\n" +
                          ")\r\n" +
                          "REM Process exited, safe to restart\r\n" +
                          "echo Restarting application from: !EXE_PATH!\r\n" +
                          "timeout /t 2 /nobreak >nul\r\n" +
                          "start \"\" \"!EXE_PATH!\"\r\n" +
                          "endlocal\r\n" +
                          "del /f /q \"%~f0\" 2>nul\r\n";

                File.WriteAllText(batPath, bat);
                Log.Normal($"[系统命令] 已创建重启脚本: {batPath}");

                var psi = new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                Process.Start(psi);
                // 退出当前进程，交由批处理脚本完成重启
                // 使用退出码 99 通知 Console：这是有意的重启，不是崩溃
                Environment.Exit(99);
            }
            catch (Exception ex)
            {
                Log.Error($"[系统命令] 创建或启动重启脚本失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 处理 #shutdown 命令 (action 委托) - 关闭应用程序
    /// </summary>
    private void HandleAuthShutdownCommand(string args, Msg msg)
    {
        // 检查权限（系统命令仅Master和1001可用）
        if (!msg.IsSystemAccount && !msg.IsMasterAccount)
        {
            Reply("❌ 权限不足！仅 Master 账号和系统账号可以使用此命令", msg);
            return;
        }

        Reply("⏳ 正在关闭应用程序...", msg);

        Task.Delay(1000).ContinueWith(_ =>
        {
            try
            {
                Log.Normal("[系统命令] 执行关闭操作...");
                Dispose();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log.Error($"[系统命令] 关闭失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 异步触发主程序更新 - 等待更新完成后返回，并向用户实时反馈进度
    /// <summary>
    /// 异步触发主程序更新 - 等待更新完成后返回，并向用户实时反馈进度
    /// 拦截并转发CustomUpdateManager的关键进度消息
    /// </summary>
    private async Task TriggerMainUpdateAsync(Msg msg)
    {
        try
        {
            Log.Normal("[系统命令] 触发主程序更新");

            var sentMessages = new System.Collections.Generic.HashSet<string>();

            void Logger(string message)
            {
                Log.Normal($"[系统命令/更新] {message}");

                // 关键进度检查点 - 转发重要的日志消息给用户
                bool shouldReply = false;
                string replyMessage = "";

                // 1. 获取发布信息
                if (message.Contains("获取GitHub releases") || message.Contains("访问GitHub API"))
                {
                    shouldReply = true;
                    replyMessage = "📋 正在获取GitHub发布版本信息...";
                }
                // 2. 筛选更新包
                else if (message.Contains("筛选") && message.Contains("UpdatePackageV"))
                {
                    shouldReply = true;
                    replyMessage = "🔍 正在筛选可用的更新包...";
                }
                // 3. 版本检查
                else if (message.Contains("最新发布") || message.Contains("当前安装版本") || message.Contains("Release程序集版本"))
                {
                    shouldReply = true;
                    replyMessage = "📦 正在检查版本信息...";
                }
                // 4. 查找Asset
                else if (message.Contains("查找并下载MDiceV2.Core.Dice"))
                {
                    shouldReply = true;
                    replyMessage = "🔎 正在查找更新文件...";
                }
                // 5. 下载文件
                else if (message.Contains("开始下载") || message.Contains("下载文件") || message.Contains("尝试") && message.Contains("下载"))
                {
                    shouldReply = true;
                    replyMessage = "⬇️  正在下载更新文件...";
                }
                // 6. 生成脚本
                else if (message.Contains("生成") && (message.Contains("批处理") || message.Contains("更新脚本")))
                {
                    shouldReply = true;
                    replyMessage = "⚙️  正在生成更新脚本...";
                }
                // 7. 下载完成
                else if (message.Contains("✅") && (message.Contains("下载成功") || message.Contains("文件已保存")))
                {
                    shouldReply = true;
                    replyMessage = "✓ 文件下载完成，准备安装...";
                }
                // 8. 当前版本已是最新
                else if (message.Contains("当前版本已是最新") || message.Contains("版本已最新"))
                {
                    shouldReply = true;
                    replyMessage = "ℹ️  您已在最新版本，无需更新";
                }

                // 避免发送重复消息
                if (shouldReply && !sentMessages.Contains(replyMessage))
                {
                    sentMessages.Add(replyMessage);
                    Reply(replyMessage, msg);
                }
            }
            ;

            var mgr = new CustomUpdateManager(Logger);
            var result = await mgr.ExecuteCustomUpdateAsync();

            if (result.Success)
            {
                Log.Normal($"[系统命令] 主程序更新完成: {result.Message}");
                // 发送成功消息
                Reply("✅ 更新检查完成，程序即将重启应用更新...", msg);

                // 等待消息送达（1秒足以确保消息发送）
                await Task.Delay(1000);

                // 执行重启
                RestartApplication();
            }
            else
            {
                Log.Warn($"[系统命令] 主程序更新未完成: {result.Message}");
                // 在更新失败时回复错误信息
                Reply($"❌ 更新失败: {result.Message}", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[系统命令] 主程序更新过程异常: {ex.Message}");
            Reply($"❌ 更新过程异常: {ex.Message}", msg);
        }
    }

    /// <summary>
    /// 触发Mod更新 - 通过ModEventBridge通知mod进行自动下载
    /// </summary>
    /// <summary>
    /// 异步触发Mod更新 - 通过ModEventBridge通知mod进行自动下载，等待完成后返回
    /// </summary>
    private async Task TriggerTestUpdateAsync(Msg msg)
    {
        try
        {
            Log.Normal("[系统命令] 触发更新脚本测试");

            var mgr = new CustomUpdateManager(Log.Normal);
            var result = await mgr.TestUpdateScriptAsync();

            if (result.Success)
            {
                Log.Normal($"[系统命令] 测试更新脚本完成: {result.Message}");
                Reply($"✅ {result.Message}", msg);

                // 等待消息送达（1秒足以确保消息发送）
                await Task.Delay(1000);

                // 执行重启
                RestartApplication();
            }
            else
            {
                Log.Warn($"[系统命令] 测试更新脚本未完成: {result.Message}");
                Reply($"❌ 测试失败: {result.Message}", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[系统命令] 处理测试更新脚本时发生错误: {ex}");
            Reply($"❌ 测试更新脚本时发生错误: {ex.Message}", msg);
        }
    }

    private async Task TriggerModUpdateAsync(Msg msg)
    {
        try
        {
            Log.Normal("[系统命令] 触发 CustomizedReply Mod 更新");

            // 1) 优先：如果 Mod 已加载，走 Mod 自身的更新逻辑
            if (_modEventBridge != null)
            {
                // 注意：此处的 Id 必须与 CustomizedReply 的 mod.json 中保持一致
                const string customizedReplyModId = "com.example.customreply";

                var ok = await _modEventBridge.RequestModUpdateAsync(customizedReplyModId);
                if (ok)
                {
                    Log.Normal("[系统命令] CustomizedReply Mod 更新请求已提交/完成（具体进度请查看 Mod 日志）");
                    Reply("✅ 已向 CustomizedReply Mod 发起更新请求（查看日志获取下载/解压进度）", msg);
                    return;
                }

                Log.Warn("[系统命令] CustomizedReply Mod 更新请求未成功（可能尚未安装/未被加载），将尝试直接从 GitHub 下载并安装");
            }
            else
            {
                Log.Warn("[系统命令] ModEventBridge 未初始化（可能尚未加载任何 Mod），将尝试直接从 GitHub 下载并安装");
            }

            // 2) 兜底：即使 Mod 未加载/未安装，也能直接从 GitHub 拉取并安装
            void Logger(string message) => Log.Normal($"[系统命令/Mod更新] {message}");
            var installOk = await DownloadAndInstallCustomizedReplyModAsync(Logger);

            if (installOk)
            {
                Reply("✅ CustomizedReply 更新包已下载并安装到运行目录的 mods 文件夹（重启后生效）", msg);
            }
            else
            {
                Reply("❌ CustomizedReply 更新失败：未能从 GitHub 找到/下载符合格式的包，或安装过程出错（请查看日志）", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[系统命令] CustomizedReply Mod 更新过程异常: {ex.Message}");
            Reply($"❌ CustomizedReply Mod 更新过程异常: {ex.Message}", msg);
        }
    }

    private async Task TriggerAiModUpdateAsync(Msg msg)
    {
        try
        {
            Log.Normal("[系统命令] 触发 AIMod 更新");

            void Logger(string message) => Log.Normal($"[系统命令/AIMod更新] {message}");
            var result = await DownloadAndScheduleAIModUpdateAsync(Logger);

            if (result.Success && result.RequiresRestart)
            {
                Reply(
                    $"✅ AIMod 更新包 {result.AssetName} 已下载，程序将退出并由脚本完成覆盖安装后重启。\n" +
                    $"版本：{result.VersionLabel}\n" +
                    $"脚本：{result.ScriptPath}\n" +
                    $"Payload：{result.PayloadDir}",
                    msg);
                await Task.Delay(1200);
                Environment.Exit(99);
            }
            else
            {
                Reply($"❌ AIMod 更新失败：{result.Message}", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[系统命令] AIMod 更新过程异常: {ex.Message}");
            Reply($"❌ AIMod 更新过程异常: {ex.Message}", msg);
        }
    }

    /// <summary>
    /// 获取应用根目录
    /// 如果当前进程在 Core 子目录中（如 Core/MDiceV2.Core.Dice），向上走一级
    /// 否则直接使用可执行文件所在目录
    /// </summary>
    private string GetApplicationRootDirectory()
    {
        try
        {
            var mainModule = Process.GetCurrentProcess().MainModule;
            if (mainModule != null && !string.IsNullOrWhiteSpace(mainModule.FileName))
            {
                var modulePath = mainModule.FileName;
                var moduleDir = Path.GetDirectoryName(modulePath);

                // 如果可执行文件在 Core 子目录中，向上走一级到应用根目录
                if (!string.IsNullOrWhiteSpace(moduleDir) &&
                    Path.GetFileName(moduleDir).Equals("Core", StringComparison.OrdinalIgnoreCase))
                {
                    var rootDir = Path.GetDirectoryName(moduleDir);
                    if (!string.IsNullOrWhiteSpace(rootDir))
                    {
                        Log.Normal($"[系统命令] 检测到在Core子目录运行，应用根目录: {rootDir}");
                        return rootDir;
                    }
                }

                return moduleDir ?? AppContext.BaseDirectory;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[系统命令] 获取启动进程路径失败: {ex.Message}，降级到AppContext.BaseDirectory");
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 重启应用程序 - 生成外部脚本等待当前进程退出后重启
    /// </summary>
    private void RestartApplication()
    {
        try
        {
            Log.Normal("[系统命令] 准备通过外部脚本重启应用程序...");

            var currentProcess = Process.GetCurrentProcess();
            var pid = currentProcess.Id;

            // 根据启动模式选择重启哪个可执行文件
            var startupMode = ServiceBootstrapper.CurrentStartupMode;
            var appRootDir = GetApplicationRootDirectory();
            var exePath = startupMode == StartupMode.Console
                ? Path.Combine(appRootDir, "MDiceV2.Console.exe")
                : Path.Combine(appRootDir, "MDiceV2.Launcher.exe");

            if (string.IsNullOrWhiteSpace(exePath))
            {
                Log.Error("[系统命令] 无法获取可执行文件路径，重启终止");
                return;
            }

            var batPath = Path.Combine(Path.GetTempPath(), $"mdice_restart_{Guid.NewGuid():N}.bat");

            // 改进的脚本：使用 taskkill 判断进程是否存在，更可靠
            var bat = "@echo off\r\n" +
                      "setlocal enabledelayedexpansion\r\n" +
                      $"set PID={pid}\r\n" +
                      $"set EXE_PATH={exePath}\r\n" +
                      "echo Waiting for application (PID !PID!) to exit...\r\n" +
                      ":loop\r\n" +
                      "tasklist /FI \"PID eq !PID!\" 2>nul | findstr /I !PID! >nul\r\n" +
                      "if %ERRORLEVEL%==0 (\r\n" +
                      "  REM Process still running\r\n" +
                      "  timeout /t 1 /nobreak >nul\r\n" +
                      "  goto loop\r\n" +
                      ")\r\n" +
                      "REM Process exited, safe to restart\r\n" +
                      "echo Restarting application from: !EXE_PATH!\r\n" +
                      "timeout /t 2 /nobreak >nul\r\n" +
                      "start \"\" \"!EXE_PATH!\"\r\n" +
                      "endlocal\r\n" +
                      "del /f /q \"%~f0\" 2>nul\r\n";

            File.WriteAllText(batPath, bat);
            Log.Normal($"[系统命令] 已创建重启脚本: {batPath}");

            var psi = new ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = true,
                CreateNoWindow = true
            };

            Process.Start(psi);
            Log.Normal("[系统命令] 重启脚本已启动，当前进程即将退出");

            // 退出当前进程，交由批处理脚本完成重启
            // 使用退出码 99 通知 Console：这是有意的重启，不是崩溃
            Environment.Exit(99);
        }
        catch (Exception ex)
        {
            Log.Error($"[系统命令] 创建或启动重启脚本失败: {ex.Message}");
        }
    }

    private async Task TriggerModUpdate(Msg msg)
    {
        try
        {
            Log.Normal("[系统命令] 触发 CustomizedReply Mod 更新");

            // 在后台线程中执行更新流程，避免阻塞消息处理
            // 此为fire-and-forget的后台任务，是正常的设计模式
#pragma warning disable CS4014
            _ = Task.Run(async () =>
            {
                try
                {
                    // 1) 优先：如果 Mod 已加载，走 Mod 自身的更新逻辑
                    if (_modEventBridge != null)
                    {
                        // 注意：此处的 Id 必须与 CustomizedReply 的 mod.json 中保持一致
                        const string customizedReplyModId = "com.example.customreply";

                        var ok = await _modEventBridge.RequestModUpdateAsync(customizedReplyModId);
                        if (ok)
                        {
                            Log.Normal("[系统命令] CustomizedReply Mod 更新请求已提交/完成（具体进度请查看 Mod 日志）");
                            Reply("✅ 已向 CustomizedReply Mod 发起更新请求（查看日志获取下载/解压进度）", msg);
                            return;
                        }

                        Log.Warn("[系统命令] CustomizedReply Mod 更新请求未成功（可能尚未安装/未被加载），将尝试直接从 GitHub 下载并安装");
                    }
                    else
                    {
                        Log.Warn("[系统命令] ModEventBridge 未初始化（可能尚未加载任何 Mod），将尝试直接从 GitHub 下载并安装");
                    }

                    // 2) 兜底：即使 Mod 未加载/未安装，也能直接从 GitHub 拉取并安装
                    void Logger(string message) => Log.Normal($"[系统命令/Mod更新] {message}");
                    var installOk = await DownloadAndInstallCustomizedReplyModAsync(Logger);

                    if (installOk)
                    {
                        Reply("✅ CustomizedReply 更新包已下载并安装到运行目录的 mods 文件夹（重启后生效）", msg);
                    }
                    else
                    {
                        Reply("❌ CustomizedReply 更新失败：未能从 GitHub 找到/下载符合格式的包，或安装过程出错（请查看日志）", msg);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[系统命令] CustomizedReply Mod 更新过程异常: {ex.Message}");
                    Reply($"❌ CustomizedReply Mod 更新过程异常: {ex.Message}", msg);
                }
            });
#pragma warning restore CS4014
        }
        catch (Exception ex)
        {
            Log.Error($"[系统命令] Mod更新失败: {ex.Message}");
            Reply($"❌ Mod 更新失败: {ex.Message}", msg);
        }
    }

    private async Task<bool> DownloadAndInstallCustomizedReplyModAsync(Action<string> log)
    {
        const string owner = "HumulusQ";
        const string repo = "MDiceV2Public";

        // 与你上传的格式兼容：CustomizedReplyPackV0252353.zip
        const string assetPrefix = "CustomizedReplyPackV";

        var tempZip = Path.Combine(Path.GetTempPath(), $"CustomizedReply_{Guid.NewGuid():N}.zip");
        var tempExtract = Path.Combine(Path.GetTempPath(), $"CustomizedReply_extract_{Guid.NewGuid():N}");

        try
        {
            var cwd = Directory.GetCurrentDirectory();
            var modsRoot = Path.Combine(cwd, "mods");
            Directory.CreateDirectory(modsRoot);

            var targetModFile = Path.Combine(modsRoot, "CustomizedReply.mod");
            var targetModFolder = Path.Combine(modsRoot, "CustomizedReply");

            log($"Mods目录: {modsRoot}");
            log($"目标文件: {targetModFile}");

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(300); // 下载超时:5倍延长(300秒)
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MDiceV2", "1.0"));
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var releasesUrl = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100";
            log($"拉取Release列表: {releasesUrl}");

            var json = await http.GetStringAsync(releasesUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            string? downloadUrl = null;
            string? assetName = null;
            DateTime bestPublishedAt = DateTime.MinValue;
            long bestNumericVersion = -1;

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                DateTime publishedAt = DateTime.MinValue;
                if (rel.TryGetProperty("published_at", out var publishedEl))
                {
                    var publishedStr = publishedEl.GetString();
                    if (!string.IsNullOrWhiteSpace(publishedStr) && DateTime.TryParse(publishedStr, out var parsedPublishedAt))
                    {
                        publishedAt = parsedPublishedAt;
                    }
                }

                if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameEl))
                        continue;

                    var name = nameEl.GetString();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (!name.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!asset.TryGetProperty("browser_download_url", out var urlEl))
                        continue;

                    var url = urlEl.GetString();
                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    // 解析文件名末尾的数字版本：CustomizedReplyPackV0252353.zip -> 252353
                    long numericVersion = -1;
                    if (name.Length > assetPrefix.Length + ".zip".Length)
                    {
                        var digits = name.Substring(assetPrefix.Length, name.Length - assetPrefix.Length - ".zip".Length);
                        if (!string.IsNullOrWhiteSpace(digits) && long.TryParse(digits, out var parsedVersion))
                        {
                            numericVersion = parsedVersion;
                        }
                    }

                    var isBetter = false;
                    if (publishedAt > bestPublishedAt)
                    {
                        isBetter = true;
                    }
                    else if (publishedAt == bestPublishedAt && numericVersion > bestNumericVersion)
                    {
                        isBetter = true;
                    }

                    if (isBetter)
                    {
                        bestPublishedAt = publishedAt;
                        bestNumericVersion = numericVersion;
                        downloadUrl = url;
                        assetName = name;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(assetName))
            {
                log($"未找到资源：名称需匹配 {assetPrefix}*.zip");
                return false;
            }

            log($"找到资源: {assetName}");
            log($"下载URL: {downloadUrl}");

            // 下载到临时zip
            await using (var stream = await http.GetStreamAsync(downloadUrl).ConfigureAwait(false))
            await using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.CopyToAsync(fs).ConfigureAwait(false);
            }

            // 按你的需求：重命名为 CustomizedReply.mod 并放入运行目录 mods 下
            if (File.Exists(targetModFile))
            {
                try
                {
                    var bak = targetModFile + ".bak";
                    File.Copy(targetModFile, bak, overwrite: true);
                    log($"已备份旧文件: {bak}");
                }
                catch (Exception ex)
                {
                    log($"备份旧文件失败（忽略）: {ex.Message}");
                }
            }
            File.Copy(tempZip, targetModFile, overwrite: true);
            log("已写入 CustomizedReply.mod");

            // 同时解压为目录形态，确保 ModPluginLoader 可直接加载
            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // 找出包含 mod.json 的根目录（兼容 zip 内部套一层文件夹）
            string? extractedRoot = null;
            if (File.Exists(Path.Combine(tempExtract, "mod.json")))
            {
                extractedRoot = tempExtract;
            }
            else
            {
                var firstLevelDirs = Directory.GetDirectories(tempExtract);
                if (firstLevelDirs.Length == 1 && File.Exists(Path.Combine(firstLevelDirs[0], "mod.json")))
                {
                    extractedRoot = firstLevelDirs[0];
                }
            }

            if (extractedRoot == null)
            {
                log("解压后未找到 mod.json（zip内容结构不符合预期），仅保留 CustomizedReply.mod");
                return true;
            }

            // 覆盖安装到 mods/CustomizedReply
            if (Directory.Exists(targetModFolder))
            {
                Directory.Delete(targetModFolder, recursive: true);
            }
            CopyDirectory(extractedRoot, targetModFolder);
            log($"已安装目录: {targetModFolder}");

            if (!File.Exists(Path.Combine(targetModFolder, "mod.json")))
            {
                log("安装后仍未发现 mods/CustomizedReply/mod.json（可能 zip 结构异常）");
            }

            return true;
        }
        catch (Exception ex)
        {
            log($"下载/安装失败: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                if (Directory.Exists(tempExtract))
                {
                    Directory.Delete(tempExtract, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(destDir, name);
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            var dest = Path.Combine(destDir, name);
            CopyDirectory(dir, dest);
        }
    }

    /// <summary>
    /// 预载消息权限信息（仅在尚未加载时执行）。
    /// </summary>
    private void EnsureMsgAuthInfo(Msg msg)
    {
        if (msg.IsAuthInfoLoaded)
        {
            return;
        }

        msg.IsSystemAccount = msg.UserId == 1001;

        if (!string.IsNullOrWhiteSpace(basicConfigData.Master) &&
            long.TryParse(basicConfigData.Master, out var masterId) &&
            masterId > 0 && msg.UserId == masterId)
        {
            msg.IsMasterAccount = true;
        }
        else
        {
            msg.IsMasterAccount = false;
        }

        // 检查用户是否为群管理员/群主
        msg.IsGroupAdmin = IsGroupAdmin(msg.GroupId, msg.UserId);

        // 如果缓存中查不到该用户（启动时常见），通过API获取并更新缓存
        if (!msg.IsGroupAdmin && msg.GroupId > 0)
        {
            msg.IsGroupAdmin = EnsureGroupAdminFromApi(msg.GroupId, msg.UserId);
        }

        msg.UserAuthLevel = personAuth.TryGetValue(msg.UserId, out var level) ? level : null;
        msg.IsWhitelisted = msg.UserAuthLevel.HasValue && msg.UserAuthLevel.Value == 0;
        msg.HasAuthPermission = msg.IsSystemAccount || msg.IsMasterAccount ||
                                (msg.UserAuthLevel.HasValue && msg.UserAuthLevel.Value < 3) ||
                                msg.IsGroupAdmin;  // 群管理员也拥有授权权限
        msg.IsAuthInfoLoaded = true;
    }

    /// <summary>
    /// 检查#指令调用权限。
    /// </summary>
    private bool IsAuthCommandAuthorized(Msg msg)
    {
        EnsureMsgAuthInfo(msg);
        return msg.HasAuthPermission;
    }

    /// <summary>
    /// 处理 #aa 指令：设置个人/群白名单等级 (action 委托)
    /// 语法：#aa [g|p] id level
    /// 
    /// 权限检查采用独立的双轨制：
    /// 1. 群管理员/群主 (IsGroupAdmin) - 独立于白名单系统
    /// 2. 白名单系统 (PersonAuth 等级 < 2) - 传统的白名单验证
    /// 以上任一条件满足即可使用此指令。
    /// </summary>
    private void HandleAuthAssign(string args, Msg msg)
    {
        // 白名单系统 (Master、系统账号1001、或 PersonAuth 等级 < 2)
        bool isWhiteListUser = msg.IsSystemAccount || msg.IsMasterAccount ||
                              (msg.UserAuthLevel.HasValue && msg.UserAuthLevel.Value < 2);
        
        
        Log.Normal($"[权限指令] 处理 #aa 指令: UserId={msg.UserId}, Content={msg.Content}");
        Log.Normal($"[权限指令] 通过权限检查: UserId={msg.UserId}");
        string raw = msg.Content ?? string.Empty;

        // 处理 CQ 码格式的 @（如 [CQ:at,qq=123456]）
        // 提取其中的账号 ID，替换整个 CQ 码
        raw = ReplaceCQAtCodeWithId(raw);

        var match = Regex.Match(raw, @"^#aa\s*(?<mode>[gpGP])?\s*(?<id>\S+)\s+(?<level>\d)");
        if (!match.Success)
        {
            Reply("格式: #aa [g|p] <ID> <等级>（等级为单个数字）", msg);
            return;
        }

        char mode = match.Groups["mode"].Success ? char.ToLowerInvariant(match.Groups["mode"].Value[0]) : 'p';
        string idText = ExtractIdToken(match.Groups["id"].Value);
        string levelText = match.Groups["level"].Value;
        byte level = (byte)(levelText[0] - '0');

        if (mode == 'g')
        {
            if (!int.TryParse(idText, out var groupId))
            {
                Reply("群ID无效", msg);
                return;
            }

            long groupIdLong = (long)groupId;
            // 获取或创建群数据记录
            if (!groupDataRecords.TryGetValue(groupIdLong, out var record))
            {
                record = new GroupDataRecord { GroupId = groupIdLong };
                groupDataRecords[groupIdLong] = record;
            }
            record.AuthLevel = level;
            groupAuth[groupId] = level;
            SaveGroupData(groupIdLong);
            Reply($"已设置群 {groupId} 的白名单等级为 {level}", msg);
        }
        else
        {
            if (!long.TryParse(idText, out var userId))
            {
                Reply("用户ID无效", msg);
                return;
            }

            personAuth[userId] = level;
            SaveUserData(userId);
            Reply($"已设置用户 {userId} 的白名单等级为 {level}", msg);
        }
    }

    /// <summary>
    /// 处理 CQ 码格式的 @ 替换
    /// 将 [CQ:at,qq=123456] 替换为对应的账号 ID（123456）
    /// 这样可以让指令支持直接 @ 用户的方式
    /// </summary>
    private string ReplaceCQAtCodeWithId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        // 使用正则表达式匹配所有 [CQ:at,...] 格式的 @ 码
        // 支持：[CQ:at,qq=123456] 或 [CQ:at, qq=123456] 等变体
        string pattern = @"\[CQ:at\s*,\s*qq\s*=\s*(\d+)\s*(?:,\s*[^\]]*?)?\]";
        string result = Regex.Replace(raw, pattern, "$1", RegexOptions.IgnoreCase);

        return result;
    }

    /// <summary>
    /// 从普通数字或CQ码中提取ID文本，返回原始字符串中的首个数字序列；若不存在则返回原输入。
    /// </summary>
    private string ExtractIdToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        // CQ码示例：[CQ:at,qq=123456]
        var cqMatch = Regex.Match(raw, @"qq=(\d+)");
        if (cqMatch.Success) return cqMatch.Groups[1].Value;

        // 通用数字提取
        var numMatch = Regex.Match(raw, @"(\d+)");
        if (numMatch.Success) return numMatch.Groups[1].Value;

        return raw;
    }

    /// <summary>
    /// 处理 .as 指令：以指定成员身份执行指令
    /// 格式：.as [@成员CQ码] [.实际指令]
    /// 支持多个 @：.as [@A] [@B] [.指令]
    /// </summary>
    private void HandleAsCommand(string args, Msg msg)
    {

        string raw = msg.Content?.Trim() ?? string.Empty;
        Log.Error($"[As] 原始指令内容: '{raw}'");
        if (!raw.StartsWith(".as", StringComparison.OrdinalIgnoreCase))
        {
            Reply("委托格式错咯，请使用正确的格式: .as [@成员CQ码] [.指令]", msg);
            return;
        }

        string remainder = raw.Length > 3 ? raw.Substring(3).Trim() : string.Empty;
        if (string.IsNullOrEmpty(remainder))
        {
            Reply("委托格式错咯，请使用正确的格式: .as [@成员CQ码] [.指令]", msg);
            return;
        }

        var tokens = remainder.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int commandIndex = Array.FindIndex(tokens, t => t.StartsWith(".", StringComparison.Ordinal));
        if (commandIndex <= 0)
        {
            Reply("委托格式错咯，请使用正确的格式: .as [@成员CQ码] [.指令]", msg);
            return;
        }

        string mentionText = string.Join(" ", tokens.Take(commandIndex));
        string commandText = string.Join(" ", tokens.Skip(commandIndex)).Trim();

        if (!commandText.StartsWith(".", StringComparison.Ordinal))
        {
            Reply("委托格式错咯，请使用正确的格式: .as [@成员CQ码] [.指令]", msg);
            return;
        }

        var targetUserIds = ExtractAllUserIdsFromMentions(mentionText);
        if (targetUserIds.Count == 0)
        {
            Reply("无法解析目标成员，请使用 @成员 或 [CQ:at,qq=123456]。", msg);
            return;
        }

        foreach (var targetUserId in targetUserIds)
        {
            if (targetUserId <= 0)
            {
                continue;
            }

            string targetName = GetReasonableSenderName(targetUserId, msg.IsSimulationMode);
            string receipt = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["AsProxyReceipt"], targetName);

            Log.Normal($"[As] User {msg.UserId} execute as {targetUserId}: {commandText}");
            var newMsg = new Msg(msg.GroupId, targetUserId, commandText, msg.Source, msg.IsSimulationMode, msg.IsAted, msg.ShouldIgnore);
            newMsg.ReplyPrefix = receipt;
            newMsg.IsAuthInfoLoaded = msg.IsAuthInfoLoaded;
            newMsg.UserAuthLevel = msg.UserAuthLevel;
            newMsg.IsSystemAccount = msg.IsSystemAccount;
            newMsg.IsMasterAccount = msg.IsMasterAccount;
            newMsg.HasAuthPermission = msg.HasAuthPermission;
            newMsg.IsWhitelisted = msg.IsWhitelisted;
            newMsg.IsGroupAdmin = msg.IsGroupAdmin;  // 传递群管理员权限
            OnHandleMessage(newMsg);
        }
    }

    /// <summary>
    /// 处理技能录入/配置指令 (.st)
    /// </summary>
    private void HandleSkillInsert(string args, Msg msg)
    {
        string rawContent = msg.Content.Trim();
        long userId = msg.UserId;

        var match = Regex.Match(rawContent, @"^\.st\s*(?:\((?<character>[^)]+)\))?\s*(?<tail>.*)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["SkillInsertFormatError"], msg);
            return;
        }

        string characterNameFromCommand = match.Groups["character"].Success ? match.Groups["character"].Value.Trim() : string.Empty;
        string tail = match.Groups["tail"].Value.Trim();

        string characterName = GetOrCreateCharacterName(userId, characterNameFromCommand, msg.IsSimulationMode, msg);
        if (characterName == null)
        {
            return;
        }

        var userCharacters = characterSkills.GetOrAdd(userId, _ => new ConcurrentDictionary<string, CharacterSheet>());
        if (!userCharacters.TryGetValue(characterName, out var sheet))
        {
            if (userCharacters.Count >= 6)
            {
                Reply(GlobalFeedbackMessages.FeedbackTemplates["CharacterCardLimitExceeded"], msg);
                return;
            }

            sheet = userCharacters.GetOrAdd(characterName, _ => new CharacterSheet());
        }

        var characterSkillsDict = sheet.Skills;

        // 4) 解析紧随人物名之后的 {key value} 配置块（允许多个），严格从左到右消费
        //    支持 key 为:
        //      - type       -> sheet.CharacterType
        //      - format     -> sheet.COCCharacterDetailsCustomFormat
        while (tail.StartsWith("{"))
        {
            int endBrace = tail.IndexOf('}');
            if (endBrace <= 1)
            {
                // 非法块（没有内容或没有闭合），跳出，避免吞掉后续技能
                break;
            }

            string inner = tail.Substring(1, endBrace - 1).Trim();
            // 从剩余串移除本块
            tail = tail.Substring(endBrace + 1).TrimStart();

            // 使用空格分割 key 和 value
            string[] parts = inner.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                // 不符合格式 {key value}，忽略该块，继续下一个
                continue;
            }

            string key = parts[0].Trim().ToLowerInvariant();
            string value = parts[1].Trim();

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            switch (key)
            {
                case "type":
                    sheet.CharacterType = value;
                    break;
                case "format":
                    sheet.COCCharacterDetailsCustomFormat = value;
                    break;
                default:
                    // 未知配置键，忽略
                    break;
            }
        }

        // 5) 剩余 tail 为技能部分
        string skillsPart = tail;
        if (string.IsNullOrWhiteSpace(skillsPart))
        {
            // 只更新了配置项，无技能，直接保存并反馈
            SaveCharacterSkills();
            Reply($"人物卡 '{characterName}' 配置已更新。", msg);
            return;
        }

        // 新的技能解析逻辑：
        // 技能名：连续字符直到遇到 [0-9+\-dD]
        // 若技能名后为 +/-，则开启掷骰模式（支持 [0-9+\-dD]）；否则只支持纯数字
        List<string> updatedSkills = new List<string>();
        int index = 0;
        while (index < skillsPart.Length)
        {
            // 跳过前导空格
            while (index < skillsPart.Length && char.IsWhiteSpace(skillsPart[index]))
            {
                index++;
            }
            if (index >= skillsPart.Length) break;

            // 匹配技能名：连续字符直到遇到 [0-9+\-dD]
            int skillStart = index;
            while (index < skillsPart.Length &&
                   !char.IsDigit(skillsPart[index]) &&
                   skillsPart[index] != '+' &&
                   skillsPart[index] != '-' &&
                   skillsPart[index] != 'd' &&
                   skillsPart[index] != 'D')
            {
                index++;
            }
            string skillName = skillsPart.Substring(skillStart, index - skillStart).Trim();
            if (string.IsNullOrEmpty(skillName))
            {
                // 没有技能名，跳过单个字符
                if (index < skillsPart.Length)
                    index++;
                continue;
            }

            // 检查技能名是否有效（不含数字）
            if (Regex.IsMatch(skillName, @"\d"))
            {
                // 技能名中包含数字，跳过
                continue;
            }

            // 检查技能名后的字符，决定是否开启掷骰模式
            bool enableDiceMode = false;
            bool isNegative = false;

            if (index < skillsPart.Length && (skillsPart[index] == '+' || skillsPart[index] == '-'))
            {
                enableDiceMode = true;
                if (skillsPart[index] == '-')
                    isNegative = true;
                index++;
            }
            else if (index < skillsPart.Length && char.IsDigit(skillsPart[index]))
            {
                // 直接跟数字，不开启掷骰模式
                enableDiceMode = false;
            }
            else
            {
                // 没有数值，跳过这个技能
                continue;
            }

            // 匹配数值
            int valueStart = index;
            if (enableDiceMode)
            {
                // 掷骰模式：匹配 [0-9+\-dD]
                while (index < skillsPart.Length &&
                       (char.IsDigit(skillsPart[index]) ||
                        skillsPart[index] == '+' ||
                        skillsPart[index] == '-' ||
                        skillsPart[index] == 'd' ||
                        skillsPart[index] == 'D'))
                {
                    index++;
                }
            }
            else
            {
                // 纯数字模式：只匹配 [0-9]
                while (index < skillsPart.Length && char.IsDigit(skillsPart[index]))
                {
                    index++;
                }
            }

            string skillValueStr = skillsPart.Substring(valueStart, index - valueStart).Trim();
            if (string.IsNullOrEmpty(skillValueStr))
                continue;

            // 处理技能值
            int finalSkillValue = 0;

            if (enableDiceMode)
            {
                // 获取当前值（相对调整）
                characterSkillsDict.TryGetValue(skillName, out int currentSkillValue);
                finalSkillValue = currentSkillValue;

                // 评估表达式（如 d20, 20-5, d20+2 等）
                var (success, total) = EvaluateSkillExpression(skillValueStr, msg);
                if (success)
                {
                    finalSkillValue += isNegative ? -total : total;
                }
                else
                {
                    // 表达式评估失败
                    Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["RollError"], skillValueStr), msg);
                    continue;
                }
            }
            else
            {
                // 纯数字直接赋值
                if (int.TryParse(skillValueStr, out int parsedValue))
                {
                    finalSkillValue = parsedValue;
                }
                else
                {
                    Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["SkillValueFormatError"], skillName, skillValueStr), msg);
                    continue;
                }
            }

            // 技能值范围限制
            if (finalSkillValue < 0 || finalSkillValue > 9999)
            {
                Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["SkillValueOutOfRange"], skillName, finalSkillValue.ToString()), msg);
                finalSkillValue = Math.Clamp(finalSkillValue, 0, 9999);
            }

            characterSkillsDict.AddOrUpdate(skillName, finalSkillValue, (key, oldValue) => finalSkillValue);
            updatedSkills.Add($"{skillName}:{finalSkillValue}");
        }

        if (updatedSkills.Count > 0)
        {
            string replyMessage = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["SkillInsertSuccess"], characterName, string.Join(", ", updatedSkills));
            Reply(replyMessage, msg);
            Log.InfoFormat($"用户 {userId} 的人物卡 {characterName} 技能更新: {string.Join(", ", updatedSkills)}");
        }
        else
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["SkillInsertNoValidSkills"], msg);
        }

        // 保存数据
        SaveCharacterSkills();
    }

    /// <summary>
    /// 评估技能表达式（支持纯数字、掷骰、以及包含 +/- 的组合表达式）
    /// 例如：20, d20, 2d8, 20-5, d20+2, 3d6-2+d4 等
    /// 返回 (成功, 总值)
    /// </summary>
    private (bool success, int total) EvaluateSkillExpression(string expr, Msg msg)
    {
        try
        {
            expr = expr.Trim();
            if (string.IsNullOrEmpty(expr))
                return (false, 0);

            int result = 0;
            int i = 0;

            while (i < expr.Length)
            {
                // 尝试匹配掷骰表达式 \d*d\d*
                var diceMatch = Regex.Match(expr.Substring(i), @"^(\d*d\d*)", RegexOptions.IgnoreCase);
                if (diceMatch.Success)
                {
                    string diceExpr = diceMatch.Groups[1].Value.ToLower();
                    var rollResult = Dice.Roll(diceExpr);
                    if (rollResult.Success)
                    {
                        result += rollResult.Total;
                        i += diceExpr.Length;
                    }
                    else
                    {
                        return (false, 0);
                    }
                }
                // 尝试匹配开头的纯数字
                else if (i == 0 && Regex.IsMatch(expr.Substring(i), @"^\d+"))
                {
                    var numMatch = Regex.Match(expr.Substring(i), @"^(\d+)");
                    if (numMatch.Success)
                    {
                        result = int.Parse(numMatch.Groups[1].Value);
                        i += numMatch.Length;
                    }
                }
                // 尝试匹配 +数字 或 -数字
                else if (Regex.IsMatch(expr.Substring(i), @"^[+\-]\d+"))
                {
                    var numMatch = Regex.Match(expr.Substring(i), @"^([+\-])(\d+)");
                    if (numMatch.Success)
                    {
                        int num = int.Parse(numMatch.Groups[2].Value);
                        if (numMatch.Groups[1].Value == "+")
                            result += num;
                        else
                            result -= num;
                        i += numMatch.Length;
                    }
                }
                // 尝试匹配 +掷骰 或 -掷骰
                else if (Regex.IsMatch(expr.Substring(i), @"^[+\-]"))
                {
                    char op = expr[i];
                    i++;
                    var diceMatch2 = Regex.Match(expr.Substring(i), @"^(\d*d\d*)", RegexOptions.IgnoreCase);
                    if (diceMatch2.Success)
                    {
                        string diceExpr = diceMatch2.Groups[1].Value.ToLower();
                        var rollResult = Dice.Roll(diceExpr);
                        if (rollResult.Success)
                        {
                            if (op == '+')
                                result += rollResult.Total;
                            else
                                result -= rollResult.Total;
                            i += diceExpr.Length;
                        }
                        else
                        {
                            return (false, 0);
                        }
                    }
                    else
                    {
                        // +/- 后没有数字或掷骰，错误
                        return (false, 0);
                    }
                }
                else
                {
                    // 无效字符或格式
                    return (false, 0);
                }
            }

            return (true, result);
        }
        catch
        {
            return (false, 0);
        }
    }

    /// <summary>
    /// 处理自定义检查指令
    /// </summary>
    private void HandleCostomCheck(string args, Msg msg)
    {
        string rawContent = msg.Content.Trim();
        long userId = msg.UserId;

        // 1. 解析指令模式和人物卡
        // 格式:
        //   - 普通检定: .cc{模式}(人物卡) 技能 [_副指令]
        //   - 对抗检定: .cc{模式}(人物卡) 技能1 [_副指令] @目标 [技能2]
        // 模式和人物卡可忽略
        // 副指令支持连锁匹配，如 _l_h_d 等
        // @目标支持: @[CQ:at,qq=123456] 或 @123456
        // 对抗模式下，主干部分1用于调用者，主干部分2用于被@目标（模拟对方执行的指令）
        string pattern = @"^\.cc(?:\{(?<mode>[^}]+)\})?(?:\((?<character>[^)]+)\))?\s*(?<mainPart1>(?:(?!\s_[A-Za-z]).)*?)(?<subCommands>(?:\s*_[A-Za-z]+)*)?(?:\s*@\s*(?<atTarget>\S+)(?:\s+(?<mainPart2>.*))?)?$";
        Match match = Regex.Match(rawContent, pattern, RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["CoCFormatError"], msg);
            Log.InfoFormat($"CoC 指令格式错误: {rawContent}");
            return;
        }

        string rawMode = match.Groups["mode"].Success ? match.Groups["mode"].Value : string.Empty;
        string mode = string.IsNullOrWhiteSpace(rawMode)
            ? (GetDefaultCheckMode(userId) ?? "coc7")
            : rawMode.ToLowerInvariant();
        string characterName = match.Groups["character"].Success ? match.Groups["character"].Value.Trim() : "";
        string mainPart = match.Groups["mainPart1"].Value.Trim();
        string mainPart2 = match.Groups["mainPart2"].Success ? match.Groups["mainPart2"].Value.Trim() : "";
        string atTargetRaw = match.Groups["atTarget"].Success ? match.Groups["atTarget"].Value.Trim() : string.Empty;
        
        var subCommands = new List<string>();
        if (match.Groups["subCommands"].Success)
        {
            foreach (Match m in Regex.Matches(match.Groups["subCommands"].Value, "_[A-Za-z]+", RegexOptions.IgnoreCase))
            {
                subCommands.Add(m.Value.ToLowerInvariant());
            }
        }
        
        // 解析@目标用户ID
        long? atTargetUserId = null;
        if (!string.IsNullOrEmpty(atTargetRaw))
        {
            // 支持 [CQ:at,qq=123456] 格式
            var cqMatch = Regex.Match(atTargetRaw, @"\[CQ:at[^\]]*qq=(\d+)\]", RegexOptions.IgnoreCase);
            if (cqMatch.Success && long.TryParse(cqMatch.Groups[1].Value, out long parsedId))
            {
                atTargetUserId = parsedId;
            }
            // 支持纯数字格式 @123456
            else if (long.TryParse(atTargetRaw, out long pureId))
            {
                atTargetUserId = pureId;
            }
        }
        
        bool isVersusMode = subCommands.Contains("_v");
        bool isLoopMode = subCommands.Contains("_l");
        
        // _v 和 _l 天然互斥，循环检定之间无法直接对比
        if (isVersusMode && isLoopMode)
        {
            Reply("⚠️ 对抗检定(_v)与循环检定(_l)不能同时使用。\n循环多次检定之间无法直接进行对比，请只使用其中一个。", msg);
            return;
        }
        if (!IsSupportedCheckMode(mode))
        {
            Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["UnsupportedCheckMode"], mode), msg);
            return;
        }

        if (!string.IsNullOrWhiteSpace(rawMode) && TrySetDefaultCheckMode(userId, mode))
        {
            SaveUserData(userId);
        }

        string newCharacterName = GetOrCreateCharacterName(userId, characterName, msg.IsSimulationMode, msg);
        if (newCharacterName == null)
        {
            return;
        }
        characterName = newCharacterName;

        // 确保 userCharacters 存在，因为 GetOrCreateCharacterName 已经处理了创建逻辑
        if (!characterSkills.TryGetValue(userId, out var userCharacters))
        {
            Log.Error($"在 GetOrCreateCharacterName 之后未能获取 userCharacters。");
            Reply(GlobalFeedbackMessages.FeedbackTemplates["InternalError"], msg);
            return;
        }

        if (!userCharacters.TryGetValue(characterName, out var sheet))
        {
            Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["CharacterNotFound"], characterName), msg);
            return;
        }

        bool isHiddenMode = subCommands.Contains("_h");
        List<string> lastSubCmds = new();
        string lastSkillName = "";
        int lastSkillValue = 0;

        List<string> results = new List<string>();
        List<string> exMessages = new List<string>(); // 存储个性化文本

        Log.InfoFormat("get in now6");
        // 主干部分解析和掷骰
        // 使用简单的元素匹配避免复杂的嵌套正则表达式
        Log.InfoFormat("Main part to parse: '{mainPart}' (length: {mainPart.Length})");

        var parsedParts = ParseMainPartSimple(mainPart, mode);
        if (parsedParts.Count == 0)
        {
            Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["MainPartFormatError"], mode.ToUpper()), msg);
            return;
        }

        Log.InfoFormat("Parsed {parsedParts.Count} main parts");

        foreach (var parsedPart in parsedParts)
        {
            // 解构元组
            var (fullText, subCmds, skill, value) = parsedPart;

            var effectiveSubCmds = (subCmds != null && subCmds.Count > 0) ? subCmds : lastSubCmds;

            string result = "";
            string exmessage = ""; // 获取个性化文本
            // 每次解析使用当前人物卡对应的技能集
            if (!userCharacters.TryGetValue(characterName, out var sheetForCheck))
            {
                Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["CharacterNotFound"], characterName), msg);
                return;
            }
            var characterSkillsDict = sheetForCheck.Skills;

            if (mode == "coc7")
            {
                var (detail, exMsg) = ProcessCoC7MainPartSimple(fullText, effectiveSubCmds, skill, value, characterSkillsDict, ref lastSubCmds, ref lastSkillName, ref lastSkillValue);
                result = detail;
                exmessage = exMsg;
            }
            else if (mode == "et")
            {
                var (detail, exMsg) = ProcessETMainPartSimple(fullText, effectiveSubCmds, skill, value, characterSkillsDict, ref lastSubCmds, ref lastSkillName, ref lastSkillValue);
                result = detail;
                exmessage = exMsg;
            }
            results.Add(result);
            exMessages.Add(exmessage); // 保存个性化文本

            lastSubCmds = new List<string>(effectiveSubCmds);

            if (!isLoopMode) break; // 如果不是循环模式，只处理第一个主干部分
        }

        string finalReply = string.Empty;
        
        // 处理对抗检定（_v 模式）
        if (isVersusMode && atTargetUserId.HasValue)
        {
            string versusResult = ProcessVersusCheck(userId, characterName, atTargetUserId.Value, mainPart, mode, userCharacters, msg);
            finalReply += "\n" + versusResult;
        }
        else if (isVersusMode && !atTargetUserId.HasValue && !string.IsNullOrEmpty(atTargetRaw))
        {
            finalReply += $"\n⚠️ 无法解析对抗目标: @{atTargetRaw}，请使用 @[CQ:at,qq=123456] 或 @123456 格式";
        }
        
        if (results.Count > 0)
        {
            string combinedReply = string.Join("\n", results);
            
            // 仅在非循环模式下添加个性化文本
            if (!isLoopMode && exMessages.Count > 0 && !string.IsNullOrEmpty(exMessages[0]))
            {
                combinedReply += "\n" + exMessages[0];
            }
            
            finalReply = combinedReply + finalReply;
            
            if (isHiddenMode)
            {
                string publicText = GlobalFeedbackMessages.FeedbackTemplates.TryGetValue("HiddenRollPublic", out var pub) ? pub : "已执行暗骰，结果已私发。";
                string privateText = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["HiddenRollPrivatePrefix"], finalReply);

                // 模板为空时兜底，避免发送空白消息
                if (string.IsNullOrWhiteSpace(privateText))
                {
                    privateText = $"[暗骰结果]\n{finalReply}";
                }

                // 仅群聊发送公共提示，私聊不需要"已私发"提示
                if (msg.Source == MessageSource.group)
                {
                    Reply(publicText, msg);
                }

                // 私聊结果
                if (msg.IsSimulationMode)
                {
                    Reply(privateText, msg);
                }
                else if (MessageDistribution != null)
                {
                    if (MessageDistribution.WSconnection.IsWsConnected)
                    {
                        MessageDistribution.WSconnection.SendPrivateMessage(msg.UserId, privateText);
                    }
                    else
                    {
                        Log.Error("未知错误，WebSocket 未连接，无法发送私聊消息。");
                    }
                }
            }
            else
            {
                Reply(finalReply, msg);
            }

            Log.InfoFormat($"用户 {userId} CoC 检定结果: {finalReply}");

        }
    }
    
    /// <summary>
    /// 处理对抗检定（_v 副指令）
    /// 对调用者和被@者使用同一技能值进行检定对比
    /// </summary>
    private string ProcessVersusCheck(long callerUserId, string callerCharacterName, long targetUserId, string mainPart, string mode, 
        ConcurrentDictionary<string, CharacterSheet> callerCharacters, Msg msg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("━━━━━━【对抗检定】━━━━━━");
        
        // 获取被对抗目标的名称
        string targetName = GetReasonableSenderName(targetUserId, msg.IsSimulationMode);
        sb.AppendLine($"【{targetName}】");
        
        // 获取被对抗目标的角色卡（如果有）
        if (!characterSkills.TryGetValue(targetUserId, out var targetCharacters))
        {
            sb.AppendLine($"⚠️ {targetName} 没有角色卡，无法进行对抗检定");
            sb.AppendLine("━━━━━━【对抗结束】━━━━━━");
            return sb.ToString();
        }
        
        // 获取被对抗目标的当前使用角色卡
        string? targetCharacterName = null;
        if (CurrentCharacterNames.TryGetValue(targetUserId, out var currentChar))
        {
            targetCharacterName = currentChar;
        }
        
        if (string.IsNullOrEmpty(targetCharacterName) && targetCharacters.Count > 0)
        {
            targetCharacterName = targetCharacters.Keys.First();
        }
        
        if (string.IsNullOrEmpty(targetCharacterName) || !targetCharacters.TryGetValue(targetCharacterName, out var targetSheet))
        {
            sb.AppendLine($"⚠️ {targetName} 没有正在使用的角色卡，无法进行对抗检定");
            sb.AppendLine("━━━━━━【对抗结束】━━━━━━");
            return sb.ToString();
        }
        
        // 获取被对抗目标的技能字典
        var targetSkillsDict = targetSheet.Skills;
        
        // 解析主干部分获取技能信息（复用解析逻辑）
        var parsedParts = ParseMainPartSimple(mainPart, mode);
        if (parsedParts.Count == 0)
        {
            sb.AppendLine($"⚠️ 无法解析技能信息，无法进行对抗检定");
            sb.AppendLine("━━━━━━【对抗结束】━━━━━━");
            return sb.ToString();
        }
        
        // 取第一个解析结果
        var (fullText, subCmds, skill, value) = parsedParts[0];
        
        // 获取调用者的技能字典
        if (!callerCharacters.TryGetValue(callerCharacterName, out var callerSheet))
        {
            sb.AppendLine($"⚠️ 调用者角色卡不存在");
            sb.AppendLine("━━━━━━【对抗结束】━━━━━━");
            return sb.ToString();
        }
        var callerSkillsDict = callerSheet.Skills;
        
        // 获取技能值
        int callerSkillValue = GetSkillValueFromParsed(skill, value, callerSkillsDict);
        int targetSkillValue = GetSkillValueFromParsed(skill, value, targetSkillsDict);
        
        if (callerSkillValue < 0 || targetSkillValue < 0)
        {
            sb.AppendLine($"⚠️ 技能值获取失败，无法进行对抗检定");
            sb.AppendLine("━━━━━━【对抗结束】━━━━━━");
            return sb.ToString();
        }
        
        // 记录使用的技能名
        string skillName = ExtractSkillName(skill);
        
        // 进行检定
        List<string> lastSubCmds = new();
        string lastSkillName = "";
        int lastSkillValue = 0;
        
        string callerResult, targetResult;
        if (mode == "coc7")
        {
            var (callerDetail, _) = ProcessCoC7MainPartSimple(fullText, subCmds, skill, value, callerSkillsDict, ref lastSubCmds, ref lastSkillName, ref lastSkillValue);
            callerResult = callerDetail;
            
            lastSubCmds = new();
            lastSkillName = "";
            lastSkillValue = 0;
            
            var (targetDetail, _) = ProcessCoC7MainPartSimple(fullText, subCmds, skill, value, targetSkillsDict, ref lastSubCmds, ref lastSkillName, ref lastSkillValue);
            targetResult = targetDetail;
        }
        else if (mode == "et")
        {
            var (callerDetail, _) = ProcessETMainPartSimple(fullText, subCmds, skill, value, callerSkillsDict, ref lastSubCmds, ref lastSkillName, ref lastSkillValue);
            callerResult = callerDetail;
            
            lastSubCmds = new();
            lastSkillName = "";
            lastSkillValue = 0;
            
            var (targetDetail, _) = ProcessETMainPartSimple(fullText, subCmds, skill, value, targetSkillsDict, ref lastSubCmds, ref lastSkillName, ref lastSkillValue);
            targetResult = targetDetail;
        }
        else
        {
            sb.AppendLine($"⚠️ 当前模式 '{mode}' 不支持对抗检定");
            sb.AppendLine("━━━━━━【对抗结束】━━━━━━");
            return sb.ToString();
        }
        
        // 提取检定结果判定
        string callerJudgment = ExtractCheckResult(callerResult);
        string targetJudgment = ExtractCheckResult(targetResult);
        
        // 输出结果
        sb.AppendLine($"使用技能: {skillName}");
        sb.AppendLine($"【{callerCharacterName}】(技能值:{callerSkillValue})");
        sb.AppendLine(callerResult);
        sb.AppendLine();
        sb.AppendLine($"【{targetCharacterName}】(技能值:{targetSkillValue})");
        sb.AppendLine(targetResult);
        sb.AppendLine();
        
        // 判定胜负
        sb.AppendLine("━━━━━━【对抗结果】━━━━━━");
        
        if (mode == "et")
        {
            // ET模式：检定数值 → 技能值 → 出目（出目高者胜利）
            int callerCheckValue = ExtractETCheckValue(callerResult);
            int targetCheckValue = ExtractETCheckValue(targetResult);
            
            if (callerCheckValue > targetCheckValue)
            {
                sb.AppendLine($"🎯 {callerCharacterName} 胜利！(检定数值胜)");
            }
            else if (callerCheckValue < targetCheckValue)
            {
                sb.AppendLine($"🎯 {targetCharacterName} 胜利！(检定数值胜)");
            }
            else if (callerSkillValue > targetSkillValue)
            {
                sb.AppendLine($"🎯 {callerCharacterName} 胜利！(技能值胜)");
            }
            else if (callerSkillValue < targetSkillValue)
            {
                sb.AppendLine($"🎯 {targetCharacterName} 胜利！(技能值胜)");
            }
            else
            {
                int callerRoll = ExtractETRollValue(callerResult);
                int targetRoll = ExtractETRollValue(targetResult);
                if (callerRoll > targetRoll)
                    sb.AppendLine($"🎯 {callerCharacterName} 胜利！(出目胜)");
                else if (callerRoll < targetRoll)
                    sb.AppendLine($"🎯 {targetCharacterName} 胜利！(出目胜)");
                else
                    sb.AppendLine($"⚖️ 平局！");
            }
        }
        else
        {
            int callerLevel = GetResultLevel(callerJudgment);
            int targetLevel = GetResultLevel(targetJudgment);
            
            if (callerLevel > targetLevel)
                sb.AppendLine($"🎯 {callerCharacterName} 胜利！");
            else if (callerLevel < targetLevel)
                sb.AppendLine($"🎯 {targetCharacterName} 胜利！");
            else
                sb.AppendLine($"⚖️ 平局！");
        }
        sb.AppendLine("━━━━━━【对抗结束】━━━━━━");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 从解析的技能和值中获取技能值
    /// </summary>
    private int GetSkillValueFromParsed(string skill, string value, ConcurrentDictionary<string, int> skillsDict)
    {
        if (!string.IsNullOrEmpty(skill))
        {
            var skillMatch = Regex.Match(skill, @"^([A-Za-z_\u4e00-\u9fa5]+)([-+]?(?:\d+|d\d+))?$");
            if (skillMatch.Success && !string.IsNullOrEmpty(skillMatch.Groups[1].Value))
            {
                string skillName = skillMatch.Groups[1].Value;
                if (skillsDict.TryGetValue(skillName, out int storedValue))
                {
                    // 应用修饰符
                    string modifierStr = skillMatch.Groups[2].Value;
                    if (!string.IsNullOrEmpty(modifierStr))
                    {
                        if (modifierStr.StartsWith("+") && int.TryParse(modifierStr.Substring(1), out int plusVal))
                            return storedValue + plusVal;
                        if (modifierStr.StartsWith("-") && int.TryParse(modifierStr.Substring(1), out int minusVal))
                            return storedValue - minusVal;
                        if (int.TryParse(modifierStr, out int pureVal))
                            return pureVal;
                    }
                    return storedValue;
                }
            }
        }
        
        if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int pureValue))
        {
            return pureValue;
        }
        
        return -1;
    }
    
    /// <summary>
    /// 从技能字符串中提取技能名
    /// </summary>
    private string ExtractSkillName(string skill)
    {
        if (string.IsNullOrEmpty(skill))
            return "直接";
            
        var match = Regex.Match(skill, @"^([A-Za-z_\u4e00-\u9fa5]+)");
        if (match.Success)
            return match.Groups[1].Value;
            
        return skill;
    }
    
    /// <summary>
    /// 从检定结果字符串中提取判定结果
    /// </summary>
    private string ExtractCheckResult(string result)
    {
        // 尝试匹配"极限成功"、"困难成功"、"成功"、"失败"、"大成功"、"大失败"
        if (result.Contains("极限成功")) return "极限成功";
        if (result.Contains("困难成功")) return "困难成功";
        if (result.Contains("大成功")) return "大成功";
        if (result.Contains("成功") && !result.Contains("大成功") && !result.Contains("困难成功")) return "成功";
        if (result.Contains("大失败")) return "大失败";
        if (result.Contains("失败")) return "失败";
        return "未知";
    }
    
    /// <summary>
    /// 根据检定结果获取等级（用于比较）
    /// </summary>
    private int GetResultLevel(string result)
    {
        return result switch
        {
            "大成功" => 5,
            "极限成功" => 4,
            "困难成功" => 3,
            "成功" => 2,
            "失败" => 1,
            "大失败" => 0,
            _ => -1
        };
    }
    
    /// <summary>
    /// 从ET检定结果提取检定数值
    /// </summary>
    private int ExtractETCheckValue(string result)
    {
        // ET检定结果格式: "力量检定:1d5->2 检定数值:17(成功+10)"
        // 尝试提取"检定数值:"后的数值
        var match = Regex.Match(result, @"检定数值[:：](\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int checkValue))
        {
            return checkValue;
        }
        return 0;
    }
    
    /// <summary>
    /// 从ET检定结果提取出目数值
    /// </summary>
    private int ExtractETRollValue(string result)
    {
        // ET检定结果格式示例: "力量检定:1d5->2" 或 "力量检定:d5->3"
        // 尝试提取 -> 后的数值
        var match = Regex.Match(result, @"->(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int rollValue))
        {
            return rollValue;
        }
        return 0;
    }
    
    /// <summary>
    /// 处理理智检定指令
    /// </summary>
    private void HandleSanityCheck(string args, Msg msg)
    {
        string rawContent = msg.Content.Trim();
        long userId = msg.UserId;

        // 正则表达式匹配指令格式
        // .sc{人物名} 掷骰表达式1 / 掷骰表达式2 [临时理智值/直接理智值]
        // 人物名可选，数值参数可选（可以是临时理智值或直接理智值）
        // 当提供数值参数时，该参数既可作为临时理智（若需存储）或直接理智值（若不存储）
        string pattern = @"^\.sc(?:\(([^)]+)\))?\s*([^/\s]+)\s*/\s*([^/\s]+)(?:\s+(\d+))?$";
        Match match = Regex.Match(rawContent, pattern, RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            Reply("理智检定指令格式错误。请使用 .sc(人物名) 掷骰表达式1 / 掷骰表达式2 [数值]", msg);
            Log.InfoFormat($"理智检定指令格式错误: {rawContent}");
            return;
        }

        string characterNameFromCommand = match.Groups[1].Success ? match.Groups[1].Value.Trim() : "";
        string diceExpr1 = match.Groups[2].Value.Trim();
        string diceExpr2 = match.Groups[3].Value.Trim();
        string optionalValueStr = match.Groups[4].Value.Trim();
        int? optionalValue = string.IsNullOrEmpty(optionalValueStr) ? null : int.Parse(optionalValueStr);

        // 判断是否为"直接模式"（提供了数值参数时）
        bool isDirectMode = optionalValue.HasValue;

        string? characterName = null;
        CharacterSheet? sanitySheet = null;
        ConcurrentDictionary<string, int>? characterSkillsDict = null;

        // 如果不是直接模式，需要获取人物卡信息
        if (!isDirectMode)
        {
            characterName = GetOrCreateCharacterName(userId, characterNameFromCommand, msg.IsSimulationMode, msg);
            if (characterName == null)
            {
                return;
            }

            // 确保 userCharacters 存在
            if (!characterSkills.TryGetValue(userId, out var sanityUserCharacters))
            {
                Log.Error($"未能获取用户人物卡集合。");
                Reply("内部错误：未能获取用户人物卡集合。", msg);
                return;
            }

            if (!sanityUserCharacters.TryGetValue(characterName!, out sanitySheet))
            {
                Reply($"人物卡 '{characterName}' 不存在或没有技能。", msg);
                return;
            }

            characterSkillsDict = sanitySheet.Skills;

            // 获取理智技能值
            int sanityValue = 0;
            bool hasSanity = characterSkillsDict.TryGetValue("理智", out sanityValue);
            if (!hasSanity)
            {
                // 尝试获取意志技能
                if (characterSkillsDict.TryGetValue("意志", out sanityValue))
                {
                    // 将意志技能转录到理智技能
                    characterSkillsDict.AddOrUpdate("理智", sanityValue, (key, oldValue) => sanityValue);
                    Reply($"未找到理智技能，已将意志技能值 {sanityValue} 转录为理智技能。", msg);
                }
                else
                {
                    Reply("未找到理智技能或意志技能，无法进行理智检定。", msg);
                    return;
                }
            }

            // 设置当前理智值为存储的理智值
            optionalValue = sanityValue;
        }

        // 掷百面骰进行理智检定
        var sanityCheckRoll = Dice.Roll("1d100");
        if (!sanityCheckRoll.Success)
        {
            Reply($"理智检定掷骰失败: {sanityCheckRoll.Detail}", msg);
            return;
        }

        bool isSuccess = sanityCheckRoll.Total <= optionalValue!.Value;

        string resultDiceExpr = isSuccess ? diceExpr1 : diceExpr2;

        // 掷对应表达式
        var resultRoll = Dice.CalculateExpression(resultDiceExpr);
        if (!resultRoll.Success)
        {
            Reply($"理智检定结果掷骰失败: {resultRoll.Detail}", msg);
            return;
        }

        // 计算扣除后的理智值
        int damage = resultRoll.Total;
        int newSanityValue = optionalValue.Value - damage;

        // 仅在非直接模式时更新并保存人物卡
        if (!isDirectMode && sanitySheet != null && characterSkillsDict != null)
        {
            // 更新理智技能值
            characterSkillsDict.AddOrUpdate("理智", newSanityValue, (key, oldValue) => newSanityValue);

            // 保存数据
            SaveCharacterSkills();

            // 构建回复消息（带有更新提示）
            string sanityCheckResult = isSuccess ? "成功" : "失败";
            string replyMessage = $"理智检定: 1d100 = {sanityCheckRoll.Total} ≤ {optionalValue.Value} → {sanityCheckResult}\n";
            replyMessage += $"掷骰结果: {resultRoll.Detail}\n";
            replyMessage += $"理智损失: {damage}\n";
            replyMessage += $"当前理智: {newSanityValue}";

            Reply(replyMessage, msg);
            Log.InfoFormat($"用户 {userId} 的人物卡 {characterName} 理智检定: {sanityCheckResult}, 损失 {damage}, 当前理智 {newSanityValue}");
        }
        else
        {
            // 直接模式：只返回扣除结果，不存储
            string sanityCheckResult = isSuccess ? "成功" : "失败";
            string replyMessage = $"理智检定: 1d100 = {sanityCheckRoll.Total} ≤ {optionalValue.Value} → {sanityCheckResult}\n";
            replyMessage += $"掷骰结果: {resultRoll.Detail}\n";
            replyMessage += $"扣除后理智: {newSanityValue}";

            Reply(replyMessage, msg);
            Log.InfoFormat($"用户 {userId} 执行直接理智检定: {sanityCheckResult}, 损失 {damage}, 扣除后理智 {newSanityValue}");
        }
    }

    /// <summary>
    /// 处理帮助指令
    /// </summary>
    private void HandleHelp(string args, Msg msg)
    {
        string trimmedArgs = args.Trim();

        List<string> numberedKeys = new List<string>();
        int page = 1;
        var keys = GlobalFeedbackMessages.HelpTemplates.Keys.OrderBy(k => k).ToList();
        int totalPages = (int)Math.Ceiling((double)keys.Count / 20.0);
        if (trimmedArgs.StartsWith("list", StringComparison.OrdinalIgnoreCase))
        {
            // 处理list分页
            string listPart = trimmedArgs.Substring(4).Trim();
            if (!string.IsNullOrEmpty(listPart) && int.TryParse(listPart, out int parsedPage) && parsedPage > 0)
            {
                page = parsedPage;
            }
            // 获取键列表并排序
            if (page > totalPages) page = totalPages;
        }
        else
        {
            // 尝试作为关键词匹配
            if (!string.IsNullOrEmpty(trimmedArgs) && GlobalFeedbackMessages.HelpTemplates.TryGetValue(trimmedArgs, out string helpContent))
            {
                Reply(helpContent, msg);
                return; // 修复：成功匹配关键词后直接返回，不再发送完整列表
            }
        }
        // 返回list的第一页
        int startIndex = (page - 1) * 20;
        var pageKeys = keys.Skip(startIndex).Take(20).ToList();
        // 添加序号，若帮助内容以【...】开头则附加说明
        numberedKeys = pageKeys
        .Select((key, index) =>
        {
            string label = $"{startIndex + index + 1}. {key}";
            if (GlobalFeedbackMessages.HelpTemplates.TryGetValue(key, out string content))
            {
                var bracketMatch = System.Text.RegularExpressions.Regex.Match(content, @"^【(.+?)】");
                if (bracketMatch.Success)
                {
                    label += $" - {bracketMatch.Groups[1].Value}";
                }
            }
            return label;
        })
        .ToList();
        string pageText = GlobalFeedbackMessages.FeedbackTemplates["HelpDefaultMessage"] + "\n" + string.Join("\n", numberedKeys) + $"\n——————第[{page}/{totalPages}]页——————";
        Reply(pageText, msg);
    }

    /// <summary>
    /// 处理 name 指令：
    /// .name xxx    设置当前用户持久化名称（与账号绑定）
    /// .name        查询当前设置名称
    /// 名称存储在 UserData（DisplayName 字段）中。
    /// </summary>
    private void HandleNameCommand(string args, Msg msg)
    {
        string trimmedArgs = (args ?? string.Empty).Trim();

        try
        {
            // 不带参数：查询当前名称
            if (string.IsNullOrEmpty(trimmedArgs))
            {
                if (userDisplayNames.TryGetValue(msg.UserId, out var existing) && !string.IsNullOrWhiteSpace(existing))
                {
                    Reply($"当前名称：{existing}", msg);
                }
                else
                {
                    Reply("尚未为你设置名称。使用 .name 名称 来设置。", msg);
                }
                return;
            }

            // 允许使用 reset / clear / off 清除设置
            if (trimmedArgs.Equals("reset", StringComparison.OrdinalIgnoreCase)
                || trimmedArgs.Equals("clear", StringComparison.OrdinalIgnoreCase)
                || trimmedArgs.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                userDisplayNames.TryRemove(msg.UserId, out _);
                SaveUserData(msg.UserId);
                Reply("已清除已设置的名称。", msg);
                return;
            }

            // 其余情况视为设置名称
            string newName = trimmedArgs;

            // 基础校验：长度与简单非法字符过滤，可按需调整
            if (newName.Length > 32)
            {
                Reply("名称过长，请限制在 32 个字符以内。", msg);
                return;
            }

            // 保存到缓存并持久化到 UserData
            userDisplayNames[msg.UserId] = newName;
            SaveUserData(msg.UserId);
            Reply($"已将你的名称设置为：{newName}", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 处理 name 指令时发生错误: {ex.Message}");
            Reply("设置名称时发生内部错误。", msg);
        }
    }

    /// <summary>
    /// 处理 com 指令（占位，无子指令，仅解析模式关键字）：
    /// 语法示例：
    /// .com coc
    /// .com et
    /// .com dnd
    /// 当前仅用于识别与校验模式，不执行具体逻辑，便于后续扩展。
    /// </summary>
    private void HandleComCommand(string args, Msg msg)
    {
        string argsTrimmed = (args ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(argsTrimmed))
        {
            Reply("使用帮助：\n.com list - 列出你拥有的所有人物卡\n.com set [序号或卡名] - 切换正在使用的人物卡\n.com del [序号或卡名] - 删除人物卡（需要确认）\n.com [序号或卡名] - 显示人物卡详情", msg);
            return;
        }

        // 解析第一个子命令（list / set / del）或卡片标识
        string[] parts = Regex.Split(argsTrimmed, @"\s+");
        string command = parts[0].ToLowerInvariant();

        // 获取用户的所有人物卡
        if (!characterSkills.TryGetValue(msg.UserId, out var userCharacters) || userCharacters.Count == 0)
        {
            Reply("你还没有任何人物卡，请先使用 .st 指令创建人物卡。", msg);
            return;
        }

        // 命令：.com list - 列出所有人物卡
        if (command == "list")
        {
            var cardList = new List<string>();
            int index = 1;
            foreach (var name in userCharacters.Keys.OrderBy(k => k))
            {
                cardList.Add($"{index}. {name}");
                index++;
            }
            Reply(string.Join("\n", cardList), msg);
            return;
        }

        // 命令：.com set [序号或卡名] - 切换正在使用的人物卡
        if (command == "set")
        {
            if (parts.Length < 2)
            {
                Reply("用法: .com set [序号或卡名]", msg);
                return;
            }

            string cardIdentifier = string.Join(" ", parts.Skip(1)).Trim();
            string? targetCardName = ResolveCardIdentifier(cardIdentifier, userCharacters);

            if (string.IsNullOrEmpty(targetCardName))
            {
                Reply($"找不到人物卡: {cardIdentifier}", msg);
                return;
            }

            // 更新当前使用的人物卡
            CurrentCharacterNames.AddOrUpdate(msg.UserId, targetCardName, (k, v) => targetCardName);
            Reply($"已切换至人物卡: {targetCardName}", msg);
            return;
        }

        // 命令：.com del [序号或卡名] - 删除人物卡（需要确认）
        if (command == "del")
        {
            if (parts.Length < 2)
            {
                Reply("用法: .com del [序号或卡名]", msg);
                return;
            }

            string cardIdentifier = string.Join(" ", parts.Skip(1)).Trim();
            string? targetCardName = ResolveCardIdentifier(cardIdentifier, userCharacters);

            if (string.IsNullOrEmpty(targetCardName))
            {
                Reply($"找不到人物卡: {cardIdentifier}", msg);
                return;
            }

            // 设置用户焦点状态，等待确认
            MessageDistribution?.SetUserFocus(msg.UserId.ToString(), $"com_del_confirm:{targetCardName}");
            Reply($"即将删除人物卡: {targetCardName}，请输入 'y' 确认或输入其他内容取消。", msg);
            return;
        }

        // 命令：.com [序号或卡名] - 显示人物卡详情
        string? cardName = ResolveCardIdentifier(command, userCharacters);
        if (string.IsNullOrEmpty(cardName))
        {
            // 如果第一个参数不是有效的卡片标识，尝试整个参数字符串
            cardName = ResolveCardIdentifier(argsTrimmed, userCharacters);
        }

        if (string.IsNullOrEmpty(cardName))
        {
            Reply($"找不到人物卡: {argsTrimmed}，使用 .com list 查看所有人物卡。", msg);
            return;
        }

        if (!userCharacters.TryGetValue(cardName, out var sheet))
        {
            Reply($"找不到人物卡 '{cardName}'。", msg);
            return;
        }

        Reply(sheet.CharacterDetails(), msg);
    }

    /// <summary>
    /// 根据序号或名称解析人物卡
    /// 优先尝试解析为序号，如果失败则作为名称查询
    /// </summary>
    private string? ResolveCardIdentifier(string identifier, ConcurrentDictionary<string, CharacterSheet> userCharacters)
    {
        // 尝试解析为数字序号
        if (int.TryParse(identifier, out int index) && index > 0)
        {
            var cardList = userCharacters.Keys.OrderBy(k => k).ToList();
            if (index <= cardList.Count)
            {
                return cardList[index - 1];
            }
        }

        // 尝试直接匹配卡名（完全匹配或模糊匹配）
        if (userCharacters.TryGetValue(identifier, out _))
        {
            return identifier;
        }

        // 尝试模糊匹配（包含关键词）
        var fuzzyMatches = userCharacters.Keys.Where(k => k.Contains(identifier, StringComparison.OrdinalIgnoreCase)).ToList();
        if (fuzzyMatches.Count == 1)
        {
            return fuzzyMatches[0];
        }

        return null;
    }

    /// <summary>
    /// 为 duel 指令创建转发消息节点
    /// </summary>
    private (string timestamp, long userId, string senderName, string content) CreateDuelForwardNode(string content)
    {
        var selfInfo = MessageDistribution?.GetSelfInfo();
        var botId = selfInfo?.UserId ?? 1001;
        var botName = selfInfo?.Nickname ?? "机器人";
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return (timestamp, botId, botName, content);
    }

    /// <summary>
    /// duel 指令转发消息回复（支持群组和私聊）
    /// 群组使用 OneBot 11 转发格式；模拟模式显示合并气泡；私聊 fallback 到普通消息
    /// </summary>
    private void ReplyDuelForward(List<string> messageContents, Msg msg)
    {
        if (msg.IsSimulationMode)
        {
            // 模拟模式：直接调用ReplyForward以创建合并气泡（内部会生成ForwardMessage）
            var forwardNodes = messageContents
                .Select(content => CreateDuelForwardNode(content))
                .ToList();
            MessageDistribution?.ReplyForward(forwardNodes, msg);
        }
        else if (msg.Source == MessageSource.group && MessageDistribution?.WSconnection?.IsWsConnected == true)
        {
            // 群组且WS已连接：发送OneBot 11合并转发
            var forwardNodes = messageContents
                .Select(content => CreateDuelForwardNode(content))
                .ToList();
            MessageDistribution?.ReplyForward(forwardNodes, msg);
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
    /// 处理 duel 指令（开始或推进对战游戏）：
    /// 语法示例：
    /// .duel
    /// 无需任何参数，用于开始新游戏或推进当前游戏的回合
    /// </summary>
    private void HandleDuelCommand(string args, Msg msg)
    {
        Log.InfoFormat("[Duel] 处理 duel 指令，用户: {0}", msg.UserId);

        string userIdStr = msg.UserId.ToString();
        string normalizedArgs = (args ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        bool isRestart = normalizedArgs == "restart";

        // 每日回合上限检查
        if (IsDuelTurnLimited(msg.UserId))
        {
            var userTrustValue = userTrust.TryGetValue(msg.UserId, out var trust) ? trust : 0;
            var noTurnsMessage = GlobalFeedbackMessages.FeedbackTemplates["DuelNoTurnsAvailable"];
            var formattedMessage = SafeFormatString(noTurnsMessage, userTrustValue.ToString("F1"));
            Reply(formattedMessage, msg);
            return;
        }

        // 娱乐功能扣减好感度（仅在未被回合上限拦截时执行）
        ApplyDuelPenalty(msg.UserId);

        if (isRestart)
        {
            gameStates.TryRemove(userIdStr, out _);
            MessageDistribution?.ClearUserFocus(userIdStr);
        }

        var gameState = LoadUserGameState(userIdStr);

        // 每次用户通过 .duel 进入/继续游戏时，更新活跃时间戳
        if (gameState != null)
        {
            gameState.LastActiveTime = DateTime.UtcNow;
        }

        // 获取当前游戏阶段
        var currentPhase = GetCurrentGamePhase(userIdStr);

        // 如果当前等待决策，提示已有状态
        if (currentPhase == GamePhase.WaitingForDecision && gameState != null &&
            (gameState.PendingCard != null || gameState.IsProcessingHandAction))
        {
            // 创建消息列表用于合并转发
            var continuedGameMessages = new List<string>();

            // 显示继续游戏的剩余回合数
            var duelLimit = GetDuelDailyTurnLimit(msg.UserId);
            var turnsRemaining = GetDuelTurnsRemaining(msg.UserId);
            var detailedInfo = GetDuelLimitDetailedInfo(msg.UserId);
            var runtime = GetDailyRuntimeState(msg.UserId);
            var duelContinueMessage = GlobalFeedbackMessages.FeedbackTemplates["DuelContinue"];
            var formattedDuelContinueMessage = SafeFormatString(duelContinueMessage, duelLimit.ToString(), runtime.DuelTurnsToday.ToString(), turnsRemaining.ToString(), detailedInfo);

            // 添加前置消息
            continuedGameMessages.Add(formattedDuelContinueMessage);

            var statusMessage = MDiceV2.Core.GameBattle.GameStateUtils.GetGameStatus(gameState);
            var combinedMessage = $"{statusMessage}";

            if (gameState.IsProcessingHandAction && gameState.Player2.HandCards.Count > 0)
            {
                var handInfo = gameState.Player2.GetHandInfo();
                combinedMessage += $"\n{handInfo}";
                combinedMessage += "\n请选择要使用的手牌：";
                combinedMessage += "\n格式：手牌编号.场地位置（如：1.1 = 使用第1张牌放到前场，2.y = 使用第2张特殊卡，3.n = 不使用第3张特殊卡）";
                combinedMessage += "\n或者直接回复 0 跳过当前回合";

                // 添加游戏状态消息
                continuedGameMessages.Add(combinedMessage);

                // 单一合并转发
                ReplyDuelForward(continuedGameMessages, msg);
                MessageDistribution?.SetUserFocus(userIdStr, "carddecision");
                return;
            }

            if (gameState.PendingCard != null)
            {
                var pendingCard = gameState.PendingCard;
                combinedMessage += $"\n你有一张等待处理的卡牌：{pendingCard.Name}";
                if (pendingCard is MDiceV2.Core.GameBattle.CharacterCard)
                {
                    combinedMessage += "\n请回复 1（前场）、2（中场）或 3（后场）来选择放置位置。";
                }
                else if (pendingCard is MDiceV2.Core.GameBattle.SpecialCard)
                {
                    combinedMessage += "\n请回复 y（使用）或 n（不使用）来决定是否使用特殊卡。";
                }

                // 添加游戏状态消息
                continuedGameMessages.Add(combinedMessage);

                // 单一合并转发
                ReplyDuelForward(continuedGameMessages, msg);
                MessageDistribution?.SetUserFocus(userIdStr, "carddecision");
                return;
            }
        }

        // 执行到这里表示是新游戏，创建并开始
        Log.InfoFormat("[Duel] 创建新游戏，用户: {0}", msg.UserId);
        gameState = CreateNewGame(userIdStr);
        gameState.LastActiveTime = DateTime.UtcNow;
        gameStates[userIdStr] = gameState;

        Log.InfoFormat("[Duel] 新游戏创建完成，CurrentTurn: {0}", gameState.CurrentTurn);

        // 创建主消息列表用于合并新游戏初始化的所有消息
        var consolidatedMessages = new List<string>();

        // 显示新游戏的可用回合数
        var duelLimitInit = GetDuelDailyTurnLimit(msg.UserId);
        var turnsRemainingInit = GetDuelTurnsRemaining(msg.UserId);
        var detailedInitInfo = GetDuelLimitDetailedInfo(msg.UserId);
        var runtimeInit = GetDailyRuntimeState(msg.UserId);
        var duelNewMessage = GlobalFeedbackMessages.FeedbackTemplates["DuelNew"];
        var formattedDuelNewMessage = SafeFormatString(duelNewMessage, duelLimitInit.ToString(), runtimeInit.DuelTurnsToday.ToString(), turnsRemainingInit.ToString(), detailedInitInfo);

        var rulesMessage = GetGameRules();
        var newGameStatus = MDiceV2.Core.GameBattle.GameStateUtils.GetGameStatus(gameState);

        // 将初始化消息合并成紧凑的节点
        consolidatedMessages.Add(formattedDuelNewMessage);
        // 将规则和游戏状态合并为一个节点
        consolidatedMessages.Add(rulesMessage + "\n\n" + newGameStatus);

        // 如果是新游戏或需要抽卡的情况，执行抽卡前检查回合限制
        //Log.InfoFormat("[Duel] 检查是否需要执行回合：gameState.CurrentTurn={0}, PendingCard={1}, IsProcessingHandAction={2}", 
        //    gameState.CurrentTurn, gameState.PendingCard != null, gameState.IsProcessingHandAction);
        if (gameState.CurrentTurn == 1 || (gameState.PendingCard == null && !gameState.IsProcessingHandAction))
        {
            //Log.InfoFormat("[Duel] 进入回合处理分支");
            // 检查是否还有剩余回合数（仅在游戏进行中检查，不计算初始创建）
            if (gameState.CurrentTurn > 1 && IsDuelTurnLimited(msg.UserId))
            {
                var detailedInfo = GetDuelLimitDetailedInfo(msg.UserId);
                Reply($"每日 duel 回合已用尽，无法继续游戏。\n{detailedInfo}", msg);
                return;
            }

            var turnManager = new MDiceV2.Core.GameBattle.TurnManager(gameState);
            var turnMessages = turnManager.StartTurn();

            // 记录一次可操作回合
            Log.InfoFormat("[Duel] HandleDuelCommand 调用 IncrementDuelTurn，用户: {0}, CurrentTurn: {1}", msg.UserId, gameState.CurrentTurn);
            //IncrementDuelTurn(msg.UserId);

            // 合并所有回合消息为一个紧凑的节点（用换行分隔，而不是多个独立节点）
            if (turnMessages.Count > 0)
            {
                consolidatedMessages.Add(string.Join("\n", turnMessages));
            }

            // 单一合并转发：包含初始化信息和第一回合所有内容（2-3个紧凑节点）
            ReplyDuelForward(consolidatedMessages, msg);

            if (gameState.PendingCard != null || gameState.IsProcessingHandAction)
            {
                MessageDistribution?.SetUserFocus(userIdStr, "carddecision");
            }
        }
        else if (gameState.PendingCard != null || gameState.IsProcessingHandAction)
        {
            Log.InfoFormat("[Duel] 进入待卡处理分支");
            var combinedMessage = string.Empty;

            if (gameState.IsProcessingHandAction && gameState.Player2.HandCards.Count > 0)
            {
                var handInfo = gameState.Player2.GetHandInfo();
                combinedMessage = $"{handInfo}";
                combinedMessage += "\n请选择要使用的手牌：";
                combinedMessage += "\n格式：手牌编号.场地位置（如：1.1 = 使用第1张牌放到前场，2.y = 使用第2张特殊卡，3.n = 不使用第3张特殊卡）";
                combinedMessage += "\n或者直接回复 0/end 跳过当前回合";
            }
            else if (gameState.PendingCard != null)
            {
                var pendingCard = gameState.PendingCard;
                combinedMessage = $"你有一张等待处理的卡牌：{pendingCard.Name}";
                if (pendingCard is MDiceV2.Core.GameBattle.CharacterCard)
                {
                    combinedMessage += "\n请回复 1（前场）、2（中场）或 3（后场）来选择放置位置。";
                }
                else if (pendingCard is MDiceV2.Core.GameBattle.SpecialCard)
                {
                    combinedMessage += "\n请回复 y（使用）或 n（不使用）来决定是否使用特殊卡。";
                }
            }

            ReplyDuelForward(new List<string> { combinedMessage }, msg);
            MessageDistribution?.SetUserFocus(userIdStr, "carddecision");
        }
        else
        {
            Log.InfoFormat("[Duel] 进入默认分支 - 无游戏或卡牌");
            Reply("当前没有正在进行的游戏或等待处理的卡牌。请使用 .duel 开始新游戏。", msg);
        }
    }

    /// <summary>
    /// 获取游戏规则说明
    /// </summary>
    private string GetGameRules()
    {
        return @"######《对战游戏规则》######
通过使用卡牌和人物卡增长三维属性，最终根据差距最大的属性决定胜负。

当到达第20回合或者某一属性低于-10时，游戏结束并结算，详细基本规则可使用“.rule(duel)基础规则”查询。

关于详细的人物卡和特殊卡效果，请使用”.rule(duel)[卡牌名称]“查询(如”.rule(duel)哥布林“)，或者直接在对局决策状态下使用”s[卡牌名称]“来快捷查询。";
    }

    /// <summary>
    /// 加载用户游戏状态（从内存中获取）
    /// </summary>
    public MDiceV2.Core.GameBattle.GameState? LoadUserGameState(string userId)
    {
        return gameStates.TryGetValue(userId, out var gameState) ? gameState : null;
    }

    /// <summary>
    /// 保存用户游戏状态（只保存到内存，gameState已经是引用所以不需要重新赋值）
    /// </summary>


    /// <summary>
    /// 从 GameRuleData 二进制 JSON 文件加载所有游戏状态到内存（启动时调用）
    /// 改进：遇到非全局致命性错误时优先保证加载顺利进行
    /// </summary>
    public void LoadAllGameStates()
    {
        try
        {
            Log.InfoFormat("[LoadAllGameStates] ========== 游戏状态加载开始 ==========");

            // 第一步：尝试初始化 GameLoader
            bool loaderInitialized = false;
            try
            {
                loaderInitialized = MDiceV2.Core.GameBattle.GameLoader.Initialize();
                if (loaderInitialized)
                {
                    Log.InfoFormat("[LoadAllGameStates] GameLoader 初始化成功");
                }
                else
                {
                    Log.Warn("[LoadAllGameStates] GameLoader 初始化失败，数据加载可能不完整，但将继续尝试恢复");
                }
            }
            catch (Exception initEx)
            {
                Log.Warn($"[LoadAllGameStates] GameLoader 初始化异常: {initEx.Message}，将在降级模式下继续加载");
                loaderInitialized = false;
            }

            // 第二步：加载游戏状态数据
            var ruleData = GameRuleDataStore.Load();
            if (ruleData == null || ruleData.UserGameStates == null || ruleData.UserGameStates.Count == 0)
            {
                Log.InfoFormat("[LoadAllGameStates] 没有保存的游戏状态数据");
                gameStates = new System.Collections.Concurrent.ConcurrentDictionary<string, MDiceV2.Core.GameBattle.GameState>();
                return;
            }

            // 第三步：逐用户加载游戏状态，隔离处理单个用户的失败
            var restored = new Dictionary<string, MDiceV2.Core.GameBattle.GameState>();
            int successCount = 0;
            int failureCount = 0;
            var failedUsers = new List<string>();

            foreach (var kvp in ruleData.UserGameStates)
            {
                string userId = kvp.Key;
                GameStateSnapshot snapshot = kvp.Value;

                try
                {
                    // 数据格式验证
                    if (snapshot == null)
                    {
                        Log.Warn($"[LoadAllGameStates] 用户 {userId} 的快照为null，跳过");
                        failureCount++;
                        failedUsers.Add(userId);
                        continue;
                    }

                    // 尝试从快照恢复游戏状态
                    var state = GameStateSnapshotMapper.FromSnapshot(snapshot);
                    if (state != null)
                    {
                        restored[userId] = state;
                        successCount++;
                        Log.InfoFormat("[LoadAllGameStates] 用户 {0} 的游戏状态恢复成功 (回合: {1})", userId, state.CurrentTurn);
                    }
                    else
                    {
                        Log.Warn($"[LoadAllGameStates] 用户 {userId} 的快照转换失败（FromSnapshot返回null），跳过");
                        failureCount++;
                        failedUsers.Add(userId);
                    }
                }
                catch (Exception userEx)
                {
                    Log.Warn($"[LoadAllGameStates] 用户 {userId} 的游戏状态加载失败: {userEx.Message}，跳过此用户");
                    failureCount++;
                    failedUsers.Add(userId);
                    // 继续处理下一个用户，不中断整个加载流程
                }
            }

            // 第四步：填充内存字典
            gameStates = new System.Collections.Concurrent.ConcurrentDictionary<string, MDiceV2.Core.GameBattle.GameState>(restored);

            // 第五步：输出详细的加载统计
            var loadedKeys = string.Join(",", gameStates.Keys);
            Log.InfoFormat("[LoadAllGameStates] ========== 游戏状态加载完成 ==========");
            Log.InfoFormat("[LoadAllGameStates] 成功加载: {0} 个游戏状态（用户: {1}）", successCount, loadedKeys);

            if (failureCount > 0)
            {
                var failedUsersStr = string.Join(",", failedUsers);
                Log.Warn($"[LoadAllGameStates] 加载失败: {failureCount} 个用户的数据无法恢复（用户: {failedUsersStr}）");
            }

            if (!loaderInitialized && successCount > 0)
            {
                Log.Warn("[LoadAllGameStates] 已在 GameLoader 初始化失败的降级模式下成功加载 " + successCount + " 个游戏状态");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[LoadAllGameStates] 游戏状态加载过程发生严重异常: {ex.Message}");
            Log.Error($"[LoadAllGameStates] 堆栈跟踪: {ex.StackTrace}");
            // 即使发生异常，也初始化空字典而不是让gameStates为null
            gameStates = new System.Collections.Concurrent.ConcurrentDictionary<string, MDiceV2.Core.GameBattle.GameState>();
        }
        finally
        {
            // 保证gameStates不为null
            if (gameStates == null)
            {
                gameStates = new System.Collections.Concurrent.ConcurrentDictionary<string, MDiceV2.Core.GameBattle.GameState>();
                Log.Warn("[LoadAllGameStates] gameStates被重新初始化为空字典");
            }
        }
    }

    /// <summary>
    /// 将所有游戏状态保存到 GameRuleData 二进制 JSON 文件（关闭时调用）
    /// </summary>
    public void SaveAllGameStates()
    {
        try
        {
            // 根据最近活跃时间过滤需要保存的游戏状态
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-gameStateRetentionDays);

            var snapshotDict = new Dictionary<string, GameStateSnapshot>();
            foreach (var kvp in gameStates)
            {
                var state = kvp.Value;
                if (state == null)
                {
                    continue;
                }

                if (state.LastActiveTime == default)
                {
                    state.LastActiveTime = now;
                }

                if (state.LastActiveTime >= cutoff)
                {
                    var snap = GameStateSnapshotMapper.ToSnapshot(state);
                    if (snap != null)
                    {
                        snapshotDict[kvp.Key] = snap;
                    }
                }
            }

            var ruleData = new GameRuleData
            {
                UserGameStates = snapshotDict
            };

            GameRuleDataStore.Save(ruleData);
            var savedKeys = string.Join(",", snapshotDict.Keys);
            Log.InfoFormat("[SaveAllGameStates] 已通过 GameRuleData 保存 {0} 个游戏状态快照（保留期: {1} 天），用户: {2}", snapshotDict.Count, gameStateRetentionDays, savedKeys);
        }
        catch (Exception ex)
        {
            Log.Error($"[SaveAllGameStates] 保存所有游戏状态到 GameRuleData 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取用户当前游戏阶段
    /// </summary>
    public GamePhase GetCurrentGamePhase(string userId)
    {
        var gameState = LoadUserGameState(userId);
        if (gameState == null)
        {
            return GamePhase.NoGame;
        }

        if (gameState.IsGameOver)
        {
            return GamePhase.GameEnded;
        }

        // 检查是否处于手牌操作阶段或等待卡牌决策
        if (gameState.IsProcessingHandAction || gameState.PendingCard != null)
        {
            return GamePhase.WaitingForDecision;
        }

        return GamePhase.GameOngoing;
    }

    /// <summary>
    /// 创建新游戏
    /// </summary>
    private MDiceV2.Core.GameBattle.GameState CreateNewGame(string userId)
    {
        // 初始化GameLoader
        if (!MDiceV2.Core.GameBattle.GameLoader.Initialize())
        {
            throw new InvalidOperationException("游戏数据加载失败，无法开始游戏。请检查游戏文件是否完整。");
        }

        // 检查是否有足够的卡牌
        var humanCharacters = MDiceV2.Core.GameBattle.GameLoader.GetCharactersByFaction(MDiceV2.Core.GameBattle.Faction.Human);
        var demonCharacters = MDiceV2.Core.GameBattle.GameLoader.GetCharactersByFaction(MDiceV2.Core.GameBattle.Faction.Demon);
        var humanSpecialCards = MDiceV2.Core.GameBattle.GameLoader.GetSpecialCardsByFaction(MDiceV2.Core.GameBattle.Faction.Human);
        var demonSpecialCards = MDiceV2.Core.GameBattle.GameLoader.GetSpecialCardsByFaction(MDiceV2.Core.GameBattle.Faction.Demon);

        if (humanCharacters.Count == 0 && demonCharacters.Count == 0)
        {
            throw new InvalidOperationException("没有找到角色卡数据，无法开始游戏。");
        }

        if (humanSpecialCards.Count == 0 && demonSpecialCards.Count == 0)
        {
            throw new InvalidOperationException("没有找到特殊卡数据，无法开始游戏。");
        }

        var gameState = new MDiceV2.Core.GameBattle.GameState
        {
            Player1 = new MDiceV2.Core.GameBattle.Player("魔王军", 10, 10, 10), // AI
            Player2 = new MDiceV2.Core.GameBattle.Player("人类玩家", 10, 10, 10), // 玩家
            Player2Id = userId,
            CurrentTurn = 1,
            CurrentWeather = "Clear"
        };

        // 初始化卡牌牌堆
        var turnManager = new MDiceV2.Core.GameBattle.TurnManager(gameState);
        turnManager.InitializeGame();

        return gameState;
    }

    /// <summary>
    /// 处理.diy指令：用户自定义指令
    /// 格式：.diy [指令名]:[内容] 或 .diy list [页码]
    /// 示例：.diy ra:cc{et}
    ///      .diy ra:cc{et}+ -l
    ///      .diy list
    ///      .diy list 2
    /// </summary>
    private void HandleDiyCommand(string args, Msg msg)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            Reply("请指定自定义指令格式：.diy [指令名]:[内容]\n示例：.diy ra:cc{et}\n      .diy ra:cc{et}+ -l\n或使用 .diy list 查看已有指令", msg);
            return;
        }

        // 检查是否为list指令
        string trimmedArgs = args.Trim().ToLower();
        if (trimmedArgs == "list" || trimmedArgs.StartsWith("list "))
        {
            HandleDiyListCommand(args, msg);
            return;
        }

        // 解析指令格式：[指令名]:[内容]
        int colonIndex = args.IndexOf(':');
        if (colonIndex == -1)
        {
            Reply("格式错误！正确格式：.diy [指令名]:[内容]\n示例：.diy ra:cc{et}\n或使用 .diy list 查看已有指令", msg);
            return;
        }

        string commandName = args.Substring(0, colonIndex).Trim().ToLower();
        string commandContent = args.Substring(colonIndex + 1).Trim();

        // 验证指令名（不能为空，只能包含字母数字）
        if (string.IsNullOrWhiteSpace(commandName))
        {
            Reply("指令名不能为空！", msg);
            return;
        }

        if (!Regex.IsMatch(commandName, @"^[a-z0-9]+$"))
        {
            Reply("指令名只能包含小写字母和数字！", msg);
            return;
        }

        // 验证内容不能为空
        if (string.IsNullOrWhiteSpace(commandContent))
        {
            Reply("指令内容不能为空！", msg);
            return;
        }

        // 获取或创建用户的自定义指令字典
        long userId = msg.UserId;
        if (!userCustomCommands.ContainsKey(userId))
        {
            userCustomCommands[userId] = new Dictionary<string, string>();
        }

        // 检查数量限制（白名单用户等级<3不限制，普通用户最多10条）
        bool isWhitelisted = msg.UserAuthLevel.HasValue && msg.UserAuthLevel.Value < 3;
        int currentCount = userCustomCommands[userId].Count;
        bool isUpdate = userCustomCommands[userId].ContainsKey(commandName);

        if (!isWhitelisted && !isUpdate && currentCount >= 10)
        {
            Reply($"自定义指令数量已达上限（最多10条）！\n当前已有：{currentCount}条", msg);
            return;
        }

        // 保存自定义指令
        userCustomCommands[userId][commandName] = commandContent;

        // 持久化到用户数据
        SaveUserData(userId);

        // 转义{}以正确显示
        string escapedContent = commandContent.Replace("{", "{{").Replace("}", "}}");
        Reply($"自定义指令已设置：/{commandName}\n内容：{escapedContent}\n使用 /{commandName} [参数] 来调用此指令", msg);
        Log.InfoFormat($"用户 {userId} 设置自定义指令：/{commandName} -> {commandContent}");
    }

    /// <summary>
    /// 尝试处理自定义指令（/前缀）
    /// 使用前缀匹配，优先匹配最长的自定义指令键值，避免误匹配
    /// </summary>
    private bool TryHandleCustomCommand(string trimmedText, Msg msg)
    {
        // 移除开头的/
        string content = trimmedText.Substring(1);

        if (string.IsNullOrWhiteSpace(content))
        {
            Reply("请指定自定义指令名称！使用 .diy 指令创建自定义指令。", msg);
            return true;
        }

        // 获取用户的自定义指令字典
        long userId = msg.UserId;
        if (!userCustomCommands.ContainsKey(userId) || userCustomCommands[userId].Count == 0)
        {
            Reply($"未找到自定义指令：/{content.Split(new[] { ' ' }, StringSplitOptions.None)[0]}\n使用 .diy 指令创建自定义指令。", msg);
            return true;
        }

        // 按照指令名长度从长到短排序，优先匹配最长的指令
        var sortedCommands = userCustomCommands[userId].OrderByDescending(kvp => kvp.Key.Length);

        foreach (var customCmd in sortedCommands)
        {
            string commandName = customCmd.Key;
            string commandTemplate = customCmd.Value;

            // 检查是否以该指令名开头
            if (content.StartsWith(commandName, StringComparison.OrdinalIgnoreCase))
            {
                // 提取用户参数（指令名后的所有内容）
                string userArgs = content.Substring(commandName.Length).Trim();

                // 执行自定义指令
                ExecuteCustomCommand(commandName, commandTemplate, userArgs, msg);
                return true;
            }
        }

        // 未找到匹配的自定义指令
        string attemptedCmd = content.Split(new[] { ' ' }, StringSplitOptions.None)[0];
        Reply($"未找到自定义指令：/{attemptedCmd}\n使用 .diy list 查看已有指令", msg);
        return true;
    }

    /// <summary>
    /// 执行自定义指令
    /// </summary>
    private void ExecuteCustomCommand(string commandName, string commandTemplate, string userArgs, Msg msg)
    {
        // 解析指令模板
        // 格式：主体内容[+ 后缀]
        string mainContent;
        string suffix = string.Empty;

        int plusIndex = commandTemplate.IndexOf('+');
        if (plusIndex != -1)
        {
            mainContent = commandTemplate.Substring(0, plusIndex).Trim();
            suffix = commandTemplate.Substring(plusIndex + 1).Trim();
        }
        else
        {
            mainContent = commandTemplate;
        }

        // 构建完整指令：. + 主体内容 + 用户参数 + 后缀
        string expandedCommand = mainContent;
        if (!string.IsNullOrWhiteSpace(userArgs))
        {
            expandedCommand += " " + userArgs;
        }
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            expandedCommand += " " + suffix;
        }

        // 确保指令以.开头
        if (!expandedCommand.StartsWith(".", StringComparison.Ordinal))
        {
            expandedCommand = "." + expandedCommand;
        }

        Log.InfoFormat($"用户 {msg.UserId} 执行自定义指令：/{commandName} {userArgs} -> {expandedCommand}");

        // 创建新的消息对象并递归处理
        var newMsg = new Msg(msg.GroupId, msg.UserId, expandedCommand, msg.Source, msg.IsSimulationMode, msg.IsAted, msg.ShouldIgnore);
        OnHandleMessage(newMsg);
    }

    /// <summary>
    /// 处理.diy list指令：显示用户的自定义指令列表
    /// 格式：.diy list [页码]
    /// 每页显示10条，默认显示第1页
    /// </summary>
    private void HandleDiyListCommand(string args, Msg msg)
    {
        long userId = msg.UserId;

        // 获取用户的自定义指令列表
        if (!userCustomCommands.ContainsKey(userId) || userCustomCommands[userId].Count == 0)
        {
            Reply("你还没有设置任何自定义指令！\n使用 .diy [指令名]:[内容] 来创建自定义指令\n示例：.diy ra:cc{et}", msg);
            return;
        }

        // 解析页码
        int pageNumber = 1;
        string trimmedArgs = args.Trim();
        if (trimmedArgs.Length > 4) // "list" 后面有内容
        {
            string pageStr = trimmedArgs.Substring(4).Trim();
            if (!string.IsNullOrWhiteSpace(pageStr) && !int.TryParse(pageStr, out pageNumber))
            {
                Reply("页码格式错误！请使用数字\n示例：.diy list 2", msg);
                return;
            }
            if (pageNumber < 1)
            {
                pageNumber = 1;
            }
        }

        var commands = userCustomCommands[userId];
        int totalCount = commands.Count;
        int pageSize = 10;
        int totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        if (pageNumber > totalPages)
        {
            Reply($"页码超出范围！当前共 {totalPages} 页", msg);
            return;
        }

        // 构建显示内容
        var sortedCommands = commands.OrderBy(kvp => kvp.Key).ToList();
        int startIndex = (pageNumber - 1) * pageSize;
        int endIndex = Math.Min(startIndex + pageSize, totalCount);

        var responseLines = new List<string>
        {
            $"=== 自定义指令列表 (第{pageNumber}/{totalPages}页) ===",
            $"共 {totalCount} 条指令"
        };

        for (int i = startIndex; i < endIndex; i++)
        {
            var kvp = sortedCommands[i];
            responseLines.Add($"/{kvp.Key}:{kvp.Value}");
        }

        if (totalPages > 1)
        {
            if (pageNumber < totalPages)
            {
                responseLines.Add($"\n使用 .diy list {pageNumber + 1} 查看下一页");
            }
            if (pageNumber > 1)
            {
                responseLines.Add($"使用 .diy list {pageNumber - 1} 查看上一页");
            }
        }
        string escapedContent = string.Join("\n", responseLines).Replace("{", "{{").Replace("}", "}}");
        Reply(escapedContent, msg);
    }

    /// <summary>
    /// 处理 #pfm 指令：性能调试命令
    /// #pfm start   - 启动调试会话，记录来自启动者的多条指令性能信息
    /// #pfm stop    - 停止调试会话并返回收集的所有信息
    /// #pfm status  - 查看调试状态
    /// </summary>
    private void HandleDebugPerfMonitor(string args, Msg msg)
    {
        // 权限检查：仅 Master 和 1001 可用
        if (!msg.IsSystemAccount && !msg.IsMasterAccount)
        {
            Reply("❌ 权限不足！仅 Master 账号和系统账号可以使用此命令", msg);
            return;
        }

        string command = args.Trim().ToLowerInvariant();

        switch (command)
        {
            case "start":
                DebugMonitor.StartDebugSession(msg.UserId);
                Reply($"✅ 调试模式已启动（启动者ID: {msg.UserId}）\n" +
                      "将记录多条来自您的指令性能信息。\n" +
                      "• 每条指令完成后继续记录\n" +
                      "• 当日志接近4000字符时自动停止\n" +
                      "• 或手动执行 #pfm stop 停止并获取回执\n" +
                      "输出限制: 4000字符。", msg);
                Log.Normal($"[#pfm] 调试会话由用户 {msg.UserId} 启动（支持多消息模式）");
                break;

            case "stop":
                {
                    var debugInfo = DebugMonitor.StopDebugSession(msg.UserId);
                    if (debugInfo != null)
                    {
                        Reply($"✅ 调试会话已手动结束，收集信息如下：\n\n{debugInfo}", msg);
                        Log.Normal("[#pfm] 调试会话已停止，信息已返回给用户");
                    }
                    else
                    {
                        Reply("❌ 调试模式未启动或您不是启动者。", msg);
                    }
                }
                break;

            case "status":
                {
                    bool isActive = DebugMonitor.IsEnabled;
                    string status = isActive ? "🟢 激活中" : "🔴 未激活";
                    Reply($"调试模式状态：{status}", msg);
                }
                break;

            default:
                Reply("用法：#pfm start|stop|status\n" +
                      "- start：启动调试模式，记录多条指令\n" +
                      "- stop：手动停止调试并获取收集的信息\n" +
                      "- status：查看当前调试状态\n" +
                      "\n说明：\n" +
                      "• 调试模式记录启动者的所有消息\n" +
                      "• 每条指令完成后自动检查日志大小\n" +
                      "• 日志接近4000字符时自动停止并返回\n" +
                      "• 输出限制：4000字符", msg);
                break;
        }
    }


    /// <summary>
    /// 处理 .ti 指令 - 临时疯狂随机选择
    /// </summary>
    private void HandleTempInsanity(string args, Msg msg)
    {
        try
        {
            string trimmedArgs = args.Trim().ToLower();
            
            // 默认使用即时疯狂表（表Ⅶ），.ti solo 使用总结疯狂表
            Dictionary<string, string> insanityTable;
            string tableName;
            bool isSoloMode = false;
            
            if (trimmedArgs == "solo")
            {
                insanityTable = GlobalFeedbackMessages.TempInsanityTable;
                tableName = "总结疯狂表";
                isSoloMode = true;
            }
            else
            {
                insanityTable = GlobalFeedbackMessages.InstantInsanityTable;
                tableName = "即时疯狂表（表Ⅶ）";
            }

            // 首先掷D10确定使用的是哪个效果
            int d10Result = Dice.Roll("1d10").Total;
            
            // 从表中随机选择一个效果（使用即时疯狂的掷骰格式：1-10对应不同的即时症状）
            int index = d10Result - 1; // D10结果1-10对应索引0-9
            
            // 确保索引在有效范围内
            if (index < 0 || index >= insanityTable.Count)
            {
                index = GlobalRandom.Next(insanityTable.Count);
            }
            
            var selectedEffect = insanityTable.ElementAt(index);

            // 构建返回消息
            string effectMessage;
            if (isSoloMode)
            {
                // 总结疯狂表：显示D10掷骰结果和效果名称
                effectMessage = $"【疯狂发作 - {tableName}】\n" +
                               $"🎲 D10 = {d10Result}\n" +
                               $"━━━━━━━━━━━━━━━━━━━━\n" +
                               $"▶ {selectedEffect.Key}\n" +
                               selectedEffect.Value;
            }
            else
            {
                // 即时疯狂表：显示D10掷骰结果（行号）和即时症状
                effectMessage = $"【疯狂发作 - {tableName}】\n" +
                               $"🎲 D10 = {d10Result}\n" +
                               $"━━━━━━━━━━━━━━━━━━━━\n" +
                               $"▶ 症状 {d10Result}: {selectedEffect.Key}\n" +
                               selectedEffect.Value;
            }

            // 通过 RefineMsg 处理掷骰表达式（自动替换 <dice xxxx> 为实际掷骰结果）
            string refinedMessage = RefineMsg(effectMessage, msg);

            Reply(refinedMessage, msg);

        }
        catch (Exception ex)
        {
            Log.Error($"[临时疯狂] 处理 .ti 指令时出错: {ex.Message}");
            Reply("处理临时疯狂指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 .gc 指令（角色属性生成）
    /// 语法：.gc coc [行数]（行数为1-20，默认为1）
    /// </summary>
    private void HandleCharacterGen(string args, Msg msg)
    {
        try
        {
            // 解析参数
            string trimmedArgs = args.Trim();

            // 默认模式为 coc，行数为 1
            string mode = "coc";
            int lineCount = 1;

            if (!string.IsNullOrWhiteSpace(trimmedArgs))
            {
                // 分离模式和行数
                var parts = trimmedArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length > 0)
                {
                    // 检查第一个参数是否为模式（coc/dnd/et）
                    string firstPart = parts[0].ToLower();
                    if (firstPart == "coc" || firstPart == "dnd" || firstPart == "et")
                    {
                        mode = firstPart;

                        // 检查第二个参数是否为行数
                        if (parts.Length > 1 && int.TryParse(parts[1], out int parsedLineCount))
                        {
                            lineCount = Math.Clamp(parsedLineCount, 1, 20);
                        }
                    }
                    else if (int.TryParse(firstPart, out int parsedLineCount))
                    {
                        // 如果第一个参数是数字，则使用默认模式 coc 和指定的行数
                        lineCount = Math.Clamp(parsedLineCount, 1, 20);
                    }
                }
            }

            // 目前实现 coc、dnd、et 模式
            if (mode == "coc")
            {
                HandleCoCCharacterGen(lineCount, msg);
            }
            else if (mode == "dnd")
            {
                HandleDNDCharacterGen(lineCount, msg);
            }
            else if (mode == "et")
            {
                HandleETCharacterGen(lineCount, msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[角色生成] 处理 .gc 指令时出错: {ex.Message}");
            Reply("处理角色生成指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 CoC 角色属性生成
    /// </summary>
    private void HandleCoCCharacterGen(int lineCount, Msg msg)
    {
        try
        {
            var resultLines = new List<string>(); // 保存每行角色属性生成结果的文本列表

            // 生成指定行数的属性
            for (int i = 0; i < lineCount; i++) // 按行数循环生成属性
            {
                // 8个属性掷骰：3d6*5（力量、体质、敏捷、外貌、智力、意志、教育）、(2d6+6)*5（体型）
                DiceResult str = Dice.Roll("3d6"); // 力量掷骰结果
                DiceResult con = Dice.Roll("3d6"); // 体质掷骰结果
                DiceResult siz = Dice.Roll("2d6"); // 体型掷骰结果（未加基础值）
                DiceResult dex = Dice.Roll("3d6"); // 敏捷掷骰结果
                DiceResult app = Dice.Roll("3d6"); // 外貌掷骰结果
                DiceResult pow = Dice.Roll("3d6"); // 智力/意志掷骰结果（按现有输出映射）
                DiceResult edu = Dice.Roll("3d6"); // 意志/教育掷骰结果（按现有输出映射）
                DiceResult san = Dice.Roll("3d6"); // 教育/理智掷骰结果（按现有输出映射）
                DiceResult luck = Dice.Roll("3d6"); // 幸运掷骰结果

                // 计算属性值
                int strVal = str.Total * 5; // 力量数值
                int conVal = con.Total * 5; // 体质数值
                int sizVal = (siz.Total + 6) * 5; // 体型数值（2d6+6）
                int dexVal = dex.Total * 5; // 敏捷数值
                int appVal = app.Total * 5; // 外貌数值
                int powVal = pow.Total * 5; // 智力数值（按现有输出映射）
                int eduVal = edu.Total * 5; // 意志数值（按现有输出映射）
                int sanVal = san.Total * 5; // 教育数值（按现有输出映射）
                int luckVal = luck.Total * 5; // 幸运数值

                // 计算属性总和
                int attributeTotal = strVal + conVal + sizVal + dexVal + appVal + powVal + eduVal + sanVal; // 不含幸运的总和
                int attributeTotalWithLuck = attributeTotal + luckVal; // 含幸运的总和

                string lineMessage = $"力量：{strVal}，体质：{conVal}，体型：{sizVal}，敏捷：{dexVal}，外貌：{appVal}，智力：{powVal}，意志：{eduVal}，教育：{sanVal}，幸运：{luckVal}\n" +
                    $"Total：{attributeTotal}/{attributeTotalWithLuck}"; // 输出不含幸运/含幸运的总和对比
                resultLines.Add(lineMessage);
            }

            // 构建最终返回消息
            string resultMessage = string.Join("\n\n", resultLines);
            string title = $"【CoC 角色属性生成 - 共 {lineCount} 行】";
            Reply($"{title}\n{resultMessage}", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[CoC角色生成] 处理 CoC 角色属性生成时出错: {ex.Message}");
            Reply("处理 CoC 角色属性生成时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 ET 角色属性生成
    /// 规则：所有属性使用 4d5 投掷
    /// 玛娜 = (情感+20)/20*智力（向下取整）
    /// 生命 = (体质+20)/20*体型（向下取整，体型手动设置）
    /// </summary>
    private void HandleETCharacterGen(int lineCount, Msg msg)
    {
        try
        {
            var resultLines = new List<string>();

            // 生成指定行数的属性
            for (int i = 0; i < lineCount; i++)
            {
                // 投掷所有8个属性，均使用 4d5
                DiceResult strength = Dice.Roll("4d5");
                DiceResult appearance = Dice.Roll("4d5");
                DiceResult knowledge = Dice.Roll("4d5");
                DiceResult agility = Dice.Roll("4d5");
                DiceResult intelligence = Dice.Roll("4d5");
                DiceResult keenness = Dice.Roll("4d5");
                DiceResult constitution = Dice.Roll("4d5");
                DiceResult emotion = Dice.Roll("4d5");

                // 计算衍生属性
                int mana = (emotion.Total + 20) / 20 * intelligence.Total;  // 向下取整
                double lifeTypeRatio = (constitution.Total + 20) / 20.0;  // 生命/体型参考，保留2位小数

                // 计算属性总和
                int attributeTotal = strength.Total + appearance.Total + knowledge.Total + agility.Total +
                                    intelligence.Total + keenness.Total + constitution.Total + emotion.Total;

                string lineMessage = $"力量：{strength.Total}，外貌：{appearance.Total}，见闻：{knowledge.Total}，敏捷：{agility.Total}\n" +
                    $"智力：{intelligence.Total}，敏锐：{keenness.Total}，体质：{constitution.Total}，情感：{emotion.Total}\n" +
                    $"玛娜：{mana}，生命/体型参考：{lifeTypeRatio:F2}\n" +
                    $"Total：{attributeTotal}";
                resultLines.Add(lineMessage);
            }

            // 构建最终返回消息
            string resultMessage = string.Join("\n\n", resultLines);
            string title = $"【ET 角色属性生成 - 共 {lineCount} 行】";
            Reply($"{title}\n{resultMessage}", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[ET角色生成] 处理 ET 角色属性生成时出错: {ex.Message}");
            Reply("处理 ET 角色属性生成时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 DND 5E 角色属性生成
    /// 规则：投掷 4d6，取最高的 3 个骰子总和，重复 6 次
    /// </summary>
    private void HandleDNDCharacterGen(int lineCount, Msg msg)
    {
        try
        {
            var resultLines = new List<string>();

            // 生成指定行数的属性
            for (int i = 0; i < lineCount; i++)
            {
                // 投掷 6 组 4d6，每组取最高 3 个骰子
                var scores = new List<int>();
                for (int j = 0; j < 6; j++)
                {
                    DiceResult diceResult = Dice.Roll("4d6");
                    // 排序骰子结果，取最高的 3 个
                    var sortedRolls = diceResult.Rolls.OrderByDescending(x => x).ToList();
                    int score = sortedRolls[0] + sortedRolls[1] + sortedRolls[2];
                    scores.Add(score);
                }

                // 按降序排列，方便分配
                scores.Sort((a, b) => b.CompareTo(a));

                // 创建属性名称数组
                string[] attributeNames = { "力量", "敏捷", "体质", "智力", "感知", "魅力" };

                // 构建属性字符串
                var attributeLines = new List<string>();

                for (int j = 0; j < 6; j++)
                {
                    attributeLines.Add($"{attributeNames[j]}: {scores[j]}");
                }

                string lineMessage = string.Join(", ", attributeLines);
                resultLines.Add(lineMessage);
            }

            // 构建最终返回消息
            string resultMessage = string.Join("\n\n", resultLines);
            string title = $"【DND 5E Character Generation - {lineCount} Set(s)】";
            Reply($"{title}\n{resultMessage}", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[DND Character Generation] 生成中遇到错误: {ex.Message}");
            Reply("人物生成中遇到了错误.", msg);
        }
    }

    /// <summary>
    /// 处理 .team 指令（队伍管理系统）
    /// 子命令：new, add, join, del, call, sort, list, set
    /// 统一格式：.team 子命令 参数（子命令与参数间空格可省略，如 .teamnew队伍名）
    /// </summary>
    private void HandleTeamCommand(string args, Msg msg)
    {
        if (msg.Source != MessageSource.group)
        {
            Reply("队伍管理指令仅在群组中可用。", msg);
            return;
        }

        try
        {
            var trimmedArgs = args.Trim();
            if (string.IsNullOrEmpty(trimmedArgs))
            {
                Reply("队伍管理指令格式：.team 子命令 参数\n" +
                      "子命令：new 队伍名, add @或QQ, join 队伍名, del 队伍名, call 队伍名, sort 技能名, list, set", msg);
                return;
            }

            // 提取子命令（字母序列）和剩余参数，空格可省略
            var match = Regex.Match(trimmedArgs, @"^([a-zA-Z]+)\s*(.*)$");
            if (!match.Success)
            {
                Reply("队伍管理指令格式无效。子命令：new, add, join, del, call, sort, list, set", msg);
                return;
            }
            string command = match.Groups[1].Value.ToLower();
            string param = match.Groups[2].Value.Trim();

            switch (command)
            {
                case "new":
                    HandleTeamNew(param, msg);
                    break;
                case "add":
                    HandleTeamAdd(param, msg);
                    break;
                case "join":
                    HandleTeamJoin(param, msg);
                    break;
                case "del":
                    HandleTeamDel(param, msg);
                    break;
                case "call":
                    HandleTeamCall(param, msg);
                    break;
                case "sort":
                    HandleTeamSort(param, msg);
                    break;
                case "list":
                    HandleTeamList(param, msg);
                    break;
                case "set":
                    HandleTeamSet(param, msg);
                    break;
                default:
                    // 查询 Mod 注册的 .team 子指令
                    if (_modEventBridge != null)
                    {
                        var providers = _modEventBridge.GetSubcommandProviders();
                        Log.InfoFormat("[SubcommandDispatch] parent=team sub={0} providers={1} types={2}", command, providers.Count, string.Join(",", providers.Select(p => p.GetType().FullName ?? p.GetType().Name)));
                        foreach (var provider in providers)
                        {
                            var result = provider.HandleSubcommand("team", command, param, msg);
                            if (result != null)
                            {
                                Log.InfoFormat("[SubcommandDispatch] parent=team sub={0} provider={1} found=true", command, provider.GetType().FullName ?? provider.GetType().Name);
                                Reply(result, msg);
                                return;
                            }
                        }
                    }
                    Reply($"未知的队伍管理子命令：{command}\n" +
                          "有效子命令：new, add, join, del, call, sort, list, set", msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[队伍管理] 处理 .team 指令时出错: {ex.Message}");
            Reply("处理队伍管理指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// .team new [队伍名] - 创建新队伍
    /// </summary>
    private void HandleTeamNew(string args, Msg msg)
    {
        var teamName = args.Trim();
        if (string.IsNullOrEmpty(teamName))
        {
            Reply("请指定队伍名称：.team new <队伍名>", msg);
            return;
        }

        // 获取群数据
        if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
        {
            groupRecord = new GroupDataRecord { GroupId = msg.GroupId };
            groupDataRecords[msg.GroupId] = groupRecord;
        }

        // 初始化队伍字典
        groupRecord.Teams ??= new Dictionary<string, TeamInfo>();

        // 检查非白名单群的队伍数量限制（AuthLevel >= 3）
        var authLevel = groupRecord.AuthLevel ?? 3;
        if (authLevel >= 3 && groupRecord.Teams.Count >= 5)
        {
            Reply($"非白名单群最多只能创建 5 个队伍，当前已有 {groupRecord.Teams.Count} 个队伍。", msg);
            return;
        }

        // 检查队伍是否已存在
        if (groupRecord.Teams.ContainsKey(teamName))
        {
            Reply($"队伍 '{teamName}' 已存在，请使用其他名称。", msg);
            return;
        }

        // 创建新队伍
        var newTeam = new TeamInfo
        {
            TeamName = teamName,
            CreatorId = msg.UserId,
            Members = new List<long> { msg.UserId }
        };
        groupRecord.Teams[teamName] = newTeam;

        // 设置为用户的默认队伍
        groupRecord.UserDefaultTeams ??= new Dictionary<long, string>();
        groupRecord.UserDefaultTeams[msg.UserId] = teamName;

        SaveGroupData(msg.GroupId);
        Reply($"✓ 队伍 '{teamName}' 创建成功，已将其设置为你的默认队伍。", msg);
    }

    /// <summary>
    /// .team [队伍名] add [@形式的cq码或qq号] - 添加成员到队伍
    /// </summary>
    /// <summary>
    /// 处理 .team add [@或QQ] 子命令：将成员添加到默认队伍
    /// 支持两种参数格式：
    ///   1. @形式: 用户在消息中@某人，自动转为 [CQ:at,qq=123456789] CQ码格式
    ///   2. QQ号: 用户手动输入 123456789 纯数字格式
    /// 这两种格式会被自动识别和处理
    /// </summary>
    private void HandleTeamAdd(string args, Msg msg)
    {
        try
        {
            var trimmedArgs = args.Trim();
            Log.Warn($"[HandleTeamAdd] 接收到的参数: '{trimmedArgs}'");

            if (string.IsNullOrEmpty(trimmedArgs))
            {
                Reply("格式：.team add [@或QQ号] [[@或QQ号] ...]\n示例：.team add @张三 或 .team add 123456789 或 .team add @张三 @李四 @王五", msg);
                return;
            }

            // 从缓存获取或创建群数据
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupData))
            {
                Reply("群数据不存在，请先创建队伍。", msg);
                return;
            }

            groupData.Teams ??= new Dictionary<string, TeamInfo>();
            groupData.UserDefaultTeams ??= new Dictionary<long, string>();

            // 检查用户是否有默认队伍
            if (!groupData.UserDefaultTeams.TryGetValue(msg.UserId, out var defaultTeamName))
            {
                Reply("您还没有加入任何队伍。请先使用 .team join <队伍名> 加入队伍。", msg);
                return;
            }

            // 验证默认队伍是否存在
            if (!groupData.Teams.TryGetValue(defaultTeamName, out var team))
            {
                groupData.UserDefaultTeams.Remove(msg.UserId);
                Reply($"您的默认队伍 '{defaultTeamName}' 已被删除。请重新选择队伍。", msg);
                SaveGroupData(msg.GroupId);
                return;
            }

            // 检查权限：只有创建者或白名单用户（Level ≤ 1）可以添加成员
            if (team.CreatorId != msg.UserId && (groupData.AuthLevel == null || groupData.AuthLevel > 1))
            {
                Reply($"您没有权限向队伍'{defaultTeamName}'添加成员。只有队伍创建者或群主/管理员可以添加成员。", msg);
                return;
            }

            // 解析所有要添加的成员 ID
            var memberIds = ExtractAllUserIdsFromMentions(trimmedArgs);
            Log.Error($"[HandleTeamAdd] 解析后的成员ID列表: {string.Join(", ", memberIds)}");

            if (memberIds.Count == 0)
            {
                Reply("无法识别要添加的成员。请使用 @格式 或 QQ 号码。", msg);
                return;
            }

            // 循环添加所有成员
            var successCount = 0;
            var failureReasons = new List<string>();

            foreach (var memberId in memberIds)
            {
                if (memberId <= 0)
                {
                    failureReasons.Add("无效的成员ID");
                    continue;
                }

                // 检查成员是否已在队伍中
                if (team.Members.Contains(memberId))
                {
                    var memberName = GetReasonableSenderName(memberId, msg.IsSimulationMode);
                    failureReasons.Add($"{memberName} 已在队伍中");
                    continue;
                }

                // 添加成员
                team.Members.Add(memberId);
                var addedMemberName = GetReasonableSenderName(memberId, msg.IsSimulationMode);
                Log.Warn($"[HandleTeamAdd] 成功添加成员: {addedMemberName} (QQ{memberId})");
                successCount++;
            }

            team.UpdatedAt = DateTime.UtcNow;
            SaveGroupData(msg.GroupId);

            // 生成回执消息
            var replyMessage = new StringBuilder();
            replyMessage.AppendLine($"✓ 队伍 '{defaultTeamName}' 成员添加结果：");
            replyMessage.AppendLine($"  成功添加: {successCount} 人");

            if (failureReasons.Count > 0)
            {
                replyMessage.AppendLine($"  失败: {string.Join(", ", failureReasons)}");
            }

            Reply(replyMessage.ToString().Trim(), msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[队伍管理] 处理 .team add 指令时出错: {ex.Message}");
            Reply("处理队伍添加指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// .team join [队伍名] - 加入队伍并设置为默认队伍
    /// </summary>
    private void HandleTeamJoin(string args, Msg msg)
    {
        var teamName = args.Trim();
        if (string.IsNullOrEmpty(teamName))
        {
            Reply("请指定要加入的队伍：.team join <队伍名>", msg);
            return;
        }

        // 获取群数据
        if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
        {
            Reply("群数据不存在，请先创建队伍。", msg);
            return;
        }

        groupRecord.Teams ??= new Dictionary<string, TeamInfo>();
        groupRecord.UserDefaultTeams ??= new Dictionary<long, string>();

        // 检查队伍是否存在
        if (!groupRecord.Teams.TryGetValue(teamName, out var team))
        {
            Reply($"队伍 '{teamName}' 不存在。", msg);
            return;
        }

        // 检查是否已是成员
        if (team.Members.Contains(msg.UserId))
        {
            Reply($"你已经是队伍 '{teamName}' 的成员，现在将其设置为默认队伍。", msg);
        }
        else
        {
            // 添加为成员
            team.Members.Add(msg.UserId);
            team.UpdatedAt = DateTime.UtcNow;
        }

        // 设置为默认队伍
        groupRecord.UserDefaultTeams[msg.UserId] = teamName;

        SaveGroupData(msg.GroupId);
        Reply($"✓ 已加入队伍 '{teamName}'，并将其设置为默认队伍。", msg);
    }

    /// <summary>
    /// .team del [队伍名] - 删除队伍（仅限白名单用户或队伍创建者）
    /// </summary>
    private void HandleTeamDel(string args, Msg msg)
    {
        var teamName = args.Trim();
        if (string.IsNullOrEmpty(teamName))
        {
            Reply("请指定要删除的队伍：.team del <队伍名>", msg);
            return;
        }

        // 获取群数据
        if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
        {
            Reply("群数据不存在。", msg);
            return;
        }

        groupRecord.Teams ??= new Dictionary<string, TeamInfo>();

        // 检查队伍是否存在
        if (!groupRecord.Teams.TryGetValue(teamName, out var team))
        {
            Reply($"队伍 '{teamName}' 不存在。", msg);
            return;
        }

        // 权限检查：只有白名单等级 <= 1 或队伍创建者可删除
        var authLevel = groupRecord.AuthLevel ?? 3;

        if (authLevel > 1 && team.CreatorId != msg.UserId)
        {
            Reply("只有白名单用户或队伍创建者才能删除队伍。", msg);
            return;
        }

        // 执行删除
        DeleteTeam(msg.GroupId, teamName, groupRecord);
        Reply($"✓ 队伍 '{teamName}' 已删除。", msg);
    }

    /// <summary>
    /// .team call [队伍名] - @队伍所有成员
    /// </summary>
    private void HandleTeamCall(string args, Msg msg)
    {
        var teamName = args.Trim();

        // 获取群数据
        if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
        {
            Reply("群数据不存在，请先创建队伍。", msg);
            return;
        }

        groupRecord.Teams ??= new Dictionary<string, TeamInfo>();
        groupRecord.UserDefaultTeams ??= new Dictionary<long, string>();

        // 如果省略队伍名，使用默认队伍
        if (string.IsNullOrEmpty(teamName))
        {
            if (!groupRecord.UserDefaultTeams.TryGetValue(msg.UserId, out var defaultTeam))
            {
                Reply("未找到默认队伍，请指定队伍名或先加入一个队伍。", msg);
                return;
            }
            teamName = defaultTeam;
        }
        else
        {
            // 如果指定了队伍名，将其设置为默认队伍
            if (groupRecord.Teams.ContainsKey(teamName))
            {
                groupRecord.UserDefaultTeams[msg.UserId] = teamName;
                SaveGroupData(msg.GroupId);
            }
        }

        // 检查队伍是否存在
        if (!groupRecord.Teams.TryGetValue(teamName, out var team))
        {
            Reply($"队伍 '{teamName}' 不存在。", msg);
            return;
        }

        // 构建 @消息
        if (team.Members.Count == 0)
        {
            Reply($"队伍 '{teamName}' 中没有成员。", msg);
            return;
        }

        var mentionList = string.Join(" ", team.Members.Select(id => $"[CQ:at,qq={id}]"));
        var callMessage = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["TeamCallMessage"], teamName, mentionList);

        Reply(callMessage, msg);
    }

    /// <summary>
    /// .team sort [技能名] - 获取默认队伍成员的技能数据并排序
    /// </summary>
    private void HandleTeamSort(string args, Msg msg)
    {
        var skillName = args.Trim();
        if (string.IsNullOrEmpty(skillName))
        {
            Reply("请指定要排序的技能：.team sort <技能名>", msg);
            return;
        }

        // 获取群数据
        if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
        {
            Reply("群数据不存在。", msg);
            return;
        }

        groupRecord.UserDefaultTeams ??= new Dictionary<long, string>();

        // 获取用户的默认队伍
        if (!groupRecord.UserDefaultTeams.TryGetValue(msg.UserId, out var teamName))
        {
            Reply("未找到默认队伍，请先加入一个队伍。", msg);
            return;
        }

        groupRecord.Teams ??= new Dictionary<string, TeamInfo>();

        // 检查队伍是否存在
        if (!groupRecord.Teams.TryGetValue(teamName, out var team))
        {
            Reply($"队伍 '{teamName}' 不存在。", msg);
            return;
        }

        // 收集团队成员的技能数据
        var skillData = new List<(long userId, string userName, int skillValue)>();

        foreach (var memberId in team.Members)
        {
            if (!characterSkills.TryGetValue(memberId, out var userChars))
            {
                continue;
            }

            // 获取用户的当前人物卡
            string? currentCharName = null;
            if (currentRulebookNames.TryGetValue(memberId, out var rulebook))
            {
                // 尝试获取该规则书下的人物卡
                foreach (var charEntry in userChars)
                {
                    currentCharName = charEntry.Key;
                    break;
                }
            }

            if (currentCharName == null && userChars.Count > 0)
            {
                currentCharName = userChars.First().Key;
            }

            if (currentCharName == null || !userChars.TryGetValue(currentCharName, out var charSheet))
            {
                continue;
            }

            // 获取技能值
            int skillValue = 0;
            if (charSheet.Skills != null && charSheet.Skills.TryGetValue(skillName, out var value))
            {
                skillValue = value;
            }

            var userName = GetReasonableSenderName(memberId);
            skillData.Add((memberId, userName, skillValue));
        }

        // 按技能值排序（降序）
        var sorted = skillData.OrderByDescending(x => x.skillValue).ToList();

        // 构建返回消息
        var resultLines = new List<string> { $"队伍 '{teamName}' 技能 '{skillName}' 排序：" };
        int rank = 1;
        foreach (var (userId, userName, skillValue) in sorted)
        {
            resultLines.Add($"{rank}. {userName} ({userId}): {skillValue}");
            rank++;
        }

        Reply(string.Join("\n", resultLines), msg);
    }

    /// <summary>
    /// .team list - 显示当前群的所有队伍和其创建者
    /// </summary>
    private void HandleTeamList(string args, Msg msg)
    {
        try
        {
            // 获取群数据
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                Reply("群中还没有队伍。", msg);
                return;
            }

            groupRecord.Teams ??= new Dictionary<string, TeamInfo>();

            if (groupRecord.Teams.Count == 0)
            {
                Reply("群中还没有队伍。使用 .team new <队伍名> 创建一个队伍。", msg);
                return;
            }

            // 构建队伍列表
            var resultLines = new List<string> { $"【群队伍列表】(共 {groupRecord.Teams.Count} 个)" };

            int index = 1;
            foreach (var (teamName, teamInfo) in groupRecord.Teams)
            {
                string creatorName = GetReasonableSenderName(teamInfo.CreatorId);
                int memberCount = teamInfo.Members?.Count ?? 0;
                string createdAt = teamInfo.CreatedAt.ToString("MM-dd HH:mm");

                resultLines.Add($"{index}. 【{teamName}】");
                resultLines.Add($"   创建者: {creatorName} ({teamInfo.CreatorId})");
                resultLines.Add($"   成员数: {memberCount}");
                resultLines.Add($"   创建时间: {createdAt}");

                index++;
            }

            Reply(string.Join("\n", resultLines), msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[队伍管理] 处理 .team list 指令时出错: {ex.Message}");
            Reply("处理队伍列表指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// .team set [队伍名] - 将默认队伍切换到指定的已存在队伍
    /// </summary>
    private void HandleTeamSet(string args, Msg msg)
    {
        try
        {
            var trimmedArgs = args.Trim();
            if (string.IsNullOrEmpty(trimmedArgs))
            {
                Reply("请指定要切换到的队伍：.team set <队伍名>", msg);
                return;
            }

            // 获取群数据
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                Reply("群数据不存在。", msg);
                return;
            }

            groupRecord.Teams ??= new Dictionary<string, TeamInfo>();
            groupRecord.UserDefaultTeams ??= new Dictionary<long, string>();

            string targetTeamName = trimmedArgs;

            // 检查队伍是否存在
            if (!groupRecord.Teams.TryGetValue(targetTeamName, out var team))
            {
                Reply($"队伍 '{targetTeamName}' 不存在。使用 .team list 查看所有队伍。", msg);
                return;
            }

            // 检查用户是否是队伍成员
            if (!team.Members.Contains(msg.UserId))
            {
                Reply($"您不是队伍 '{targetTeamName}' 的成员，无法切换。请先使用 .team join <队伍名> 加入队伍。", msg);
                return;
            }

            // 设置为默认队伍
            var previousTeam = groupRecord.UserDefaultTeams.TryGetValue(msg.UserId, out var prev) ? prev : null;
            groupRecord.UserDefaultTeams[msg.UserId] = targetTeamName;

            SaveGroupData(msg.GroupId);
            Reply($"✓ 已将默认队伍从 '{previousTeam ?? "无"}' 切换到 '{targetTeamName}'。", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[队伍管理] 处理 .team set 指令时出错: {ex.Message}");
            Reply("处理队伍切换指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 删除队伍（辅助方法）
    /// </summary>
    private void DeleteTeam(long groupId, string teamName, GroupDataRecord groupRecord)
    {
        if (groupRecord.Teams != null && groupRecord.Teams.ContainsKey(teamName))
        {
            groupRecord.Teams.Remove(teamName);

            // 清理用户的默认队伍设置
            if (groupRecord.UserDefaultTeams != null)
            {
                var usersWithDefaultTeam = groupRecord.UserDefaultTeams
                    .Where(kvp => kvp.Value == teamName)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var userId in usersWithDefaultTeam)
                {
                    groupRecord.UserDefaultTeams.Remove(userId);
                }
            }

            SaveGroupData(groupId);
        }
    }

    /// <summary>
    /// 从@形式的cq码或qq号中提取用户ID
    /// 支持格式：[CQ:at,qq=123456789] 或 123456789
    /// </summary>
    private long ExtractUserIdFromMention(string mention)
    {
        mention = mention.Trim();

        // 调试日志：打印接收到的原始内容
        Log.Error($"[ExtractUserIdFromMention] 接收到的原始字符串: '{mention}'");

        // 检查 CQ 码格式 - 匹配 [CQ:at,qq=123456789]
        var cqMatch = Regex.Match(mention, @"\[CQ:at,qq=(\d+)\]");
        if (cqMatch.Success && long.TryParse(cqMatch.Groups[1].Value, out var id))
        {
            Log.Error($"[ExtractUserIdFromMention] CQ码匹配成功，QQ号: {id}");
            return id;
        }

        // 如果 CQ 码格式失败，尝试更宽松的匹配（处理可能的格式变化）
        // 比如格式可能是 [CQ:at qq=123456789] 或其他变体
        var looseMatch = Regex.Match(mention, @"\[CQ:at[^\]]*qq=(\d+)\]");
        if (looseMatch.Success && long.TryParse(looseMatch.Groups[1].Value, out var looseId))
        {
            Log.Error($"[ExtractUserIdFromMention] 宽松CQ码匹配成功，QQ号: {looseId}");
            return looseId;
        }

        // 检查纯数字qq号
        if (long.TryParse(mention, out var qqId))
        {
            Log.Error($"[ExtractUserIdFromMention] 纯数字QQ号匹配成功，QQ号: {qqId}");
            return qqId;
        }

        // 如果都失败了，尝试从字符串中提取数字（容错处理）
        var digitMatch = Regex.Match(mention, @"(\d{5,})");//至少5位数字，以排除其他无关数字
        if (digitMatch.Success && long.TryParse(digitMatch.Groups[1].Value, out var fallbackId))
        {
            Log.Error($"[ExtractUserIdFromMention] 数字提取匹配成功，QQ号: {fallbackId}");
            return fallbackId;
        }

        Log.Error($"[ExtractUserIdFromMention] 无法解析用户提及: '{mention}'");
        return -1;
    }

    /// <summary>
    /// 从输入字符串中提取所有成员ID（支持多个 @ 或 QQ 号，用空格分隔）
    /// </summary>
    private List<long> ExtractAllUserIdsFromMentions(string input)
    {
        var memberIds = new List<long>();
        input = input.Trim();

        Log.Warn($"[ExtractAllUserIdsFromMentions] 接收到的原始字符串: '{input}'");

        // 分割输入字符串，支持空格分隔
        var mentions = input.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var mention in mentions)
        {
            long memberId = ExtractUserIdFromMention(mention);
            if (memberId > 0)
            {
                // 避免重复添加
                if (!memberIds.Contains(memberId))
                {
                    memberIds.Add(memberId);
                    Log.Warn($"[ExtractAllUserIdsFromMentions] 成功提取成员ID: {memberId}");
                }
                else
                {
                    Log.Warn($"[ExtractAllUserIdsFromMentions] 成员ID {memberId} 重复，跳过");
                }
            }
        }

        Log.Warn($"[ExtractAllUserIdsFromMentions] 提取的成员ID列表: {string.Join(", ", memberIds)}");
        return memberIds;
    }

    /// <summary>
    /// .en 指令 - 技能增长
    /// 支持格式：.en [技能名] [技能值] [技能名] [技能值]... 或 .en [技能名] [技能值] # [重复次数]
    /// </summary>
    private void HandleEnCommand(string args, Msg msg)
    {
        long userId = msg.UserId;
        string rawInput = args.Trim();

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            Reply("格式: .en <技能名> [技能值] [技能名] [技能值]... 或 .en <技能名> #[重复次数]\n示例: .en 刀术 65 # 投掷\n示例: .en 心理学 70 侦查", msg);
            return;
        }

        // 获取当前使用的人物卡
        if (!characterSkills.TryGetValue(userId, out var userCharacters) || userCharacters.Count == 0)
        {
            Reply("未找到人物卡，请先创建人物卡。", msg);
            return;
        }

        string? currentCharacterName = null;
        if (CurrentCharacterNames.TryGetValue(userId, out var name))
        {
            currentCharacterName = name;
        }

        if (string.IsNullOrEmpty(currentCharacterName) || !userCharacters.TryGetValue(currentCharacterName, out var sheet))
        {
            Reply("未找到当前使用的人物卡。", msg);
            return;
        }

        var skillsDict = sheet.Skills;

        // 状态追踪
        string? lastSkillName = null;
        int lastSkillValue = 0;
        List<string> results = new();

        // 分割输入：支持空格分割，但数字时连续读取
        int index = 0;
        while (index < rawInput.Length)
        {
            // 跳过前导空格
            while (index < rawInput.Length && char.IsWhiteSpace(rawInput[index]))
            {
                index++;
            }

            if (index >= rawInput.Length) break;

            // 检测 # 符号（特殊符号，用于连续增长）
            if (rawInput[index] == '#')
            {
                if (lastSkillName == null)
                {
                    results.Add("错误: # 符号必须在技能之后");
                    break;
                }

                index++; // 跳过 #

                // 读取 # 后的数字（至多1位）
                int repeatCount = 1;
                if (index < rawInput.Length && char.IsDigit(rawInput[index]))
                {
                    repeatCount = int.Parse(rawInput[index].ToString());
                    index++;
                }

                // 重复增长
                for (int i = 0; i < repeatCount; i++)
                {
                    var (resultMsg, newValue) = ProcessSkillGrowth(lastSkillName, lastSkillValue, skillsDict);
                    results.Add(resultMsg);
                    lastSkillValue = newValue;
                }

                continue;
            }

            // 读取一节内容（直到空格或字符串结束）
            int startIdx = index;
            bool hasDigit = false;
            bool hasLetter = false;

            while (index < rawInput.Length && !char.IsWhiteSpace(rawInput[index]) && rawInput[index] != '#')
            {
                char ch = rawInput[index];
                if (char.IsDigit(ch))
                {
                    hasDigit = true;
                }
                else if (char.IsLetter(ch) || ch == '汉' || (ch >= '\u4e00' && ch <= '\u9fff'))
                {
                    hasLetter = true;
                }
                index++;
            }

            string segment = rawInput.Substring(startIdx, index - startIdx).Trim();
            if (string.IsNullOrEmpty(segment)) continue;

            // 处理逻辑：
            // 1. 如果只有数字 -> 临时增长（不保存）
            // 2. 如果有字母/汉字 -> 技能名
            // 3. 如果既有字母又有数字 -> 技能名+值组合

            if (hasDigit && !hasLetter)
            {
                // 只有数字：临时增长
                if (int.TryParse(segment, out var tempValue))
                {
                    var (resultMsg, newValue) = ProcessSkillGrowth(lastSkillName, tempValue, skillsDict, isTemporal: true);
                    results.Add(resultMsg);
                    lastSkillValue = newValue;
                }
            }
            else if (hasLetter && !hasDigit)
            {
                // 只有字母/汉字：技能名
                lastSkillName = segment;
                var (resultMsg, newValue) = ProcessSkillGrowth(lastSkillName, GetOrDefaultSkillValue(lastSkillName, skillsDict), skillsDict);
                results.Add(resultMsg);
                lastSkillValue = newValue;
            }
            else if (hasLetter && hasDigit)
            {
                // 既有字母又有数字：解析出技能名和值
                // 从右到左查找数字段
                int digitStartIdx = segment.Length - 1;
                while (digitStartIdx >= 0 && !char.IsDigit(segment[digitStartIdx]))
                {
                    digitStartIdx--;
                }

                if (digitStartIdx >= 0)
                {
                    // 找到最左边的连续数字段的开始
                    int digitEndIdx = digitStartIdx;
                    while (digitStartIdx > 0 && char.IsDigit(segment[digitStartIdx - 1]))
                    {
                        digitStartIdx--;
                    }

                    string skillName = segment.Substring(0, digitStartIdx).Trim();
                    string valueStr = segment.Substring(digitStartIdx);

                    if (int.TryParse(valueStr, out var skillValue))
                    {
                        lastSkillName = skillName;
                        var (resultMsg, newValue) = ProcessSkillGrowth(lastSkillName, skillValue, skillsDict);
                        results.Add(resultMsg);
                        lastSkillValue = newValue;
                    }
                }
            }
        }

        // 保存人物卡
        SaveCharacterSkills();

        // 发送结果
        string finalResult = string.Join("\n", results);
        Reply(finalResult, msg);
    }

    /// <summary>
    /// 获取技能值或默认为 0
    /// </summary>
    private int GetOrDefaultSkillValue(string skillName, ConcurrentDictionary<string, int> skillsDict)
    {
        if (skillsDict.TryGetValue(skillName, out var value))
        {
            return value;
        }
        return 0;
    }

    /// <summary>
    /// 处理技能增长逻辑
    /// </summary>
    private (string resultMessage, int newValue) ProcessSkillGrowth(
        string? skillName,
        int skillValue,
        ConcurrentDictionary<string, int> skillsDict,
        bool isTemporal = false)
    {
        if (string.IsNullOrEmpty(skillName))
        {
            skillName = "未知技能";
        }

        // 投掷 D100
        var d100Roll = Dice.Roll("1d100");
        if (!d100Roll.Success)
        {
            return ("D100 投掷失败", 0);
        }
        int d100Result = d100Roll.Rolls.First();

        // 判断是否成功：D100 > 95 或 D100 > 技能值
        bool success = d100Result > 95 || d100Result > skillValue;

        string resultMsg;
        int newValue = skillValue;

        if (success)
        {
            // 投掷 D10 并增加技能值
            var d10Roll = Dice.Roll("1d10");
            if (!d10Roll.Success)
            {
                return ("D10 投掷失败", skillValue);
            }
            int d10Result = d10Roll.Rolls.First();
            newValue = skillValue + d10Result;

            if (!isTemporal)
            {
                // 保存到人物卡
                skillsDict.AddOrUpdate(skillName, newValue, (k, v) => newValue);
                resultMsg = $"【{skillName}】增长成功！\nD100: {d100Result} > {skillValue}\nD10: {d10Result}\n{skillValue} → {newValue}";
            }
            else
            {
                // 临时增长，不保存
                resultMsg = $"【{skillName}】(临时)增长成功！\nD100: {d100Result} > {skillValue}\nD10: {d10Result}\n{skillValue} → {newValue} (不保存)";
            }
        }
        else
        {
            resultMsg = $"【{skillName}】增长失败。\nD100: {d100Result} ≤ {skillValue}";
        }

        return (resultMsg, newValue);
    }

    /// <summary>
    /// 处理已弃用的 .ra 指令（属性检定）
    /// 内部调用 HandleCostomCheck，但在结果前添加弃用提示
    /// </summary>
    private void HandleRaCommand(string args, Msg msg)
    {
        // 发送弃用提示
        string deprecationTip = "Tip: .ra/.rc 指令在后续更新中将不受支持，请使用 .cc 指令进行通用检定";
        Reply(deprecationTip, msg);

        // 修改消息内容，将 .ra 替换为 .cc
        string originalContent = msg.Content;
        msg.Content = msg.Content.Replace(".ra", ".cc", StringComparison.OrdinalIgnoreCase);

        // 调用标准的 .cc 处理函数
        HandleCostomCheck(args, msg);

        // 恢复原始内容（防止影响其他处理）
        msg.Content = originalContent;
    }

    /// <summary>
    /// 替换 cardname 模板中的 {技能名} 占位符为实际技能值
    /// 直接搜索闭合大括号对，无需正则表达式
    /// </summary>
    private string ReplaceCardNamePlaceholders(string template, long userId)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        StringBuilder result = new StringBuilder();
        int pos = 0;

        while (pos < template.Length)
        {
            int openBrace = template.IndexOf('{', pos);
            if (openBrace == -1)
            {
                // 没有更多的大括号，追加剩余文本
                result.Append(template.Substring(pos));
                break;
            }

            // 追加开括号前的文本
            result.Append(template.Substring(pos, openBrace - pos));

            // 查找闭合大括号
            int closeBrace = template.IndexOf('}', openBrace);
            if (closeBrace == -1)
            {
                // 没有闭合大括号，把开括号原样输出并继续
                result.Append('{');
                pos = openBrace + 1;
                continue;
            }

            // 提取技能名
            string skillName = template.Substring(openBrace + 1, closeBrace - openBrace - 1).Trim();

            // 查找用户当前角色卡并替换技能值
            string skillValue = "N/A";
            if (characterSkills.TryGetValue(userId, out var userCards) && userCards.Count > 0)
            {
                // 获取当前选中的角色卡（假设使用 userDefaultCharacterNames 或遍历第一张卡）
                var currentCard = userCards.Values.FirstOrDefault();
                if (currentCard?.Skills != null && currentCard.Skills.TryGetValue(skillName, out var skill))
                {
                    skillValue = skill.ToString();
                }
            }

            result.Append(skillValue);
            pos = closeBrace + 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// 处理 .cn 指令（仿名片）
    /// 子命令：
    /// .cn - 查询当前模板
    /// .cn set [文本] - 设置模板
    /// .cn on - 启用自动同步（需要机器人有群管理员权限）
    /// .cn off - 禁用自动同步
    /// </summary>
    private void HandleCardNameCommand(string args, Msg msg)
    {
        string trimmedArgs = (args ?? string.Empty).Trim();
        string switchKey = $"{msg.UserId}_{msg.GroupId}";

        try
        {
            // 子命令：查询当前模板
            if (string.IsNullOrEmpty(trimmedArgs))
            {
                if (cardNameTemplates.TryGetValue(msg.UserId, out var template) && !string.IsNullOrWhiteSpace(template))
                {
                    string resolvedTemplate = ReplaceCardNamePlaceholders(template, msg.UserId);
                    Reply($"当前仿名片模板：{template}\n转义后：{resolvedTemplate}", msg);
                }
                else
                {
                    Reply("尚未为你设置仿名片模板。使用 .cn set [文本] 来设置。", msg);
                }
                return;
            }

            // 子命令：set [文本]
            if (trimmedArgs.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            {
                string templateText = trimmedArgs.Substring(4).Trim();

                if (string.IsNullOrWhiteSpace(templateText))
                {
                    Reply("模板文本不能为空。", msg);
                    return;
                }

                if (templateText.Length > 256)
                {
                    Reply("模板过长，请限制在 256 个字符以内。", msg);
                    return;
                }

                cardNameTemplates[msg.UserId] = templateText;
                SaveUserData(msg.UserId);

                string resolvedTemplate = ReplaceCardNamePlaceholders(templateText, msg.UserId);
                Reply($"✓ 已设置仿名片模板：{templateText}\n转义后示例：{resolvedTemplate}", msg);
                return;
            }

            // 子命令：on（需要检查机器人在该群的管理员权限）
            if (trimmedArgs.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.Source != MessageSource.group)
                {
                    Reply("仿名片功能仅支持群聊。", msg);
                    return;
                }

                // 检查机器人是否有管理员权限
                MessageDistribution?.CheckBotGroupPermission(msg.GroupId, (hasPermission) =>
                {
                    if (!hasPermission)
                    {
                        Reply("机器人需要在该群拥有管理员权限才能启用此功能。请联系群管理员给予机器人管理员权限。", msg);
                        return;
                    }

                    cardNameSwitches[switchKey] = true;
                    SaveUserData(msg.UserId);
                    Reply("✓ 已启用仿名片自动同步功能。机器人将在你发送消息时自动更新群名片。", msg);
                });
                return;
            }

            // 子命令：off
            if (trimmedArgs.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                cardNameSwitches[switchKey] = false;
                SaveUserData(msg.UserId);
                Reply("✓ 已禁用仿名片自动同步功能。", msg);
                return;
            }

            // 未识别的子命令
            Reply("未识别的子命令。用法：.cn | .cn set [文本] | .cn on | .cn off", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 处理 cn 指令时发生错误: {ex.Message}");
            Reply("处理仿名片指令时发生内部错误。", msg);
        }
    }

    /// <summary>
    /// 处理已弃用的 .rc 指令（角色检定）
    /// 内部调用 HandleCostomCheck，但在结果前添加弃用提示
    /// </summary>
    private void HandleRcCommand(string args, Msg msg)
    {
        // 发送弃用提示
        string deprecationTip = "Tip: .ra/.rc 指令在后续更新中将不受支持，请使用 .cc 指令进行通用检定";
        Reply(deprecationTip, msg);

        // 修改消息内容，将 .rc 替换为 .cc
        string originalContent = msg.Content;
        msg.Content = msg.Content.Replace(".rc", ".cc", StringComparison.OrdinalIgnoreCase);

        // 调用标准的 .cc 处理函数
        HandleCostomCheck(args, msg);

        // 恢复原始内容（防止影响其他处理）
        msg.Content = originalContent;
    }

    /// <summary>
    /// 处理转发消息格式 JSON
    /// 格式: {"__forward_message":true,"contents":["segment1","segment2",...]}
    /// </summary>
    private void HandleForwardMessageFormat(string jsonStr, Msg msg)
    {
        try
        {
            // 简单的 JSON 解析（提取 contents 数组）
            // 正则表达式提取 contents 值："contents":\["...", "...", ...]
            var match = System.Text.RegularExpressions.Regex.Match(
                jsonStr,
                @"""contents""\s*:\s*\[(.*?)\]\s*\}",
                System.Text.RegularExpressions.RegexOptions.Singleline
            );

            if (!match.Success)
            {
                Log.Warn("[转发消息] 无法从 JSON 中提取 contents，降级为普通消息");
                Reply(jsonStr, msg);
                return;
            }

            var contentStr = match.Groups[1].Value;
            var contents = new List<string>();

            // 简单的字符串提取：按 "..." 分割
            var parts = System.Text.RegularExpressions.Regex.Matches(
                contentStr,
                @"""([^""\\]*(?:\\.[^""\\]*)*)""",
                System.Text.RegularExpressions.RegexOptions.Singleline
            );

            foreach (System.Text.RegularExpressions.Match part in parts)
            {
                // 反转义字符串
                string decoded = part.Groups[1].Value
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\")
                    .Replace("\\n", "\n")
                    .Replace("\\r", "\r");
                contents.Add(decoded);
            }

            if (contents.Count == 0)
            {
                Log.Warn("[转发消息] 解析出的内容为空，降级为普通消息");
                Reply(jsonStr, msg);
                return;
            }

            Log.Normal($"[转发消息] 成功解析 {contents.Count} 个转发消息段");

            // 调用 MessageDistribution.ReplyForward
            if (MessageDistribution != null)
            {
                var forwardEntries = new List<(string timestamp, long userId, string senderName, string content)>();

                foreach (var content in contents)
                {
                    forwardEntries.Add((
                        DateTime.Now.ToString("HH:mm:ss"),
                        1001,  // 系统账号
                        "[ABot]",
                        content
                    ));
                }

                MessageDistribution.ReplyForward(forwardEntries, msg);
                Log.Normal($"[转发消息] 已发送 {forwardEntries.Count} 条转发消息");
            }
            else
            {
                Log.Error("[转发消息] MessageDistribution 未初始化，无法发送转发消息，降级为普通消息");
                // 降级：合并所有内容并以普通消息发送
                Reply(string.Join("\n---\n", contents), msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[转发消息] 处理失败: {ex.Message}，降级为普通消息");
            Reply(jsonStr, msg);
        }
    }

    /// <summary>
    /// 处理抽牌命令 (.draw 和 .drawx)
    /// </summary>
    private void HandleDrawCommand(string args, Msg msg)
    {
        try
        {
            // 解析命令参数，区分 .draw 和 .drawx
            bool isDrawX = args.StartsWith("x");
            string actualArgs = isDrawX ? args.Substring(1).Trim() : args;
            
            // 解析牌堆名，支持 .draw键值 格式（空格可选）
            string deckName = ParseDeckName(actualArgs);
            
            if (string.IsNullOrEmpty(deckName))
            {
                Reply("请指定要抽牌的牌堆：.draw 牌堆名 或 .drawx 牌堆名", msg);
                return;
            }
            
            if (deckName.StartsWith("_"))
            {
                Reply("无法从隐藏牌堆中抽牌。", msg);
                return;
            }
            
            // 获取群数据记录
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                groupRecord = new GroupDataRecord { GroupId = msg.GroupId };
                groupDataRecords[msg.GroupId] = groupRecord;
            }
            
            // 确保临时牌堆字典存在
            groupRecord.TemporaryDecks ??= new Dictionary<string, List<string>>();
            
            List<string> targetDeck;
            bool isTemporaryDeck = false;
            
            // 优先检查临时牌堆
            if (groupRecord.TemporaryDecks.TryGetValue(deckName, out var tempDeck))
            {
                targetDeck = tempDeck;
                isTemporaryDeck = true;
            }
            else if (DeckSet.defaultPublicDeck.TryGetValue(deckName, out var publicDeck))
            {
                targetDeck = publicDeck;
            }
            else
            {
                Reply($"牌堆 '{deckName}' 不存在。使用 .decklist 查看可用牌堆。", msg);
                return;
            }
            
            // 检查牌堆是否为空
            if (targetDeck.Count == 0)
            {
                if (isTemporaryDeck)
                {
                    // 删除空的临时牌堆
                    groupRecord.TemporaryDecks.Remove(deckName);
                    SaveGroupData(msg.GroupId);
                }
                Reply($"牌堆 '{deckName}' 已空。", msg);
                return;
            }
            
            // 随机抽取一张牌
            Random random = new Random();
            int selectedIndex = random.Next(targetDeck.Count);
            string selectedCard = targetDeck[selectedIndex];
            
            // 解析并替换 {牌堆名}/{%牌堆名} 占位符
            selectedCard = ExpandDeckPlaceholders(selectedCard, msg);
            
            // 构建回复消息
            string response = $"从牌堆 '{deckName}' 抽出: {selectedCard}";
            
            // 根据命令类型处理牌
            if (isDrawX)
            {
                // 抽出不放回 - 仅对临时牌堆操作
                if (!isTemporaryDeck)
                {
                    // 创建临时牌堆副本
                    var tempDeckCopy = new List<string>(DeckSet.defaultPublicDeck[deckName]);
                    tempDeckCopy.RemoveAt(selectedIndex);
                    groupRecord.TemporaryDecks[deckName] = tempDeckCopy;
                    SaveGroupData(msg.GroupId);
                    response += " (已创建临时牌堆，牌不放回)";
                }
                else
                {
                    // 从现有临时牌堆中移除
                    targetDeck.RemoveAt(selectedIndex);
                    SaveGroupData(msg.GroupId);
                    response += " (牌不放回)";
                    
                    // 检查是否需要删除空牌堆
                    if (targetDeck.Count == 0)
                    {
                        groupRecord.TemporaryDecks.Remove(deckName);
                    }
                }
            }
            // .draw 命令总是放回，无需修改牌堆
            
            Reply(response, msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .draw 指令时出错: {ex.Message}");
            Reply("处理抽牌指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 .jrrp 指令（强制抽取隐藏牌堆“_抽签动画”）
    /// </summary>
    private void HandleJrrpCommand(string args, Msg msg)
    {
        try
        {
            string refinedMessage = RefineMsg("<deck _今日运势>", msg);
            Reply(refinedMessage, msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .jrrp 指令时出错: {ex.Message}");
            Reply("处理 .jrrp 指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 .ww 指令——双重十字骰的加骰检定
    /// 格式: .ww&lt;骰子数&gt; [a&lt;加骰阈值&gt;] [+/-n]
    /// 例如: .ww10 a9（投掷10个d10，出目≥9为加骰触发条件）
    ///      .ww10 a9 +5（投掷后额外加5成功）
    /// 加骰阈值默认为8，有效范围8-10，超过10则忽略设置（回退到默认8）
    /// 
    /// 检定规则：
    /// 1. 投掷N个d10，出目8/9/10计为成功度，≥加骰阈值计为加骰数
    /// 2. 若存在加骰数，用加骰数个d10进入下一轮
    /// 3. 成功度累加，加骰数每轮重置
    /// 4. 最多15轮，达到则触发彩蛋终止
    /// 5. 可使用 +n 或 -n 修饰成功数
    /// </summary>
    private void HandleWwRoll(string args, Msg msg)
    {
        try
        {
            // === 解析参数 ===
            // 格式: 数字部分 + a数字部分（可选） + +/-n（可选）
            string input = (args ?? "").Trim();
            if (string.IsNullOrEmpty(input))
            {
                Reply("格式: .ww&lt;骰子数&gt; [a&lt;加骰阈值&gt;] [+/-n]\n示例: .ww10 或 .ww8a9 或 .ww10a9+5", msg);
                return;
            }

            // 首先检查是否有 +/- 修饰符
            int successModifier = 0;
            string modifierStr = "";
            int modIdx = input.LastIndexOfAny(new[] { '+', '-' });
            
            if (modIdx > 0)
            {
                // 检查是否是有效的修饰符（+/- 后面跟数字）
                modifierStr = input.Substring(modIdx).Trim();
                if (Regex.IsMatch(modifierStr, @"^[+-]\d+$"))
                {
                    // 有效的修饰符，提取出来
                    if (int.TryParse(modifierStr, out successModifier))
                    {
                        input = input.Substring(0, modIdx).Trim();
                    }
                }
            }

            // 解析加骰阈值（a后面的数字）
            int addThreshold = 8; // 默认加骰阈值
            int addIdx = input.IndexOf('a', StringComparison.OrdinalIgnoreCase);
            string diceCountStr;
            if (addIdx >= 0)
            {
                string thresholdStr = input.Substring(addIdx + 1).Trim();
                if (int.TryParse(thresholdStr, out int parsedThreshold))
                {
                    // 大于10时忽略此设置
                    if (parsedThreshold >= 8 && parsedThreshold <= 10)
                        addThreshold = parsedThreshold;
                }
                diceCountStr = input.Substring(0, addIdx).Trim();
            }
            else
            {
                diceCountStr = input;
            }

            if (!int.TryParse(diceCountStr, out int diceCount) || diceCount <= 0)
            {
                Reply("骰子数应为正整数。格式: .ww&lt;骰子数&gt; [a加骰阈值] [+/-n]", msg);
                return;
            }

            if (diceCount > 999)
            {
                Reply("骰子数过大，建议不超过999。", msg);
                return;
            }

            // === 执行检定 ===
            const int maxRounds = 15;
            int totalSuccess = 0;     // 累计成功度
            int currentDice = diceCount; // 本轮投掷数
            var roundDetails = new List<string>();

            for (int round = 1; round <= maxRounds; round++)
            {
                // 投掷 currentDice 个 d10
                var rollResult = Dice.Roll($"{currentDice}d10");
                if (!rollResult.Success)
                {
                    Reply($"第{round}轮掷骰失败: {rollResult.Detail}", msg);
                    return;
                }

                // 统计成功数（出目 >= 8: 即8,9,10）和加骰数（出目 >= 加骰阈值）
                int roundSuccess = 0;
                int roundAddDice = 0;
                foreach (int val in rollResult.Rolls)
                {
                    if (val >= 8) roundSuccess++;
                    if (val >= addThreshold) roundAddDice++;
                }

                totalSuccess += roundSuccess;

                // 根据单轮骰子数量选择显示格式
                // 如果骰子太多（>25个），改为按出目频次统计显示，避免刷屏
                string rollValuesStr;
                if (currentDice > 25)
                {
                    // 统计1~10每个数字的出现次数
                    var freq = new int[11]; // 索引1-10有效，索引0不用
                    foreach (int val in rollResult.Rolls)
                        freq[val]++;

                    var freqParts = new List<string>();
                    for (int v = 1; v <= 10; v++)
                    {
                        if (freq[v] > 0)
                            freqParts.Add($"{v}:{freq[v]}次");
                    }
                    rollValuesStr = string.Join(" ", freqParts);
                }
                else
                {
                    // 骰子数 <= 25 时逐个显示
                    rollValuesStr = string.Join(", ", rollResult.Rolls);
                }
                roundDetails.Add($"第{round}轮: [{rollValuesStr}] 成功+{roundSuccess} 加骰{roundAddDice}");

                // 检查是否触发彩蛋（达到15轮）
                if (round >= maxRounds)
                {
                    string easterEgg = GlobalFeedbackMessages.FeedbackTemplates.TryGetValue("WwRollLimitReached", out var egg)
                        ? egg
                        : "骰子都快被你扔光了，回路都快过载了！适可而止啊！#ﾟÅﾟ）⊂彡☆))ﾟДﾟ)･∵";
                    roundDetails.Add(easterEgg);
                    break;
                }

                currentDice = roundAddDice;

                // 加骰数为0，结束
                if (currentDice == 0)
                    break;
            }

            // === 应用成功数修饰符 ===
            int finalSuccess = Math.Max(0, totalSuccess + successModifier);
            string modifierDisplay = successModifier != 0 ? $" {(successModifier > 0 ? "+" : "")}{successModifier}" : "";

            // === 构建回复 ===
            var sb = new StringBuilder();
            sb.AppendLine($"投掷{diceCount}d10 {(addThreshold == 8 ? "" : $"加骰阈值≥{addThreshold}")}");
            sb.AppendLine(string.Join("\n", roundDetails));
            sb.AppendLine($"━━━━━━━━━━━━━━━━");
            sb.Append($"总计成功度: {totalSuccess}{modifierDisplay} = {finalSuccess}");

            Reply(sb.ToString(), msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[WwRoll] 处理 .ww 指令时出错: {ex.Message}");
            Reply("处理 .ww 指令时发生内部错误。", msg);
        }
    }

    /// <summary>
    /// 解析牌堆名，支持 .draw键值 格式（空格可选）
    /// </summary>
    private string ParseDeckName(string args)
    {
        if (string.IsNullOrEmpty(args))
            return string.Empty;
        
        // 移除前导空格
        args = args.Trim();
        
        // 如果第一个字符不是空格，可能是 .draw键值 格式
        if (!args.StartsWith(" "))
        {
            return args;
        }
        
        // 移除前导空格后返回
        return args.Trim();
    }

    /// <summary>
    /// 处理牌堆列表命令 (.decklist)
    /// </summary>
    private void HandleDeckListCommand(string args, Msg msg)
    {
        try
        {
            // 解析页码参数
            int page = 1;
            if (!string.IsNullOrEmpty(args) && int.TryParse(args.Trim(), out int parsedPage))
            {
                page = Math.Max(1, parsedPage);
            }
            
            const int itemsPerPage = 30;
            
            // 获取公共牌堆列表
            var publicDeckNames = DeckSet.defaultPublicDeck.Keys.Where(k => !k.StartsWith("_")).ToList();
            publicDeckNames.Sort(); // 按名称排序
            
            // 计算分页
            int totalItems = publicDeckNames.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / itemsPerPage);
            page = Math.Min(page, totalPages);
            
            int startIndex = (page - 1) * itemsPerPage;
            int endIndex = Math.Min(startIndex + itemsPerPage, totalItems);
            
            // 构建公共牌堆列表
            var resultLines = new List<string>();
            var pageDeckNames = new List<string>();
            for (int i = startIndex; i < endIndex; i++)
            {
                pageDeckNames.Add($"【{publicDeckNames[i]}】");
            }
            resultLines.Add($"【公共牌堆列表】(第{page}/{totalPages}页，共{totalItems}个)");
            resultLines.Add(string.Join("", pageDeckNames));
            
            // 获取当前群的临时牌堆
            if (groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord) && 
                groupRecord.TemporaryDecks?.Count > 0)
            {
                var visibleTempDecks = groupRecord.TemporaryDecks.Keys.Where(k => !k.StartsWith("_")).OrderBy(k => k).ToList();
                if (visibleTempDecks.Count > 0)
                {
                    resultLines.Add($"\n当前群临时牌堆:");
                    foreach (var tempDeckName in visibleTempDecks)
                    {
                        var remainingCount = groupRecord.TemporaryDecks[tempDeckName].Count;
                        resultLines.Add($"【{tempDeckName}】(剩余{remainingCount}张)");
                    }
                }
                else
                {
                    resultLines.Add($"\n当前群临时牌堆: 无");
                }
            }
            else
            {
                resultLines.Add($"\n当前群临时牌堆: 无");
            }
            
            // 如果有多页，添加翻页提示
            if (totalPages > 1)
            {
                resultLines.Add($"\n使用 .decklist {page + 1} 查看下一页");
            }
            
            Reply(string.Join("\n", resultLines), msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .decklist 指令时出错: {ex.Message}");
            Reply("处理牌堆列表指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理牌堆管理命令 (.deck)
    /// </summary>
    private void HandleDeckCommand(string args, Msg msg)
    {
        try
        {
            if (string.IsNullOrEmpty(args))
            {
                Reply("牌堆管理命令:\n.deck list - 查看可用牌堆列表\n.deck add 牌堆名 牌1 牌2 牌3... - 添加牌到临时牌堆（包含模板牌堆）\n.deck new 牌堆名 牌1 牌2 牌3... - 强制创建新临时牌堆\n.deck clear [牌堆名] - 清空当前群的指定临时牌堆\n.deck clearall - 清空当前群所有临时牌堆\n.deck reset 牌堆名 - 删除指定临时牌堆", msg);
                return;
            }
            
            // 提取子命令（字母序列）和剩余参数，空格可省略
            var match = Regex.Match(args.Trim(), @"^([a-zA-Z]+)\s*(.*)$");
            if (!match.Success)
            {
                Reply("请指定子命令。使用 .deck 查看帮助。", msg);
                return;
            }
            string subCommand = match.Groups[1].Value.ToLower();
            string subArgs = match.Groups[2].Value.Trim();
            
            if (subCommand == "list")
            {
                HandleDeckListCommand(subArgs, msg);
            }
            else if (subCommand == "add")
            {
                HandleDeckAddCommand(subArgs, msg);
            }
            else if (subCommand == "new")
            {
                HandleDeckNewCommand(subArgs, msg);
            }
            else if (subCommand == "clear")
            {
                HandleDeckClearCommand(subArgs, msg);
            }
            else if (subCommand == "clearall")
            {
                HandleDeckClearAllCommand(msg);
            }
            else if (subCommand == "reset")
            {
                HandleDeckResetCommand(subArgs, msg);
            }
            else
            {
                // 查询 Mod 注册的 .deck 子指令
                if (_modEventBridge != null)
                {
                    foreach (var provider in _modEventBridge.GetSubcommandProviders())
                    {
                        var result = provider.HandleSubcommand("deck", subCommand, subArgs, msg);
                        if (result != null) { Reply(result, msg); return; }
                    }
                }
                Reply("未知的子命令。支持: list, add, new, clear, clearall, reset", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .deck 指令时出错: {ex.Message}");
            Reply("处理牌堆管理指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 .deck add 命令
    /// </summary>
    private void HandleDeckAddCommand(string args, Msg msg)
    {
        try
        {
            if (string.IsNullOrEmpty(args))
            {
                Reply("请指定牌堆名和要添加的牌：.deck add 牌堆名 牌1 牌2 牌3...", msg);
                return;
            }
            
            var parts = args.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                Reply("请指定要添加的牌：.deck add 牌堆名 牌1 牌2 牌3...", msg);
                return;
            }
            
            string deckName = parts[0];
            if (deckName.StartsWith("_"))
            {
                Reply("无法修改或创建隐藏牌堆。", msg);
                return;
            }
            string cardsText = parts[1];
            var cardsToAdd = ParseCards(cardsText);
            
            if (cardsToAdd.Count == 0)
            {
                Reply("没有有效的牌可以添加。", msg);
                return;
            }
            
            // 获取或创建群数据记录
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                groupRecord = new GroupDataRecord { GroupId = msg.GroupId };
                groupDataRecords[msg.GroupId] = groupRecord;
            }
            
            groupRecord.TemporaryDecks ??= new Dictionary<string, List<string>>();
            
            // 获取或创建临时牌堆
            if (!groupRecord.TemporaryDecks.TryGetValue(deckName, out var tempDeck))
            {
                tempDeck = new List<string>();
                groupRecord.TemporaryDecks[deckName] = tempDeck;
            }
            
            // 如果存在模板牌堆，添加模板牌堆中的所有牌
            int templateCardCount = 0;
            if (DeckSet.defaultPublicDeck.TryGetValue(deckName, out var templateDeck))
            {
                tempDeck.AddRange(templateDeck);
                templateCardCount = templateDeck.Count;
            }
            
            // 添加用户指定的牌
            tempDeck.AddRange(cardsToAdd);
            
            SaveGroupData(msg.GroupId);
            
            string response = $"已向临时牌堆 '{deckName}' 添加 {cardsToAdd.Count} 张牌";
            if (templateCardCount > 0)
            {
                response += $"（包含模板牌堆的 {templateCardCount} 张牌）";
            }
            response += $"，当前共有 {tempDeck.Count} 张牌。";
            
            Reply(response, msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .deck add 指令时出错: {ex.Message}");
            Reply("处理添加牌指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 .deck new 命令
    /// </summary>
    private void HandleDeckNewCommand(string args, Msg msg)
    {
        try
        {
            if (string.IsNullOrEmpty(args))
            {
                Reply("请指定牌堆名和要添加的牌：.deck new 牌堆名 牌1 牌2 牌3...", msg);
                return;
            }
            
            var parts = args.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                Reply("请指定要添加的牌：.deck new 牌堆名 牌1 牌2 牌3...", msg);
                return;
            }
            
            string deckName = parts[0];
            if (deckName.StartsWith("_"))
            {
                Reply("无法创建隐藏牌堆。", msg);
                return;
            }
            string cardsText = parts[1];
            var cardsToAdd = ParseCards(cardsText);
            
            if (cardsToAdd.Count == 0)
            {
                Reply("没有有效的牌可以添加。", msg);
                return;
            }
            
            // 获取或创建群数据记录
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                groupRecord = new GroupDataRecord { GroupId = msg.GroupId };
                groupDataRecords[msg.GroupId] = groupRecord;
            }
            
            groupRecord.TemporaryDecks ??= new Dictionary<string, List<string>>();
            
            // 强制创建新的临时牌堆（覆盖现有的）
            var newDeck = new List<string>(cardsToAdd);
            
            // 如果存在模板牌堆，也添加模板牌堆中的所有牌
            int templateCardCount = 0;
            if (DeckSet.defaultPublicDeck.TryGetValue(deckName, out var templateDeck))
            {
                newDeck.AddRange(templateDeck);
                templateCardCount = templateDeck.Count;
            }
            
            groupRecord.TemporaryDecks[deckName] = newDeck;
            
            SaveGroupData(msg.GroupId);
            
            string response = $"已创建新的临时牌堆 '{deckName}'，包含 {cardsToAdd.Count} 张牌";
            if (templateCardCount > 0)
            {
                response += $"（包含模板牌堆的 {templateCardCount} 张牌）";
            }
            response += $"，总共 {newDeck.Count} 张牌。";
            
            Reply(response, msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .deck new 指令时出错: {ex.Message}");
            Reply("处理创建牌堆指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 .deck clear 命令
    /// </summary>
    private void HandleDeckClearCommand(string args, Msg msg)
    {
        try
        {
            if (string.IsNullOrEmpty(args))
            {
                Reply("请指定要清空的临时牌堆名：.deck clear 牌堆名", msg);
                return;
            }
            
            string deckName = args.Trim();
            
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                Reply("当前群没有临时牌堆。", msg);
                return;
            }
            
            if (groupRecord.TemporaryDecks?.Remove(deckName) == true)
            {
                SaveGroupData(msg.GroupId);
                Reply($"已清空临时牌堆 '{deckName}'。", msg);
            }
            else
            {
                Reply($"临时牌堆 '{deckName}' 不存在。", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .deck clear 指令时出错: {ex.Message}");
            Reply("处理清空牌堆指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 .deck clearall 命令
    /// </summary>
    private void HandleDeckClearAllCommand(Msg msg)
    {
        try
        {
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                Reply("当前群没有临时牌堆。", msg);
                return;
            }
            
            if (groupRecord.TemporaryDecks?.Count > 0)
            {
                int clearedCount = groupRecord.TemporaryDecks.Count;
                groupRecord.TemporaryDecks.Clear();
                SaveGroupData(msg.GroupId);
                Reply($"已清空当前群所有 {clearedCount} 个临时牌堆。", msg);
            }
            else
            {
                Reply("当前群没有临时牌堆。", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .deck clearall 指令时出错: {ex.Message}");
            Reply("处理清空所有牌堆指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 处理 .deck reset 命令
    /// </summary>
    private void HandleDeckResetCommand(string args, Msg msg)
    {
        try
        {
            if (string.IsNullOrEmpty(args))
            {
                Reply("请指定要重置的临时牌堆名：.deck reset 牌堆名", msg);
                return;
            }
            
            string deckName = args.Trim();
            
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                Reply("当前群没有临时牌堆。", msg);
                return;
            }
            
            if (groupRecord.TemporaryDecks?.Remove(deckName) == true)
            {
                SaveGroupData(msg.GroupId);
                Reply($"已删除临时牌堆 '{deckName}'。", msg);
            }
            else
            {
                Reply($"临时牌堆 '{deckName}' 不存在。", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[牌堆系统] 处理 .deck reset 指令时出错: {ex.Message}");
            Reply("处理删除牌堆指令时发生错误。", msg);
        }
    }

    /// <summary>
    /// 解析牌名列表
    /// </summary>
    private List<string> ParseCards(string cardsText)
    {
        var cards = new List<string>();
        
        if (string.IsNullOrEmpty(cardsText))
            return cards;
        
        // 简单的按空格分割牌名
        // 注意：这里假设牌名不包含空格
        // 如果需要支持带空格的牌名，可以使用引号或其他分隔符
        var parts = cardsText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var card in parts)
        {
            if (!string.IsNullOrWhiteSpace(card))
            {
                cards.Add(card.Trim());
            }
        }
        
        return cards;
    }

    /// <summary>
    /// 处理 .welcome 指令：查看/设置入群欢迎语
    /// 语法：
    /// .welcome - 查看当前群的欢迎语
    /// .welcome set [内容] - 设置欢迎语（支持 {at} 和 {nickname} 占位符）
    /// .welcome on - 启用自动发送欢迎语
    /// .welcome off - 禁用自动发送欢迎语
    /// </summary>
    private void HandleWelcomeCommand(string args, Msg msg)
    {
        if (msg.Source != MessageSource.group)
        {
            Reply("入群欢迎语功能仅在群组中可用。", msg);
            return;
        }

        string trimmedArgs = (args ?? string.Empty).Trim();

        try
        {
            // 获取或创建群数据记录
            if (!groupDataRecords.TryGetValue(msg.GroupId, out var groupRecord))
            {
                groupRecord = new GroupDataRecord { GroupId = msg.GroupId };
                groupDataRecords[msg.GroupId] = groupRecord;
            }

            // 子命令：无参数 - 查询当前欢迎语
            if (string.IsNullOrEmpty(trimmedArgs))
            {
                string status = groupRecord.WelcomeEnabled == true ? "已启用" : "已禁用";
                if (!string.IsNullOrWhiteSpace(groupRecord.Welcome))
                {
                    Reply($"当前入群欢迎语：\n{groupRecord.Welcome}\n自动发送：{status}\n\n占位符说明：{{at}} = @新成员，{{nickname}} = 新成员昵称", msg);
                }
                else
                {
                    Reply("尚未设置入群欢迎语。使用 .welcome set [内容] 来设置。\n占位符说明：{at} = @新成员，{nickname} = 新成员昵称", msg);
                }
                return;
            }

            // 子命令：set [内容]
            if (trimmedArgs.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            {
                string welcomeText = trimmedArgs.Substring(4).Trim();

                if (string.IsNullOrWhiteSpace(welcomeText))
                {
                    Reply("欢迎语内容不能为空。", msg);
                    return;
                }

                if (welcomeText.Length > 500)
                {
                    Reply("欢迎语过长，请限制在 500 个字符以内。", msg);
                    return;
                }

                groupRecord.Welcome = welcomeText;
                SaveGroupData(msg.GroupId);
                Reply($"✓ 已设置入群欢迎语：\n{welcomeText}", msg);
                return;
            }

            // 子命令：on
            if (trimmedArgs.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(groupRecord.Welcome))
                {
                    Reply("请先设置欢迎语内容。使用 .welcome set [内容] 来设置。", msg);
                    return;
                }

                groupRecord.WelcomeEnabled = true;
                SaveGroupData(msg.GroupId);
                Reply("✓ 已启用入群欢迎语自动发送功能。新成员加入时将自动发送欢迎语。", msg);
                return;
            }

            // 子命令：off
            if (trimmedArgs.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                groupRecord.WelcomeEnabled = false;
                SaveGroupData(msg.GroupId);
                Reply("✓ 已禁用入群欢迎语自动发送功能。", msg);
                return;
            }

            // 子命令：test - 测试发送欢迎语（仅对命令发送者）
            if (trimmedArgs.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(groupRecord.Welcome))
                {
                    Reply("请先设置欢迎语内容。使用 .welcome set [内容] 来设置。", msg);
                    return;
                }

                string testWelcome = groupRecord.Welcome
                    .Replace("{at}", $"[CQ:at,qq={msg.UserId}]")
                    .Replace("{nickname}", GetReasonableSenderName(msg.UserId, msg.IsSimulationMode));

                Reply($"【测试】入群欢迎语将发送给新成员：\n{testWelcome}", msg);
                return;
            }

            // 未识别的子命令
            Reply("未识别的子命令。用法：\n" +
                  ".welcome - 查看当前欢迎语\n" +
                  ".welcome set [内容] - 设置欢迎语\n" +
                  ".welcome on - 启用自动发送\n" +
                  ".welcome off - 禁用自动发送\n" +
                  ".welcome test - 测试欢迎语\n" +
                  "占位符：{at} = @新成员，{nickname} = 新成员昵称", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 处理 welcome 指令时发生错误: {ex.Message}");
            Reply("处理入群欢迎语指令时发生内部错误。", msg);
        }
    }

    /// <summary>
    /// 发送入群欢迎语（由 OnGroupIncrease 事件调用）
    /// </summary>
    public void SendWelcomeMessage(long groupId, long userId, string userNickname)
    {
        try
        {
            if (!groupDataRecords.TryGetValue(groupId, out var groupRecord))
            {
                Log.InfoFormat($"[入群欢迎] 群 {groupId} 没有数据记录，跳过欢迎语");
                return;
            }

            if (groupRecord.WelcomeEnabled != true || string.IsNullOrWhiteSpace(groupRecord.Welcome))
            {
                Log.InfoFormat($"[入群欢迎] 群 {groupId} 未启用欢迎语或未设置内容，跳过");
                return;
            }

            string welcomeMessage = groupRecord.Welcome
                .Replace("{at}", $"[CQ:at,qq={userId}]")
                .Replace("{nickname}", userNickname);

            Log.InfoFormat($"[入群欢迎] 群 {groupId} 发送欢迎语给用户 {userId} ({userNickname})");

            // 发送欢迎语消息
            if (MessageDistribution?.WSconnection != null && MessageDistribution.WSconnection.IsWsConnected)
            {
                MessageDistribution.WSconnection.SendGroupMessage(groupId, welcomeMessage);
            }
            else
            {
                Log.Warn("[入群欢迎] WebSocket 未连接，无法发送欢迎语");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[入群欢迎] 发送欢迎语时出错: {ex.Message}");
        }
    }
}
