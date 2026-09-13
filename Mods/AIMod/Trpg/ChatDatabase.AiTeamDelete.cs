using System.Data.SQLite;
using System.Linq;
using System.Text;

namespace AIMod.Trpg;

public partial class ChatDatabase
{
    public async Task<AiTeamDeletePreview> PreviewAiTeamDataDeleteAsync(
        long ownerUserId,
        long groupId,
        string teamName)
    {
        var warnings = new List<string>();
        var target = await ResolveAiTeamDeleteTargetAsync(ownerUserId, groupId, teamName, warnings);
        var counts = await BuildAiTeamDeleteCountsAsync(target, warnings, transaction: null);
        return new AiTeamDeletePreview(target, counts, warnings);
    }

    public async Task<AiTeamDeleteResult> DeleteAiTeamDataAsync(
        long ownerUserId,
        long groupId,
        string teamName)
    {
        var warnings = new List<string>();
        var target = await ResolveAiTeamDeleteTargetAsync(ownerUserId, groupId, teamName, warnings);
        var counts = await BuildAiTeamDeleteCountsAsync(target, warnings, transaction: null);
        var hasTargetData = target.WorldIds.Count > 0 || target.CharacterIds.Count > 0 || target.VirtualIds.Count > 0;
        if (!hasTargetData)
            return new AiTeamDeleteResult(target, counts, warnings, false);

        using var tx = _connection.BeginTransaction();
        try
        {
            await DeleteAiTeamChildTablesAsync(target, warnings, tx);
            await DeleteAiTeamWorldTablesAsync(target, warnings, tx);
            counts["AiCharacterEntry"] = await DeleteAiCharacterEntriesAsync(target, tx);
            counts["TrpgWorld"] = await DeleteTrpgWorldsAsync(target, tx);
            tx.Commit();
            return new AiTeamDeleteResult(target, counts, warnings, true);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private async Task<AiTeamDeleteTarget> ResolveAiTeamDeleteTargetAsync(
        long ownerUserId,
        long groupId,
        string teamName,
        List<string> warnings)
    {
        var normalizedTeamName = (teamName ?? string.Empty).Trim();
        var worldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var virtualIds = new HashSet<long>();

        if (await TableExistsAsync("TrpgWorld"))
        {
            using var worldCmd = _connection.CreateCommand();
            worldCmd.CommandText = """
                SELECT WorldId
                FROM TrpgWorld
                WHERE OwnerUserId = @ownerUserId AND GroupId = @groupId AND TeamName = @teamName
                ORDER BY WorldId
                """;
            worldCmd.Parameters.AddWithValue("@ownerUserId", ownerUserId);
            worldCmd.Parameters.AddWithValue("@groupId", groupId);
            worldCmd.Parameters.AddWithValue("@teamName", normalizedTeamName);
            using var worldReader = await worldCmd.ExecuteReaderAsync();
            while (await worldReader.ReadAsync())
            {
                if (!worldReader.IsDBNull(0))
                    worldIds.Add(worldReader.GetString(0));
            }
        }

        if (!await TableExistsAsync("AiCharacterEntry"))
        {
            warnings.Add("AiCharacterEntry 表不存在，无法继续定位 AI 角色。");
            return new AiTeamDeleteTarget(ownerUserId, groupId, normalizedTeamName, worldIds.ToList(), characterIds.ToList(), virtualIds.ToList());
        }

        var aiColumns = await GetTableColumnsAsync("AiCharacterEntry");
        var hasOwnerColumn = aiColumns.Contains("OwnerUserId");
        var hasWorldColumn = aiColumns.Contains("WorldId");

        using var aiCmd = _connection.CreateCommand();
        var aiWhere = new List<string>();
        if (hasOwnerColumn)
        {
            aiWhere.Add("OwnerUserId = @ownerUserId");
            aiCmd.Parameters.AddWithValue("@ownerUserId", ownerUserId);
        }
        else if (worldIds.Count > 0 && hasWorldColumn)
        {
            aiWhere.Add(BuildInClause(aiCmd, "WorldId", "@worldId", worldIds.ToList()));
            warnings.Add("AiCharacterEntry 缺少 OwnerUserId，已仅在已确认 WorldId 范围内辅助定位。");
        }
        else
        {
            warnings.Add("AiCharacterEntry 缺少 OwnerUserId/WorldId，已跳过旧 schema 辅助定位以避免跨 owner 误删。");
            return new AiTeamDeleteTarget(ownerUserId, groupId, normalizedTeamName, worldIds.ToList(), characterIds.ToList(), virtualIds.ToList());
        }

        if (aiColumns.Contains("GroupId"))
        {
            aiWhere.Add("GroupId = @groupId");
            aiCmd.Parameters.AddWithValue("@groupId", groupId);
        }

        if (aiColumns.Contains("TeamName"))
        {
            aiWhere.Add("TeamName = @teamName");
            aiCmd.Parameters.AddWithValue("@teamName", normalizedTeamName);
        }

        var selectWorld = hasWorldColumn ? "WorldId," : "NULL AS WorldId,";
        var selectCharacter = aiColumns.Contains("CharacterId") ? "CharacterId," : "NULL AS CharacterId,";
        var selectVirtual = aiColumns.Contains("VirtualId") ? "VirtualId" : "NULL AS VirtualId";
        aiCmd.CommandText = $"""
            SELECT {selectWorld} {selectCharacter} {selectVirtual}
            FROM AiCharacterEntry
            WHERE {string.Join(" AND ", aiWhere)}
            """;
        using var aiReader = await aiCmd.ExecuteReaderAsync();
        while (await aiReader.ReadAsync())
        {
            if (!aiReader.IsDBNull(0))
                worldIds.Add(aiReader.GetString(0));
            if (!aiReader.IsDBNull(1))
                characterIds.Add(aiReader.GetString(1));
            if (!aiReader.IsDBNull(2))
                virtualIds.Add(Convert.ToInt64(aiReader.GetValue(2)));
        }

        return new AiTeamDeleteTarget(
            ownerUserId,
            groupId,
            normalizedTeamName,
            worldIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            characterIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            virtualIds.OrderBy(x => x).ToList());
    }

    private async Task<Dictionary<string, int>> BuildAiTeamDeleteCountsAsync(
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction? transaction)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["World"] = target.WorldIds.Count,
            ["AI角色"] = target.CharacterIds.Count,
            ["VirtualId"] = target.VirtualIds.Count
        };

        counts["ChatHistory"] = await CountScopedTableAsync("ChatHistory", target, warnings, transaction);
        counts["LongTermMemory"] = await CountScopedTableAsync("LongTermMemory", target, warnings, transaction);
        counts["RawArchive"] = await CountRawArchiveAsync(target, warnings, transaction);
        counts["CharacterMemory"] = await CountScopedTableAsync("CharacterMemory", target, warnings, transaction);
        counts["NarrativeMemoryNode"] = await CountScopedTableAsync("NarrativeMemoryNode", target, warnings, transaction);
        counts["AffectiveTagState"] = await CountScopedTableAsync("AffectiveTagState", target, warnings, transaction);
        counts["AffectiveTagEvent"] = await CountScopedTableAsync("AffectiveTagEvent", target, warnings, transaction);
        counts["SceneSnapshot"] = await CountScopedTableAsync("SceneSnapshot", target, warnings, transaction);
        counts["Quest"] = await CountScopedTableAsync("Quest", target, warnings, transaction);
        counts["CharacterInventoryItem"] = await CountScopedTableAsync("CharacterInventoryItem", target, warnings, transaction);
        counts["CharacterInventorySeedState"] = await CountScopedTableAsync("CharacterInventorySeedState", target, warnings, transaction);
        counts["NpcCanonicalState"] = await CountScopedTableAsync("NpcCanonicalState", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["EntityCanonical"] = await CountScopedTableAsync("EntityCanonical", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["EntitySalience"] = await CountScopedTableAsync("EntitySalience", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["EventLog"] = await CountScopedTableAsync("EventLog", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["CausalGraph"] = await CountScopedTableAsync("CausalGraph", target, warnings, transaction);
        counts["TimelineNodes"] = await CountScopedTableAsync("TimelineNodes", target, warnings, transaction);
        counts["BehaviorEvidence"] = await CountScopedTableAsync("BehaviorEvidence", target, warnings, transaction);
        counts["CharacterSheet"] = await CountScopedTableAsync("CharacterSheet", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["AiDebugSetting"] = await CountScopedTableAsync("AiDebugSetting", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["LlmDebugLog"] = await CountScopedTableAsync("LlmDebugLog", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["LlmUsageLog"] = await CountScopedTableAsync("LlmUsageLog", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["CommonApiUsageLog"] = await CountCommonApiUsageDeleteAsync(target, warnings, transaction);
        counts["AiCharacterRuntimeControl"] = await CountScopedTableAsync("AiCharacterRuntimeControl", target, warnings, transaction);
        counts["CharacterHotMeta"] = await CountScopedTableAsync("CharacterHotMeta", target, warnings, transaction);
        counts["SceneDictionary"] = await CountScopedTableAsync("SceneDictionary", target, warnings, transaction, allowGroupCharacterFallback: false);
        counts["AiCharacterEntry"] = target.CharacterIds.Count;
        counts["TrpgWorld"] = target.WorldIds.Count;
        return counts;
    }

    private async Task DeleteAiTeamChildTablesAsync(
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction transaction)
    {
        await DeleteScopedTableAsync("CommonApiUsageLog", target, warnings, transaction, commonApiUsageMode: true);
        await DeleteScopedTableAsync("LlmDebugLog", target, warnings, transaction, allowGroupCharacterFallback: false);
        await DeleteScopedTableAsync("LlmUsageLog", target, warnings, transaction, allowGroupCharacterFallback: false);
        await DeleteRawArchiveAsync(target, warnings, transaction);
        await DeleteScopedTableAsync("BehaviorEvidence", target, warnings, transaction);
        await DeleteScopedTableAsync("TimelineNodes", target, warnings, transaction);
        await DeleteScopedTableAsync("CharacterInventoryItem", target, warnings, transaction);
        await DeleteScopedTableAsync("CharacterInventorySeedState", target, warnings, transaction);
        await DeleteScopedTableAsync("Quest", target, warnings, transaction);
        await DeleteScopedTableAsync("SceneSnapshot", target, warnings, transaction);
        await DeleteScopedTableAsync("AffectiveTagEvent", target, warnings, transaction);
        await DeleteScopedTableAsync("AffectiveTagState", target, warnings, transaction);
        await DeleteScopedTableAsync("CharacterMemory", target, warnings, transaction);
        await DeleteScopedTableAsync("NarrativeMemoryNode", target, warnings, transaction);
        await DeleteScopedTableAsync("CausalGraph", target, warnings, transaction);
        await DeleteScopedTableAsync("LongTermMemory", target, warnings, transaction);
        await DeleteScopedTableAsync("ChatHistory", target, warnings, transaction);
        await DeleteScopedTableAsync("AiCharacterRuntimeControl", target, warnings, transaction);
        await DeleteScopedTableAsync("CharacterHotMeta", target, warnings, transaction);
    }

    private async Task DeleteAiTeamWorldTablesAsync(
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction transaction)
    {
        await DeleteScopedTableAsync("AiDebugSetting", target, warnings, transaction, allowGroupCharacterFallback: false);
        await DeleteScopedTableAsync("EntitySalience", target, warnings, transaction, allowGroupCharacterFallback: false);
        await DeleteScopedTableAsync("EntityCanonical", target, warnings, transaction, allowGroupCharacterFallback: false);
        await DeleteScopedTableAsync("NpcCanonicalState", target, warnings, transaction, allowGroupCharacterFallback: false);
        await DeleteScopedTableAsync("EventLog", target, warnings, transaction, allowGroupCharacterFallback: false);
        await DeleteScopedTableAsync("CharacterSheet", target, warnings, transaction, allowGroupCharacterFallback: false);
        await DeleteScopedTableAsync("SceneDictionary", target, warnings, transaction, allowGroupCharacterFallback: false);
    }

    private async Task<int> DeleteAiCharacterEntriesAsync(AiTeamDeleteTarget target, SQLiteTransaction transaction)
    {
        if (!await TableExistsAsync("AiCharacterEntry"))
            return 0;

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE FROM AiCharacterEntry
            WHERE OwnerUserId = @ownerUserId AND GroupId = @groupId AND TeamName = @teamName
            """;
        cmd.Parameters.AddWithValue("@ownerUserId", target.OwnerUserId);
        cmd.Parameters.AddWithValue("@groupId", target.GroupId);
        cmd.Parameters.AddWithValue("@teamName", target.TeamName);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> DeleteTrpgWorldsAsync(AiTeamDeleteTarget target, SQLiteTransaction transaction)
    {
        if (!await TableExistsAsync("TrpgWorld"))
            return 0;

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE FROM TrpgWorld
            WHERE OwnerUserId = @ownerUserId AND GroupId = @groupId AND TeamName = @teamName
            """;
        cmd.Parameters.AddWithValue("@ownerUserId", target.OwnerUserId);
        cmd.Parameters.AddWithValue("@groupId", target.GroupId);
        cmd.Parameters.AddWithValue("@teamName", target.TeamName);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountCommonApiUsageDeleteAsync(
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction? transaction)
    {
        return await ExecuteCommonApiUsageDeleteOrCountAsync(target, warnings, transaction, delete: false);
    }

    private async Task<int> CountRawArchiveAsync(
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction? transaction)
    {
        if (!await TableExistsAsync("RawArchive"))
            return 0;

        var columns = await GetTableColumnsAsync("RawArchive");
        if (columns.Contains("WorldId"))
            return await ExecuteWorldScopedTableAsync("RawArchive", target, transaction, delete: false);

        if (!await TableExistsAsync("LongTermMemory"))
        {
            warnings.Add("RawArchive 存在，但 LongTermMemory 不存在，已跳过计数。");
            return 0;
        }

        var ltmColumns = await GetTableColumnsAsync("LongTermMemory");
        if (!ltmColumns.Contains("WorldId") && !(ltmColumns.Contains("GroupId") && ltmColumns.Contains("CharacterId")))
        {
            warnings.Add("RawArchive 缺少安全联动删除条件，已跳过计数。");
            return 0;
        }

        using var cmd = _connection.CreateCommand();
        if (transaction != null)
            cmd.Transaction = transaction;

        var joinWhere = BuildLongTermMemoryWhereClause(cmd, target, ltmColumns);
        if (string.IsNullOrWhiteSpace(joinWhere))
        {
            warnings.Add("RawArchive 旧 schema 无法安全限定目标范围，已跳过计数。");
            return 0;
        }

        cmd.CommandText = $"""
            SELECT COUNT(1)
            FROM RawArchive ra
            INNER JOIN LongTermMemory ltm ON ra.MemoryId = ltm.Id
            WHERE {joinWhere}
            """;
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    private async Task DeleteRawArchiveAsync(
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction transaction)
    {
        if (!await TableExistsAsync("RawArchive"))
            return;

        var columns = await GetTableColumnsAsync("RawArchive");
        if (columns.Contains("WorldId"))
        {
            await ExecuteWorldScopedTableAsync("RawArchive", target, transaction, delete: true);
            return;
        }

        if (!await TableExistsAsync("LongTermMemory"))
        {
            warnings.Add("RawArchive 存在，但 LongTermMemory 不存在，已跳过删除。");
            return;
        }

        var ltmColumns = await GetTableColumnsAsync("LongTermMemory");
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        var joinWhere = BuildLongTermMemoryWhereClause(cmd, target, ltmColumns);
        if (string.IsNullOrWhiteSpace(joinWhere))
        {
            warnings.Add("RawArchive 旧 schema 无法安全限定目标范围，已跳过删除。");
            return;
        }

        cmd.CommandText = $"""
            DELETE FROM RawArchive
            WHERE MemoryId IN (
                SELECT ltm.Id
                FROM LongTermMemory ltm
                WHERE {joinWhere}
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountScopedTableAsync(
        string tableName,
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction? transaction,
        bool allowGroupCharacterFallback = true)
    {
        return await ExecuteScopedTableDeleteOrCountAsync(
            tableName,
            target,
            warnings,
            transaction,
            delete: false,
            allowGroupCharacterFallback: allowGroupCharacterFallback);
    }

    private async Task DeleteScopedTableAsync(
        string tableName,
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction transaction,
        bool allowGroupCharacterFallback = true,
        bool commonApiUsageMode = false)
    {
        if (commonApiUsageMode)
        {
            await ExecuteCommonApiUsageDeleteOrCountAsync(target, warnings, transaction, delete: true);
            return;
        }

        await ExecuteScopedTableDeleteOrCountAsync(
            tableName,
            target,
            warnings,
            transaction,
            delete: true,
            allowGroupCharacterFallback: allowGroupCharacterFallback);
    }

    private async Task<int> ExecuteScopedTableDeleteOrCountAsync(
        string tableName,
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction? transaction,
        bool delete,
        bool allowGroupCharacterFallback)
    {
        if (!await TableExistsAsync(tableName))
            return 0;

        var columns = await GetTableColumnsAsync(tableName);
        if (columns.Contains("WorldId"))
            return await ExecuteWorldScopedTableAsync(tableName, target, transaction, delete);

        if (allowGroupCharacterFallback && columns.Contains("GroupId") && columns.Contains("CharacterId"))
            return await ExecuteGroupCharacterScopedTableAsync(tableName, target, transaction, delete);

        warnings.Add($"{tableName} 缺少安全删除所需字段，已跳过。");
        return 0;
    }

    private async Task<int> ExecuteCommonApiUsageDeleteOrCountAsync(
        AiTeamDeleteTarget target,
        List<string> warnings,
        SQLiteTransaction? transaction,
        bool delete)
    {
        if (!await TableExistsAsync("CommonApiUsageLog"))
            return 0;

        var columns = await GetTableColumnsAsync("CommonApiUsageLog");
        if (!columns.Contains("UserId") || !columns.Contains("GroupId") || !columns.Contains("TeamName"))
        {
            warnings.Add("CommonApiUsageLog 缺少 UserId/GroupId/TeamName，已跳过。");
            return 0;
        }

        using var cmd = _connection.CreateCommand();
        if (transaction != null)
            cmd.Transaction = transaction;

        var where = new List<string>
        {
            "UserId = @userId",
            "GroupId = @groupId",
            "TeamName = @teamName"
        };
        cmd.Parameters.AddWithValue("@userId", target.OwnerUserId);
        cmd.Parameters.AddWithValue("@groupId", target.GroupId);
        cmd.Parameters.AddWithValue("@teamName", target.TeamName);

        cmd.CommandText = delete
            ? $"DELETE FROM CommonApiUsageLog WHERE {string.Join(" AND ", where)}"
            : $"SELECT COUNT(1) FROM CommonApiUsageLog WHERE {string.Join(" AND ", where)}";

        if (delete)
            return await cmd.ExecuteNonQueryAsync();

        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    private async Task<int> ExecuteWorldScopedTableAsync(
        string tableName,
        AiTeamDeleteTarget target,
        SQLiteTransaction? transaction,
        bool delete)
    {
        if (target.WorldIds.Count == 0)
            return 0;

        using var cmd = _connection.CreateCommand();
        if (transaction != null)
            cmd.Transaction = transaction;

        var worldClause = BuildInClause(cmd, "WorldId", "@worldId", target.WorldIds);
        cmd.CommandText = delete
            ? $"DELETE FROM {tableName} WHERE {worldClause}"
            : $"SELECT COUNT(1) FROM {tableName} WHERE {worldClause}";

        if (delete)
            return await cmd.ExecuteNonQueryAsync();

        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    private async Task<int> ExecuteGroupCharacterScopedTableAsync(
        string tableName,
        AiTeamDeleteTarget target,
        SQLiteTransaction? transaction,
        bool delete)
    {
        if (target.CharacterIds.Count == 0)
            return 0;

        using var cmd = _connection.CreateCommand();
        if (transaction != null)
            cmd.Transaction = transaction;

        var where = new List<string>
        {
            "GroupId = @groupId",
            BuildInClause(cmd, "CharacterId", "@characterId", target.CharacterIds)
        };
        cmd.Parameters.AddWithValue("@groupId", target.GroupId);

        cmd.CommandText = delete
            ? $"DELETE FROM {tableName} WHERE {string.Join(" AND ", where)}"
            : $"SELECT COUNT(1) FROM {tableName} WHERE {string.Join(" AND ", where)}";

        if (delete)
            return await cmd.ExecuteNonQueryAsync();

        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    private async Task<HashSet<string>> GetTableColumnsAsync(string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var pragmaCmd = _connection.CreateCommand();
        pragmaCmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = await pragmaCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
                columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name = @tableName";
        cmd.Parameters.AddWithValue("@tableName", tableName);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value;
    }

    private static string BuildInClause<T>(
        SQLiteCommand cmd,
        string columnName,
        string paramPrefix,
        IReadOnlyList<T> values)
    {
        var placeholders = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            var name = $"{paramPrefix}{i}";
            placeholders.Add(name);
            var parameterValue = values[i];
            cmd.Parameters.AddWithValue(name, parameterValue is null ? (object)DBNull.Value : parameterValue);
        }

        return $"{columnName} IN ({string.Join(", ", placeholders)})";
    }

    private static string BuildLongTermMemoryWhereClause(
        SQLiteCommand cmd,
        AiTeamDeleteTarget target,
        HashSet<string> columns)
    {
        if (columns.Contains("WorldId") && target.WorldIds.Count > 0)
            return BuildInClause(cmd, "ltm.WorldId", "@ltmWorldId", target.WorldIds);

        if (columns.Contains("GroupId") && columns.Contains("CharacterId") && target.CharacterIds.Count > 0)
        {
            cmd.Parameters.AddWithValue("@ltmGroupId", target.GroupId);
            return $"ltm.GroupId = @ltmGroupId AND {BuildInClause(cmd, "ltm.CharacterId", "@ltmCharacterId", target.CharacterIds)}";
        }

        return string.Empty;
    }
}

public sealed record AiTeamDeleteTarget(
    long OwnerUserId,
    long GroupId,
    string TeamName,
    IReadOnlyList<string> WorldIds,
    IReadOnlyList<string> CharacterIds,
    IReadOnlyList<long> VirtualIds);

public sealed record AiTeamDeletePreview(
    AiTeamDeleteTarget Target,
    Dictionary<string, int> Counts,
    IReadOnlyList<string> Warnings);

public sealed record AiTeamDeleteResult(
    AiTeamDeleteTarget Target,
    Dictionary<string, int> Counts,
    IReadOnlyList<string> Warnings,
    bool Deleted);
