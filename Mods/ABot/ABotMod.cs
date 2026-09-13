using System;
using System.Collections.Generic;
using System.Text;
using MDiceV2.Interfaces;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.IO;

namespace ABot;

/// <summary>
/// 文件日志助手 - 用于诊断早期初始化阶段
/// </summary>
internal static class FileLogger
{
    private static readonly string _logPath = Path.Combine(
        Path.GetTempPath(), 
        "abot_init.log"
    );
    
    public static void Log(string message)
    {
        try
        {
            // 确保目录存在
            string? directory = Path.GetDirectoryName(_logPath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            string logLine = $"{message}\n";
            
            // 尝试追加到文件
            if (!File.Exists(_logPath))
            {
                File.WriteAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] ========== ABot Initialization Log Started ==========\n", System.Text.Encoding.UTF8);
            }
            
            File.AppendAllText(_logPath, logLine, System.Text.Encoding.UTF8);
            
            // 同时输出到控制台
            Console.WriteLine($"[FileLogger] {message}");
        }
        catch (Exception ex)
        {
            // 失败时至少输出到控制台
            Console.WriteLine($"[FileLogger ERROR] Failed to write log: {ex.Message}");
            Console.WriteLine($"[FileLogger ERROR] Log path: {_logPath}");
            Console.WriteLine($"[FileLogger ERROR] Message was: {message}");
        }
    }
}

/// <summary>
/// ABOT 解释器 Mod - 作为MDiceV2的战斗脚本系统
/// 
/// 功能介绍：
/// =========
/// 这是一个完整的ABOT脚本解释器，提供：
/// 1. ABOT脚本的解析和编译
/// 2. 战斗系统的字节码执行
/// 3. 动态脚本支持和扩展
/// 
/// Mod工作流程：
/// ===========
/// 1. 程序启动 -> 调用OnLoad()初始化解释器
/// 2. 用户启用Mod -> 调用OnEnable()准备环境
/// 3. 执行脚本 -> 调用Interpreter.Execute()
/// 4. 用户禁用Mod -> 调用OnDisable()清理状态
/// 5. 程序关闭 -> 调用OnUnload()释放C++资源
/// 
/// 架构特点：
/// =========
/// - 三层设计：C# Mod接口 -> C++/CLI包装 -> C++核心
/// - 零迁移成本：核心代码可无缝从C++/CLI迁移到P/Invoke
/// - 完整编程语言：支持完整的ABOT脚本语法
/// - 高性能：字节码编译和虚拟机执行
/// </summary>
public class ABotMod : IModPlugin, INavigationPanelProvider, ICommandProvider
{
    // ============ IModPlugin 属性实现 ============
    
    /// <summary>
    /// Mod唯一标识符
    /// 建议格式：com.author.modname
    /// </summary>
    public string ModId => "com.abot.interpreter";

    /// <summary>
    /// Mod显示名称
    /// 在UI的Mod管理面板中显示
    /// </summary>
    public string ModName => "ABOT Interpreter";

    /// <summary>
    /// Mod版本号
    /// 遵循语义化版本：major.minor.patch
    /// </summary>
    public string Version => "0.1.0";

    /// <summary>
    /// Mod描述信息
    /// </summary>
    public string Description => "Battle Orchestration Toolkit (ABOT) script interpreter for MDiceV2. " +
                                 "Provides complete scripting support for battle simulation and AI logic.";

    /// <summary>
    /// Mod作者
    /// </summary>
    public string Author => "ABOT Development Team";

    // ============ INavigationPanelProvider 属性实现 ============
    
    /// <summary>
    /// 导航面板唯一标识符
    /// </summary>
    public string PanelId => "com.abot.interpreter.panel";

    /// <summary>
    /// 导航面板显示name
    /// </summary>
    public string PanelName => "ABOT Interpreter";

    /// <summary>
    /// 面板在导航栏中的优先级（数值越大越靠前）
    /// </summary>
    public int Priority => 90;

    /// <summary>
    /// 面板的icon来源（暂不使用）
    /// </summary>
    public string? IconSource => null;

    /// <summary>
    /// 是否为Mod面板（区别于系统面板）
    /// </summary>
    public bool IsModPanel => true;

    // ============ 私有字段 ============
    
    /// <summary>
    /// LRU 缓存池最大用户数
    /// </summary>
    private const int MAX_POOL_SIZE = 5;

    /// <summary>
    /// 单个解释器实例（暂现实现，阶段2改为 LRU 缓存池）
    /// 用于多用户隔离
    /// </summary>
    private ABotInterpreter? _interpreter;
    
    /// <summary>
    /// 当前执行命令的用户 ID（多用户隔离）
    /// 在处理 .abot 命令时由 HandleAbotCommand 设置
    /// </summary>
    private long _currentUserId = 0;
    
    /// <summary>
    /// 用户解释器映射池（LRU 缓存实现，最多5个活跃用户）
    /// 键：用户ID，值：该用户的解释器实例
    /// </summary>
    private Dictionary<long, ABotInterpreter> _interpreterPool = new();
    
    /// <summary>
    /// LRU 访问顺序追踪（LinkedList）
    /// 最近访问的用户在尾部，最旧的用户在头部
    /// 当超过 MAX_POOL_SIZE 时，移除头部用户
    /// </summary>
    private LinkedList<long> _lruOrder = new();
    
    /// <summary>
    /// LRU 节点映射
    /// 键：用户ID，值：该用户在 _lruOrder 中的节点
    /// 用于快速定位和移动节点
    /// </summary>
    private Dictionary<long, LinkedListNode<long>> _lruNodes = new();
    
    /// <summary>
    /// 离线用户状态存储（多用户隔离支持）
    /// 当 LRU 缓存驱逐一个用户时，该用户的状态被保存到此存储
    /// 用于阶段 5 的数据库持久化
    /// </summary>
    private ABotOfflineStateStore _offlineStateStore = new();
    
    /// <summary>
    /// 用户状态数据库访问层（多用户隔离支持）
    /// 将离线用户状态持久化到 SQLite 文件
    /// 支持应用启动时恢复历史用户状态
    /// </summary>
    private ABotStateDatabase? _stateDatabase;

    // ============ 初始化 ============

    /// <summary>
    /// 构造函数 - 初始化日志系统
    /// </summary>
    public ABotMod()
    {
        // 初始化文件日志系统
        ABotLogger.Initialize();
        ABotLogger.Info("ABotMod instance created");
    }
    
    
    private IModContext? _context;
    private bool _isEnabled = false;

    // ============ 构造函数 ============

