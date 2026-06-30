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

    public OneBotFileDownloadService(Func<WSconnection?> getConnection, Action<string>? logger = null)
    {
        _getConnection = getConnection;
        _logger = logger ?? Log.Normal;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(20)
        };

        _downloadDir = Path.Combine(GetApplicationRootDirectory(), "temp", "update_qq");
    }

    public async Task<OneBotFileDownloadResult> DownloadAsync(OneBotFileInfo fileInfo)
    {
        if (fileInfo == null)
        {
            return Fail("文件信息为空");
        }

        if (string.IsNullOrWhiteSpace(fileInfo.FileId))
        {
            return Fail("OneBot 文件缺少 file_id，无法调用 get_file");
        }

        try
        {
            Directory.CreateDirectory(_downloadDir);
            _logger($"[QQ更新包] 调用 get_file: user={fileInfo.UserId}, group={fileInfo.GroupId}, file={fileInfo.FileName}, size={fileInfo.FileSize}");

            var connection = _getConnection();
            if (connection == null || !connection.IsWsConnected)
            {
                return Fail("WebSocket 未连接，无法调用 OneBot get_file");
            }

            var request = new Dictionary<string, object>
            {
                ["action"] = "get_file",
                ["params"] = new Dictionary<string, object>
                {
                    ["file_id"] = fileInfo.FileId
                }
            };

            var response = await connection.SendRequestAndAwaitResponseAsync(request, GetFileTimeoutMs);
            if (response == null)
            {
                return Fail("OneBot get_file 超时或无响应（已使用 15 分钟超时）");
            }

            if (response.Value.TryGetProperty("status", out var statusElement) &&
                string.Equals(statusElement.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
            {
                var wording = TryGetString(response.Value, "wording");
                return Fail(string.IsNullOrWhiteSpace(wording)
                    ? "OneBot get_file 返回 failed"
                    : $"OneBot get_file 返回 failed: {wording}");
            }

            if (!response.Value.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return Fail("OneBot get_file 响应缺少 data 对象");
            }

            var responsePath = FirstString(data, "path", "file");
            var responseUrl = FirstString(data, "url");
            var responseName = FirstString(data, "name", "file_name");
            var fileName = PickFileName(responseName, fileInfo.FileName, responsePath, fileInfo.FileId);

            if (!string.IsNullOrWhiteSpace(responsePath))
            {
                var localCopy = await TryCopyLocalFileAsync(responsePath, fileName);
                if (localCopy.Success)
                {
                    return localCopy;
                }

                if (string.IsNullOrWhiteSpace(responseUrl))
                {
                    return localCopy;
                }

                _logger($"[QQ更新包] get_file 返回路径不可访问，将尝试 URL 下载: {localCopy.ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(responseUrl))
            {
                return await DownloadFromUrlAsync(responseUrl, fileName);
            }

            return Fail("OneBot get_file 未返回可访问 path/file，也未返回 url");
        }
        catch (Exception ex)
        {
            _logger($"[QQ更新包] 下载文件失败: {ex.Message}");
            return Fail($"下载文件失败: {ex.Message}");
        }
    }

    private async Task<OneBotFileDownloadResult> TryCopyLocalFileAsync(string sourcePath, string fileName)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return Fail("LLOneBot 返回了本机路径，但 MDiceV2 无法访问。可能是 LLOneBot 与 MDiceV2 不在同一机器/容器，或文件尚未被 LLOneBot 下载。");
            }

            var targetPath = CreateUniqueTargetPath(fileName);
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

            await source.CopyToAsync(target);
            await target.FlushAsync();

            var length = new FileInfo(targetPath).Length;
            _logger($"[QQ更新包] 本机路径复制完成: {targetPath} ({length} bytes)");
            return new OneBotFileDownloadResult(true, targetPath, fileName, length, null);
        }
        catch (Exception ex)
        {
            return Fail($"复制 LLOneBot 本机文件失败: {ex.Message}");
        }
    }

    private async Task<OneBotFileDownloadResult> DownloadFromUrlAsync(string url, string fileName)
    {
        try
        {
            var targetPath = CreateUniqueTargetPath(fileName);
            _logger($"[QQ更新包] 开始通过 URL 流式下载: {url}");

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return Fail($"URL 下载失败: {response.StatusCode}");
            }

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);

            await source.CopyToAsync(target);
            await target.FlushAsync();

            var length = new FileInfo(targetPath).Length;
            _logger($"[QQ更新包] URL 下载完成: {targetPath} ({length} bytes)");
            return new OneBotFileDownloadResult(true, targetPath, fileName, length, null);
        }
        catch (Exception ex)
        {
            return Fail($"URL 下载失败: {ex.Message}");
        }
    }

    private string CreateUniqueTargetPath(string fileName)
    {
        Directory.CreateDirectory(_downloadDir);
        var safeName = MakeSafeFileName(fileName);
        var target = Path.Combine(_downloadDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{safeName}");
        return target;
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
