using System.Data.SQLite;
using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace AIMod.Trpg;

/// <summary>
/// 从 MDiceV2 主数据库读取队伍数据（只读，带缓存）
/// 主库路径: data/MDiceV2.db，GroupData 表存储 JSON 格式的队伍信息
/// </summary>
public class TeamDataProvider
{
    private readonly string _mainDbPath;
    private readonly IModContext _context;
    private readonly ConcurrentDictionary<(long groupId, string teamName), TeamSnapshot> _cache = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(30);

    public TeamDataProvider(IModContext context)
    {
        _context = context;
        var launcherBaseDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        _mainDbPath = System.IO.Path.Combine(launcherBaseDir, "data", "MDiceV2.db");
    }

    /// <summary>
    /// 获取指定群中某队伍的快照。返回 null 表示未找到队伍。
    /// </summary>
    public TeamSnapshot? GetTeamForGroup(long groupId, string teamName)
    {
        if (string.IsNullOrEmpty(teamName)) return null;

        // 刷新缓存（如果过期）
        if ((DateTime.UtcNow - _lastRefresh) > _refreshInterval)
        {
            RefreshCache();
        }

        return _cache.TryGetValue((groupId, teamName), out var snapshot) ? snapshot : null;
    }

    /// <summary>
    /// 获取指定群中用户绑定的默认队伍名
    /// </summary>
    public string? GetUserDefaultTeamName(long groupId, long userId)
    {
        if ((DateTime.UtcNow - _lastRefresh) > _refreshInterval)
        {
            RefreshCache();
        }

        // 遍历缓存查找该用户在该群的默认队伍
        foreach (var kvp in _cache)
        {
            if (kvp.Key.groupId == groupId && kvp.Value.UserDefaultTeams != null)
            {
                if (kvp.Value.UserDefaultTeams.TryGetValue(userId, out var teamName))
                    return teamName;
            }
        }
        return null;
    }

    /// <summary>
    /// 获取指定群的所有队伍快照
    /// </summary>
    public List<TeamSnapshot> GetTeamsForGroup(long groupId)
    {
        if ((DateTime.UtcNow - _lastRefresh) > _refreshInterval)
        {
            RefreshCache();
        }

        var result = new List<TeamSnapshot>();
        foreach (var kvp in _cache)
        {
            if (kvp.Key.groupId == groupId)
                result.Add(kvp.Value);
        }
        return result;
    }

    /// <summary>
    /// 使缓存失效，强制下次读取时刷新
    /// 在外部直接修改主库后调用
    /// </summary>
    public void InvalidateCache()
    {
        _lastRefresh = DateTime.MinValue;
    }

    private void RefreshCache()
    {
        try
        {
            if (!System.IO.File.Exists(_mainDbPath))
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Main database not found at: {_mainDbPath}");
                return;
            }

            using var conn = new SQLiteConnection($"Data Source={_mainDbPath};Version=3;Read Only=True;");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM GroupData";
            using var reader = cmd.ExecuteReader();

            var newCache = new ConcurrentDictionary<(long, string), TeamSnapshot>();

            while (reader.Read())
            {
                var keyStr = reader.GetString(0);
                if (!long.TryParse(keyStr, out var groupId)) continue;

                var jsonValue = reader.GetString(1);
                try
                {
                    var doc = JsonDocument.Parse(jsonValue);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("Teams", out var teamsObj)) continue;

                    foreach (var teamProp in teamsObj.EnumerateObject())
                    {
                        var teamName = teamProp.Name;
                        var teamData = teamProp.Value;

                        var snapshot = new TeamSnapshot
                        {
                            TeamName = teamName,
                            GroupId = groupId
                        };

                        if (teamData.TryGetProperty("CreatorId", out var creatorEl))
                            snapshot.CreatorId = creatorEl.GetInt64();

                        if (teamData.TryGetProperty("Members", out var membersEl))
                        {
                            snapshot.Members = new List<long>();
                            foreach (var m in membersEl.EnumerateArray())
                                snapshot.Members.Add(m.GetInt64());
                        }

                        // 从 UserDefaultTeams 提取（在群级别）
                        if (root.TryGetProperty("UserDefaultTeams", out var udtEl))
                        {
                            snapshot.UserDefaultTeams = new Dictionary<long, string>();
                            foreach (var udtProp in udtEl.EnumerateObject())
                            {
                                if (long.TryParse(udtProp.Name, out var uid))
                                    snapshot.UserDefaultTeams[uid] = udtProp.Value.GetString() ?? "";
                            }
                        }

                        newCache[(groupId, teamName)] = snapshot;
                    }
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Parse GroupData[{groupId}] error: {ex.Message}");
                }
            }

            // 原子替换缓存
            _cache.Clear();
            foreach (var kvp in newCache)
                _cache[kvp.Key] = kvp.Value;

            _lastRefresh = DateTime.UtcNow;
            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Team cache refreshed, {_cache.Count} teams loaded");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] RefreshCache error: {ex.Message}");
        }
    }
}

public class TeamSnapshot
{
    public string TeamName { get; set; } = "";
    public long GroupId { get; set; }
    public long CreatorId { get; set; }
    public List<long> Members { get; set; } = new();
    public Dictionary<long, string>? UserDefaultTeams { get; set; }
}