    /// <summary>
    /// ABotMod 构造函数
    /// 在Mod实例化时由宿主程序调用
    /// </summary>
    public ABotMod(IModContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] ABotMod constructor called");
    }

    // ============ 生命周期方法 ============

    /// <summary>
    /// Mod加载阶段
    /// 在此初始化解释器和基础資源
    /// 此时Mod可能还未启用
    /// </summary>
    public void OnLoad()
    {
        try
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> OnLoad() START");
            Console.WriteLine("[ABot] >>> OnLoad() START");
            _context!.Log(LogLevel.Info, "[ABot] Starting initialization...");
            
            // 创建解释器实例
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Creating ABotInterpreter instance...");
            Console.WriteLine("[ABot] >>> Creating ABotInterpreter instance...");
            _interpreter = new ABotInterpreter();
            
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] ABotInterpreter instance created");
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Interpreter is null: {_interpreter == null}");
            Console.WriteLine("[ABot] >>> ABotInterpreter instance created successfully");
            
            // NEW: 检查初始化是否成功
            string? loadError = _interpreter?.GetLoadError();
            if (loadError != null)
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] CRITICAL: C++/CLI interop failed: {loadError}");
                _context.Log(LogLevel.Error, $"[ABot] CRITICAL: C++/CLI interop failed: {loadError}");
                _context.Log(LogLevel.Warn, "[ABot] ABotMod will not be functional until C++ layer is properly built and deployed");
                _context.Log(LogLevel.Info, "[ABot] Expected files: ABot.CLI.dll, ABot.Core.dll in application runtime directory");
                // 不抛出异常，允许程序继续运行 - 面板会显示错误信息
            }
            else
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] C++/CLI interop initialized successfully");
                _context.Log(LogLevel.Info, "[ABot] C++/CLI interop initialized successfully");
            }
            
            // 立即尝试注册导航面板
            // 注：即使C++层失败，仍然会注册面板，但面板会显示错误
            Console.WriteLine("[ABot] >>> About to call RegisterNavigationPanel()");
            RegisterNavigationPanel();
            Console.WriteLine("[ABot] >>> RegisterNavigationPanel() completed");
            
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> OnLoad() END - SUCCESS");
            Console.WriteLine("[ABot] >>> OnLoad() END - SUCCESS");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> OnLoad() EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[ABot] >>> OnLoad() EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[ABot] >>> StackTrace: {ex.StackTrace}");
            _context!.Log(LogLevel.Error, $"[ABot] Failed to load interpreter: {ex.Message}");
            _context.Log(LogLevel.Error, $"[ABot] Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Mod启用阶段
    /// 在此准备处理消息或执行业务逻辑
    /// </summary>
    public void OnEnable()
    {
        if (_interpreter == null)
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] OnEnable() - Interpreter is null!");
            ABotLogger.Error("Interpreter not initialized, cannot enable");
            _context?.Log(LogLevel.Warn, "[ABot] Interpreter not initialized, cannot enable");
            return;
        }

        try
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> OnEnable() START");
            ABotLogger.Info("OnEnable() START");
            _isEnabled = true;
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] _isEnabled set to true");
            ABotLogger.Info("Interpreter enabled, _isEnabled = true");
            _context?.Log(LogLevel.Info, "[ABot] Interpreter enabled");
            
            // === 初始化数据库（阶段 5 特性） ===
            try
            {
                // 统一数据目录：data/ABot/
                string dataDirectory = Path.Combine(
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")),
                    "data",
                    "ABot"
                );
                
                ABotLogger.Info($"Initializing state database at {dataDirectory}");
                _stateDatabase = new ABotStateDatabase(dataDirectory, _offlineStateStore);
                _context?.Log(LogLevel.Info, $"[ABot] State database initialized at {dataDirectory}");
                
                // 从数据库恢复历史用户状态到内存
                ABotLogger.Info("Loading user states from database...");
                int recoveredUsers = _stateDatabase.LoadFromDatabase();
                if (recoveredUsers > 0)
                {
                    ABotLogger.Info($"Recovered {recoveredUsers} user states from database");
                    _context?.Log(LogLevel.Info, $"[ABot] Recovered {recoveredUsers} user states from database");
                }
                else
                {
                    ABotLogger.Info("No user states found in database");
                }
            }
            catch (Exception dbEx)
            {
                ABotLogger.Error($"Failed to initialize state database: {dbEx.Message}");
                _context?.Log(LogLevel.Warn, $"[ABot] Failed to initialize state database: {dbEx.Message}");
                // 继续运行，不让数据库初始化失败导致 Mod 无法启用
            }
            
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> OnEnable() END - SUCCESS");
            ABotLogger.Info("OnEnable() END - SUCCESS");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] OnEnable() EXCEPTION: {ex.Message}");
            ABotLogger.Error($"OnEnable() EXCEPTION: {ex.Message}");
            _context?.Log(LogLevel.Error, $"[ABot] Failed to enable: {ex.Message}");
            _isEnabled = false;
        }
    }

    /// <summary>
    /// Mod禁用阶段
    /// 在此停止处理消息或执行业务逻辑
    /// 
    /// 关闭前自动保存：
    /// - 所有活跃用户（5个） → 转移到离线存储
    /// - 所有离线用户（最多100个） → 持久化到数据库
    /// 
    /// 保证程序关闭时不会丢失任何用户的游戏进度
    /// </summary>
    public void OnDisable()
    {
        if (!_isEnabled)
            return;

        try
        {
            // === 步骤 1：保存所有活跃用户的状态到离线存储 ===
            try
            {
                int activeCount = _interpreterPool.Count;
                if (activeCount > 0)
                {
                    ABotLogger.Info($"Saving {activeCount} active users before shutdown...");
                    _context?.Log(LogLevel.Info, $"[ABot] Saving {activeCount} active users before shutdown...");
                    
                    var userIds = new List<long>(_interpreterPool.Keys);  // 避免迭代时修改集合
                    int savedCount = 0;
                    int failCount = 0;

                    foreach (long userId in userIds)
                    {
                        try
                        {
                            if (_interpreterPool.TryGetValue(userId, out var interpreter))
                            {
                                ABotLogger.Debug($"Saving state for active user {userId}...");
                                var snapshot = interpreter.SaveState(userId);
                                ABotLogger.Debug($"State captured for user {userId}, adding to offline store");
                                _offlineStateStore.SaveOfflineState(snapshot);
                                savedCount++;
                            }
                        }
                        catch (Exception saveEx)
                        {
                            ABotLogger.Error($"Failed to save active user {userId}: {saveEx.Message}");
                            _context?.Log(LogLevel.Warn, $"[ABot] Failed to save active user {userId}: {saveEx.Message}");
                            failCount++;
                        }
                    }

                    ABotLogger.Info($"Saved {savedCount} active users to offline storage" +
                                                (failCount > 0 ? $" ({failCount} failed)" : ""));
                    _context?.Log(LogLevel.Info, $"[ABot] Saved {savedCount} active users to offline storage" +
                                                (failCount > 0 ? $" ({failCount} failed)" : ""));
                }
                else
                {
                    ABotLogger.Info("No active users to save");
                }
            }
            catch (Exception activeEx)
            {
                ABotLogger.Error($"Error saving active users: {activeEx.Message}");
                _context?.Log(LogLevel.Warn, $"[ABot] Error saving active users: {activeEx.Message}");
                // 继续执行其他保存步骤，不中断关闭流程
            }

            // === 步骤 2：持久化所有离线状态到数据库 ===
            try
            {
                if (_stateDatabase != null)
                {
                    int totalOfflineCount = _offlineStateStore.OfflineStateCount;
                    ABotLogger.Info($"Persisting {totalOfflineCount} total offline states to database...");
                    _context?.Log(LogLevel.Info, $"[ABot] Persisting {totalOfflineCount} total offline states to database...");
                    
                    _stateDatabase.PersistToDatabase();
                    
                    ABotLogger.Info("All user states persisted successfully");
                    _context?.Log(LogLevel.Info, "[ABot] All user states persisted successfully");
                }
                else
                {
                    ABotLogger.Warn("StateDatabase is null, cannot persist");
                }
            }
            catch (Exception dbEx)
            {
                ABotLogger.Error($"Failed to persist states to database: {dbEx.Message}\n{dbEx.StackTrace}");
                _context?.Log(LogLevel.Warn, $"[ABot] Failed to persist states to database: {dbEx.Message}");
                // 继续禁用，不让数据库错误阻止 Mod 关闭
            }
            
            ABotLogger.Info("Interpreter disabled and all states saved");
            _isEnabled = false;
            _context?.Log(LogLevel.Info, "[ABot] Interpreter disabled and all states saved");
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[ABot] Failed to disable: {ex.Message}");
        }
    }

    /// <summary>
    /// Mod卸载阶段
    /// 在此释放所有资源，包括C++对象
    /// </summary>
    public void OnUnload()
    {
        ABotLogger.Info("OnUnload() START - Program is shutting down");
        
        try
        {
            // === 步骤 1：保存所有活跃用户的状态到离线存储 ===
            try
            {
                int activeCount = _interpreterPool.Count;
                if (activeCount > 0)
                {
                    ABotLogger.Info($"Saving {activeCount} active users before shutdown...");
                    _context?.Log(LogLevel.Info, $"[ABot] Saving {activeCount} active users before shutdown...");
                    
                    var userIds = new List<long>(_interpreterPool.Keys);
                    int savedCount = 0;
                    int failCount = 0;

                    foreach (long userId in userIds)
                    {
                        try
                        {
                            if (_interpreterPool.TryGetValue(userId, out var interpreter))
                            {
                                ABotLogger.Debug($"Saving state for active user {userId}...");
                                var snapshot = interpreter.SaveState(userId);
                                _offlineStateStore.SaveOfflineState(snapshot);
                                savedCount++;
                            }
                        }
                        catch (Exception saveEx)
                        {
                            ABotLogger.Error($"Failed to save active user {userId}: {saveEx.Message}");
                            failCount++;
                        }
                    }

                    ABotLogger.Info($"Saved {savedCount} active users to offline storage" +
                                                (failCount > 0 ? $" ({failCount} failed)" : ""));
                }
                else
                {
                    ABotLogger.Info("No active users to save");
                }
            }
            catch (Exception activeEx)
            {
                ABotLogger.Error($"Error saving active users: {activeEx.Message}");
            }

            // === 步骤 2：持久化所有离线状态到数据库 ===
            try
            {
                if (_stateDatabase != null)
                {
                    int totalOfflineCount = _offlineStateStore.OfflineStateCount;
                    ABotLogger.Info($"Persisting {totalOfflineCount} total offline states to database...");
                    _context?.Log(LogLevel.Info, $"[ABot] Persisting {totalOfflineCount} total offline states to database...");
                    
                    _stateDatabase.PersistToDatabase();
                    
                    ABotLogger.Info("✓ All user states persisted successfully to database");
                    _context?.Log(LogLevel.Info, "[ABot] All user states persisted successfully");
                }
                else
                {
                    ABotLogger.Warn("StateDatabase is null, cannot persist states");
                }
            }
            catch (Exception dbEx)
            {
                ABotLogger.Error($"Failed to persist states to database: {dbEx.Message}");
                _context?.Log(LogLevel.Warn, $"[ABot] Failed to persist states to database: {dbEx.Message}");
            }

            // === 步骤 3：清理C++资源 ===
            try
            {
                if (_interpreter != null)
                {
                    ABotLogger.Info("Cleaning up interpreter resources...");
                    _context?.Log(LogLevel.Info, "[ABot] Cleaning up interpreter...");
                    _interpreter.Dispose();
                    _interpreter = null;
                    ABotLogger.Info("Interpreter disposed successfully");
                    _context?.Log(LogLevel.Info, "[ABot] Interpreter unloaded");
                }
            }
            catch (Exception interpEx)
            {
                ABotLogger.Error($"Error disposing interpreter: {interpEx.Message}");
                _context?.Log(LogLevel.Error, $"[ABot] Failed to unload: {interpEx.Message}");
            }

            ABotLogger.Info("OnUnload() END - ABot cleanup completed");
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"OnUnload() EXCEPTION: {ex.Message}");
            _context?.Log(LogLevel.Error, $"[ABot] Failed to unload: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理群组消息
    /// </summary>
    public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
    {
        if (!_isEnabled || _interpreter == null)
        {
            return null;
        }

        try
        {
            // 当前版本中，ABOT仅用于战斗脚本执行，不处理群组消息
            // 未来可扩展为支持基于ABOT脚本的自动回复
            return null;
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[ABot] Error processing group message: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 处理私聊消息
    /// </summary>
    public ModMessageResult? OnPrivateMessage(long userId, string content)
    {
        if (!_isEnabled || _interpreter == null)
        {
            return null;
        }

        try
        {
            // 当前版本中，ABOT仅用于战斗脚本执行，不处理私聊消息
            // 未来可扩展为支持基于ABOT脚本的自动回复
            return null;
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[ABot] Error processing private message: {ex.Message}");
            return null;
        }
    }

    // ============ 公开API ============

    /// <summary>
    /// 获取解释器实例
    /// 用于外部代码调用解释器功能
    /// </summary>
    public ABotInterpreter? GetInterpreter()
    {
        if (!_isEnabled)
        {
            _context?.Log(LogLevel.Warn, "[ABot] Attempting to access interpreter while mod is disabled");
            return null;
        }
        return _interpreter;
    }

    /// <summary>
    /// 获取或创建指定用户的解释器实例（LRU 缓存池实现）
    /// 
    /// 实现流程：
    /// 1. 如果用户的解释器已在池中，移动到 LRU 尾部（标记为最近访问）并返回
    /// 2. 如果不在：
    ///    a. 创建新解释器实例
    ///    b. 初始化解释器（加载预设等）
    ///    c. 如果池已满（5个），驱逐最旧的用户（LRU 头部）
    ///    d. 将新用户添加到池和 LRU 尾部
    /// 3. 返回用户的解释器
    /// 
    /// LRU 策略：
    /// - 最近访问的用户保留在缓存中
    /// - 当达到 5 人上限时，最久未访问的用户被驱逐
    /// - 每次访问都更新最近访问时间
    /// </summary>
    private ABotInterpreter GetOrCreateInterpreter(long userId)
    {
        // === 情景 1：用户已在池中 ===
        if (_interpreterPool.TryGetValue(userId, out var existingInterp))
        {
            // 更新 LRU 顺序：将用户节点移到末尾（标记为最近访问）
            if (_lruNodes.TryGetValue(userId, out var node))
            {
                _lruOrder.Remove(node);
                _lruOrder.AddLast(userId);
                _lruNodes[userId] = _lruOrder.Last!;
            }
            
            ABotLogger.Debug($"[CACHE HIT] Retrieved interpreter for user {userId} from LRU pool (size: {_interpreterPool.Count}/{MAX_POOL_SIZE})");
            _context?.Log(LogLevel.Debug, $"[ABot] [CACHE HIT] Retrieved interpreter for user {userId} from LRU pool (size: {_interpreterPool.Count}/{MAX_POOL_SIZE})");
            return existingInterp;
        }

        // === 情景 2：用户不在池中，需要创建 ===
        ABotLogger.Info($"[CACHE MISS] User {userId} not in active pool. Offline state count: {_offlineStateStore.OfflineStateCount}");
        _context?.Log(LogLevel.Info, $"[ABot] [CACHE MISS] User {userId} not in active pool. Offline state count: {_offlineStateStore.OfflineStateCount}");
        
        ABotLogger.Info($"[NEW INTERP] Creating new interpreter instance for user {userId}");
        var newInterp = new ABotInterpreter();
        
        // 检查初始化是否成功
        string? loadError = newInterp.GetLoadError();
        if (loadError != null)
        {
            ABotLogger.Error($"[INIT FAIL] Failed to initialize interpreter for user {userId}: {loadError}");
            _context?.Log(LogLevel.Error, $"[ABot] [INIT FAIL] Failed to initialize interpreter for user {userId}: {loadError}");
            throw new InvalidOperationException($"Failed to initialize interpreter: {loadError}");
        }

        // === 检查是否需要驱逐（LRU 满了） ===
        if (_interpreterPool.Count >= MAX_POOL_SIZE)
        {
            // 驱逐最旧的用户（LRU 头部）
            long lruUserId = _lruOrder.First!.Value;
            
            ABotLogger.Warn($"[EVICTION] LRU pool full ({MAX_POOL_SIZE} users). Evicting user {lruUserId}");
            
            // 在驱逐前保存用户状态（阶段 3：存储到内存）
            try
            {
                if (_interpreterPool.TryGetValue(lruUserId, out var evictedInterp))
                {
                    ABotLogger.Info($"[SAVE STATE] Saving state for evicted user {lruUserId}...");
                    var stateSnapshot = evictedInterp.SaveState(lruUserId);
                    
                    if (stateSnapshot == null)
                    {
                        ABotLogger.Error($"[SAVE FAIL] SaveState returned null for user {lruUserId}");
                    }
                    else if (!stateSnapshot.IsValid)
                    {
                        ABotLogger.Warn($"[SAVE INVALID] SaveState returned invalid snapshot for user {lruUserId} (UserId={stateSnapshot.UserId})");
                    }
                    else
                    {
                        ABotLogger.Info($"[SAVE OK] Snapshot captured: UserId={stateSnapshot.UserId}, Size={stateSnapshot.EstimatedSizeBytes} bytes");
                        _offlineStateStore.SaveOfflineState(stateSnapshot);
                        ABotLogger.Info($"[OFFLINE STORE] Saved offline state for evicted user {lruUserId}. Total offline: {_offlineStateStore.OfflineStateCount}");
                    }
                }
            }
            catch (Exception saveEx)
            {
                ABotLogger.Error($"[EVICTION ERROR] Failed to save offline state for user {lruUserId}: {saveEx.Message}\n{saveEx.StackTrace}");
                _context?.Log(LogLevel.Warn, $"[ABot] Failed to save offline state for user {lruUserId}: {saveEx.Message}");
                // 继续驱逐，即使保存失败
            }
            
            _lruOrder.RemoveFirst();
            _interpreterPool.Remove(lruUserId);
            _lruNodes.Remove(lruUserId);
            
            ABotLogger.Info($"[EVICTED] User {lruUserId} removed from active pool. Active: {_interpreterPool.Count}, Offline: {_offlineStateStore.OfflineStateCount}");
            _context?.Log(LogLevel.Info, $"[ABot] LRU pool full ({MAX_POOL_SIZE} users). Evicted oldest user {lruUserId}. Offline states: {_offlineStateStore.OfflineStateCount}");
        }

        // === 添加新用户到池和 LRU ===
        _interpreterPool[userId] = newInterp;
        var newNode = _lruOrder.AddLast(userId);
        _lruNodes[userId] = newNode;
        
        // === 尝试恢复离线状态（阶段 4 特性） ===
        ABotLogger.Info($"[RESTORE] Attempting to restore offline state for user {userId}...");
        try
        {
            var offlineSnapshot = _offlineStateStore.GetOfflineState(userId);
            if (offlineSnapshot == null)
            {
                ABotLogger.Info($"[RESTORE SKIP] No offline state found for user {userId}. Using fresh interpreter.");
                _context?.Log(LogLevel.Info, $"[ABot] No offline state for user {userId}, starting fresh");
            }
            else if (!offlineSnapshot.IsValid)
            {
                ABotLogger.Warn($"[RESTORE INVALID] Offline snapshot for user {userId} is invalid (UserId={offlineSnapshot.UserId}). Using fresh interpreter.");
                _context?.Log(LogLevel.Warn, $"[ABot] Offline snapshot invalid for user {userId}");
            }
            else
            {
                ABotLogger.Info($"[RESTORE LOAD] Snapshot found: UserId={offlineSnapshot.UserId}, Size={offlineSnapshot.EstimatedSizeBytes} bytes. Loading...");
                bool loadSuccess = newInterp.LoadState(offlineSnapshot);
                
                if (loadSuccess)
                {
                    ABotLogger.Info($"[RESTORE OK] ✓ Successfully restored state for user {userId}");
                    _context?.Log(LogLevel.Info, $"[ABot] ✓ Successfully restored offline state for user {userId}");
                    _offlineStateStore.RemoveOfflineState(userId);  // 恢复后删除离线状态
                }
                else
                {
                    ABotLogger.Warn($"[RESTORE FAIL] ✗ LoadState returned false for user {userId}. Using fresh interpreter instead.");
                    ABotLogger.Warn($"[RESTORE FAIL] Possible reasons: RoundManager not in snapshot, C++ deserialization failed, or incompatible snapshot format");
                    _context?.Log(LogLevel.Warn, $"[ABot] Failed to restore offline state for user {userId}, using fresh interpreter instead");
                }
            }
        }
        catch (Exception restoreEx)
        {
            ABotLogger.Error($"[RESTORE EXCEPTION] Exception while restoring offline state for user {userId}: {restoreEx.Message}\n{restoreEx.StackTrace}");
            _context?.Log(LogLevel.Warn, $"[ABot] Exception while restoring offline state for user {userId}: {restoreEx.Message}");
            // 继续使用新鲜解释器，不中断流程
        }
        
        ABotLogger.Info($"[READY] Interpreter for user {userId} ready. Active: {_interpreterPool.Count}/{MAX_POOL_SIZE}");
        _context?.Log(LogLevel.Info, $"[ABot] Interpreter for user {userId} created and stored in LRU pool (size: {_interpreterPool.Count}/{MAX_POOL_SIZE})");

        return newInterp;
    }

    /// <summary>
    /// 检查Mod是否就绪
    /// </summary>
    public bool IsReady => _isEnabled && _interpreter != null;
    
    /// <summary>
    /// 获取Mod的启用状态（用于面板诊断和初始化）
    /// </summary>
    public bool IsEnabled => _isEnabled;

    /// <summary>
    /// 获取此Mod提供的所有命令处理器
    /// 实现ICommandProvider接口以支持群聊命令
    /// </summary>
    public Dictionary<string, Func<string, object, string?>> GetCommandHandlers()
    {
        var handlers = new Dictionary<string, Func<string, object, string?>>();
        
        if (!_isEnabled || _interpreter == null)
        {
            _context?.Log(LogLevel.Warn, "[ABot] GetCommandHandlers called but Mod is not ready");
            return handlers;
        }
        
        try
        {
            // 注册 .abot 命令处理器
            handlers["abot"] = HandleAbotCommand;
            _context?.Log(LogLevel.Info, "[ABot] Registered command handler: 'abot'");
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[ABot] Failed to register command handlers: {ex.Message}");
        }
        
        return handlers;
    }

    /// <summary>
    /// 处理 .abot 命令
    /// 支持的子命令：
    ///   .abot script [ABOL代码] - 执行ABOT脚本
    ///   .abot nr - 执行下一回合
    /// 
    /// 返回值是要发送给用户的回复内容，由MessageProcessor负责发送
    /// 
    /// 多用户隔离：
    /// 从 msg.UserId 提取用户标识，为每个用户维护独立的解释器实例
    /// </summary>
    private string? HandleAbotCommand(string args, object msgObj)
    {
        // 类型转换：从object转为Msg
        var msg = msgObj as Msg;
        if (msg == null)
        {
            _context?.Log(LogLevel.Warn, "[ABot] HandleAbotCommand received invalid message object");
            return "[ABot] 内部错误：消息对象无效";
        }

        // ⭐ 阶段1修改：捕获用户ID用于多用户隔离
        _currentUserId = msg.UserId;
        _context?.Log(LogLevel.Info, $"[ABot] HandleAbotCommand called for user {_currentUserId} with args length={args?.Length ?? -1}");

        try
        {
            if (!string.IsNullOrEmpty(args))
            {
                _context?.Log(LogLevel.Info, $"[ABot] args content: {args}");
            }
            
            var trimmed = args?.Trim() ?? "";
            _context?.Log(LogLevel.Info, $"[ABot] trimmed content length={trimmed.Length}");
            
            if (trimmed.StartsWith("script ", StringComparison.OrdinalIgnoreCase))
            {
                // .abot script [ABOL代码]
                string scriptContent = trimmed.Substring(7).Trim();
                _context?.Log(LogLevel.Info, $"[ABot] script content length after substring={scriptContent.Length}");
                _context?.Log(LogLevel.Info, $"[ABot] script first 100 chars: {scriptContent.Substring(0, Math.Min(100, scriptContent.Length))}");
                
                if (string.IsNullOrEmpty(scriptContent))
                {
                    return "[ABot] 错误：未提供脚本。用法: .abot script [ABOL_CODE]";
                }
                return ExecuteAbotScript(scriptContent);
            }
            else if (trimmed.Equals("nr", StringComparison.OrdinalIgnoreCase) || 
                     trimmed.Equals("next round", StringComparison.OrdinalIgnoreCase))
            {
                // .abot nr - 执行下一回合
                return ExecuteNextRound();
            }
            else
            {
                // 显示帮助信息
                return "[ABot] 可用命令:\n" +
                       "  .abot script [ABOL_CODE] - 执行ABOT战斗脚本\n" +
                       "  .abot nr - 执行下一回合";
            }
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[ABot] Error in HandleAbotCommand: {ex.Message}");
            return $"[ABot] 命令执行错误: {ex.Message}";
        }
    }

    /// <summary>
    /// 执行ABOT脚本
    /// 返回执行结果的字符串
    /// 使用与ABotPanel完全相同的预处理流程
    /// 
    /// 多用户隔离：使用 GetOrCreateInterpreter(_currentUserId) 获取该用户的独立解释器
    /// </summary>
    private string? ExecuteAbotScript(string scriptContent)
    {
        try
        {
            _context?.Log(LogLevel.Info, "[ABot] Executing script from chat command");
            
            // ⭐ 阶段1修改：获取当前用户的解释器（多用户隔离）
            var interpreter = GetOrCreateInterpreter(_currentUserId);
            if (interpreter == null)
            {
                return "[ABot] 错误：解释器未初始化";
            }
            
            // ⭐ 关键修复：清除旧的战斗状态
            // 当执行 .abot script 时，应该清空该用户的历史角色
            _context?.Log(LogLevel.Info, "[ABot] Clearing previous battle state before starting new script...");
            int clearResult = interpreter.ClearAllCharacters();
            if (clearResult != 0 && clearResult != 1)
            {
                _context?.Log(LogLevel.Warn, $"[ABot] Warning: Failed to clear previous characters (code {clearResult}), continuing anyway...");
            }
            _context?.Log(LogLevel.Info, "[ABot] Previous battle state cleared successfully");
            
            // 步骤1：为 expr(...) 内容进行 Base64 编码
            _context?.Log(LogLevel.Info, "[ABot] Step 1: Preprocessing expression attributes (Base64 encoding)...");
            string processedScript = PreprocessExpressionAttributes(scriptContent);
            _context?.Log(LogLevel.Info, $"[ABot] After preprocessing: {processedScript.Length} characters");
            
            // 步骤2：提取参数卡片（Phase 3+ 支持）
            _context?.Log(LogLevel.Info, "[ABot] Step 2: Extracting parameter cards...");
            var parameterCards = ExtractParameterCards(processedScript);
            
            if (parameterCards.Count == 0)
            {
                // 纯脚本模式：直接执行
                _context?.Log(LogLevel.Info, "[ABot] No parameter cards detected, executing as pure script");
                int result = interpreter.ExecuteScript(processedScript);
                
                if (result == 0)
                {
                    return "[ABot] ✓ 脚本执行成功";
                }
                else
                {
                    string errorMsg = interpreter.GetLastError() ?? "未知错误";
                    return $"[ABot] ✗ 脚本执行失败 (代码 {result}): {errorMsg}";
                }
            }
            else
            {
                // 参数卡片模式：逐卡片处理（与UI一致）
                _context?.Log(LogLevel.Info, $"[ABot] Detected {parameterCards.Count} parameter card(s), processing each card...");
                
                int cardIndex = 0;
                StringBuilder executeResult = new StringBuilder();
                
                foreach (var card in parameterCards)
                {
                    cardIndex++;
                    string cardType = DetectCardType(card);
                    _context?.Log(LogLevel.Info, $"[ABot] Card {cardIndex}: Type={cardType}");
                    
                    if (cardType.Equals("skillset", StringComparison.OrdinalIgnoreCase))
                    {
                        // 清理skillset卡：移除expr(...)后、>之前的多余字符
                        string cleanedCard = CleanSkillsetCard(card);
                        _context?.Log(LogLevel.Info, $"[ABot] Registering skillset card {cardIndex}...");
                        
                        int skillsetResult = interpreter.RegisterSkillset(cleanedCard);
                        if (skillsetResult == 0)
                        {
                            executeResult.AppendLine($"[Card {cardIndex}] ✓ Skillset registered successfully");
                        }
                        else
                        {
                            string errorMsg = interpreter.GetLastError() ?? "Unknown error";
                            executeResult.AppendLine($"[Card {cardIndex}] ✗ Skillset registration failed: {errorMsg}");
                        }
                    }
                    else if (cardType.Equals("character", StringComparison.OrdinalIgnoreCase))
                    {
                        _context?.Log(LogLevel.Info, $"[ABot] Parsing character card {cardIndex}...");
                        
                        int charResult = interpreter.ParseCharacter(card);
                        if (charResult == 0)
                        {
                            executeResult.AppendLine($"[Card {cardIndex}] ✓ Character parsed successfully");
                            
                            // 关键修复：将解析的角色添加到回合管理器
                            _context?.Log(LogLevel.Info, $"[ABot] Adding character {cardIndex} to round manager...");
                            int addResult = interpreter.AddCharacterToRoundManager();
                            if (addResult == 0)
                            {
                                executeResult.AppendLine($"[Card {cardIndex}] ✓ Character added to battle");
                            }
                            else
                            {
                                string addErrorMsg = interpreter.GetLastError() ?? "Unknown error";
                                executeResult.AppendLine($"[Card {cardIndex}] ✗ Failed to add character to battle: {addErrorMsg}");
                            }
                        }
                        else
                        {
                            string errorMsg = interpreter.GetLastError() ?? "Unknown error";
                            executeResult.AppendLine($"[Card {cardIndex}] ✗ Character parsing failed: {errorMsg}");
                        }
                    }
                    else
                    {
                        _context?.Log(LogLevel.Warn, $"[ABot] Card {cardIndex}: Unknown card type '{cardType}'");
                        executeResult.AppendLine($"[Card {cardIndex}] ⚠ Unknown card type: {cardType}");
                    }
                }
                
                // 关键修复：如果有角色卡被添加，需要初始化回合管理器
                int characterCardCount = parameterCards.Count(c => DetectCardType(c).Equals("character", StringComparison.OrdinalIgnoreCase));
                
                if (characterCardCount >= 2)
                {
                    _context?.Log(LogLevel.Info, $"[ABot] Initializing round manager with {characterCardCount} characters...");
                    int initResult = interpreter.InitializeRoundManager();
                    if (initResult == 0)
                    {
                        executeResult.AppendLine($"");
                        executeResult.AppendLine($"[Battle] ✓ Battle initialized successfully - ready for combat");
                    }
                    else
                    {
                        string initErrorMsg = interpreter.GetLastError() ?? "Unknown error";
                        executeResult.AppendLine($"");
                        executeResult.AppendLine($"[Battle] ✗ Failed to initialize round manager: {initErrorMsg}");
                    }
                }
                else if (characterCardCount > 0)
                {
                    executeResult.AppendLine($"");
                    executeResult.AppendLine($"[Battle] ⚠ Need at least 2 characters to start battle (current: {characterCardCount})");
                }
                
                string result = executeResult.ToString();
                return string.IsNullOrEmpty(result) ? "[ABot] 脚本执行完成" : result;
            }
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[ABot] Script execution error: {ex.Message}");
            return $"[ABot] ✗ 脚本错误: {ex.Message}";
        }
    }

    /// <summary>
    /// 预处理脚本：对所有 expr(...) 内部内容进行 Base64 编码
    /// 保持格式：def = expr(BASE64_CONTENT)
    /// </summary>
    private string PreprocessExpressionAttributes(string input)
    {
        var result = new StringBuilder();
        int pos = 0;
        
        while (pos < input.Length)
        {
            // 查找 "expr(" 的位置
            int exprPos = input.IndexOf("expr(", pos);
            if (exprPos < 0)
            {
                result.Append(input.Substring(pos));
                break;
            }
            
            // 添加 "expr(" 之前的内容
            result.Append(input.Substring(pos, exprPos - pos + 5));  // 包含 "expr("
            
            // 找到匹配的结束括号
            int bracketCount = 1;
            int i = exprPos + 5;  // 跳过 "expr("
            
            while (i < input.Length && bracketCount > 0)
            {
                if (input[i] == '(')
                    bracketCount++;
                else if (input[i] == ')')
                    bracketCount--;
                i++;
            }
            
            if (bracketCount == 0)
            {
                // 找到了匹配的右括号
                // 提取表达式内容（不含括号）
                string scriptContent = input.Substring(exprPos + 5, i - exprPos - 6);
                
                // 对表达式内容进行 Base64 编码
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(scriptContent);
                string encoded = System.Convert.ToBase64String(bytes);
                
                // 添加编码后的内容
                result.Append(encoded);
                result.Append(")");  // 添加右括号
                
                pos = i;
            }
            else
            {
                // 括号不匹配，直接跳过
                pos = exprPos + 5;
            }
        }
        
        return result.ToString();
    }

    /// <summary>
    /// 从输入中提取所有参数卡（以 [ ] 括起的内容）
    /// </summary>
    private List<string> ExtractParameterCards(string input)
    {
        var cards = new List<string>();
        
        int openBracket = -1;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '[')
            {
                openBracket = i;
            }
            else if (input[i] == ']' && openBracket >= 0)
            {
                // 提取括号内的内容
                string card = input.Substring(openBracket + 1, i - openBracket - 1).Trim();
                if (!string.IsNullOrWhiteSpace(card))
                {
                    cards.Add(card);
                }
                openBracket = -1;
            }
        }
        
        return cards;
    }

    /// <summary>
    /// 清理 skillset 卡：移除 expr(...) 后面但在 > 之前的多余字符
    /// </summary>
    private string CleanSkillsetCard(string card)
    {
        int exprStart = card.IndexOf("expr(");
        if (exprStart < 0)
            return card;
        
        int exprParenEnd = card.LastIndexOf(")");
        if (exprParenEnd <= exprStart)
            return card;
        
        int lastBracket = card.LastIndexOf(">");
        if (lastBracket <= exprParenEnd)
            return card;
        
        string between = card.Substring(exprParenEnd + 1, lastBracket - exprParenEnd - 1);
        if (string.IsNullOrEmpty(between))
            return card;
        
        _context?.Log(LogLevel.Info, $"[ABot] Cleaning skillset card: removing '{between}' between expr(...) and >");
        return card.Substring(0, exprParenEnd + 1) + ">" + card.Substring(lastBracket + 1);
    }

    /// <summary>
    /// 检测参数卡的类型（skillset 或 character）
    /// </summary>
    private string DetectCardType(string card)
    {
        if (card.Contains("<type value=skillset", StringComparison.OrdinalIgnoreCase))
            return "skillset";
        
        if (card.Contains("<type value=character", StringComparison.OrdinalIgnoreCase))
            return "character";
        
        return "unknown";
    }

    /// <summary>
    /// 统一的回合输出格式化函数
    /// UI面板和聊天窗口都使用此函数生成相同的日志格式
    /// </summary>
    private string FormatRoundOutput(ABotInterpreter interpreter)
    {
        string battleStatus = interpreter.GetRoundStatus();
        string battleLog = interpreter.GetRoundLog();
        
        string output = "⚔ [ABOT 战斗回合]\n";
        output += battleStatus;  // 包含 === Battle Status === 和所有信息
        
        if (!string.IsNullOrEmpty(battleLog))
        {
            output += "\n📋 事件:\n" + battleLog;  // 包含 === Battle Log === 和所有事件
        }
        
        return output;
    }

    /// <summary>
    /// 执行下一回合
    /// 返回回合执行结果的字符串
    /// 
    /// 多用户隔离：使用 GetOrCreateInterpreter(_currentUserId) 获取该用户的独立解释器
    /// </summary>
    private string? ExecuteNextRound()
    {
        try
        {
            ABotLogger.Info($"[NEXT ROUND] Starting next round execution for user {_currentUserId}");
            _context?.Log(LogLevel.Info, "[ABot] Executing next round from chat command");
            
            // ⭐ 阶段1修改：获取当前用户的解释器（多用户隔离）
            ABotLogger.Info($"[NEXT ROUND] About to call GetOrCreateInterpreter({_currentUserId})");
            var interpreter = GetOrCreateInterpreter(_currentUserId);
            if (interpreter == null)
            {
                ABotLogger.Error($"[NEXT ROUND FAIL] GetOrCreateInterpreter returned null for user {_currentUserId}");
                return "[ABot] 错误：解释器未初始化";
            }
            
            ABotLogger.Info($"[NEXT ROUND] Got interpreter. Active pool size: {_interpreterPool.Count}/{MAX_POOL_SIZE}");
            
            // 执行下一回合
            ABotLogger.Info($"[NEXT ROUND] Calling AdvanceRound()");
            int result = interpreter.AdvanceRound();
            
            ABotLogger.Info($"[NEXT ROUND] AdvanceRound() returned: {result}");
            
            if (result == 0)
            {
                ABotLogger.Info($"[NEXT ROUND OK] Round executed successfully");
                
                // ✅ 使用统一的格式化函数
                string output = FormatRoundOutput(interpreter);
                
                // 将输出分割成转发消息格式
                _context?.Log(LogLevel.Info, $"[ABot] Processing round output for forward message format (length: {output.Length})");
                string forwardJson = ConvertToForwardMessageFormat(output);
                return forwardJson;
            }
            else
            {
                string errorMsg = interpreter.GetLastError() ?? "未知错误";
                ABotLogger.Error($"[NEXT ROUND FAIL] AdvanceRound failed with code {result}: {errorMsg}");
                ABotLogger.Error($"[NEXT ROUND DEBUG] Active users: {_interpreterPool.Count}, Offline users: {_offlineStateStore.OfflineStateCount}");
                
                // 诊断信息
                if (result == 5)
                {
                    ABotLogger.Error($"[DIAGNOSIS] Code 5 = Round manager not initialized");
                    ABotLogger.Error($"[DIAGNOSIS] This means:");
                    ABotLogger.Error($"[DIAGNOSIS] 1. User {_currentUserId} never executed .abot script, OR");
                    ABotLogger.Error($"[DIAGNOSIS] 2. User was evicted from cache, state restored failed, OR");
                    ABotLogger.Error($"[DIAGNOSIS] 3. C++ side of LoadState() did not properly initialize RoundManager");
                    ABotLogger.Error($"[DIAGNOSIS] Current state: Offline snapshot exists? {(_offlineStateStore.GetOfflineState(_currentUserId) != null ? "YES" : "NO")}");
                }
                
                return $"[ABot] ✗ 回合执行失败 (代码 {result}): {errorMsg}";
            }
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"[NEXT ROUND EXCEPTION] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            _context?.Log(LogLevel.Error, $"[ABot] Next round error: {ex.Message}");
            return $"[ABot] ✗ 回合错误: {ex.Message}";
        }
    }

    /// <summary>
    /// 将字符串输出转换为转发消息格式 (JSON)
    /// 分割规则：
    /// 1. 按换行符分割
    /// 2. 如果单行超过400字符，也进行分割
    /// 返回格式：{"__forward_message": true, "contents": ["...", "...", ...]}
    /// </summary>
    private string ConvertToForwardMessageFormat(string content)
    {
        var contentList = new List<string>();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        StringBuilder currentBlock = new StringBuilder();
        int currentBlockLength = 0;
        
        foreach (var line in lines)
        {
            // 如果单行长度超过400字符，先将当前块添加，再处理这一行
            if (line.Length > 400)
            {
                // 先添加当前块
                if (currentBlock.Length > 0)
                {
                    contentList.Add(currentBlock.ToString().TrimEnd());
                    currentBlock.Clear();
                    currentBlockLength = 0;
                }
                
                // 长行分割：按400字符为单位
                for (int i = 0; i < line.Length; i += 400)
                {
                    int length = Math.Min(400, line.Length - i);
                    contentList.Add(line.Substring(i, length));
                }
            }
            else
            {
                // 检查添加这一行是否会超过400字符
                int newLength = currentBlockLength + line.Length + 1; // +1 用于换行符
                
                if (currentBlockLength > 0 && newLength > 400)
                {
                    // 超过了，先添加当前块
                    contentList.Add(currentBlock.ToString().TrimEnd());
                    currentBlock.Clear();
                    currentBlock.AppendLine(line);
                    currentBlockLength = line.Length;
                }
                else
                {
                    // 还没超过，继续添加
                    if (currentBlock.Length > 0)
                    {
                        currentBlock.AppendLine();
                    }
                    currentBlock.Append(line);
                    currentBlockLength = newLength - 1; // 不计最后一个\n
                }
            }
        }
        
        // 添加最后一个块
        if (currentBlock.Length > 0)
        {
            contentList.Add(currentBlock.ToString().TrimEnd());
        }
        
        // 如果只有一条内容，直接返回（不使用转发格式）
        if (contentList.Count <= 1)
        {
            return contentList.Count > 0 ? contentList[0] : "";
        }
        
        // 构建 JSON 格式的转发消息
        _context?.Log(LogLevel.Info, $"[ABot] Converting to forward message: {contentList.Count} segments");
        
        var jsonObj = new Dictionary<string, object>
        {
            { "__forward_message", true },
            { "contents", contentList }
        };
        
        // 使用简单的 JSON 序列化（避免依赖）
        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"__forward_message\":true,\"contents\":[");
        for (int i = 0; i < contentList.Count; i++)
        {
            if (i > 0) jsonBuilder.Append(",");
            // 简单转义引号
            string escaped = contentList[i].Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
            jsonBuilder.Append($"\"{escaped}\"");
        }
        jsonBuilder.Append("]}");
        
        return jsonBuilder.ToString();
    }

    // ============ INavigationPanelProvider 实现 ============

    /// <summary>
    /// 创建导航面板UI控件
    /// </summary>
    public Control CreatePanel()
    {
        try
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> CreatePanel() START");
            _context?.Log(LogLevel.Info, "[ABot] CreatePanel() called");
            
            // 检查解释器是否初始化成功
            if (_interpreter == null)
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] CreatePanel(): Interpreter is null!");
                _context?.Log(LogLevel.Error, "[ABot] CreatePanel() failed: Interpreter is null");
                return CreateErrorPanel("Interpreter instance not created");
            }
            
            string? loadError = _interpreter.GetLoadError();
            if (loadError != null)
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] CreatePanel(): C++/CLI interop not available: {loadError}");
                _context?.Log(LogLevel.Warn, $"[ABot] CreatePanel() showing error panel: {loadError}");
                return CreateErrorPanel(loadError);
            }
            
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Creating ABotPanel instance with functional interpreter...");
            var panel = new ABotPanel(this, _interpreter);
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] ABotPanel created successfully");
            _context?.Log(LogLevel.Info, "[ABot] Navigation panel created successfully");
            return panel;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] CreatePanel() EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _context?.Log(LogLevel.Error, $"[ABot] CreatePanel() failed: {ex.Message}");
            return CreateErrorPanel($"Panel creation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 创建错误面板，用于显示诊断信息
    /// </summary>
    private Control CreateErrorPanel(string errorMessage)
    {
        var errorStackPanel = new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(15)
        };
        
        var titleText = new TextBlock
        {
            Text = "❌ ABOT Interpreter Unavailable",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Colors.Red),
            Margin = new Thickness(0, 0, 0, 10)
        };
        errorStackPanel.Children.Add(titleText);
        
        var errorText = new TextBlock
        {
            Text = errorMessage,
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.DarkRed),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 500
        };
        errorStackPanel.Children.Add(errorText);
        
        // 检查是否是框架不兼容的错误
        if (errorMessage.IndexOf("BadImageFormatException", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var divider = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Colors.LightGray),
                Margin = new Thickness(0, 10, 0, 10)
            };
            errorStackPanel.Children.Add(divider);
            
            var diagTitle = new TextBlock
            {
                Text = "DIAGNOSTIC INFO:",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Colors.DarkRed),
                Margin = new Thickness(0, 0, 0, 5)
            };
            errorStackPanel.Children.Add(diagTitle);
            
            var diagText = new TextBlock
            {
                Text = "• ABot.CLI.dll found but is incompatible with this process\n" +
                       "• ABot.CLI targets .NET Framework 4.7.2\n" +
                       "• This application uses .NET 10\n" +
                       "• C++/CLI bridge cannot work between these frameworks\n\n" +
                       "SOLUTIONS:\n" +
                       "1. Rebuild ABot.CLI to target .NET 6+ (recommended)\n" +
                       "2. Use P/Invoke wrapper instead of C++/CLI\n" +
                       "3. Run ABot in separate process via IPC",
                FontSize = 10,
                Foreground = new SolidColorBrush(Colors.DarkGray),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500,
                LineHeight = 1.3
            };
            errorStackPanel.Children.Add(diagText);
        }
        else
        {
            var fixText = new TextBlock
            {
                Text = "Required: Ensure ABot.CLI.dll and ABot.Core.dll are compiled and available in the application runtime directory.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Colors.Gray),
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500,
                Margin = new Thickness(0, 10, 0, 0)
            };
            errorStackPanel.Children.Add(fixText);
        }
        
        var scrollViewer = new ScrollViewer
        {
            Content = errorStackPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        
        var border = new Border
        {
            Child = scrollViewer,
            Background = new SolidColorBrush(Color.Parse("#FFF0F0")),
            BorderBrush = new SolidColorBrush(Colors.Red),
            BorderThickness = new Thickness(2)
        };
        
        return border;
    }

    /// <summary>
    /// 注册导航面板
    /// </summary>
    private void RegisterNavigationPanel()
    {
        try
        {
            Console.WriteLine("[ABot] >>> RegisterNavigationPanel START");
            _context?.Log(LogLevel.Info, "[ABot] RegisterNavigationPanel START");
            
            Console.WriteLine("[ABot] >>> Checking implementation status - implements INavigationPanelProvider: true");
            _context?.Log(LogLevel.Debug, "[ABot] ABotMod implements INavigationPanelProvider");
            
            Console.WriteLine($"[ABot] >>> Panel info - Id: {PanelId}, Name: {PanelName}, Priority: {Priority}, IsModPanel: {IsModPanel}");
            _context?.Log(LogLevel.Info, $"[ABot] Panel info - Id: {PanelId}, Name: {PanelName}, Priority: {Priority}, IsModPanel: {IsModPanel}");
            
            // 通过 Context 获取导航面板注册表服务
            Console.WriteLine("[ABot] >>> Calling _context.GetNavigationPanelRegistry()...");
            var registry = _context?.GetNavigationPanelRegistry();
            Console.WriteLine($"[ABot] >>> Registry result: {(registry != null ? "SUCCESS (not null)" : "NULL")}");
            _context?.Log(LogLevel.Info, $"[ABot] GetNavigationPanelRegistry returned: {(registry != null ? "INavigationPanelRegistry instance" : "NULL")}");
            
            if (registry == null)
            {
                Console.WriteLine("[ABot] >>> CRITICAL ERROR: Navigation panel registry is NULL!");
                Console.WriteLine("[ABot] >>> This means NavigationPanelRegistry.Instance returned null");
                _context?.Log(LogLevel.Error, "[ABot] CRITICAL ERROR: Navigation panel registry is NULL - panel registration failed");
                _context?.Log(LogLevel.Warn, "[ABot] Possible cause: NavigationPanelRegistry not initialized yet, or exception occurred");
                return;
            }

            Console.WriteLine("[ABot] >>> About to call registry.Register(this)...");
            _context?.Log(LogLevel.Info, "[ABot] Calling registry.Register() with ABotMod as INavigationPanelProvider");
            
            registry.Register(this);
            
            Console.WriteLine("[ABot] >>> registry.Register() completed without exception");
            _context?.Log(LogLevel.Info, "[ABot] ✓ Navigation panel registered successfully");
            Console.WriteLine("[ABot] >>> Panel should now appear in main window navigation bar");
            Console.WriteLine("[ABot] >>> RegisterNavigationPanel END - SUCCESS");
        }
        catch (InvalidOperationException ioEx)
        {
            Console.WriteLine($"[ABot] >>> INVALID_OPERATION EXCEPTION (panel ID already registered?): {ioEx.Message}");
            _context?.Log(LogLevel.Error, $"[ABot] InvalidOperationException during panel registration: {ioEx.Message}");
            _context?.Log(LogLevel.Error, $"[ABot] Possible cause: PanelId '{PanelId}' already registered by another provider");
        }
        catch (ArgumentException argEx)
        {
            Console.WriteLine($"[ABot] >>> ARGUMENT EXCEPTION: {argEx.Message}");
            _context?.Log(LogLevel.Error, $"[ABot] ArgumentException during panel registration: {argEx.Message}");
            _context?.Log(LogLevel.Error, $"[ABot] Possible causes: Missing PanelId, Empty PanelName, null provider, etc.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot] >>> UNEXPECTED EXCEPTION in RegisterNavigationPanel: {ex.GetType().Name}");
            Console.WriteLine($"[ABot] >>> Message: {ex.Message}");
            Console.WriteLine($"[ABot] >>> StackTrace: {ex.StackTrace}");
            _context?.Log(LogLevel.Error, $"[ABot] UNEXPECTED Exception in RegisterNavigationPanel: {ex.GetType().Name}: {ex.Message}");
            _context?.Log(LogLevel.Error, $"[ABot] StackTrace: {ex.StackTrace}");
        }
    }
}

