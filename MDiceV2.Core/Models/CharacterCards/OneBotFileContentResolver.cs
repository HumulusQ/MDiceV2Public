using System.Net.Http;
using System.Text.Json;

namespace MDiceV2.Models.CharacterCards;

public sealed record FileResolveResult(bool Success, byte[]? Content, string Source, string ErrorMessage)
{
    public static FileResolveResult Fail(string message) => new(false, null, string.Empty, message);
    public static FileResolveResult Succeed(byte[] content, string source) => new(true, content, source, string.Empty);
}

/// <summary>Obtains uploaded content without executing or opening the uploaded file.</summary>
public sealed class OneBotFileContentResolver : IDisposable
{
    public const int MaxFileSizeBytes = 5 * 1024 * 1024;
    private readonly MessageDistribution _messageDistribution;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public OneBotFileContentResolver(MessageDistribution messageDistribution, HttpClient? httpClient = null)
    {
        _messageDistribution = messageDistribution;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttpClient = httpClient is null;
    }

    public async Task<FileResolveResult> ResolveAsync(OneBotFileInfo file, CancellationToken cancellationToken)
    {
        if (file.FileSize > MaxFileSizeBytes)
            return FileResolveResult.Fail("文件超过 5 MiB，无法自动导入。");

        if (TryGetHttpUri(file.Url, out var url))
        {
            var downloaded = await ReadUrlAsync(url, cancellationToken);
            if (downloaded.Success) return downloaded;
        }

        var fromPath = await ReadPathAsync(file.Path, cancellationToken);
        if (fromPath.Success) return fromPath;

        var fromApi = await TryGetFileContentAsync(file, cancellationToken);
        if (fromApi.Success) return fromApi;
        if (file.IsPrivateMessage)
        {
            return FileResolveResult.Fail(
                $"已识别到私聊文件，但 OneBot 未提供可读的 url/path，get_file 也未返回文件内容。{fromApi.ErrorMessage}");
        }

        return FileResolveResult.Fail("当前 OneBot 实现没有提供可用的群文件下载地址。");
    }

    private async Task<FileResolveResult> TryGetFileContentAsync(OneBotFileInfo file, CancellationToken cancellationToken)
    {
        if (file.IsPrivateMessage && string.IsNullOrWhiteSpace(file.FileId))
        {
            return FileResolveResult.Fail("私聊文件消息未提供 file_id，OneBot 无法请求文件内容。");
        }

        if (string.IsNullOrWhiteSpace(file.FileId))
            return FileResolveResult.Fail("群文件缺少可读取的 file_id。");

        try
        {
            if (file.GroupId > 0)
            {
                var fromGroupUrl = await TryReadFromOneBotActionAsync("get_group_file_url", new Dictionary<string, object>
                {
                    ["group_id"] = file.GroupId, ["file_id"] = file.FileId, ["busid"] = file.BusId
                }, cancellationToken);
                if (fromGroupUrl.Success) return fromGroupUrl;
            }

            // A number of existing OneBot adapters expose the generic get_file
            // extension instead of get_group_file_url.
            var fromGetFile = await TryReadFromOneBotActionAsync("get_file", new Dictionary<string, object>
            {
                ["file_id"] = file.FileId
            }, cancellationToken);
            if (!fromGetFile.Success)
            {
                return FileResolveResult.Fail($"OneBot get_file 无法读取文件：{fromGetFile.ErrorMessage}");
            }
            return fromGetFile.Success ? fromGetFile : FileResolveResult.Fail("OneBot 未返回群文件地址。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"[人物卡导入] 获取群文件地址失败: {ex.Message}");
            return FileResolveResult.Fail("读取群文件时发生错误。");
        }
    }

    private async Task<FileResolveResult> TryReadFromOneBotActionAsync(
        string action, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var response = await _messageDistribution.WSconnection.SendRequestAndAwaitResponseAsync(new Dictionary<string, object>
        {
            ["action"] = action,
            ["params"] = parameters
        });
        if (response is null) return FileResolveResult.Fail("OneBot 未返回文件地址。");
        if (response.Value.TryGetProperty("status", out var status) &&
            string.Equals(status.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
        {
            var wording = ExtractFirstString(response.Value, "wording", "message");
            return FileResolveResult.Fail(string.IsNullOrWhiteSpace(wording)
                ? $"OneBot {action} 返回 failed。"
                : $"OneBot {action} 返回 failed：{wording}");
        }

        var address = ExtractFirstString(response.Value, "url", "path", "file");
        if (string.IsNullOrWhiteSpace(address))
            return FileResolveResult.Fail($"OneBot {action} 未返回 url/path/file。");
        if (TryGetHttpUri(address, out var url)) return await ReadUrlAsync(url, cancellationToken);
        return await ReadPathAsync(address, cancellationToken);
    }

    private async Task<FileResolveResult> ReadUrlAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return FileResolveResult.Fail("无法下载群文件。");
            if (response.Content.Headers.ContentLength is > MaxFileSizeBytes)
                return FileResolveResult.Fail("文件超过 5 MiB，无法自动导入。");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ReadLimitedAsync(stream, "url", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FileResolveResult.Fail("下载群文件超时。");
        }
        catch (HttpRequestException)
        {
            return FileResolveResult.Fail("无法下载群文件。");
        }
    }

    private static async Task<FileResolveResult> ReadPathAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return FileResolveResult.Fail("本地文件路径不可用。");
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileSizeBytes) return FileResolveResult.Fail("文件超过 5 MiB，无法自动导入。");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            return await ReadLimitedAsync(stream, "path", cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return FileResolveResult.Fail("本地文件路径不可读取。");
        }
        catch (IOException)
        {
            return FileResolveResult.Fail("本地文件读取失败。");
        }
    }

    private static async Task<FileResolveResult> ReadLimitedAsync(Stream stream, string source, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (output.Length + read > MaxFileSizeBytes)
                return FileResolveResult.Fail("文件超过 5 MiB，无法自动导入。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return FileResolveResult.Succeed(output.ToArray(), source);
    }

    private static bool TryGetHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))) return true;
        uri = null!;
        return false;
    }

    private static string ExtractFirstString(JsonElement response, params string[] names)
    {
        var element = response;
        if (TryGetProperty(element, "data", out var data) && data.ValueKind == JsonValueKind.Object) element = data;
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
