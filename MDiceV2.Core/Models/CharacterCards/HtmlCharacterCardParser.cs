using System.Text.Json;
using System.Text.RegularExpressions;

namespace MDiceV2.Models.CharacterCards;

public sealed record CharacterCardParseResult(
    bool Success,
    string ErrorMessage,
    EmbeddedInvestigatorDocument? Document)
{
    public static CharacterCardParseResult Fail(string message) => new(false, message, null);
    public static CharacterCardParseResult Succeed(EmbeddedInvestigatorDocument document) => new(true, string.Empty, document);
}

/// <summary>
/// Extracts only the inert application/json script element.  It never creates an
/// HTML DOM, evaluates JavaScript, or dereferences page resources.
/// </summary>
public sealed class HtmlCharacterCardParser
{
    private static readonly Regex EmbeddedJsonRegex = new(
        "<script\\b(?=[^>]*(?<![\\w:-])id\\s*=\\s*[\"']embedded-investigator[\"'])(?=[^>]*(?<![\\w:-])type\\s*=\\s*[\"']application/json[\"'])[^>]*>(?<json>.*?)</script\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public CharacterCardParseResult Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return CharacterCardParseResult.Fail("文件内容为空。");

        Match match;
        try
        {
            match = EmbeddedJsonRegex.Match(html);
        }
        catch (RegexMatchTimeoutException)
        {
            return CharacterCardParseResult.Fail("人物卡 HTML 解析超时。");
        }

        if (!match.Success)
            return CharacterCardParseResult.Fail("未找到 embedded-investigator 人物卡数据。");

        var json = match.Groups["json"].Value.Trim();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

            var root = document.RootElement;
            var schema = GetString(root, "schema");
            var version = GetInt32(root, "version");
            if (!string.Equals(schema, "tott-coc7e-investigator", StringComparison.Ordinal))
                return CharacterCardParseResult.Fail($"不支持的人物卡 schema：{schema}");
            if (version != 1)
                return CharacterCardParseResult.Fail($"不支持的人物卡版本：{version}");

            var result = JsonSerializer.Deserialize<EmbeddedInvestigatorDocument>(json, CharacterCardJson.Options);
            return result is null
                ? CharacterCardParseResult.Fail("人物卡 JSON 为空。")
                : CharacterCardParseResult.Succeed(result);
        }
        catch (JsonException ex)
        {
            return CharacterCardParseResult.Fail($"人物卡 JSON 无法解析：{ex.Message}");
        }
    }

    private static string GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}
