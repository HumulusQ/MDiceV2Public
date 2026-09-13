using AIMod.Trpg.SemanticGraph;

namespace AIMod.Trpg;

public partial class ChatDatabase
{
    private async Task EnsureInnerStateSchemaAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterInnerState (
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL,
                ThoughtText TEXT NOT NULL DEFAULT '',
                EmotionText TEXT NOT NULL DEFAULT '',
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY(WorldId, GroupId, CharacterId)
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<CharacterInnerState> GetCharacterInnerStateAsync(TrpgScope scope, string characterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT WorldId, GroupId, CharacterId, ThoughtText, EmotionText, UpdatedAt
            FROM CharacterInnerState
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return CharacterInnerState.Empty(scope, characterId);

        return new CharacterInnerState
        {
            WorldId = reader.GetString(0),
            GroupId = reader.GetInt64(1),
            CharacterId = reader.GetString(2),
            ThoughtText = reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3)) ? "无" : reader.GetString(3),
            EmotionText = reader.IsDBNull(4) || string.IsNullOrWhiteSpace(reader.GetString(4)) ? "无" : reader.GetString(4),
            UpdatedAt = reader.IsDBNull(5) || !DateTime.TryParse(reader.GetString(5), out var updatedAt) ? DateTime.UtcNow : updatedAt
        };
    }

    public async Task UpsertCharacterInnerStateAsync(TrpgScope scope, string characterId, string thoughtText, string emotionText)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CharacterInnerState (WorldId, GroupId, CharacterId, ThoughtText, EmotionText, UpdatedAt)
            VALUES (@worldId, @groupId, @characterId, @thoughtText, @emotionText, @updatedAt)
            ON CONFLICT(WorldId, GroupId, CharacterId)
            DO UPDATE SET ThoughtText = @thoughtText, EmotionText = @emotionText, UpdatedAt = @updatedAt
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@thoughtText", string.IsNullOrWhiteSpace(thoughtText) ? "无" : thoughtText.Trim());
        cmd.Parameters.AddWithValue("@emotionText", string.IsNullOrWhiteSpace(emotionText) ? "无" : emotionText.Trim());
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }
}