/// <summary>
/// ABOT解释器导航面板
/// 提供战斗脚本的执行和调试界面
/// </summary>
public class ABotPanel : ContentControl
{
    private TextBox? _scriptInputBox;
    private TextBox? _logOutput;
    private ScrollViewer? _logScrollViewer;
    private TextBox? _battleInfoOutput;
    private ScrollViewer? _battleInfoScrollViewer;
    private Button? _nextRoundButton;
    private ABotInterpreter? _interpreter;
    private ABotMod? _mod;

    /// <summary>
    /// 统一的回合输出格式化函数
    /// UI面板和聊天窗口都使用此函数生成相同的日志格式
    /// </summary>
    private string FormatRoundOutput(ABotInterpreter interpreter)
    {
        string battleStatus = interpreter.GetRoundStatus();
        string battleLog = interpreter.GetRoundLog();
        
        string output = "⚔ [ABOT 战斗回合]\n";
        output += battleStatus;  // 包含 === Battle Status === 和所有信息
        
        if (!string.IsNullOrEmpty(battleLog))
        {
            output += "\n📋 事件:\n" + battleLog;  // 包含 === Battle Log === 和所有事件
        }
        
        return output;
    }

    /// <summary>
    /// 创建 ABOT 面板
    /// </summary>
    public ABotPanel(ABotMod mod, ABotInterpreter? interpreter = null)
    {
        _mod = mod;
        _interpreter = interpreter;
        
        FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> ABotPanel constructor START");
        FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] _interpreter is null: {_interpreter == null}");
        FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] _mod is null: {_mod == null}");
        
        InitializeUI();
        
        FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> ABotPanel constructor END");
    }
    
    /// <summary>
    /// 向后兼容的构造函数
    /// </summary>
    [Obsolete("Use ABotPanel(ABotMod, ABotInterpreter) instead")]
    public ABotPanel(ABotInterpreter? interpreter = null)
    {
        _interpreter = interpreter;
        FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] ABotPanel (legacy) constructor - interpreter is null: {_interpreter == null}");
        InitializeUI();
    }
    
    /// <summary>
    /// 初始化UI组件 - 带战斗信息面板和回合执行按钮
    /// </summary>
    private void InitializeUI()
    {
        // 主容器
        var mainPanel = new StackPanel
        {
            Spacing = 10,
            Orientation = Orientation.Vertical
        };

        // 标题
        var titleTextBlock = new TextBlock
        {
            Text = "ABOT Battle Simulator",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Colors.DarkBlue),
            Margin = new Thickness(0, 0, 0, 5)
        };
        mainPanel.Children.Add(titleTextBlock);

        // 说明文本
        var descriptionText = new TextBlock
        {
            Text = "Enter ABOT script with custom tag format. Script is sent to C++ core for parsing and execution.",
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            Foreground = new SolidColorBrush(Colors.DarkGray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        mainPanel.Children.Add(descriptionText);

        // 输入区域标签
        var inputLabel = new TextBlock
        {
            Text = "[Input] ABOT Script",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Colors.DarkGray)
        };
        mainPanel.Children.Add(inputLabel);

        // 输入TextBox
        _scriptInputBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 140,
            Foreground = new SolidColorBrush(Colors.Black),
            Background = new SolidColorBrush(Color.Parse("#F5F5F5")),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 5),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 10
        };
        _scriptInputBox.Classes.Add("WhiteBackgroundTextBox");
        // 设置默认示例文本（使用真实的标签格式）
        _scriptInputBox.Text = @"[
