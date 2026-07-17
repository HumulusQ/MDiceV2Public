using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MDiceV2.Interfaces;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia;

namespace CustomizedReply;

/// <summary>
/// 自定义回复Mod - CustomizedReply
/// 
/// 功能介绍：
/// =========
/// 这是一个MDiceV2 Mod的完整示例，展示如何：
/// 1. 实现IModPlugin接口
/// 2. 接收并处理群消息
/// 3. 与宿主程序交互（发送消息、记录日志）
/// 4. 使用多种匹配方式（精确、正则、模糊）
/// 
/// Mod工作流程：
/// ===========
/// 1. 程序启动 -> 调用OnLoad()加载规则库
/// 2. 用户启用Mod -> 调用OnEnable()准备处理消息
/// 3. 有消息到达 -> 调用OnGroupMessage()进行匹配和回复
/// 4. 用户禁用Mod -> 调用OnDisable()停止处理
/// 5. 程序关闭 -> 调用OnUnload()清理资源
/// 
/// 示例规则库结构（见data.json）：
/// ==============================
/// {
///   "replies": [
///     {
///       "trigger": "你好",
///       "matchType": "exact",  // 精确匹配
///       "replies": ["你好呀！", "嗨～"]
///     },
///     {
///       "trigger": "^(早|早上|早安)",
///       "matchType": "regex",  // 正则匹配
///       "replies": ["早上好～"]
///     },
///     {
///       "trigger": "谢谢",
///       "matchType": "fuzzy",  // 模糊匹配（包含）
///       "replies": ["不客气～"]
///     }
///   ]
/// }
/// 
/// 三种匹配方式说明：
/// =================
/// - Exact: 消息内容完全相同，区分大小写
/// - Regex: 使用正则表达式匹配，强大灵活但性能稍低
/// - Fuzzy: 消息包含触发词即可匹配，最宽松但可能误匹配
/// 
/// Mod的设计考虑：
/// ==============
/// - 线程安全：规则库在OnLoad后只读，无需锁定
/// - 性能优化：避免频繁分配内存，使用缓存加速
/// - 错误恢复：捕获异常避免影响主程序
/// - 日志记录：在关键步骤记录日志便于调试
/// </summary>
public class CustomizedReplyMod : IModPlugin, IConfigurable, INavigationPanelProvider
{
    // ============ IModPlugin属性实现 ============
    
    /// <summary>
    /// Mod唯一标识符
    /// 建议格式：com.author.modname
    /// 这个ID会在日志中出现，帮助识别来自此Mod的消息
    /// </summary>
    public string ModId => "com.example.customreply";

    /// <summary>
    /// Mod显示名称
    /// 在UI的Mod管理面板中显示
    /// </summary>
    public string ModName => "Custom Reply System";

    /// <summary>
    /// Mod版本号
    /// 遵循语义化版本：major.minor.patch
    /// - major: 大功能变化或不兼容更新
    /// - minor: 新增功能但向后兼容
    /// - patch: Bug修复
    /// </summary>
    public string Version => "1.0.0";

    /// <summary>
    /// Mod作者
    /// 用于日志、UI显示和统计
    /// </summary>
    public string Author => "Example Author";

    /// <summary>
    /// Mod描述
    /// 简短说明Mod的功能
    /// </summary>
    public string Description => "Provides customized reply functionality for group and private chats with exact, regex, and fuzzy matching support.";

    // ============ INavigationPanelProvider 属性实现 ============

    /// <summary>
    /// 导航面板唯一标识符
    /// </summary>
    public string PanelId => "customized-reply-panel";

    /// <summary>
    /// 导航面板显示名称
    /// </summary>
    public string PanelName => "Customized Reply";

    /// <summary>
    /// 面板在导航栏中的优先级（数值越大越靠前）
    /// </summary>
    public int Priority => 100;

    /// <summary>
    /// 面板的icon来源（暂不使用）
    /// </summary>
    public string? IconSource => null;

    /// <summary>
    /// 是否为Mod面板（区别于系统面板）
    /// </summary>
    public bool IsModPanel => true;

    // ============ 内部状态 ============

    /// <summary>
    /// Mod的上下文接口
    /// 通过此接口与宿主程序交互
    /// - 发送消息
    /// - 获取用户信息
    /// - 记录日志
    /// 
    /// 注：通过构造函数注入，由宿主程序提供
    /// </summary>
    private readonly IModContext _context;

    /// <summary>
    /// 回复规则库
    /// 在OnLoad时从data.json加载
    /// 格式：List<ReplyRule>
    /// </summary>
    private List<ReplyRule> _replyRules = new();

    /// <summary>
    /// 脚本执行结果缓存
    /// 键为规则索引，值为脚本执行的结果
    /// 每次规则触发时生成，用于在回复中引用输出行
    /// </summary>
    private Dictionary<int, ScriptExecutionResult> _executionResults = new();

    /// <summary>
    /// 脚本的持久全局状态存储
    /// 键为规则的触发词（trigger），值为该规则的全局状态字典
    /// 跨多次规则触发保留，除非手动清除
    /// </summary>
    private Dictionary<string, Dictionary<string, object>> _scriptGlobalState = new();

    /// <summary>
    /// 脚本执行器实例
    /// 用于执行Lua脚本（待集成真实引擎）
    /// </summary>
    private ScriptExecutor _scriptExecutor = new();

    /// <summary>
    /// 当前已初始化的脚本实例UID集合
    /// 用于跟踪哪些脚本UID已被加载
    /// 当规则添加/删除时动态更新
    /// </summary>
    private HashSet<string> _activeScriptUids = new();

    /// <summary>
    /// Mod是否已启用
    /// 当用户禁用Mod时设为false
    /// 禁用时Mod仍在内存中但不处理消息
    /// </summary>
    private bool _isEnabled = false;

    /// <summary>
    /// Mod是否已加载
    /// OnLoad调用后设为true
    /// 即使禁用也保持true（资源未释放）
    /// </summary>
    private bool _isLoaded = false;

    /// <summary>
    /// 导航面板是否已注册
    /// 防止重复注册
    /// </summary>
    private bool _navigationPanelRegistered = false;

    // ============ 构造函数 ============

