using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MDiceV2.Interfaces;
using MDiceV2.Interfaces.Mod;

namespace MDiceV2.Core.Mod;

/// <summary>
/// Mod 事件分发网桥
/// 负责：
/// 1. 管理所有已加载 Mod 的生命周期
/// 2. 分发群消息和私聊消息到各 Mod
/// 3. 处理 Mod 的启用/禁用状态
/// 4. 协调多 Mod 的消息处理链
/// </summary>
public class ModEventBridge
{
    public event Action? CommandProvidersChanged;

    /// <summary>
    /// 日志接口
    /// </summary>
    private readonly IModContext _modContext;

    /// <summary>
    /// 已加载 Mod 的列表（包括禁用的）
    /// Key: Mod ID，Value: (Mod 实例, 元数据, 是否启用)
    /// </summary>
    private readonly Dictionary<string, (IModPlugin Plugin, IModMetadata Metadata, bool IsEnabled)> _mods = new();

    /// <summary>
    /// 已启用且已加载的 Mod（缓存，按优先级排序）
    /// </summary>
    private List<(IModPlugin Plugin, IModMetadata Metadata)> _enabledModsCache = new();

    /// <summary>
    /// 缓存是否有效
    /// </summary>
    private bool _cacheValid = false;

    /// <summary>
    /// 构造函数
    /// </summary>
    public ModEventBridge(IModContext modContext)
    {
        _modContext = modContext ?? throw new ArgumentNullException(nameof(modContext));
    }

    /// <summary>
    /// 注册 Mod
    /// 在 ModPluginLoader 加载完成后调用
    /// </summary>
    public void RegisterMod(IModPlugin plugin, IModMetadata metadata, bool isEnabled = true)
    {
        if (_mods.ContainsKey(metadata.Id))
        {
            _modContext.Log(LogLevel.Warn, $"Mod already registered: {metadata.Id}");
            return;
        }

        _mods[metadata.Id] = (plugin, metadata, isEnabled);
        InvalidateCache();
        SynchronizeNavigationPanel(plugin, isEnabled);

        _modContext.Log(LogLevel.Debug,
            $"Registered mod: {metadata.Name} (Enabled: {isEnabled})");
        _modContext.Log(LogLevel.Info,
            $"[ModBridge] RegisterMod id={metadata.Id} enabled={isEnabled} bridgeId={GetObjectId(this)}");
        CommandProvidersChanged?.Invoke();
    }

    /// <summary>
    /// 初始化所有已启用的 Mod
    /// 调用每个 Mod 的 OnLoad() 和 OnEnable()
    /// 
    /// 调用时机：程序启动时，在所有 Mod 加载后
    /// </summary>
    public void InitializeAllMods()
    {
        _modContext.Log(LogLevel.Info, "Initializing all mods...");

        foreach (var (modId, (plugin, metadata, isEnabled)) in _mods)
        {
            try
            {
                // 调用 OnLoad()（总是调用，即使禁用）
                plugin.OnLoad();
                _modContext.Log(LogLevel.Debug, $"OnLoad() called for: {metadata.Name}");

                // 如果启用，调用 OnEnable()
                if (isEnabled)
                {
                    plugin.OnEnable();
                    _modContext.Log(LogLevel.Debug, $"OnEnable() called for: {metadata.Name}");
                }
            }
            catch (Exception ex)
            {
                _modContext.Log(LogLevel.Error,
                    $"Error initializing mod '{metadata.Name}': {ex.Message}");
            }
        }

        InvalidateCache();
        _modContext.Log(LogLevel.Info, "Mod initialization completed");
    }

