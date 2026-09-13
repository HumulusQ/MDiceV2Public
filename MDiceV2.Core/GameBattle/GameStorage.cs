using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 游戏状态持久化储存类
    /// </summary>
    public static class GameStorage
    {
        private static readonly string SaveDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MDiceV2",
            "GameBattleSaves"
        );

        static GameStorage()
        {
            // 确保保存目录存在
            Directory.CreateDirectory(SaveDirectory);
        }

        /// <summary>
        /// 保存游戏状态
        /// </summary>
        public static void SaveGameState(GameState gameState, string saveFileName = "autosave.json")
        {
            string filePath = Path.Combine(SaveDirectory, saveFileName);
            string json = JsonSerializer.Serialize(gameState, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// 加载游戏状态
        /// </summary>
        public static GameState LoadGameState(string saveFileName = "autosave.json")
        {
            string filePath = Path.Combine(SaveDirectory, saveFileName);
            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<GameState>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        /// <summary>
        /// 获取所有保存文件列表
        /// </summary>
        public static List<string> GetSaveFiles()
        {
            return Directory.GetFiles(SaveDirectory, "*.json")
                .Select(Path.GetFileName)
                .ToList();
        }

        /// <summary>
        /// 删除保存文件
        /// </summary>
        public static void DeleteSaveFile(string saveFileName)
        {
            string filePath = Path.Combine(SaveDirectory, saveFileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}