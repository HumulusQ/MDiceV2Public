using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private readonly ConcurrentDictionary<long, SpeakerNameSnapshot> _speakerNameCache = new();
    private readonly TimeSpan _speakerNameRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly string _mainDbPath;
    private DateTime _speakerNameLastRefresh = DateTime.MinValue;

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
        var launcherBaseDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        _mainDbPath = Path.Combine(launcherBaseDir, "data", "MDiceV2.db");
    }

    /// <summary>
    /// 分类发言者并格式化消息。
    /// 返回 null 表示发言者不在队伍中，应忽略。
    /// </summary>
    public (string? SpeakerType, string Nickname, string Formatted) ClassifyAndFormat(
        long groupId, long userId, string content, bool isAted,
        TeamSnapshot? team, string oocPrefix, IModContext context)
    {
        var nickname = ResolveSpeakerName(groupId, userId, team, context);

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

    private string ResolveSpeakerName(long groupId, long userId, TeamSnapshot? team, IModContext context)
    {
        string fallbackNickname;
        try
        {
            fallbackNickname = context.GetUserInfo(userId).Nickname;
        }
        catch
        {
            fallbackNickname = string.Empty;
        }

        RefreshSpeakerNameCacheIfNeeded(context);

        if (_speakerNameCache.TryGetValue(userId, out var snapshot))
        {
            // TRPG 内优先使用当前角色名。当前版本主程序尚未持久化 .com set，
            // 因此这里只能读取未来可能存在的 CurrentCharacterName/ActiveCharacterName，
            // 或在用户只有一张人物卡时使用唯一人物卡名。
            if (ShouldPreferCharacterName(userId, team) && !string.IsNullOrWhiteSpace(snapshot.CharacterName))
            {
                return snapshot.CharacterName!;
            }

            // 其次使用 .name 设置的 DisplayName。
            if (!string.IsNullOrWhiteSpace(snapshot.DisplayName))
            {
                return snapshot.DisplayName!;
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackNickname))
        {
            return fallbackNickname;
        }

        return userId > 0 ? userId.ToString() : "[UnknownUser]";
    }

    private bool ShouldPreferCharacterName(long userId, TeamSnapshot? team)
    {
        if (userId <= 0)
        {
            return false;
        }

        // 队伍信息缺失时，消息会被默认视作 PL，允许使用角色名。
        if (team == null)
        {
            return true;
        }

        // GM/KP 通常不应被强制替换成某张人物卡名。
        return team.Members.Contains(userId) && userId != team.CreatorId;
    }

    private void RefreshSpeakerNameCacheIfNeeded(IModContext context)
    {
        if ((DateTime.UtcNow - _speakerNameLastRefresh) <= _speakerNameRefreshInterval)
        {
            return;
        }

        try
        {
            if (!File.Exists(_mainDbPath))
            {
                context.Log(LogLevel.Debug, $"[AIMod:TRPG] Main database not found while resolving speaker names: {_mainDbPath}");
                _speakerNameLastRefresh = DateTime.UtcNow;
                return;
            }

            using var conn = new SQLiteConnection($"Data Source={_mainDbPath};Version=3;Read Only=True;");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM UserData";
            using var reader = cmd.ExecuteReader();

            var newCache = new ConcurrentDictionary<long, SpeakerNameSnapshot>();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (!long.TryParse(key, out var userId))
                {
                    continue;
                }

                var jsonValue = reader.GetString(1);
                try
                {
                    using var doc = JsonDocument.Parse(jsonValue);
                    var snapshot = ParseSpeakerNameSnapshot(doc.RootElement);
                    if (snapshot.HasAnyName)
                    {
                        newCache[userId] = snapshot;
                    }
                }
                catch (Exception ex)
                {
                    context.Log(LogLevel.Debug, $"[AIMod:TRPG] Parse UserData[{userId}] speaker name error: {ex.Message}");
                }
            }

            _speakerNameCache.Clear();
            foreach (var kvp in newCache)
            {
                _speakerNameCache[kvp.Key] = kvp.Value;
            }

            _speakerNameLastRefresh = DateTime.UtcNow;
            context.Log(LogLevel.Debug, $"[AIMod:TRPG] Speaker name cache refreshed, {_speakerNameCache.Count} users loaded");
        }
        catch (Exception ex)
        {
            _speakerNameLastRefresh = DateTime.UtcNow;
            context.Log(LogLevel.Warn, $"[AIMod:TRPG] RefreshSpeakerNameCache error: {ex.Message}");
        }
    }

    private static SpeakerNameSnapshot ParseSpeakerNameSnapshot(JsonElement root)
    {
        var displayName = GetTrimmedString(root, "DisplayName");
        var characterName = GetFirstTrimmedString(root,
            "CurrentCharacterName",
            "ActiveCharacterName",
            "CurrentRoleName",
            "TeamCharacterName");

        if (string.IsNullOrWhiteSpace(characterName)
            && TryGetPropertyIgnoreCase(root, "CharacterSheets", out var sheets)
            && sheets.ValueKind == JsonValueKind.Object)
        {
            var characterNames = sheets.EnumerateObject()
                .Select(p => p.Name?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

            if (characterNames.Count == 1)
            {
                characterName = characterNames[0];
            }
        }

        return new SpeakerNameSnapshot(characterName, displayName);
    }

    private static string? GetFirstTrimmedString(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetTrimmedString(root, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetTrimmedString(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals(propertyName)
                    || string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record SpeakerNameSnapshot(string? CharacterName, string? DisplayName)
    {
        public bool HasAnyName => !string.IsNullOrWhiteSpace(CharacterName) || !string.IsNullOrWhiteSpace(DisplayName);
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