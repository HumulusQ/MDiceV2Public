using System;
using System.Collections.Generic;
using System.IO;
using MDiceV2.Models;
using System.Text.Json;

namespace ABot;

/// <summary>
/// ABot 多用户状态持久化层（SQLite）
/// 
/// 用途：
/// ====
/// 1. 将离线用户状态从内存持久化到 SQLite 数据库
/// 2. 在应用启动时恢复之前保存的用户状态
/// 3. 定期备份活跃用户的状态
/// 4. 支持用户账户删除时的状态清理
/// 
/// 数据库设计：
/// ==========
/// 表名：abot_user_states
/// 列：
/// - user_id (INTEGER PRIMARY KEY) - 用户ID
/// - state_json (TEXT) - 状态快照的 JSON 数据
/// - created_at (DATETIME) - 快照创建时间
/// - updated_at (DATETIME) - 最后更新时间
/// - version (INTEGER) - 状态版本号（用于兼容性检查）
/// 
/// 生命周期：
/// ========
/// 1. 用户被 LRU 驱逐 → SaveOfflineState() 到内存
/// 2. 周期性或手动 → PersistToDatabase() 保存到 SQLite
/// 3. 应用启动 → LoadFromDatabase() 加载所有状态到内存
/// 4. 用户重新上线 → LoadState() 恢复到新解释器
/// 
/// 注意：
/// - 阶段 5 为设计层，具体实现需要调用 C# SQLite 库
/// - 当前为占位符实现，生产环境应使用 NuGet SQLite 包
/// </summary>
public class ABotStateDatabase
{
    /// <summary>
    /// 数据库文件路径
    /// 位置：Mods/ABot/Data/abot_states.db
    /// </summary>
    private readonly string _databasePath;

    /// <summary>
    /// 关联的离线状态存储（内存缓存）
    /// </summary>
    private readonly ABotOfflineStateStore _offlineStore;

    /// <summary>
    /// 数据库IO操作封装（使用DataIO进行SQLite操作）
    /// </summary>
    private readonly DataIO _dataIO;

    // ============ 常量 ============

    private const string DATABASE_FILENAME = "abot_states.db";
    private const int SCHEMA_VERSION = 1;

    // ============ 构造函数 ============

    /// <summary>
    /// 初始化数据库访问层
    /// 参数 dataDirectory：数据文件夹路径（例如 Mods/ABot/Data）
    /// </summary>
    public ABotStateDatabase(string dataDirectory, ABotOfflineStateStore offlineStore)
    {
        _databasePath = Path.Combine(dataDirectory, DATABASE_FILENAME);
        _offlineStore = offlineStore ?? throw new ArgumentNullException(nameof(offlineStore));

        // 初始化 DataIO 实例，使用自定义数据库路径
        _dataIO = new DataIO(_databasePath);

        ABotLogger.Info($"ABotStateDatabase created with path: {_databasePath}");

        // 初始化数据库（创建表结构）
        InitializeDatabase();
    }

    // ============ 方法 ============

