using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;

namespace MDiceV2.Models;

/// <summary>
/// 数据输入输出管理器
/// 负责SQLite数据库的读写操作
/// </summary>
public partial class DataIO : ObservableObject
{
    private SQLiteConnection? _connection;
    private readonly string _dbPath;

    /// <summary>
    /// 默认构造函数 - 使用MDiceV2默认数据库
    /// 初始化数据库连接
    /// </summary>
    public DataIO() : this(null)
    {
    }

    /// <summary>
    /// 构造函数 - 支持自定义数据库路径
    /// </summary>
    /// <param name="dbPath">数据库路径。如果为null，使用默认的data/MDiceV2.db</param>
    public DataIO(string? dbPath)
    {
        // 确定数据库路径
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            // 使用默认路径：项目目录/data/MDiceV2.db
            string projectPath = Directory.GetCurrentDirectory();
            string dataFolder = Path.Combine(projectPath, "data");
            Directory.CreateDirectory(dataFolder); // 确保目录存在
            _dbPath = Path.Combine(dataFolder, "MDiceV2.db");
        }
        else
        {
            // 使用自定义路径，但确保目录存在
            string? directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            _dbPath = dbPath;
        }

        Log.InfoFormat($"Database path: {_dbPath}");
        EnsureDatabaseFileExists();
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
            Log.InfoFormat("Database file created successfully at: {_dbPath}");
        }
        else
        {
            Log.InfoFormat("Database file already exists: {_dbPath}");
        }

        try
        {
            // 【UTF-8 编码】确保 SQLite 连接使用 UTF-8 编码
            // UseUTF16Encoding=False 明确禁用 UTF-16，强制使用 UTF-8
            // BinaryGUID=False 防止 GUID 的编码问题
            string connectionString = $"Data Source={_dbPath};Version=3;UseUTF16Encoding=False;BinaryGUID=False;";
            _connection = new SQLiteConnection(connectionString);
            _connection.Open();

            // 启用并发优化（WAL模式等）
            EnableConcurrencyOptimizations();

            if (isNewDatabase)
            {
                // 创建基础表结构
                CreateInitialTables();
            }

            Log.InfoFormat("SQLite database connection established successfully.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to establish SQLite database connection: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 创建初始表结构
    /// </summary>
    private void CreateInitialTables()
    {
        string createTableSql = @"
        CREATE TABLE IF NOT EXISTS DataStore (
            key TEXT PRIMARY KEY,
            value TEXT,
            updated_at INTEGER DEFAULT 0
        );";

        using var command = new SQLiteCommand(createTableSql, _connection);
        command.ExecuteNonQuery();
        Log.InfoFormat("Initial table 'DataStore' created successfully");
    }

    /// <summary>
    /// 启用SQLite并发优化：WAL模式和同步模式
    /// WAL (Write-Ahead Logging) 允许读写并发
    /// NORMAL 同步模式 = 更好的性能和足够的安全性
    /// </summary>
    private void EnableConcurrencyOptimizations()
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("[DataIO] 无法启用并发优化：连接未打开");
            return;
        }

        try
        {
            // 启用WAL模式
            using (var cmd = new SQLiteCommand("PRAGMA journal_mode = WAL;", _connection))
            {
                var result = cmd.ExecuteScalar();
                Log.InfoFormat($"[DataIO] WAL模式已启用: {result}");
            }

            // 设置同步模式为NORMAL（性能和安全的平衡）
            using (var cmd = new SQLiteCommand("PRAGMA synchronous = NORMAL;", _connection))
            {
                cmd.ExecuteNonQuery();
                Log.InfoFormat("[DataIO] 同步模式已设置为NORMAL");
            }

            // 设置临时存储为内存（加速临时操作）
            using (var cmd = new SQLiteCommand("PRAGMA temp_store = MEMORY;", _connection))
            {
                cmd.ExecuteNonQuery();
                Log.InfoFormat("[DataIO] 临时存储已设置为内存");
            }

            // 设置缓存大小（单位：页）
            using (var cmd = new SQLiteCommand("PRAGMA cache_size = 10000;", _connection))
            {
                cmd.ExecuteNonQuery();
                Log.InfoFormat("[DataIO] 缓存大小已设置为10000页");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[DataIO] 启用并发优化失败: {ex.Message}");
        }
    }

    private void EnsureBlobTable(string tableName)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Error("[DataIO] 数据库连接未打开，无法创建/确认表");
            throw new InvalidOperationException("SQLite connection is not open");
        }

        using var createTableCommand = new SQLiteCommand(
            $"CREATE TABLE IF NOT EXISTS {sanitizedTableName} (key TEXT PRIMARY KEY, blob BLOB, updated_at INTEGER DEFAULT 0)",
            _connection);
        createTableCommand.ExecuteNonQuery();
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
    /// 保存数据到数据库（支持事务和冲突解决）
    /// </summary>
    /// <param name="tableName">表名</param>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="transaction">可选的外部事务</param>
    public void SaveData(string tableName, string key, string value, SQLiteTransaction? transaction = null)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        Log.InfoFormat($"[DataIO] 开始保存数据到表 '{sanitizedTableName}'");

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Error($"[DataIO] 数据库连接未打开或为空，无法保存数据。Connection state: {_connection?.State.ToString() ?? "null"}");
            return;
        }

        try
        {
            // 确保表存在（包含updated_at字段用于冲突解决）
            Log.InfoFormat($"[DataIO] 确保表 '{sanitizedTableName}' 存在");
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
                Log.InfoFormat($"[DataIO] 为表 '{sanitizedTableName}' 添加 updated_at 列");
            }
            catch (SQLiteException ex) when (ex.Message.Contains("duplicate column") || ex.Message.Contains("already exists"))
            {
                // 列已存在，忽略错误
                Log.InfoFormat($"[DataIO] 表 '{sanitizedTableName}' 的 updated_at 列已存在");
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
                            Log.InfoFormat($"[DataIO] 数据'{key}'的本地时间戳较旧，跳过覆盖（保留远程更新）");
                            return;
                        }
                    }
                }

                // 插入或替换数据
                string sql = $"INSERT OR REPLACE INTO {sanitizedTableName} (key, value, updated_at) VALUES (@key, @value, @updatedAt)";
                using var command = new SQLiteCommand(sql, _connection, transaction);
                command.Parameters.AddWithValue("@key", key);
                
                // 【UTF-8 验证】确保值正确编码为 UTF-8
                // 将字符串转换为 UTF-8 字节，再转回，以确保编码完整性
                if (!string.IsNullOrEmpty(value))
                {
                    try
                    {
                        byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes(value);
                        string utf8String = System.Text.Encoding.UTF8.GetString(utf8Bytes);
                        command.Parameters.AddWithValue("@value", utf8String);
                        
                        // 日志：记录包含非 ASCII 字符的数据（用于调试中文编码问题）
                        if (value.Any(c => c > 127))
                        {
                            Log.InfoFormat($"[DataIO] ✅ UTF-8 encoding verified for key '{key}' with non-ASCII characters (length: {utf8Bytes.Length} bytes)");
                        }
                    }
                    catch (Exception encEx)
                    {
                        Log.Error($"[DataIO] ⚠️ UTF-8 encoding error for key '{key}': {encEx.Message}, using value as-is");
                        command.Parameters.AddWithValue("@value", value);
                    }
                }
                else
                {
                    command.Parameters.AddWithValue("@value", value);
                }
                
                command.Parameters.AddWithValue("@updatedAt", currentTimestamp);
                command.ExecuteNonQuery();

                // 提交事务
                if (ownTransaction)
                {
                    transaction.Commit();
                }

                Log.InfoFormat($"[DataIO] 成功保存数据到表 '{sanitizedTableName}'，时间戳: {currentTimestamp}");
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
            Log.Warn($"Failed to save data to table '{sanitizedTableName}': {ex.Message}");
        }
    }

    /// <summary>
    /// 批量保存数据（在单个事务中）- 用于同步操作
    /// </summary>
    /// <param name="tableName">表名</param>
    /// <param name="dataItems">数据项列表</param>
    public void SaveDataBatch(string tableName, IEnumerable<(string Key, string Value)> dataItems)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Error($"[DataIO] 数据库连接未打开，无法批量保存数据");
            return;
        }

        using var transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            // 确保表存在
            using var createTableCommand = new SQLiteCommand(
                $"CREATE TABLE IF NOT EXISTS {sanitizedTableName} (key TEXT PRIMARY KEY, value TEXT, updated_at INTEGER DEFAULT 0)",
                _connection, transaction);
            createTableCommand.ExecuteNonQuery();

            // 数据库迁移：为现有表添加updated_at列（如果不存在）
            try
            {
                using var alterCommand = new SQLiteCommand(
                    $"ALTER TABLE {sanitizedTableName} ADD COLUMN updated_at INTEGER DEFAULT 0",
                    _connection, transaction);
                alterCommand.ExecuteNonQuery();
            }
            catch (SQLiteException ex) when (ex.Message.Contains("duplicate column") || ex.Message.Contains("already exists"))
            {
                // 列已存在，忽略错误
            }

            long currentTimestamp = DateTime.UtcNow.Ticks;

            // 批量插入
            foreach (var (key, value) in dataItems)
            {
                string sql = $"INSERT OR REPLACE INTO {sanitizedTableName} (key, value, updated_at) VALUES (@key, @value, @updatedAt)";
                using var command = new SQLiteCommand(sql, _connection, transaction);
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value);
                command.Parameters.AddWithValue("@updatedAt", currentTimestamp);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            Log.InfoFormat($"[DataIO] 批量保存 {dataItems.Count()} 条数据到表 '{sanitizedTableName}'");
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Log.Error($"[DataIO] 批量保存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes only values that differ from the currently persisted value. All changes are
    /// committed atomically so callers can safely apply an imported configuration set.
    /// </summary>
    public int SaveDataBatchIfChanged(string tableName, IEnumerable<(string Key, string Value)> dataItems)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        var items = dataItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection is not open");
        if (items.Count == 0)
            return 0;

        using var transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            using (var createTableCommand = new SQLiteCommand(
                $"CREATE TABLE IF NOT EXISTS {sanitizedTableName} (key TEXT PRIMARY KEY, value TEXT, updated_at INTEGER DEFAULT 0)",
                _connection, transaction))
                createTableCommand.ExecuteNonQuery();

            var changed = 0;
            var timestamp = DateTime.UtcNow.Ticks;
            foreach (var (key, value) in items)
            {
                string? existingValue;
                using (var readCommand = new SQLiteCommand(
                    $"SELECT value FROM {sanitizedTableName} WHERE key = @key", _connection, transaction))
                {
                    readCommand.Parameters.AddWithValue("@key", key);
                    existingValue = readCommand.ExecuteScalar() as string;
                }

                if (string.Equals(existingValue, value, StringComparison.Ordinal))
                    continue;

                using var writeCommand = new SQLiteCommand(
                    $"INSERT OR REPLACE INTO {sanitizedTableName} (key, value, updated_at) VALUES (@key, @value, @updatedAt)",
                    _connection, transaction);
                writeCommand.Parameters.AddWithValue("@key", key);
                writeCommand.Parameters.AddWithValue("@value", value);
                writeCommand.Parameters.AddWithValue("@updatedAt", timestamp);
                writeCommand.ExecuteNonQuery();
                changed++;
            }

            transaction.Commit();
            return changed;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 从数据库读取数据
    /// </summary>
    /// <param name="tableName">表名</param>
    /// <param name="key">键</param>
    /// <returns>值，如果不存在则返回null</returns>
    public string? ReadData(string tableName, string key)
    {
        string sanitizedTableName = SanitizeTableName(tableName);

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("SQLite connection is not open. Cannot read data.");
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
                
                // 【UTF-8 验证】确保读出的值是有效的 UTF-8
                if (!string.IsNullOrEmpty(value) && value.Any(c => c > 127))
                {
                    try
                    {
                        // 验证字符串可以正确编码为 UTF-8 并解码回来
                        byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes(value);
                        string utf8Verified = System.Text.Encoding.UTF8.GetString(utf8Bytes);
                        Log.InfoFormat($"[DataIO] ✅ UTF-8 read verified for key '{key}' (length: {utf8Bytes.Length} bytes)");
                        return utf8Verified;
                    }
                    catch (Exception encEx)
                    {
                        Log.Warn($"[DataIO] ⚠️ UTF-8 verification warning for key '{key}': {encEx.Message}, returning value as-is");
                        return value;
                    }
                }
                
                return value;
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read data from table '{sanitizedTableName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 保存二进制数据（BLOB）到表
    /// </summary>
    public void SaveBlob(string tableName, string key, byte[] blob)
    {
        string sanitizedTableName = SanitizeTableName(tableName);

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Error("[DataIO] SQLite connection is not open. Cannot save blob.");
            return;
        }

        EnsureBlobTable(sanitizedTableName);

        try
        {
            string sql = $"INSERT OR REPLACE INTO {sanitizedTableName} (key, blob) VALUES (@key, @blob)";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.Add("@blob", System.Data.DbType.Binary, blob.Length).Value = blob;
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to save blob to table '{sanitizedTableName}': {ex.Message}");
        }
    }

    /// <summary>
    /// 读取二进制数据（BLOB）
    /// </summary>
    public byte[]? ReadBlob(string tableName, string key)
    {
        string sanitizedTableName = SanitizeTableName(tableName);

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("SQLite connection is not open. Cannot read blob.");
            return null;
        }

        try
        {
            string sql = $"SELECT blob FROM {sanitizedTableName} WHERE key = @key";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@key", key);
            var result = command.ExecuteScalar();
            return result as byte[];
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read blob from table '{sanitizedTableName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读取整张表的二进制数据（key -> blob）
    /// </summary>
    public Dictionary<string, byte[]> ReadAllBlobs(string tableName)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        var result = new Dictionary<string, byte[]>();

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("SQLite connection is not open. Cannot read blobs.");
            return result;
        }

        try
        {
            using var checkTableCommand = new SQLiteCommand(
                $"SELECT name FROM sqlite_master WHERE type='table' AND name='{sanitizedTableName}'",
                _connection);
            var exists = checkTableCommand.ExecuteScalar();
            if (exists == null)
            {
                return result;
            }

            string sql = $"SELECT key, blob FROM {sanitizedTableName}";
            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string key = reader["key"].ToString()!;
                var blob = reader["blob"] as byte[];
                if (blob != null)
                {
                    result[key] = blob;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read blobs from table '{sanitizedTableName}': {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 读取表中的所有数据
    /// </summary>
    /// <param name="tableName">表名</param>
    /// <returns>包含所有键值对的字典</returns>
    public Dictionary<string, string> ReadAllData(string tableName)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        var data = new Dictionary<string, string>();

        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Warn("SQLite connection is not open. Cannot read all data.");
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

            Log.InfoFormat($"Read {data.Count} items from table '{sanitizedTableName}'.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read all data from table '{sanitizedTableName}': {ex.Message}");
        }

        return data;
    }

    /// <summary>
    /// 关闭数据库连接
    /// </summary>
    public void Close()
    {
        Log.InfoFormat("[DataIO] 开始关闭数据库连接...");
        
        if (_connection == null)
        {
            Log.InfoFormat("[DataIO] 数据库连接已经为null，无需关闭");
            return;
        }

        try
        {
            var state = _connection.State;
            Log.InfoFormat($"[DataIO] 当前连接状态: {state}");
            
            if (state == System.Data.ConnectionState.Open)
            {
                _connection.Close();
                Log.InfoFormat("[DataIO] 数据库连接已关闭");
                
                _connection.Dispose();
                Log.InfoFormat("[DataIO] 数据库连接已释放");
                
                _connection = null;
                Log.InfoFormat("[DataIO] 数据库连接引用已清空");
            }
            else
            {
                Log.Warn($"[DataIO] 数据库连接未处于打开状态，当前状态: {state}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DataIO] 关闭数据库连接时发生错误: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }
}
