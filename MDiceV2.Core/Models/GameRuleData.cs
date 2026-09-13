using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.Json;
using MDiceV2.Core.GameBattle;

namespace MDiceV2.Models;

/// <summary>
/// 全局游戏规则/状态数据根对象
/// 以用户名为键保存每个玩家的 GameStateSnapshot 数据。
/// </summary>
public class GameRuleData
{
    public Dictionary<string, GameStateSnapshot> UserGameStates { get; set; } = new();
}

/// <summary>
/// GameRuleData 的磁盘读写帮助类
/// 负责将 GameRuleData 以二进制 JSON 文件的形式保存/加载。
/// </summary>
public static class GameRuleDataStore
{
    private const string TableName = "game_state_data";

    public static GameRuleData Load()
    {
        var dataIO = new DataIO();
        var blobs = dataIO.ReadAllBlobs(TableName);

        var userStates = new Dictionary<string, GameStateSnapshot>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var kvp in blobs)
        {
            try
            {
                var state = JsonSerializer.Deserialize<GameStateSnapshot>(kvp.Value, options);
                if (state != null)
                {
                    userStates[kvp.Key] = state;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[GameRuleDataStore] 反序列化用户 {kvp.Key} 的游戏状态快照失败: {ex.Message}");
            }
        }

        var loadedKeys = string.Join(",", userStates.Keys);
        Log.InfoFormat($"[GameRuleDataStore] 已从 SQLite 加载 {userStates.Count} 个游戏状态快照，用户: {loadedKeys}");

        dataIO.Close();
        return new GameRuleData { UserGameStates = userStates };
    }

    public static void Save(GameRuleData data)
    {
        if (data == null)
        {
            Log.Warn("[GameRuleDataStore] 传入的 GameRuleData 为 null，跳过保存");
            return;
        }

        var dataIO = new DataIO();
        var serializerOptions = new JsonSerializerOptions { WriteIndented = false };

        var existing = dataIO.ReadAllBlobs(TableName).Keys.ToHashSet();

        foreach (var kvp in data.UserGameStates)
        {
            try
            {
                byte[] blob = JsonSerializer.SerializeToUtf8Bytes(kvp.Value, serializerOptions);
                dataIO.SaveBlob(TableName, kvp.Key, blob);
                existing.Remove(kvp.Key);
            }
            catch (Exception ex)
            {
                Log.Warn($"[GameRuleDataStore] 序列化/保存用户 {kvp.Key} 的游戏状态快照失败: {ex.Message}");
            }
        }

        foreach (var staleKey in existing)
        {
            try
            {
                using var command = new System.Data.SQLite.SQLiteCommand($"DELETE FROM {TableName} WHERE key = @key", new System.Data.SQLite.SQLiteConnection($"Data Source={System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "data", "MDiceV2.db")};Version=3;"));
                command.Connection.Open();
                command.Parameters.AddWithValue("@key", staleKey);
                command.ExecuteNonQuery();
                command.Connection.Close();
            }
            catch (Exception ex)
            {
                Log.Warn($"[GameRuleDataStore] 清理用户 {staleKey} 旧游戏状态失败: {ex.Message}");
            }
        }

        var savedKeys = string.Join(",", data.UserGameStates.Keys);
        Log.InfoFormat($"[GameRuleDataStore] 已保存 {data.UserGameStates.Count} 个游戏状态快照到 SQLite，用户: {savedKeys}");

        dataIO.Close();
    }
}
