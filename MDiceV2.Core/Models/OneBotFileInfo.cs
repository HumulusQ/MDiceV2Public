using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

#nullable enable
namespace MDiceV2.Models;

/// <summary>
/// OneBot 文件消息的宽容解析模型。
/// </summary>
public sealed class OneBotFileInfo
{
    public string SourceKind { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long GroupId { get; set; }
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public bool IsPrivateMessage =>
        SourceKind.Equals("private_message", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<OneBotFileInfo> ExtractFromMessageSegments(
        JsonElement messageElement,
        string sourceKind,
        long userId,
        long groupId)
    {
        var files = new List<OneBotFileInfo>();
        if (messageElement.ValueKind != JsonValueKind.Array)
        {
            return files;
        }

        foreach (var segment in messageElement.EnumerateArray())
        {
            try
            {
                if (!segment.TryGetProperty("type", out var typeElement) ||
                    !string.Equals(typeElement.GetString(), "file", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                JsonElement fileElement = segment;
                if (segment.TryGetProperty("data", out var dataElement) &&
                    dataElement.ValueKind == JsonValueKind.Object)
                {
                    fileElement = dataElement;
                }

                var info = FromJsonElement(sourceKind, userId, groupId, fileElement);
                if (!string.IsNullOrWhiteSpace(info.FileId) ||
                    !string.IsNullOrWhiteSpace(info.FileName) ||
                    !string.IsNullOrWhiteSpace(info.Path) ||
                    !string.IsNullOrWhiteSpace(info.Url))
                {
                    files.Add(info);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[OneBot文件] 解析 message file 段失败: {ex.Message}");
            }
        }

        return files;
    }

    public static OneBotFileInfo FromJsonElement(
        string sourceKind,
        long userId,
        long groupId,
        JsonElement fileElement)
    {
        var info = new OneBotFileInfo
        {
            SourceKind = sourceKind,
            UserId = userId,
            GroupId = groupId,
            ReceivedAt = DateTime.UtcNow
        };

        if (fileElement.ValueKind == JsonValueKind.String)
        {
            var text = fileElement.GetString() ?? string.Empty;
            info.FileName = SanitizeDisplayName(text);
            info.FileId = text;
            return info;
        }

        if (fileElement.ValueKind != JsonValueKind.Object)
        {
            return info;
        }

        info.FileId = GetFirstString(fileElement, "file_id", "id", "file");
        info.FileName = SanitizeDisplayName(GetFirstString(fileElement, "name", "file_name", "file"));
        info.FileSize = GetFirstInt64(fileElement, "file_size", "size") ?? 0;
        info.Path = GetFirstString(fileElement, "path");
        info.Url = GetFirstString(fileElement, "url");

        return info;
    }

    private static string SanitizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var name = System.IO.Path.GetFileName(value.Trim());
            return string.IsNullOrWhiteSpace(name) ? value.Trim() : name;
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string GetFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString()?.Trim() ?? string.Empty;
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return value.GetBoolean().ToString();
            }
        }

        return string.Empty;
    }

    private static long? GetFirstInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            {
                return numeric;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
