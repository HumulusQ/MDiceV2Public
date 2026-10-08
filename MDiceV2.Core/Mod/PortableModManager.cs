using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;

namespace MDiceV2.Core.Mod;

public sealed record ManagedModInfo(
    string Id,
    string CommandName,
    string Name,
    string Version,
    string Description,
    bool IsPortable,
    bool IsEnabled,
    bool SupportsReload,
    string? Error = null);

/// <summary>Owns portable package storage, hot reload, and the shared chat-management surface.</summary>
public sealed class PortableModManager : IDisposable
{
    public const long MaxPackageBytes = 100L * 1024 * 1024;
    public const long MaxExpandedBytes = 500L * 1024 * 1024;
    public const int MaxEntries = 4096;
    public const double MaxCompressionRatio = 100d;

    private static readonly Regex CommandNamePattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private readonly string _modsRoot;
    private readonly string _portableRoot;
    private readonly ModEventBridge _bridge;
    private readonly IModContext _context;
    private readonly MessageDistribution _distribution;
    private readonly MessageProcessor _processor;
    private readonly OneBotFileDownloadService _downloader;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PortableRuntime> _portableById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _recentFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<ManagedModInfo> _startupErrors = new();
    private bool _disposed;

    public PortableModManager(
        string modsRoot,
        ModEventBridge bridge,
        IModContext context,
        MessageDistribution distribution,
        MessageProcessor processor)
    {
        _modsRoot = Path.GetFullPath(modsRoot);
        _portableRoot = Path.Combine(_modsRoot, ".portable");
        _bridge = bridge;
        _context = context;
        _distribution = distribution;
        _processor = processor;
        _downloader = new OneBotFileDownloadService(() => _distribution.WSconnection, Log.Normal, MaxPackageBytes);
        Directory.CreateDirectory(_portableRoot);
        _distribution.OnFileMessage += OnFileMessage;
    }

    public void LoadInstalledPackages()
    {
        foreach (var statePath in Directory.EnumerateFiles(_portableRoot, "state.json", SearchOption.AllDirectories))
        {
            try
            {
                var state = JsonSerializer.Deserialize<PortableState>(File.ReadAllText(statePath));
                if (state is null || string.IsNullOrWhiteSpace(state.CurrentHash))
                    continue;
                var modRoot = Path.GetDirectoryName(statePath)!;
                var contentPath = Path.Combine(modRoot, "versions", state.CurrentHash, "content");
                var loaded = CreateRuntime(contentPath, state.CurrentHash, state.Enabled);
                EnsureNoCrossTypeCollision(loaded.Metadata, replacingPortableId: null);
                ActivateRuntime(loaded, state.Enabled);
                _portableById[loaded.Metadata.Id] = loaded;
                Log.Normal($"[PortableMod] startup loaded {loaded.Metadata.CommandName} v{loaded.Metadata.Version}");
            }
            catch (Exception ex)
            {
                Log.Error($"[PortableMod] failed to restore '{statePath}': {ex.Message}");
            }
        }
    }

