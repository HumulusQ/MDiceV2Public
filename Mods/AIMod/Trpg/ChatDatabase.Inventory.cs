using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

public partial class ChatDatabase
{
    private static readonly HashSet<string> ActiveInventoryStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "carried", "equipped", "stored", "unknown"
    };

    private static readonly HashSet<string> InactiveInventoryOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "consume", "drop", "transfer", "loss"
    };

    public async Task<bool> HasInitialInventoryImportedAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM CharacterInventorySeedState
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        return await cmd.ExecuteScalarAsync() != null;
    }

    public async Task EnsureInitialInventoryImportedAsync(TrpgScope scope, AiCharacterEntry aiChar)
    {
        var groupId = scope.GroupId;
        if (await HasInitialInventoryImportedAsync(scope, aiChar.CharacterId))
            return;

        var seedJson = aiChar.InitialInventoryJson ?? "[]";
        var seedHash = ComputeSeedHash(seedJson);
        var seedItems = ParseInitialInventorySeed(scope, aiChar.CharacterId, seedJson);
        var sourceEventId = await AppendInventorySeedWorldEventAsync(scope, aiChar.CharacterId, seedItems);

        foreach (var item in seedItems)
        {
            item.SourceEventId = sourceEventId;
            item.LastEventId = sourceEventId;
            await UpsertInventoryItemAsync(scope, item);
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO CharacterInventorySeedState (WorldId, GroupId, CharacterId, SeedHash, ImportedAt, SourceEventId)
            VALUES (@worldId, @groupId, @characterId, @seedHash, CURRENT_TIMESTAMP, @sourceEventId)
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", aiChar.CharacterId);
        cmd.Parameters.AddWithValue("@seedHash", seedHash);
        cmd.Parameters.AddWithValue("@sourceEventId", sourceEventId.HasValue ? sourceEventId.Value : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long?> AppendInventorySeedWorldEventAsync(TrpgScope scope, string characterId, IReadOnlyList<CharacterInventoryItem> items)
    {
        if (items.Count == 0)
            return null;

        try
        {
            var evt = new WorldEvent
            {
                EventType = "inventory_seed",
                Actors = new List<string> { characterId },
                Result = $"initial inventory seed: {items.Count} item(s)",
                SourceEntityId = characterId,
                Payload = new Dictionary<string, object>
                {
                    ["character_id"] = characterId,
                    ["items"] = items.Select(item => new Dictionary<string, object>
                    {
                        ["item_key"] = item.ItemKey,
                        ["display_name"] = item.DisplayName,
                        ["quantity"] = item.Quantity,
                        ["unit"] = item.Unit ?? "",
                        ["state"] = item.State
                    }).ToList(),
                    ["source"] = "InitialInventoryJson"
                },
                Timestamp = DateTime.UtcNow
            };

            return await InsertEventLogAsync(scope, evt);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] inventory seed event append skipped: {ex.Message}");
            return null;
        }
    }

    public async Task UpsertInventoryItemAsync(TrpgScope scope, CharacterInventoryItem item)
    {
        item.WorldId = scope.WorldId;
        item.GroupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CharacterInventoryItem (
                WorldId, GroupId, CharacterId, ItemKey, DisplayName, Quantity, Unit, State,
                Description, LocationHint, OwnerEntityId,
                SourceKind, AuthorityRank, Confidence,
                IsAssumed, IsContradicted, NeedsReview,
                SourceEventId, LastEventId, LastEvidence,
                IsVisibleToCharacter, IsActive, Metadata, CreatedAt, UpdatedAt)
            VALUES (
                @worldId, @groupId, @characterId, @itemKey, @displayName, @quantity, @unit, @state,
                @description, @locationHint, @ownerEntityId,
                @sourceKind, @authorityRank, @confidence,
                @isAssumed, @isContradicted, @needsReview,
                @sourceEventId, @lastEventId, @lastEvidence,
                @isVisibleToCharacter, @isActive, @metadata, @createdAt, @updatedAt)
            ON CONFLICT(WorldId, GroupId, CharacterId, ItemKey) DO UPDATE SET
                DisplayName = @displayName,
                Quantity = @quantity,
                Unit = @unit,
                State = @state,
                Description = @description,
                LocationHint = @locationHint,
                OwnerEntityId = @ownerEntityId,
                SourceKind = @sourceKind,
                AuthorityRank = @authorityRank,
                Confidence = @confidence,
                IsAssumed = @isAssumed,
                IsContradicted = @isContradicted,
                NeedsReview = @needsReview,
                SourceEventId = COALESCE(CharacterInventoryItem.SourceEventId, @sourceEventId),
                LastEventId = @lastEventId,
                LastEvidence = @lastEvidence,
                IsVisibleToCharacter = @isVisibleToCharacter,
                IsActive = @isActive,
                Metadata = @metadata,
                UpdatedAt = @updatedAt
            """;
        AddInventoryParameters(cmd, item);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<CharacterInventoryItem>> GetActiveInventoryItemsAsync(TrpgScope scope, string characterId, int limit = 24)
    {
        var groupId = scope.GroupId;
        var items = new List<CharacterInventoryItem>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, ItemKey, DisplayName, Quantity, Unit, State,
                   Description, LocationHint, OwnerEntityId, SourceKind, AuthorityRank, Confidence,
                   IsAssumed, IsContradicted, NeedsReview, SourceEventId, LastEventId, LastEvidence,
                   IsVisibleToCharacter, IsActive, Metadata, CreatedAt, UpdatedAt
            FROM CharacterInventoryItem
            WHERE WorldId = @worldId
              AND GroupId = @groupId
              AND CharacterId = @characterId
              AND IsActive = 1
              AND State IN ('carried', 'equipped', 'stored', 'unknown')
            ORDER BY State = 'equipped' DESC, UpdatedAt DESC, Id ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(ReadInventoryItem(reader));
        return items;
    }

    public async Task<List<CharacterInventoryItem>> GetAllInventoryItemsAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        var items = new List<CharacterInventoryItem>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, ItemKey, DisplayName, Quantity, Unit, State,
                   Description, LocationHint, OwnerEntityId, SourceKind, AuthorityRank, Confidence,
                   IsAssumed, IsContradicted, NeedsReview, SourceEventId, LastEventId, LastEvidence,
                   IsVisibleToCharacter, IsActive, Metadata, CreatedAt, UpdatedAt
            FROM CharacterInventoryItem
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            ORDER BY IsActive DESC, UpdatedAt DESC, Id ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(ReadInventoryItem(reader));
        return items;
    }

    public async Task ApplyInventoryMutationAsync(TrpgScope scope, string characterId, InventoryMutation mutation, long? eventId)
    {
        var groupId = scope.GroupId;
        if (mutation.IsFullSnapshot || IsOperation(mutation.Operation, "snapshot"))
        {
            await ApplyInventorySnapshotAsync(scope, characterId, new List<InventoryMutation> { mutation }, eventId, mutation.Evidence);
            return;
        }

        var itemKey = NormalizeItemKey(FirstNonEmpty(mutation.ItemKey, mutation.DisplayName));
        if (string.IsNullOrWhiteSpace(itemKey))
            return;

        var existing = await GetInventoryItemAsync(scope, characterId, itemKey);
        if (existing == null && ShouldRejectUnverifiedNewItemClaim(mutation))
        {
            var reviewItem = BuildRejectedInventoryClaim(scope, characterId, itemKey, mutation, eventId);
            await UpsertInventoryItemAsync(scope, reviewItem);
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] Inventory mutation applied: operation={mutation.Operation}, item={reviewItem.ItemKey}, active={reviewItem.IsActive}");
            return;
        }

        var item = existing ?? new CharacterInventoryItem
        {
            GroupId = groupId,
            WorldId = scope.WorldId,
            CharacterId = characterId,
            ItemKey = itemKey,
            DisplayName = FirstNonEmpty(mutation.DisplayName, mutation.ItemKey),
            Quantity = 0,
            State = "unknown",
            SourceEventId = eventId
        };

        ApplyMutationToItem(item, mutation, eventId);
        await UpsertInventoryItemAsync(scope, item);
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] Inventory mutation applied: operation={mutation.Operation}, item={item.ItemKey}, active={item.IsActive}");
    }

    public async Task ApplyInventorySnapshotAsync(TrpgScope scope, string characterId, List<InventoryMutation> snapshotItems, long? eventId, string evidence)
    {
        var groupId = scope.GroupId;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE CharacterInventoryItem
                SET IsActive = 0,
                    State = 'unknown',
                    SourceKind = 'GmCorrection',
                    AuthorityRank = 90,
                    Confidence = 1.0,
                    IsAssumed = 0,
                    LastEventId = @eventId,
                    LastEvidence = @evidence,
                    UpdatedAt = @updatedAt
                WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND IsActive = 1
                """;
            cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
            cmd.Parameters.AddWithValue("@groupId", groupId);
            cmd.Parameters.AddWithValue("@characterId", characterId);
            cmd.Parameters.AddWithValue("@eventId", eventId.HasValue ? eventId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@evidence", evidence ?? "");
            cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var mutation in snapshotItems)
        {
            var itemKey = NormalizeItemKey(FirstNonEmpty(mutation.ItemKey, mutation.DisplayName));
            if (string.IsNullOrWhiteSpace(itemKey))
                continue;

            var item = await GetInventoryItemAsync(scope, characterId, itemKey) ?? new CharacterInventoryItem
            {
                WorldId = scope.WorldId,
                GroupId = groupId,
                CharacterId = characterId,
                ItemKey = itemKey,
                SourceEventId = eventId
            };

            item.DisplayName = FirstNonEmpty(mutation.DisplayName, mutation.ItemKey, item.DisplayName);
            item.Quantity = mutation.QuantitySet ?? (mutation.QuantityDelta != 0 ? mutation.QuantityDelta : 1);
            item.Unit = mutation.Unit ?? "";
            item.State = FirstNonEmpty(mutation.NewState, "carried");
            item.SourceKind = "GmCorrection";
            item.AuthorityRank = 90;
            item.Confidence = Math.Clamp(mutation.Confidence <= 0 ? 1.0 : mutation.Confidence, 0, 1);
            item.IsAssumed = false;
            item.IsContradicted = false;
            item.NeedsReview = false;
            item.LastEventId = eventId;
            item.LastEvidence = FirstNonEmpty(mutation.Evidence ?? "", evidence ?? "");
            item.IsActive = ActiveInventoryStates.Contains(item.State);
            item.UpdatedAt = DateTime.UtcNow;
            await UpsertInventoryItemAsync(scope, item);
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] Inventory mutation applied: operation=snapshot, item={item.ItemKey}, active={item.IsActive}");
        }
    }

    public async Task ResetInventoryFromInitialSeedAsync(TrpgScope scope, AiCharacterEntry aiChar)
    {
        var groupId = scope.GroupId;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM CharacterInventoryItem
                WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId;

                DELETE FROM CharacterInventorySeedState
                WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId;
                """;
            cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
            cmd.Parameters.AddWithValue("@groupId", groupId);
            cmd.Parameters.AddWithValue("@characterId", aiChar.CharacterId);
            await cmd.ExecuteNonQueryAsync();
        }

        await EnsureInitialInventoryImportedAsync(scope, aiChar);
    }

    private async Task<CharacterInventoryItem?> GetInventoryItemAsync(TrpgScope scope, string characterId, string itemKey)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, ItemKey, DisplayName, Quantity, Unit, State,
                   Description, LocationHint, OwnerEntityId, SourceKind, AuthorityRank, Confidence,
                   IsAssumed, IsContradicted, NeedsReview, SourceEventId, LastEventId, LastEvidence,
                   IsVisibleToCharacter, IsActive, Metadata, CreatedAt, UpdatedAt
            FROM CharacterInventoryItem
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND ItemKey = @itemKey
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@itemKey", itemKey);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadInventoryItem(reader) : null;
    }

    private static void ApplyMutationToItem(CharacterInventoryItem item, InventoryMutation mutation, long? eventId)
    {
        var operation = (mutation.Operation ?? "").Trim().ToLowerInvariant();
        item.DisplayName = FirstNonEmpty(mutation.DisplayName, item.DisplayName, mutation.ItemKey, item.ItemKey);
        item.Unit = FirstNonEmpty(mutation.Unit, item.Unit);
        item.SourceKind = NormalizeSourceKind(mutation.SourceKind, operation);
        item.AuthorityRank = mutation.AuthorityRank > 0 ? mutation.AuthorityRank : DefaultAuthorityRank(item.SourceKind);
        item.Confidence = Math.Clamp(mutation.Confidence <= 0 ? DefaultConfidence(item.SourceKind) : mutation.Confidence, 0, 1);
        item.IsAssumed = item.AuthorityRank < 50 || item.SourceKind is "PlayerDeclared" or "SceneImplied";
        item.LastEventId = eventId;
        item.LastEvidence = mutation.Evidence ?? "";
        item.UpdatedAt = DateTime.UtcNow;

        if (mutation.QuantitySet.HasValue)
            item.Quantity = mutation.QuantitySet.Value;
        else if (mutation.QuantityDelta != 0)
            item.Quantity += mutation.QuantityDelta;
        else if (IsOperation(operation, "gain") && item.Quantity <= 0)
            item.Quantity = 1;

        if (IsOperation(operation, "correction"))
        {
            item.SourceKind = "GmCorrection";
            item.AuthorityRank = 90;
            item.Confidence = 1.0;
            item.IsAssumed = false;
        }

        item.State = ResolveNewState(operation, mutation.NewState, item.State);
        item.IsContradicted = IsOperation(operation, "correction") && (item.Quantity <= 0 || !ActiveInventoryStates.Contains(item.State));
        item.NeedsReview = item.SourceKind is "PlayerDeclared" or "SceneImplied" && item.Confidence < 0.65;
        item.IsActive = ActiveInventoryStates.Contains(item.State) && item.Quantity > 0 && !item.IsContradicted;
    }

    private static bool ShouldRejectUnverifiedNewItemClaim(InventoryMutation mutation)
    {
        var operation = (mutation.Operation ?? "").Trim().ToLowerInvariant();
        if (IsOperation(operation, "correction") || IsOperation(operation, "snapshot") || mutation.IsFullSnapshot)
            return false;

        var sourceKind = NormalizeSourceKind(mutation.SourceKind, operation);
        var authorityRank = mutation.AuthorityRank > 0 ? mutation.AuthorityRank : DefaultAuthorityRank(sourceKind);
        if (authorityRank >= 50)
            return false;

        return !CanCreateNewActiveItem(sourceKind, operation, authorityRank);
    }

    private static bool CanCreateNewActiveItem(string sourceKind, string operation, int authorityRank)
    {
        if (authorityRank >= 50)
            return true;
        if (IsOperation(operation, "correction") || IsOperation(operation, "snapshot"))
            return true;

        return sourceKind is "InitialSeed"
            or "SceneImplied"
            or "GmImplied"
            or "GmConfirmed"
            or "GmCorrection"
            or "ManualOverride";
    }

    private static CharacterInventoryItem BuildRejectedInventoryClaim(
        TrpgScope scope,
        string characterId,
        string itemKey,
        InventoryMutation mutation,
        long? eventId)
    {
        var operation = (mutation.Operation ?? "").Trim().ToLowerInvariant();
        var sourceKind = NormalizeSourceKind(mutation.SourceKind, operation);
        var authorityRank = mutation.AuthorityRank > 0 ? mutation.AuthorityRank : DefaultAuthorityRank(sourceKind);
        var confidence = Math.Clamp(mutation.Confidence <= 0 ? DefaultConfidence(sourceKind) : mutation.Confidence, 0, 1);

        return new CharacterInventoryItem
        {
            WorldId = scope.WorldId,
            GroupId = scope.GroupId,
            CharacterId = characterId,
            ItemKey = itemKey,
            DisplayName = FirstNonEmpty(mutation.DisplayName, mutation.ItemKey, itemKey),
            Quantity = 0,
            Unit = FirstNonEmpty(mutation.Unit, ""),
            State = "unknown",
            SourceKind = sourceKind,
            AuthorityRank = authorityRank,
            Confidence = confidence,
            IsAssumed = true,
            IsContradicted = false,
            NeedsReview = true,
            SourceEventId = eventId,
            LastEventId = eventId,
            LastEvidence = mutation.Evidence ?? "",
            IsVisibleToCharacter = true,
            IsActive = false,
            Metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["rejected_reason"] = "unverified_new_item_claim",
                ["operation"] = mutation.Operation,
                ["source_kind"] = sourceKind,
                ["authority_rank"] = authorityRank,
                ["confidence"] = confidence,
                ["target_entity_id"] = mutation.TargetEntityId
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static string ResolveNewState(string operation, string newState, string currentState)
    {
        if (!string.IsNullOrWhiteSpace(newState))
            return NormalizeState(newState);
        if (IsOperation(operation, "equip"))
            return "equipped";
        if (IsOperation(operation, "unequip"))
            return "carried";
        if (IsOperation(operation, "break"))
            return "broken";
        if (IsOperation(operation, "repair"))
            return "carried";
        if (InactiveInventoryOperations.Contains(operation))
            return operation == "consume" ? "consumed" : operation == "loss" ? "lost" : operation == "transfer" ? "transferred" : "lost";
        if (IsOperation(operation, "gain"))
            return "carried";
        return NormalizeState(currentState);
    }

    private static List<CharacterInventoryItem> ParseInitialInventorySeed(TrpgScope scope, string characterId, string seedJson)
    {
        var groupId = scope.GroupId;
        var result = new List<CharacterInventoryItem>();
        if (string.IsNullOrWhiteSpace(seedJson) || seedJson.Trim() == "[]")
            return result;

        try
        {
            using var doc = JsonDocument.Parse(seedJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var displayName = "";
                var quantity = 1.0;
                var unit = "";
                var description = "";
                var state = "carried";
                var itemKey = "";

                if (element.ValueKind == JsonValueKind.String)
                {
                    displayName = element.GetString() ?? "";
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    displayName = GetJsonString(element, "displayName", "DisplayName", "name", "Name");
                    itemKey = GetJsonString(element, "itemKey", "ItemKey", "key", "Key");
                    unit = GetJsonString(element, "unit", "Unit");
                    description = GetJsonString(element, "description", "Description");
                    state = FirstNonEmpty(GetJsonString(element, "state", "State"), "carried");
                    quantity = GetJsonDouble(element, 1, "quantity", "Quantity", "count", "Count");
                }

                displayName = FirstNonEmpty(displayName, itemKey);
                itemKey = NormalizeItemKey(FirstNonEmpty(itemKey, displayName));
                if (string.IsNullOrWhiteSpace(itemKey) || string.IsNullOrWhiteSpace(displayName))
                    continue;

                result.Add(new CharacterInventoryItem
                {
                    WorldId = scope.WorldId,
                    GroupId = groupId,
                    CharacterId = characterId,
                    ItemKey = itemKey,
                    DisplayName = displayName,
                    Quantity = quantity,
                    Unit = unit,
                    State = NormalizeState(state),
                    Description = description,
                    SourceKind = "InitialSeed",
                    AuthorityRank = 70,
                    Confidence = 1.0,
                    IsAssumed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
        catch
        {
            return result;
        }

        return result;
    }

    private static string GetJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                return value.ToString();
        }
        return "";
    }

    private static double GetJsonDouble(JsonElement element, double defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number))
                return number;
        }
        return defaultValue;
    }

    private static CharacterInventoryItem ReadInventoryItem(System.Data.Common.DbDataReader reader)
    {
        return new CharacterInventoryItem
        {
            Id = reader.GetInt64(0),
            WorldId = reader.GetString(1),
            GroupId = reader.GetInt64(2),
            CharacterId = reader.GetString(3),
            ItemKey = reader.GetString(4),
            DisplayName = reader.GetString(5),
            Quantity = reader.GetDouble(6),
            Unit = reader.GetString(7),
            State = reader.GetString(8),
            Description = reader.GetString(9),
            LocationHint = reader.GetString(10),
            OwnerEntityId = reader.GetString(11),
            SourceKind = reader.GetString(12),
            AuthorityRank = reader.GetInt32(13),
            Confidence = reader.GetDouble(14),
            IsAssumed = reader.GetInt32(15) != 0,
            IsContradicted = reader.GetInt32(16) != 0,
            NeedsReview = reader.GetInt32(17) != 0,
            SourceEventId = reader.IsDBNull(18) ? null : reader.GetInt64(18),
            LastEventId = reader.IsDBNull(19) ? null : reader.GetInt64(19),
            LastEvidence = reader.GetString(20),
            IsVisibleToCharacter = reader.GetInt32(21) != 0,
            IsActive = reader.GetInt32(22) != 0,
            Metadata = reader.GetString(23),
            CreatedAt = ParseDate(reader.GetValue(24)),
            UpdatedAt = ParseDate(reader.GetValue(25))
        };
    }

    private static void AddInventoryParameters(System.Data.Common.DbCommand cmd, CharacterInventoryItem item)
    {
        cmd.Parameters.Add(new SQLiteParameter("@worldId", item.WorldId));
        cmd.Parameters.Add(new SQLiteParameter("@groupId", item.GroupId));
        cmd.Parameters.Add(new SQLiteParameter("@characterId", item.CharacterId));
        cmd.Parameters.Add(new SQLiteParameter("@itemKey", item.ItemKey));
        cmd.Parameters.Add(new SQLiteParameter("@displayName", item.DisplayName));
        cmd.Parameters.Add(new SQLiteParameter("@quantity", item.Quantity));
        cmd.Parameters.Add(new SQLiteParameter("@unit", item.Unit ?? ""));
        cmd.Parameters.Add(new SQLiteParameter("@state", NormalizeState(item.State)));
        cmd.Parameters.Add(new SQLiteParameter("@description", item.Description ?? ""));
        cmd.Parameters.Add(new SQLiteParameter("@locationHint", item.LocationHint ?? ""));
        cmd.Parameters.Add(new SQLiteParameter("@ownerEntityId", item.OwnerEntityId ?? ""));
        cmd.Parameters.Add(new SQLiteParameter("@sourceKind", item.SourceKind ?? "InitialSeed"));
        cmd.Parameters.Add(new SQLiteParameter("@authorityRank", item.AuthorityRank));
        cmd.Parameters.Add(new SQLiteParameter("@confidence", item.Confidence));
        cmd.Parameters.Add(new SQLiteParameter("@isAssumed", item.IsAssumed ? 1 : 0));
        cmd.Parameters.Add(new SQLiteParameter("@isContradicted", item.IsContradicted ? 1 : 0));
        cmd.Parameters.Add(new SQLiteParameter("@needsReview", item.NeedsReview ? 1 : 0));
        cmd.Parameters.Add(new SQLiteParameter("@sourceEventId", item.SourceEventId.HasValue ? item.SourceEventId.Value : DBNull.Value));
        cmd.Parameters.Add(new SQLiteParameter("@lastEventId", item.LastEventId.HasValue ? item.LastEventId.Value : DBNull.Value));
        cmd.Parameters.Add(new SQLiteParameter("@lastEvidence", item.LastEvidence ?? ""));
        cmd.Parameters.Add(new SQLiteParameter("@isVisibleToCharacter", item.IsVisibleToCharacter ? 1 : 0));
        cmd.Parameters.Add(new SQLiteParameter("@isActive", item.IsActive ? 1 : 0));
        cmd.Parameters.Add(new SQLiteParameter("@metadata", item.Metadata ?? "{}"));
        cmd.Parameters.Add(new SQLiteParameter("@createdAt", item.CreatedAt.ToString("o")));
        cmd.Parameters.Add(new SQLiteParameter("@updatedAt", item.UpdatedAt.ToString("o")));
    }

    private static DateTime ParseDate(object value)
        => DateTime.TryParse(value?.ToString(), out var parsed) ? parsed : DateTime.UtcNow;

    private static string ComputeSeedHash(string seedJson)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seedJson ?? "")));

    private static string NormalizeItemKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var normalized = value.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        var lastSeparator = false;
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastSeparator = false;
            }
            else if (!lastSeparator)
            {
                sb.Append('_');
                lastSeparator = true;
            }
        }
        return sb.ToString().Trim('_');
    }

    private static string NormalizeState(string state)
    {
        var normalized = (state ?? "").Trim().ToLowerInvariant();
        return normalized is "carried" or "equipped" or "stored" or "lost" or "consumed" or "transferred" or "broken" or "unknown"
            ? normalized
            : "unknown";
    }

    private static string NormalizeSourceKind(string sourceKind, string operation)
    {
        if (IsOperation(operation, "correction") || IsOperation(operation, "snapshot"))
            return "GmCorrection";
        var normalized = FirstNonEmpty(sourceKind, "PlayerDeclared");
        return normalized is "InitialSeed" or "PlayerDeclared" or "SceneImplied" or "GmImplied" or "GmConfirmed" or "GmCorrection" or "ManualOverride" or "SystemRepair"
            ? normalized
            : "PlayerDeclared";
    }

    private static int DefaultAuthorityRank(string sourceKind)
        => sourceKind switch
        {
            "GmCorrection" or "ManualOverride" => 90,
            "InitialSeed" or "GmConfirmed" => 70,
            "GmImplied" => 50,
            _ => 30
        };

    private static double DefaultConfidence(string sourceKind)
        => sourceKind is "PlayerDeclared" or "SceneImplied" ? 0.7 : 1.0;

    private static bool IsOperation(string operation, string expected)
        => string.Equals(operation, expected, StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }
}
