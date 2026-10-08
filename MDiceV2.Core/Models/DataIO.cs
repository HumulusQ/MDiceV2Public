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
public partial class DataIO : ObservableObject, IDisposable
{
    private const int WalAutoCheckpointPages = 256;
    private const long WalRetainedSizeLimitBytes = 16L * 1024L * 1024L;

    private SQLiteConnection? _connection;
    private readonly string _dbPath;
    private readonly object _databaseLock = new();
    private readonly HashSet<string> _initializedTextTables = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initializedBlobTables = new(StringComparer.Ordinal);

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
            // Published builds keep Core in an application-root/Core directory. Resolve the
            // shared data directory from that stable location and also tolerate callers whose
            // working directory is already "data" (which previously produced data/data).
            string dataFolder = ResolveDefaultDataFolder();
            Directory.CreateDirectory(dataFolder); // 确保目录存在
            var canonicalPath = Path.Combine(dataFolder, "MDiceV2.db");
            var legacyNestedPath = Path.Combine(dataFolder, "data", "MDiceV2.db");

            // Never abandon an existing user database just because it was created under the
            // historical data/data path. Fresh installations use the canonical path; an old
            // nested database remains readable and receives the startup WAL recovery.
            if (!File.Exists(canonicalPath) && File.Exists(legacyNestedPath))
            {
                _dbPath = legacyNestedPath;
                Log.Warn($"[DataIO] 检测到旧版嵌套数据路径，将继续使用以避免数据丢失: {_dbPath}");
            }
            else
            {
                _dbPath = canonicalPath;
            }
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

