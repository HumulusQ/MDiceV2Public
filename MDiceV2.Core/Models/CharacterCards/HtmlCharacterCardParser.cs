using System.Text.Json;
using System.Text.RegularExpressions;

namespace MDiceV2.Models.CharacterCards;

public sealed record CharacterCardParseResult(
    bool Success,
    string ErrorMessage,
    IReadOnlyList<EmbeddedInvestigatorDocument> Documents,
    bool IsLibrary,
    string SourceSchema,
    int SourceVersion)
{
    public EmbeddedInvestigatorDocument? Document => Documents.Count == 0 ? null : Documents[0];

    public static CharacterCardParseResult Fail(string message) =>
        new(false, message, Array.Empty<EmbeddedInvestigatorDocument>(), false, string.Empty, 0);

    public static CharacterCardParseResult Succeed(EmbeddedInvestigatorDocument document) =>
        new(true, string.Empty, new[] { document }, false, document.Schema, document.Version);

    public static CharacterCardParseResult SucceedLibrary(
        IReadOnlyList<EmbeddedInvestigatorDocument> documents,
        string schema,
        int version) =>
        new(true, string.Empty, documents, true, schema, version);
}

/// <summary>
/// Extracts only the inert application/json script element.  It never creates an
/// HTML DOM, evaluates JavaScript, or dereferences page resources.
/// </summary>
public sealed class HtmlCharacterCardParser
{
    private const string InvestigatorSchema = "tott-coc7e-investigator";
    private const string InvestigatorLibrarySchema = "tott-coc7e-investigator-library";
    private const int SupportedVersion = 1;

    private static readonly Regex EmbeddedJsonRegex = new(
        "<script\\b(?=[^>]*(?<![\\w:-])id\\s*=\\s*[\"']embedded-investigator[\"'])(?=[^>]*(?<![\\w:-])type\\s*=\\s*[\"']application/json[\"'])[^>]*>(?<json>.*?)</script\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex EmbeddedLibraryJsonRegex = new(
        "<script\\b(?=[^>]*(?<![\\w:-])id\\s*=\\s*[\"']embedded-investigator-library[\"'])(?=[^>]*(?<![\\w:-])type\\s*=\\s*[\"']application/json[\"'])[^>]*>(?<json>.*?)</script\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public CharacterCardParseResult Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return CharacterCardParseResult.Fail("文件内容为空。");

        Match libraryMatch;
        Match investigatorMatch;
        try
        {
            libraryMatch = EmbeddedLibraryJsonRegex.Match(html);
            investigatorMatch = EmbeddedJsonRegex.Match(html);
        }
        catch (RegexMatchTimeoutException)
        {
            return CharacterCardParseResult.Fail("人物卡 HTML 解析超时。");
        }

        if (libraryMatch.Success)
        {
            var libraryJson = libraryMatch.Groups["json"].Value.Trim();
            if (!IsJsonNull(libraryJson))
                return ParseLibrary(libraryJson);
        }

        if (!investigatorMatch.Success)
            return CharacterCardParseResult.Fail("未找到 embedded-investigator 或 embedded-investigator-library 人物卡数据。");

        var json = investigatorMatch.Groups["json"].Value.Trim();
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
            if (!string.Equals(schema, InvestigatorSchema, StringComparison.Ordinal))
                return CharacterCardParseResult.Fail($"不支持的人物卡 schema：{schema}");
            if (version != SupportedVersion)
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

    private static CharacterCardParseResult ParseLibrary(string json)
    {
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
            if (!string.Equals(schema, InvestigatorLibrarySchema, StringComparison.Ordinal))
                return CharacterCardParseResult.Fail($"不支持的人物卡数据包 schema：{schema}");
            if (version != SupportedVersion)
                return CharacterCardParseResult.Fail($"不支持的人物卡数据包版本：{version}");
            if (!TryGetProperty(root, "investigators", out var investigatorsElement) ||
                investigatorsElement.ValueKind != JsonValueKind.Array)
            {
                return CharacterCardParseResult.Fail("人物卡数据包缺少 investigators 数组。");
            }

            var investigators = new List<EmbeddedInvestigatorDocument>();
            var index = 0;
            foreach (var investigatorElement in investigatorsElement.EnumerateArray())
            {
                index++;
                var investigatorSchema = GetString(investigatorElement, "schema");
                var investigatorVersion = GetInt32(investigatorElement, "version");
                if (!string.Equals(investigatorSchema, InvestigatorSchema, StringComparison.Ordinal))
                    return CharacterCardParseResult.Fail($"数据包中第 {index} 张人物卡的 schema 不受支持：{investigatorSchema}");
                if (investigatorVersion != SupportedVersion)
                    return CharacterCardParseResult.Fail($"数据包中第 {index} 张人物卡的版本不受支持：{investigatorVersion}");

                var investigator = JsonSerializer.Deserialize<EmbeddedInvestigatorDocument>(
                    investigatorElement.GetRawText(), CharacterCardJson.Options);
                if (investigator is null)
                    return CharacterCardParseResult.Fail($"数据包中第 {index} 张人物卡为空。");
                investigators.Add(investigator);
            }

            if (investigators.Count == 0)
                return CharacterCardParseResult.Fail("人物卡数据包中没有可导入的调查员。");

            // The coordinator makes the last imported card current. Preserve the
            // exporter's current-card selection by moving that card to the end.
            var currentId = GetString(root, "currentId");
            if (!string.IsNullOrWhiteSpace(currentId))
            {
                var currentIndex = investigators.FindIndex(x =>
                    string.Equals(x.Id, currentId, StringComparison.Ordinal));
                if (currentIndex >= 0 && currentIndex != investigators.Count - 1)
                {
                    var current = investigators[currentIndex];
                    investigators.RemoveAt(currentIndex);
                    investigators.Add(current);
                }
            }

            return CharacterCardParseResult.SucceedLibrary(investigators, schema, version);
        }
        catch (JsonException ex)
        {
            return CharacterCardParseResult.Fail($"人物卡数据包 JSON 无法解析：{ex.Message}");
        }
    }

    private static bool IsJsonNull(string json) =>
        string.IsNullOrWhiteSpace(json) || json.Equals("null", StringComparison.OrdinalIgnoreCase);

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
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

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
