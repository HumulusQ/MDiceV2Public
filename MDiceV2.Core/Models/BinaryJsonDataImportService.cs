using System.Data.SQLite;
using System.Text;
using System.Text.Json;

namespace MDiceV2.Models;

public sealed record BinaryJsonDataImportPlan(
    IReadOnlyDictionary<string, string> SourceRows,
    int SourceRowCount,
    int DefaultEntriesSkipped,
    int RowsPendingWrite);

public sealed record BinaryJsonDataImportResult(
    bool Success,
    string Message,
    int SourceRowCount,
    int DefaultEntriesSkipped,
    int RowsWritten);

/// <summary>
/// Safely reads BinaryJsonData from a separately uploaded SQLite database.  Known
/// template dictionaries are merged field-by-field so source defaults never replace
/// a locally customized value.
/// </summary>
public sealed class BinaryJsonDataImportService
{
    public const string TableName = "BinaryJsonData";
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public BinaryJsonDataImportResult TryCreatePlan(
        byte[] databaseContent,
        DataIO targetData,
        out BinaryJsonDataImportPlan? plan)
    {
        plan = null;
        if (databaseContent == null || databaseContent.Length < SqliteHeader.Length ||
            !databaseContent.AsSpan(0, SqliteHeader.Length).SequenceEqual(SqliteHeader))
        {
            return Fail("文件不是有效的 SQLite 数据库。");
        }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"mdice-import-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(temporaryPath, databaseContent);
            var sourceRows = ReadSourceRows(temporaryPath);
            if (sourceRows.Count == 0)
            {
                return Fail("数据库的 BinaryJsonData 表没有可导入的数据。");
            }

            ValidateKnownTemplateRows(sourceRows);
            var preview = BuildUpdates(sourceRows, targetData);
            plan = new BinaryJsonDataImportPlan(sourceRows, sourceRows.Count, preview.DefaultEntriesSkipped, preview.Updates.Count);
            return new BinaryJsonDataImportResult(
                true,
                "文件校验通过。",
                plan.SourceRowCount,
                plan.DefaultEntriesSkipped,
                plan.RowsPendingWrite);
        }
        catch (Exception ex)
        {
            Log.Warn($"[数据库导入] 预检失败: {ex.Message}");
            return Fail($"无法读取 BinaryJsonData：{ex.Message}");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public BinaryJsonDataImportResult Apply(BinaryJsonDataImportPlan plan, DataIO targetData)
    {
        try
        {
            // Build again at confirmation time so a concurrent local template edit is
            // preserved whenever the uploaded field is a default value.
            var update = BuildUpdates(plan.SourceRows, targetData);
            var rowsWritten = targetData.SaveDataBatchIfChanged(TableName, update.Updates.Select(item => (item.Key, item.Value)));
            if (rowsWritten > 0)
            {
                GlobalFeedbackMessages.ReloadTemplatesFromDatabase();
            }

            return new BinaryJsonDataImportResult(
                true,
                rowsWritten > 0 ? "BinaryJsonData 已导入并热加载。" : "没有需要覆盖的内容；当前设置保持不变。",
                plan.SourceRowCount,
                update.DefaultEntriesSkipped,
                rowsWritten);
        }
        catch (Exception ex)
        {
            Log.Error($"[数据库导入] 应用失败: {ex}");
            return Fail($"导入失败，未写入任何数据：{ex.Message}", plan.SourceRowCount, plan.DefaultEntriesSkipped);
        }
    }

    private static Dictionary<string, string> ReadSourceRows(string databasePath)
    {
        var connectionString = $"Data Source={databasePath};Version=3;Read Only=True;FailIfMissing=True;";
        using var connection = new SQLiteConnection(connectionString);
        connection.Open();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var tableInfo = new SQLiteCommand("PRAGMA table_info(BinaryJsonData);", connection))
        using (var reader = tableInfo.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader["name"]?.ToString() ?? string.Empty);
            }
        }

        if (!columns.Contains("key") || !columns.Contains("value"))
        {
            throw new InvalidDataException("未找到 BinaryJsonData(key, value) 表结构。");
        }

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = new SQLiteCommand("SELECT key, value FROM BinaryJsonData;", connection);
        using var dataReader = command.ExecuteReader();
        while (dataReader.Read())
        {
            if (dataReader.IsDBNull(0) || dataReader.IsDBNull(1))
            {
                throw new InvalidDataException("BinaryJsonData 包含空 key 或 value。");
            }

            var key = dataReader.GetString(0);
            var value = dataReader.GetString(1);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidDataException("BinaryJsonData 包含空 key。");
            }

            rows[key] = value;
        }

        return rows;
    }

    private static void ValidateKnownTemplateRows(IReadOnlyDictionary<string, string> sourceRows)
    {
        foreach (var key in new[] { "FeedbackTemplate", "HelpTemplates" })
        {
            if (!sourceRows.TryGetValue(key, out var json))
            {
                continue;
            }

            if (DeserializeTemplate(json) == null)
            {
                throw new InvalidDataException($"{key} 不是 string:string JSON 对象。");
            }
        }
    }

    private static UpdateBuildResult BuildUpdates(IReadOnlyDictionary<string, string> sourceRows, DataIO targetData)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var defaultsByRow = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["FeedbackTemplate"] = GlobalFeedbackMessages.GetDefaultFeedbackTemplates(),
            ["HelpTemplates"] = GlobalFeedbackMessages.GetDefaultHelpTemplates()
        };
        var defaultEntriesSkipped = 0;

        foreach (var (key, sourceValue) in sourceRows)
        {
            var currentValue = targetData.ReadData(TableName, key);
            if (!defaultsByRow.TryGetValue(key, out var defaults))
            {
                if (!string.Equals(currentValue, sourceValue, StringComparison.Ordinal))
                {
                    updates[key] = sourceValue;
                }
                continue;
            }

            var sourceTemplates = DeserializeTemplate(sourceValue)!;
            var currentTemplates = DeserializeTemplate(currentValue) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var changed = false;
            foreach (var (templateKey, sourceText) in sourceTemplates)
            {
                if (defaults.TryGetValue(templateKey, out var defaultText) &&
                    string.Equals(sourceText, defaultText, StringComparison.Ordinal))
                {
                    defaultEntriesSkipped++;
                    continue;
                }

                if (!currentTemplates.TryGetValue(templateKey, out var currentText) ||
                    !string.Equals(currentText, sourceText, StringComparison.Ordinal))
                {
                    currentTemplates[templateKey] = sourceText;
                    changed = true;
                }
            }

            if (changed)
            {
                updates[key] = JsonSerializer.Serialize(currentTemplates, JsonOptions);
            }
        }

        return new UpdateBuildResult(updates, defaultEntriesSkipped);
    }

    private static Dictionary<string, string>? DeserializeTemplate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BinaryJsonDataImportResult Fail(string message, int sourceRows = 0, int defaultsSkipped = 0) =>
        new(false, message, sourceRows, defaultsSkipped, 0);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record UpdateBuildResult(Dictionary<string, string> Updates, int DefaultEntriesSkipped);
}