<type value=skillset>
<skilldef id=WillbeUsefulNextTime,para={},def = expr(set self.atk.value += 10; set self.dmg.d1 += 1; set self.dmg.d2 += 1; set self.dmg.d3 += 1; set self.dmg.d4 += 1; return;)>
]
[
<type value=character> //用来定义这是个角色卡，无需更改。
<name value=烈海王> //你角色的名称。 
<camp value=1> //这是阵营。
<atk value=100> //这是攻击值。
<hp value=50>//这是生命值。
<dmg d1=1,d2=3,d3=5,d4=7> //这是普通攻击的基本伤害。
<skill name=必可活用于下一次,type=ondamagetaken,id=WillbeUsefulNextTime, cd=0, rate=100>
]
[
<type value=character>
<name value=范马勇次郎>
<camp value=2>
<atk value=120>
<hp value=60, max=60>
<dmg d1=2, d2=4, d3=6, d4=8>
]";
        mainPanel.Children.Add(_scriptInputBox);

        // 按钮区域
        var buttonPanel = new StackPanel
        {
            Spacing = 8,
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 5)
        };

        var executeButton = new Button
        {
            Content = "▶ Execute Script",
            MinWidth = 130,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.Parse("#4CAF50")),
            Foreground = new SolidColorBrush(Colors.White),
            FontWeight = FontWeight.Bold
        };
        executeButton.Click += OnParseButtonClicked;
        buttonPanel.Children.Add(executeButton);

        // NEW: Next Round Button
        _nextRoundButton = new Button
        {
            Content = "⚔ Next Round",
            MinWidth = 120,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.Parse("#FF9800")),
            Foreground = new SolidColorBrush(Colors.White),
            FontWeight = FontWeight.Bold,
            IsEnabled = false
        };
        _nextRoundButton.Click += OnNextRoundClicked;
        buttonPanel.Children.Add(_nextRoundButton);

        var clearInputButton = new Button
        {
            Content = "Clear Input",
            MinWidth = 110,
            Padding = new Thickness(10, 8, 10, 8)
        };
        clearInputButton.Click += (_, _) => {
            if (_scriptInputBox != null)
                _scriptInputBox.Text = "";
        };
        buttonPanel.Children.Add(clearInputButton);

        var clearLogsButton = new Button
        {
            Content = "Clear Logs",
            MinWidth = 100,
            Padding = new Thickness(10, 8, 10, 8)
        };
        clearLogsButton.Click += (_, _) => {
            if (_logOutput != null)
                _logOutput.Text = "";
            if (_battleInfoOutput != null)
                _battleInfoOutput.Text = "";
        };
        buttonPanel.Children.Add(clearLogsButton);

        var loadExampleButton = new Button
        {
            Content = "📋 Load Example",
            MinWidth = 110,
            Padding = new Thickness(10, 8, 10, 8)
        };
        loadExampleButton.Click += (_, _) => {
            if (_scriptInputBox != null)
                _scriptInputBox.Text = @"[
<type value=character>
<name value=烈海王>
<camp value=1>
<atk value=100>
<hp value=50, max=50>
<dmg d1=1, d2=3, d3=5, d4=7>
]

