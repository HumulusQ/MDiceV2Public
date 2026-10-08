using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

#nullable enable
namespace MDiceV2.Models;

public sealed record OneBotFileDownloadResult(
    bool Success,
    string? LocalPath,
    string FileName,
    long FileSize,
    string? ErrorMessage);

public sealed class OneBotFileDownloadService
{
    private const int GetFileTimeoutMs = 15 * 60 * 1000;
    private readonly Func<WSconnection?> _getConnection;
    private readonly Action<string> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _downloadDir;
    private readonly long? _maxFileSizeBytes;
    private readonly Func<Dictionary<string, object>, int, Task<JsonElement?>>? _requestSender;

    public OneBotFileDownloadService(
        Func<WSconnection?> getConnection,
        Action<string>? logger = null,
        long? maxFileSizeBytes = null,
        Func<Dictionary<string, object>, int, Task<JsonElement?>>? requestSender = null)
    {
        _getConnection = getConnection;
        _logger = logger ?? Log.Normal;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(20)
        };
        _maxFileSizeBytes = maxFileSizeBytes;
        _requestSender = requestSender;

        _downloadDir = Path.Combine(GetApplicationRootDirectory(), "temp", "update_qq");
    }

    public async Task<OneBotFileDownloadResult> DownloadAsync(OneBotFileInfo fileInfo)
    {
        if (fileInfo == null)
        {
            return Fail("文件信息为空");
        }

        try
        {
            Directory.CreateDirectory(_downloadDir);

            var directName = PickFileName(string.Empty, fileInfo.FileName, fileInfo.Path, fileInfo.FileId);
            if (!string.IsNullOrWhiteSpace(fileInfo.Path))
            {
                var directCopy = await TryCopyLocalFileAsync(fileInfo.Path, directName);
                if (directCopy.Success) return directCopy;
            }
            if (!string.IsNullOrWhiteSpace(fileInfo.Url))
            {
                var directDownload = await DownloadFromUrlAsync(fileInfo.Url, directName);
                if (directDownload.Success) return directDownload;
            }

            if (string.IsNullOrWhiteSpace(fileInfo.FileId))
                return Fail("OneBot 文件未提供可用的 url/path/file_id");

            var connection = _getConnection();
            if (_requestSender is null && (connection == null || !connection.IsWsConnected))
            {
                return Fail("WebSocket 未连接，无法请求 OneBot 文件地址");
            }

            var attempts = new List<(string Action, Dictionary<string, object> Parameters)>();
            if (fileInfo.GroupId > 0)
            {
                attempts.Add(("get_group_file_url", new Dictionary<string, object>
                {
                    ["group_id"] = fileInfo.GroupId,
                    ["file_id"] = fileInfo.FileId,
                    ["busid"] = fileInfo.BusId
                }));
            }
            else if (fileInfo.IsPrivateMessage)
            {
                attempts.Add(("get_private_file_url", new Dictionary<string, object>
                {
                    ["user_id"] = fileInfo.UserId,
                    ["file_id"] = fileInfo.FileId
                }));
            }
            // Compatibility fallback for adapters that implement the generic extension.
            attempts.Add(("get_file", new Dictionary<string, object> { ["file_id"] = fileInfo.FileId }));

            var failures = new List<string>();
            foreach (var (action, parameters) in attempts)
            {
                _logger($"[OneBot文件] 调用 {action}: user={fileInfo.UserId}, group={fileInfo.GroupId}, file={fileInfo.FileName}, size={fileInfo.FileSize}");
                var request = new Dictionary<string, object> { ["action"] = action, ["params"] = parameters };
                var response = _requestSender is not null
                    ? await _requestSender(request, GetFileTimeoutMs)
                    : await connection!.SendRequestAndAwaitResponseAsync(request, GetFileTimeoutMs);
                if (response is null) { failures.Add($"{action} 超时或无响应"); continue; }

                if (response.Value.TryGetProperty("status", out var statusElement) &&
                    string.Equals(statusElement.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
                {
                    var wording = FirstString(response.Value, "wording", "message", "msg");
                    failures.Add(string.IsNullOrWhiteSpace(wording) ? $"{action} 返回 failed" : $"{action} 返回 failed: {wording}");
                    continue;
                }

                var data = response.Value.TryGetProperty("data", out var responseData) && responseData.ValueKind == JsonValueKind.Object
                    ? responseData : response.Value;
                var responsePath = FirstString(data, "path", "file");
                var responseUrl = FirstString(data, "url");
                var responseName = FirstString(data, "name", "file_name");
                var fileName = PickFileName(responseName, fileInfo.FileName, responsePath, fileInfo.FileId);

                if (!string.IsNullOrWhiteSpace(responsePath))
                {
                    var localCopy = await TryCopyLocalFileAsync(responsePath, fileName);
                    if (localCopy.Success) return localCopy;
                    failures.Add($"{action}: {localCopy.ErrorMessage}");
                }
                if (!string.IsNullOrWhiteSpace(responseUrl))
                {
                    var downloaded = await DownloadFromUrlAsync(responseUrl, fileName);
                    if (downloaded.Success) return downloaded;
                    failures.Add($"{action}: {downloaded.ErrorMessage}");
                }
                if (string.IsNullOrWhiteSpace(responsePath) && string.IsNullOrWhiteSpace(responseUrl))
                    failures.Add($"{action} 未返回 url/path/file");
            }

            return Fail("OneBot 文件下载失败：" + string.Join("；", failures));
        }
        catch (Exception ex)
        {
            _logger($"[QQ更新包] 下载文件失败: {ex.Message}");
            return Fail($"下载文件失败: {ex.Message}");
        }
    }

    private async Task<OneBotFileDownloadResult> TryCopyLocalFileAsync(string sourcePath, string fileName)
    {
        string? targetPath = null;
        try
        {
            if (!File.Exists(sourcePath))
            {
                return Fail("LLOneBot 返回了本机路径，但 MDiceV2 无法访问。可能是 LLOneBot 与 MDiceV2 不在同一机器/容器，或文件尚未被 LLOneBot 下载。");
            }

            targetPath = CreateUniqueTargetPath(fileName);
            _logger($"[QQ更新包] 开始从本机路径流式复制: {sourcePath}");

            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                useAsync: true);
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);

            await CopyWithLimitAsync(source, target);
            await target.FlushAsync();

            var length = new FileInfo(targetPath).Length;
            _logger($"[QQ更新包] 本机路径复制完成: {targetPath} ({length} bytes)");
            return new OneBotFileDownloadResult(true, targetPath, fileName, length, null);
        }
        catch (Exception ex)
        {
            TryDeletePartial(targetPath);
            return Fail($"复制 LLOneBot 本机文件失败: {ex.Message}");
        }
    }

    private async Task<OneBotFileDownloadResult> DownloadFromUrlAsync(string url, string fileName)
    {
        string? targetPath = null;
        try
        {
            targetPath = CreateUniqueTargetPath(fileName);
            _logger($"[QQ更新包] 开始通过 URL 流式下载: {url}");

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return Fail($"URL 下载失败: {response.StatusCode}");
            }
            if (_maxFileSizeBytes.HasValue && response.Content.Headers.ContentLength > _maxFileSizeBytes.Value)
                return Fail($"URL 文件超过允许的 {_maxFileSizeBytes.Value} 字节");

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);

            await CopyWithLimitAsync(source, target);
            await target.FlushAsync();

            var length = new FileInfo(targetPath).Length;
            _logger($"[QQ更新包] URL 下载完成: {targetPath} ({length} bytes)");
            return new OneBotFileDownloadResult(true, targetPath, fileName, length, null);
        }
        catch (Exception ex)
        {
            TryDeletePartial(targetPath);
            return Fail($"URL 下载失败: {ex.Message}");
        }
    }

    private static void TryDeletePartial(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private string CreateUniqueTargetPath(string fileName)
    {
        Directory.CreateDirectory(_downloadDir);
        var safeName = MakeSafeFileName(fileName);
        var target = Path.Combine(_downloadDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{safeName}");
        return target;
    }

    private async Task CopyWithLimitAsync(Stream source, Stream target)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0) break;
            total += read;
            if (_maxFileSizeBytes.HasValue && total > _maxFileSizeBytes.Value)
                throw new InvalidDataException($"文件超过允许的 {_maxFileSizeBytes.Value} 字节");
            await target.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static string PickFileName(string responseName, string originalName, string responsePath, string fileId)
    {
        foreach (var candidate in new[] { responseName, originalName, responsePath, fileId })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var name = Path.GetFileName(candidate.Trim());
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
            catch
            {
                return candidate.Trim();
            }
        }

        return "MDiceV2.Core.UpdatePackage";
    }

    private static string MakeSafeFileName(string fileName)
    {
        var safe = string.IsNullOrWhiteSpace(fileName) ? "MDiceV2.Core.UpdatePackage" : fileName.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(c, '_');
        }

        return safe.Length > 120 ? safe[^120..] : safe;
    }

    private static string FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = TryGetString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string TryGetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => string.Empty
            };
        }

        return string.Empty;
    }

    private static string GetApplicationRootDirectory()
    {
        try
        {
            var mainModule = Process.GetCurrentProcess().MainModule;
            var moduleDir = Path.GetDirectoryName(mainModule?.FileName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(moduleDir) &&
                Path.GetFileName(moduleDir).Equals("Core", StringComparison.OrdinalIgnoreCase))
            {
                var root = Path.GetDirectoryName(moduleDir);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    return root;
                }
            }

            return moduleDir ?? AppContext.BaseDirectory;
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }

    private static OneBotFileDownloadResult Fail(string message)
    {
        return new OneBotFileDownloadResult(false, null, string.Empty, 0, message);
    }
}
