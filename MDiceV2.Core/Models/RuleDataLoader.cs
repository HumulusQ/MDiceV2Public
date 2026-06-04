using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text.Json;
using System.Linq;
using MDiceV2.Models;

namespace MDiceV2.Models
{
    /// <summary>
    /// 规则数据加载器 - 负责从 Resources/Rule 文件夹自动加载规则数据到 RuleDataIO
    /// 支持 SQLite 数据库文件和 JSON 配置文件
    /// 加载完成后将源文件重命名为 .complete 以防止重复加载
    /// </summary>
    public static class RuleDataLoader
    {
        /// <summary>
        /// 从资源文件夹加载所有规则数据
        /// 执行顺序：首先加载 .db 文件，再加载 .json 文件（后加载的覆盖先加载的）
        /// </summary>
        /// <param name="ruleDataIO">目标 RuleDataIO 实例</param>
        public static void LoadRulesFromResourceFolder(RuleDataIO ruleDataIO)
        {
            if (ruleDataIO == null)
            {
                Log.Warn("[RuleLoader] RuleDataIO 为空，无法加载规则数据");
                return;
            }

            try
            {
                string ruleRootPath = GetRuleRootPath();
                Log.InfoFormat("[RuleLoader] 开始加载规则数据，路径：{0}", ruleRootPath);

                // 确保文件夹存在
                if (!Directory.Exists(ruleRootPath))
                {
                    Directory.CreateDirectory(ruleRootPath);
                    Log.InfoFormat("[RuleLoader] 规则文件夹不存在，已创建：{0}", ruleRootPath);
                    return;
                }

                int successCount = 0;
                int failureCount = 0;
                var failedFiles = new List<string>();

                // 第一步：加载 SQLite 数据库文件
                try
                {
                    var dbFiles = DiscoverResourceFiles(ruleRootPath, "*.db");
                    foreach (var dbPath in dbFiles)
                    {
                        try
                        {
                            LoadSqliteDatabase(dbPath, ruleDataIO);
                            RenameToComplete(dbPath);
                            successCount++;
                            Log.InfoFormat("[RuleLoader] ✓ 数据库文件已加载：{0}", Path.GetFileName(dbPath));
                        }
                        catch (Exception ex)
                        {
                            failureCount++;
                            failedFiles.Add($"{Path.GetFileName(dbPath)}: {ex.Message}");
                            Log.Warn($"[RuleLoader] 警告：无法加载规则文件 {dbPath}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[RuleLoader] 加载数据库文件时出错：{ex.Message}");
                }

                // 第二步：加载 JSON 文件
                try
                {
                    var jsonFiles = DiscoverResourceFiles(ruleRootPath, "*.json");
                    foreach (var jsonPath in jsonFiles)
                    {
                        try
                        {
                            LoadJsonRulebook(jsonPath, ruleDataIO);
                            RenameToComplete(jsonPath);
                            successCount++;
                            Log.InfoFormat("[RuleLoader] ✓ JSON 文件已加载：{0}", Path.GetFileName(jsonPath));
                        }
                        catch (Exception ex)
                        {
                            failureCount++;
                            failedFiles.Add($"{Path.GetFileName(jsonPath)}: {ex.Message}");
                            Log.Warn($"[RuleLoader] 警告：无法加载规则文件 {jsonPath}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[RuleLoader] 加载 JSON 文件时出错：{ex.Message}");
                }

                // 输出完成总结
                Log.InfoFormat("[RuleLoader] 完成。成功加载 {0} 个文件，{1} 个文件失败", successCount, failureCount);
                if (failedFiles.Count > 0)
                {
                    Log.Warn("[RuleLoader] 失败文件列表：");
                    foreach (var failedFile in failedFiles)
                    {
                        Log.Warn($"  - {failedFile}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RuleLoader] 规则数据加载过程发生错误：{ex.Message}");
            }
        }

        /// <summary>
        /// 获取规则文件夹的绝对路径
        /// 相对于应用程序的当前运行目录
        /// 与 MDiceV2_Published\Resources\Rule 目录结构保持一致
        /// </summary>
        private static string GetRuleRootPath()
        {
            string currentDirectory = Directory.GetCurrentDirectory();
            string resourcesPath = Path.Combine(currentDirectory, "Resources");
            string rulePath = Path.Combine(resourcesPath, "Rule");
            return rulePath;
        }

        /// <summary>
        /// 扫描规则文件夹中符合模式的文件，排除已处理的 .complete 文件
        /// </summary>
        /// <param name="folderPath">文件夹路径</param>
        /// <param name="searchPattern">搜索模式（如 "*.json"、"*.db"）</param>
        /// <returns>文件路径列表（按字母顺序排序）</returns>
        private static List<string> DiscoverResourceFiles(string folderPath, string searchPattern)
        {
            if (!Directory.Exists(folderPath))
                return new List<string>();

            var files = Directory.GetFiles(folderPath, searchPattern, SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".complete", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            return files;
        }

        /// <summary>
        /// 标准化表名：移除 _1、_2 等数字后缀和文件扩展名
        /// 例如：dnd_1.json → dnd，duel_2.db → duel
        /// </summary>
        /// <param name="filename">文件名（包含或不包含扩展名）</param>
        /// <returns>标准化后的表名</returns>
        private static string NormalizeTableName(string filename)
        {
            // 移除扩展名
            string nameWithoutExt = Path.GetFileNameWithoutExtension(filename);

            // 移除尾部的 _数字 后缀（_1、_2 等）
            int lastUnderscore = nameWithoutExt.LastIndexOf('_');
            if (lastUnderscore > 0 && lastUnderscore < nameWithoutExt.Length - 1)
            {
                string suffix = nameWithoutExt.Substring(lastUnderscore + 1);
                if (suffix.All(char.IsDigit))
                {
                    nameWithoutExt = nameWithoutExt.Substring(0, lastUnderscore);
                }
            }

            return nameWithoutExt;
        }

        /// <summary>
        /// 从外部 SQLite 数据库文件加载所有表中的数据到 RuleDataIO
        /// </summary>
        /// <param name="dbPath">数据库文件路径</param>
        /// <param name="ruleDataIO">目标 RuleDataIO 实例</param>
        private static void LoadSqliteDatabase(string dbPath, RuleDataIO ruleDataIO)
        {
            if (!File.Exists(dbPath))
                throw new FileNotFoundException($"数据库文件不存在：{dbPath}");

            try
            {
                // 使用临时 RuleDataIO 实例打开这个数据库文件
                using (var tempDb = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath};Version=3;"))
                {
                    tempDb.Open();

                    // 查询所有表名
                    var command = tempDb.CreateCommand();
                    command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                    var reader = command.ExecuteReader();
                    var tableNames = new List<string>();

                    while (reader.Read())
                    {
                        tableNames.Add(reader.GetString(0));
                    }
                    reader.Close();

                    // 对每个表读取数据并写入目标数据库
                    foreach (var tableName in tableNames)
                    {
                        try
                        {
                            var selectCommand = tempDb.CreateCommand();
                            selectCommand.CommandText = $"SELECT * FROM {tableName}";
                            var dataReader = selectCommand.ExecuteReader();

                            // 确保目标表存在
                            ruleDataIO.CreateTableIfNotExists(tableName);

                            int recordCount = 0;
                            while (dataReader.Read())
                            {
                                // 假设表结构为：key (TEXT PRIMARY KEY), value (TEXT), updated_at (INTEGER)
                                string key = dataReader.GetValue(0)?.ToString() ?? "";
                                string value = dataReader.GetValue(1)?.ToString() ?? "";

                                if (!string.IsNullOrEmpty(key))
                                {
                                    ruleDataIO.SaveData(tableName, key, value);
                                    recordCount++;
                                }
                            }
                            dataReader.Close();

                            Log.InfoFormat("[RuleLoader]   表 '{0}' 已加载，记录数：{1}", tableName, recordCount);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"[RuleLoader] 加载表 '{tableName}' 时出错：{ex.Message}");
                        }
                    }

                    tempDb.Close();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"加载 SQLite 数据库失败：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从 JSON 文件加载规则数据到 RuleDataIO
        /// 预期 JSON 格式为平面对象：{ "key1": "value1", "key2": "value2", ... }
        /// 表名由文件名决定（标准化处理后）
        /// </summary>
        /// <param name="jsonPath">JSON 文件路径</param>
        /// <param name="ruleDataIO">目标 RuleDataIO 实例</param>
        private static void LoadJsonRulebook(string jsonPath, RuleDataIO ruleDataIO)
        {
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"JSON 文件不存在：{jsonPath}");

            try
            {
                // 读取文件内容
                string jsonContent = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(jsonContent))
                    throw new InvalidOperationException("JSON 文件内容为空");

                // 解析 JSON 为平面字典
                using (JsonDocument doc = JsonDocument.Parse(jsonContent))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("JSON 根元素必须是对象");

                    // 确定表名
                    string tableName = NormalizeTableName(Path.GetFileName(jsonPath));
                    if (string.IsNullOrWhiteSpace(tableName))
                        throw new InvalidOperationException("无法从文件名确定表名");

                    // 确保表存在
                    ruleDataIO.CreateTableIfNotExists(tableName);

                    int recordCount = 0;
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        string key = property.Name;
                        string value = property.Value.GetRawText();

                        // 如果 value 本身是字符串，则提取字符串值（去掉 JSON 引号）
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            value = property.Value.GetString() ?? "";
                        }

                        if (!string.IsNullOrEmpty(key))
                        {
                            ruleDataIO.SaveData(tableName, key, value);
                            recordCount++;
                        }
                    }

                    Log.InfoFormat("[RuleLoader]   表 '{0}' 已加载，记录数：{1}", tableName, recordCount);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"加载 JSON 文件失败：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 将文件重命名为 .complete 后缀，防止下次启动时重复加载
        /// </summary>
        /// <param name="filePath">文件路径</param>
        private static void RenameToComplete(string filePath)
        {
            try
            {
                string completePath = filePath + ".complete";
                if (File.Exists(filePath))
                {
                    // 如果 .complete 文件已存在，先删除
                    if (File.Exists(completePath))
                    {
                        File.Delete(completePath);
                    }
                    File.Move(filePath, completePath);
                    Log.InfoFormat("[RuleLoader]   文件已重命名为 .complete：{0}", Path.GetFileName(completePath));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[RuleLoader] 重命名文件失败：{ex.Message}");
            }
        }
    }
}
