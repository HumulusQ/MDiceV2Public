using System.Data.SQLite;
using AIMod.Trpg.SemanticGraph;

namespace AIMod.Trpg;

public partial class ChatDatabase
{
    private async Task EnsureSemanticGraphSchemaAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SemanticGraphNode (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL DEFAULT '',
                NodeKind TEXT NOT NULL,
                Text TEXT NOT NULL,
                Summary TEXT NOT NULL DEFAULT '',
                Importance REAL NOT NULL DEFAULT 0,
                AssignedImportance REAL NOT NULL DEFAULT 0,
                SourceScope TEXT NOT NULL DEFAULT '',
                SourceMessageIds TEXT NOT NULL DEFAULT '[]',
                RawExcerpt TEXT NOT NULL DEFAULT '[]',
                ContentHash TEXT NOT NULL DEFAULT '',
                Metadata TEXT NOT NULL DEFAULT '{}',
                CreatedAt TEXT NOT NULL,
                LastActivatedAt TEXT NULL,
                ActivationCount INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_sgn_world_group_kind_text
                ON SemanticGraphNode(WorldId, GroupId, NodeKind, Text);

            CREATE INDEX IF NOT EXISTS idx_sgn_world_group_char_kind
                ON SemanticGraphNode(WorldId, GroupId, CharacterId, NodeKind, IsDeleted);

            CREATE INDEX IF NOT EXISTS idx_sgn_importance
                ON SemanticGraphNode(WorldId, GroupId, Importance DESC);

            CREATE TABLE IF NOT EXISTS SemanticGraphEdge (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL DEFAULT '',
                SourceNodeId INTEGER NOT NULL,
                TargetNodeId INTEGER NOT NULL,
                EdgeKind TEXT NOT NULL,
                Weight REAL NOT NULL DEFAULT 1.0,
                Evidence TEXT NOT NULL DEFAULT '',
                SourceMessageIds TEXT NOT NULL DEFAULT '[]',
                Metadata TEXT NOT NULL DEFAULT '{}',
                CreatedAt TEXT NOT NULL,
                LastReinforcedAt TEXT NULL,
                ReinforceCount INTEGER NOT NULL DEFAULT 0,
                UNIQUE(WorldId, GroupId, CharacterId, SourceNodeId, TargetNodeId, EdgeKind)
            );

            CREATE INDEX IF NOT EXISTS idx_sge_source
                ON SemanticGraphEdge(WorldId, GroupId, CharacterId, SourceNodeId);

            CREATE INDEX IF NOT EXISTS idx_sge_target
                ON SemanticGraphEdge(WorldId, GroupId, CharacterId, TargetNodeId);

            CREATE INDEX IF NOT EXISTS idx_sge_kind
                ON SemanticGraphEdge(WorldId, GroupId, CharacterId, EdgeKind);

            CREATE TABLE IF NOT EXISTS SemanticTokenStats (
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                TokenText TEXT NOT NULL,
                NodeCount INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY(WorldId, GroupId, TokenText)
            );

            CREATE INDEX IF NOT EXISTS idx_token_stats_token
                ON SemanticTokenStats(WorldId, GroupId, TokenText);

            CREATE TABLE IF NOT EXISTS SemanticGraphMeta (
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                Key TEXT NOT NULL,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY(WorldId, GroupId, Key)
            );
            """;
        await cmd.ExecuteNonQueryAsync();

        var hasContentHash = false;
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(SemanticGraphNode);";
            using var reader = await pragma.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader["name"]?.ToString(), "ContentHash", StringComparison.OrdinalIgnoreCase))
                {
                    hasContentHash = true;
                    break;
                }
            }
        }

        if (!hasContentHash)
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = """
                ALTER TABLE SemanticGraphNode
                ADD COLUMN ContentHash TEXT NOT NULL DEFAULT '';
                """;
            await alter.ExecuteNonQueryAsync();
        }

        using var createIndex = _connection.CreateCommand();
        createIndex.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_sgn_memory_content_hash
                ON SemanticGraphNode(WorldId, GroupId, CharacterId, NodeKind, ContentHash)
                WHERE NodeKind = 'memory' AND IsDeleted = 0 AND ContentHash <> '';
            """;
        await createIndex.ExecuteNonQueryAsync();
    }