    public IReadOnlyList<ManagedModInfo> GetAllManagedMods()
    {
        return _bridge.GetAllMods().Values
            .Select(entry => new ManagedModInfo(
                entry.Metadata.Id,
                EffectiveCommandName(entry.Metadata),
                entry.Metadata.Name,
                entry.Metadata.Version,
                entry.Metadata.Description,
                IsPortable(entry.Metadata),
                entry.IsEnabled,
                IsPortable(entry.Metadata) || entry.Metadata.SupportHotReload))
            .Concat(_startupErrors)
            .OrderBy(info => info.CommandName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void AddStartupFailure(string name, string error)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        _startupErrors.Add(new ManagedModInfo(safeName, safeName, safeName, "?", string.Empty, false, false, false, error));
    }

    public ManagedModInfo? FindByCommandName(string commandName)
    {
        return GetAllManagedMods().FirstOrDefault(
            info => string.Equals(info.CommandName, commandName, StringComparison.OrdinalIgnoreCase));
    }

    public string GetMenu(string commandName)
    {
        var match = FindEntry(commandName);
        if (match is null)
            return $"未找到 Mod：{commandName}";
        var (plugin, metadata, enabled) = match.Value;
        var lines = new List<string>
        {
            $"{metadata.Name} ({EffectiveCommandName(metadata)}) v{metadata.Version}",
            $"作者：{metadata.Author}",
            $"状态：{(enabled ? "开启" : "关闭")} / 类型：{(IsPortable(metadata) ? "便携" : "普通")}",
            string.IsNullOrWhiteSpace(metadata.Description) ? plugin.Description : metadata.Description
        };
        if (plugin is IModManagementCommandProvider provider)
        {
            var commands = SafeGetManagementCommands(provider);
            if (commands.Count > 0)
            {
                lines.Add("管理指令：");
                lines.AddRange(commands.Select(command => $"- {command.Name}: {command.Description}"));
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    public string ExecuteManagementCommand(string commandName, string subcommand, string args, object message)
    {
        var match = FindEntry(commandName);
        if (match is null)
            return $"未找到 Mod：{commandName}";
        if (!match.Value.IsEnabled)
            return $"Mod '{commandName}' 当前已关闭。";
        if (match.Value.Plugin is not IModManagementCommandProvider provider)
            return $"Mod '{commandName}' 没有注册管理指令。";
        var command = SafeGetManagementCommands(provider).FirstOrDefault(
            item => string.Equals(item.Name, subcommand, StringComparison.OrdinalIgnoreCase));
        if (command is null)
            return $"Mod '{commandName}' 没有注册管理指令 '{subcommand}'。";
        return command.Handler(args, message) ?? string.Empty;
    }

    public async Task<(bool Success, string Message)> SetEnabledAsync(string commandName, bool enabled)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var match = FindEntry(commandName);
            if (match is null)
                return (false, $"未找到 Mod：{commandName}");
            var status = match.Value;
            if (status.IsEnabled == enabled)
                return (true, $"Mod '{commandName}' 已经{(enabled ? "开启" : "关闭")}。 ");

            var portable = IsPortable(status.Metadata);
            var marker = portable
                ? GetPortableDisabledMarker(status.Metadata.CommandName)
                : FindStandardDisabledMarker(status.Metadata.Id);
            var oldMarker = marker is not null && File.Exists(marker);
            try
            {
                if (marker is not null)
                    SetDisabledMarker(marker, !enabled);
                var success = enabled ? _bridge.EnableMod(status.Metadata.Id) : _bridge.DisableMod(status.Metadata.Id);
                if (!success)
                {
                    if (marker is not null) SetDisabledMarker(marker, oldMarker);
                    return (false, $"Mod '{commandName}' {(enabled ? "开启" : "关闭")}失败，状态已回滚。");
                }
                if (portable && _portableById.TryGetValue(status.Metadata.Id, out var runtime))
                {
                    runtime.Enabled = enabled;
                    SaveState(runtime);
                }
                return (true, $"Mod '{commandName}' 已{(enabled ? "开启" : "关闭")}。 ");
            }
            catch (Exception ex)
            {
                var current = _bridge.GetModStatus(status.Metadata.Id);
                if (current is not null && current.Value.IsEnabled != status.IsEnabled)
                {
                    if (status.IsEnabled) _bridge.EnableMod(status.Metadata.Id);
                    else _bridge.DisableMod(status.Metadata.Id);
                }
                if (portable && _portableById.TryGetValue(status.Metadata.Id, out var runtime))
                    runtime.Enabled = status.IsEnabled;
                if (marker is not null) SetDisabledMarker(marker, oldMarker);
                return (false, $"操作失败：{ex.Message}");
            }
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<(bool Success, string Message)> ReloadAsync(string commandName)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var match = FindEntry(commandName);
            if (match is null)
                return (false, $"未找到 Mod：{commandName}");
            if (!IsPortable(match.Value.Metadata))
                return (false, $"普通 Mod '{commandName}' 不支持宿主级热重载。");
            if (!_portableById.TryGetValue(match.Value.Metadata.Id, out var oldRuntime))
                return (false, "便携 Mod 运行时记录不存在。");
            return ReplaceRuntime(oldRuntime, oldRuntime.ContentPath, oldRuntime.VersionHash, oldRuntime.Enabled, commitState: false);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<(bool Success, string Message)> InstallPackageAsync(string packagePath)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        string? stagedVersionRoot = null;
        try
        {
            if (!packagePath.EndsWith(".mmod", StringComparison.OrdinalIgnoreCase))
                return (false, "文件扩展名不是 .mmod。");
            var info = new FileInfo(packagePath);
            if (!info.Exists || info.Length > MaxPackageBytes)
                return (false, "便携 Mod 包不存在或超过 100 MiB。");

            string hash;
            await using (var packageStream = File.OpenRead(packagePath))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream)).ToLowerInvariant();
            var manifest = ValidateArchiveAndReadManifest(packagePath);
            var oldByName = _portableById.Values.FirstOrDefault(runtime =>
                string.Equals(runtime.Metadata.CommandName, manifest.CommandName, StringComparison.OrdinalIgnoreCase));
            var oldById = _portableById.Values.FirstOrDefault(runtime =>
                string.Equals(runtime.Metadata.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
            if (oldByName is not null && !string.Equals(oldByName.Metadata.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                return (false, "同短名便携 Mod 的 ID 不一致，拒绝覆盖。");
            if (oldById is not null && !string.Equals(oldById.Metadata.CommandName, manifest.CommandName, StringComparison.OrdinalIgnoreCase))
                return (false, "同 ID 便携 Mod 的短名不一致，拒绝改名覆盖。");
            var old = oldByName ?? oldById;
            EnsureNoCrossTypeCollision(manifest, old?.Metadata.Id);
            var modRoot = Path.Combine(_portableRoot, manifest.CommandName);
            stagedVersionRoot = Path.Combine(modRoot, "versions", hash);
            var contentPath = Path.Combine(stagedVersionRoot, "content");
            if (!Directory.Exists(contentPath))
            {
                Directory.CreateDirectory(contentPath);
                ExtractValidated(packagePath, contentPath);
            }
            Directory.CreateDirectory(stagedVersionRoot);
            File.Copy(packagePath, Path.Combine(stagedVersionRoot, "package.mmod"), overwrite: true);

            if (old is null)
            {
                var loaded = CreateRuntime(contentPath, hash, enabled: true);
                ActivateRuntime(loaded, enabled: true);
                _portableById[loaded.Metadata.Id] = loaded;
                SaveState(loaded);
                return (true, $"已安装并开启便携 Mod {loaded.Metadata.Name} ({loaded.Metadata.CommandName}) v{loaded.Metadata.Version}。");
            }

            return ReplaceRuntime(old, contentPath, hash, old.Enabled, commitState: true);
        }
        catch (Exception ex)
        {
            Log.Error($"[PortableMod] install failed: {ex}");
            return (false, $"安装失败：{ex.Message}");
        }
        finally { _mutationGate.Release(); }
    }

    private (bool Success, string Message) ReplaceRuntime(
        PortableRuntime old,
        string newContentPath,
        string newHash,
        bool enabled,
        bool commitState)
    {
        PortableRuntime? candidate = null;
        var oldDetached = false;
        try
        {
            // Construct before stopping the old instance; OnLoad is intentionally delayed.
            candidate = CreateRuntime(newContentPath, newHash, enabled);
            if (!string.Equals(candidate.Metadata.Id, old.Metadata.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Metadata.CommandName, old.Metadata.CommandName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新包身份与已安装便携 Mod 不一致。");

            _bridge.UnregisterMod(old.Metadata.Id, invokeLifecycle: true);
            oldDetached = true;
            ReleaseContext(old);
            ActivateRuntime(candidate, enabled);
            _portableById[candidate.Metadata.Id] = candidate;
            if (commitState) SaveState(candidate);
            return (true, $"便携 Mod '{candidate.Metadata.CommandName}' 已热重载至 v{candidate.Metadata.Version}，状态保持为{(enabled ? "开启" : "关闭")}。");
        }
        catch (Exception ex)
        {
            if (candidate is not null)
            {
                _bridge.UnregisterMod(candidate.Metadata.Id, invokeLifecycle: true);
                ReleaseContext(candidate);
            }
            if (!oldDetached)
                return (false, $"热更新预校验失败：{ex.Message}；旧版本仍在运行。");
            try
            {
                var rollback = CreateRuntime(old.ContentPath, old.VersionHash, enabled);
                ActivateRuntime(rollback, enabled);
                _portableById[rollback.Metadata.Id] = rollback;
                SaveState(rollback);
                return (false, $"热更新失败：{ex.Message}；已恢复旧版本 v{rollback.Metadata.Version}。");
            }
            catch (Exception rollbackEx)
            {
                return (false, $"热更新失败：{ex.Message}；旧版本恢复也失败：{rollbackEx.Message}");
            }
        }
    }

    private PortableRuntime CreateRuntime(string contentPath, string versionHash, bool enabled)
    {
        var metadata = ReadManifest(Path.Combine(contentPath, "mod.json"), strictPortable: true);
        var dllPath = Path.GetFullPath(Path.Combine(contentPath, metadata.DllFileName));
        EnsureUnderRoot(contentPath, dllPath);
        if (!File.Exists(dllPath)) throw new FileNotFoundException("入口 DLL 不存在。", metadata.DllFileName);
        var loadContext = new PortableModLoadContext(dllPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(dllPath);
            var pluginType = !string.IsNullOrWhiteSpace(metadata.PluginClassName)
                ? assembly.GetType(metadata.PluginClassName!, throwOnError: true)
                : assembly.GetTypes().FirstOrDefault(type => typeof(IModPlugin).IsAssignableFrom(type) && !type.IsAbstract);
            if (pluginType is null) throw new TypeLoadException("包内找不到 IModPlugin 实现。");
            var constructor = pluginType.GetConstructor(new[] { typeof(IModContext) })
                ?? throw new MissingMethodException($"{pluginType.FullName} 缺少构造函数 (IModContext)。");
            var plugin = (IModPlugin?)constructor.Invoke(new object[] { _context })
                ?? throw new InvalidOperationException("无法创建 Mod 实例。");
            if (!string.Equals(plugin.ModId, metadata.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("插件 ModId 与清单 id 不一致。");
            if (plugin is IPortableModContextReceiver contextReceiver)
            {
                var dataDirectory = Path.Combine(_portableRoot, metadata.CommandName, "data");
                Directory.CreateDirectory(dataDirectory);
                contextReceiver.SetPortableModContext(new PortableModContext(dataDirectory));
            }
            return new PortableRuntime(plugin, metadata, loadContext, contentPath, versionHash, enabled);
        }
        catch { loadContext.Unload(); throw; }
    }

    private void ActivateRuntime(PortableRuntime runtime, bool enabled, bool register = true)
    {
        var loaded = false;
        var registered = false;
        try
        {
            runtime.Plugin!.OnLoad();
            loaded = true;
            if (register)
            {
                _bridge.RegisterMod(runtime.Plugin, runtime.Metadata, enabled);
                registered = true;
            }
            if (enabled) runtime.Plugin.OnEnable();
        }
        catch
        {
            if (registered)
                _bridge.UnregisterMod(runtime.Metadata.Id, invokeLifecycle: true);
            else if (loaded)
            {
                try { runtime.Plugin?.OnUnload(); } catch { }
            }
            ReleaseContext(runtime);
            throw;
        }
    }

    private void OnFileMessage(OneBotFileInfo file)
    {
        if (_disposed || file.GroupId <= 0 || !file.FileName.EndsWith(".mmod", StringComparison.OrdinalIgnoreCase)) return;
        var key = $"{file.GroupId}:{file.UserId}:{file.FileId}:{file.FileName}";
        var now = DateTime.UtcNow;
        foreach (var item in _recentFiles.Where(item => now - item.Value > TimeSpan.FromMinutes(2)).ToArray())
            _recentFiles.TryRemove(item.Key, out _);
        if (!_recentFiles.TryAdd(key, now)) return;
        _ = HandleUploadedFileAsync(file);
    }

    private async Task HandleUploadedFileAsync(OneBotFileInfo file)
    {
        if (!_processor.IsDiceAdministrator(file.UserId))
        {
            SendGroup(file.GroupId, "拒绝安装 .mmod：上传者不是骰子管理员。");
            return;
        }
        if (file.FileSize > MaxPackageBytes)
        {
            SendGroup(file.GroupId, "拒绝安装 .mmod：文件超过 100 MiB。");
            return;
        }

        SendGroup(file.GroupId, "检测到骰管上传的 .mmod，正在下载并安全校验。注意：未签名 Mod 将以骰娘进程权限运行。");
        var download = await _downloader.DownloadAsync(file).ConfigureAwait(false);
        if (!download.Success || string.IsNullOrWhiteSpace(download.LocalPath))
        {
            SendGroup(file.GroupId, $".mmod 下载失败：{download.ErrorMessage}");
            return;
        }
        try
        {
            var result = await InstallPackageAsync(download.LocalPath).ConfigureAwait(false);
            SendGroup(file.GroupId, result.Message);
        }
        finally
        {
            try { File.Delete(download.LocalPath); } catch { }
        }
    }

    private ModMetadata ValidateArchiveAndReadManifest(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException($"包内条目超过 {MaxEntries} 个。");
        long expanded = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? manifest = null;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            ValidateEntryName(normalized);
            if (!paths.Add(normalized)) throw new InvalidDataException($"包内存在重复路径：{normalized}");
            if ((((uint)entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"不允许符号链接：{normalized}");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("解压总大小超过 500 MiB。");
            if (entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > MaxCompressionRatio))
                throw new InvalidDataException($"条目压缩比超过限制：{normalized}");
            if (string.Equals(normalized, "mod.json", StringComparison.Ordinal)) manifest = entry;
        }
        if (manifest is null) throw new InvalidDataException("包根目录缺少大小写精确的 mod.json。");
        using var stream = manifest.Open();
        using var reader = new StreamReader(stream);
        return ParseManifest(reader.ReadToEnd(), strictPortable: true);
    }

    private static void ExtractValidated(string packagePath, string destination)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            ValidateEntryName(normalized);
            var target = Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
            EnsureUnderRoot(destination, target);
            if (normalized.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(output);
        }
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.StartsWith('\\') || Path.IsPathRooted(name))
            throw new InvalidDataException($"非法包路径：{name}");
        if (name.Split('/').Any(part => part is ".." or "."))
            throw new InvalidDataException($"包路径包含目录穿越：{name}");
        if (name.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"包路径含 Windows 非法字符：{name}");
    }

    private static void EnsureUnderRoot(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路径逃逸出便携 Mod 目录。");
    }

    private static ModMetadata ReadManifest(string path, bool strictPortable) =>
        ParseManifest(File.ReadAllText(path), strictPortable);

    private static ModMetadata ParseManifest(string json, bool strictPortable)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Required(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new InvalidDataException($"mod.json 缺少非空字段 '{name}'。");
        var metadata = new ModMetadata
        {
            Id = Required("id"), CommandName = Required("commandName"), Name = Required("name"),
            Version = Required("version"),
            Author = root.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(author.GetString()) ? author.GetString()!.Trim() : "Unknown",
            Description = Required("description"),
            DllFileName = Required("dllFileName"), PluginClassName = strictPortable ? Required("pluginClassName") : root.TryGetProperty("pluginClassName", out var className) ? className.GetString() : null,
            Priority = root.TryGetProperty("priority", out var priority) && priority.TryGetInt32(out var number) ? number : 100,
            ModType = root.TryGetProperty("modType", out var modType) ? modType.GetString() ?? "dll" : "dll",
            SupportHotReload = root.TryGetProperty("supportHotReload", out var hotReload) && hotReload.ValueKind == JsonValueKind.True,
            ApiVersion = Required("apiVersion"), PackageType = Required("packageType")
        };
        if (!CommandNamePattern.IsMatch(metadata.CommandName)) throw new InvalidDataException("commandName 只能包含字母、数字、_ 和 -。");
        if (strictPortable && (!string.Equals(metadata.PackageType, "portable", StringComparison.OrdinalIgnoreCase) || !metadata.SupportHotReload))
            throw new InvalidDataException("便携包必须声明 packageType=portable 且 supportHotReload=true。");
        if (!string.Equals(metadata.ModType, "dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("当前便携包只支持 dll Mod。");
        return metadata;
    }

    private void EnsureNoCrossTypeCollision(IModMetadata candidate, string? replacingPortableId)
    {
        foreach (var entry in _bridge.GetAllMods().Values)
        {
            var sameId = string.Equals(entry.Metadata.Id, candidate.Id, StringComparison.OrdinalIgnoreCase);
            var sameName = string.Equals(EffectiveCommandName(entry.Metadata), candidate.CommandName, StringComparison.OrdinalIgnoreCase);
            if (!sameId && !sameName) continue;
            if (IsPortable(entry.Metadata) && string.Equals(entry.Metadata.Id, replacingPortableId, StringComparison.OrdinalIgnoreCase)) continue;
            throw new InvalidOperationException($"与现有{(IsPortable(entry.Metadata) ? "便携" : "普通")} Mod '{entry.Metadata.Name}' 的 ID 或短名冲突。");
        }
    }

    private (IModPlugin Plugin, IModMetadata Metadata, bool IsEnabled)? FindEntry(string commandName)
    {
        foreach (var entry in _bridge.GetAllMods().Values)
            if (string.Equals(EffectiveCommandName(entry.Metadata), commandName, StringComparison.OrdinalIgnoreCase)) return entry;
        return null;
    }

    private static IReadOnlyList<ModManagementCommand> SafeGetManagementCommands(IModManagementCommandProvider provider)
    {
        return (provider.GetModManagementCommands() ?? Array.Empty<ModManagementCommand>())
            .Where(command => command is not null && !string.IsNullOrWhiteSpace(command.Name) && CommandNamePattern.IsMatch(command.Name))
            .GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
    }

    private string? FindStandardDisabledMarker(string id)
    {
        foreach (var directory in Directory.EnumerateDirectories(_modsRoot))
        {
            if (string.Equals(Path.GetFileName(directory), ".portable", StringComparison.OrdinalIgnoreCase)) continue;
            var manifest = Path.Combine(directory, "mod.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("id", out var value) && string.Equals(value.GetString(), id, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(directory, ".disabled");
            }
            catch { }
        }
        return null;
    }

    private string GetPortableDisabledMarker(string commandName) => Path.Combine(_portableRoot, commandName, ".disabled");
    private static void SetDisabledMarker(string path, bool disabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (disabled) File.WriteAllText(path, string.Empty);
        else if (File.Exists(path)) File.Delete(path);
    }

    private void SaveState(PortableRuntime runtime)
    {
        var modRoot = Path.Combine(_portableRoot, runtime.Metadata.CommandName);
        Directory.CreateDirectory(modRoot);
        SetDisabledMarker(Path.Combine(modRoot, ".disabled"), !runtime.Enabled);
        var state = new PortableState(runtime.Metadata.Id, runtime.Metadata.CommandName, runtime.VersionHash, runtime.Enabled);
        var path = Path.Combine(modRoot, "state.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static string EffectiveCommandName(IModMetadata metadata) =>
        string.IsNullOrWhiteSpace(metadata.CommandName) ? metadata.Id : metadata.CommandName;
    private static bool IsPortable(IModMetadata metadata) => string.Equals(metadata.PackageType, "portable", StringComparison.OrdinalIgnoreCase);
    private void SendGroup(long groupId, string message) => _distribution.WSconnection.SendGroupMessage(groupId, message);

    private static void ReleaseContext(PortableRuntime runtime)
    {
        var context = runtime.LoadContext;
        if (context is null) return;
        var weakReference = new WeakReference(context, trackResurrection: false);
        runtime.Plugin = null;
        runtime.LoadContext = null;
        context.Unload();
        context = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (weakReference.IsAlive)
            Log.Warn($"[PortableMod] load context for {runtime.Metadata.CommandName} remains alive; the Mod may retain a static event or background thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _distribution.OnFileMessage -= OnFileMessage;
        foreach (var runtime in _portableById.Values) ReleaseContext(runtime);
        _portableById.Clear();
        _mutationGate.Dispose();
    }

    private sealed class PortableRuntime
    {
        public PortableRuntime(IModPlugin plugin, ModMetadata metadata, PortableModLoadContext loadContext, string contentPath, string versionHash, bool enabled)
        { Plugin = plugin; Metadata = metadata; LoadContext = loadContext; ContentPath = contentPath; VersionHash = versionHash; Enabled = enabled; }
        public IModPlugin? Plugin { get; set; }
        public ModMetadata Metadata { get; }
        public PortableModLoadContext? LoadContext { get; set; }
        public string ContentPath { get; }
        public string VersionHash { get; }
        public bool Enabled { get; set; }
    }

    private sealed record PortableState(string Id, string CommandName, string CurrentHash, bool Enabled);
}
