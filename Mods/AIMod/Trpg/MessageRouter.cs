using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AIMod.Trpg;

/// <summary>
/// 消息路由：发言者分类、触发判定、60秒冷却
/// </summary>
public class MessageRouter
{
    private readonly IModContext _context;
    private readonly ConcurrentDictionary<(long GroupId, string CharacterId), DateTime> _cooldownUntil = new();
    private readonly ConcurrentDictionary<(long GroupId, string CharacterId), bool> _pendingExecution = new();
    private readonly ConcurrentDictionary<(long GroupId, string CharacterId), Task> _delayedTasks = new();
    private readonly ConcurrentDictionary<(long GroupId, string CharacterId), DateTime> _lastTriggerTime = new();
    private readonly ConcurrentDictionary<(long GroupId, string CharacterId), List<string>> _pendingMessages = new();

    // 过滤任意 CQ 码结构（包括表情、图片、文件、音频、视频、转发等）
    private static readonly Regex CqCodeRegex = new(
        @"\[CQ:[^\]]+\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 检测骰子结果消息（由外部骰子系统播报）
    private static readonly Regex DiceResultRegex = new(
        @"(?:\b\d+D\d+\b|\b\d+d\d+\b).*?(?:成功|失败|大失败|极难成功|困难成功|常规成功|致命失败|Fumble|Critical|Success|Failure)|(?:成功|失败|大失败|极难成功|困难成功|常规成功|致命失败|Fumble|Critical|Success|Failure).*?(?:\b\d+D\d+\b|\b\d+d\d+\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public MessageRouter(IModContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 分类发言者并格式化消息。
    /// 返回 null 表示发言者不在队伍中，应忽略。
    /// </summary>
    public (string? SpeakerType, string Nickname, string Formatted) ClassifyAndFormat(
        long groupId, long userId, string content, bool isAted,
        TeamSnapshot? team, string oocPrefix, IModContext context)
    {
        var nickname = context.GetUserInfo(userId).Nickname;

        // 1. 在触发前剥离所有 CQ 码结构
        if (CqCodeRegex.IsMatch(content))
        {
            var stripped = CqCodeRegex.Replace(content, "").Trim();
            if (string.IsNullOrEmpty(stripped))
            {
                context.Log(LogLevel.Debug, $"[AIMod:TRPG] 过滤纯 CQ 码消息: {content}");
                return (null, nickname, "");
            }

            // 保留剥离 CQ 后的文本继续流程
            content = stripped;
        }

        // 2. 过滤程序本体已拦截的掷骰指令（以 . 开头的命令消息）
        // 注意：System-Dice（骰子结果播报）不以 . 开头，不受影响
        var trimmedContent = content.TrimStart();
        if (trimmedContent.StartsWith(".") && !trimmedContent.StartsWith(oocPrefix))
        {
            context.Log(LogLevel.Debug, $"[AIMod:TRPG] 过滤命令消息: {content}");
            return (null, nickname, "");
        }

        // 3. 检测括号OOC（仅检查开头的左括号，支持不闭合括号）
        // 这种内容会被记录但不会触发AI响应
        var trimmedForCheck = content.Trim();
        if (trimmedForCheck.StartsWith("(") || trimmedForCheck.StartsWith("（"))
        {
            context.Log(LogLevel.Debug, $"[AIMod:TRPG] 检测到括号OOC内容，记录但不触发AI响应: {content}");
            return ("OOC", nickname, $"[OOC-{nickname}]: {content}");
        }

        // 分类逻辑
        string? speakerType;

        if (team == null)
        {
            // 队伍信息获取失败，默认视为 PL
            context.Log(LogLevel.Debug, $"[AIMod:TRPG] 队伍信息为空，默认视为 PL (Group={groupId}, User={userId})");
            speakerType = "PL";
        }
        else if (content.StartsWith(oocPrefix))
        {
            speakerType = "OOC";
        }
        else if (userId == team.CreatorId)
        {
            speakerType = "GM";
        }
        else if (team.Members.Contains(userId))
        {
            speakerType = "PL";
        }
        else if (DiceResultRegex.IsMatch(content))
        {
            // 不在队伍中，但消息是骰子结果播报 → 记录历史但不触发 AI 响应
            speakerType = "System-Dice";
        }
        else
        {
            // 不在队伍中，默认视为 PL（可能是 PL 在队伍外）
            context.Log(LogLevel.Debug, $"[AIMod:TRPG] 用户不在队伍中，默认视为 PL (Group={groupId}, User={userId}, Team={team.TeamName})");
            speakerType = "PL";
        }

        var formatted = $"[{speakerType}-{nickname}]: {content}";
        return (speakerType, nickname, formatted);
    }

    /// <summary>
    /// 判定是否应触发 AI 响应
    /// </summary>
    public bool ShouldTrigger(string? speakerType, bool isAted)
    {
        return speakerType switch
        {
            "GM" => true,
            "PL" => true,
            "System-Dice" => false, // 骰子结果只进历史，不触发 AI 回复
            "OOC" => false, // OOC 消息完全不触发 AI（即使被 @）
            _ => false
        };
    }

    /// <summary>
    /// 检查冷却状态。返回 true 表示可以执行 API 调用。
    /// </summary>
    public bool IsCooldownActive(long groupId, string characterId)
    {
        var key = (groupId, characterId);
        if (_cooldownUntil.TryGetValue(key, out var until))
        {
            return DateTime.UtcNow < until;
        }
        return false;
    }

    /// <summary>
    /// 记录触发时间（仅在收到新消息时调用）
    /// </summary>
    public void RecordTriggerTime(long groupId, string characterId)
    {
        var key = (groupId, characterId);
        _lastTriggerTime[key] = DateTime.UtcNow;
    }

    /// <summary>
    /// 获取最后一次触发时间（不更新）
    /// </summary>
    public DateTime? GetLastTriggerTime(long groupId, string characterId)
    {
        var key = (groupId, characterId);
        if (_lastTriggerTime.TryGetValue(key, out var time))
            return time;
        return null;
    }

    /// <summary>
    /// 记录冷却期间的消息
    /// </summary>
    public void RecordPendingMessage(long groupId, string characterId, string message)
    {
        var key = (groupId, characterId);
        _pendingMessages.AddOrUpdate(key, 
            new List<string> { message }, 
            (k, list) => { list.Add(message); return list; });
    }

    /// <summary>
    /// 获取并清除冷却期间的消息
    /// </summary>
    public List<string> GetAndClearPendingMessages(long groupId, string characterId)
    {
        var key = (groupId, characterId);
        if (_pendingMessages.TryRemove(key, out var messages))
            return messages;
        return new List<string>();
    }

    /// <summary>
    /// 尝试获取冷却锁。返回 (canExecute, hasPending, cooldownEndsAt) 元组。
    /// canExecute=true 表示可以立即执行 API 调用。
    /// hasPending=true 表示有待执行的请求（冷却完成时需要执行）。
    /// cooldownEndsAt 表示冷却结束时间（如果冷却中）。
    /// </summary>
    public (bool CanExecute, bool HasPending, DateTime? CooldownEndsAt) TryAcquireCooldown(long groupId, string characterId, int cooldownSeconds)
    {
        var key = (groupId, characterId);
        var newUntil = DateTime.UtcNow.AddSeconds(cooldownSeconds);
        var now = DateTime.UtcNow;

        if (!_cooldownUntil.ContainsKey(key))
        {
            return (_cooldownUntil.TryAdd(key, newUntil), false, null);
        }

        // 检查是否已过冷却期
        if (_cooldownUntil.TryGetValue(key, out var currentUntil) && now >= currentUntil)
        {
            // 冷却已过，检查是否有待执行的请求
            var hasPending = _pendingExecution.TryRemove(key, out _);
            // 更新冷却时间（无论是否有待执行请求）
            _cooldownUntil.TryUpdate(key, newUntil, currentUntil);
            return (true, hasPending, null);
        }

        // 仍在冷却中，标记为待执行（去重）
        _pendingExecution.TryAdd(key, true);
        return (false, false, currentUntil);
    }

    /// <summary>
    /// 清除待执行标记（用于取消延时任务）
    /// </summary>
    public void ClearPendingExecution(long groupId, string characterId)
    {
        var key = (groupId, characterId);
        _pendingExecution.TryRemove(key, out _);
    }

    /// <summary>
    /// 取消并清除之前的延时任务
    /// </summary>
    public void CancelDelayedTask(long groupId, string characterId)
    {
        var key = (groupId, characterId);
        if (_delayedTasks.TryRemove(key, out var existingTask))
        {
            // 不等待任务完成，只是标记为已取消
            // 实际的取消逻辑在延时任务中通过检查 pendingExecution 实现
        }
    }

    /// <summary>
    /// 存储延时任务引用
    /// </summary>
    public void StoreDelayedTask(long groupId, string characterId, Task task)
    {
        var key = (groupId, characterId);
        _delayedTasks[key] = task;
    }

    /// <summary>
    /// 强制设置冷却（用于异常恢复等场景）
    /// </summary>
    public void ForceSetCooldown(long groupId, string characterId, int cooldownSeconds)
    {
        _cooldownUntil[(groupId, characterId)] = DateTime.UtcNow.AddSeconds(cooldownSeconds);
    }

    public void ClearCharacterState(long groupId, string characterId)
    {
        var key = (groupId, characterId);
        _cooldownUntil.TryRemove(key, out _);
        _pendingExecution.TryRemove(key, out _);
        _delayedTasks.TryRemove(key, out _);
        _lastTriggerTime.TryRemove(key, out _);
        _pendingMessages.TryRemove(key, out _);
    }
}
