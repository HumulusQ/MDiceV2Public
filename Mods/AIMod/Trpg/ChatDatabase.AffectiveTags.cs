using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIMod.Trpg;

public partial class ChatDatabase
{
    public async Task<AffectiveTagState?> FindAffectiveTagStateAsync(
        TrpgScope scope,
        string characterId,
        string tagType,
        string sourceKey,
        string? targetEntityId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, TagType, DisplayName, SourceKey, TargetEntityId, IntensityTier,
                   Charge, ChargeCap, RepetitionCount, AdaptationLevel, Status, LastEvidence,
                   CreatedAt, UpdatedAt, LastAppliedFoldCount, ExpirePolicy, Metadata
            FROM AffectiveTagState
            WHERE WorldId = @worldId
              AND GroupId = @groupId
              AND CharacterId = @characterId
              AND TagType = @tagType
              AND SourceKey = @sourceKey
              AND IFNULL(TargetEntityId, '') = IFNULL(@targetEntityId, '')
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@tagType", tagType);
        cmd.Parameters.AddWithValue("@sourceKey", sourceKey);
        cmd.Parameters.AddWithValue("@targetEntityId", targetEntityId ?? "");

        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapAffectiveTagState(reader) : null;
    }

    public async Task<List<AffectiveTagState>> GetActiveAffectiveTagStatesAsync(TrpgScope scope, string characterId, int limit = 8)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, TagType, DisplayName, SourceKey, TargetEntityId, IntensityTier,
                   Charge, ChargeCap, RepetitionCount, AdaptationLevel, Status, LastEvidence,
                   CreatedAt, UpdatedAt, LastAppliedFoldCount, ExpirePolicy, Metadata
            FROM AffectiveTagState
            WHERE WorldId = @worldId
              AND GroupId = @groupId
              AND CharacterId = @characterId
              AND Status IN ('Active', 'Fading')
            ORDER BY Charge DESC, UpdatedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<AffectiveTagState>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(MapAffectiveTagState(reader));
        return results;
    }

    public async Task<List<AffectiveTagState>> GetAffectiveTagStatesAsync(TrpgScope scope, string characterId, int limit = 64)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, TagType, DisplayName, SourceKey, TargetEntityId, IntensityTier,
                   Charge, ChargeCap, RepetitionCount, AdaptationLevel, Status, LastEvidence,
                   CreatedAt, UpdatedAt, LastAppliedFoldCount, ExpirePolicy, Metadata
            FROM AffectiveTagState
            WHERE WorldId = @worldId
              AND GroupId = @groupId
              AND CharacterId = @characterId
            ORDER BY Status = 'Expired' ASC, Charge DESC, UpdatedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<AffectiveTagState>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(MapAffectiveTagState(reader));
        return results;
    }

    public async Task UpsertAffectiveTagStateAsync(TrpgScope scope, AffectiveTagState state)
    {
        state.WorldId = scope.WorldId;
        state.GroupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AffectiveTagState
                (WorldId, GroupId, CharacterId, TagType, DisplayName, SourceKey, TargetEntityId, IntensityTier,
                 Charge, ChargeCap, RepetitionCount, AdaptationLevel, Status, LastEvidence,
                 CreatedAt, UpdatedAt, LastAppliedFoldCount, ExpirePolicy, Metadata)
            VALUES
                (@worldId, @groupId, @characterId, @tagType, @displayName, @sourceKey, @targetEntityId, @intensityTier,
                 @charge, @chargeCap, @repetitionCount, @adaptationLevel, @status, @lastEvidence,
                 @createdAt, @updatedAt, @lastAppliedFoldCount, @expirePolicy, @metadata)
            ON CONFLICT(WorldId, GroupId, CharacterId, TagType, SourceKey, TargetEntityId) DO UPDATE SET
                DisplayName = @displayName,
                IntensityTier = @intensityTier,
                Charge = @charge,
                ChargeCap = @chargeCap,
                RepetitionCount = @repetitionCount,
                AdaptationLevel = @adaptationLevel,
                Status = @status,
                LastEvidence = @lastEvidence,
                UpdatedAt = @updatedAt,
                LastAppliedFoldCount = @lastAppliedFoldCount,
                ExpirePolicy = @expirePolicy,
                Metadata = @metadata
            """;
        cmd.Parameters.AddWithValue("@worldId", state.WorldId);
        cmd.Parameters.AddWithValue("@groupId", state.GroupId);
        cmd.Parameters.AddWithValue("@characterId", state.CharacterId);
        cmd.Parameters.AddWithValue("@tagType", state.TagType);
        cmd.Parameters.AddWithValue("@displayName", state.DisplayName);
        cmd.Parameters.AddWithValue("@sourceKey", state.SourceKey);
        cmd.Parameters.AddWithValue("@targetEntityId", state.TargetEntityId ?? "");
        cmd.Parameters.AddWithValue("@intensityTier", state.IntensityTier);
        cmd.Parameters.AddWithValue("@charge", state.Charge);
        cmd.Parameters.AddWithValue("@chargeCap", state.ChargeCap);
        cmd.Parameters.AddWithValue("@repetitionCount", state.RepetitionCount);
        cmd.Parameters.AddWithValue("@adaptationLevel", state.AdaptationLevel);
        cmd.Parameters.AddWithValue("@status", state.Status);
        cmd.Parameters.AddWithValue("@lastEvidence", state.LastEvidence ?? "");
        cmd.Parameters.AddWithValue("@createdAt", state.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updatedAt", state.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@lastAppliedFoldCount", state.LastAppliedFoldCount);
        cmd.Parameters.AddWithValue("@expirePolicy", state.ExpirePolicy);
        cmd.Parameters.AddWithValue("@metadata", state.Metadata ?? "{}");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateAffectiveTagStateAsync(TrpgScope scope, AffectiveTagState state)
    {
        await UpsertAffectiveTagStateAsync(scope, state);
    }

    public async Task InsertAffectiveTagEventAsync(TrpgScope scope, AffectiveTagEvent tagEvent)
    {
        tagEvent.WorldId = scope.WorldId;
        tagEvent.GroupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AffectiveTagEvent
                (WorldId, GroupId, CharacterId, SourceEventId, TagType, DisplayName, SourceKey, TargetEntityId,
                 EffectKind, IntensityTier, Novelty, Evidence, FoldCount, Metadata)
            VALUES
                (@worldId, @groupId, @characterId, @sourceEventId, @tagType, @displayName, @sourceKey, @targetEntityId,
                 @effectKind, @intensityTier, @novelty, @evidence, @foldCount, @metadata)
            """;
        cmd.Parameters.AddWithValue("@worldId", tagEvent.WorldId);
        cmd.Parameters.AddWithValue("@groupId", tagEvent.GroupId);
        cmd.Parameters.AddWithValue("@characterId", tagEvent.CharacterId);
        cmd.Parameters.AddWithValue("@sourceEventId", tagEvent.SourceEventId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@tagType", tagEvent.TagType);
        cmd.Parameters.AddWithValue("@displayName", tagEvent.DisplayName);
        cmd.Parameters.AddWithValue("@sourceKey", tagEvent.SourceKey);
        cmd.Parameters.AddWithValue("@targetEntityId", tagEvent.TargetEntityId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@effectKind", tagEvent.EffectKind);
        cmd.Parameters.AddWithValue("@intensityTier", tagEvent.IntensityTier);
        cmd.Parameters.AddWithValue("@novelty", tagEvent.Novelty);
        cmd.Parameters.AddWithValue("@evidence", tagEvent.Evidence ?? "");
        cmd.Parameters.AddWithValue("@foldCount", tagEvent.FoldCount);
        cmd.Parameters.AddWithValue("@metadata", tagEvent.Metadata ?? "{}");
        await cmd.ExecuteNonQueryAsync();
    }

    private static AffectiveTagState MapAffectiveTagState(DbDataReader reader)
    {
        return new AffectiveTagState
        {
            Id = reader.GetInt64(0),
            WorldId = reader.GetString(1),
            GroupId = reader.GetInt64(2),
            CharacterId = reader.GetString(3),
            TagType = reader.GetString(4),
            DisplayName = reader.GetString(5),
            SourceKey = reader.GetString(6),
            TargetEntityId = reader.IsDBNull(7) || string.IsNullOrWhiteSpace(reader.GetString(7)) ? null : reader.GetString(7),
            IntensityTier = reader.GetString(8),
            Charge = reader.GetDouble(9),
            ChargeCap = reader.GetDouble(10),
            RepetitionCount = reader.GetInt32(11),
            AdaptationLevel = reader.GetDouble(12),
            Status = reader.GetString(13),
            LastEvidence = reader.GetString(14),
            CreatedAt = DateTime.Parse(reader.GetString(15)),
            UpdatedAt = DateTime.Parse(reader.GetString(16)),
            LastAppliedFoldCount = reader.GetInt32(17),
            ExpirePolicy = reader.GetString(18),
            Metadata = reader.GetString(19)
        };
    }
}