    public async Task<long> UpsertSemanticGraphNodeAsync(TrpgScope scope, SemanticGraphNode node)
    {
        if (node.NodeKind == SemanticGraphNodeKind.Memory && !string.IsNullOrWhiteSpace(node.ContentHash))
        {
            var existingId = await FindSemanticMemoryNodeIdByHashAsync(scope, node.CharacterId, node.ContentHash);
            if (existingId > 0)
            {
                using var update = _connection.CreateCommand();
                update.CommandText = """
                    UPDATE SemanticGraphNode
                    SET Text = CASE WHEN LENGTH(@text) > LENGTH(Text) THEN @text ELSE Text END,
                        Summary = CASE WHEN LENGTH(@summary) > LENGTH(Summary) THEN @summary ELSE Summary END,
                        Importance = MAX(Importance, @importance),
                        AssignedImportance = MAX(AssignedImportance, @assignedImportance),
                        SourceScope = CASE WHEN SourceScope = '' THEN @sourceScope ELSE SourceScope END,
                        SourceMessageIds = CASE WHEN SourceMessageIds = '[]' THEN @sourceMessageIds ELSE SourceMessageIds END,
                        RawExcerpt = CASE WHEN RawExcerpt = '[]' THEN @rawExcerpt ELSE RawExcerpt END,
                        Metadata = CASE WHEN Metadata = '{}' THEN @metadata ELSE Metadata END,
                        LastActivatedAt = @lastActivatedAt,
                        ActivationCount = ActivationCount + 1,
                        IsDeleted = 0
                    WHERE Id = @id
                    """;
                update.Parameters.AddWithValue("@id", existingId);
                update.Parameters.AddWithValue("@text", node.Text ?? "");
                update.Parameters.AddWithValue("@summary", node.Summary ?? "");
                update.Parameters.AddWithValue("@importance", node.Importance);
                update.Parameters.AddWithValue("@assignedImportance", node.AssignedImportance);
                update.Parameters.AddWithValue("@sourceScope", node.SourceScope ?? "");
                update.Parameters.AddWithValue("@sourceMessageIds", node.SourceMessageIds ?? "[]");
                update.Parameters.AddWithValue("@rawExcerpt", node.RawExcerpt ?? "[]");
                update.Parameters.AddWithValue("@metadata", node.Metadata ?? "{}");
                update.Parameters.AddWithValue("@lastActivatedAt", DateTime.UtcNow.ToString("O"));
                await update.ExecuteNonQueryAsync();
                return existingId;
            }
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SemanticGraphNode (
                WorldId, GroupId, CharacterId, NodeKind, Text, Summary, Importance, AssignedImportance,
                SourceScope, SourceMessageIds, RawExcerpt, ContentHash, Metadata, CreatedAt, LastActivatedAt,
                ActivationCount, IsDeleted)
            VALUES (
                @worldId, @groupId, @characterId, @nodeKind, @text, @summary, @importance, @assignedImportance,
                @sourceScope, @sourceMessageIds, @rawExcerpt, @contentHash, @metadata, @createdAt, @lastActivatedAt,
                @activationCount, @isDeleted);
            SELECT last_insert_rowid();
            """;
        AddNodeParameters(cmd, scope, node);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<long> UpsertSemanticSurfaceNodeAsync(TrpgScope scope, string nodeKind, string text, string characterId = "")
    {
        text = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        using (var find = _connection.CreateCommand())
        {
            find.CommandText = """
                SELECT Id FROM SemanticGraphNode
                WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
                  AND NodeKind = @nodeKind AND Text = @text AND IsDeleted = 0
                LIMIT 1
                """;
            find.Parameters.AddWithValue("@worldId", scope.WorldId);
            find.Parameters.AddWithValue("@groupId", scope.GroupId);
            find.Parameters.AddWithValue("@characterId", characterId ?? "");
            find.Parameters.AddWithValue("@nodeKind", nodeKind);
            find.Parameters.AddWithValue("@text", text);
            var existing = await find.ExecuteScalarAsync();
            if (existing != null && existing != DBNull.Value)
                return Convert.ToInt64(existing);
        }

        return await UpsertSemanticGraphNodeAsync(scope, new SemanticGraphNode
        {
            CharacterId = characterId ?? "",
            NodeKind = nodeKind,
            Text = text,
            Summary = "",
            CreatedAt = DateTime.UtcNow
        });
    }

    public async Task<long> FindSemanticMemoryNodeIdByHashAsync(TrpgScope scope, string characterId, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
            return 0;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id
            FROM SemanticGraphNode
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
              AND NodeKind = 'memory' AND IsDeleted = 0 AND ContentHash = @contentHash
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@contentHash", contentHash);
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
    }

    public async Task UpsertSemanticGraphEdgeAsync(
        TrpgScope scope,
        long sourceId,
        long targetId,
        string edgeKind,
        double weight,
        string evidence,
        string characterId = "")
    {
        if (sourceId <= 0 || targetId <= 0 || sourceId == targetId || string.IsNullOrWhiteSpace(edgeKind))
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SemanticGraphEdge (
                WorldId, GroupId, CharacterId, SourceNodeId, TargetNodeId, EdgeKind, Weight,
                Evidence, SourceMessageIds, Metadata, CreatedAt, LastReinforcedAt, ReinforceCount)
            VALUES (
                @worldId, @groupId, @characterId, @sourceNodeId, @targetNodeId, @edgeKind, @weight,
                @evidence, '[]', '{}', @createdAt, NULL, 0)
            ON CONFLICT(WorldId, GroupId, CharacterId, SourceNodeId, TargetNodeId, EdgeKind)
            DO UPDATE SET
                Weight = MIN(1.0, Weight + @reinforceDelta),
                LastReinforcedAt = @createdAt,
                ReinforceCount = ReinforceCount + 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@sourceNodeId", sourceId);
        cmd.Parameters.AddWithValue("@targetNodeId", targetId);
        cmd.Parameters.AddWithValue("@edgeKind", edgeKind);
        cmd.Parameters.AddWithValue("@weight", Math.Clamp(weight, 0, 1));
        cmd.Parameters.AddWithValue("@reinforceDelta", Math.Clamp(weight * 0.2, 0.01, 0.2));
        cmd.Parameters.AddWithValue("@evidence", evidence ?? "");
        cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<SemanticGraphNode>> FindSemanticSurfaceNodesAsync(TrpgScope scope, IEnumerable<string> texts, IEnumerable<string> kinds, string characterId = "")
    {
        var textList = texts.Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        var kindList = kinds.Select(k => (k ?? "").Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (textList.Count == 0 || kindList.Count == 0)
            return new List<SemanticGraphNode>();

        var textParams = string.Join(",", textList.Select((_, i) => $"@text{i}"));
        var kindParams = string.Join(",", kindList.Select((_, i) => $"@kind{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, WorldId, GroupId, CharacterId, NodeKind, Text, Summary, Importance, AssignedImportance,
                   SourceScope, SourceMessageIds, RawExcerpt, ContentHash, Metadata, CreatedAt, LastActivatedAt,
                   ActivationCount, IsDeleted
            FROM SemanticGraphNode
            WHERE WorldId = @worldId AND GroupId = @groupId AND IsDeleted = 0
              AND CharacterId IN ('', @characterId)
              AND NodeKind IN ({kindParams})
              AND Text IN ({textParams})
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        for (var i = 0; i < kindList.Count; i++) cmd.Parameters.AddWithValue($"@kind{i}", kindList[i]);
        for (var i = 0; i < textList.Count; i++) cmd.Parameters.AddWithValue($"@text{i}", textList[i]);
        return await ReadSemanticNodesAsync(cmd);
    }

    public async Task<List<SemanticGraphNode>> SearchSemanticMemoryNodesAsync(TrpgScope scope, IEnumerable<string> terms, string characterId, int limit)
    {
        var termList = terms.Select(t => (t ?? "").Trim()).Where(t => t.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (termList.Count == 0)
            return new List<SemanticGraphNode>();

        var predicates = string.Join(" OR ", termList.Select((_, i) => $"Summary LIKE @like{i} OR Text LIKE @like{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, WorldId, GroupId, CharacterId, NodeKind, Text, Summary, Importance, AssignedImportance,
                   SourceScope, SourceMessageIds, RawExcerpt, ContentHash, Metadata, CreatedAt, LastActivatedAt,
                   ActivationCount, IsDeleted
            FROM SemanticGraphNode
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId IN ('', @characterId)
              AND NodeKind = 'memory' AND IsDeleted = 0
              AND ({predicates})
            ORDER BY Importance DESC, CreatedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@limit", limit);
        for (var i = 0; i < termList.Count; i++) cmd.Parameters.AddWithValue($"@like{i}", $"%{termList[i]}%");
        return await ReadSemanticNodesAsync(cmd);
    }

    public async Task<List<SemanticGraphNode>> GetSemanticNodesByIdsAsync(TrpgScope scope, IEnumerable<long> ids)
    {
        var idList = ids.Where(id => id > 0).Distinct().Take(100).ToList();
        if (idList.Count == 0)
            return new List<SemanticGraphNode>();

        var idParams = string.Join(",", idList.Select((_, i) => $"@id{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, WorldId, GroupId, CharacterId, NodeKind, Text, Summary, Importance, AssignedImportance,
                   SourceScope, SourceMessageIds, RawExcerpt, ContentHash, Metadata, CreatedAt, LastActivatedAt,
                   ActivationCount, IsDeleted
            FROM SemanticGraphNode
            WHERE WorldId = @worldId AND GroupId = @groupId AND IsDeleted = 0 AND Id IN ({idParams})
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        for (var i = 0; i < idList.Count; i++) cmd.Parameters.AddWithValue($"@id{i}", idList[i]);
        return await ReadSemanticNodesAsync(cmd);
    }

    public async Task<List<SemanticGraphEdge>> GetSemanticOutgoingEdgesAsync(TrpgScope scope, long sourceNodeId, string characterId, int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, SourceNodeId, TargetNodeId, EdgeKind, Weight,
                   Evidence, SourceMessageIds, Metadata, CreatedAt, LastReinforcedAt, ReinforceCount
            FROM SemanticGraphEdge
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId IN ('', @characterId)
              AND SourceNodeId = @sourceNodeId
            ORDER BY Weight DESC, ReinforceCount DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@sourceNodeId", sourceNodeId);
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadSemanticEdgesAsync(cmd);
    }

    public async Task<List<SemanticGraphEdge>> GetSemanticIncomingEdgesAsync(TrpgScope scope, long targetNodeId, string characterId, int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, SourceNodeId, TargetNodeId, EdgeKind, Weight,
                   Evidence, SourceMessageIds, Metadata, CreatedAt, LastReinforcedAt, ReinforceCount
            FROM SemanticGraphEdge
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId IN ('', @characterId)
              AND TargetNodeId = @targetNodeId
            ORDER BY Weight DESC, ReinforceCount DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@targetNodeId", targetNodeId);
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadSemanticEdgesAsync(cmd);
    }

    public async Task<Dictionary<string, int>> GetSemanticTokenNodeCountsAsync(TrpgScope scope, IEnumerable<string> tokens)
    {
        var tokenList = tokens.Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToList();
        var result = tokenList.ToDictionary(t => t, _ => 0, StringComparer.OrdinalIgnoreCase);
        if (tokenList.Count == 0)
            return result;

        var tokenParams = string.Join(",", tokenList.Select((_, i) => $"@token{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT TokenText, NodeCount
            FROM SemanticTokenStats
            WHERE WorldId = @worldId AND GroupId = @groupId AND TokenText IN ({tokenParams})
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        for (var i = 0; i < tokenList.Count; i++) cmd.Parameters.AddWithValue($"@token{i}", tokenList[i]);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    public async Task IncrementSemanticTokenStatsAsync(TrpgScope scope, IEnumerable<string> tokens)
    {
        var now = DateTime.UtcNow.ToString("O");
        foreach (var token in tokens.Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SemanticTokenStats (WorldId, GroupId, TokenText, NodeCount, UpdatedAt)
                VALUES (@worldId, @groupId, @token, 1, @now)
                ON CONFLICT(WorldId, GroupId, TokenText)
                DO UPDATE SET NodeCount = NodeCount + 1, UpdatedAt = @now
                """;
            cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
            cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
            cmd.Parameters.AddWithValue("@token", token);
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task ReplaceSemanticTokenStatsAsync(TrpgScope scope, IReadOnlyDictionary<string, int> tokenCounts)
    {
        using var tx = _connection.BeginTransaction();
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM SemanticTokenStats
                WHERE WorldId = @worldId AND GroupId = @groupId
                """;
            delete.Parameters.AddWithValue("@worldId", scope.WorldId);
            delete.Parameters.AddWithValue("@groupId", scope.GroupId);
            await delete.ExecuteNonQueryAsync();
        }

        var now = DateTime.UtcNow.ToString("O");
        foreach (var pair in tokenCounts.Where(pair => pair.Value > 0))
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO SemanticTokenStats (WorldId, GroupId, TokenText, NodeCount, UpdatedAt)
                VALUES (@worldId, @groupId, @tokenText, @nodeCount, @updatedAt)
                """;
            insert.Parameters.AddWithValue("@worldId", scope.WorldId);
            insert.Parameters.AddWithValue("@groupId", scope.GroupId);
            insert.Parameters.AddWithValue("@tokenText", pair.Key);
            insert.Parameters.AddWithValue("@nodeCount", pair.Value);
            insert.Parameters.AddWithValue("@updatedAt", now);
            await insert.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public async Task<double> GetSemanticKillFloorAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM SemanticGraphMeta WHERE WorldId = @worldId AND GroupId = @groupId AND Key = 'KillFloor'";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        var value = (await cmd.ExecuteScalarAsync())?.ToString();
        return double.TryParse(value, out var floor) ? floor : 0;
    }

    public async Task SetSemanticKillFloorAsync(TrpgScope scope, double value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SemanticGraphMeta (WorldId, GroupId, Key, Value, UpdatedAt)
            VALUES (@worldId, @groupId, 'KillFloor', @value, @now)
            ON CONFLICT(WorldId, GroupId, Key)
            DO UPDATE SET Value = @value, UpdatedAt = @now
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@value", value.ToString("R"));
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> PruneSemanticGraphBelowKillFloorAsync(TrpgScope scope)
    {
        var floor = await GetSemanticKillFloorAsync(scope);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE SemanticGraphNode
            SET IsDeleted = 1
            WHERE WorldId = @worldId AND GroupId = @groupId
              AND NodeKind = 'memory' AND IsDeleted = 0 AND Importance < @floor
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@floor", floor);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> DeleteSemanticEdgesAttachedToDeletedNodesAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM SemanticGraphEdge
            WHERE WorldId = @worldId AND GroupId = @groupId
              AND (SourceNodeId IN (
                    SELECT Id FROM SemanticGraphNode
                    WHERE WorldId = @worldId AND GroupId = @groupId AND IsDeleted = 1
                  )
               OR TargetNodeId IN (
                    SELECT Id FROM SemanticGraphNode
                    WHERE WorldId = @worldId AND GroupId = @groupId AND IsDeleted = 1
                  ))
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> DeleteSemanticOrphanSurfaceNodesAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM SemanticGraphNode
            WHERE WorldId = @worldId AND GroupId = @groupId
              AND IsDeleted = 0
              AND NodeKind IN ('token', 'name', 'topic', 'scene', 'entity_anchor')
              AND Id NOT IN (
                  SELECT DISTINCT SourceNodeId FROM SemanticGraphEdge WHERE WorldId = @worldId AND GroupId = @groupId
                  UNION
                  SELECT DISTINCT TargetNodeId FROM SemanticGraphEdge WHERE WorldId = @worldId AND GroupId = @groupId
              )
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task RebuildSemanticTokenStatsAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT surface.Text, COUNT(DISTINCT edge.SourceNodeId) AS MemoryCount
            FROM SemanticGraphNode surface
            JOIN SemanticGraphEdge edge
              ON edge.WorldId = surface.WorldId
             AND edge.GroupId = surface.GroupId
             AND edge.TargetNodeId = surface.Id
            JOIN SemanticGraphNode memory
              ON memory.Id = edge.SourceNodeId
             AND memory.WorldId = surface.WorldId
             AND memory.GroupId = surface.GroupId
            WHERE surface.WorldId = @worldId AND surface.GroupId = @groupId
              AND surface.IsDeleted = 0
              AND memory.IsDeleted = 0
              AND surface.NodeKind IN ('token', 'name', 'topic', 'scene', 'entity_anchor')
              AND memory.NodeKind = 'memory'
            GROUP BY surface.Text
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            counts[reader.GetString(0)] = reader.GetInt32(1);

        await ReplaceSemanticTokenStatsAsync(scope, counts);
    }

    private static void AddNodeParameters(SQLiteCommand cmd, TrpgScope scope, SemanticGraphNode node)
    {
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", node.CharacterId ?? "");
        cmd.Parameters.AddWithValue("@nodeKind", node.NodeKind);
        cmd.Parameters.AddWithValue("@text", node.Text ?? "");
        cmd.Parameters.AddWithValue("@summary", node.Summary ?? "");
        cmd.Parameters.AddWithValue("@importance", node.Importance);
        cmd.Parameters.AddWithValue("@assignedImportance", node.AssignedImportance);
        cmd.Parameters.AddWithValue("@sourceScope", node.SourceScope ?? "");
        cmd.Parameters.AddWithValue("@sourceMessageIds", node.SourceMessageIds ?? "[]");
        cmd.Parameters.AddWithValue("@rawExcerpt", node.RawExcerpt ?? "[]");
        cmd.Parameters.AddWithValue("@contentHash", node.ContentHash ?? "");
        cmd.Parameters.AddWithValue("@metadata", node.Metadata ?? "{}");
        cmd.Parameters.AddWithValue("@createdAt", node.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@lastActivatedAt", node.LastActivatedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@activationCount", node.ActivationCount);
        cmd.Parameters.AddWithValue("@isDeleted", node.IsDeleted ? 1 : 0);
    }

    private static async Task<List<SemanticGraphNode>> ReadSemanticNodesAsync(SQLiteCommand cmd)
    {
        var nodes = new List<SemanticGraphNode>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new SemanticGraphNode
            {
                Id = reader.GetInt64(0),
                WorldId = reader.GetString(1),
                GroupId = reader.GetInt64(2),
                CharacterId = reader.IsDBNull(3) ? "" : reader.GetString(3),
                NodeKind = reader.GetString(4),
                Text = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Summary = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Importance = reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                AssignedImportance = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                SourceScope = reader.IsDBNull(9) ? "" : reader.GetString(9),
                SourceMessageIds = reader.IsDBNull(10) ? "[]" : reader.GetString(10),
                RawExcerpt = reader.IsDBNull(11) ? "[]" : reader.GetString(11),
                ContentHash = reader.IsDBNull(12) ? "" : reader.GetString(12),
                Metadata = reader.IsDBNull(13) ? "{}" : reader.GetString(13),
                CreatedAt = DateTime.TryParse(reader.GetString(14), out var createdAt) ? createdAt : DateTime.UtcNow,
                LastActivatedAt = reader.IsDBNull(15) || !DateTime.TryParse(reader.GetString(15), out var activatedAt) ? null : activatedAt,
                ActivationCount = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                IsDeleted = !reader.IsDBNull(17) && reader.GetInt32(17) != 0
            });
        }

        return nodes;
    }

    private static async Task<List<SemanticGraphEdge>> ReadSemanticEdgesAsync(SQLiteCommand cmd)
    {
        var edges = new List<SemanticGraphEdge>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            edges.Add(new SemanticGraphEdge
            {
                Id = reader.GetInt64(0),
                WorldId = reader.GetString(1),
                GroupId = reader.GetInt64(2),
                CharacterId = reader.IsDBNull(3) ? "" : reader.GetString(3),
                SourceNodeId = reader.GetInt64(4),
                TargetNodeId = reader.GetInt64(5),
                EdgeKind = reader.GetString(6),
                Weight = reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                Evidence = reader.IsDBNull(8) ? "" : reader.GetString(8),
                SourceMessageIds = reader.IsDBNull(9) ? "[]" : reader.GetString(9),
                Metadata = reader.IsDBNull(10) ? "{}" : reader.GetString(10),
                CreatedAt = DateTime.TryParse(reader.GetString(11), out var createdAt) ? createdAt : DateTime.UtcNow,
                LastReinforcedAt = reader.IsDBNull(12) || !DateTime.TryParse(reader.GetString(12), out var reinforcedAt) ? null : reinforcedAt,
                ReinforceCount = reader.IsDBNull(13) ? 0 : reader.GetInt32(13)
            });
        }

        return edges;
    }
}