    /// <summary>
    /// 启用 Mod
    /// 调用 Mod 的 OnEnable()
    /// </summary>
    public bool EnableMod(string modId)
    {
        if (!_mods.TryGetValue(modId, out var modEntry))
        {
            _modContext.Log(LogLevel.Warn, $"Mod not found: {modId}");
            return false;
        }

        var (plugin, metadata, isEnabled) = modEntry;

        if (isEnabled)
        {
            _modContext.Log(LogLevel.Debug, $"Mod already enabled: {modId}");
            return true;
        }

        try
        {
            plugin.OnEnable();
            _mods[modId] = (plugin, metadata, true);
            InvalidateCache();
            SynchronizeNavigationPanel(plugin, isEnabled: true);

            _modContext.Log(LogLevel.Info, $"Mod enabled: {metadata.Name}");
            CommandProvidersChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _modContext.Log(LogLevel.Error,
                $"Error enabling mod '{metadata.Name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 禁用 Mod
    /// 调用 Mod 的 OnDisable()
    /// 注意：DLL 本身不卸载，仍在内存中
    /// </summary>
    public bool DisableMod(string modId)
    {
        if (!_mods.TryGetValue(modId, out var modEntry))
        {
            _modContext.Log(LogLevel.Warn, $"Mod not found: {modId}");
            return false;
        }

        var (plugin, metadata, isEnabled) = modEntry;

        if (!isEnabled)
        {
            _modContext.Log(LogLevel.Debug, $"Mod already disabled: {modId}");
            return true;
        }

        try
        {
            plugin.OnDisable();
            _mods[modId] = (plugin, metadata, false);
            InvalidateCache();
            SynchronizeNavigationPanel(plugin, isEnabled: false);

            _modContext.Log(LogLevel.Info, $"Mod disabled: {metadata.Name}");
            CommandProvidersChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _modContext.Log(LogLevel.Error,
                $"Error disabling mod '{metadata.Name}': {ex.Message}");
            return false;
        }
    }

    private static void SynchronizeNavigationPanel(IModPlugin plugin, bool isEnabled)
    {
        if (plugin is not INavigationPanelProvider provider)
            return;

        var registry = NavigationPanelRegistry.Instance;
        if (isEnabled)
        {
            if (!registry.IsRegistered(provider.PanelId))
                registry.Register(provider);
        }
        else
        {
            registry.Unregister(provider.PanelId);
        }
    }

    /// <summary>
    /// 分发群消息到所有启用的 Mod
    /// 
    /// 执行流程：
    /// 1. 按优先级顺序调用每个 Mod 的 OnGroupMessage()
    /// 2. 如果 Mod 返回 non-null 结果：
    ///    - 如果 Intercepted=true，记录并返回结果
    ///    - 如果 StopPropagation=true，停止继续分发
    /// 3. 如果所有 Mod 都返回 null，返回 null（继续传递给 MessageProcessor）
    /// </summary>
    public ModMessageResult? InvokeGroupMessage(long groupId, long userId, string content, bool isAted)
    {
        // 刷新缓存
        RefreshEnabledModsCache();

        _modContext.Log(LogLevel.Info,
            $"[ModEventBridge] 群消息分发开始: {_enabledModsCache.Count}个已启用Mod, Group={groupId}, User={userId}, Content={content}");
        _modContext.Log(LogLevel.Info,
            $"[ModBridge] InvokeGroupMessage bridgeId={GetObjectId(this)} mods={_enabledModsCache.Count} ids={string.Join(",", _enabledModsCache.Select(x => x.Metadata.Id))}");

        foreach (var (plugin, metadata) in _enabledModsCache)
        {
            try
            {
                //_modContext.Log(LogLevel.Info, $"[ModEventBridge] 调用Mod.OnGroupMessage: {metadata.Name}");
                var result = plugin.OnGroupMessage(groupId, userId, content, isAted);

                if (result != null)
                {
                    _modContext.Log(LogLevel.Info,
                        $"[ModEventBridge] Mod '{metadata.Name}' 返回结果: Intercepted={result.Intercepted}, Reply={result.Reply}");

                    // 如果这个 Mod 拦截了消息，返回结果
                    if (result.Intercepted)
                    {
                        return result;
                    }

                    // 如果要求停止传播，也返回结果
                    if (result.StopPropagation)
                    {
                        return result;
                    }

                    // 否则继续分发给下一个 Mod
                }
            }
            catch (Exception ex)
            {
                _modContext.Log(LogLevel.Error,
                    $"[ModEventBridge] Mod '{metadata.Name}' OnGroupMessage() 异常: {ex.Message}");
                // 异常不中断链，继续处理下一个 Mod
            }
        }

        // 所有 Mod 都没有处理，返回 null
        //_modContext.Log(LogLevel.Info, $"[ModEventBridge] 没有Mod处理此群消息");
        return null;
    }

    /// <summary>
    /// 分发私聊消息到所有启用的 Mod
    /// 
    /// 执行流程与 InvokeGroupMessage 相同
    /// </summary>
    public ModMessageResult? InvokePrivateMessage(long userId, string content)
    {
        RefreshEnabledModsCache();

        _modContext.Log(LogLevel.Info,
            $"[ModEventBridge] 私聊消息分发开始: {_enabledModsCache.Count}个已启用Mod, User={userId}, Content={content}");

        foreach (var (plugin, metadata) in _enabledModsCache)
        {
            try
            {
                //_modContext.Log(LogLevel.Info, $"[ModEventBridge] 调用Mod.OnPrivateMessage: {metadata.Name}");
                var result = plugin.OnPrivateMessage(userId, content);

                if (result != null)
                {
                    _modContext.Log(LogLevel.Info,
                        $"[ModEventBridge] Mod '{metadata.Name}' 返回结果: Intercepted={result.Intercepted}, Reply={result.Reply}");

                    if (result.Intercepted || result.StopPropagation)
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                _modContext.Log(LogLevel.Error,
                    $"[ModEventBridge] Mod '{metadata.Name}' OnPrivateMessage() 异常: {ex.Message}");
            }
        }

        //_modContext.Log(LogLevel.Info, $"[ModEventBridge] 没有Mod处理此私聊消息");
        return null;
    }

    /// <summary>
    /// 卸载所有 Mod
    /// 调用 OnUnload() 钩子并清理资源
    /// 
    /// 调用时机：程序关闭时
    /// </summary>
    public void UnloadAllMods()
    {
        _modContext.Log(LogLevel.Info, "[ModEventBridge] ========== UnloadAllMods START ==========");
        _modContext.Log(LogLevel.Info, $"[ModEventBridge] Unloading {_mods.Count} mods...");

        foreach (var (modId, (plugin, metadata, _)) in _mods)
        {
            try
            {
                _modContext.Log(LogLevel.Info, $"[ModEventBridge] Calling OnUnload() for mod: {metadata.Name}");
                plugin.OnUnload();
                _modContext.Log(LogLevel.Info, $"[ModEventBridge] ✓ OnUnload() completed for: {metadata.Name}");
            }
            catch (Exception ex)
            {
                _modContext.Log(LogLevel.Error,
                    $"[ModEventBridge] ✗ Error unloading mod '{metadata.Name}': {ex.Message}");
                // 不中断卸载流程
            }
        }

        _mods.Clear();
        InvalidateCache();
        CommandProvidersChanged?.Invoke();
        _modContext.Log(LogLevel.Info, "[ModEventBridge] ✓ All mods unloaded successfully");
        _modContext.Log(LogLevel.Info, "[ModEventBridge] ========== UnloadAllMods END ==========");
    }

    /// <summary>
    /// 获取所有已注册的 Mod（无论启用状态）
    /// </summary>
    public Dictionary<string, (IModPlugin Plugin, IModMetadata Metadata, bool IsEnabled)> GetAllMods()
    {
        return new Dictionary<string, (IModPlugin, IModMetadata, bool)>(_mods);
    }

    /// <summary>
    /// 获取特定 Mod 的状态
    /// </summary>
    public (IModPlugin? Plugin, IModMetadata? Metadata, bool IsEnabled)? GetModStatus(string modId)
    {
        if (_mods.TryGetValue(modId, out var entry))
        {
            return (entry.Plugin, entry.Metadata, entry.IsEnabled);
        }
        return null;
    }

    /// <summary>
    /// 获取已启用的 Mod 数量
    /// </summary>
    public int GetEnabledModCount()
    {
        return _mods.Values.Count(x => x.IsEnabled);
    }

    /// <summary>
    /// 获取总 Mod 数量
    /// </summary>
    public int GetTotalModCount()
    {
        return _mods.Count;
    }

    /// <summary>
    /// 收集所有已加载 Mod 提供的指令处理器
    /// 
    /// 此方法被 MessageProcessor 调用，用于收集并注册所有 Mod 的自定义指令
    /// 如果多个 Mod 注册相同的指令名，先注册的优先，后注册的会被忽略
    /// </summary>
    /// <returns>
    /// 指令字典，键为指令名（不含.前缀），值为对应的处理器委托
    /// 格式：{ "abot" → HandleAbotCommand, "custom" → HandleCustomCommand, ... }
    /// 处理器返回字符串作为回复内容，由MessageProcessor负责发送
    /// </returns>
    public Dictionary<string, Func<string, object, string?>> GetAllCommandHandlers()
    {
        var allHandlers = new Dictionary<string, Func<string, object, string?>>();
        
        foreach (var (modId, (plugin, metadata, isEnabled)) in _mods)
        {
            // 只收集来自已启用的 Mod 的指令处理器
            if (!isEnabled)
            {
                _modContext.Log(LogLevel.Debug, $"[GetAllCommandHandlers] 跳过禁用的Mod: {metadata.Name}");
                continue;
            }

            // 检查 Mod 是否实现了 ICommandProvider 接口
            if (plugin is ICommandProvider cmdProvider)
            {
                try
                {
                    var handlers = cmdProvider.GetCommandHandlers();
                    if (handlers != null)
                    {
                        foreach (var (cmdName, handler) in handlers)
                        {
                            if (allHandlers.ContainsKey(cmdName))
                            {
                                _modContext.Log(LogLevel.Warn, 
                                    $"[GetAllCommandHandlers] 指令冲突: '{cmdName}' 被多个Mod声明，仅保留第一个 (来自{metadata.Name})");
                                continue;
                            }
                            
                            allHandlers[cmdName] = handler;
                            _modContext.Log(LogLevel.Info, 
                                $"[GetAllCommandHandlers] 已注册Mod指令: .{cmdName} (来自Mod: {metadata.Name})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _modContext.Log(LogLevel.Error, 
                        $"[GetAllCommandHandlers] Mod '{metadata.Name}' 的 GetCommandHandlers() 抛出异常: {ex.Message}");
                }
            }
        }
        
        return allHandlers;
    }

    /// <summary>
    /// 获取所有实现了 ISubcommandProvider 的已启用 Mod
    /// 供父指令在 default/else 分支查询 Mod 注册的子指令
    /// </summary>
    public List<ISubcommandProvider> GetSubcommandProviders()
    {
        var providers = new List<ISubcommandProvider>();
        
        foreach (var (modId, (plugin, metadata, isEnabled)) in _mods)
        {
            if (!isEnabled) continue;

            if (plugin is ISubcommandProvider subProvider)
            {
                providers.Add(subProvider);
            }
        }
        
        return providers;
    }

    // ============ 私有方法 ============

    /// <summary>
    /// 刷新启用 Mod 的缓存
    /// </summary>
    private void RefreshEnabledModsCache()
    {
        if (_cacheValid)
            return;

        _enabledModsCache = _mods
            .Where(x => x.Value.IsEnabled)
            .OrderByDescending(x => x.Value.Metadata.Priority)
            .Select(x => (x.Value.Plugin, x.Value.Metadata))
            .ToList();

        _cacheValid = true;
    }

    /// <summary>
    /// 使缓存失效
    /// 在启用/禁用 Mod 时调用
    /// </summary>
    private void InvalidateCache()
    {
        _cacheValid = false;
    }

    private static string GetObjectId(object? instance)
    {
        return instance is null ? "null" : RuntimeHelpers.GetHashCode(instance).ToString();
    }

    /// <summary>
    /// 请求特定 Mod 执行自更新逻辑。
    /// 通过反射调用 Mod 内部公开的 CheckAndUpdateFromGitHubAsync（如果存在）。
    /// </summary>
    /// <param name="modId">mod.json 中配置的 Mod Id，例如 com.example.customreply</param>
    /// <returns>若成功触发调用（不代表更新一定成功）返回 true，否则返回 false。</returns>
    public async Task<bool> RequestModUpdateAsync(string modId)
    {
        if (!_mods.TryGetValue(modId, out var entry))
        {
            _modContext.Log(LogLevel.Warn, $"[ModEventBridge] RequestModUpdateAsync: Mod not found: {modId}");
            return false;
        }

        var plugin = entry.Plugin;
        try
        {
            var type = plugin.GetType();
            var method = type.GetMethod("CheckAndUpdateFromGitHubAsync", BindingFlags.Instance | BindingFlags.Public);

            if (method == null)
            {
                _modContext.Log(LogLevel.Warn,
                    $"[ModEventBridge] Mod '{modId}' does not expose CheckAndUpdateFromGitHubAsync, skip update request");
                return false;
            }

            _modContext.Log(LogLevel.Info,
                $"[ModEventBridge] Invoking CheckAndUpdateFromGitHubAsync on mod '{modId}'...");

            var resultObj = method.GetParameters().Length == 0
                ? method.Invoke(plugin, Array.Empty<object>())
                : method.Invoke(plugin, new object[] { "HumulusQ", "MDiceV2Public" });

            if (resultObj is Task task)
            {
                await task.ConfigureAwait(false);
            }

            _modContext.Log(LogLevel.Info,
                $"[ModEventBridge] CheckAndUpdateFromGitHubAsync finished for mod '{modId}'");
            return true;
        }
        catch (Exception ex)
        {
            _modContext.Log(LogLevel.Error,
                $"[ModEventBridge] Error while requesting update for mod '{modId}': {ex.Message}");
            return false;
        }
    }
}
