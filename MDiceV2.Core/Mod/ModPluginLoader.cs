using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using MDiceV2.Interfaces.Mod;

namespace MDiceV2.Core.Mod;

/// <summary>
/// Mod 插件加载器
/// 负责扫描、加载和管理所有 Mod
/// 
/// 工作流程：
/// 1. 扫描 data/mods 目录
/// 2. 读取和验证每个 mod.json
/// 3. 加载对应的 DLL 文件
/// 4. 通过反射创建 IModPlugin 实例
/// 5. 按优先级排序
/// 6. 注册到 ModEventBridge
/// </summary>
public class ModPluginLoader
{
    private static readonly AsyncLocal<string?> s_loadingModDirectory = new();
    private static readonly object s_hostLoadLock = new();
    private static readonly HashSet<string> s_hostLoadInProgress = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] SharedAssemblyNamePrefixes =
    {
        "Avalonia",
        "Semi.Avalonia",
        "ReactiveUI",
        "Splat",
        "SkiaSharp",
        "HarfBuzzSharp",
        "System.Data.SQLite",
        "Polly",
        "CommunityToolkit.Mvvm",
        "Grpc",
        "Google.Protobuf",
        "protobuf-net",
        "EntityFramework",
        "MDiceV2.Interfaces",
        "MDiceV2.Abstractions"
    };

    /// <summary>
    /// 当前宿主程序支持的 API 版本
    /// 格式：major.minor
    /// Mod 的 apiVersion 应与此兼容
    /// </summary>
    private const string SUPPORTED_API_VERSION = "1.0";

    /// <summary>
    /// Mods 根目录（通常为 data/mods）
    /// </summary>
    private readonly string _modsRootPath;

    /// <summary>
    /// 已加载的 Mod 实例字典
    /// Key: Mod ID，Value: (IModPlugin 实例, 元数据)
    /// </summary>
    private readonly Dictionary<string, (IModPlugin Plugin, IModMetadata Metadata)> _loadedMods = new();
    private readonly HashSet<string> _disabledModIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 加载失败的 Mod 列表（用于日志记录和调试）
    /// </summary>
    private readonly List<(string ModPath, string ErrorMessage)> _failedMods = new();

    /// <summary>
    /// ModContext 实现（由外部注入）
    /// </summary>
    private readonly IModContext _modContext;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="modsRootPath">Mods 根目录路径，通常为 data/mods</param>
    /// <param name="modContext">Mod 上下文实现</param>
    public ModPluginLoader(string modsRootPath, IModContext? modContext = null)
    {
        _modsRootPath = modsRootPath ?? throw new ArgumentNullException(nameof(modsRootPath));
        _modContext = modContext ?? throw new ArgumentNullException(nameof(modContext));

        // 确保目录存在
        Directory.CreateDirectory(_modsRootPath);
    }

    /// <summary>
    /// 安全记录日志（处理 null 的 ModContext）
    /// </summary>
    private void LogInfo(string message)
    {
        if (_modContext != null)
        {
            _modContext.Log(LogLevel.Info, message);
        }
        else
        {
            Console.WriteLine($"[ModPluginLoader] {message}");
        }
    }

    /// <summary>
    /// 安全记录错误日志
    /// </summary>
    private void LogError(string message)
    {
        if (_modContext != null)
        {
            _modContext.Log(LogLevel.Error, message);
        }
        else
        {
            Console.Error.WriteLine($"[ModPluginLoader ERROR] {message}");
        }
    }

    /// <summary>
    /// 安全记录调试日志
    /// </summary>
    private void LogDebug(string message)
    {
        if (_modContext != null)
        {
            _modContext.Log(LogLevel.Debug, message);
        }
        else
        {
            Console.WriteLine($"[ModPluginLoader DEBUG] {message}");
        }
    }

    /// <summary>
    /// 安全记录致命错误
    /// </summary>
    private void LogFatal(string message)
    {
        if (_modContext != null)
        {
            _modContext.Log(LogLevel.Fatal, message);
        }
        else
        {
            Console.Error.WriteLine($"[ModPluginLoader FATAL] {message}");
        }
    }

    /// <summary>
    /// 检查 Mod 的 API 版本是否与宿主程序兼容
    /// 
    /// 兼容性规则：
    /// - 主版本号必须相同（例如 1.0 和 1.2 兼容，但 1.0 和 2.0 不兼容）
    /// - 次版本号可以不同（允许向后兼容新功能）
    /// 
    /// 示例：
    /// SUPPORTED_API_VERSION = "1.0"
    /// - "1.0" ✓ 完全匹配
    /// - "1.1" ✓ 可兼容（Mod 要求更新的 API，但主版本相同）
    /// - "1.2" ✓ 可兼容
    /// - "0.9" ✗ 不兼容（Mod 要求的版本太低）
    /// - "2.0" ✗ 不兼容（主版本号不同）
    /// </summary>
    private static bool IsApiVersionCompatible(string modApiVersion)
    {
        try
        {
            // 解析版本号
            if (!Version.TryParse(modApiVersion, out var modVersion))
            {
                // 如果 Mod 版本格式无效，记录警告但允许加载（向后兼容）
                Console.WriteLine($"[ModPluginLoader] WARNING: Invalid API version format '{modApiVersion}', allowing anyway");
                return true;
            }

            if (!Version.TryParse(SUPPORTED_API_VERSION, out var supportedVersion))
            {
                // 这不应该发生，但如果发生了，允许加载
                Console.WriteLine($"[ModPluginLoader] WARNING: Invalid supported API version format '{SUPPORTED_API_VERSION}'");
                return true;
            }

            // 检查主版本号是否相同
            if (modVersion.Major != supportedVersion.Major)
            {
                return false;
            }

            // 主版本号相同，即为兼容（允许次版本号不同，实现向后兼容）
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModPluginLoader] ERROR checking API version: {ex.Message}, allowing anyway");
            return true; // 出错时允许加载，不中断启动流程
        }
    }

    /// <summary>
    /// 扫描并加载所有 Mod
    /// 
    /// 返回按优先级排序（从高到低）的 Mod 列表
    /// </summary>
    public List<(IModPlugin Plugin, IModMetadata Metadata)> LoadAllMods()
    {
        _loadedMods.Clear();
        _disabledModIds.Clear();
        _failedMods.Clear();

        try
        {
            Console.WriteLine("[ModPluginLoader] >>> ========== LoadAllMods START ==========");
            LogInfo($"Starting to load mods from: {_modsRootPath}");

            // 注册程序集解析事件，确保 Mod 的依赖能从宿主已加载的程序集中解析
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            Console.WriteLine($"[ModPluginLoader] >>> Scanning mods directory: {_modsRootPath}");

            // 扫描 mods 目录下的所有子文件夹
            var modDirectories = Directory.GetDirectories(_modsRootPath);
            Console.WriteLine($"[ModPluginLoader] >>> Found {modDirectories.Length} mod folders");

            foreach (var modDir in modDirectories)
            {
                var modFolderName = Path.GetFileName(modDir);
                try
                {
                    Console.WriteLine($"[ModPluginLoader] >>> Loading mod from: {modFolderName}");
                    LoadModFromDirectory(modDir);
                    Console.WriteLine($"[ModPluginLoader] >>> ✓ Successfully loaded: {modFolderName}");
                }
                catch (Exception ex)
                {
                    var innerMsg = ex.InnerException != null ? $"\n  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                    Console.WriteLine($"[ModPluginLoader] >>> ✗ FAILED to load {modFolderName}: {ex.Message}{innerMsg}");
                    var modName = Path.GetFileName(modDir);
                    LogError($"Failed to load mod '{modName}': {ex.Message}{innerMsg}");
                    _failedMods.Add((modDir, $"{ex.Message}{innerMsg}"));
                }
            }

            // 按优先级排序（高优先级优先）
            var sortedMods = _loadedMods.Values
                .OrderByDescending(x => x.Metadata.Priority)
                .ToList();

            LogInfo($"Successfully loaded {sortedMods.Count} mods, {_failedMods.Count} failed");

            return sortedMods;
        }
        catch (Exception ex)
        {
            LogFatal($"Critical error loading mods: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 从指定目录加载单个 Mod
    /// </summary>
    private void LoadModFromDirectory(string modDirectory)
    {
        Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] Starting for: {modDirectory}");
        
        // 1. 读取 mod.json
        var modJsonPath = Path.Combine(modDirectory, "mod.json");
        if (!File.Exists(modJsonPath))
        {
            Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] ERROR: mod.json not found at {modJsonPath}");
            throw new FileNotFoundException($"mod.json not found in {modDirectory}");
        }

        // 2. 解析元数据
        var metadata = ParseModMetadata(modJsonPath);
        Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] Loaded metadata: ID={metadata.Id}, Name={metadata.Name}");

        // 检查 ID 唯一性
        if (_loadedMods.ContainsKey(metadata.Id))
        {
            Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] ERROR: Duplicate mod ID: {metadata.Id}");
            throw new InvalidOperationException($"Duplicate mod ID: {metadata.Id}");
        }

        // 【已禁用】API 版本检查 - 允许任何版本的 Mod 加载
        // if (!IsApiVersionCompatible(metadata.ApiVersion))
        // {
        //     var errorMsg = $"Mod '{metadata.Name}' (ID: {metadata.Id}) requires API version {metadata.ApiVersion}, " +
        //                   $"but this system supports {SUPPORTED_API_VERSION}. Mod will not be loaded.";
        //     Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] ERROR: {errorMsg}");
        //     LogError(errorMsg);
        //     _failedMods.Add((modDirectory, errorMsg));
        //     throw new InvalidOperationException(errorMsg);
        // }

        // 3. Retain disabled modules in the runtime so Mod Manager can enable
        // them immediately, without a process restart.
        var disabledMarkerPath = Path.Combine(modDirectory, ".disabled");
        if (File.Exists(disabledMarkerPath))
        {
            Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] Loading disabled mod for runtime toggle: {metadata.Name}");
            _disabledModIds.Add(metadata.Id);
        }

        // 4. 加载 DLL 文件
        var dllPath = Path.Combine(modDirectory, metadata.DllFileName);
        if (!File.Exists(dllPath))
        {
            Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] ERROR: DLL not found at {dllPath}");
            throw new FileNotFoundException($"DLL file not found: {metadata.DllFileName}");
        }
        
        Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] Loading DLL: {dllPath}");

        // TODO: Introduce a ModLoadContext : AssemblyLoadContext that uses
        // AssemblyDependencyResolver for plugin deps.json / runtimes / native libraries,
        // while explicitly sharing MDiceV2.Interfaces and MDiceV2.Abstractions to avoid
        // type identity splits. Keep the current LoadFrom path unchanged for this update.
        Assembly assembly;
        var previousLoadingModDirectory = s_loadingModDirectory.Value;
        s_loadingModDirectory.Value = modDirectory;
        try
        {
            assembly = Assembly.LoadFrom(dllPath);
        }
        finally
        {
            s_loadingModDirectory.Value = previousLoadingModDirectory;
        }
        Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] Assembly loaded successfully");
        LogDebug($"Loaded assembly: {Path.GetFileName(dllPath)}");

        // 5. 查找并创建 IModPlugin 实现
        Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] Creating plugin instance, ClassName: {metadata.PluginClassName}");
        var pluginInstance = CreatePluginInstance(assembly, metadata, modDirectory);
        Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] Plugin instance created successfully");

        // 6. 存储 Mod
        _loadedMods[metadata.Id] = (pluginInstance, metadata);
        Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] Mod stored in _loadedMods. Total mods: {_loadedMods.Count}");

        LogInfo($"Loaded mod: {metadata.Name} v{metadata.Version} (Priority: {metadata.Priority}, API: {metadata.ApiVersion})");
        Console.WriteLine($"[ModPluginLoader] >>> [LoadModFromDirectory] ✓ SUCCESS");
    }

    /// <summary>
    /// 解析 mod.json 文件并返回元数据
    /// </summary>
    private IModMetadata ParseModMetadata(string modJsonPath)
    {
        try
        {
            var json = File.ReadAllText(modJsonPath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new ModMetadata
            {
                Id = GetJsonString(root, "id") ?? throw new InvalidOperationException("Missing 'id' field"),
                Name = GetJsonString(root, "name") ?? throw new InvalidOperationException("Missing 'name' field"),
                Version = GetJsonString(root, "version") ?? "1.0.0",
                Author = GetJsonString(root, "author") ?? "Unknown",
                Description = GetJsonString(root, "description") ?? "",
                DllFileName = GetJsonString(root, "dllFileName") ?? throw new InvalidOperationException("Missing 'dllFileName' field"),
                PluginClassName = GetJsonString(root, "pluginClassName"),
                Priority = GetJsonInt(root, "priority") ?? 100,
                ModType = GetJsonString(root, "modType") ?? "dll",
                SupportHotReload = GetJsonBool(root, "supportHotReload") ?? false,
                ApiVersion = GetJsonString(root, "apiVersion") ?? "1.0"
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid mod.json format: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 从程序集中创建 IModPlugin 实现的实例
    /// </summary>
    private IModPlugin CreatePluginInstance(Assembly assembly, IModMetadata metadata, string modDirectory)
    {
        Type? pluginType = null;

        if (!string.IsNullOrEmpty(metadata.PluginClassName))
        {
            // 直接加载指定的类
            pluginType = assembly.GetType(metadata.PluginClassName);
            if (pluginType == null)
                throw new TypeLoadException($"Cannot find class: {metadata.PluginClassName}");
        }
        else
        {
            // 自动查找实现 IModPlugin 的类
            pluginType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IModPlugin).IsAssignableFrom(t) && !t.IsInterface);

            if (pluginType == null)
                throw new TypeLoadException("No IModPlugin implementation found in assembly");
        }

        // 查找构造函数：ctor(IModContext)
        var constructor = pluginType.GetConstructor(new[] { typeof(IModContext) });
        if (constructor == null)
            throw new MissingMethodException($"No constructor found for {pluginType.Name}(IModContext)");

        // 创建实例
        var instance = (IModPlugin?)constructor.Invoke(new object[] { _modContext })
            ?? throw new InvalidOperationException("Failed to create plugin instance");

        return instance;
    }

    /// <summary>
    /// 获取已加载的 Mod（按优先级排序）
    /// </summary>
    public List<(IModPlugin Plugin, IModMetadata Metadata)> GetLoadedMods()
    {
        return _loadedMods.Values
            .OrderByDescending(x => x.Metadata.Priority)
            .ToList();
    }

    /// <summary>
    /// 通过 ID 获取 Mod
    /// </summary>
    public (IModPlugin Plugin, IModMetadata Metadata)? GetModById(string modId)
    {
        _loadedMods.TryGetValue(modId, out var result);
        return result;
    }

    /// <summary>
    /// 检查 Mod 是否已加载
    /// </summary>
    public bool IsModLoaded(string modId)
    {
        return _loadedMods.ContainsKey(modId);
    }

    /// <summary>Returns whether the mod was marked disabled when it was discovered.</summary>
    public bool IsModDisabled(string modId)
    {
        return _disabledModIds.Contains(modId);
    }

    /// <summary>
    /// 获取加载失败的 Mod 列表
    /// </summary>
    public List<(string ModPath, string ErrorMessage)> GetFailedMods()
    {
        return _failedMods;
    }

    // ============ 辅助方法 ============

    /// <summary>
    /// 程序集解析事件处理
    /// 当 Mod 的 DLL 依赖（如 MDiceV2.Interfaces）不在 Mod 目录中时，
    /// 从宿主已加载的程序集中查找并返回，避免类型解析失败
    /// </summary>
    private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name);
        var simpleName = requestedName.Name;
        if (string.IsNullOrWhiteSpace(simpleName))
        {
            Console.WriteLine("[ModPluginLoader] >>> AssemblyResolve: failed (empty assembly name)");
            return null;
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == simpleName)
            {
                Console.WriteLine($"[ModPluginLoader] >>> AssemblyResolve: resolved from already-loaded host assembly '{simpleName}'");
                return asm;
            }
        }

        if (ShouldPreferHostAssembly(simpleName) &&
            TryLoadFromHostDefaultContext(requestedName, simpleName, out var hostAssembly))
        {
            Console.WriteLine($"[ModPluginLoader] >>> AssemblyResolve: resolved '{simpleName}' from host default load context");
            return hostAssembly;
        }

        foreach (var (label, directory) in GetHostProbeDirectories())
        {
            if (TryLoadAssemblyFromDirectory(directory, simpleName, out var probedAssembly))
            {
                Console.WriteLine($"[ModPluginLoader] >>> AssemblyResolve: resolved from {label} '{simpleName}' -> {directory}");
                return probedAssembly;
            }
        }

        var modDirectory = GetRequestingModDirectory(args);
        if (!string.IsNullOrWhiteSpace(modDirectory) &&
            TryLoadAssemblyFromDirectory(modDirectory, simpleName, out var modAssembly))
        {
            Console.WriteLine($"[ModPluginLoader] >>> AssemblyResolve: resolved from mod directory '{simpleName}' -> {modDirectory}");
            return modAssembly;
        }

        Console.WriteLine($"[ModPluginLoader] >>> AssemblyResolve: failed '{simpleName}'");
        return null;
    }

    private static bool ShouldPreferHostAssembly(string simpleName)
    {
        return SharedAssemblyNamePrefixes.Any(prefix =>
            simpleName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            simpleName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) ||
            simpleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryLoadFromHostDefaultContext(AssemblyName requestedName, string simpleName, out Assembly? assembly)
    {
        assembly = null;

        lock (s_hostLoadLock)
        {
            if (!s_hostLoadInProgress.Add(simpleName))
            {
                return false;
            }
        }

        try
        {
            assembly = Assembly.Load(requestedName);
            return assembly != null;
        }
        catch
        {
            return false;
        }
        finally
        {
            lock (s_hostLoadLock)
            {
                s_hostLoadInProgress.Remove(simpleName);
            }
        }
    }

    private static IEnumerable<(string Label, string Directory)> GetHostProbeDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (var probe in EnumerateProbeDirectory("app base directory", baseDirectory, seen))
        {
            yield return probe;
        }

        var appRoot = GetApplicationRootDirectory(baseDirectory);
        foreach (var probe in EnumerateProbeDirectory("application root directory", appRoot, seen))
        {
            yield return probe;
        }

        foreach (var probe in EnumerateProbeDirectory("core directory", Path.Combine(appRoot, "Core"), seen))
        {
            yield return probe;
        }
    }

    private static IEnumerable<(string Label, string Directory)> EnumerateProbeDirectory(
        string label,
        string? directory,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            yield break;
        }

        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory) || !seen.Add(fullDirectory))
        {
            yield break;
        }

        yield return (label, fullDirectory);
    }

    private static string GetApplicationRootDirectory(string baseDirectory)
    {
        var trimmedBaseDirectory = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(trimmedBaseDirectory).Equals("Core", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(trimmedBaseDirectory)?.FullName ?? trimmedBaseDirectory;
        }

        return trimmedBaseDirectory;
    }

    private static bool TryLoadAssemblyFromDirectory(string directory, string simpleName, out Assembly? assembly)
    {
        assembly = null;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        var candidatePath = Path.Combine(directory, simpleName + ".dll");
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        try
        {
            assembly = Assembly.LoadFrom(candidatePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? GetRequestingModDirectory(ResolveEventArgs args)
    {
        var requestingAssemblyDirectory = GetAssemblyDirectory(args.RequestingAssembly);
        if (IsDirectoryUnderModsRoot(requestingAssemblyDirectory))
        {
            return requestingAssemblyDirectory;
        }

        if (IsDirectoryUnderModsRoot(s_loadingModDirectory.Value))
        {
            return s_loadingModDirectory.Value;
        }

        return null;
    }

    private static string? GetAssemblyDirectory(Assembly? assembly)
    {
        try
        {
            var location = assembly?.Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            return Path.GetDirectoryName(location);
        }
        catch
        {
            return null;
        }
    }

    private bool IsDirectoryUnderModsRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(_modsRootPath))
        {
            return false;
        }

        var directoryFullPath = Path.GetFullPath(directory);
        var modsRootFullPath = Path.GetFullPath(_modsRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return directoryFullPath.Equals(modsRootFullPath, StringComparison.OrdinalIgnoreCase) ||
               directoryFullPath.StartsWith(modsRootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从 JSON 中获取字符串值
    /// </summary>
    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    /// <summary>
    /// 从 JSON 中获取整数值
    /// </summary>
    private static int? GetJsonInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt32();
        }
        return null;
    }

    /// <summary>
    /// 从 JSON 中获取布尔值
    /// </summary>
    private static bool? GetJsonBool(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        if (element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.False)
        {
            return false;
        }
        return null;
    }
}
