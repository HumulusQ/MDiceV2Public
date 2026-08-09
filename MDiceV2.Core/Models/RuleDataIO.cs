using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;

namespace MDiceV2.Models;

/// <summary>
/// 规则数据输入输出管理器
/// 专门用于管理TRPG规则书数据的SQLite数据库操作
/// </summary>
public partial class RuleDataIO : ObservableObject
{
    private SQLiteConnection? _connection;
    private readonly string _dbPath;

    /// <summary>
    /// 构造函数
    /// 初始化规则书数据库连接
    /// </summary>
    public RuleDataIO()
    {
        // 数据库文件路径，放在项目目录下的data子文件夹中
        string projectPath = Directory.GetCurrentDirectory();
        string dataFolder = Path.Combine(projectPath, "data");
        Directory.CreateDirectory(dataFolder); // 确保目录存在
        _dbPath = Path.Combine(dataFolder, "RuleDatabase.db");

        Log.InfoFormat($"Rule database path: {_dbPath}");
        EnsureDatabaseFileExists();
        EnsureDefaultTableExists();
    }

    /// <summary>
    /// 确保数据库文件存在并初始化
    /// </summary>
    private void EnsureDatabaseFileExists()
    {
        bool isNewDatabase = !File.Exists(_dbPath);

        if (isNewDatabase)
        {
            SQLiteConnection.CreateFile(_dbPath);
            Log.InfoFormat("Rule database file created successfully at: {_dbPath}");
        }
        else
        {
            Log.InfoFormat("Rule database file already exists: {_dbPath}");
        }

        try
        {
            _connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
            _connection.Open();

            // 启用并发优化（WAL模式等）
            EnableConcurrencyOptimizations();

            Log.InfoFormat("Rule SQLite database connection established successfully.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to establish rule SQLite database connection: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 启用SQLite并发优化：WAL模式和同步模式
    /// </summary>
    private void EnableConcurrencyOptimizations()
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("[RuleDataIO] 无法启用并发优化：连接未打开");
            return;
        }

        try
        {
            // 启用WAL模式
            using (var cmd = new SQLiteCommand("PRAGMA journal_mode = WAL;", _connection))
            {
                var result = cmd.ExecuteScalar();
                Log.InfoFormat($"[RuleDataIO] WAL模式已启用: {result}");
            }

            // 设置同步模式为NORMAL（性能和安全的平衡）
            using (var cmd = new SQLiteCommand("PRAGMA synchronous = NORMAL;", _connection))
            {
                cmd.ExecuteNonQuery();
                Log.InfoFormat("[RuleDataIO] 同步模式已设置为NORMAL");
            }

            // 设置临时存储为内存（加速临时操作）
            using (var cmd = new SQLiteCommand("PRAGMA temp_store = MEMORY;", _connection))
            {
                cmd.ExecuteNonQuery();
                Log.InfoFormat("[RuleDataIO] 临时存储已设置为内存");
            }

            // 设置缓存大小（单位：页）
            using (var cmd = new SQLiteCommand("PRAGMA cache_size = 10000;", _connection))
            {
                cmd.ExecuteNonQuery();
                Log.InfoFormat("[RuleDataIO] 缓存大小已设置为10000页");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[RuleDataIO] 启用并发优化失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 确保默认表存在
    /// </summary>
    private void EnsureDefaultTableExists()
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("Database connection is not open, cannot ensure default table exists.");
            return;
        }

        try
        {
            string sql = "CREATE TABLE IF NOT EXISTS default_rule (key TEXT PRIMARY KEY, value TEXT, updated_at INTEGER DEFAULT 0)";
            using var command = new SQLiteCommand(sql, _connection);
            command.ExecuteNonQuery();
            Log.InfoFormat("Ensured default_rule table exists");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to ensure default_rule table: {ex.Message}");
        }
    }

    /// <summary>
    /// 确保指定的表存在（公开方法，供 RuleDataLoader 使用）
    /// </summary>
    /// <param name="tableName">表名</param>
    public void CreateTableIfNotExists(string tableName)
    {
        try
        {
            string sanitizedTableName = SanitizeTableName(tableName);
            
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                Log.Warn($"[RuleDataIO] 数据库连接未打开，无法创建表 '{sanitizedTableName}'");
                return;
            }

            string sql = $"CREATE TABLE IF NOT EXISTS {sanitizedTableName} (key TEXT PRIMARY KEY, value TEXT, updated_at INTEGER DEFAULT 0)";
            using var command = new SQLiteCommand(sql, _connection);
            command.ExecuteNonQuery();
            Log.InfoFormat("[RuleDataIO] 表 '{0}' 已确保存在", sanitizedTableName);
        }
        catch (Exception ex)
        {
            Log.Warn($"[RuleDataIO] 创建/检查表失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 验证表名，防止SQL注入
    /// </summary>
    private string SanitizeTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new ArgumentException("Table name cannot be null or empty.", nameof(tableName));
        }

        // 允许字母、数字和下划线
        if (!Regex.IsMatch(tableName, @"^[a-zA-Z0-9_]+$"))
        {
            throw new ArgumentException("Table name contains invalid characters. Only alphanumeric and underscore are allowed.", nameof(tableName));
        }

        return tableName;
    }

    /// <summary>
    /// 保存规则数据到数据库（支持事务和冲突解决）
    /// </summary>
    /// <param name="tableName">表名（规则书名）</param>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="transaction">可选的外部事务</param>
    public void SaveData(string tableName, string key, string value, SQLiteTransaction? transaction = null)
    {
        string sanitizedTableName = SanitizeTableName(tableName);

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("Rule SQLite connection is not open. Cannot save data.");
            return;
        }

        try
        {
            // 确保表存在（包含updated_at字段用于冲突解决）
            using var createTableCommand = new SQLiteCommand(
                $"CREATE TABLE IF NOT EXISTS {sanitizedTableName} (key TEXT PRIMARY KEY, value TEXT, updated_at INTEGER DEFAULT 0)",
                _connection);
            if (transaction != null)
                createTableCommand.Transaction = transaction;
            createTableCommand.ExecuteNonQuery();

            // 数据库迁移：为现有表添加updated_at列（如果不存在）
            try
            {
                using var alterCommand = new SQLiteCommand(
                    $"ALTER TABLE {sanitizedTableName} ADD COLUMN updated_at INTEGER DEFAULT 0",
                    _connection);
                if (transaction != null)
                    alterCommand.Transaction = transaction;
                alterCommand.ExecuteNonQuery();
            }
            catch (SQLiteException ex) when (ex.Message.Contains("duplicate column") || ex.Message.Contains("already exists"))
            {
                // 列已存在，忽略错误
            }

            // 获取当前时间戳（UTC Ticks，用于冲突解决）
            long currentTimestamp = DateTime.UtcNow.Ticks;

            // 使用事务保证原子性
            bool ownTransaction = transaction == null;
            if (ownTransaction)
            {
                transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            }

            try
            {
                // 检查现有数据的时间戳（冲突解决）
                using var checkCommand = new SQLiteCommand(
                    $"SELECT value, updated_at FROM {sanitizedTableName} WHERE key = @key",
                    _connection, transaction);
                checkCommand.Parameters.AddWithValue("@key", key);
                using var reader = checkCommand.ExecuteReader();
                
                if (reader.Read())
                {
                    if (long.TryParse(reader["updated_at"].ToString(), out var existingTimestamp))
                    {
                        // 在远程同步场景中，只有新时间戳更新时才覆盖
                        if (currentTimestamp < existingTimestamp)
                        {
                            Log.InfoFormat($"[RuleDataIO] 数据'{key}'的本地时间戳较旧，跳过覆盖（保留远程更新）");
                            return;
                        }
                    }
                }

                // 插入或替换数据
                string sql = $"INSERT OR REPLACE INTO {sanitizedTableName} (key, value, updated_at) VALUES (@key, @value, @updatedAt)";
                using var command = new SQLiteCommand(sql, _connection, transaction);
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value);
                command.Parameters.AddWithValue("@updatedAt", currentTimestamp);
                command.ExecuteNonQuery();

                // 提交事务
                if (ownTransaction)
                {
                    transaction.Commit();
                }

                Log.InfoFormat($"Saved rule data to table '{sanitizedTableName}': Key='{key}', Timestamp='{currentTimestamp}'");
            }
            catch (Exception)
            {
                if (ownTransaction)
                {
                    transaction?.Rollback();
                }
                throw;
            }
            finally
            {
                if (ownTransaction)
                {
                    transaction?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to save rule data to table '{sanitizedTableName}': {ex.Message}");
        }
    }

    /// <summary>
    /// 从数据库读取规则数据
    /// </summary>
    /// <param name="tableName">表名（规则书名）</param>
    /// <param name="key">键</param>
    /// <returns>值，如果不存在则返回null</returns>
    public string? ReadData(string tableName, string key)
    {
        string sanitizedTableName = SanitizeTableName(tableName);

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("Rule SQLite connection is not open. Cannot read data.");
            return null;
        }

        try
        {
            string sql = $"SELECT value FROM {sanitizedTableName} WHERE key = @key";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@key", key);

            var result = command.ExecuteScalar();
            if (result != null)
            {
                string value = result.ToString()!;
                Log.InfoFormat($"Read rule data from table '{sanitizedTableName}': Key='{key}', Result='{value}'");
                return value;
            }
            else
            {
                Log.InfoFormat($"Read rule data from table '{sanitizedTableName}': Key='{key}', Result='null' (No data found)");
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read rule data from table '{sanitizedTableName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读取表中的所有规则数据
    /// </summary>
    /// <param name="tableName">表名（规则书名）</param>
    /// <returns>包含所有键值对的字典</returns>
    public Dictionary<string, string> ReadAllData(string tableName)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        var data = new Dictionary<string, string>();

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("Rule SQLite connection is not open. Cannot read all data.");
            return data;
        }

        try
        {
            // 检查表是否存在
            using var checkTableCommand = new SQLiteCommand(
                $"SELECT name FROM sqlite_master WHERE type='table' AND name='{sanitizedTableName}'",
                _connection);
            var result = checkTableCommand.ExecuteScalar();
            if (result == null)
            {
                Log.InfoFormat($"Table '{sanitizedTableName}' does not exist. Returning empty dictionary.");
                return data;
            }

            string sql = $"SELECT key, value FROM {sanitizedTableName}";
            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                string key = reader["key"].ToString()!;
                string value = reader["value"].ToString()!;
                data[key] = value;
            }

            Log.InfoFormat($"Read {data.Count} items from rule table '{sanitizedTableName}'.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read all rule data from table '{sanitizedTableName}': {ex.Message}");
        }

        return data;
    }

    /// <summary>
    /// 关闭数据库连接
    /// </summary>
    public void Close()
    {
        if (_connection != null && _connection.State == System.Data.ConnectionState.Open)
        {
            _connection.Close();
            _connection.Dispose();
            _connection = null;
            Log.InfoFormat("Rule SQLite connection closed and disposed.");
        }
    }
}