using System.Data.Common;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

public partial class ChatDatabase
{
    internal async Task EnsureCommonApiUsageLogSchemaAsync()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS CommonApiUsageLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAt TEXT NOT NULL,
                    UserId INTEGER NOT NULL,
                    GroupId INTEGER NOT NULL,
                    WorldId TEXT NULL,
                    TeamName TEXT NULL,
                    CharacterId TEXT NULL,
                    Provider TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    AgentName TEXT NOT NULL,
                    RequestKind TEXT NOT NULL,
                    InputTokens INTEGER NOT NULL DEFAULT 0,
                    OutputTokens INTEGER NOT NULL DEFAULT 0,
                    TotalTokens INTEGER NOT NULL DEFAULT 0,
                    CachedInputTokens INTEGER NULL,
                    CacheHitTokens INTEGER NULL,
                    CacheMissTokens INTEGER NULL,
                    EstimatedCost REAL NOT NULL DEFAULT 0,
                    Success INTEGER NOT NULL DEFAULT 0,
                    SourceMessageId TEXT NULL,
                    TurnId TEXT NULL,
                    Metadata TEXT NOT NULL DEFAULT '{}'
                );

                CREATE INDEX IF NOT EXISTS idx_common_api_usage_created
                    ON CommonApiUsageLog(CreatedAt);

                CREATE INDEX IF NOT EXISTS idx_common_api_usage_user_created
                    ON CommonApiUsageLog(UserId, CreatedAt);

                CREATE INDEX IF NOT EXISTS idx_common_api_usage_group_created
                    ON CommonApiUsageLog(GroupId, CreatedAt);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] CommonApiUsageLog schema migration skipped: {ex.Message}");
        }
    }

    public async Task InsertCommonApiUsageLogAsync(CommonApiUsageLogEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CommonApiUsageLog
                (CreatedAt, UserId, GroupId, WorldId, TeamName, CharacterId,
                 Provider, Model, AgentName, RequestKind,
                 InputTokens, OutputTokens, TotalTokens,
                 CachedInputTokens, CacheHitTokens, CacheMissTokens,
                 EstimatedCost, Success, SourceMessageId, TurnId, Metadata)
            VALUES
                (@createdAt, @userId, @groupId, @worldId, @teamName, @characterId,
                 @provider, @model, @agentName, @requestKind,
                 @inputTokens, @outputTokens, @totalTokens,
                 @cachedInputTokens, @cacheHitTokens, @cacheMissTokens,
                 @estimatedCost, @success, @sourceMessageId, @turnId, @metadata)
            """;
        cmd.Parameters.AddWithValue("@createdAt", entry.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@userId", entry.UserId);
        cmd.Parameters.AddWithValue("@groupId", entry.GroupId);
        cmd.Parameters.AddWithValue("@worldId", string.IsNullOrWhiteSpace(entry.WorldId) ? (object)DBNull.Value : entry.WorldId);
        cmd.Parameters.AddWithValue("@teamName", string.IsNullOrWhiteSpace(entry.TeamName) ? (object)DBNull.Value : entry.TeamName);
        cmd.Parameters.AddWithValue("@characterId", string.IsNullOrWhiteSpace(entry.CharacterId) ? (object)DBNull.Value : entry.CharacterId);
        cmd.Parameters.AddWithValue("@provider", string.IsNullOrWhiteSpace(entry.Provider) ? "unknown" : entry.Provider);
        cmd.Parameters.AddWithValue("@model", string.IsNullOrWhiteSpace(entry.Model) ? "unknown" : entry.Model);
        cmd.Parameters.AddWithValue("@agentName", string.IsNullOrWhiteSpace(entry.AgentName) ? "unknown" : entry.AgentName);
        cmd.Parameters.AddWithValue("@requestKind", string.IsNullOrWhiteSpace(entry.RequestKind) ? "unknown" : entry.RequestKind);
        cmd.Parameters.AddWithValue("@inputTokens", entry.InputTokens);
        cmd.Parameters.AddWithValue("@outputTokens", entry.OutputTokens);
        cmd.Parameters.AddWithValue("@totalTokens", entry.TotalTokens);
        cmd.Parameters.AddWithValue("@cachedInputTokens", entry.CachedInputTokens ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@cacheHitTokens", entry.CacheHitTokens ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@cacheMissTokens", entry.CacheMissTokens ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@estimatedCost", Convert.ToDouble(entry.EstimatedCost));
        cmd.Parameters.AddWithValue("@success", entry.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("@sourceMessageId", string.IsNullOrWhiteSpace(entry.SourceMessageId) ? (object)DBNull.Value : entry.SourceMessageId);
        cmd.Parameters.AddWithValue("@turnId", string.IsNullOrWhiteSpace(entry.TurnId) ? (object)DBNull.Value : entry.TurnId);
        cmd.Parameters.AddWithValue("@metadata", string.IsNullOrWhiteSpace(entry.Metadata) ? "{}" : entry.Metadata);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<CommonApiUsageReport> GetCommonApiUsageReportAsync(
        DateTime from,
        DateTime to,
        long? optionalUserId = null,
        long? optionalGroupId = null)
    {
        var rows = await GetCommonApiUsageRowsAsync(from, to, optionalUserId, optionalGroupId);
        var report = new CommonApiUsageReport
        {
            FromUtc = from,
            ToUtc = to,
            Rows = rows
                .GroupBy(row => row.UserId)
                .Select(group =>
                {
                    var aggregated = AggregateCommonApiCache(group);
                    return new CommonApiUsageReportRow
                    {
                        UserId = group.Key,
                        RequestCount = group.Count(),
                        SuccessCount = group.Count(x => x.Success),
                        FailureCount = group.Count(x => !x.Success),
                        InputTokens = group.Sum(x => x.InputTokens),
                        OutputTokens = group.Sum(x => x.OutputTokens),
                        TotalTokens = group.Sum(x => x.TotalTokens),
                        CachedInputTokens = aggregated.CachedInputTokens,
                        CacheHitTokens = aggregated.CacheHitTokens,
                        CacheMissTokens = aggregated.CacheMissTokens,
                        CacheKnownTokens = aggregated.CacheKnownTokens,
                        EstimatedCost = group.Sum(x => x.EstimatedCost)
                    };
                })
                .OrderByDescending(row => row.EstimatedCost)
                .ThenByDescending(row => row.TotalTokens)
                .ThenBy(row => row.UserId)
                .ToList()
        };

        var totalCache = AggregateCommonApiCache(rows);
        report.RequestCount = rows.Count;
        report.SuccessCount = rows.Count(x => x.Success);
        report.FailureCount = rows.Count(x => !x.Success);
        report.InputTokens = rows.Sum(x => x.InputTokens);
        report.OutputTokens = rows.Sum(x => x.OutputTokens);
        report.TotalTokens = rows.Sum(x => x.TotalTokens);
        report.CachedInputTokens = totalCache.CachedInputTokens;
        report.CacheHitTokens = totalCache.CacheHitTokens;
        report.CacheMissTokens = totalCache.CacheMissTokens;
        report.CacheKnownTokens = totalCache.CacheKnownTokens;
        report.EstimatedCost = rows.Sum(x => x.EstimatedCost);
        return report;
    }

    private async Task<List<CommonApiUsageLogEntry>> GetCommonApiUsageRowsAsync(
        DateTime from,
        DateTime to,
        long? optionalUserId,
        long? optionalGroupId)
    {
        var rows = new List<CommonApiUsageLogEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CreatedAt, UserId, GroupId, WorldId, TeamName, CharacterId,
                   Provider, Model, AgentName, RequestKind,
                   InputTokens, OutputTokens, TotalTokens,
                   CachedInputTokens, CacheHitTokens, CacheMissTokens,
                   EstimatedCost, Success, SourceMessageId, TurnId, Metadata
            FROM CommonApiUsageLog
            WHERE CreatedAt >= @fromUtc AND CreatedAt <= @toUtc
              AND (@userId IS NULL OR UserId = @userId)
              AND (@groupId IS NULL OR GroupId = @groupId)
            ORDER BY CreatedAt DESC
            """;
        cmd.Parameters.AddWithValue("@fromUtc", from.ToString("o"));
        cmd.Parameters.AddWithValue("@toUtc", to.ToString("o"));
        cmd.Parameters.AddWithValue("@userId", optionalUserId.HasValue ? optionalUserId.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@groupId", optionalGroupId.HasValue ? optionalGroupId.Value : (object)DBNull.Value);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(ReadCommonApiUsageEntry(reader));
        }

        return rows;
    }

    private static CommonApiUsageLogEntry ReadCommonApiUsageEntry(DbDataReader reader)
    {
        return new CommonApiUsageLogEntry
        {
            Id = reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0)),
            CreatedAt = reader.IsDBNull(1) ? DateTime.UtcNow : DateTime.Parse(reader.GetString(1)),
            UserId = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2)),
            GroupId = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3)),
            WorldId = reader.IsDBNull(4) ? null : reader.GetString(4),
            TeamName = reader.IsDBNull(5) ? null : reader.GetString(5),
            CharacterId = reader.IsDBNull(6) ? null : reader.GetString(6),
            Provider = reader.IsDBNull(7) ? "unknown" : reader.GetString(7),
            Model = reader.IsDBNull(8) ? "unknown" : reader.GetString(8),
            AgentName = reader.IsDBNull(9) ? "unknown" : reader.GetString(9),
            RequestKind = reader.IsDBNull(10) ? "unknown" : reader.GetString(10),
            InputTokens = reader.IsDBNull(11) ? 0 : Convert.ToInt64(reader.GetValue(11)),
            OutputTokens = reader.IsDBNull(12) ? 0 : Convert.ToInt64(reader.GetValue(12)),
            TotalTokens = reader.IsDBNull(13) ? 0 : Convert.ToInt64(reader.GetValue(13)),
            CachedInputTokens = reader.IsDBNull(14) ? null : Convert.ToInt64(reader.GetValue(14)),
            CacheHitTokens = reader.IsDBNull(15) ? null : Convert.ToInt64(reader.GetValue(15)),
            CacheMissTokens = reader.IsDBNull(16) ? null : Convert.ToInt64(reader.GetValue(16)),
            EstimatedCost = reader.IsDBNull(17) ? 0m : Convert.ToDecimal(reader.GetValue(17)),
            Success = !reader.IsDBNull(18) && Convert.ToInt32(reader.GetValue(18)) == 1,
            SourceMessageId = reader.IsDBNull(19) ? null : reader.GetString(19),
            TurnId = reader.IsDBNull(20) ? null : reader.GetString(20),
            Metadata = reader.IsDBNull(21) ? "{}" : reader.GetString(21)
        };
    }

    private static CommonApiCacheAggregate AggregateCommonApiCache(IEnumerable<CommonApiUsageLogEntry> rows)
    {
        long cachedInputTokens = 0;
        long cacheHitTokens = 0;
        long cacheMissTokens = 0;
        long cacheKnownTokens = 0;
        var hasKnownCache = false;

        foreach (var row in rows)
        {
            var hasCacheData = row.CachedInputTokens.HasValue || row.CacheHitTokens.HasValue || row.CacheMissTokens.HasValue;
            if (!hasCacheData)
                continue;

            hasKnownCache = true;
            cachedInputTokens += row.CachedInputTokens ?? 0;
            cacheHitTokens += row.CacheHitTokens ?? 0;
            cacheMissTokens += row.CacheMissTokens ?? 0;
            cacheKnownTokens += ResolveCacheKnownTokens(row);
        }

        return new CommonApiCacheAggregate
        {
            CachedInputTokens = hasKnownCache ? cachedInputTokens : null,
            CacheHitTokens = hasKnownCache ? cacheHitTokens : null,
            CacheMissTokens = hasKnownCache ? cacheMissTokens : null,
            CacheKnownTokens = cacheKnownTokens
        };
    }

    private static long ResolveCacheKnownTokens(CommonApiUsageLogEntry row)
    {
        if (row.CacheHitTokens.HasValue || row.CacheMissTokens.HasValue)
            return Math.Max(0, (row.CacheHitTokens ?? 0) + (row.CacheMissTokens ?? 0));

        if (row.CachedInputTokens.HasValue)
            return Math.Max(0, row.InputTokens);

        return 0;
    }

    private sealed class CommonApiCacheAggregate
    {
        public long? CachedInputTokens { get; init; }
        public long? CacheHitTokens { get; init; }
        public long? CacheMissTokens { get; init; }
        public long CacheKnownTokens { get; init; }
    }
}

public sealed class CommonApiUsageLogEntry
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long UserId { get; set; }
    public long GroupId { get; set; }
    public string? WorldId { get; set; }
    public string? TeamName { get; set; }
    public string? CharacterId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string RequestKind { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? CacheHitTokens { get; set; }
    public long? CacheMissTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public bool Success { get; set; }
    public string? SourceMessageId { get; set; }
    public string? TurnId { get; set; }
    public string Metadata { get; set; } = "{}";
}

public sealed class CommonApiUsageReport
{
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public List<CommonApiUsageReportRow> Rows { get; set; } = new();
    public int RequestCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? CacheHitTokens { get; set; }
    public long? CacheMissTokens { get; set; }
    public long CacheKnownTokens { get; set; }
    public decimal EstimatedCost { get; set; }
}

public sealed class CommonApiUsageReportRow
{
    public long UserId { get; set; }
    public int RequestCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? CacheHitTokens { get; set; }
    public long? CacheMissTokens { get; set; }
    public long CacheKnownTokens { get; set; }
    public decimal EstimatedCost { get; set; }
}
