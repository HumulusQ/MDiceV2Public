using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MDiceV2.Models;

namespace MDiceV2.Core.Infrastructure;

public enum ManagedBotFramework
{
    SnowLuma,
    NapCat
}

public sealed record ManagedBotFrameworkInstallResult(
    ManagedBotFramework Framework,
    string Version,
    string WebSocketUrl);

public sealed record ManagedBotAccount(string UserId, string DisplayName);

public sealed record SnowLumaAgreementBundle(
    string Version,
    string EulaText,
    string PrivacyText);

/// <summary>
/// Downloads and hosts an optional QQ/OneBot runtime from an official upstream
/// release. Third-party binaries never participate in the MDiceV2 build.
/// </summary>
public sealed class BotFrameworkRuntimeManager : IDisposable
{
    private const string ActiveConfigFileName = "active-framework.json";
    private const string ManagedProcessFileName = ".managed-process.json";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _frameworksRoot;
    private readonly HttpClient _httpClient;
    private Process? _runtimeProcess;
    private string? _runtimeLauncherPath;

    public BotFrameworkRuntimeManager()
    {
        _frameworksRoot = ResolveFrameworksRoot();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MDiceV2", MessageProcessor.GetApplicationVersion()));
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("BotFrameworkInstaller", "1.0"));
    }

    public string FrameworksRoot => _frameworksRoot;

    public static bool HasActiveInstalledRuntime()
    {
        try
        {
            var root = ResolveFrameworksRoot();
            var state = ReadActiveState(root);
            return state != null && FindLauncher(root, state.Framework) != null;
        }
        catch
        {
            return false;
        }
    }

    public ManagedBotFramework? GetActiveFramework()
    {
        return ReadActiveState(_frameworksRoot)?.Framework;
    }

    public bool IsAutoConnectEnabled() => ReadActiveState(_frameworksRoot)?.AutoConnect ?? true;

    public void SetAutoConnectEnabled(bool enabled)
    {
        var state = ReadActiveState(_frameworksRoot);
        if (state != null && state.AutoConnect != enabled)
        {
            WriteActiveState(_frameworksRoot, state with { AutoConnect = enabled });
        }
    }

    public bool IsInstalled(ManagedBotFramework framework) => FindLauncher(_frameworksRoot, framework) != null;

    public string? GetSelectedSnowLumaAccount() =>
        ReadActiveState(_frameworksRoot) is { Framework: ManagedBotFramework.SnowLuma } state
            ? state.TargetUserId
            : null;

    public IReadOnlyList<ManagedBotAccount> GetDetectedSnowLumaAccounts()
    {
        var configDirectory = Path.Combine(
            GetFrameworkDirectory(ManagedBotFramework.SnowLuma), "runtime", "config");
        if (!Directory.Exists(configDirectory))
        {
            return Array.Empty<ManagedBotAccount>();
        }

        return Directory.EnumerateFiles(configDirectory, "onebot_*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path)["onebot_".Length..])
            .Where(userId => userId.Length is >= 5 and <= 12 && userId.All(char.IsDigit) && userId != "0")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(userId => userId, StringComparer.Ordinal)
            .Select(userId => new ManagedBotAccount(userId, $"QQ {userId}"))
            .ToArray();
    }

    public SnowLumaAgreementBundle GetSnowLumaAgreements()
    {
        var runtimeDirectory = Path.Combine(
            GetFrameworkDirectory(ManagedBotFramework.SnowLuma), "runtime");
        var eulaPath = Path.Combine(runtimeDirectory, "EULA.md");
        var privacyPath = Path.Combine(runtimeDirectory, "PRIVACY.md");
        if (!File.Exists(eulaPath) || !File.Exists(privacyPath))
        {
            throw new FileNotFoundException("SnowLuma 协议文件不完整，请重新下载安装。");
        }

        var eula = File.ReadAllText(eulaPath, Encoding.UTF8);
        var privacy = File.ReadAllText(privacyPath, Encoding.UTF8);
        return new SnowLumaAgreementBundle(
            ComputeSnowLumaAgreementVersion(eula, privacy),
            eula,
            privacy);
    }

    public bool IsSnowLumaConsentRequired()
    {
        if (!IsInstalled(ManagedBotFramework.SnowLuma))
        {
            return false;
        }

        var agreements = GetSnowLumaAgreements();
        var consentPath = Path.Combine(
            GetFrameworkDirectory(ManagedBotFramework.SnowLuma), "runtime", "config", "consent.json");
        if (!File.Exists(consentPath))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(consentPath));
            return !document.RootElement.TryGetProperty("version", out var version) ||
                   !string.Equals(version.GetString(), agreements.Version, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public void RecordSnowLumaConsent(string agreementVersion)
    {
        var agreements = GetSnowLumaAgreements();
        if (!string.Equals(agreementVersion, agreements.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SnowLuma 协议内容已更新，请重新阅读后确认。");
        }

        var configDirectory = Path.Combine(
            GetFrameworkDirectory(ManagedBotFramework.SnowLuma), "runtime", "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "consent.json"),
            JsonSerializer.Serialize(new
            {
                version = agreements.Version,
                acceptedAt = DateTime.UtcNow.ToString("O")
            }, JsonOptions),
            Utf8NoBom);
    }

    public void StartForAccountDiscovery(ManagedBotFramework framework)
    {
        var definition = GetDefinition(framework);
        var launcher = FindLauncher(_frameworksRoot, framework)
            ?? throw new FileNotFoundException($"未找到 {definition.DisplayName} 运行时，请先下载。");
        if (framework == ManagedBotFramework.SnowLuma)
        {
            if (IsSnowLumaConsentRequired())
            {
                throw new InvalidOperationException("请先在 MDice 中阅读并接受 SnowLuma EULA 与隐私说明。");
            }
            EnsureSnowLumaOneBotConfig(Path.GetDirectoryName(launcher)!);
        }
        StartRuntime(launcher, definition.DisplayName);
    }

    public async Task<ManagedBotFrameworkInstallResult> InstallAsync(
        ManagedBotFramework framework,
        IProgress<string>? status = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(framework);
        var frameworkDirectory = GetFrameworkDirectory(framework);
        var runtimeDirectory = Path.Combine(frameworkDirectory, "runtime");
        var downloadJobDirectory = Path.Combine(_frameworksRoot, ".downloads", Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(downloadJobDirectory, "runtime.zip");
        var stagingDirectory = Path.Combine(downloadJobDirectory, "staging");
        string? backupDirectory = null;
        var installedNewRuntime = false;

        Directory.CreateDirectory(downloadJobDirectory);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            status?.Report($"正在查询 {definition.DisplayName} 最新版本…");
            JsonElement releaseRoot = default;
            Exception? lastReleaseException = null;
            foreach (var sourceId in BuildDownloadSourceOrder(GetPreferredDownloadSource()))
            {
                var releaseApiUrl = CustomUpdateManager.MirrorSites.TransformUrl(definition.ReleaseApi, sourceId);
                try
                {
                    using var releaseResponse = await _httpClient.GetAsync(releaseApiUrl, cancellationToken);
                    releaseResponse.EnsureSuccessStatusCode();
                    await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var releaseDocument = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
                    releaseRoot = releaseDocument.RootElement.Clone();
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
                {
                    lastReleaseException = ex;
                }
            }
            if (releaseRoot.ValueKind == JsonValueKind.Undefined)
            {
                throw new HttpRequestException("所有下载源均无法获取 Release 信息。", lastReleaseException);
            }
            var version = releaseRoot.TryGetProperty("tag_name", out var tagName)
                ? tagName.GetString() ?? "latest"
                : "latest";
            var asset = SelectAsset(releaseRoot, framework);
            var assetName = asset.GetProperty("name").GetString() ?? "runtime.zip";
            var downloadUrl = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidDataException("上游 Release 未提供有效下载地址。");
            }

            status?.Report($"正在下载 {definition.DisplayName} {version}…");
            var preferredSource = GetPreferredDownloadSource();
            var sourceOrder = BuildDownloadSourceOrder(preferredSource);
            Exception? lastDownloadException = null;
            foreach (var sourceId in sourceOrder)
            {
                var candidateUrl = CustomUpdateManager.MirrorSites.TransformUrl(downloadUrl, sourceId);
                try
                {
                    status?.Report($"正在从 {GetDownloadSourceDisplayName(sourceId)} 下载 {definition.DisplayName} {version}…");
                    using var downloadResponse = await _httpClient.GetAsync(
                        candidateUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    downloadResponse.EnsureSuccessStatusCode();
                    var totalLength = downloadResponse.Content.Headers.ContentLength;
                    await using var source = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
                    await using var target = new FileStream(
                        archivePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        useAsync: true);
                    var buffer = new byte[1024 * 1024];
                    long received = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        received += count;
                        if (totalLength > 0)
                        {
                            progress?.Report(Math.Clamp(received * 100d / totalLength.Value, 0d, 100d));
                        }
                    }
                    lastDownloadException = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    lastDownloadException = ex;
                    TryDeleteFile(archivePath);
                    status?.Report($"{GetDownloadSourceDisplayName(sourceId)} 不可用，尝试下一个下载源…");
                }
            }
            if (lastDownloadException != null)
            {
                throw new HttpRequestException("所有下载源均无法建立连接。", lastDownloadException);
            }

            status?.Report($"正在解压 {assetName}…");
            await Task.Run(
                () => ExtractZipSafely(archivePath, stagingDirectory),
                cancellationToken);
            var stagedLauncher = FindLauncherInExtractedDirectory(stagingDirectory, framework)
                ?? throw new InvalidDataException("下载包内未找到受支持的启动文件。");
            var stagedRuntimeRoot = Path.GetDirectoryName(stagedLauncher)!;

            Directory.CreateDirectory(frameworkDirectory);
            if (Directory.Exists(runtimeDirectory))
            {
                backupDirectory = Path.Combine(
                    frameworkDirectory,
                    $"runtime.backup-{DateTime.Now:yyyyMMddHHmmss}");
                Directory.Move(runtimeDirectory, backupDirectory);
            }

            Directory.Move(stagedRuntimeRoot, runtimeDirectory);
            installedNewRuntime = true;
            if (backupDirectory != null)
            {
                PreserveUserConfiguration(backupDirectory, runtimeDirectory);
            }

            if (framework == ManagedBotFramework.SnowLuma)
            {
                EnsureSnowLumaOneBotConfig(runtimeDirectory);
            }
            else if (framework == ManagedBotFramework.NapCat)
            {
                EnsureNapCatOneBotConfig(runtimeDirectory);
            }

            var previousState = ReadActiveState(_frameworksRoot);
            var state = new ActiveFrameworkState
            {
                Framework = framework,
                WebSocketUrl = definition.WebSocketUrl,
                Version = version,
                InstalledAtUtc = DateTime.UtcNow,
                TargetUserId = framework == ManagedBotFramework.SnowLuma
                    ? previousState?.TargetUserId
                    : null,
                AutoConnect = previousState?.AutoConnect ?? true
            };
            WriteActiveState(_frameworksRoot, state);
            progress?.Report(100);
            status?.Report($"{definition.DisplayName} {version} 已安装，准备启动…");
            return new ManagedBotFrameworkInstallResult(framework, version, definition.WebSocketUrl);
        }
        catch
        {
            if (installedNewRuntime)
            {
                TryDeleteDirectory(runtimeDirectory);
            }

            if (backupDirectory != null &&
                Directory.Exists(backupDirectory) &&
                !Directory.Exists(runtimeDirectory))
            {
                Directory.Move(backupDirectory, runtimeDirectory);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(downloadJobDirectory);
        }
    }

    public async Task<string?> StartAndWaitForLoginAsync(
        ManagedBotFramework framework,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(framework);
        var state = ReadActiveState(_frameworksRoot);
        if (framework == ManagedBotFramework.SnowLuma &&
            (state?.Framework != framework || string.IsNullOrWhiteSpace(state.TargetUserId)))
        {
            StartForAccountDiscovery(framework);
            status?.Report("请选择 SnowLuma 机器人账号");
            return null;
        }
        var webSocketUrl = state?.Framework == framework && !string.IsNullOrWhiteSpace(state.WebSocketUrl)
            ? state.WebSocketUrl
            : definition.WebSocketUrl;
        var launcher = FindLauncher(_frameworksRoot, framework)
            ?? throw new FileNotFoundException($"未找到 {definition.DisplayName} 运行时，请先下载。");

        if (framework == ManagedBotFramework.SnowLuma)
        {
            // Migrate older managed installations that still exposed one shared
            // global MDice endpoint. Per-account endpoints avoid multi-QQ races.
            EnsureSnowLumaOneBotConfig(Path.GetDirectoryName(launcher)!);
        }

        // A single MDice instance owns exactly one protocol runtime. Starting
        // the selected launcher here also stops a previously managed peer.
        StartRuntime(launcher, definition.DisplayName);

        status?.Report($"等待 {definition.DisplayName} 账号登录…");
        while (!cancellationToken.IsCancellationRequested)
        {
            var loggedInUserId = await ProbeLoginAsync(webSocketUrl, cancellationToken);
            if (loggedInUserId != null &&
                (framework != ManagedBotFramework.SnowLuma || loggedInUserId == state?.TargetUserId))
            {
                status?.Report("账号已登录，连接中…");
                return webSocketUrl;
            }

            if (_runtimeProcess?.HasExited == true)
            {
                throw new InvalidOperationException(
                    $"{definition.DisplayName} 已退出（代码 {_runtimeProcess.ExitCode}），请检查其日志。");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _runtimeProcess?.Dispose();
        _runtimeProcess = null;
        _runtimeLauncherPath = null;
    }

    public async Task<string> SelectSnowLumaAccountAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        userId = userId.Trim();
        if (!IsValidSnowLumaAccountId(userId))
        {
            throw new ArgumentException("请输入 5–12 位有效 QQ 号。", nameof(userId));
        }

        var runtimeDirectory = Path.Combine(
            GetFrameworkDirectory(ManagedBotFramework.SnowLuma), "runtime");
        if (FindLauncher(_frameworksRoot, ManagedBotFramework.SnowLuma) is null)
        {
            throw new FileNotFoundException("未找到 SnowLuma Launcher，请先下载运行时。");
        }

        var port = FindAvailableLoopbackPort(32101, 100);
        ConfigureSnowLumaAccount(runtimeDirectory, userId, port);
        var webSocketUrl = $"ws://127.0.0.1:{port}/";
        var previous = ReadActiveState(_frameworksRoot);
        WriteActiveState(_frameworksRoot, new ActiveFrameworkState
        {
            Framework = ManagedBotFramework.SnowLuma,
            WebSocketUrl = webSocketUrl,
            Version = previous?.Framework == ManagedBotFramework.SnowLuma ? previous.Version : string.Empty,
            InstalledAtUtc = previous?.Framework == ManagedBotFramework.SnowLuma
                ? previous.InstalledAtUtc
                : DateTime.UtcNow,
            TargetUserId = userId,
            AutoConnect = previous?.AutoConnect ?? true
        });

        await StopRuntimeAsync(cancellationToken);
        StartForAccountDiscovery(ManagedBotFramework.SnowLuma);
        return webSocketUrl;
    }

    private static bool IsValidSnowLumaAccountId(string userId) =>
        userId.Length is >= 5 and <= 12 &&
        userId[0] != '0' &&
        userId.All(character => character is >= '0' and <= '9');

    private async Task StopRuntimeAsync(CancellationToken cancellationToken)
    {
        if (_runtimeProcess is not { HasExited: false } process)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken);
        process.Dispose();
        _runtimeProcess = null;
        _runtimeLauncherPath = null;
        ClearManagedProcessState();
    }

    private void StartRuntime(string launcherPath, string displayName)
    {
        if (_runtimeProcess is { HasExited: false })
        {
            if (string.Equals(_runtimeLauncherPath, launcherPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _runtimeProcess.Kill(entireProcessTree: true);
            _runtimeProcess.WaitForExit();
            _runtimeProcess.Dispose();
            _runtimeProcess = null;
            ClearManagedProcessState();
        }

        StopPersistedManagedProcess();

        Log.InfoFormat($"[BotFramework] 启动 {displayName}: {launcherPath}");
        _runtimeProcess = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"\"{launcherPath}\"\"",
            WorkingDirectory = Path.GetDirectoryName(launcherPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (_runtimeProcess is null)
        {
            throw new InvalidOperationException($"无法启动 {displayName}。");
        }

        _runtimeLauncherPath = launcherPath;
        WriteManagedProcessState(new ManagedProcessState
        {
            ProcessId = _runtimeProcess.Id,
            LauncherPath = launcherPath,
            StartedAtUtc = _runtimeProcess.StartTime.ToUniversalTime()
        });
    }

    private void StopPersistedManagedProcess()
    {
        var state = ReadManagedProcessState();
        if (state is null)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            var startTime = process.StartTime.ToUniversalTime();
            if (Math.Abs((startTime - state.StartedAtUtc).TotalSeconds) <= 2 && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"[BotFramework] 无法回收上一托管进程: {ex.Message}");
        }
        finally
        {
            ClearManagedProcessState();
        }
    }

    private ManagedProcessState? ReadManagedProcessState()
    {
        var path = Path.Combine(_frameworksRoot, ManagedProcessFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ManagedProcessState>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"[BotFramework] 无法读取托管进程状态: {ex.Message}");
            return null;
        }
    }

    private void WriteManagedProcessState(ManagedProcessState state) =>
        File.WriteAllText(
            Path.Combine(_frameworksRoot, ManagedProcessFileName),
            JsonSerializer.Serialize(state, JsonOptions),
            Utf8NoBom);

    private void ClearManagedProcessState()
    {
        var path = Path.Combine(_frameworksRoot, ManagedProcessFileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            Log.Warn($"[BotFramework] 无法清理托管进程状态: {ex.Message}");
        }
    }

    private static string ComputeSnowLumaAgreementVersion(string eula, string privacy)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (id, text) in new[] { ("eula", eula), ("privacy", privacy) })
        {
            hash.AppendData(Encoding.UTF8.GetBytes(id));
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(Encoding.UTF8.GetBytes(text));
            hash.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }

    private static async Task<string?> ProbeLoginAsync(string webSocketUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(webSocketUrl), timeout.Token);

            const string echo = "mdice_login_probe";
            var request = Encoding.UTF8.GetBytes(
                "{\"action\":\"get_login_info\",\"params\":{},\"echo\":\"mdice_login_probe\"}");
            await socket.SendAsync(request, WebSocketMessageType.Text, true, timeout.Token);

            var buffer = new byte[16 * 1024];
            while (!timeout.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (!root.TryGetProperty("echo", out var echoElement) ||
                    !string.Equals(echoElement.ToString(), echo, StringComparison.Ordinal))
                {
                    continue;
                }

                var success = root.TryGetProperty("status", out var statusElement) &&
                              string.Equals(statusElement.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
                if (!success && root.TryGetProperty("retcode", out var retcodeElement))
                {
                    success = retcodeElement.TryGetInt32(out var retcode) && retcode == 0;
                }

                if (!success || !root.TryGetProperty("data", out var dataElement))
                {
                    return null;
                }

                return dataElement.TryGetProperty("user_id", out var userIdElement) &&
                       long.TryParse(userIdElement.ToString(), out var userId) && userId > 0
                    ? userId.ToString()
                    : null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or JsonException or UriFormatException)
        {
            return null;
        }

        return null;
    }

    private static JsonElement SelectAsset(JsonElement releaseRoot, ManagedBotFramework framework)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("上游 Release 响应缺少 assets。");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var match = framework switch
            {
                ManagedBotFramework.SnowLuma =>
                    name.StartsWith("SnowLuma-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("-win-x64-lite.zip", StringComparison.OrdinalIgnoreCase),
                ManagedBotFramework.NapCat =>
                    name.Equals("NapCat.Shell.Windows.Node.zip", StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if (match)
            {
                return asset.Clone();
            }
        }

        throw new InvalidDataException("最新 Release 中没有找到受支持的 Windows x64 完整包。");
    }

    private static void ExtractZipSafely(string archivePath, string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("下载包包含越界路径，已拒绝解压。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static string? FindLauncher(string frameworksRoot, ManagedBotFramework framework)
    {
        var runtimeDirectory = Path.Combine(frameworksRoot, GetFolderName(framework), "runtime");
        return Directory.Exists(runtimeDirectory)
            ? FindLauncherInExtractedDirectory(runtimeDirectory, framework)
            : null;
    }

    private static string? FindLauncherInExtractedDirectory(string directory, ManagedBotFramework framework)
    {
        var candidates = framework == ManagedBotFramework.SnowLuma
            ? new[] { "launcher.bat" }
            : new[] { "launcher-win10.bat", "launcher-user.bat", "launcher.bat", "napcat.bat" };

        foreach (var candidate in candidates)
        {
            var match = Directory.EnumerateFiles(directory, candidate, SearchOption.AllDirectories).FirstOrDefault();
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static void EnsureNapCatOneBotConfig(string runtimeDirectory)
    {
        var configDirectory = Path.Combine(runtimeDirectory, "config");
        var configPath = Path.Combine(configDirectory, "onebot11.json");
        if (File.Exists(configPath))
        {
            return;
        }

        Directory.CreateDirectory(configDirectory);
        var config = new
        {
            network = new
            {
                httpServers = Array.Empty<object>(),
                httpSseServers = Array.Empty<object>(),
                httpClients = Array.Empty<object>(),
                websocketServers = new[]
                {
                    new
                    {
                        enable = true,
                        name = "MDiceV2",
                        host = "127.0.0.1",
                        port = 3001,
                        reportSelfMessage = false,
                        enableForcePushEvent = true,
                        messagePostFormat = "array",
                        token = "",
                        debug = false,
                        heartInterval = 30000
                    }
                },
                websocketClients = Array.Empty<object>(),
                plugins = Array.Empty<object>()
            },
            musicSignUrl = "",
            enableLocalFile2Url = false,
            parseMultMsg = false,
            imageDownloadProxy = ""
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions), Utf8NoBom);
    }

    private static void EnsureSnowLumaOneBotConfig(string runtimeDirectory)
    {
        var configDirectory = Path.Combine(runtimeDirectory, "config");
        var configPath = Path.Combine(configDirectory, "onebot.json");
        Directory.CreateDirectory(configDirectory);
        JsonObject config;
        if (File.Exists(configPath))
        {
            config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                ?? throw new InvalidDataException("SnowLuma onebot.json 不是有效的 JSON 对象。");
        }
        else
        {
            config = new JsonObject();
        }

        var networks = config["networks"] as JsonObject ?? new JsonObject();
        config["networks"] = networks;
        networks["httpServers"] ??= new JsonArray();
        networks["httpClients"] ??= new JsonArray();
        networks["wsClients"] ??= new JsonArray();
        var wsServers = networks["wsServers"] as JsonArray ?? new JsonArray();
        networks["wsServers"] = wsServers;
        RemoveManagedSnowLumaAdapters(wsServers);
        File.WriteAllText(configPath, config.ToJsonString(JsonOptions), Utf8NoBom);

        // SnowLuma otherwise requires the user to inject each discovered QQ
        // process from WebUI. Opt in to its supported auto-load switch because
        // this runtime is explicitly managed by MDiceV2.
        var runtimeConfigPath = Path.Combine(configDirectory, "runtime.json");
        JsonObject runtimeConfig;
        if (File.Exists(runtimeConfigPath))
        {
            runtimeConfig = JsonNode.Parse(File.ReadAllText(runtimeConfigPath)) as JsonObject
                ?? throw new InvalidDataException("SnowLuma runtime.json 不是有效的 JSON 对象。");
        }
        else
        {
            runtimeConfig = new JsonObject();
        }

        runtimeConfig["hookAutoLoad"] = true;
        File.WriteAllText(runtimeConfigPath, runtimeConfig.ToJsonString(JsonOptions), Utf8NoBom);
    }

    private static void ConfigureSnowLumaAccount(string runtimeDirectory, string userId, int port)
    {
        var configDirectory = Path.Combine(runtimeDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, $"onebot_{userId}.json");
        var config = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            : new JsonObject();
        config ??= new JsonObject();

        var networks = config["networks"] as JsonObject ?? new JsonObject();
        config["networks"] = networks;
        var wsServers = networks["wsServers"] as JsonArray ?? new JsonArray();
        networks["wsServers"] = wsServers;
        RemoveManagedSnowLumaAdapters(wsServers);
        wsServers.Add(new JsonObject
        {
            ["name"] = "mdice",
            ["host"] = "127.0.0.1",
            ["port"] = port,
            ["path"] = "/",
            ["role"] = "Universal",
            ["accessToken"] = "",
            ["messageFormat"] = "array",
            ["reportSelfMessage"] = false
        });
        File.WriteAllText(configPath, config.ToJsonString(JsonOptions), Utf8NoBom);
    }

    private static void RemoveManagedSnowLumaAdapters(JsonArray adapters)
    {
        for (var index = adapters.Count - 1; index >= 0; index--)
        {
            if (adapters[index] is JsonObject adapter &&
                string.Equals(adapter["name"]?.GetValue<string>(), "mdice", StringComparison.OrdinalIgnoreCase))
            {
                adapters.RemoveAt(index);
            }
        }
    }

    private static int FindAvailableLoopbackPort(int firstPort, int count)
    {
        for (var port = firstPort; port < firstPort + count; port++)
        {
            try
            {
                var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Try the next MDice-reserved local port.
            }
        }

        throw new IOException("没有可用的本机端口用于 SnowLuma WebSocket。");
    }

    private static void PreserveUserConfiguration(string backupDirectory, string runtimeDirectory)
    {
        var oldConfigDirectory = Path.Combine(backupDirectory, "config");
        if (!Directory.Exists(oldConfigDirectory))
        {
            return;
        }

        CopyDirectoryContents(oldConfigDirectory, Path.Combine(runtimeDirectory, "config"));
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(
                sourceFile,
                Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)),
                overwrite: true);
        }

        foreach (var sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectoryContents(
                sourceSubdirectory,
                Path.Combine(destinationDirectory, Path.GetFileName(sourceSubdirectory)));
        }
    }

    private static string ResolveFrameworksRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            for (var depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "BotFrameworks");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var root = Path.Combine(Environment.CurrentDirectory, "BotFrameworks");
        Directory.CreateDirectory(root);
        return root;
    }

    private static ActiveFrameworkState? ReadActiveState(string frameworksRoot)
    {
        var path = Path.Combine(frameworksRoot, ActiveConfigFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ActiveFrameworkState>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"[BotFramework] 无法读取活动框架状态: {ex.Message}");
            return null;
        }
    }

    private static void WriteActiveState(string frameworksRoot, ActiveFrameworkState state)
    {
        Directory.CreateDirectory(frameworksRoot);
        File.WriteAllText(
            Path.Combine(frameworksRoot, ActiveConfigFileName),
            JsonSerializer.Serialize(state, JsonOptions),
            Utf8NoBom);
    }

    private static string GetFolderName(ManagedBotFramework framework) => framework switch
    {
        ManagedBotFramework.SnowLuma => "SnowLuma",
        ManagedBotFramework.NapCat => "NapCat",
        _ => throw new ArgumentOutOfRangeException(nameof(framework))
    };

    private string GetFrameworkDirectory(ManagedBotFramework framework) =>
        Path.Combine(_frameworksRoot, GetFolderName(framework));

    private static FrameworkDefinition GetDefinition(ManagedBotFramework framework) => framework switch
    {
        ManagedBotFramework.SnowLuma => new(
            "SnowLuma",
            "https://api.github.com/repos/SnowLuma/SnowLuma/releases/latest",
            "ws://127.0.0.1:3001/"),
        ManagedBotFramework.NapCat => new(
            "NapCat",
            "https://api.github.com/repos/NapNeko/NapCatQQ/releases/latest",
            "ws://127.0.0.1:3001/"),
        _ => throw new ArgumentOutOfRangeException(nameof(framework))
    };

    private static string GetPreferredDownloadSource()
    {
        try
        {
            return GlobalFeedbackMessages.GetBasicSetting("UpdateSource") ?? "github";
        }
        catch
        {
            return "github";
        }
    }

    private static IReadOnlyList<string> BuildDownloadSourceOrder(string preferred)
    {
        var order = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred)) order.Add(preferred);
        order.AddRange(CustomUpdateManager.MirrorSites.GetAllSourceIds()
            .Where(source => !order.Contains(source, StringComparer.OrdinalIgnoreCase)));
        if (!order.Contains("github", StringComparer.OrdinalIgnoreCase)) order.Add("github");
        return order;
    }

    private static string GetDownloadSourceDisplayName(string sourceId) =>
        CustomUpdateManager.MirrorSites.GetAllMirrors()
            .FirstOrDefault(item => item.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            .DisplayName ?? sourceId;

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[BotFramework] 清理临时目录失败: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A failed mirror attempt must not hide the original network error.
        }
    }

    private sealed record FrameworkDefinition(string DisplayName, string ReleaseApi, string WebSocketUrl);

    private sealed record ActiveFrameworkState
    {
        public ManagedBotFramework Framework { get; init; }
        public string WebSocketUrl { get; init; } = "ws://127.0.0.1:3001/";
        public string? TargetUserId { get; init; }
        public string Version { get; init; } = string.Empty;
        public DateTime InstalledAtUtc { get; init; }
        public bool AutoConnect { get; init; } = true;
    }

    private sealed class ManagedProcessState
    {
        public int ProcessId { get; init; }
        public string LauncherPath { get; init; } = string.Empty;
        public DateTime StartedAtUtc { get; init; }
    }
}