[
<type value=character>
<name value=范马勇次郎>
<camp value=2>
<atk value=120>
<hp value=60, max=60>
<dmg d1=2, d2=4, d3=6, d4=8>
]";
        };
        buttonPanel.Children.Add(loadExampleButton);

        var resetButton = new Button
        {
            Content = "🔄 Reset All",
            MinWidth = 100,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.Parse("#FF6B6B")),
            Foreground = new SolidColorBrush(Colors.White),
            FontWeight = FontWeight.Bold
        };
        resetButton.Click += OnResetButtonClicked;
        buttonPanel.Children.Add(resetButton);

        mainPanel.Children.Add(buttonPanel);

        // 双面板容器 - 水平布局
        var dualPanelContainer = new Grid();
        dualPanelContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dualPanelContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ========== 左面板: 战斗信息 ==========
        var battleInfoPanel = new StackPanel
        {
            Spacing = 5,
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 5, 0)
        };

        var battleInfoLabel = new TextBlock
        {
            Text = "[Battle Info] Combat State",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Colors.DarkGreen)
        };
        battleInfoPanel.Children.Add(battleInfoLabel);

        _battleInfoOutput = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Foreground = new SolidColorBrush(Color.Parse("#C8E6C9")),
            Background = new SolidColorBrush(Color.Parse("#1B5E20")),
            Padding = new Thickness(8),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 10,
            IsHitTestVisible = true,
            ContextMenu = CreateCopyContextMenu()
        };
        _battleInfoOutput.Text = "[Idle] Battle simulator ready. Awaiting character setup.\n";

        _battleInfoScrollViewer = new ScrollViewer
        {
            Content = _battleInfoOutput,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 250,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.DarkGreen)
        };
        battleInfoPanel.Children.Add(_battleInfoScrollViewer);

        Grid.SetColumn(battleInfoPanel, 0);
        dualPanelContainer.Children.Add(battleInfoPanel);

        // ========== 右面板: 执行日志 ==========
        var logPanel = new StackPanel
        {
            Spacing = 5,
            Orientation = Orientation.Vertical,
            Margin = new Thickness(5, 0, 0, 0)
        };

        var logLabel = new TextBlock
        {
            Text = "[Logs] Execution Results",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Colors.DarkGray)
        };
        logPanel.Children.Add(logLabel);

        // 日志输出区域
        _logOutput = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Foreground = new SolidColorBrush(Color.Parse("#E0E0E0")),
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Padding = new Thickness(8),
            Text = "[Ready] Battle simulator initialized. Load example or enter battle scenario.\n",
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 10,
            IsHitTestVisible = true,
            ContextMenu = CreateCopyContextMenu()
        };

        _logScrollViewer = new ScrollViewer
        {
            Content = _logOutput,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 250,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.DarkGray)
        };
        logPanel.Children.Add(_logScrollViewer);

        Grid.SetColumn(logPanel, 1);
        dualPanelContainer.Children.Add(logPanel);

        mainPanel.Children.Add(dualPanelContainer);

        // 创建Border作为根容器
        var border = new Border
        {
            Padding = new Thickness(12),
            Child = mainPanel,
            Background = new SolidColorBrush(Color.Parse("#FFFFFF"))
        };

        Content = border;
    }

    /// <summary>
    /// 执行脚本按钮点击事件
    /// 将脚本文本交给 C++ 核心处理
    /// 支持 Phase 3+ 参数卡片模式 | 向后兼容脚本模式
    /// </summary>
    private void OnParseButtonClicked(object? sender, RoutedEventArgs? e)
    {
        if (_scriptInputBox == null || _logOutput == null)
            return;

        var scriptText = _scriptInputBox.Text?.Trim();
        if (string.IsNullOrEmpty(scriptText))
        {
            AppendLog("[WARN] Script input is empty");
            return;
        }

        try
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] >>> Script execution button clicked");
            
            AppendLog("");
            AppendLog("[INFO] ========== SCRIPT EXECUTION ==========");
            AppendLog($"[INFO] Timestamp: {DateTime.Now:HH:mm:ss.fff}");
            AppendLog($"[INFO] Script length: {scriptText.Length} characters");

            // 预处理：为 expr(...) 属性值加上引号
            AppendLog("[INFO] Preprocessing script to quote expression attributes...");
            scriptText = PreprocessExpressionAttributes(scriptText);
            AppendLog($"[INFO] After preprocessing: {scriptText.Length} characters");

            if (_interpreter == null)
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] ERROR: Interpreter is null!");
                AppendLog("[ERROR] Interpreter not initialized");
                return;
            }
            
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Interpreter exists. Checking for parameter cards...");
            
            // ============ Phase 3+: Parameter Card Detection ============
            var parameterCards = ExtractParameterCards(scriptText);
            
            if (parameterCards.Count > 0)
            {
                AppendLog($"[INFO] Detected: {parameterCards.Count} parameter card(s)");
                AppendLog($"[INFO] Entering parameter card processing mode");
                
                // 生命周期诊断：打印所有卡片类型
                AppendLog($"[LIFECYCLE] Card type summary:");
                int skillsetCount = 0;
                int characterCount_init = 0;
                foreach (var card in parameterCards)
                {
                    string detectedType = DetectCardType(card);
                    if (detectedType.Equals("skillset", StringComparison.OrdinalIgnoreCase))
                    {
                        skillsetCount++;
                        AppendLog($"  [{skillsetCount}] skillset type={detectedType}");
                    }
                    else if (detectedType.Equals("character", StringComparison.OrdinalIgnoreCase))
                    {
                        characterCount_init++;
                        AppendLog($"  [Char {characterCount_init}] character type={detectedType}");
                    }
                    else
                    {
                        AppendLog($"  [?] unknown type={detectedType}");
                    }
                }
                AppendLog($"[LIFECYCLE] Summary: {skillsetCount} skillset(s), {characterCount_init} character(s)");
                
                int cardIndex = 0;
                int characterCount = 0;
                foreach (var card in parameterCards)
                {
                    cardIndex++;
                    string cardType = DetectCardType(card);
                    AppendLog($"");
                    AppendLog($"[CARD {cardIndex}] Type: {cardType}");
                    
                    // Parse and display card content
                    ParseParameterCardForDebug(card, cardType);
                    
                    // 处理 skillset 卡
                    if (cardType.Equals("skillset", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLog($"[SKILLSET {cardIndex}] CardType confirmed as 'skillset', passing to RegisterSkillset()");
                        AppendLog($"[SKILLSET {cardIndex}] Card content length: {card.Length} characters");
                        AppendLog($"[SKILLSET {cardIndex}] First 200 chars: '{card.Substring(0, Math.Min(200, card.Length))}'");
                        
                        // === 修复：在调用C++前清洁卡片 ===
                        string cleanedCard = card;
                        
                        // 关键修复：只移除 expr(...) 后、但在末尾 '>' 之前的多余字符
                        // 正确格式：<skilldef ...def = expr(BASE64)>
                        int exprStart = cleanedCard.IndexOf("expr(");
                        if (exprStart >= 0)
                        {
                            // 找最后一个 ) 作为 expr(...) 的结尾
                            int exprParenEnd = cleanedCard.LastIndexOf(")");
                            if (exprParenEnd > exprStart)
                            {
                                // 找末尾的 '>'
                                int lastBracket = cleanedCard.LastIndexOf(">");
                                
                                if (lastBracket > exprParenEnd)
                                {
                                    // 检查 ) 和 > 之间是否有多余字符
                                    string between = cleanedCard.Substring(exprParenEnd + 1, lastBracket - exprParenEnd - 1);
                                    if (!string.IsNullOrEmpty(between))
                                    {
                                        AppendLog($"[SKILLSET {cardIndex}] ⚠️  Found characters between expr(...) and '>': '{between}'");
                                        // 移除中间的多余字符，保留 )>
                                        cleanedCard = cleanedCard.Substring(0, exprParenEnd + 1) + ">" + 
                                                     cleanedCard.Substring(lastBracket + 1);
                                        AppendLog($"[SKILLSET {cardIndex}]    Cleaned from length {card.Length} to {cleanedCard.Length}");
                                    }
                                }
                            }
                        }
                        
                        if (cleanedCard != card)
                        {
                            AppendLog($"[SKILLSET {cardIndex}] ℹ️  Card was cleaned before passing to C++");
                        }
                        
                        try
                        {
                            AppendLog($"[SKILLSET {cardIndex}] ├─ About to call RegisterSkillset()...");
                            
                            // === 在发送到C++之前添加十六进制调试 ===
                            AppendLog($"[SKILLSET {cardIndex}] ═══ HEX DEBUG: cleanedCard content ═══");
                            AppendLog($"[SKILLSET {cardIndex}] Length: {cleanedCard.Length} bytes");
                            
                            // 输出前200个字符的十六进制转储
                            int dumpLength = Math.Min(200, cleanedCard.Length);
                            for (int startLine = 0; startLine < dumpLength; startLine += 16)
                            {
                                string hexLine = "";
                                int lineEnd = Math.Min(startLine + 16, dumpLength);
                                for (int i = startLine; i < lineEnd; i++)
                                {
                                    hexLine += $"{(byte)cleanedCard[i]:X2} ";
                                }
                                AppendLog($"[SKILLSET {cardIndex}] {startLine:D4}: {hexLine}");
                            }
                            
                            // 输出 ASCII 表示
                            AppendLog($"[SKILLSET {cardIndex}] ASCII: {cleanedCard.Substring(0, Math.Min(100, cleanedCard.Length))}");
                            
                            // 特别找到 def 属性
                            int defIndex = cleanedCard.IndexOf("def");
                            if (defIndex >= 0)
                            {
                                int endIndex = Math.Min(defIndex + 150, cleanedCard.Length);
                                string defSection = cleanedCard.Substring(defIndex, endIndex - defIndex);
                                AppendLog($"[SKILLSET {cardIndex}] DEF section (ASCII): {defSection}");
                                
                                // 十六进制转储 def 部分
                                AppendLog($"[SKILLSET {cardIndex}] DEF hex dump:");
                                for (int startLine = 0; startLine < defSection.Length; startLine += 16)
                                {
                                    string hexLine = "";
                                    int lineEnd = Math.Min(startLine + 16, defSection.Length);
                                    for (int i = startLine; i < lineEnd; i++)
                                    {
                                        hexLine += $"{(byte)defSection[i]:X2} ";
                                    }
                                    AppendLog($"[SKILLSET {cardIndex}] DEF-{startLine:D4}: {hexLine}");
                                }
                            }
                            
                            AppendLog($"[SKILLSET {cardIndex}] ═══ End HEX DEBUG ═══");
                            
                            int skillsetResult = _interpreter!.RegisterSkillset(cleanedCard);
                            AppendLog($"[SKILLSET {cardIndex}] └─ RegisterSkillset() returned: {skillsetResult}");
                            
                            if (skillsetResult == 0)
                            {
                                AppendLog($"[SKILLSET {cardIndex}] ✅ SUCCESS: Skill registered successfully");
                            }
                            else
                            {
                                string lastError = _interpreter.GetLastError();
                                string errorCodeName = GetErrorCodeName(skillsetResult);
                                
                                AppendLog($"");
                                AppendLog($"[SKILLSET {cardIndex}] ❌ REGISTRATION FAILED");
                                AppendLog($"[SKILLSET {cardIndex}] ├─ Error Code: {skillsetResult} ({errorCodeName})");
                                
                                // 美化错误消息：分行显示
                                if (!string.IsNullOrEmpty(lastError))
                                {
                                    AppendLog($"[SKILLSET {cardIndex}] ├─ C++ Error Message:");
                                    // 如果错误消息包含多行，逐行显示
                                    string[] errorLines = lastError.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                                    for (int i = 0; i < errorLines.Length; i++)
                                    {
                                        string line = errorLines[i].Trim();
                                        if (!string.IsNullOrEmpty(line))
                                        {
                                            bool isLast = (i == errorLines.Length - 1);
                                            string prefix = isLast ? "│  └─ " : "│  ├─ ";
                                            AppendLog($"[SKILLSET {cardIndex}] {prefix}{line}");
                                        }
                                    }
                                }
                                else
                                {
                                    AppendLog($"[SKILLSET {cardIndex}] ├─ C++ Error Message: (empty)");
                                }
                                
                                // 显示原始卡片内容用于诊断
                                AppendLog($"[SKILLSET {cardIndex}] ├─ Raw Card Content (first 300 chars):");
                                AppendLog($"[SKILLSET {cardIndex}] │  {card.Substring(0, Math.Min(300, card.Length))}");
                                
                                AppendLog($"[SKILLSET {cardIndex}] └─ Diagnostic Info:");
                                AnalyzeParsError(card, cardIndex);
                                AppendLog($"");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"");
                            AppendLog($"[SKILLSET {cardIndex}] ❌ EXCEPTION THROWN");
                            AppendLog($"[SKILLSET {cardIndex}] ├─ Type: {ex.GetType().Name}");
                            AppendLog($"[SKILLSET {cardIndex}] ├─ Message: {ex.Message}");
                            if (!string.IsNullOrEmpty(ex.StackTrace))
                            {
                                AppendLog($"[SKILLSET {cardIndex}] └─ StackTrace:");
                                string[] stackLines = ex.StackTrace.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                                foreach (string line in stackLines.Take(3))  // 只显示前3行
                                {
                                    if (!string.IsNullOrEmpty(line.Trim()))
                                        AppendLog($"[SKILLSET {cardIndex}]    {line.Trim()}");
                                }
                            }
                            AppendLog($"");
                        }
                    }
                    // 处理 ankeset 卡
                    else if (cardType.Equals("ankeset", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLog($"[ANKESET {cardIndex}] CardType confirmed as 'ankeset', passing to RegisterAnkeset()");
                        AppendLog($"[ANKESET {cardIndex}] Card content length: {card.Length} characters");
                        AppendLog($"[ANKESET {cardIndex}] First 200 chars: '{card.Substring(0, Math.Min(200, card.Length))}'");
                        
                        try
                        {
                            AppendLog($"[ANKESET {cardIndex}] ├─ About to call RegisterANKESet()...");
                            
                            int ankesetResult = _interpreter!.RegisterANKESet(card);
                            AppendLog($"[ANKESET {cardIndex}] └─ RegisterANKESet() returned: {ankesetResult}");
                            
                            if (ankesetResult == 0)
                            {
                                AppendLog($"[ANKESET {cardIndex}] ✅ SUCCESS: ANKE preset registered successfully");
                            }
                            else
                            {
                                string lastError = _interpreter.GetLastError();
                                string errorCodeName = GetErrorCodeName(ankesetResult);
                                
                                AppendLog($"");
                                AppendLog($"[ANKESET {cardIndex}] ❌ REGISTRATION FAILED");
                                AppendLog($"[ANKESET {cardIndex}] ├─ Error Code: {ankesetResult} ({errorCodeName})");
                                
                                // 美化错误消息：分行显示
                                if (!string.IsNullOrEmpty(lastError))
                                {
                                    AppendLog($"[ANKESET {cardIndex}] ├─ C++ Error Message:");
                                    // 如果错误消息包含多行，逐行显示
                                    string[] errorLines = lastError.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                                    for (int i = 0; i < errorLines.Length; i++)
                                    {
                                        string line = errorLines[i].Trim();
                                        if (!string.IsNullOrEmpty(line))
                                        {
                                            bool isLast = (i == errorLines.Length - 1);
                                            string prefix = isLast ? "│  └─ " : "│  ├─ ";
                                            AppendLog($"[ANKESET {cardIndex}] {prefix}{line}");
                                        }
                                    }
                                }
                                else
                                {
                                    AppendLog($"[ANKESET {cardIndex}] ├─ C++ Error Message: (empty)");
                                }
                                
                                // 显示原始卡片内容用于诊断
                                AppendLog($"[ANKESET {cardIndex}] ├─ Raw Card Content (first 300 chars):");
                                AppendLog($"[ANKESET {cardIndex}] │  {card.Substring(0, Math.Min(300, card.Length))}");
                                
                                AppendLog($"[ANKESET {cardIndex}] └─ END ERROR REPORT");
                                AppendLog($"");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"");
                            AppendLog($"[ANKESET {cardIndex}] ❌ EXCEPTION THROWN");
                            AppendLog($"[ANKESET {cardIndex}] ├─ Type: {ex.GetType().Name}");
                            AppendLog($"[ANKESET {cardIndex}] ├─ Message: {ex.Message}");
                            if (!string.IsNullOrEmpty(ex.StackTrace))
                            {
                                AppendLog($"[ANKESET {cardIndex}] └─ StackTrace:");
                                string[] stackLines = ex.StackTrace.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                                foreach (string line in stackLines.Take(3))  // 只显示前3行
                                {
                                    if (!string.IsNullOrEmpty(line.Trim()))
                                        AppendLog($"[ANKESET {cardIndex}]    {line.Trim()}");
                                }
                            }
                            AppendLog($"");
                        }
                    }
                    // Track character cards for battle initialization
                    else if (cardType.Equals("character", StringComparison.OrdinalIgnoreCase))
                    {
                        // Parse the character
                        int parseResult = _interpreter!.ParseCharacter(card);
                        if (parseResult == 0)
                        {
                            // Add parsed character to round manager
                            int addResult = _interpreter.AddCharacterToRoundManager();
                            if (addResult == 0)
                            {
                                AppendLog($"[CHAR {characterCount + 1}] ✓ Successfully added to battle");
                                characterCount++;
                            }
                            else
                            {
                                AppendLog($"[CHAR {characterCount + 1}] ✗ Failed to add to battle: {_interpreter.GetLastError()}");
                            }
                        }
                        else
                        {
                            AppendLog($"[CHAR {characterCount + 1}] ✗ Failed to parse character: {_interpreter.GetLastError()}");
                        }
                    }
                }
                
                AppendLog("");
                AppendLog($"[INFO] Summary: {characterCount} character(s) added to battle");
                
                // Initialize battle if we have at least 2 characters
                if (characterCount >= 2)
                {
                    int initResult = _interpreter!.InitializeRoundManager();
                    if (initResult == 0)
                    {
                        AppendLog("[INFO] ✓ Battle initialized successfully");
                        AppendLog("[INFO] ========== EXECUTION COMPLETE ==========");
                        
                        if (_nextRoundButton != null)
                        {
                            _nextRoundButton.IsEnabled = true;
                            AppendLog("[INFO] ✓ Battle ready! Next Round button enabled");
                        }
                    }
                    else
                    {
                        AppendLog("[ERROR] Failed to initialize battle: " + _interpreter.GetLastError());
                        AppendLog("[INFO] ========== EXECUTION FAILED ==========");
                        if (_nextRoundButton != null)
                        {
                            _nextRoundButton.IsEnabled = false;
                        }
                    }
                }
                else
                {
                    AppendLog("[WARN] Need at least 2 characters to start battle");
                    AppendLog("[INFO] ========== EXECUTION COMPLETE ==========");
                    if (_nextRoundButton != null)
                    {
                        _nextRoundButton.IsEnabled = false;
                    }
                }
                
                return;
            }

            // ============ Fallback: Legacy Script Execution ============
            
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Interpreter exists. Calling IsReady()...");
            
            // 检查解释器是否就绪
            bool isReady = false;
            try
            {
                isReady = _interpreter.IsReady();
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] IsReady() returned: {isReady}");
            }
            catch (Exception readyEx)
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] IsReady() threw exception: {readyEx.Message}");
                AppendLog($"[ERROR] IsReady() exception: {readyEx.Message}");
                return;
            }
            
            if (!isReady)
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Interpreter not ready - aborting execution");
                AppendLog("[ERROR] Interpreter is not ready");
                AppendLog("[ERROR] Ensure C++ core libraries (ABot.Core.dll, ABot.CLI.dll) are compiled and present");
                return;
            }

            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Interpreter is ready. Executing script...");
            _interpreter.ClearError();
            int result = _interpreter.ExecuteScript(scriptText);

            if (result == 0)
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Script execution succeeded");
                AppendLog("[SUCCESS] Script executed successfully");
            }
            else
            {
                FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] Script execution failed with result: {result}");
                AppendLog("[ERROR] Script execution failed");
                string errorMsg = _interpreter.GetLastError();
                AppendLog($"[ERROR] {errorMsg}");
            }
            
            AppendLog("[INFO] ========== EXECUTION COMPLETE ==========");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] OnParseButtonClicked exception: {ex.GetType().Name}: {ex.Message}");
            AppendLog($"[ERROR] Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// 预处理脚本：对所有 expr(...) 内部内容进行 Base64 编码
    /// 保持格式：def = expr(BASE64_CONTENT)
    /// </summary>
    private string PreprocessExpressionAttributes(string input)
    {
        var result = new System.Text.StringBuilder();
        int pos = 0;
        
        while (pos < input.Length)
        {
            // 查找 "expr(" 的位置
            int exprPos = input.IndexOf("expr(", pos);
            if (exprPos < 0)
            {
                result.Append(input.Substring(pos));
                break;
            }
            
            // 添加 "expr(" 之前的内容
            result.Append(input.Substring(pos, exprPos - pos + 5));  // 包含 "expr("
            
            // 找到匹配的结束括号
            int bracketCount = 1;
            int i = exprPos + 5;  // 跳过 "expr("
            
            while (i < input.Length && bracketCount > 0)
            {
                if (input[i] == '(')
                    bracketCount++;
                else if (input[i] == ')')
                    bracketCount--;
                i++;
            }
            
            if (bracketCount == 0)
            {
                // 找到了匹配的右括号
                // 提取表达式内容（不含括号）
                string scriptContent = input.Substring(exprPos + 5, i - exprPos - 6);
                
                // 对表达式内容进行 Base64 编码
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(scriptContent);
                string encoded = System.Convert.ToBase64String(bytes);
                
                // 添加编码后的内容
                result.Append(encoded);
                result.Append(")");  // 添加右括号
                
                pos = i;
            }
            else
            {
                // 括号不匹配，直接跳过
                pos = exprPos + 5;
            }
        }
        
        return result.ToString();
    }

    /// <summary>
    /// 從輸入中提取所有參數卡（以 [ ] 括起的內容）
    /// </summary>
    private List<string> ExtractParameterCards(string input)
    {
        var cards = new List<string>();
        
        AppendLog($"[EXTRACT] Input length: {input.Length}");
        
        int openBracket = -1;
        int cardIndex = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '[')
            {
                openBracket = i;
                AppendLog($"[EXTRACT] Found '[' at position {i}");
            }
            else if (input[i] == ']' && openBracket >= 0)
            {
                // 提取括號內的內容
                string card = input.Substring(openBracket + 1, i - openBracket - 1).Trim();
                if (!string.IsNullOrWhiteSpace(card))
                {
                    cardIndex++;
                    AppendLog($"[EXTRACT] Card #{cardIndex}: length={card.Length}, first 80 chars: '{card.Substring(0, Math.Min(80, card.Length))}'");
                    cards.Add(card);
                }
                openBracket = -1;
            }
        }
        
        AppendLog($"[EXTRACT] Total cards extracted: {cards.Count}");
        return cards;
    }

    /// <summary>
    /// 根據 &lt;type value=...&gt; 確定參數卡的類型
    /// </summary>
    private string DetectCardType(string card)
    {
        
        string lowerCard = card.ToLowerInvariant();
        
        // 查找 <type value=...>
        int typeStart = lowerCard.IndexOf("<type");
        if (typeStart < 0)
        {
            return "unknown";
        }
        
        int valueStart = lowerCard.IndexOf("value=", typeStart);
        if (valueStart < 0)
        {
            return "unknown";
        }
        
        // 定位 value= 之後的實際值
        int charValueStart = valueStart + 6; // "value=" 的長度
        
        // 跳過引號或空格
        while (charValueStart < lowerCard.Length && (lowerCard[charValueStart] == '"' || lowerCard[charValueStart] == '\'' || lowerCard[charValueStart] == ' '))
            charValueStart++;
        
        // 找出值的結束位置
        int charValueEnd = charValueStart;
        while (charValueEnd < lowerCard.Length && lowerCard[charValueEnd] != '>' && lowerCard[charValueEnd] != '"' && lowerCard[charValueEnd] != '\'' && lowerCard[charValueEnd] != ' ')
            charValueEnd++;
        
        if (charValueEnd <= charValueStart)
        {
            return "unknown";
        }
        
        string typeValue = lowerCard.Substring(charValueStart, charValueEnd - charValueStart).Trim();
        
        // 根據 type 值返回類型
        if (typeValue.Contains("skillset"))
        {
            return "skillset";
        }
        if (typeValue.Contains("stateset"))
            return "stateset";
        if (typeValue.Contains("ankeset"))
            return "ankeset";
        if (typeValue.Contains("character"))
        {
            return "character";
        }
        
        return typeValue; // 返回具體的 type 值供識別
    }

    /// <summary>
    /// 解析參數卡內容並輸出調試信息
    /// 支持多种格式：value=...、name=value、attribute=value等
    /// </summary>
    private void ParseParameterCardForDebug(string card, string cardType)
    {
        var fields = new Dictionary<string, string>();
        
        // 提取所有 <...> 標籤
        int pos = 0;
        while (pos < card.Length)
        {
            int tagStart = card.IndexOf('<', pos);
            if (tagStart < 0) break;
            
            int tagEnd = card.IndexOf('>', tagStart);
            if (tagEnd < 0) break;
            
            string tag = card.Substring(tagStart + 1, tagEnd - tagStart - 1).Trim();
            
            // 提取標籤名（第一個單詞）
            string[] parts = tag.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                string tagName = parts[0].ToLower();
                
                // 提取所有 key=value 對
                ExtractKeyValuePairs(tag, fields);
                
                // 如果是 dmg/state/skill 等複雜標籤，記錄整個內容
                if (tagName == "dmg" || tagName == "state" || tagName == "skill" || tagName == "dr" || tagName == "dfs")
                {
                    // 移除標籤名後的所有 key=value 對
                    string contentWithoutTagName = tag.Length > parts[0].Length 
                        ? tag.Substring(parts[0].Length).Trim() 
                        : "";
                    
                    if (!string.IsNullOrEmpty(contentWithoutTagName))
                    {
                        fields[tagName] = contentWithoutTagName;
                    }
                }
            }
            
            pos = tagEnd + 1;
        }
        
        // 輸出解析結果
        foreach (var field in fields)
        {
            AppendLog($"  • {field.Key}: {field.Value}");
        }
    }

    /// <summary>
    /// 從標籤中提取所有 key=value 對
    /// </summary>
    private void ExtractKeyValuePairs(string tag, Dictionary<string, string> fields)
    {
        // 找出所有 key= 出現的位置
        int pos = 0;
        while (pos < tag.Length)
        {
            // 尋找 = 符號
            int eqPos = tag.IndexOf('=', pos);
            if (eqPos < 0) break;
            
            // 回溯找出 key 的開始位置（跳過空格）
            int keyEnd = eqPos - 1;
            while (keyEnd >= pos && (tag[keyEnd] == ' ' || tag[keyEnd] == '\t'))
                keyEnd--;
            
            if (keyEnd < pos) 
            {
                pos = eqPos + 1;
                continue;
            }
            
            // 往前找出 key 的開始位置（直到遇到空格、逗號或標籤開始）
            int keyStart = keyEnd;
            while (keyStart > pos && (char.IsLetterOrDigit(tag[keyStart - 1]) || tag[keyStart - 1] == '_'))
                keyStart--;
            
            string key = tag.Substring(keyStart, keyEnd - keyStart + 1).ToLower();
            
            // 從 = 之後開始提取值
            int valueStart = eqPos + 1;
            while (valueStart < tag.Length && (tag[valueStart] == ' ' || tag[valueStart] == '\t' || tag[valueStart] == '"' || tag[valueStart] == '\''))
                valueStart++;
            
            // 找出值的結束位置（直到逗號、空格或標籤結束）
            int valueEnd = valueStart;
            bool inQuotes = false;
            char quoteChar = '\0';
            
            while (valueEnd < tag.Length)
            {
                char c = tag[valueEnd];
                
                if (!inQuotes && (c == '"' || c == '\''))
                {
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (inQuotes && c == quoteChar && (valueEnd + 1 >= tag.Length || tag[valueEnd + 1] != quoteChar))
                {
                    inQuotes = false;
                }
                else if (!inQuotes && (c == ',' || c == ' ' || c == '\t'))
                {
                    break;
                }
                
                valueEnd++;
            }
            
            if (valueEnd > valueStart)
            {
                string value = tag.Substring(valueStart, valueEnd - valueStart).Trim();
                value = value.Trim(new char[] { '"', '\'' });
                
                if (!string.IsNullOrEmpty(value))
                {
                    fields[key] = value;
                }
            }
            
            pos = valueEnd;
        }
    }

    /// <summary>
    /// 下一回合按钮点击事件处理
    /// 执行一回合战斗，包括行动方选择（d100+atk）和行动结果
    /// </summary>
    private void OnNextRoundClicked(object? sender, RoutedEventArgs? e)
    {
        if (_interpreter == null)
        {
            AppendBattleInfo("[ERROR] Interpreter not initialized\n");
            return;
        }

        if (!_interpreter.IsReady())
        {
            AppendBattleInfo("[ERROR] Interpreter is not ready (C++ layer may not be compiled)\n");
            return;
        }

        try
        {
            AppendBattleInfo("\n");
            AppendBattleInfo("[DEBUG] About to call _interpreter.AdvanceRound()\n");
            AppendBattleInfo("[BATTLE] ========== NEXT ROUND START ==========\n");
            AppendBattleInfo($"[BATTLE] Timestamp: {DateTime.Now:HH:mm:ss.fff}\n");

            // 调用 C++ 层的 abot_round_manager_advance()
            int roundResult = _interpreter.AdvanceRound();
            
            AppendBattleInfo($"[DEBUG] AdvanceRound() returned: {roundResult}\n");
            
            if (roundResult != 0)
            {
                AppendBattleInfo($"[ERROR] Round execution failed with code: {roundResult}\n");
                AppendBattleInfo($"[ERROR] Details: {_interpreter.GetLastError()}\n");
                
                // 【诊断】异常时显示技能触发日志
                string skillLog = _interpreter.GetSkillTriggerLog();
                if (!string.IsNullOrEmpty(skillLog))
                {
                    AppendBattleInfo("[🔵 C#_SKILL_TRIGGER_LOG]\n");
                    AppendBattleInfo(skillLog + "\n");
                    AppendBattleInfo("[/🔵 C#_SKILL_TRIGGER_LOG]\n");
                }
                else
                {
                    AppendBattleInfo("[🔵 C#_SKILL_TRIGGER_LOG] (empty - no diagnostics from C++)\n");
                }
                return;
            }

            // ✅ 使用统一的格式化函数显示回合结果
            string roundOutput = FormatRoundOutput(_interpreter);
            foreach (var line in roundOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrEmpty(line))
                {
                    AppendBattleInfo(line + "\n");
                }
            }

            // 检查战斗是否已结束
            if (_interpreter.IsRoundFinished())
            {
                AppendBattleInfo("\n");
                AppendBattleInfo("[BATTLE] ⚔ BATTLE FINISHED! ⚔\n");
            }
            else
            {
                AppendBattleInfo("[BATTLE] ========== ROUND COMPLETE ==========\n");
            }
        }
        catch (Exception ex)
        {
            AppendBattleInfo($"[ERROR] Round execution failed: {ex.Message}\n");
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] OnNextRoundClicked exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reset按钮点击事件 - 重置所有状态和清除面板
    /// </summary>
    private void OnResetButtonClicked(object? sender, RoutedEventArgs? e)
    {
        try
        {
            AppendBattleInfo("\n");
            AppendBattleInfo("[RESET] ========== RESETTING ALL STATES ==========\n");
            AppendBattleInfo($"[RESET] Timestamp: {DateTime.Now:HH:mm:ss.fff}\n");

            // 1. 清空输入框
            if (_scriptInputBox != null)
            {
                _scriptInputBox.Text = "";
                AppendBattleInfo("[RESET] ✓ Input cleared\n");
            }

            // 2. 清空日志
            if (_logOutput != null)
            {
                _logOutput.Text = "";
                AppendBattleInfo("[RESET] ✓ Logs cleared\n");
            }

            // 3. 清空战斗信息
            if (_battleInfoOutput != null)
            {
                _battleInfoOutput.Text = "[Idle] Battle simulator reset and ready for new setup.\n";
            }

            // 4. 重新初始化 RoundManager（如果解释器可用）
            if (_interpreter != null && _interpreter.IsReady())
            {
                try
                {
                    int resetResult = _interpreter.InitializeRoundManager();
                    if (resetResult == 0)
                    {
                        AppendBattleInfo("[RESET] ✓ Round manager reinitialized\n");
                    }
                    else
                    {
                        AppendBattleInfo("[RESET] ! Could not fully reset round manager\n");
                    }
                }
                catch (Exception resetEx)
                {
                    AppendBattleInfo($"[RESET] ! Warning during reset: {resetEx.Message}\n");
                }
            }

            // 5. 禁用Next Round按钮
            if (_nextRoundButton != null)
            {
                _nextRoundButton.IsEnabled = false;
                AppendBattleInfo("[RESET] ✓ Battle controls disabled\n");
            }

            AppendBattleInfo("[RESET] ========== RESET COMPLETE ==========\n");
        }
        catch (Exception ex)
        {
            AppendBattleInfo($"[ERROR] Reset failed: {ex.Message}\n");
            FileLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] OnResetButtonClicked exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 附加战斗信息日志（与执行日志分离）
    /// </summary>
    private void AppendBattleInfo(string message)
    {
        if (_battleInfoOutput == null)
            return;

        _battleInfoOutput.Text += message;

        // 自动滚动到底部
        if (_battleInfoScrollViewer != null)
        {
            _battleInfoScrollViewer.ScrollToEnd();
        }
    }

    /// <summary>
    /// 附加日誌信息
    /// </summary>
    private void AppendLog(string message)
    {
        if (_logOutput == null)
            return;

        _logOutput.Text += message + "\n";

        // 自动滚动到底部
        if (_logScrollViewer != null)
        {
            _logScrollViewer.ScrollToEnd();
        }
    }

    /// <summary>
    /// 创建日志复制菜单
    /// </summary>
    private ContextMenu CreateCopyContextMenu()
    {
        var contextMenu = new ContextMenu();

        var copyAllItem = new MenuItem { Header = "Copy All" };
        copyAllItem.Click += async (_, _) => {
            if (_logOutput != null && !string.IsNullOrEmpty(_logOutput.Text))
            {
                try
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel?.Clipboard != null)
                    {
                        var dataObject = new DataObject();
                        dataObject.Set(DataFormats.Text, _logOutput.Text);
                        await topLevel.Clipboard.SetDataObjectAsync(dataObject);
                        AppendLog("[INFO] Logs copied to clipboard");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[WARN] Failed to copy to clipboard: {ex.Message}");
                }
            }
        };
        contextMenu.Items.Add(copyAllItem);

        var clearItem = new MenuItem { Header = "Clear Logs" };
        clearItem.Click += (_, _) => {
            if (_logOutput != null)
                _logOutput.Text = "";
        };
        contextMenu.Items.Add(clearItem);

        return contextMenu;
    }

    /// <summary>
    /// 获取错误代码的可读名称
    /// </summary>
    private string GetErrorCodeName(int errorCode)
    {
        return errorCode switch
        {
            0 => "SUCCESS",
            1 => "NULL_PTR",
            2 => "INVALID_XML",
            3 => "PARSE_ERROR",
            4 => "COMPILE_ERROR",
            5 => "RUNTIME_ERROR",
            6 => "OUT_OF_MEMORY",
            -1 => "UNKNOWN",
            _ => $"UNDEFINED({errorCode})"
        };
    }

    /// <summary>
    /// 分析解析错误的详细原因
    /// </summary>
    private void AnalyzeParsError(string card, int cardIndex)
    {
        AppendLog($"[SKILLSET {cardIndex}]    ├─ XML Structure Check:");
        
        // 检查基本标签
        bool hasTypeTag = card.Contains("<type");
        bool hasSkilldefTag = card.Contains("<skilldef");
        bool hasDefAttr = card.Contains("def");
        bool hasExprFunc = card.Contains("expr(");
        
        AppendLog($"[SKILLSET {cardIndex}]    │  ├─ Has <type> tag: {(hasTypeTag ? "✓" : "✗")}");
        AppendLog($"[SKILLSET {cardIndex}]    │  ├─ Has <skilldef> tag: {(hasSkilldefTag ? "✓" : "✗")}");
        AppendLog($"[SKILLSET {cardIndex}]    │  ├─ Has 'def' attribute: {(hasDefAttr ? "✓" : "✗")}");
        AppendLog($"[SKILLSET {cardIndex}]    │  └─ Has 'expr(...)' format: {(hasExprFunc ? "✓" : "✗")}");
        
        // 检查 expr(...) 内容
        if (hasExprFunc)
        {
            int exprPos = card.IndexOf("expr(");
            int closingPos = card.IndexOf(")", exprPos);
            
            if (exprPos >= 0 && closingPos > exprPos)
            {
                string exprContent = card.Substring(exprPos, closingPos - exprPos + 1);
                AppendLog($"[SKILLSET {cardIndex}]    ├─ Expression Content:");
                AppendLog($"[SKILLSET {cardIndex}]    │  ├─ Full: '{exprContent.Substring(0, Math.Min(80, exprContent.Length))}...'");
                AppendLog($"[SKILLSET {cardIndex}]    │  ├─ Length: {exprContent.Length} chars");
                
                // 检查是否为Base64
                int contentStart = exprPos + 5; // 跳过 "expr("
                int contentEnd = closingPos;
                string innerContent = card.Substring(contentStart, contentEnd - contentStart);
                
                bool isBase64 = innerContent.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');
                AppendLog($"[SKILLSET {cardIndex}]    │  ├─ Appears to be Base64: {(isBase64 ? "✓" : "✗")}");
                AppendLog($"[SKILLSET {cardIndex}]    │  ├─ Base64 Content Length: {innerContent.Length}");
                AppendLog($"[SKILLSET {cardIndex}]    │  └─ Base64 Content: {innerContent}");
                
                // 尝试解码看看
                if (isBase64)
                {
                    try
                    {
                        byte[] decodedBytes = System.Convert.FromBase64String(innerContent);
                        string decodedText = System.Text.Encoding.UTF8.GetString(decodedBytes);
                        AppendLog($"[SKILLSET {cardIndex}]    ├─ Base64 Decoded:");
                        AppendLog($"[SKILLSET {cardIndex}]    │  ├─ Decoded Length: {decodedText.Length} chars");
                        AppendLog($"[SKILLSET {cardIndex}]    │  └─ Decoded Text: {decodedText}");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[SKILLSET {cardIndex}]    └─ Base64 Decode Failed: {ex.Message}");
                    }
                }
            }
        }
        else
        {
            AppendLog($"[SKILLSET {cardIndex}]    └─ No expr() found in def attribute");
        }
    }

    /// <summary>
    /// 设置ABotMod引用（供外部调用）
    /// </summary>
    public void SetMod(ABotMod mod)
    {
        // Panel is now independent and doesn't need mod reference
        // All script execution is delegated to C++ interpreter
    }

    /// <summary>
    /// 析构函数 - 清理资源
    /// </summary>
    ~ABotPanel()
    {
        // Cleanup if needed in the future
    }
}