    private static string ResolveDefaultDataFolder()
    {
        var currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(currentDirectory), "data", StringComparison.OrdinalIgnoreCase))
            return currentDirectory;

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(baseDirectory), "Core", StringComparison.OrdinalIgnoreCase))
        {
            var applicationRoot = Directory.GetParent(baseDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(applicationRoot))
                return Path.Combine(applicationRoot, "data");
        }

        return Path.Combine(currentDirectory, "data");
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
            // A pooled native connection can keep the WAL alive after the managed wrapper is
            // closed. This database has one application owner, so pooling provides no benefit.
            string connectionString = $"Data Source={_dbPath};Version=3;UseUTF16Encoding=False;BinaryGUID=False;Pooling=False;";
            _connection = new SQLiteConnection(connectionString);
            _connection.Open();

            // 启用并发优化（WAL模式等）
            EnableConcurrencyOptimizations();

            if (isNewDatabase)
            {
                // 创建基础表结构
                CreateInitialTables();
            }

            // Recover and physically truncate a WAL left by an interrupted previous run.
            // No application reader has been exposed through this instance at this point.
            CheckpointWal("startup");

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

            using (var cmd = new SQLiteCommand($"PRAGMA wal_autocheckpoint = {WalAutoCheckpointPages};", _connection))
            {
                cmd.ExecuteNonQuery();
                Log.InfoFormat($"[DataIO] WAL自动检查点已设置为{WalAutoCheckpointPages}页");
            }

            using (var cmd = new SQLiteCommand($"PRAGMA journal_size_limit = {WalRetainedSizeLimitBytes};", _connection))
            {
                cmd.ExecuteNonQuery();
                Log.InfoFormat($"[DataIO] WAL保留尺寸上限已设置为{WalRetainedSizeLimitBytes}字节");
            }

            using (var cmd = new SQLiteCommand("PRAGMA busy_timeout = 5000;", _connection))
            {
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[DataIO] 启用并发优化失败: {ex.Message}");
        }
    }

    private void EnsureBlobTable(string tableName, SQLiteTransaction? transaction = null)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            Log.Error("[DataIO] 数据库连接未打开，无法创建/确认表");
            throw new InvalidOperationException("SQLite connection is not open");
        }
        if (_initializedBlobTables.Contains(sanitizedTableName))
            return;

        using var createTableCommand = new SQLiteCommand(
            $"CREATE TABLE IF NOT EXISTS {sanitizedTableName} (key TEXT PRIMARY KEY, blob BLOB, updated_at INTEGER DEFAULT 0)",
            _connection, transaction);
        createTableCommand.ExecuteNonQuery();
        _initializedBlobTables.Add(sanitizedTableName);
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
        lock (_databaseLock)
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                Log.Error($"[DataIO] 数据库连接未打开或为空，无法保存数据。Connection state: {_connection?.State.ToString() ?? "null"}");
                return;
            }

            try
            {
                EnsureTextTable(sanitizedTableName, transaction);
                var currentTimestamp = DateTime.UtcNow.Ticks;
                var ownTransaction = transaction == null;
                if (ownTransaction)
                    transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);

                try
                {
                    // Avoid a live SELECT reader at COMMIT. Unchanged values are deliberately
                    // skipped so repeated shutdown snapshots do not create WAL traffic.
                    using var command = new SQLiteCommand($"""
                        INSERT INTO {sanitizedTableName} (key, value, updated_at)
                        VALUES (@key, @value, @updatedAt)
                        ON CONFLICT(key) DO UPDATE SET
                            value = excluded.value,
                            updated_at = excluded.updated_at
                        WHERE excluded.updated_at >= {sanitizedTableName}.updated_at
                          AND {sanitizedTableName}.value IS NOT excluded.value;
                        """, _connection, transaction);
                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@value", value);
                    command.Parameters.AddWithValue("@updatedAt", currentTimestamp);
                    command.ExecuteNonQuery();

                    if (ownTransaction)
                        transaction!.Commit();
                }
                catch
                {
                    if (ownTransaction)
                        transaction?.Rollback();
                    throw;
                }
                finally
                {
                    if (ownTransaction)
                        transaction?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to save data to table '{sanitizedTableName}': {ex.Message}");
            }
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
        var items = dataItems.ToList();

        lock (_databaseLock)
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                Log.Error("[DataIO] 数据库连接未打开，无法批量保存数据");
                return;
            }
            if (items.Count == 0)
                return;

            EnsureTextTable(sanitizedTableName);
            using var transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            try
            {
                var currentTimestamp = DateTime.UtcNow.Ticks;
                using var command = new SQLiteCommand($"""
                    INSERT INTO {sanitizedTableName} (key, value, updated_at)
                    VALUES (@key, @value, @updatedAt)
                    ON CONFLICT(key) DO UPDATE SET
                        value = excluded.value,
                        updated_at = excluded.updated_at
                    WHERE {sanitizedTableName}.value IS NOT excluded.value;
                    """, _connection, transaction);
                var keyParameter = command.Parameters.Add("@key", System.Data.DbType.String);
                var valueParameter = command.Parameters.Add("@value", System.Data.DbType.String);
                command.Parameters.AddWithValue("@updatedAt", currentTimestamp);

                foreach (var (key, value) in items)
                {
                    keyParameter.Value = key;
                    valueParameter.Value = value;
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
                Log.InfoFormat($"[DataIO] 批量检查并保存 {items.Count} 条数据到表 '{sanitizedTableName}'");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Log.Error($"[DataIO] 批量保存失败: {ex.Message}");
            }
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

        if (items.Count == 0)
            return 0;

        lock (_databaseLock)
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
                throw new InvalidOperationException("SQLite connection is not open");

            EnsureTextTable(sanitizedTableName);
            using var transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            try
            {
                var changed = 0;
                var timestamp = DateTime.UtcNow.Ticks;
                using var command = new SQLiteCommand($"""
                    INSERT INTO {sanitizedTableName} (key, value, updated_at)
                    VALUES (@key, @value, @updatedAt)
                    ON CONFLICT(key) DO UPDATE SET
                        value = excluded.value,
                        updated_at = excluded.updated_at
                    WHERE {sanitizedTableName}.value IS NOT excluded.value;
                    """, _connection, transaction);
                var keyParameter = command.Parameters.Add("@key", System.Data.DbType.String);
                var valueParameter = command.Parameters.Add("@value", System.Data.DbType.String);
                command.Parameters.AddWithValue("@updatedAt", timestamp);

                foreach (var (key, value) in items)
                {
                    keyParameter.Value = key;
                    valueParameter.Value = value;
                    changed += command.ExecuteNonQuery();
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
    }

    private void CheckpointWal(string reason)
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            return;

        try
        {
            using var command = new SQLiteCommand("PRAGMA wal_checkpoint(TRUNCATE);", _connection);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return;

            var busy = reader.GetInt32(0);
            var logFrames = reader.GetInt32(1);
            var checkpointedFrames = reader.GetInt32(2);
            if (busy == 0)
                Log.InfoFormat($"[DataIO] WAL检查点完成 ({reason}): {checkpointedFrames}/{logFrames} frames");
            else
                Log.Warn($"[DataIO] WAL检查点被活动连接阻塞 ({reason}): {checkpointedFrames}/{logFrames} frames");
        }
        catch (Exception ex)
        {
            // A failed checkpoint must never prevent the connection from being disposed.
            Log.Warn($"[DataIO] WAL检查点失败 ({reason}): {ex.Message}");
        }
    }

    private void EnsureTextTable(string tableName, SQLiteTransaction? transaction = null)
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection is not open");
        if (_initializedTextTables.Contains(tableName))
            return;

        using (var createCommand = new SQLiteCommand(
                   $"CREATE TABLE IF NOT EXISTS {tableName} (key TEXT PRIMARY KEY, value TEXT, updated_at INTEGER DEFAULT 0)",
                   _connection, transaction))
        {
            createCommand.ExecuteNonQuery();
        }

        var hasUpdatedAt = false;
        using (var schemaCommand = new SQLiteCommand($"PRAGMA table_info({tableName})", _connection, transaction))
        using (var reader = schemaCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader["name"]?.ToString(), "updated_at", StringComparison.OrdinalIgnoreCase))
                {
                    hasUpdatedAt = true;
                    break;
                }
            }
        }

        if (!hasUpdatedAt)
        {
            using var alterCommand = new SQLiteCommand(
                $"ALTER TABLE {tableName} ADD COLUMN updated_at INTEGER DEFAULT 0", _connection, transaction);
            alterCommand.ExecuteNonQuery();
        }

        _initializedTextTables.Add(tableName);
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

        lock (_databaseLock)
        {
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
                if (result == null)
                    return null;

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
            catch (Exception ex)
            {
                Log.Warn($"Failed to read data from table '{sanitizedTableName}': {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 保存二进制数据（BLOB）到表
    /// </summary>
    public void SaveBlob(string tableName, string key, byte[] blob)
    {
        string sanitizedTableName = SanitizeTableName(tableName);

        lock (_databaseLock)
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                Log.Error("[DataIO] SQLite connection is not open. Cannot save blob.");
                return;
            }

            EnsureBlobTable(sanitizedTableName);

            try
            {
                using var command = new SQLiteCommand($"""
                    INSERT INTO {sanitizedTableName} (key, blob)
                    VALUES (@key, @blob)
                    ON CONFLICT(key) DO UPDATE SET blob = excluded.blob
                    WHERE {sanitizedTableName}.blob IS NOT excluded.blob;
                    """, _connection);
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.Add("@blob", System.Data.DbType.Binary, blob.Length).Value = blob;
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to save blob to table '{sanitizedTableName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 读取二进制数据（BLOB）
    /// </summary>
    public byte[]? ReadBlob(string tableName, string key)
    {
        string sanitizedTableName = SanitizeTableName(tableName);

        lock (_databaseLock)
        {
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
    }

    /// <summary>
    /// 读取整张表的二进制数据（key -> blob）
    /// </summary>
    public Dictionary<string, byte[]> ReadAllBlobs(string tableName)
    {
        string sanitizedTableName = SanitizeTableName(tableName);
        var result = new Dictionary<string, byte[]>();

        lock (_databaseLock)
        {
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
                    return result;

                string sql = $"SELECT key, blob FROM {sanitizedTableName}";
                using var command = new SQLiteCommand(sql, _connection);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader["key"].ToString()!;
                    var blob = reader["blob"] as byte[];
                    if (blob != null)
                        result[key] = blob;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to read blobs from table '{sanitizedTableName}': {ex.Message}");
            }
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

        lock (_databaseLock)
        {
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
        }

        return data;
    }

    /// <summary>
    /// 关闭数据库连接
    /// </summary>
    public void Close()
    {
        lock (_databaseLock)
        {
            Log.InfoFormat("[DataIO] 开始关闭数据库连接...");
            var connection = _connection;

            if (connection == null)
            {
                Log.InfoFormat("[DataIO] 数据库连接已经为null，无需关闭");
                return;
            }

            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                    CheckpointWal("shutdown");
            }
            finally
            {
                // Dispose regardless of Open/Closed/Broken state. The previous implementation
                // leaked every connection that had already transitioned away from Open.
                _connection = null;
                try
                {
                    if (connection.State != System.Data.ConnectionState.Closed)
                        connection.Close();
                }
                finally
                {
                    connection.Dispose();
                    _initializedTextTables.Clear();
                    _initializedBlobTables.Clear();
                    Log.InfoFormat("[DataIO] 数据库连接已关闭并释放");
                }
            }
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