    /// <summary>
    /// Mod构造函数
    /// 由宿主程序通过反射调用，传入IModContext实例
    /// 
    /// 调用过程（宿主程序实现）：
    /// 1. 使用DllImport或Assembly.Load()加载DLL
    /// 2. 查找实现IModPlugin的类
    /// 3. 获取其构造函数：ctor = type.GetConstructor(new[] { typeof(IModContext) })
    /// 4. 使用ctor.Invoke(new object[] { modContext })创建实例
    /// 
    /// 注意：
    /// - 不在构造函数中初始化重资源（如文件读取）
    /// - 重资源初始化应在OnLoad()中进行
    /// - 构造函数应快速返回，避免阻塞主线程
    /// </summary>
    public CustomizedReplyMod(IModContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // ============ 生命周期钩子 ============

    /// <summary>
    /// 初始化钩子 - Mod加载时调用
    /// 
    /// 调用时机：
    /// - 程序启动时，Mod的DLL加载后立即调用
    /// - 仅调用一次，即使Mod被禁用也不重复调用
    /// 
    /// 实现的初始化操作：
    /// 1. 从data.json文件加载回复规则
    /// 2. 编译正则表达式用于性能优化
    /// 3. 验证规则格式的有效性
    /// 4. 记录初始化日志
    /// 5. 向主程序注册导航面板UI
    /// </summary>
    public void OnLoad()
    {
        // 防止重复加载
        if (_isLoaded)
        {
            _context.Log(LogLevel.Warn, "[CustomizedReply] OnLoad called again but Mod already loaded, skipping");
            LogSender.Normal("[CustomizedReply] >>> OnLoad called again but already loaded");
            return;
        }

        try
        {
            LogSender.Normal("[CustomizedReply] >>> ========== OnLoad START ==========");
            _context.Log(LogLevel.Info, "[CustomizedReply] ========== OnLoad START ==========");
            LogSender.Normal("[CustomizedReply] >>> Loading Mod...");
            _context.Log(LogLevel.Info, "[CustomizedReply] Loading Mod...");

            // 初始化ScriptExecutor的MessageProcessor引用，以便Lua脚本可以访问Mod存储
            try
            {
                // ✅ 改进：使用MessageProcessor获取实例
                // 注：GetInstance()已标记为废弃，但IModContext中暂无替代方法，后续可通过依赖注入改进
#pragma warning disable CS0618
                var msgProcessor = MessageProcessor.GetInstance();
#pragma warning restore CS0618
                if (msgProcessor != null)
                {
                    ScriptExecutor.MessageProcessor = msgProcessor;
                    _context.Log(LogLevel.Info, "[CustomizedReply] ✓ ScriptExecutor已初始化，Lua脚本可访问Mod全局存储");
                    LogSender.Normal("[CustomizedReply] >>> ScriptExecutor.MessageProcessor initialized for Mod storage access");

                    // ✅ 注册脚本函数执行器委托
                    // 功能：处理 <func FunctionName()> 标签，使用当前规则的 scriptInstanceUid
                    // 注意：这是一个备用执行器，主要处理在 RuleExecutionEngine 中未捕获的情况
                    msgProcessor.scriptFunctionExecutor = (funcSpec, msg) =>
                    {
                        try
                        {
                            // funcSpec 格式: "FunctionName()" （来自 <func FunctionName()>）
                            // 移除括号和参数，保留函数名
                            string functionName = funcSpec.Trim();
                            int parenIndex = functionName.IndexOf('(');
                            if (parenIndex > 0)
                            {
                                functionName = functionName.Substring(0, parenIndex).Trim();
                            }

                            // ⚠️ 问题: 这里无法获取 scriptInstanceUid
                            // 应该在 RuleExecutionEngine 中处理 <func> 标签
                            // 当前这个委托作为备用，返回错误
                            _context.Log(LogLevel.Warn, $"[CustomizedReply] <func> 标签应在规则处理阶段被替换，不应到达此处。函数名: {functionName}");
                            return $"[警告] <func> 标签处理异常";
                        }
                        catch (Exception ex)
                        {
                            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ 脚本函数执行失败: {ex.Message}");
                            return $"[脚本执行错误] {ex.Message}";
                        }
                    };

                    _context.Log(LogLevel.Info, "[CustomizedReply] ✓ Script function executor registered");
                    LogSender.Normal("[CustomizedReply] >>> Script function executor registered for <func> tag support");
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[CustomizedReply] 初始化ScriptExecutor失败: {ex.Message}");
                LogSender.Warn($"[CustomizedReply] >>> Warning: Failed to initialize ScriptExecutor: {ex.Message}");
            }

            // 从data.json加载规则库
            _context.Log(LogLevel.Info, "[OnLoad] About to load rules from file...");
            LoadRulesFromFile();
            _context.Log(LogLevel.Info, $"[OnLoad] Loaded {_replyRules.Count} rules from file");

            // ✅ 关键：首先初始化ScriptExecutor，设置脚本目录（即使没有规则）
            var scriptsDir = GetCurrentScriptsDirectory();
            _context.Log(LogLevel.Info, $"[OnLoad] Initializing ScriptExecutor with scripts directory: {scriptsDir}");
            ScriptExecutor.Initialize(scriptsDir, new List<ScriptInstance>());
            _context.Log(LogLevel.Info, "[OnLoad] ✓ ScriptExecutor initialized");

            // 动态扫描和初始化脚本实例
            _context.Log(LogLevel.Info, "[OnLoad] About to refresh script instances...");
            RefreshScriptInstances();
            _context.Log(LogLevel.Info, "[OnLoad] Script instances refresh completed");

            // 注册导航面板
            _context.Log(LogLevel.Info, "[OnLoad] About to register navigation panel...");
            RegisterNavigationPanel();
            _context.Log(LogLevel.Info, "[OnLoad] Navigation panel registered");

            _isLoaded = true;
            
            // 自动测试：如果加载后没有规则，添加一条测试规则并保存以验证功能
            if (_replyRules.Count == 0)
            {
                LogSender.Normal("[CustomizedReply] >>> AUTO-TEST: No rules loaded, adding test rule to verify save functionality...");
                var testRule = new ReplyRule
                {
                    Trigger = "测试规则自动添加",
                    MatchType = MatchType.Exact,
                    Replies = new List<string> { "这是自动添加的测试规则" }
                };
                _replyRules.Add(testRule);
                LogSender.Normal($"[CustomizedReply] >>> AUTO-TEST: Test rule added. Total rules: {_replyRules.Count}");
                
                // 立即保存以验证保存是否有效
                LogSender.Normal("[CustomizedReply] >>> AUTO-TEST: Now saving test rule to file...");
                SaveRulesToFile();
                LogSender.Normal("[CustomizedReply] >>> AUTO-TEST: Save completed");
            }
            
            LogSender.Normal($"[CustomizedReply] >>> Mod loaded successfully! _isLoaded={_isLoaded}, RuleCount={_replyRules.Count}");
            _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Mod loaded successfully with {_replyRules.Count} rules. _isLoaded={_isLoaded}, _isEnabled={_isEnabled}");
            LogSender.Normal("[CustomizedReply] >>> ========== OnLoad END ==========");
            _context.Log(LogLevel.Info, "[CustomizedReply] ========== OnLoad END ==========");
        }
        catch (Exception ex)
        {
            LogSender.Error($"[CustomizedReply] >>> EXCEPTION in OnLoad: {ex.Message}");
            LogSender.Normal($"[CustomizedReply] >>> StackTrace: {ex.StackTrace}");
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Failed to load Mod: {ex.Message}\n{ex.StackTrace}");
            throw;  // 让宿主程序捕获异常并记录
        }
    }

    /// <summary>
    /// 注册导航面板到主窗口
    /// </summary>
    private void RegisterNavigationPanel()
    {
        // 防止重复注册导航面板
        if (_navigationPanelRegistered)
        {
            LogSender.Normal("[CustomizedReply] >>> Navigation panel already registered");
            _context.Log(LogLevel.Info, "[CustomizedReply] Navigation panel already registered, skipping");
            return;
        }

        try
        {
            LogSender.Normal("[CustomizedReply] >>> RegisterNavigationPanel() STARTED");
            
            // 检查实现状态
            LogSender.Normal("[CustomizedReply] >>> Checking implementation status - implements INavigationPanelProvider: true");
            _context.Log(LogLevel.Debug, "[CustomizedReply] CustomizedReplyMod implements INavigationPanelProvider");
            
            LogSender.Normal($"[CustomizedReply] >>> Panel info - Id: {PanelId}, Name: {PanelName}, Priority: {Priority}, IsModPanel: {IsModPanel}");
            _context.Log(LogLevel.Info, $"[CustomizedReply] Panel info - Id: {PanelId}, Name: {PanelName}, Priority: {Priority}, IsModPanel: {IsModPanel}");
            
            // 通过 Context 获取导航面板注册表服务
            LogSender.Normal("[CustomizedReply] >>> Calling _context.GetNavigationPanelRegistry()...");
            var registry = _context?.GetNavigationPanelRegistry();
            LogSender.Normal($"[CustomizedReply] >>> Registry result: {(registry != null ? "SUCCESS (not null)" : "NULL")}");
            _context?.Log(LogLevel.Info, $"[CustomizedReply] GetNavigationPanelRegistry returned: {(registry != null ? "INavigationPanelRegistry instance" : "NULL")}");
            
            if (registry == null)
            {
                LogSender.Error("[CustomizedReply] >>> CRITICAL ERROR: Navigation panel registry is NULL!");
                LogSender.Error("[CustomizedReply] >>> This means NavigationPanelRegistry.Instance returned null");
                _context?.Log(LogLevel.Error, "[CustomizedReply] CRITICAL ERROR: Navigation panel registry is NULL - panel registration failed");
                _context?.Log(LogLevel.Warn, "[CustomizedReply] Possible cause: NavigationPanelRegistry not initialized yet, or exception occurred");
                return;
            }

            LogSender.Normal("[CustomizedReply] >>> About to call registry.Register(this)...");
            _context?.Log(LogLevel.Info, "[CustomizedReply] Calling registry.Register() with CustomizedReplyMod as INavigationPanelProvider");
            
            registry.Register(this);
            
            LogSender.Normal("[CustomizedReply] >>> registry.Register() completed without exception");
            _context?.Log(LogLevel.Info, "[CustomizedReply] ✓ Navigation panel registered successfully");
            LogSender.Normal("[CustomizedReply] >>> Panel should now appear in main window navigation bar");
            
            _navigationPanelRegistered = true;
            LogSender.Normal("[CustomizedReply] >>> RegisterNavigationPanel() END - SUCCESS");
        }
        catch (InvalidOperationException ioEx)
        {
            LogSender.Error($"[CustomizedReply] >>> INVALID_OPERATION EXCEPTION (panel ID already registered?): {ioEx.Message}");
            _context?.Log(LogLevel.Error, $"[CustomizedReply] InvalidOperationException during panel registration: {ioEx.Message}");
            _context?.Log(LogLevel.Error, $"[CustomizedReply] Possible cause: PanelId '{PanelId}' already registered by another provider");
        }
        catch (ArgumentException argEx)
        {
            LogSender.Error($"[CustomizedReply] >>> ARGUMENT EXCEPTION: {argEx.Message}");
            _context?.Log(LogLevel.Error, $"[CustomizedReply] ArgumentException during panel registration: {argEx.Message}");
            _context?.Log(LogLevel.Error, $"[CustomizedReply] Possible causes: Missing PanelId, Empty PanelName, null provider, etc.");
        }
        catch (Exception ex)
        {
            LogSender.Error($"[CustomizedReply] >>> UNEXPECTED EXCEPTION in RegisterNavigationPanel: {ex.GetType().Name}");
            LogSender.Normal($"[CustomizedReply] >>> Message: {ex.Message}");
            LogSender.Normal($"[CustomizedReply] >>> StackTrace: {ex.StackTrace}");
            _context?.Log(LogLevel.Error, $"[CustomizedReply] UNEXPECTED Exception in RegisterNavigationPanel: {ex.GetType().Name}: {ex.Message}");
            _context?.Log(LogLevel.Error, $"[CustomizedReply] StackTrace: {ex.StackTrace}");
        }
    }

    // ============ INavigationPanelProvider 实现 ============

    /// <summary>
    /// 创建导航面板UI控件
    /// </summary>
    public Control CreatePanel()
    {
        try
        {
            LogSender.Normal("[CustomizedReply] >>> CreatePanel() START");
            _context?.Log(LogLevel.Info, "[CustomizedReply] CreatePanel() called");
            
            var panel = new CustomizedReply.UI.CustomizedReplyPanel(this);
            LogSender.Normal("[CustomizedReply] >>> CreatePanel() created panel successfully");
            _context?.Log(LogLevel.Info, "[CustomizedReply] Navigation panel created successfully");
            return panel;
        }
        catch (Exception ex)
        {
            LogSender.Error($"[CustomizedReply] >>> CreatePanel() EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _context?.Log(LogLevel.Error, $"[CustomizedReply] CreatePanel() failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 启用钩子 - 用户启用Mod时调用
    /// 
    /// 调用时机：
    /// - 用户在Mod管理面板中启用Mod时调用
    /// - 可能被多次调用（禁用->启用->禁用->启用...）
    /// 
    /// 实现的操作：
    /// - 设置_isEnabled=true，使OnGroupMessage开始处理消息
    /// - 可选：恢复Mod的持久化状态（如上次禁用时保存的配置）
    /// </summary>
    public void OnEnable()
    {
        try
        {
            LogSender.Normal("[CustomizedReply] >>> ========== OnEnable START ==========");
            _context.Log(LogLevel.Info, "[CustomizedReply] ========== OnEnable START ==========");
            if (!_isLoaded)
            {
                LogSender.Normal("[CustomizedReply] >>> OnEnable: Mod not loaded, calling OnLoad()");
                _context.Log(LogLevel.Warn, "[CustomizedReply] OnEnable called but Mod not loaded yet, loading now...");
                OnLoad();
            }

            _isEnabled = true;
            LogSender.Normal($"[CustomizedReply] >>> Mod enabled. _isEnabled={_isEnabled}, RuleCount={_replyRules.Count}");
            _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Mod enabled. _isEnabled={_isEnabled}, RuleCount={_replyRules.Count}");
            LogSender.Normal("[CustomizedReply] >>> ========== OnEnable END ==========");
            _context.Log(LogLevel.Info, "[CustomizedReply] ========== OnEnable END ==========");
        }
        catch (Exception ex)
        {
            LogSender.Error($"[CustomizedReply] >>> OnEnable EXCEPTION: {ex.Message}");
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Failed to enable Mod: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 禁用钩子 - 用户禁用Mod时调用
    /// 
    /// 调用时机：
    /// - 用户在Mod管理面板中禁用Mod时调用
    /// - 可能被多次调用
    /// 
    /// 实现的操作：
    /// - 设置_isEnabled=false，停止处理消息
    /// - 注意：DLL本身不被卸载，_replyRules仍在内存中
    /// - 重新启用时会立即恢复（无需重新加载规则）
    /// 
    /// 与热卸载的区别：
    /// - 禁用：Mod不处理消息，但资源保留，快速启用
    /// - 热卸载：仅适用于Lua脚本Mod，完全清理资源
    /// </summary>
    public void OnDisable()
    {
        try
        {
            _isEnabled = false;
            _context.Log(LogLevel.Info, "CustomizedReply Mod disabled.");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"Failed to disable CustomizedReply Mod: {ex.Message}");
        }
    }

    /// <summary>
    /// 卸载钩子 - 程序关闭时调用
    /// 
    /// 调用时机：
    /// - 程序关闭或Mod从系统卸载时
    /// - 仅调用一次
    /// 
    /// 实现的操作：
    /// - 保存Mod状态（如用户自定义的规则）到持久化存储
    /// - 清理文件句柄、数据库连接等资源
    /// - 不应该抛出异常（如果发生异常应记录但继续执行）
    /// 
    /// 当前示例的OnUnload：
    /// - 保存规则到data.json文件
    /// - 清空规则列表（可选）
    /// - 记录卸载日志
    /// </summary>
    
    /// <summary>
    /// 获取当前加载的所有规则（供UI使用）
    /// </summary>
    public List<ReplyRule> GetLoadedRules()
    {
        return new List<ReplyRule>(_replyRules);
    }

    /// <summary>
    /// 获取实际的内部规则列表（不是副本），用于建立UI和Mod规则的对象引用关联
    /// </summary>
    public List<ReplyRule> GetActualLoadedRules()
    {
        return _replyRules;
    }

    public void OnUnload()
    {
        try
        {
            LogSender.Normal("[CustomizedReply] >>> ========== OnUnload START ==========");
            _context.Log(LogLevel.Info, "[CustomizedReply] ========== OnUnload START ==========");
            
            // 卸载所有脚本（执行dispose函数）
            LogSender.Normal("[CustomizedReply] >>> OnUnload: Unloading all scripts...");
            _context.Log(LogLevel.Info, "[CustomizedReply] OnUnload: Unloading all scripts...");
            try
            {
                ScriptExecutor.UnloadAllScripts();
                LogSender.Normal("[CustomizedReply] >>> ✓ All scripts unloaded successfully");
                _context.Log(LogLevel.Info, "[CustomizedReply] ✓ All scripts unloaded successfully");
            }
            catch (Exception ex)
            {
                LogSender.Warn($"[CustomizedReply] >>> Warning: Error during script unloading: {ex.Message}");
                _context.Log(LogLevel.Warn, $"[CustomizedReply] Warning: Error during script unloading: {ex.Message}");
            }
            
            // 保存规则到data.json
            LogSender.Normal($"[CustomizedReply] >>> OnUnload: Saving {_replyRules.Count} rules to file...");
            _context.Log(LogLevel.Info, $"[CustomizedReply] OnUnload: Saving {_replyRules.Count} rules to file...");
            SaveRulesToFile();

            // 清理资源
            _replyRules.Clear();
            _isEnabled = false;
            _isLoaded = false;
            _navigationPanelRegistered = false;

            LogSender.Normal("[CustomizedReply] >>> ✓ Mod unloaded successfully");
            _context.Log(LogLevel.Info, "[CustomizedReply] ✓ Mod unloaded successfully");
            LogSender.Normal("[CustomizedReply] >>> ========== OnUnload END ==========");
            _context.Log(LogLevel.Info, "[CustomizedReply] ========== OnUnload END ==========");
        }
        catch (Exception ex)
        {
            LogSender.Error($"[CustomizedReply] >>> ✗ EXCEPTION in OnUnload: {ex.Message}");
            LogSender.Normal($"[CustomizedReply] >>> Stack trace: {ex.StackTrace}");
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Exception in OnUnload: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    // ============ 公开方法供UI调用 ============

    /// <summary>
    /// UI添加新规则时调用此方法
    /// 直接添加到内部 _replyRules 列表，保证即时生效
    /// </summary>
    public void AddRuleDirectly(ReplyRule rule)
    {
        if (rule == null) return;
        _replyRules.Add(rule);
        LogSender.Normal($"[CustomizedReply] >>> AddRuleDirectly: Rule added. Trigger='{rule.Trigger}', MatchType={rule.MatchType}, Total rules in _replyRules: {_replyRules.Count}");
        _context.Log(LogLevel.Info, $"[CustomizedReply] 📝 Rule added: '{rule.Trigger}' ({rule.MatchType}). Total rules now: {_replyRules.Count}");
        
        // 动态刷新脚本实例
        RefreshScriptInstances();
        
        // ✅ 触发 OnRulesModified 事件用于推送，不刷新 UI
        try
        {
            var rulesJson = GenerateRulesJson();
            OnRulesModified?.Invoke("mod.customreply.rules", rulesJson);
            _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ OnRulesModified event triggered for rule addition");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Failed to trigger OnRulesModified: {ex.Message}");
        }
    }

    /// <summary>
    /// UI删除规则时调用此方法
    /// 直接从内部 _replyRules 列表中删除
    /// </summary>
    public void RemoveRuleDirectly(ReplyRule rule)
    {
        if (rule == null) return;
        var ruleTrigger = rule.Trigger;
        _replyRules.Remove(rule);
        _context.Log(LogLevel.Info, $"[CustomizedReply] Rule removed directly: '{ruleTrigger}'");
        
        // 动态刷新脚本实例
        RefreshScriptInstances();
        
        // ✅ 触发 OnRulesModified 事件用于推送，不刷新 UI
        try
        {
            var rulesJson = GenerateRulesJson();
            OnRulesModified?.Invoke("mod.customreply.rules", rulesJson);
            _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ OnRulesModified event triggered for rule removal");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Failed to trigger OnRulesModified: {ex.Message}");
        }
    }

    /// <summary>
    /// UI更新规则时调用此方法
    /// 直接应用到内部 _replyRules 列表（因为rule是同一对象引用）
    /// </summary>
    public void UpdateRuleDirectly(ReplyRule rule)
    {
        if (rule == null) return;
        _context.Log(LogLevel.Info, $"[CustomizedReply] Rule updated directly: '{rule.Trigger}' ({rule.MatchType})");
        // 注意：由于rule对象是通过引用传递，修改已经生效
        //       此方法仅用于记录日志和未来扩展
        
        // 动态刷新脚本实例
        RefreshScriptInstances();
        
        // ✅ 触发 OnRulesModified 事件用于推送，不刷新 UI
        try
        {
            var rulesJson = GenerateRulesJson();
            OnRulesModified?.Invoke("mod.customreply.rules", rulesJson);
            _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ OnRulesModified event triggered for rule modification");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Failed to trigger OnRulesModified: {ex.Message}");
        }
    }

    /// <summary>
    /// 生成当前规则的JSON字符串（用于ConfigChanged事件和远程推送）
    /// 返回完整的规则库JSON，包含所有字段
    /// </summary>
    private string GenerateRulesJson()
    {
        var rulesData = new
        {
            replies = _replyRules.Select(r => new
            {
                id = r.Id,
                trigger = r.Trigger,
                matchType = r.MatchType.ToString(),
                replies = r.Replies,
                conditions = r.Conditions.Select(c => new
                {
                    type = c.ConditionType,
                    value = c.Value,
                    value2 = c.Value2,
                    isInverted = c.IsInverted
                }).ToList(),
                scriptInstanceUid = r.ScriptInstanceUid,
                scriptFilePath = r.ScriptFilePath,
                isScriptEditMode = r.IsScriptEditMode,
                scriptCalls = r.ScriptCalls,
                createdAtTicks = r.CreatedAtTicks,
                lastModifiedAtTicks = r.LastModifiedAtTicks
            }).ToList()
        };
        return JsonSerializer.Serialize(rulesData);
    }

    /// <summary>
    /// 动态刷新脚本实例
    /// 扫描所有规则中的 scriptInstanceUid，创建新的或删除不再使用的脚本实例
    /// 调用时机：规则添加、删除、修改时
    /// </summary>
    private void RefreshScriptInstances()
    {
        try
        {
            _context.Log(LogLevel.Info, "========== [RefreshScriptInstances] START ==========");

            // 1. 收集所有规则中应该存在的脚本实例 UID
            var requiredUids = new HashSet<string>();
            foreach (var rule in _replyRules)
            {
                if (!string.IsNullOrEmpty(rule.ScriptInstanceUid))
                {
                    requiredUids.Add(rule.ScriptInstanceUid);
                }
            }

            _context.Log(LogLevel.Info, $"[RefreshScriptInstances] Required script UIDs count: {requiredUids.Count}");
            foreach (var uid in requiredUids)
            {
                _context.Log(LogLevel.Info, $"[RefreshScriptInstances]   - Required UID: {uid}");
            }

            // 2. 构建完整的脚本实例列表（包括现有和新增的）
            var scriptsDir = GetCurrentScriptsDirectory();
            var allInstances = new List<ScriptInstance>();
            var newUids = new HashSet<string>();

            foreach (var uid in requiredUids)
            {
                var rule = _replyRules.FirstOrDefault(r => r.ScriptInstanceUid == uid);
                if (rule != null && !string.IsNullOrEmpty(rule.ScriptFilePath))
                {
                    var instance = new ScriptInstance
                    {
                        Uid = uid,
                        ScriptFileName = rule.ScriptFilePath
                    };
                    allInstances.Add(instance);
                    
                    if (!_activeScriptUids.Contains(uid))
                    {
                        newUids.Add(uid);
                        _context.Log(LogLevel.Info, $"[RefreshScriptInstances]   + NEW instance: {uid} <- {rule.ScriptFilePath}");
                    }
                    else
                    {
                        _context.Log(LogLevel.Info, $"[RefreshScriptInstances]   ✓ EXISTING instance: {uid} <- {rule.ScriptFilePath}");
                    }
                }
            }

            // 3. 检查删除的 UID（不再被任何规则引用）
            var deletedUids = _activeScriptUids.Where(uid => !requiredUids.Contains(uid)).ToList();
            if (deletedUids.Count > 0)
            {
                _context.Log(LogLevel.Info, $"[RefreshScriptInstances] Found {deletedUids.Count} script UIDs to remove");
                foreach (var uid in deletedUids)
                {
                    _context.Log(LogLevel.Info, $"[RefreshScriptInstances]   - DELETED UID: {uid}");
                }
            }

            // 4. ✅ 关键：总是调用Initialize，以确保ScriptExecutor状态正确
            // 即使没有实例，也要调用，这样scriptDirectory会被设置
            try
            {
                _context.Log(LogLevel.Info, $"[RefreshScriptInstances] Initializing ScriptExecutor with {allInstances.Count} total instances");
                _context.Log(LogLevel.Info, $"[RefreshScriptInstances] Scripts directory: {scriptsDir}");
                ScriptExecutor.Initialize(scriptsDir, allInstances);
                
                // 更新活跃UID集合
                _activeScriptUids.Clear();
                foreach (var uid in requiredUids)
                {
                    _activeScriptUids.Add(uid);
                }
                
                if (newUids.Count > 0)
                {
                    _context.Log(LogLevel.Info, $"[RefreshScriptInstances] ✓ Initialized {newUids.Count} new script instances");
                }
                if (deletedUids.Count > 0)
                {
                    _context.Log(LogLevel.Info, $"[RefreshScriptInstances] ✓ Cleaned up {deletedUids.Count} deleted instances");
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[RefreshScriptInstances] ✗ Failed to initialize ScriptExecutor: {ex.Message}");
                _context.Log(LogLevel.Error, $"[RefreshScriptInstances] ✗ Exception details: {ex.StackTrace}");
            }

            _context.Log(LogLevel.Info, $"========== [RefreshScriptInstances] END - Active UIDs: {_activeScriptUids.Count} ==========");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[RefreshScriptInstances] ✗ EXCEPTION: {ex.Message}");
            _context.Log(LogLevel.Error, $"[RefreshScriptInstances] StackTrace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 将当前内存中的所有规则保存到文件
    /// UI在需要时可调用此方法显式保存，或程序关闭时自动调用
    /// </summary>
    public void SaveRulesImmediately()
    {
        LogSender.Normal($"[CustomizedReply] >>> SaveRulesImmediately: About to save {_replyRules.Count} rules");
        _context.Log(LogLevel.Info, $"[CustomizedReply] Saving {_replyRules.Count} rules to file immediately...");
        SaveRulesToFile();
        LogSender.Normal("[CustomizedReply] >>> SaveRulesImmediately: Save completed");
        _context.Log(LogLevel.Info, "[CustomizedReply] ✓ Rules saved successfully");
    }

    /// <summary>
    /// 公开的日志方法，供UI调用，记录规则添加等操作
    /// </summary>
    public void LogInfo(string message)
    {
        _context.Log(LogLevel.Info, message);
    }

    // ============ 消息处理钩子 ============

    /// <summary>
    /// 群消息处理钩子
    /// 
    /// 调用时机：
    /// - 程序接收到每条群消息时调用
    /// - 仅当Mod处于Enabled状态时调用
    /// - 在MessageProcessor处理前调用（可以拦截消息）
    /// 
    /// 参数说明：
    /// - groupId: 发消息的群号
    /// - userId: 发消息的用户QQ号
    /// - content: 消息正文（已清理@前缀）
    /// - isAted: 消息是否@了机器人
    /// 
    /// 返回值说明：
    /// - null: 不处理此消息，继续传递给其他Mod或MessageProcessor
    /// - non-null: 已处理此消息
    ///   - Intercepted=true: 拦截消息，不继续传递
    ///   - Reply: 发送的回复内容
    ///   - StopPropagation: 是否阻止后续Mod处理
    /// 
    /// 匹配流程：
    /// 1. 遍历_replyRules列表
    /// 2. 对每条规则，根据matchType选择匹配方式
    /// 3. 如果匹配成功，随机选择一条回复
    /// 4. 返回ModMessageResult.Intercept()拦截此消息
    /// 5. 如果全部规则都不匹配，返回null继续传递
    /// </summary>
    public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
    {
        // 检查Mod是否启用
        if (!_isEnabled)
        {
            _context.Log(LogLevel.Warn, $"[CustomizedReply] ✗ Mod is disabled! Cannot process message: '{content}'");
            return null;
        }

        _context.Log(LogLevel.Info, $"[CustomizedReply] OnGroupMessage called: Group={groupId}, User={userId}, Content='{content}', RuleCount={_replyRules.Count}");

        try
        {
            // 如果规则为空
            if (_replyRules.Count == 0)
            {
                _context.Log(LogLevel.Warn, "[CustomizedReply] No rules loaded! Please check data.json");
                return null;
            }

            // 遍历规则库，按顺序检查匹配
            for (int i = 0; i < _replyRules.Count; i++)
            {
                var rule = _replyRules[i];
                _context.Log(LogLevel.Info, $"[CustomizedReply] Checking rule #{i + 1}: Trigger='{rule.Trigger}', MatchType={rule.MatchType}, Conditions={rule.Conditions.Count}");

                // 使用 RuleExecutionEngine 执行规则
                var engine = new RuleExecutionEngine(rule, i, _scriptExecutor, _context);
                var reply = engine.Execute(groupId, userId, content);

                if (reply != null)
                {
                    // 缓存脚本执行结果
                    if (engine.LastExecutionResult != null)
                    {
                        _executionResults[i] = engine.LastExecutionResult;
                    }

                    _context.Log(LogLevel.Info,
                        $"[CustomizedReply] ✓ Group:{groupId} User:{userId} - Returning Intercepted=true with reply: '{reply}'");

                    // 返回拦截此消息，并发送回复
                    return ModMessageResult.Intercept(reply, stopPropagation: true, modId: ModId);
                }
                else
                {
                    _context.Log(LogLevel.Info, $"[CustomizedReply] Rule #{i + 1} NOT matched or conditions failed");
                }
            }

            // 没有匹配任何规则，继续传递给其他处理器
            _context.Log(LogLevel.Info, $"[CustomizedReply] No rules matched for message '{content}', returning null");
            return null;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error,
                $"[CustomizedReply] Exception in OnGroupMessage: Group={groupId}, User={userId}, Message='{content}', Error={ex.Message}\n{ex.StackTrace}");
            return null;  // 异常时不处理，继续传递
        }
    }

    /// <summary>
    /// 私聊消息处理钩子
    /// 
    /// 当前实现：不处理私聊消息
    /// 可以在此实现Mod的管理命令，如：
    /// - !customreply add "触发词" "回复内容" -- 添加规则
    /// - !customreply delete "触发词" -- 删除规则
    /// - !customreply list -- 列出所有规则
    /// 
    /// 为了简化示例，此版本不实现私聊功能
    /// 开发者可以根据需要扩展此方法
    /// </summary>
    public ModMessageResult? OnPrivateMessage(long userId, string content)
    {
        // 当前实现不处理私聊
        // 如需扩展，可在此添加Mod管理命令
        return null;
    }

    // ============ 更新逻辑（GitHub Release -> CustomizedReply.mod） ============

    /// <summary>
    /// 检查 GitHub Release 中的最新 UpdatePackageV*，
    /// 查找其中的 CustomizedReplyPackV*.zip，
    /// 并下载到当前程序目录下的 mods/CustomizedReply.mod。
    /// 
    /// 注意：
    /// - 更新过程只负责下载并覆盖压缩包文件，不会尝试热替换当前正在加载的 DLL；
    /// - 新版本要生效，建议在下载完成后重启程序，并通过 Mod 管理面板重新加载/导入。
    /// </summary>
    public async Task<ModUpdateResult> CheckAndUpdateFromGitHubAsync(string owner = "HumulusQ", string repo = "MDiceV2Public")
    {
        var result = new ModUpdateResult();

        try
        {
            _context.Log(LogLevel.Info, "[CustomizedReply.Update] ========== CheckAndUpdateFromGitHub START ==========");

            var releases = await GetAllReleasesAsync(owner, repo);
            if (releases.Count == 0)
            {
                result.Success = false;
                result.Message = "未从 GitHub 获取到任何 Release";
                _context.Log(LogLevel.Warn, "[CustomizedReply.Update] " + result.Message);
                return result;
            }

            // 仿照主程序逻辑：仅考虑名称为 UpdatePackageVn 的 Release，按标签中的数字版本倒序排序
            var candidates = releases
                .Select(r => new { Release = r, NumericTag = ExtractNumericVersion(r.TagName) })
                .Where(x => !string.IsNullOrWhiteSpace(x.Release.Name) && x.Release.Name.StartsWith("UpdatePackageV", StringComparison.OrdinalIgnoreCase))
                .Where(x => System.Version.TryParse(x.NumericTag, out _))
                .OrderByDescending(x => System.Version.Parse(x.NumericTag!))
                .ToList();

            if (candidates.Count == 0)
            {
                result.Success = false;
                result.Message = "未找到任何 UpdatePackageV* 类型的 Release";
                _context.Log(LogLevel.Warn, "[CustomizedReply.Update] " + result.Message);
                return result;
            }

            var latest = candidates[0].Release;
            _context.Log(LogLevel.Info, $"[CustomizedReply.Update] 使用 Release: Name={latest.Name}, Tag={latest.TagName}");

            // 在该 Release 中寻找 CustomizedReplyPackV*.zip
            var modAsset = latest.Assets
                .FirstOrDefault(a => a.Name.StartsWith("CustomizedReplyPackV", StringComparison.OrdinalIgnoreCase)
                                     && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (modAsset == null)
            {
                result.Success = false;
                result.Message = "在最新 UpdatePackageV* Release 中未找到 CustomizedReplyPackV*.zip 资源";
                _context.Log(LogLevel.Warn, "[CustomizedReply.Update] " + result.Message);
                return result;
            }

            var remoteVer = ExtractNumericVersion(modAsset.Name) ?? latest.TagName;
            result.RemoteVersion = remoteVer;
            result.AssetName = modAsset.Name;
            _context.Log(LogLevel.Info, $"[CustomizedReply.Update] 找到远程 Mod 包: {modAsset.Name}, 标记版本={remoteVer}");

            // 目标路径：程序当前目录下的 mods/CustomizedReply.mod
            var appBase = AppDomain.CurrentDomain.BaseDirectory;
            var modsRoot = Path.Combine(appBase, "mods");
            Directory.CreateDirectory(modsRoot);

            var targetPath = Path.Combine(modsRoot, "CustomizedReply.mod");
            var tempPath = Path.Combine(Path.GetTempPath(), $"CustomizedReply_{Guid.NewGuid():N}.mod");

            _context.Log(LogLevel.Info, $"[CustomizedReply.Update] 下载目标: {targetPath}");

            await DownloadAssetAsync(modAsset, tempPath, owner, repo, latest.TagName);

            // 备份旧文件（如果存在）
            if (File.Exists(targetPath))
            {
                try
                {
                    var backupPath = targetPath + ".bak";
                    File.Copy(targetPath, backupPath, overwrite: true);
                    _context.Log(LogLevel.Info, $"[CustomizedReply.Update] 已备份旧文件到: {backupPath}");
                }
                catch (Exception backupEx)
                {
                    _context.Log(LogLevel.Warn, $"[CustomizedReply.Update] 备份旧文件失败: {backupEx.Message}");
                }
            }

            // 覆盖为 CustomizedReply.mod
            File.Copy(tempPath, targetPath, overwrite: true);

            var modFolderPath = Path.Combine(modsRoot, "CustomizedReply");
            bool directoryInstalled = TryInstallPackageToDirectory(targetPath, modFolderPath);
            EnsureSingleStructure(targetPath, modFolderPath, directoryInstalled);

            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 忽略临时文件删除失败
            }

            if (directoryInstalled)
            {
                result.Success = true;
                result.Message = $"已下载并更新 '{modFolderPath}'，远程版本标记={remoteVer}";
            }
            else
            {
                result.Success = false;
                result.Message = "已获取压缩包但解压失败，已保留 mods/CustomizedReply.mod 供手动处理";
            }

            _context.Log(LogLevel.Info, "[CustomizedReply.Update] " + result.Message);
            _context.Log(LogLevel.Info, "[CustomizedReply.Update] ========== CheckAndUpdateFromGitHub END (" + (result.Success ? "success" : "partial") + ") ==========");

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"更新失败: {ex.Message}";
            _context.Log(LogLevel.Error, "[CustomizedReply.Update] ✗ 更新过程出现异常: " + ex.Message + "\n" + ex.StackTrace);
            _context.Log(LogLevel.Info, "[CustomizedReply.Update] ========== CheckAndUpdateFromGitHub END (error) ==========");
            return result;
        }
    }

    /// <summary>
    /// Mod 更新结果
    /// </summary>
    public class ModUpdateResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? RemoteVersion { get; set; }
        public string? AssetName { get; set; }
    }

    /// <summary>
    /// 从 GitHub 获取所有 Release（简化版，实现独立于主程序的 CustomUpdateManager）。
    /// </summary>
    private CustomUpdateManager CreateModUpdateDownloader()
    {
        return new CustomUpdateManager(message =>
            _context.Log(LogLevel.Info, "[CustomizedReply.Update.Downloader] " + message));
    }

    /// <summary>
    /// 通过主程序的共享更新器获取 GitHub Release 列表。
    /// </summary>
    private Task<List<GitHubRelease>> GetAllReleasesAsync(string owner, string repo)
    {
        return CreateModUpdateDownloader().GetGitHubReleasesAsync(owner, repo);
    }

    /// <summary>
    /// 通过主程序的共享更新器下载资源到指定位置。
    /// </summary>
    private Task DownloadAssetAsync(
        GitHubAsset asset,
        string targetPath,
        string owner,
        string repo,
        string? releaseTag = null)
    {
        return CreateModUpdateDownloader().DownloadGitHubAssetAsync(asset, targetPath, owner, repo, releaseTag);
    }

    /// <summary>
    /// 提取版本字符串中的数字部分（最多四段），例如：
    /// "0.2.5.1860-beta" -> "0.2.5.1860"；
    /// "V0251860" -> "0251860"。
    /// </summary>
    private static string? ExtractNumericVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        var match = Regex.Match(versionText, @"^\s*([0-9]+(?:\.[0-9]+){0,3})");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // 不符合点分版本格式时，退而求其次提取连续数字串
        var digits = Regex.Match(versionText, @"[0-9]+");
        return digits.Success ? digits.Value : null;
    }

    /// <summary>
    /// GitHub Release DTO（仅包含 Mod 更新所需字段）。
    /// </summary>
    private sealed class GitHubReleaseDto
    {
        public string? name { get; set; }
        public string? tag_name { get; set; }
        public DateTime published_at { get; set; }
        public string? body { get; set; }

        public List<GitHubAssetDto>? assets { get; set; }

        public string Name => name ?? string.Empty;
        public string TagName => tag_name ?? string.Empty;
        public DateTime PublishedAt => published_at;
        public string Body => body ?? string.Empty;
        public List<GitHubAssetDto> Assets => assets ?? new List<GitHubAssetDto>();
    }

    /// <summary>
    /// GitHub Asset DTO（仅包含名称与下载地址）。
    /// </summary>
    private sealed class GitHubAssetDto
    {
        public string? name { get; set; }
        public long size { get; set; }
        public string? browser_download_url { get; set; }

        public string Name => name ?? string.Empty;
        public long Size => size;
        public string BrowserDownloadUrl => browser_download_url ?? string.Empty;
    }

    /// <summary>
    /// 将 .mod 包解压到指定目录。
    /// </summary>
    private bool TryInstallPackageToDirectory(string packagePath, string targetDirectory)
    {
        try
        {
            _context.Log(LogLevel.Info,
                $"[CustomizedReply.Update] 正在解压 Mod 包到 {targetDirectory}，确保运行时使用最新内容");

            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
                _context.Log(LogLevel.Info, "[CustomizedReply.Update] 已清理旧目录");
            }

            Directory.CreateDirectory(targetDirectory);
            ZipFile.ExtractToDirectory(packagePath, targetDirectory, overwriteFiles: true);

            _context.Log(LogLevel.Info,
                $"[CustomizedReply.Update] ✓ 解压完成，目录内容已刷新: {targetDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error,
                $"[CustomizedReply.Update] ✗ 解压 Mod 包失败，原因: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 确保 mods 下只有 .mod 或同名文件夹其中之一。
    /// </summary>
    private void EnsureSingleStructure(string packagePath, string folderPath, bool directoryIsFinal)
    {
        if (directoryIsFinal)
        {
            // 目录是最终形态，删除 .mod 文件
            try
            {
                if (File.Exists(packagePath))
                {
                    File.Delete(packagePath);
                    _context.Log(LogLevel.Info,
                        "[CustomizedReply.Update] 已删除 CustomizedReply.mod，仅保留同名目录");
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn,
                    $"[CustomizedReply.Update] 删除 CustomizedReply.mod 失败: {ex.Message}");
            }
        }
        else
        {
            // 解压失败，保留 .mod，清理目录避免重复
            try
            {
                if (Directory.Exists(folderPath))
                {
                    Directory.Delete(folderPath, recursive: true);
                    _context.Log(LogLevel.Info,
                        "[CustomizedReply.Update] 解压失败，已移除半成品目录，仅保留 .mod 文件");
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn,
                    $"[CustomizedReply.Update] 清理目录失败: {ex.Message}");
            }
        }
    }

    // ============ 私有辅助方法 ============

    /// <summary>
    /// 将规则库保存到data.json文件
    /// </summary>

    public void SaveRulesToFile()
    {
        var launcherBaseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        // 尝试多个可能的路径来找到保存位置
        var possiblePaths = new[]
        {
            // 路径1: 相对于 AppDomain 基目录的 data/CustomizedReply/data.json（首选）
            // 统一数据存储位置：Launcher/data/CustomizedReply/data.json
            Path.Combine(launcherBaseDir, "data", "CustomizedReply", "data.json"),
            
            // 路径2: 相对于应用程序执行目录
            Path.Combine(AppContext.BaseDirectory, "data", "CustomizedReply", "data.json"),
            
            // 路径3: 向上查找到 MDiceV2.Launcher 目录
            Path.Combine(Directory.GetCurrentDirectory(), "data", "CustomizedReply", "data.json"),
            
            // 路径4: 旧路径 data/mods/CustomizedReply/（向后兼容）
            Path.Combine(launcherBaseDir, "data", "mods", "CustomizedReply", "data.json"),
        };

        string? dataFilePath = null;
        
        LogSender.Normal("[CustomizedReply] >>> SaveRulesToFile() - Checking possible paths:");
        // 优先使用已存在的文件路径，如果都不存在，使用第一个路径
        foreach (var path in possiblePaths)
        {
            bool exists = File.Exists(path);
            LogSender.Error($"[CustomizedReply] >>>   {(exists ? "✓ EXISTS" : "✗ NOT FOUND")}: {path}");
            if (exists)
            {
                dataFilePath = path;
                LogSender.Normal($"[CustomizedReply] >>> Using existing file: {dataFilePath}");
                break;
            }
        }
        
        // 如果没有找到现有文件，使用第一个路径（会创建新文件）
        if (dataFilePath == null)
        {
            dataFilePath = possiblePaths[0];
            LogSender.Normal($"[CustomizedReply] >>> No existing file found, using first path: {dataFilePath}");
        }

        try
        {
            LogSender.Normal($"[CustomizedReply] >>> SaveRulesToFile: Writing {_replyRules.Count} rules to {dataFilePath}");
            _context.Log(LogLevel.Info, $"[CustomizedReply] SaveRulesToFile: Writing {_replyRules.Count} rules to {dataFilePath}");
            
            // 详细日志：显示每个规则的当前状态
            for (int i = 0; i < _replyRules.Count; i++)
            {
                var rule = _replyRules[i];
                _context.Log(LogLevel.Info, $"[CustomizedReply]   Rule #{i + 1}: Trigger='{rule.Trigger}', MatchType={rule.MatchType} ({rule.MatchType.ToString()}), Conditions={rule.Conditions.Count}, ScriptInstanceUid={rule.ScriptInstanceUid ?? "none"}");
            }
            
            // 确保目录存在
            var directory = Path.GetDirectoryName(dataFilePath) ?? "";
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                LogSender.Normal($"[CustomizedReply] >>> Creating directory: {directory}");
                _context.Log(LogLevel.Info, $"[CustomizedReply] Creating directory: {directory}");
                Directory.CreateDirectory(directory);
            }

            // 构建JSON对象
            var replyDtos = _replyRules.Select((rule, index) =>
            {
                var matchTypeStr = rule.MatchType.ToString();
                _context.Log(LogLevel.Info, $"[CustomizedReply] SaveRulesToFile: Rule #{index + 1} '{rule.Trigger}' has MatchType={matchTypeStr} (enum value={rule.MatchType})");
                
                var dto = new Dictionary<string, object>
                {
                    { "id", rule.Id },
                    { "trigger", rule.Trigger },
                    { "matchType", matchTypeStr },
                    { "replies", rule.Replies },
                    { "createdAtTicks", rule.CreatedAtTicks },
                    { "lastModifiedAtTicks", rule.LastModifiedAtTicks }
                };

                // 添加脚本相关字段（如果有脚本实例UID、脚本文件路径、或脚本编辑模式启用）
                if (!string.IsNullOrEmpty(rule.ScriptInstanceUid) || !string.IsNullOrEmpty(rule.ScriptFilePath) || rule.IsScriptEditMode)
                {
                    // 只添加非空的脚本实例UID
                    if (!string.IsNullOrEmpty(rule.ScriptInstanceUid))
                    {
                        dto["scriptInstanceUid"] = rule.ScriptInstanceUid;
                    }
                    
                    // 只添加非空的脚本文件路径
                    if (!string.IsNullOrEmpty(rule.ScriptFilePath))
                    {
                        dto["scriptFilePath"] = rule.ScriptFilePath;
                    }
                    
                    // 总是保存脚本模式标志（如果有任何脚本相关字段）
                    dto["isScriptEditMode"] = rule.IsScriptEditMode;
                    
                    // 只添加非空的脚本调用列表
                    if (rule.ScriptCalls != null && rule.ScriptCalls.Count > 0)
                    {
                        dto["scriptCalls"] = rule.ScriptCalls;
                    }
                }

                // 添加匹配条件（如果有）
                if (rule.Conditions.Count > 0)
                {
                    var conditionsDto = rule.Conditions.Select(c => new Dictionary<string, object>
                    {
                        { "type", c.ConditionType },
                        { "value", c.Value },
                        { "value2", c.Value2 },
                        { "isInverted", c.IsInverted }
                    }).ToList();
                    dto["conditions"] = conditionsDto;
                }

                return dto;
            }).ToList();

            var jsonObject = new
            {
                description = "CustomizedReply Mod 的规则库",
                note = "此文件由CustomizedReply Mod自动维护，可手动编辑",
                replies = replyDtos
            };

            // 序列化为JSON
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(jsonObject, options);

            // 写入文件
            File.WriteAllText(dataFilePath, json);
            LogSender.Normal($"[CustomizedReply] >>> ✓ Saved {_replyRules.Count} rules to {dataFilePath}");
            LogSender.Normal($"[CustomizedReply] >>> File size: {new FileInfo(dataFilePath).Length} bytes");
            LogSender.Normal($"[CustomizedReply] >>> File last modified: {new FileInfo(dataFilePath).LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Saved {_replyRules.Count} reply rules to {dataFilePath}");
        }
        catch (Exception ex)
        {
            LogSender.Error($"[CustomizedReply] >>> ✗ FAILED to save rules: {ex.Message}");
            LogSender.Normal($"[CustomizedReply] >>> Stack trace: {ex.StackTrace}");
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Failed to save rules to {dataFilePath}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 向主程序注册DataIO存储逻辑
    /// 这使得mod的数据可以被持久化到数据库中
    /// </summary>






    /// <summary>
    /// 从data.json文件加载规则库
    /// 
    /// 文件位置：
    /// 应放在Mod文件夹根目录，即 data/mods/CustomizedReply/data.json
    /// 
    /// 文件格式：
    /// {
    ///   "replies": [
    ///     {
    ///       "trigger": "你好",
    ///       "matchType": "exact",
    ///       "replies": ["你好呀", "嗨～"]
    ///     },
    ///     {
    ///       "trigger": "^谢谢",
    ///       "matchType": "regex",
    ///       "replies": ["不客气～"]
    ///     }
    ///   ]
    /// }
    /// 
    /// 错误处理：
    /// - 如果文件不存在，记录警告并使用空规则库
    /// - 如果JSON格式错误，记录错误并重新抛出异常
    /// </summary>
    private void LoadRulesFromFile()
    {
        string? dataFilePath = null;
        
        try
        {
            LogSender.Normal("[CustomizedReply] >>> ========== LoadRulesFromFile START ==========");
            _context.Log(LogLevel.Warn, "[CustomizedReply] ========== LoadRulesFromFile START - Searching for data.json ==========");
            var launcherBaseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            
            // 尝试多个可能的路径来查找 data.json
            var assemblyLocation = typeof(CustomizedReplyMod).Assembly.Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyLocation) ?? "";
            
            _context.Log(LogLevel.Warn, $"[CustomizedReply] Assembly location: {assemblyLocation}");
            _context.Log(LogLevel.Warn, $"[CustomizedReply] Assembly directory: {assemblyDirectory}");
            
            var possiblePaths = new[]
            {
                // 路径1: 相对于 AppDomain 基目录的 data/CustomizedReply/data.json（首选）
                // 统一数据存储位置：Launcher/data/CustomizedReply/data.json
                Path.Combine(launcherBaseDir, "data", "CustomizedReply", "data.json"),
                
                // 路径2: 相对于应用程序执行目录
                Path.Combine(AppContext.BaseDirectory, "data", "CustomizedReply", "data.json"),
                
                // 路径3: 向上查找到 MDiceV2.Launcher 目录
                Path.Combine(Directory.GetCurrentDirectory(), "data", "CustomizedReply", "data.json"),
                
                // 路径4: 旧路径 data/mods/CustomizedReply/（向后兼容）
                Path.Combine(launcherBaseDir, "data", "mods", "CustomizedReply", "data.json"),
                
                // 路径5: 在 mod 目录本身查找 data.json（备用）
                Path.Combine(assemblyDirectory, "data.json"),
                
                // 路径6: 在 mods 目录内查找（用于开发调试）
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CustomizedReply", "data.json"),
            };

            _context.Log(LogLevel.Warn, $"[CustomizedReply] Checking {possiblePaths.Length} possible paths for data.json:");
            LogSender.Normal("[CustomizedReply] >>> LoadRulesFromFile() - Checking possible paths:");
            foreach (var path in possiblePaths)
            {
                bool exists = File.Exists(path);
                var logMsg = $"[CustomizedReply]   {(exists ? "✓ FOUND" : "✗ NOT FOUND")}: {path}";
                _context.Log(exists ? LogLevel.Warn : LogLevel.Info, logMsg);
                LogSender.Normal($"[CustomizedReply] >>> {logMsg}");
                if (exists)
                {
                    dataFilePath = path;
                    LogSender.Normal($"[CustomizedReply] >>> Selected: {dataFilePath}");
                    _context.Log(LogLevel.Warn, $"[CustomizedReply] ✓ SELECTED data.json at: {dataFilePath}");
                    break;
                }
            }

            // 如果文件不存在，使用空规则库
            if (dataFilePath == null || !File.Exists(dataFilePath))
            {
                var errorMsg = $"[CustomizedReply] ✗ CRITICAL: No data.json file found! Checked all {possiblePaths.Length} paths. Using empty rule set.";
                LogSender.Error($"[CustomizedReply] >>> WARNING: {errorMsg}");
                _context.Log(LogLevel.Warn, errorMsg);
                _replyRules = new List<ReplyRule>();
                return;
            }

            // 读取JSON文件
            LogSender.Normal($"[CustomizedReply] >>> Reading data.json: {dataFilePath}");
            _context.Log(LogLevel.Warn, $"[CustomizedReply] Reading data.json from: {dataFilePath}");
            var json = File.ReadAllText(dataFilePath);
            _context.Log(LogLevel.Warn, $"[CustomizedReply] File size: {json.Length} bytes");
            
            var document = JsonDocument.Parse(json);

            // 解析JSON并提取replies数组
            var replies = new List<ReplyRule>();
            if (document.RootElement.TryGetProperty("replies", out var repliesElement))
            {
                var arrayLength = repliesElement.GetArrayLength();
                _context.Log(LogLevel.Warn, $"[CustomizedReply] Found 'replies' array with {arrayLength} rules");
                LogSender.Normal($"[CustomizedReply] >>> Found {arrayLength} reply rules in data.json");
                
                int ruleIndex = 0;
                foreach (var ruleElement in repliesElement.EnumerateArray())
                {
                    try
                    {
                        ruleIndex++;
                        // 解析每条规则
                        var trigger = ruleElement.GetProperty("trigger").GetString() ?? "";
                        var matchTypeStr = ruleElement.GetProperty("matchType").GetString() ?? "exact";
                        
                        // 尝试解析 matchType，支持大小写不敏感
                        MatchType matchType;
                        try
                        {
                            matchType = Enum.Parse<MatchType>(matchTypeStr, ignoreCase: true);
                        }
                        catch (Exception parseEx)
                        {
                            _context.Log(LogLevel.Error, $"[CustomizedReply] Rule #{ruleIndex}: Failed to parse matchType='{matchTypeStr}': {parseEx.Message}. Available types: {string.Join(", ", Enum.GetNames(typeof(MatchType)))}");
                            throw;
                        }

                        var ruleReplies = new List<string>();
                        foreach (var replyElement in ruleElement.GetProperty("replies").EnumerateArray())
                        {
                            ruleReplies.Add(replyElement.GetString() ?? "");
                        }

                        // 读取脚本相关字段（可选）
                        // 注：原有的hasScript, scriptContent, scriptMetadata未在后续使用，已移除以消除编译警告

                        // 读取脚本实例引用（新架构）
                        string? scriptInstanceUid = null;
                        string? scriptFilePath = null;
                        bool isScriptEditMode = false;
                        var scriptCalls = new List<string>();
                        
                        if (ruleElement.TryGetProperty("scriptInstanceUid", out var scriptUidElement))
                        {
                            scriptInstanceUid = scriptUidElement.GetString();
                        }
                        
                        if (ruleElement.TryGetProperty("scriptFilePath", out var scriptFilePathElement))
                        {
                            scriptFilePath = scriptFilePathElement.GetString();
                        }
                        
                        if (ruleElement.TryGetProperty("isScriptEditMode", out var isScriptEditModeElement))
                        {
                            isScriptEditMode = isScriptEditModeElement.GetBoolean();
                        }
                        
                        if (ruleElement.TryGetProperty("scriptCalls", out var scriptCallsElement) && scriptCallsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var callElement in scriptCallsElement.EnumerateArray())
                            {
                                var callName = callElement.GetString();
                                if (!string.IsNullOrEmpty(callName))
                                    scriptCalls.Add(callName);
                            }
                        }

                        // 读取匹配条件列表（可选）
                        var conditions = new List<MatchCondition>();
                        if (ruleElement.TryGetProperty("conditions", out var conditionsElement) && conditionsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var condElement in conditionsElement.EnumerateArray())
                            {
                                var condition = new MatchCondition();
                                if (condElement.TryGetProperty("type", out var typeElement))
                                {
                                    condition.ConditionType = typeElement.GetString() ?? "MatchType";
                                }
                                if (condElement.TryGetProperty("value", out var valueElement))
                                {
                                    condition.Value = valueElement.GetString() ?? "";
                                }
                                if (condElement.TryGetProperty("value2", out var value2Element))
                                {
                                    condition.Value2 = value2Element.GetString() ?? "";
                                }
                                if (condElement.TryGetProperty("isInverted", out var invertedElement))
                                {
                                    condition.IsInverted = invertedElement.GetBoolean();
                                }
                                conditions.Add(condition);
                            }
                        }

                        var ruleId = ruleElement.TryGetProperty("id", out var idElement) 
                            ? idElement.GetString() ?? Guid.NewGuid().ToString()
                            : Guid.NewGuid().ToString();
                        var createdAtTicks = ruleElement.TryGetProperty("createdAtTicks", out var catElement)
                            ? catElement.GetInt64()
                            : DateTime.UtcNow.Ticks;
                        var lastModifiedAtTicks = ruleElement.TryGetProperty("lastModifiedAtTicks", out var lmtElement)
                            ? lmtElement.GetInt64()
                            : DateTime.UtcNow.Ticks;

                        LogSender.Normal($"[CustomizedReply] >>> Rule #{ruleIndex}: '{trigger}' ({matchType}) -> {ruleReplies.Count} replies{(!string.IsNullOrEmpty(scriptInstanceUid) ? " + script" : "")}{(conditions.Count > 0 ? $" + {conditions.Count} conditions" : "")}");
                        _context.Log(LogLevel.Warn, $"[CustomizedReply] Rule #{ruleIndex}: Trigger='{trigger}', Type={matchType}, Replies={ruleReplies.Count}, Conditions={conditions.Count}" +
                            (!string.IsNullOrEmpty(scriptInstanceUid) ? $", ScriptInstanceUid={scriptInstanceUid}" : ""));

                        replies.Add(new ReplyRule
                        {
                            Id = ruleId,
                            Trigger = trigger,
                            MatchType = matchType,
                            Replies = ruleReplies,
                            CompiledRegex = matchType == MatchType.Regex
                                ? new Regex(trigger, RegexOptions.Compiled | RegexOptions.IgnoreCase)
                                : null,
                            ScriptInstanceUid = scriptInstanceUid,
                            ScriptFilePath = scriptFilePath,
                            IsScriptEditMode = isScriptEditMode,
                            ScriptCalls = scriptCalls,
                            Conditions = conditions,
                            CreatedAtTicks = createdAtTicks,
                            LastModifiedAtTicks = lastModifiedAtTicks
                        });
                    }
                    catch (Exception ruleEx)
                    {
                        _context.Log(LogLevel.Error, $"[CustomizedReply] Error parsing rule #{ruleIndex}: {ruleEx.Message}\n{ruleEx.StackTrace}");
                    }
                }
            }
            else
            {
                _context.Log(LogLevel.Warn, "[CustomizedReply] ✗ No 'replies' property found in data.json!");
            }

            _replyRules = replies;
            var ruleCount = _replyRules.Count;
            LogSender.Normal($"[CustomizedReply] >>> Successfully loaded {ruleCount} reply rules from data.json");
            _context.Log(LogLevel.Warn, $"[CustomizedReply] ✓ SUCCESS: Loaded {ruleCount} reply rules from data.json");
            _context.Log(LogLevel.Info, "[CustomizedReply] ========== LoadRulesFromFile END (success) ==========");
        }
        catch (Exception ex)
        {
            LogSender.Normal($"[CustomizedReply] >>> FAILED to load rules: {ex.Message}");
            _context.Log(LogLevel.Error,
                $"[CustomizedReply] ✗ FAILED to load rules from {dataFilePath}: {ex.Message}\n{ex.StackTrace}");
            _replyRules = new List<ReplyRule>();
            _context.Log(LogLevel.Warn, "[CustomizedReply] ========== LoadRulesFromFile END (error, using empty rules) ==========");
        }
    }

    /// <summary>
    /// 构建脚本执行上下文
    /// 将消息信息和规则数据打包为ScriptContext供脚本使用
    /// </summary>
    private ScriptExecutor.ScriptContext BuildScriptContext(long groupId, long userId, string message, ReplyRule rule)
    {
        var context = new ScriptExecutor.ScriptContext
        {
            UserId = userId,
            UserName = "", // TODO: 从消息元数据获取用户名
            GroupId = groupId,
            GroupName = "", // TODO: 从消息元数据获取群名称
            Message = message,
            DefaultReply = rule.Replies.Count > 0 ? rule.Replies[0] : "",
            Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
            IsAted = false, // TODO: 从消息信息获取
            CustomData = new(),
            LocalVariables = new(),
            SaveStateAfter = false
        };

        // 从全局状态字典中恢复脚本的持久状态
        if (_scriptGlobalState.TryGetValue(rule.Trigger, out var globalState))
        {
            context.PersistentState = new Dictionary<string, object>(globalState);
        }

        return context;
    }

    /// <summary>
    /// 获取或初始化脚本的全局状态存储
    /// </summary>
    private Dictionary<string, object> GetOrCreateScriptGlobalState(string ruleTrigger)
    {
        if (!_scriptGlobalState.ContainsKey(ruleTrigger))
        {
            _scriptGlobalState[ruleTrigger] = new Dictionary<string, object>();
        }
        return _scriptGlobalState[ruleTrigger];
    }

    /// <summary>
    /// 精确匹配
    /// 消息内容必须完全等于触发词
    /// </summary>
    private bool MatchExact(string content, string trigger)
    {
        return content == trigger;
    }

    /// <summary>
    /// 正则表达式匹配
    /// 使用.NET Regex进行模式匹配
    /// 支持复杂的正则表达式
    /// </summary>
    private bool MatchRegex(string content, string pattern)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            return regex.IsMatch(content);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"Invalid regex pattern '{pattern}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 模糊匹配
    /// 消息包含触发词即可匹配（不区分大小写）
    /// </summary>
    private bool MatchFuzzy(string content, string keyword)
    {
        return content.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从回复列表中随机选择一条
    /// </summary>
    private string SelectRandomReply(List<string> replies)
    {
        if (replies.Count == 0)
            return "（无回复内容）";

        if (replies.Count == 1)
            return replies[0];

        // 随机选择
        var random = new Random();
        return replies[random.Next(replies.Count)];
    }

    // ============ 脚本同步管理 ============

    /// <summary>
    /// 同步模式是否启用
    /// 启用时从sync-scripts目录读取脚本，禁用时从scripts目录读取
    /// </summary>
    public bool IsSyncModeEnabled { get; private set; } = false;

    /// <summary>
    /// 获取同步模式下的脚本目录
    /// 当与远程程序同步时，脚本从此目录读取
    /// </summary>
    private string GetSyncScriptsDirectory()
    {
        var launcherBaseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        return Path.Combine(launcherBaseDir, "data", "CustomizedReply", "sync-scripts");
    }

    /// <summary>
    /// 获取正常模式下的脚本目录
    /// 非同步时，脚本从此目录读取
    /// </summary>
    private string GetNormalScriptsDirectory()
    {
        var launcherBaseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        return Path.Combine(launcherBaseDir, "data", "CustomizedReply", "scripts");
    }

    /// <summary>
    /// 获取当前应该使用的脚本目录
    /// 根据是否启用P2P同步来决定
    /// </summary>
    public string GetCurrentScriptsDirectory()
    {
        return IsSyncModeEnabled ? GetSyncScriptsDirectory() : GetNormalScriptsDirectory();
    }

    /// <summary>
    /// 启用或禁用同步模式
    /// 当这个值为true时，脚本从sync-scripts目录读取
    /// 当启用时，应确保已从远程拉取配置到sync-scripts/，然后调用UI刷新
    /// </summary>
    public void SetSyncModeEnabled(bool enabled)
    {
        IsSyncModeEnabled = enabled;
        var directory = GetCurrentScriptsDirectory();
        _context?.Log(LogLevel.Info, $"[CustomizedReply] Sync mode: {(enabled ? "ENABLED" : "DISABLED")}, scripts directory switched to: {directory}");
        
        // 创建所选目录（如果不存在）
        // 调用方应该在启用后手动拉取配置和刷新UI
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _context?.Log(LogLevel.Info, $"[CustomizedReply] Created scripts directory: {directory}");
        }
    }

    // ============ IConfigurable 接口实现 ============

    public IReadOnlyList<string> GetConfigKeys()
    {
        return new[] 
        { 
            "mod.customreply.rules",      // 回复规则（包含脚本字段）
            "mod.customreply.scripts",    // 脚本列表同步
            "mod.customreply.enabled",    // Mod启用/禁用
            "mod.customreply.script-usage" // 脚本使用流程说明
        };
    }

    public string? GetConfigValue(string key)
    {
        var lowerKey = key.ToLowerInvariant();
        
        if (lowerKey == "mod.customreply.enabled")
            return _isEnabled.ToString();
        
        if (lowerKey == "mod.customreply.scripts")
        {
            // 拉取时返回【全量】所有脚本列表
            var scriptsDir = GetNormalScriptsDirectory();
            var scriptsList = new List<dynamic>();
            
            if (Directory.Exists(scriptsDir))
            {
                foreach (var file in Directory.GetFiles(scriptsDir, "*.lua"))
                {
                    var fileName = Path.GetFileName(file);
                    var fileSize = new FileInfo(file).Length;
                    var lastModified = File.GetLastWriteTime(file);
                    
                    scriptsList.Add(new
                    {
                        fileName = fileName,
                        size = fileSize,
                        lastModifiedTicks = lastModified.Ticks,
                        content = File.ReadAllText(file)
                    });
                }
            }
            
            var scriptsData = new { scripts = scriptsList };
            return JsonSerializer.Serialize(scriptsData);
        }
        
        if (lowerKey == "mod.customreply.rules")
        {
            // 拉取时返回【全量】所有规则，包含所有字段
            var rulesData = new
            {
                replies = _replyRules.Select(r => new
                {
                    id = r.Id,
                    trigger = r.Trigger,
                    matchType = r.MatchType.ToString(),
                    replies = r.Replies,
                    conditions = r.Conditions.Select(c => new
                    {
                        type = c.ConditionType,
                        value = c.Value,
                        value2 = c.Value2,
                        isInverted = c.IsInverted
                    }).ToList(),
                    scriptInstanceUid = r.ScriptInstanceUid,
                    scriptFilePath = r.ScriptFilePath,
                    isScriptEditMode = r.IsScriptEditMode,
                    scriptCalls = r.ScriptCalls,
                    createdAtTicks = r.CreatedAtTicks,
                    lastModifiedAtTicks = r.LastModifiedAtTicks
                }).ToList()
            };
            return JsonSerializer.Serialize(rulesData);
        }

        if (lowerKey == "mod.customreply.script-usage")
        {
            // 返回脚本使用流程的详细说明
            var scriptUsageHelp = new
            {
                title = "脚本使用流程指南",
                description = "完整的脚本加载、配置、执行和生命周期管理流程",
                sections = new object[]
                {
                    new
                    {
                        step = 1,
                        name = "加载脚本文件",
                        description = "将Lua脚本文件放入脚本目录中",
                        details = new
                        {
                            path = "Scripts目录：{AppBase}/data/mods/CustomizedReply/scripts/",
                            fileFormat = "*.lua",
                            example = "feedpet.lua, echo.lua, config.lua等",
                            note = "文件名将作为脚本的标识符使用，建议使用英文字母和下划线"
                        }
                    },
                    new
                    {
                        step = 2,
                        name = "在规则中开启脚本模式",
                        description = "创建或编辑回复规则，启用脚本模式并配置脚本参数",
                        details = new
                        {
                            action = "1.点击规则配置面板中的'启用脚本模式'复选框",
                            scriptFile = "2.选择要使用的脚本文件（如feedpet.lua）",
                            uid = "3.系统自动生成或指定脚本实例UID（如：feedpet_v1, feedpet_v2）",
                            note = "UID用于区分同一脚本的不同配置实例，支持多个实例共享同一脚本文件"
                        }
                    },
                    new
                    {
                        step = 3,
                        name = "保存并应用规则",
                        description = "保存规则配置，系统会加载脚本文件到内存",
                        details = new
                        {
                            action = "点击规则列表中的'保存'或'应用'按钮",
                            initialization = "系统会自动调用脚本中的initial()函数进行初始化",
                            versionInfo = "每个脚本实例维护独立的虚拟机和全局状态"
                        }
                    },
                    new
                    {
                        step = 4,
                        name = "在规则中调用脚本函数",
                        description = "在回复内容中使用<func>标签调用脚本中定义的函数",
                        details = new
                        {
                            syntax = "<func FunctionName()>",
                            example1 = "<func GetAffinityInfo()> -- 调用GetAffinityInfo函数",
                            example2 = "<func MainProcess()> -- 调用MainProcess函数处理逻辑",
                            lifecycle = "函数在规则匹配时自动调用（不需要手动调用initial）",
                            output = "函数的返回值将替换<func>标签，插入到最终回复中"
                        }
                    },
                    new
                    {
                        step = 5,
                        name = "理解脚本实例UID的作用",
                        description = "UID是脚本实例的唯一标识，管理脚本的生命周期和状态隔离",
                        details = new
                        {
                            purpose = "UID用于将规则与具体的脚本配置关联起来",
                            benefits = new
                            {
                                isolation = "A规则的脚本实例与B规则的脚本实例完全隔离",
                                persistence = "即使规则改变，脚本的全局状态仍会保留",
                                reuse = "多个规则可以共享同一个脚本实例（使用相同UID）"
                            },
                            format = "UID格式：{scriptName}_{version}，如feedpet_v1, echo_v2",
                            tracking = "系统通过ScriptInstanceUid字段追踪每个规则使用的脚本实例"
                        }
                    },
                    new
                    {
                        step = 6,
                        name = "脚本生命周期函数：initial()和dispose()",
                        description = "两个特殊的系统函数，自动在特定时机调用，用于初始化和清理资源",
                        initial = new
                        {
                            name = "initial()",
                            trigger = "首次执行脚本函数时自动调用（仅一次）",
                            purpose = "初始化脚本所需的全局变量、缓存、数据结构等",
                            example = new
                            {
                                code = "function initial()\n    _G.affinity_cache = {}\n    _G.cache_dirty = false\n    _script_registry['feedpet'] = {\n        get_affinity = GetAffinityInfo,\n        query_affinity = QueryAffinity\n    }\nend",
                                description = "初始化缓存和导出API到_script_registry全局注册表"
                            },
                            note = "如果initial()抛出异常，错误会被记录但不会阻止函数执行"
                        },
                        dispose = new
                        {
                            name = "dispose()",
                            trigger = "Mod卸载时自动调用（仅一次）",
                            purpose = "保存脚本状态、释放资源、关闭连接等",
                            example = new
                            {
                                code = "function dispose()\n    if _G.affinity_cache and _G.cache_dirty then\n        for uid, value in pairs(_G.affinity_cache) do\n            mod_storage_write('cache_' .. uid, tostring(value))\n        end\n    end\n    _G.affinity_cache = nil\nend",
                                description = "保存缓存数据到持久存储，释放资源"
                            },
                            note = "如果dispose()抛出异常，错误会被记录但不会阻止Mod卸载"
                        },
                        architecture = new
                        {
                            lifecycle = "执行流程: initial() [首次] → MainProcess() / 其他函数 [每次] → dispose() [卸载]",
                            stateManagement = "脚本全局变量跨多次执行保留，除非手动清除或dispose()清理",
                            errorHandling = "初始化/清理失败不会中断业务逻辑，只在日志中记录警告"
                        }
                    },
                    new
                    {
                        step = 7,
                        name = "脚本间通信与API导出",
                        description = "通过全局_script_registry表实现脚本间的安全通信和API共享",
                        details = new
                        {
                            registry = "_script_registry是全局高层表，每个脚本可在initial()中注册自己的API",
                            example = "_script_registry['feedpet'] = { get_name = ..., query = ... }",
                            usage = "其他脚本通过 local feedpet_api = get_script('feedpet') 获取导出的API",
                            isolation = "脚本只导出desired的接口，内部实现细节保持私有"
                        }
                    }
                },
                tips = new
                {
                    bestPractices = new[]
                    {
                        "始终在initial()中初始化全局变量，避免重复打开文件或创建对象",
                        "在dispose()中保存重要状态到持久存储，以便程序重启后恢复",
                        "使用UID为不同的规则分配不同的脚本实例，实现配置隔离",
                        "在<func>标签中调用的函数应该处理好异常，避免影响回复生成",
                        "使用_script_registry进行脚本间通信，比直接共享全局变量更安全"
                    }
                },
                commonPatterns = new
                {
                    cachedInitialization = new
                    {
                        name = "缓存初始化模式",
                        description = "在initial()中加载数据到内存缓存，后续访问直接从缓存读取",
                        pattern = "initial() { load_data(); } | MainProcess() { search(cache); }"
                    },
                    statefulProcess = new
                    {
                        name = "有状态处理模式",
                        description = "脚本维护状态信息，多次调用间共享状态，在dispose()时保存",
                        pattern = "initial() { load_state(); } | MainProcess() { update_state(); } | dispose() { save_state(); }"
                    },
                    multiInstanceScripts = new
                    {
                        name = "多实例脚本模式",
                        description = "同一脚本文件关联多个不同UID的规则，每个实例独立运行",
                        pattern = "feedpet_v1规则 → feedpet.lua实例1 | feedpet_v2规则 → feedpet.lua实例2"
                    }
                }
            };
            return JsonSerializer.Serialize(scriptUsageHelp, new JsonSerializerOptions { WriteIndented = true });
        }

        return null;
    }

    public ConfigValidationResult ValidateConfig(string key, string value)
    {
        var lowerKey = key.ToLowerInvariant();

        if (lowerKey == "mod.customreply.enabled")
        {
            if (!bool.TryParse(value, out _))
                return ConfigValidationResult.Invalid($"'{key}' 必须为布尔值 (true/false)");
        }

        if (lowerKey == "mod.customreply.scripts")
        {
            if (string.IsNullOrWhiteSpace(value))
                return ConfigValidationResult.Invalid("脚本列表数据不能为空");

            try
            {
                var document = JsonDocument.Parse(value);
                // 全量拉取时应有 scripts 数组，增量推送时应有 modifiedScripts 数组
                bool hasScripts = document.RootElement.TryGetProperty("scripts", out _);
                bool hasModified = document.RootElement.TryGetProperty("modifiedScripts", out _);
                if (!hasScripts && !hasModified)
                    return ConfigValidationResult.Invalid("脚本数据必须包含 'scripts' 或 'modifiedScripts' 数组");
            }
            catch (JsonException ex)
            {
                return ConfigValidationResult.Invalid($"JSON 格式错误: {ex.Message}");
            }
        }

        if (lowerKey == "mod.customreply.rules")
        {
            if (string.IsNullOrWhiteSpace(value))
                return ConfigValidationResult.Invalid("规则数据不能为空");

            try
            {
                var document = JsonDocument.Parse(value);
                if (!document.RootElement.TryGetProperty("replies", out _))
                    return ConfigValidationResult.Invalid("规则必须包含 'replies' 数组");
            }
            catch (JsonException ex)
            {
                return ConfigValidationResult.Invalid($"JSON 格式错误: {ex.Message}");
            }
        }

        return ConfigValidationResult.Valid();
    }

    public async Task<ConfigApplicationResult> ApplyConfigAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        try
        {
            var lowerKey = key.ToLowerInvariant();
            _context.Log(LogLevel.Info, $"[CustomizedReply] → ApplyConfigAsync called: key='{key}', valueLength={value?.Length ?? 0}");

            if (lowerKey == "mod.customreply.enabled")
            {
                if (bool.TryParse(value, out var shouldEnable))
                {
                    if (shouldEnable && !_isEnabled)
                        OnEnable();
                    else if (!shouldEnable && _isEnabled)
                        OnDisable();
                    
                    ConfigChanged?.Invoke(key, value);
                    _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Mod enabled/disabled: {_isEnabled}");
                    return ConfigApplicationResult.Succeed(value);
                }
            }

            if (lowerKey == "mod.customreply.scripts")
            {
                // 检查value是否为null或空白
                if (string.IsNullOrWhiteSpace(value))
                    return ConfigApplicationResult.Fail("mod.customreply.scripts value is empty");
                
                var document = JsonDocument.Parse(value!);
                bool isIncremental = document.RootElement.TryGetProperty("modifiedScripts", out _);
                
                _context.Log(LogLevel.Info, $"[CustomizedReply] ApplyConfigAsync: mod.customreply.scripts - isIncremental={isIncremental}");
                
                // ✅ 对于本地程序A：根据同步模式选择目录
                // ✅ 对于远程程序B：总是只使用 scripts/ 目录
                var targetScriptsDir = IsSyncModeEnabled ? GetSyncScriptsDirectory() : GetNormalScriptsDirectory();
                Directory.CreateDirectory(targetScriptsDir);
                
                _context.Log(LogLevel.Info, $"[CustomizedReply] Writing scripts to: {targetScriptsDir}");
                
                if (isIncremental)
                {
                    // 增量脚本更新
                    if (document.RootElement.TryGetProperty("modifiedScripts", out var modifiedScripts))
                    {
                        int opCount = 0;
                        foreach (var op in modifiedScripts.EnumerateArray())
                        {
                            var opType = op.GetProperty("op").GetString();
                            var fileName = op.GetProperty("fileName").GetString() ?? "";
                            
                            switch (opType)
                            {
                                case "add":
                                case "modify":
                                    {
                                        opCount++;
                                        var content = op.GetProperty("content").GetString() ?? "";
                                        var filePath = Path.Combine(targetScriptsDir, fileName);
                                        File.WriteAllText(filePath, content);
                                        _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Op#{opCount} {(opType == "add" ? "Added" : "Modified")} script: {fileName}");
                                        break;
                                    }
                                    
                                case "delete":
                                    {
                                        opCount++;
                                        var filePath = Path.Combine(targetScriptsDir, fileName);
                                        if (File.Exists(filePath))
                                        {
                                            File.Delete(filePath);
                                        }
                                        _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Op#{opCount} Deleted script: {fileName}");
                                        break;
                                    }
                            }
                        }
                        _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Incremental scripts sync complete: {opCount} operations");
                    }
                }
                else
                {
                    // 全量脚本更新（初始拉取）
                    // 清空目标目录并重新填充
                    if (Directory.Exists(targetScriptsDir))
                        Directory.Delete(targetScriptsDir, true);
                    
                    Directory.CreateDirectory(targetScriptsDir);
                    
                    if (document.RootElement.TryGetProperty("scripts", out var scriptsElement))
                    {
                        int scriptCount = 0;
                        foreach (var scriptElem in scriptsElement.EnumerateArray())
                        {
                            scriptCount++;
                            var fileName = scriptElem.GetProperty("fileName").GetString() ?? "";
                            var content = scriptElem.GetProperty("content").GetString() ?? "";
                            var filePath = Path.Combine(targetScriptsDir, fileName);
                            File.WriteAllText(filePath, content);
                            _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Pulled script #{scriptCount}: {fileName}");
                        }
                        _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Full sync complete: {scriptCount} scripts pulled to {targetScriptsDir}");
                    }
                }
                
                ConfigChanged?.Invoke(key, value);
                return ConfigApplicationResult.Succeed(value);
            }

            // 检查value是否为null或空白
            if (string.IsNullOrWhiteSpace(value))
                return ConfigApplicationResult.Fail("mod.customreply.rules value is empty");

            if (lowerKey == "mod.customreply.rules")
            {
                var document = JsonDocument.Parse(value!);
                
                // 判断是拉取（全量）还是推送（增量）
                bool isIncremental = document.RootElement.TryGetProperty("modifiedRules", out _);
                _context.Log(LogLevel.Info, $"[CustomizedReply] ApplyConfigAsync: mod.customreply.rules - isIncremental={isIncremental}");
                
                if (isIncremental)
                {
                    // 推送收到增量包时：Last-Write-Wins 合并
                    _context.Log(LogLevel.Info, $"[CustomizedReply] Processing incremental rules update (Last-Write-Wins)");
                    if (document.RootElement.TryGetProperty("modifiedRules", out var modifiedRules))
                    {
                        int opCount = 0;
                        foreach (var op in modifiedRules.EnumerateArray())
                        {
                            var opType = op.GetProperty("op").GetString();
                            switch (opType)
                            {
                                case "add":
                                    {
                                        opCount++;
                                        var newRule = ParseRuleFromJson(op.GetProperty("rule"));
                                        _replyRules.Add(newRule);
                                        _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Op#{opCount} Added rule: {newRule.Trigger}");
                                        break;
                                    }
                                    
                                case "modify":
                                    {
                                        opCount++;
                                        var ruleId = op.GetProperty("ruleId").GetString();
                                        var remoteRule = ParseRuleFromJson(op.GetProperty("rule"));
                                        
                                        var existingIdx = _replyRules.FindIndex(r => r.Id == ruleId);
                                        if (existingIdx >= 0)
                                        {
                                            var localRule = _replyRules[existingIdx];
                                            // Last-Write-Wins：远程版本更新则采纳
                                            if (remoteRule.LastModifiedAtTicks > localRule.LastModifiedAtTicks)
                                            {
                                                _replyRules[existingIdx] = remoteRule;
                                                _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Op#{opCount} Updated rule (LWW): {remoteRule.Trigger}");
                                            }
                                            else
                                            {
                                                _context.Log(LogLevel.Info, $"[CustomizedReply] ℹ Op#{opCount} Skipped update (local is newer): {remoteRule.Trigger}");
                                            }
                                        }
                                        else
                                        {
                                            _context.Log(LogLevel.Warn, $"[CustomizedReply] ⚠ Op#{opCount} Modify: rule id {ruleId} not found in local list");
                                        }
                                        break;
                                    }
                                    
                                case "delete":
                                    {
                                        opCount++;
                                        var deleteId = op.GetProperty("ruleId").GetString();
                                        var deleteIdx = _replyRules.FindIndex(r => r.Id == deleteId);
                                        if (deleteIdx >= 0)
                                        {
                                            var deletedTrigger = _replyRules[deleteIdx].Trigger;
                                            _replyRules.RemoveAt(deleteIdx);
                                            _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Op#{opCount} Deleted rule: {deletedTrigger}");
                                        }
                                        else
                                        {
                                            _context.Log(LogLevel.Warn, $"[CustomizedReply] ⚠ Op#{opCount} Delete: rule id {deleteId} not found");
                                        }
                                        break;
                                    }
                            }
                        }
                        _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Incremental sync complete: {opCount} operations processed, total rules now = {_replyRules.Count}");
                    }
                }
                else
                {
                    // 拉取时收到全量包：完全替换
                    var newRules = new List<ReplyRule>();
                    if (document.RootElement.TryGetProperty("replies", out var repliesElement))
                    {
                        _context.Log(LogLevel.Info, $"[CustomizedReply] Starting full sync: replies array has {repliesElement.GetArrayLength()} elements");
                        int ruleCount = 0;
                        foreach (var ruleElement in repliesElement.EnumerateArray())
                        {
                            try
                            {
                                var rule = ParseRuleFromJson(ruleElement);
                                newRules.Add(rule);
                                ruleCount++;
                                _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Parsed rule #{ruleCount}: trigger='{rule.Trigger}', matchType={rule.MatchType}, replies={rule.Replies.Count}");
                            }
                            catch (Exception ex)
                            {
                                _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Failed to parse rule element: {ex.Message}");
                                throw; // Re-throw to be caught by outer catch
                            }
                        }
                    }
                    else
                    {
                        _context.Log(LogLevel.Warn, $"[CustomizedReply] ⚠ Full sync packet missing 'replies' property, creating empty rule list");
                    }
                    _replyRules = newRules;
                    _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ Full sync complete: loaded {newRules.Count} rules into _replyRules");
                }

                ConfigChanged?.Invoke(key, value);
                SaveRulesToFile();
                _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ ConfigChanged event triggered, saved to file");
                return ConfigApplicationResult.Succeed(value);
            }

            if (lowerKey == "mod.customreply.script-usage")
            {
                // 脚本使用流程说明是只读参数，仅用于查询
                _context.Log(LogLevel.Info, $"[CustomizedReply] ℹ mod.customreply.script-usage is a read-only parameter (query only)");
                return ConfigApplicationResult.Succeed("Script usage help is read-only. Use GetConfigValue() to retrieve the documentation.");
            }

            return ConfigApplicationResult.Fail($"未知的配置键: {key}");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ 应用配置失败: {key} - {ex.Message}");
            return ConfigApplicationResult.Fail($"应用异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 从JsonElement解析一条规则
    /// </summary>
    private ReplyRule ParseRuleFromJson(JsonElement ruleElement)
    {
        var scriptCalls = new List<string>();
        if (ruleElement.TryGetProperty("scriptCalls", out var scriptCallsElem) && 
            scriptCallsElem.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var callElem in scriptCallsElem.EnumerateArray())
            {
                var callName = callElem.GetString();
                if (!string.IsNullOrEmpty(callName))
                    scriptCalls.Add(callName);
            }
        }

        var rule = new ReplyRule
        {
            Id = ruleElement.TryGetProperty("id", out var idElem) 
                ? idElem.GetString() ?? Guid.NewGuid().ToString() 
                : Guid.NewGuid().ToString(),
            Trigger = ruleElement.GetProperty("trigger").GetString() ?? "",
            MatchType = Enum.Parse<MatchType>(
                ruleElement.GetProperty("matchType").GetString() ?? "exact", ignoreCase: true),
            Replies = ruleElement.GetProperty("replies").EnumerateArray()
                .Select(r => r.GetString() ?? "").ToList(),
            ScriptInstanceUid = ruleElement.TryGetProperty("scriptInstanceUid", out var siu) 
                ? siu.GetString() : null,
            ScriptFilePath = ruleElement.TryGetProperty("scriptFilePath", out var sfp)
                ? sfp.GetString() : null,
            IsScriptEditMode = ruleElement.TryGetProperty("isScriptEditMode", out var isem)
                ? isem.GetBoolean() : false,
            ScriptCalls = scriptCalls,
            CreatedAtTicks = ruleElement.TryGetProperty("createdAtTicks", out var cat)
                ? cat.GetInt64() : DateTime.UtcNow.Ticks,
            LastModifiedAtTicks = ruleElement.TryGetProperty("lastModifiedAtTicks", out var lmt)
                ? lmt.GetInt64() : DateTime.UtcNow.Ticks,
            CompiledRegex = null  // 稍后处理
        };

        // 加载conditions
        if (ruleElement.TryGetProperty("conditions", out var condsElement) && 
            condsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            rule.Conditions = condsElement.EnumerateArray().Select(c => new MatchCondition
            {
                ConditionType = c.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                Value = c.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                Value2 = c.TryGetProperty("value2", out var v2) ? v2.GetString() ?? "" : "",
                IsInverted = c.TryGetProperty("isInverted", out var inv) && inv.GetBoolean()
            }).ToList();
        }

        // 重编正则表达式
        if (rule.MatchType == MatchType.Regex)
        {
            rule.CompiledRegex = new Regex(rule.Trigger, 
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        return rule;
    }

    // ============ 事件定义 ============
    
    /// <summary>
    /// ✅ 规则修改事件 - 仅用于触发远程推送，不会导致 UI 刷新
    /// 本地修改 (AddRuleDirectly, RemoveRuleDirectly, UpdateRuleDirectly) 会触发此事件
    /// 远程推送接收时不会触发，避免造成失焦问题
    /// </summary>
    public delegate void RulesModifiedEventHandler(string configKey, string configValue);

    // ConfigChanged 来自 IConfigurable 接口定义 (MDiceV2.Interfaces.Mod.ConfigChangedEventHandler)
    public event MDiceV2.Interfaces.Mod.ConfigChangedEventHandler? ConfigChanged;
    public event RulesModifiedEventHandler? OnRulesModified;

    /// <summary>
    /// 通知脚本已修改，触发推送到远程（本地脚本编辑时调用）
    /// 用于脚本保存、导入、删除等操作后，通知宿主应用进行同步推送
    /// </summary>
    public void NotifyScriptsModified()
    {
        try
        {
            // 获取当前脚本配置
            var scriptsJson = GetConfigValue("mod.customreply.scripts");
            if (!string.IsNullOrEmpty(scriptsJson))
            {
                // 触发 ConfigChanged 事件，使宿主应用可以捕捉并推送到远程
                ConfigChanged?.Invoke("mod.customreply.scripts", scriptsJson);
                _context.Log(LogLevel.Info, $"[CustomizedReply] ✓ NotifyScriptsModified: ConfigChanged event triggered for remote sync");
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[CustomizedReply] ✗ Error in NotifyScriptsModified: {ex.Message}");
        }
    }
}

// ============ 顶级数据结构（供UI访问） ============

/// <summary>
/// 匹配条件类（可序列化，用于data.json）
/// 定义规则的额外匹配条件：QQ限制、群号限制、等级限制等
/// </summary>
public class MatchCondition
{
    /// <summary>条件类型（对应UI的MatchConditionType）</summary>
    public string ConditionType { get; set; } = "MatchType"; // MatchType, QQRestriction, GroupRestriction, LevelRestriction, DailyUsageLimit, TimeCooldown

    /// <summary>条件值（如QQ号、群号等，多个值用逗号分隔）</summary>
    public string Value { get; set; } = "";

    /// <summary>附加值（用于作用域、单位等参数）</summary>
    public string Value2 { get; set; } = "";

    /// <summary>是否反相匹配（NOT）</summary>
    public bool IsInverted { get; set; } = false;
}

/// <summary>
/// 规则执行引擎类（单元化设计）
/// 容纳一个规则的完整处理逻辑：匹配、条件检查、脚本执行、回复选择
/// </summary>
public class RuleExecutionEngine
{
    private readonly ReplyRule _rule;
    private readonly int _ruleIndex;
    private readonly ScriptExecutor _scriptExecutor;
    private readonly IModContext _context;

    /// <summary>最后一次执行的脚本输出行（用于替换标签）</summary>
    public List<string> LastScriptOutputLines { get; private set; } = new();

    /// <summary>脚本执行结果缓存</summary>
    public ScriptExecutionResult? LastExecutionResult { get; private set; }

    public RuleExecutionEngine(ReplyRule rule, int ruleIndex, ScriptExecutor scriptExecutor, IModContext context)
    {
        _rule = rule;
        _ruleIndex = ruleIndex;
        _scriptExecutor = scriptExecutor;
        _context = context;
    }

    /// <summary>
    /// 完整的规则执行流程
    /// 返回null表示规则不匹配或条件不满足，返回字符串表示最终回复
    /// </summary>
    public string? Execute(long groupId, long userId, string content, string? userLevel = null, Dictionary<string, Dictionary<string, object>>? scriptGlobalState = null)
    {
        // 1. 检查触发词
        if (!CheckTrigger(content))
            return null;

        _context?.Log(LogLevel.Info, $"[CustomizedReply] ✓ Trigger matched for rule #{_ruleIndex + 1}");

        // 2. 检查补充条件（QQ限制、群号限制、등级限制）
        if (!CheckAllConditions(groupId, userId, userLevel))
        {
            _context?.Log(LogLevel.Info, $"[CustomizedReply] ✗ Conditions not met for rule #{_ruleIndex + 1}");
            return null;
        }

        _context?.Log(LogLevel.Info, $"[CustomizedReply] ✓ All conditions passed for rule #{_ruleIndex + 1}");

        // 3. 执行脚本（如果有）
        ExecuteScript(groupId, userId, content, scriptGlobalState);

        // 4. 选择回复
        var reply = SelectRandomReply(_rule.Replies);

        // 5. 替换脚本输出标签
        if (LastScriptOutputLines.Count > 0 && reply.Contains("<output:"))
        {
            reply = _scriptExecutor.ReplaceIndexedOutputTags(reply, LastScriptOutputLines);
            _context?.Log(LogLevel.Info, $"[CustomizedReply] Replaced <output:> tags in reply");
        }

        // 5a. 处理 <func FunctionName()> 标签（使用规则中的 scriptInstanceUid）
        if (reply.Contains("<func ") && !string.IsNullOrEmpty(_rule.ScriptInstanceUid))
        {
            reply = ProcessFuncTags(reply, groupId, userId, content, userLevel);
            _context?.Log(LogLevel.Info, $"[CustomizedReply] Replaced <func> tags in reply");
        }

        _context?.Log(LogLevel.Info, $"[CustomizedReply] Selected reply: '{reply}' (from {_rule.Replies.Count} options)");

        // 6. 更新限额和冷却时间（如果规则包含这些条件）
        UpdateTrackingMetrics(userId);

        return reply;
    }

    /// <summary>更新限额和冷却时间追踪指标</summary>
    private void UpdateTrackingMetrics(long userId)
    {
        if (_rule.Conditions == null || _rule.Conditions.Count == 0)
            return;

        var msgProc = MessageProcessor.Instance;
        if (msgProc == null)
            return;

        foreach (var condition in _rule.Conditions)
        {
            if (condition.ConditionType == "DailyUsageLimit" && 
                !string.IsNullOrEmpty(condition.Value))
            {
                // 更新每日计数
                string dailyScope = condition.Value2 ?? "按用户";
                _context?.Log(LogLevel.Info, $"[CustomizedReply.UpdateTrackingMetrics] 即将递增每日计数 - 规则: {_rule.Trigger}, 用户: {userId}, 作用域: {dailyScope}");
                
                msgProc.IncrementDailyCount(_rule.Trigger, userId, dailyScope);
                
                _context?.Log(LogLevel.Info, $"[CustomizedReply.UpdateTrackingMetrics] ✓ 每日计数已递增 - 规则: {_rule.Trigger}");
            }

            if (condition.ConditionType == "TimeCooldown" && 
                !string.IsNullOrEmpty(condition.Value))
            {
                // 解析冷却时长
                if (int.TryParse(condition.Value, out int duration))
                {
                    // 解析 Value2: "秒_按用户" 或 "秒_全局" 的格式
                    string unit = "秒";
                    string scope = "按用户";
                    
                    if (!string.IsNullOrEmpty(condition.Value2))
                    {
                        var parts = condition.Value2.Split('_', System.StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            unit = parts[0];
                            scope = parts[1];
                        }
                        else if (parts.Length == 1)
                        {
                            // 如果只有一个部分，假设是单位
                            unit = parts[0];
                            scope = "按用户";
                        }
                    }
                    
                    int durationSeconds = unit switch
                    {
                        "秒" => duration,
                        "分钟" => duration * 60,
                        "小时" => duration * 3600,
                        _ => duration // 默认按秒处理
                    };

                    // 更新冷却时间戳（使用从配置中读取的作用域）
                    msgProc.UpdateCooldownTimestamp(_rule.Trigger, userId, scope, durationSeconds);
                    _context?.Log(LogLevel.Info, $"[CustomizedReply] Updated cooldown timestamp for rule: {_rule.Trigger} (scope: {scope})");
                }
            }
        }
    }

    /// <summary>检查触发词匹配</summary>
    private bool CheckTrigger(string content)
    {
        return _rule.MatchType switch
        {
            MatchType.Exact => content == _rule.Trigger,
            MatchType.Regex => MatchRegex(content, _rule.Trigger),
            MatchType.Fuzzy => content.Contains(_rule.Trigger, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>检查所有补充条件</summary>
    private bool CheckAllConditions(long groupId, long userId, string? userLevel = null)
    {
        if (_rule.Conditions == null || _rule.Conditions.Count == 0)
        {
            _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckAllConditions] 无条件，默认通过");
            return true; // 无条件，默认通过
        }

        _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckAllConditions] 开始检查 {_rule.Conditions.Count} 个条件");

        foreach (var condition in _rule.Conditions)
        {
            _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckAllConditions] 检查条件: {condition.ConditionType} (Value: '{condition.Value}', Value2: '{condition.Value2}', IsInverted: {condition.IsInverted})");
            
            if (!CheckSingleCondition(condition, groupId, userId, userLevel))
            {
                _context?.Log(LogLevel.Warn, $"[CustomizedReply.CheckAllConditions] ✗ 条件检查失败: {condition.ConditionType}");
                return false; // 任何条件不通过就失败
            }
            
            _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckAllConditions] ✓ 条件通过: {condition.ConditionType}");
        }

        _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckAllConditions] ✓ 所有条件都通过");
        return true;
    }

    /// <summary>检查单个条件</summary>
    private bool CheckSingleCondition(MatchCondition condition, long groupId, long userId, string? userLevel)
    {
        _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckSingleCondition] 开始检查 {condition.ConditionType}");
        
        bool result = condition.ConditionType switch
        {
            "QQRestriction" => CheckQQRestriction(userId, condition.Value),
            "GroupRestriction" => CheckGroupRestriction(groupId, condition.Value),
            "LevelRestriction" => CheckLevelRestriction(userLevel, condition.Value),
            "DailyUsageLimit" => CheckDailyLimit(userId, condition.Value, condition.Value2),
            "TimeCooldown" => CheckCooldown(_rule.Trigger, userId, condition.Value, condition.Value2),
            _ => true // 未知条件类型，默认通过
        };

        _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckSingleCondition] 检查结果 (反相前): {result}");

        // 如果反相匹配，取反
        if (condition.IsInverted)
        {
            _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckSingleCondition] 应用反相匹配: {result} -> {!result}");
            result = !result;
        }

        _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckSingleCondition] 最终结果: {result}");
        return result;
    }

    /// <summary>检查每日使用限额</summary>
    private bool CheckDailyLimit(long userId, string limitData, string scope)
    {
        // limitData 格式: "10" (限额数)
        // scope 格式: "按用户" 或 "全局"
        if (string.IsNullOrWhiteSpace(limitData) || !int.TryParse(limitData, out int limitCount))
        {
            _context?.Log(LogLevel.Warn, $"[CustomizedReply.CheckDailyLimit] 参数解析失败: limitData='{limitData}'");
            return true;
        }

        var msgProc = MessageProcessor.Instance;
        if (msgProc == null)
        {
            _context?.Log(LogLevel.Error, $"[CustomizedReply.CheckDailyLimit] MessageProcessor.Instance 为 null!");
            return true;
        }

        _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckDailyLimit] 即将检查每日限额 - 规则: {_rule.Trigger}, 用户: {userId}, 作用域: {scope}, 限额: {limitCount}");
        
        // 使用规则触发词作为规则ID
        bool result = msgProc.CheckDailyLimit(_rule.Trigger, userId, scope ?? "按用户", limitCount);
        
        _context?.Log(LogLevel.Info, $"[CustomizedReply.CheckDailyLimit] 检查结果: {(result ? "✓ 通过 (可继续使用)" : "✗ 失败 (已达限额)")}");
        
        return result;
    }

    /// <summary>检查冷却时间是否已过</summary>
    private bool CheckCooldown(string ruleId, long userId, string durationData, string unitAndScope)
    {
        // durationData 格式: "300" (时间长度)
        // unitAndScope 格式: "秒_按用户" / "秒_全局" / "分钟_按用户" 等
        if (string.IsNullOrWhiteSpace(durationData) || !int.TryParse(durationData, out int duration))
            return true;

        // 解析 Value2: "秒_按用户" 或 "秒_全局" 的格式
        string unit = "秒";
        string scope = "按用户";
        
        if (!string.IsNullOrEmpty(unitAndScope))
        {
            var parts = unitAndScope.Split('_', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                unit = parts[0];
                scope = parts[1];
            }
            else if (parts.Length == 1)
            {
                // 如果只有一个部分，假设是单位（向后兼容）
                unit = parts[0];
                scope = "按用户";
            }
        }
        
        // 解析单位（从 Value2 中取）
        int durationSeconds = unit switch
        {
            "秒" => duration,
            "分钟" => duration * 60,
            "小时" => duration * 3600,
            _ => duration // 默认按秒处理
        };

        var msgProc = MessageProcessor.Instance;
        if (msgProc == null)
            return true;

        // 使用从配置中读取的作用域进行检查
        return msgProc.CheckCooldown(ruleId, userId, scope, durationSeconds);
    }

    /// <summary>检查QQ账号限制</summary>
    private bool CheckQQRestriction(long userId, string qqList)
    {
        if (string.IsNullOrWhiteSpace(qqList))
            return true;

        var qqs = qqList.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(q => q.Trim())
            .Where(q => long.TryParse(q, out _))
            .ToList();

        return qqs.Count > 0 && qqs.Any(q => long.TryParse(q, out long qqId) && qqId == userId);
    }

    /// <summary>检查群号限制</summary>
    private bool CheckGroupRestriction(long groupId, string groupList)
    {
        if (string.IsNullOrWhiteSpace(groupList))
            return true;

        var groups = groupList.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Trim())
            .Where(g => long.TryParse(g, out _))
            .ToList();

        return groups.Count > 0 && groups.Any(g => long.TryParse(g, out long gId) && gId == groupId);
    }

    /// <summary>检查等级限制</summary>
    private bool CheckLevelRestriction(string? userLevel, string requiredLevel)
    {
        if (string.IsNullOrWhiteSpace(requiredLevel))
            return true;

        if (string.IsNullOrWhiteSpace(userLevel))
            return false; // 无等级时，无法通过等级限制

        // 简单的字符串比较或等级值比较
        return userLevel.Equals(requiredLevel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>执行脚本（如果有）</summary>
    private void ExecuteScript(long groupId, long userId, string content, Dictionary<string, Dictionary<string, object>>? scriptGlobalState = null)
    {
        LastScriptOutputLines.Clear();
        LastExecutionResult = null;

        // 新架构：检查 ScriptInstanceUid 而不是 HasScript
        if (string.IsNullOrEmpty(_rule.ScriptInstanceUid))
            return;

        try
        {
            // 构建脚本上下文
            var scriptContext = new ScriptExecutor.ScriptContext
            {
                UserId = userId,
                GroupId = groupId,
                Message = content,
                DefaultReply = _rule.Replies.Count > 0 ? _rule.Replies[0] : "",
                Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                IsAted = false
            };

            // 恢复全局状态
            if (scriptGlobalState != null && scriptGlobalState.TryGetValue(_rule.Trigger, out var globalState))
            {
                scriptContext.PersistentState = new Dictionary<string, object>(globalState);
            }

            // 执行脚本
            var executionStartTime = DateTime.Now;
            // TODO: 新架构使用 ExecuteFunction(_rule.ScriptInstanceUid, functionName, scriptContext)
            // 临时方案：调用 MainProcess 函数
            var result = ScriptExecutor.ExecuteFunction(_rule.ScriptInstanceUid, "MainProcess", scriptContext);
            LastScriptOutputLines = string.IsNullOrEmpty(result) ? new List<string>() : new List<string> { result };

            // 缓存结果
            LastExecutionResult = new ScriptExecutionResult
            {
                RuleIndex = _ruleIndex,
                FullOutput = string.Join("\n", LastScriptOutputLines),
                OutputLines = LastScriptOutputLines,
                ExecutedAt = executionStartTime,
                ExecutionTime = DateTime.Now - executionStartTime
            };

            // 保存持久状态（由调用者负责）
            if (scriptContext.SaveStateAfter && scriptGlobalState != null)
            {
                if (!scriptGlobalState.ContainsKey(_rule.Trigger))
                    scriptGlobalState[_rule.Trigger] = new Dictionary<string, object>();

                foreach (var kvp in scriptContext.PersistentState)
                {
                    scriptGlobalState[_rule.Trigger][kvp.Key] = kvp.Value;
                }
            }

            _context?.Log(LogLevel.Info,
                $"[CustomizedReply] Script executed: {LastScriptOutputLines.Count} lines, {LastExecutionResult.ExecutionTime.TotalMilliseconds:F1}ms");
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[CustomizedReply] Script execution error: {ex.Message}");
            LastScriptOutputLines = new List<string> { $"脚本执行错误: {ex.Message}" };
        }
    }

    /// <summary>正则表达式匹配</summary>
    private bool MatchRegex(string content, string pattern)
    {
        try
        {
            return _rule.CompiledRegex?.IsMatch(content) ?? System.Text.RegularExpressions.Regex.IsMatch(content, pattern);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>随机选择回复</summary>
    private string SelectRandomReply(List<string> replies)
    {
        if (replies.Count == 0)
            return "（无回复内容）";
        if (replies.Count == 1)
            return replies[0];

        return replies[new Random().Next(replies.Count)];
    }

    /// <summary>处理 <func FunctionName()> 标签，使用规则的 scriptInstanceUid</summary>
    private string ProcessFuncTags(string reply, long groupId, long userId, string content, string? userLevel = null)
    {
        try
        {
            if (string.IsNullOrEmpty(_rule.ScriptInstanceUid))
                return reply;

            // 查找并替换所有 <func FunctionName()> 标签
            var funcMatches = System.Text.RegularExpressions.Regex.Matches(reply, @"<func\s+(\w+)\s*\(\s*\)>");
            
            if (funcMatches.Count == 0)
                return reply;

            foreach (System.Text.RegularExpressions.Match match in funcMatches)
            {
                string functionName = match.Groups[1].Value;
                string result = ExecuteFuncTag(functionName, groupId, userId, content, userLevel);
                reply = reply.Replace(match.Value, result);
            }

            return reply;
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[CustomizedReply] Error processing <func> tags: {ex.Message}");
            return reply;
        }
    }

    /// <summary>执行单个 <func> 标签对应的脚本函数</summary>
    private string ExecuteFuncTag(string functionName, long groupId, long userId, string content, string? userLevel = null)
    {
        try
        {
            // 调用脚本函数（确保scriptInstanceUid不为null）
            if (string.IsNullOrEmpty(_rule.ScriptInstanceUid))
            {
                _context?.Log(LogLevel.Warn, $"[CustomizedReply] Warning: Cannot execute script function '{functionName}' - ScriptInstanceUid is empty");
                return "[脚本实例UID为空]";
            }
            
            // 构建脚本执行上下文
            var context = new ScriptExecutor.ScriptContext
            {
                UserId = userId,
                UserName = "",
                GroupId = groupId,
                GroupName = "",
                Message = content,
                DefaultReply = "",
                Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                IsAted = false
            };

            // 调用脚本函数
            var result = ScriptExecutor.ExecuteFunction(_rule.ScriptInstanceUid, functionName, context);
            _context?.Log(LogLevel.Info, $"[CustomizedReply] ✓ Script function executed: {_rule.ScriptInstanceUid}:{functionName} -> {result}");
            
            return result ?? "[脚本无返回值]";
        }
        catch (Exception ex)
        {
            _context?.Log(LogLevel.Error, $"[CustomizedReply] ✗ Script function execution failed for {functionName}: {ex.Message}");
            return $"[脚本错误: {functionName}]";
        }
    }
}

/// <summary>
/// 脚本执行结果缓存（保存脚本的输出数据）
/// </summary>
public class ScriptExecutionResult
{
    /// <summary>规则索引（用于快速查找）</summary>
    public int RuleIndex { get; set; }

    /// <summary>完整脚本输出（所有行的合并结果）</summary>
    public string FullOutput { get; set; } = "";

    /// <summary>脚本输出行列表（支持<output:N>标签索引）</summary>
    public List<string> OutputLines { get; set; } = new();

    /// <summary>脚本执行时间戳</summary>
    public DateTime ExecutedAt { get; set; }

    /// <summary>脚本执行耗时</summary>
    public TimeSpan ExecutionTime { get; set; }
}

/// <summary>
/// 脚本元数据（记录脚本的版本、创建时间等信息）
/// </summary>
public class ScriptMetadata
{
    /// <summary>脚本显示名称</summary>
    public string ScriptName { get; set; } = "";

    /// <summary>脚本创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>脚本版本号</summary>
    public string? Version { get; set; }

    /// <summary>脚本是否需要使用持久状态</summary>
    public bool RequiresState { get; set; } = false;
}

/// <summary>
/// 脚本资源（物理文件）
/// 代表 data/mods/CustomizedReply/scripts 目录中的脚本文件
/// </summary>
public class ScriptResource
{
    /// <summary>脚本文件名（含扩展名，如 "counter.lua"）</summary>
    public string FileName { get; set; } = "";

    /// <summary>脚本文件内容</summary>
    public string Content { get; set; } = "";

    /// <summary>脚本最后修改时间</summary>
    public DateTime LastModified { get; set; } = DateTime.Now;

    /// <summary>脚本描述（可选）</summary>
    public string Description { get; set; } = "";
}

/// <summary>
/// 脚本实例（运行时对象）
/// 一个实例 UID 对应一个独立的 Lua 虚拟机和长期变量作用域
/// 多个规则可以共享同一个实例 UID 来共享状态，或者各自指定不同的 UID 来隔离状态
/// </summary>
public class ScriptInstance
{
    /// <summary>实例 UID（用户定义，唯一性由程序保证）</summary>
    public string Uid { get; set; } = Guid.NewGuid().ToString();

    /// <summary>绑定的脚本文件名（如 "counter.lua"）</summary>
    public string ScriptFileName { get; set; } = "";

    /// <summary>实例描述（用途、用途说明等）</summary>
    public string Description { get; set; } = "";

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class ReplyRule
{
    /// <summary>触发词</summary>
    public string Trigger { get; set; } = "";

    /// <summary>匹配类型（精确、正则、模糊）</summary>
    public MatchType MatchType { get; set; } = MatchType.Exact;

    /// <summary>可能的回复列表（随机选择）</summary>
    public List<string> Replies { get; set; } = new();

    /// <summary>编译后的正则表达式（仅在MatchType==Regex时使用）</summary>
    public Regex? CompiledRegex { get; set; }

    /// <summary>绑定的脚本实例 UID（可选，为空表示不使用脚本）</summary>
    public string? ScriptInstanceUid { get; set; }

    /// <summary>选中的脚本文件名（用于UI恢复和持久化存储）</summary>
    public string? ScriptFilePath { get; set; }

    /// <summary>脚本编辑模式标志（用于持久化脚本模式的开关状态）</summary>
    public bool IsScriptEditMode { get; set; } = false;

    /// <summary>脚本函数调用列表（记录此规则中用到的 <run:FunctionName> 调用）</summary>
    public List<string> ScriptCalls { get; set; } = new();

    /// <summary>补充匹配条件列表（QQ限制、群号限制、等级限制等）</summary>
    public List<MatchCondition> Conditions { get; set; } = new();

    /// <summary>规则唯一标识（用于增量推送和版本管理）</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>规则创建时间（Ticks）</summary>
    public long CreatedAtTicks { get; set; } = DateTime.UtcNow.Ticks;

    /// <summary>规则最后修改时间（Ticks，用于Last-Write-Wins冲突处理）</summary>
    public long LastModifiedAtTicks { get; set; } = DateTime.UtcNow.Ticks;
}

/// <summary>
/// 匹配类型枚举
/// </summary>
public enum MatchType
{
    /// <summary>精确匹配：消息内容完全等于触发词</summary>
    Exact,

    /// <summary>正则表达式匹配：使用Regex模式匹配</summary>
    Regex,

    /// <summary>模糊匹配：消息包含触发词即可匹配</summary>
    Fuzzy
}