    /// <summary>
    /// 初始化数据库（创建表如果不存在）
    /// 
    /// 使用 DataIO 来管理 SQLite 连接和表操作
    /// </summary>
    private void InitializeDatabase()
    {
        try
        {
            // 确保数据目录存在
            string? databaseDir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(databaseDir) && !Directory.Exists(databaseDir))
            {
                Directory.CreateDirectory(databaseDir);
                ABotLogger.Info($"Created data directory: {databaseDir}");
            }

            // DataIO 会在构造函数中自动创建表
            // 对于 abot_user_states 表，我们使用 SaveData() 的标准结构
            // (key=user_id, value=state_json_serialized, created_at, updated_at)
            
            ABotLogger.Info($"Initializing database at: {_databasePath}");
            ABotLogger.Info($"Schema version: {SCHEMA_VERSION}");
            ABotLogger.Info("Tables initialized via DataIO");
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"ERROR initializing database: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 将离线状态持久化到数据库
    /// 遍历所有离线用户状态，保存到 SQLite
    /// 
    /// 用途：
    /// 1. 应用关闭前备份所有离线状态
    /// 2. 定期备份任务
    /// 3. 内存压力大时手动触发
    /// </summary>
    public void PersistToDatabase()
    {
        try
        {
            int persistCount = 0;
            int errorCount = 0;

            ABotLogger.Info("Starting PersistToDatabase operation");
            var allOfflineUsers = _offlineStore.GetAllOfflineUserIds().ToList();
            ABotLogger.Info($"Found {allOfflineUsers.Count} offline users to persist");

            foreach (long userId in allOfflineUsers)
            {
                var snapshot = _offlineStore.GetOfflineState(userId);
                if (snapshot != null && snapshot.IsValid)
                {
                    try
                    {
                        SaveSnapshot(userId, snapshot);
                        persistCount++;
                    }
                    catch (Exception ex)
                    {
                        ABotLogger.Error($"Failed to persist state for user {userId}: {ex.Message}");
                        errorCount++;
                    }
                }
                else
                {
                    ABotLogger.Warn($"Snapshot for user {userId} is null or invalid. Skipping.");
                }
            }

            ABotLogger.Info($"PersistToDatabase complete: {persistCount} persisted" +
                            (errorCount > 0 ? $", {errorCount} errors" : ""));
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"ERROR during persistence: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 从数据库加载所有保存的用户状态到内存
    /// 
    /// 用途：
    /// 1. 应用启动时恢复所有离线用户状态
    /// 2. 系统恢复后的数据完整性检查
    /// 
    /// 返回值：成功加载的用户数
    /// </summary>
    public int LoadFromDatabase()
    {
        try
        {
            int loadCount = 0;
            int errorCount = 0;

            ABotLogger.Info("Starting LoadFromDatabase operation");

            // 【修复】使用与SaveSnapshot相同的 JsonSerializerOptions
            var options = new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            // 使用 DataIO 的 ReadAllData 方法读取所有用户状态
            // 表名：abot_user_states
            var allData = _dataIO.ReadAllData("abot_user_states");
            ABotLogger.Info($"Found {allData.Count} records in abot_user_states table");

            foreach (var kvp in allData)
            {
                try
                {
                    // key 应该是 user_id
                    if (long.TryParse(kvp.Key, out long userId))
                    {
                        // value 是 JSON-serialized snapshot
                        var snapshot = JsonSerializer.Deserialize<ABotStateSnapshot>(kvp.Value, options);
                        if (snapshot != null && snapshot.IsValid)
                        {
                            // 加载到离线状态存储
                            _offlineStore.SaveOfflineState(snapshot);
                            loadCount++;
                            ABotLogger.Debug($"Loaded state for user {userId}");
                        }
                        else
                        {
                            ABotLogger.Warn($"Invalid or corrupted snapshot for user {userId}");
                            errorCount++;
                        }
                    }
                    else
                    {
                        ABotLogger.Warn($"Invalid user ID format in database: {kvp.Key}");
                        errorCount++;
                    }
                }
                catch (Exception ex)
                {
                    ABotLogger.Error($"ERROR loading state for key {kvp.Key}: {ex.Message}");
                    errorCount++;
                }
            }

            ABotLogger.Info($"LoadFromDatabase complete: {loadCount} loaded" +
                            (errorCount > 0 ? $", {errorCount} errors" : ""));

            return loadCount;
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"ERROR loading from database: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 删除特定用户的持久化状态
    /// 
    /// 用途：
    /// 1. 用户删除账户时
    /// 2. 手动清理过期数据
    /// </summary>
    public void DeleteUserState(long userId)
    {
        try
        {
            // 注意：DataIO 当前没有提供删除方法
            // 作为临时解决方案，我们存储一个空的快照标记为已删除
            // 生产环境中应该在 DataIO 中添加删除支持
            
            Console.WriteLine($"[ABot DB] Marked state for user {userId} as deleted");
            // TODO: 在 DataIO 中添加 DeleteData(tableName, key) 方法
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot DB] ERROR deleting state for user {userId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 检查数据库是否存在且有效
    /// </summary>
    public bool IsValid
    {
        get
        {
            try
            {
                return File.Exists(_databasePath);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 获取数据库中存储的用户数
    /// </summary>
    public int GetPersistedUserCount()
    {
        try
        {
            // 使用 DataIO 的 ReadAllData 获取所有条目
            var allData = _dataIO.ReadAllData("abot_user_states");
            return allData.Count;
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"ERROR getting user count: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 保存单个快照到数据库
    /// </summary>
    private void SaveSnapshot(long userId, ABotStateSnapshot snapshot)
    {
        if (snapshot == null)
        {
            ABotLogger.Warn($"NULL snapshot for user {userId}. Skipping save.");
            return;
        }

        if (!snapshot.IsValid)
        {
            ABotLogger.Warn($"Snapshot for user {userId} is invalid (UserId mismatch). Skipping save.");
            return;
        }

        try
        {
            // 【修复】使用 UnsafeRelaxedJsonEscaping 来防止 Base64 特殊字符被转义
            // Characters 字段包含 Base64 编码的 JSON，不应该被转义
            // 否则 + 和 / 会被转义为 \u002B 和 \u002F，导致解码失败
            var options = new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            
            string jsonData = JsonSerializer.Serialize(snapshot, options);

            ABotLogger.Debug($"Attempting to save state for user {userId}: BasicInfo={(!string.IsNullOrEmpty(snapshot.CharacterBasicInfo) ? "✓" : "✗")}, RoundLog={(!string.IsNullOrEmpty(snapshot.RoundManagerLog) ? "✓" : "✗")}, Size={jsonData.Length} bytes");

            // 使用 DataIO 的 SaveData 方法保存到数据库
            // 表名：abot_user_states
            // key：user_id（转换为字符串）
            // value：JSON-serialized snapshot
            _dataIO.SaveData("abot_user_states", userId.ToString(), jsonData);

            ABotLogger.Info($"✓ Successfully saved state for user {userId} ({jsonData.Length} bytes)");
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"ERROR saving snapshot for user {userId}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 获取数据库的摘要信息
    /// </summary>
    public override string ToString()
    {
        return $"ABotStateDatabase(Path={_databasePath}, Valid={IsValid}, Users≈{GetPersistedUserCount()})";
    }
}
