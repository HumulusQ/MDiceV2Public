using System.Data.SQLite;
using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIMod.Trpg;

public partial class ChatDatabase : IDisposable
{
    private const string TrpgSchemaVersionKey = "trpg_world_scope";
    private const string TrpgSchemaVersion = "trpg_world_scope_v1";

    private readonly SQLiteConnection _connection;
    private readonly IModContext _context;
    private bool _disposed;

    public ChatDatabase(string dbPath, IModContext context)
    {
        _context = context;
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);

        _connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
        _connection.Open();
        EnableOptimizations();
    }

    private void EnableOptimizations()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = 5000;
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task InitializeSchemaAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterSheet (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                StaticBackground TEXT,
                DynamicStateJson TEXT,
                UpdatedAt DATETIME
            );

            CREATE TABLE IF NOT EXISTS ChatHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL DEFAULT '',
                MessageType TEXT NOT NULL,
                SpeakerName TEXT NOT NULL,
                Role TEXT NOT NULL,
                Content TEXT NOT NULL,
                TokenCount INTEGER DEFAULT 0,
                IsArchived INTEGER DEFAULT 0,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_chathistory_group_archived
                ON ChatHistory(GroupId, IsArchived, CreatedAt);

            CREATE INDEX IF NOT EXISTS idx_chathistory_character
                ON ChatHistory(CharacterId, IsArchived, CreatedAt);

            CREATE TABLE IF NOT EXISTS LongTermMemory (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId     INTEGER NOT NULL DEFAULT 0,
                CharacterId TEXT NOT NULL DEFAULT '',
                Keywords    TEXT NOT NULL,
                Summary     TEXT NOT NULL,
                NodeType    TEXT NOT NULL DEFAULT 'event',
                Importance  REAL NOT NULL DEFAULT 0.5,
                Tier        TEXT NOT NULL DEFAULT 'Session',
                Heat        REAL NOT NULL DEFAULT 0.5,
                Embedding   BLOB,
                Superseded  INTEGER NOT NULL DEFAULT 0,
                SupersededBy INTEGER,
                LastUsed    DATETIME,
                CreatedAt   DATETIME DEFAULT CURRENT_TIMESTAMP,
                Confidence  REAL NOT NULL DEFAULT 1.0,
                FoldCount   INTEGER NOT NULL DEFAULT 0,
                SceneId     TEXT,
                EntityId    TEXT,
                MemoryAudience TEXT NOT NULL DEFAULT 'CharacterIC',
                OwnerCharacterId TEXT NULL,
                SourceScope TEXT NULL,
                SourceMessageIds TEXT DEFAULT '[]',
                IcUsable INTEGER NOT NULL DEFAULT 1,
                Metadata TEXT DEFAULT '{}'
            );

            CREATE INDEX IF NOT EXISTS idx_ltm_keywords
                ON LongTermMemory(Keywords);
            CREATE INDEX IF NOT EXISTS idx_ltm_group_char
                ON LongTermMemory(GroupId, CharacterId);
            CREATE INDEX IF NOT EXISTS idx_ltm_importance
                ON LongTermMemory(Importance DESC, LastUsed DESC);
            CREATE INDEX IF NOT EXISTS idx_ltm_scene
                ON LongTermMemory(SceneId);
            CREATE INDEX IF NOT EXISTS idx_ltm_entity
                ON LongTermMemory(EntityId);
            CREATE INDEX IF NOT EXISTS idx_ltm_group_char_audience
                ON LongTermMemory(GroupId, CharacterId, MemoryAudience);
            CREATE INDEX IF NOT EXISTS idx_ltm_group_audience
                ON LongTermMemory(GroupId, MemoryAudience);
            CREATE INDEX IF NOT EXISTS idx_ltm_owner_audience
                ON LongTermMemory(GroupId, OwnerCharacterId, MemoryAudience);

            CREATE TABLE IF NOT EXISTS LlmUsageLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAt TEXT NOT NULL,
                Provider TEXT NOT NULL,
                Model TEXT NOT NULL,
                AgentName TEXT NOT NULL,
                RequestKind TEXT NOT NULL,
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NULL,
                TurnId TEXT NULL,
                SourceMessageId TEXT NULL,
                InputTokens INTEGER NOT NULL DEFAULT 0,
                OutputTokens INTEGER NOT NULL DEFAULT 0,
                CachedInputTokens INTEGER NULL,
                CacheHitTokens INTEGER NULL,
                CacheMissTokens INTEGER NULL,
                EstimatedCost REAL NOT NULL DEFAULT 0,
                Success INTEGER NOT NULL DEFAULT 0,
                ErrorType TEXT NULL,
                Metadata TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS idx_llm_usage_created
                ON LlmUsageLog(CreatedAt);
            CREATE INDEX IF NOT EXISTS idx_llm_usage_world_group
                ON LlmUsageLog(WorldId, GroupId, CreatedAt);
            CREATE INDEX IF NOT EXISTS idx_llm_usage_agent
                ON LlmUsageLog(AgentName, RequestKind, CreatedAt);

            CREATE TABLE IF NOT EXISTS RawArchive (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                MemoryId    INTEGER NOT NULL,
                Content    TEXT NOT NULL,
                CreatedAt  DATETIME DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (MemoryId) REFERENCES LongTermMemory(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_raw_archive_memory
                ON RawArchive(MemoryId);

            -- Quest 表（目标层）
            CREATE TABLE IF NOT EXISTS Quest (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL,
                Description TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Active',
                Priority TEXT NOT NULL DEFAULT 'Normal',
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                CompletedAt DATETIME,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                LastTouchedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                HiddenFromPrompt INTEGER NOT NULL DEFAULT 0,
                SourceSceneId TEXT NOT NULL DEFAULT '',
                LastMentionedSceneId TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_quest_group_char
                ON Quest(GroupId, CharacterId, Status);

            -- EntityCanonical 表（实体规范化层）
            CREATE TABLE IF NOT EXISTS EntityCanonical (
                EntityId TEXT PRIMARY KEY,
                CurrentDisplayName TEXT NOT NULL,
                Aliases TEXT NOT NULL,
                IdentityStatus TEXT NOT NULL DEFAULT 'Tentative',
                CoreSummary TEXT NOT NULL DEFAULT '',
                EntityFactSummary TEXT NOT NULL DEFAULT '',
                PersistentFacts TEXT NOT NULL DEFAULT '[]',
                Relationships TEXT NOT NULL DEFAULT '{}',
                Version INTEGER NOT NULL DEFAULT 1,
                ConflictReason TEXT,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                LastUpdated DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            -- EventLog 表（不可变事件流）
            CREATE TABLE IF NOT EXISTS EventLog (
                EventId INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                EventType TEXT NOT NULL,
                Payload TEXT NOT NULL,
                SourceEntityId TEXT,
                TargetEntityId TEXT,
                SceneId TEXT,
                Consequences TEXT DEFAULT '[]',
                SemanticSummary TEXT,
                NarrativeWeight REAL DEFAULT 0.0,
                NarrativeTags TEXT DEFAULT '[]',
                EmotionalWeight REAL DEFAULT 0.0,
                ArcAffinity TEXT,
                IsSemanticallyDistilled INTEGER DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_eventlog_entity ON EventLog(SourceEntityId);
            CREATE INDEX IF NOT EXISTS idx_eventlog_scene ON EventLog(SceneId);
            CREATE INDEX IF NOT EXISTS idx_eventlog_time ON EventLog(Timestamp);

            -- CausalGraph 表（因果图谱）
            CREATE TABLE IF NOT EXISTS CausalGraph (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL DEFAULT 0,
                CharacterId TEXT NOT NULL DEFAULT '',
                SourceEventId INTEGER NOT NULL,
                TargetEventId INTEGER NOT NULL,
                EdgeType TEXT NOT NULL,
                Weight REAL DEFAULT 1.0,
                CreatedFoldCount INTEGER NOT NULL DEFAULT 0,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_causalgraph_group_char ON CausalGraph(GroupId, CharacterId);
            CREATE INDEX IF NOT EXISTS idx_causalgraph_source ON CausalGraph(SourceEventId);
            CREATE INDEX IF NOT EXISTS idx_causalgraph_target ON CausalGraph(TargetEventId);
            CREATE INDEX IF NOT EXISTS idx_causalgraph_type ON CausalGraph(EdgeType);

            -- NarrativeMemoryNode 表（叙事记忆节点）
            CREATE TABLE IF NOT EXISTS NarrativeMemoryNode (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL,
                Summary TEXT NOT NULL,
                NarrativeWeight REAL DEFAULT 0.5,
                EmotionalWeight REAL DEFAULT 0.0,
                RelationshipImpact REAL DEFAULT 0.0,
                GoalImpact REAL DEFAULT 0.0,
                MysteryWeight REAL DEFAULT 0.0,
                IsResolved INTEGER DEFAULT 0,
                InvolvedEntities TEXT DEFAULT '[]',
                ArcTags TEXT DEFAULT '[]',
                Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                CreatedFoldCount INTEGER NOT NULL DEFAULT 0,
                SourceEventId INTEGER NOT NULL,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_narrativememory_group ON NarrativeMemoryNode(GroupId, CharacterId);
            CREATE INDEX IF NOT EXISTS idx_narrativememory_event ON NarrativeMemoryNode(SourceEventId);
            CREATE INDEX IF NOT EXISTS idx_narrativememory_timestamp ON NarrativeMemoryNode(Timestamp);

            -- CharacterMemory 表（角色记忆）
            CREATE TABLE IF NOT EXISTS CharacterMemory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL,
                MemoryType TEXT NOT NULL,
                Content TEXT NOT NULL,
                Confidence REAL DEFAULT 1.0,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP,
                RelatedEventId INTEGER,
                Metadata TEXT DEFAULT '{}',
                FoldCount INTEGER NOT NULL DEFAULT 0,
                LastAccessedFoldCount INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_charactermemory_group ON CharacterMemory(GroupId, CharacterId);
            CREATE INDEX IF NOT EXISTS idx_charactermemory_type ON CharacterMemory(MemoryType);
            CREATE INDEX IF NOT EXISTS idx_charactermemory_event ON CharacterMemory(RelatedEventId);

            CREATE TABLE IF NOT EXISTS AffectiveTagState (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL,
                TagType TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                SourceKey TEXT NOT NULL,
                TargetEntityId TEXT NULL,
                IntensityTier TEXT NOT NULL,
                Charge REAL DEFAULT 0,
                ChargeCap REAL DEFAULT 1,
                RepetitionCount INTEGER DEFAULT 0,
                AdaptationLevel REAL DEFAULT 0,
                Status TEXT DEFAULT 'Active',
                LastEvidence TEXT DEFAULT '',
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                LastAppliedFoldCount INTEGER DEFAULT 0,
                ExpirePolicy TEXT DEFAULT 'Scene',
                Metadata TEXT DEFAULT '{}'
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_affectivestate_identity
                ON AffectiveTagState(GroupId, CharacterId, TagType, SourceKey, TargetEntityId);
            CREATE INDEX IF NOT EXISTS idx_affectivestate_active
                ON AffectiveTagState(GroupId, CharacterId, Status, UpdatedAt);

            CREATE TABLE IF NOT EXISTS AffectiveTagEvent (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL,
                SourceEventId INTEGER NULL,
                TagType TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                SourceKey TEXT NOT NULL,
                TargetEntityId TEXT NULL,
                EffectKind TEXT NOT NULL,
                IntensityTier TEXT NOT NULL,
                Novelty TEXT DEFAULT 'Medium',
                Evidence TEXT DEFAULT '',
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                FoldCount INTEGER DEFAULT 0,
                Metadata TEXT DEFAULT '{}'
            );

            CREATE INDEX IF NOT EXISTS idx_affectiveevent_group
                ON AffectiveTagEvent(GroupId, CharacterId, CreatedAt);

            -- SceneSnapshot 表（场景快照）
            CREATE TABLE IF NOT EXISTS SceneSnapshot (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL DEFAULT 0,
                CharacterId TEXT NOT NULL DEFAULT '',
                SceneId TEXT NOT NULL,
                SceneDescription TEXT NOT NULL DEFAULT '',
                PresentEntities TEXT NOT NULL DEFAULT '[]',
                StateProperties TEXT NOT NULL DEFAULT '{}',
                SnapshotReason TEXT NOT NULL DEFAULT '',
                CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                EnteredAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PresentEntityIds TEXT NOT NULL DEFAULT '[]',
                SceneGoals TEXT NOT NULL DEFAULT '[]',
                OutstandingThreads TEXT NOT NULL DEFAULT '[]',
                SceneFlags TEXT NOT NULL DEFAULT '{}'
            );

            CREATE INDEX IF NOT EXISTS idx_scenesnapshot_scene ON SceneSnapshot(SceneId);

            CREATE TABLE IF NOT EXISTS NpcCanonicalState (
                GroupId                   INTEGER NOT NULL,
                NpcId                     TEXT NOT NULL,
                DisplayName               TEXT NOT NULL DEFAULT '',
                CoreSummary               TEXT NOT NULL DEFAULT '',
                IdentityState             TEXT NOT NULL DEFAULT '',
                KeyEventsDigest           TEXT NOT NULL DEFAULT '',
                RelationshipState         TEXT NOT NULL DEFAULT '',
                PendingRelationshipDeltaJson TEXT NOT NULL DEFAULT '{}',
                LastSummaryUpdatedAt      TEXT NOT NULL DEFAULT '',
                UpdatedAt                 TEXT NOT NULL,
                PRIMARY KEY (GroupId, NpcId)
            );

            CREATE INDEX IF NOT EXISTS idx_npccanonical_group
                ON NpcCanonicalState(GroupId, NpcId);

            CREATE TABLE IF NOT EXISTS AiCharacterEntry (
                CharacterId   TEXT PRIMARY KEY,
                VirtualId     INTEGER NOT NULL,
                GroupId       INTEGER NOT NULL,
                TeamName      TEXT NOT NULL,
                DisplayName  TEXT NOT NULL,
                StaticBackground TEXT NOT NULL DEFAULT '',
                DynamicStateJson TEXT NOT NULL DEFAULT '{}',
                SkillsJson       TEXT NOT NULL DEFAULT '{}',
                InventoryJson    TEXT NOT NULL DEFAULT '[]',
                RuleText         TEXT NOT NULL DEFAULT '',
                IsActive      INTEGER NOT NULL DEFAULT 0,
                CreatedAt     TEXT NOT NULL,
                UpdatedAt     TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_aichar_group_team
                ON AiCharacterEntry(GroupId, TeamName);

            CREATE INDEX IF NOT EXISTS idx_aichar_virtualid
                ON AiCharacterEntry(VirtualId);

            CREATE TABLE IF NOT EXISTS CharacterInventoryItem (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL,
                ItemKey TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                Quantity REAL NOT NULL DEFAULT 1,
                Unit TEXT NOT NULL DEFAULT '',
                State TEXT NOT NULL DEFAULT 'carried',

                Description TEXT NOT NULL DEFAULT '',
                LocationHint TEXT NOT NULL DEFAULT '',
                OwnerEntityId TEXT NOT NULL DEFAULT '',

                SourceKind TEXT NOT NULL DEFAULT 'InitialSeed',
                AuthorityRank INTEGER NOT NULL DEFAULT 70,
                Confidence REAL NOT NULL DEFAULT 1.0,

                IsAssumed INTEGER NOT NULL DEFAULT 0,
                IsContradicted INTEGER NOT NULL DEFAULT 0,
                NeedsReview INTEGER NOT NULL DEFAULT 0,

                SourceEventId INTEGER NULL,
                LastEventId INTEGER NULL,
                LastEvidence TEXT NOT NULL DEFAULT '',

                IsVisibleToCharacter INTEGER NOT NULL DEFAULT 1,
                IsActive INTEGER NOT NULL DEFAULT 1,
                Metadata TEXT NOT NULL DEFAULT '{}',

                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,

                UNIQUE(GroupId, CharacterId, ItemKey)
            );

            CREATE INDEX IF NOT EXISTS idx_inventory_active
                ON CharacterInventoryItem(GroupId, CharacterId, IsActive, State);

            CREATE INDEX IF NOT EXISTS idx_inventory_source_event
                ON CharacterInventoryItem(SourceEventId, LastEventId);

            CREATE TABLE IF NOT EXISTS CharacterInventorySeedState (
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NOT NULL,
                SeedHash TEXT NOT NULL DEFAULT '',
                ImportedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                SourceEventId INTEGER NULL,
                PRIMARY KEY(GroupId, CharacterId)
            );

            CREATE TABLE IF NOT EXISTS SceneDictionary (
                SceneId       TEXT PRIMARY KEY,
                SceneBaseDesc TEXT NOT NULL,
                UpdatedAt     TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS CharacterHotMeta (
                CharId        TEXT PRIMARY KEY,
                ShortTags     TEXT NOT NULL DEFAULT '',
                Aliases       TEXT NOT NULL DEFAULT '',
                UpdatedAt     TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_characterhotmeta_aliases
                ON CharacterHotMeta(Aliases);

            CREATE TABLE IF NOT EXISTS AiDebugSetting (
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                DebugEnabled INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY(WorldId, GroupId)
            );

            CREATE TABLE IF NOT EXISTS LlmDebugLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAt TEXT NOT NULL,
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                CharacterId TEXT NULL,
                AgentName TEXT NOT NULL,
                RequestKind TEXT NOT NULL,
                MessagesJson TEXT NOT NULL,
                ResponseText TEXT NULL,
                Success INTEGER NOT NULL DEFAULT 1,
                Error TEXT NULL,
                InputCharCount INTEGER NOT NULL DEFAULT 0,
                OutputCharCount INTEGER NOT NULL DEFAULT 0,
                Metadata TEXT DEFAULT '{}'
            );

            CREATE INDEX IF NOT EXISTS idx_llm_debug_world_group_created
                ON LlmDebugLog(WorldId, GroupId, CreatedAt);

            CREATE INDEX IF NOT EXISTS idx_llm_debug_world_group_agent_created
                ON LlmDebugLog(WorldId, GroupId, AgentName, CreatedAt);

            CREATE TABLE IF NOT EXISTS EntitySalience (
                WorldId TEXT NOT NULL,
                GroupId INTEGER NOT NULL,
                EntityId TEXT NOT NULL,
                Heat REAL NOT NULL DEFAULT 0,
                LastMentionedAt TEXT NULL,
                LastMentionedFoldCount INTEGER NOT NULL DEFAULT 0,
                MentionCount INTEGER NOT NULL DEFAULT 0,
                LastSceneId TEXT NULL,
                LastMentionSource TEXT NULL,
                LastMentionEvidence TEXT NULL,
                PRIMARY KEY(WorldId, GroupId, EntityId)
            );

            CREATE INDEX IF NOT EXISTS idx_entity_salience_heat
                ON EntitySalience(WorldId, GroupId, Heat DESC);
            """;
        await cmd.ExecuteNonQueryAsync();

        await EnsureTrpgWorldScopeSchemaAsync();
        await EnsureMemoryAudienceSchemaAsync();
        await EnsureQuestSchemaAsync();
        await EnsureEntityCanonicalSchemaAsync();
        await EnsureLlmUsageLogSchemaAsync();
        await EnsureCommonApiUsageLogSchemaAsync();
        await EnsureDebugSchemaAsync();
        _context.Log(LogLevel.Info, "[AIMod:TRPG] Database schema initialized");
        return;

        // 迁移：如果是旧版 FTS5 虚拟表，则重建为普通表（仅一次）
        await RebuildLegacyLongTermMemoryIfNeededAsync();

        // 迁移：为旧表添加 CharacterId 列（如果不存在）
        await MigrateAddCharacterIdColumnAsync();

        // 迁移：LongTermMemory 增加语义节点字段
        await MigrateLongTermMemoryColumnsAsync();

        // 迁移：SceneSnapshot 表
        await MigrateSceneSnapshotTableAsync();

        // 迁移：BehaviorEvidence 表
        await MigrateBehaviorEvidenceTableAsync();

        // 迁移：如果旧版 FTS5 虚拟表存在，重建为普通表（已在上方 DROP/CREATE 处理）

        // 迁移：重构 AiCharacterEntry 表（移除 AttributesJson/SpellsJson，SkillsJson 改为 '{}'）
        await MigrateAiCharacterSchemaAsync();

        // 迁移：EntityCanonical 表添加新字段
        await MigrateEntityCanonicalSchemaAsync();

        // 迁移：TimelineNodes 表
        await MigrateTimelineNodesTableAsync();

        // 迁移：CharacterMemory 添加 IsFoundational / RelatedEntityId
        await MigrateCharacterMemoryV2Async();
        await MigrateCausalGraphV2Async();
        await MigrateNarrativeMemoryNodeV2Async();

        _context.Log(LogLevel.Info, "[AIMod:TRPG] Database schema initialized");
    }

    private async Task EnsureTrpgWorldScopeSchemaAsync()
    {
        using (var versionTableCmd = _connection.CreateCommand())
        {
            versionTableCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS AimodSchemaVersion (
                    Key TEXT PRIMARY KEY,
                    Version TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )
                """;
            await versionTableCmd.ExecuteNonQueryAsync();
        }

        string? currentVersion;
        using (var readVersionCmd = _connection.CreateCommand())
        {
            readVersionCmd.CommandText = "SELECT Version FROM AimodSchemaVersion WHERE Key = @key";
            readVersionCmd.Parameters.AddWithValue("@key", TrpgSchemaVersionKey);
            currentVersion = (await readVersionCmd.ExecuteScalarAsync())?.ToString();
        }

        if (string.Equals(currentVersion, TrpgSchemaVersion, StringComparison.Ordinal))
            return;

        _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Rebuilding TRPG schema for scoped world isolation ({currentVersion ?? "none"} -> {TrpgSchemaVersion}).");

        using var tx = _connection.BeginTransaction();
        using (var dropCmd = _connection.CreateCommand())
        {
            dropCmd.Transaction = tx;
            dropCmd.CommandText = """
                DROP TABLE IF EXISTS RawArchive;
                DROP TABLE IF EXISTS LongTermMemory;
                DROP TABLE IF EXISTS ChatHistory;
                DROP TABLE IF EXISTS CharacterSheet;
                DROP TABLE IF EXISTS Quest;
                DROP TABLE IF EXISTS EntityCanonical;
                DROP TABLE IF EXISTS EventLog;
                DROP TABLE IF EXISTS CausalGraph;
                DROP TABLE IF EXISTS NarrativeMemoryNode;
                DROP TABLE IF EXISTS CharacterMemory;
                DROP TABLE IF EXISTS AffectiveTagState;
                DROP TABLE IF EXISTS AffectiveTagEvent;
                DROP TABLE IF EXISTS SceneSnapshot;
                DROP TABLE IF EXISTS NpcCanonicalState;
                DROP TABLE IF EXISTS AiCharacterEntry;
                DROP TABLE IF EXISTS CharacterInventoryItem;
                DROP TABLE IF EXISTS CharacterInventorySeedState;
                DROP TABLE IF EXISTS SceneDictionary;
                DROP TABLE IF EXISTS CharacterHotMeta;
                DROP TABLE IF EXISTS TimelineNodes;
                DROP TABLE IF EXISTS BehaviorEvidence;
                DROP TABLE IF EXISTS TrpgWorld;
                """;
            await dropCmd.ExecuteNonQueryAsync();
        }

        using (var createCmd = _connection.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS TrpgWorld (
                    WorldId TEXT PRIMARY KEY,
                    OwnerUserId INTEGER NOT NULL,
                    GroupId INTEGER NOT NULL,
                    TeamName TEXT NOT NULL,
                    CampaignName TEXT NOT NULL DEFAULT 'default',
                    DisplayName TEXT NOT NULL DEFAULT '',
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_trpgworld_owner_group
                    ON TrpgWorld(OwnerUserId, GroupId, TeamName, CampaignName);

                CREATE TABLE IF NOT EXISTS CharacterSheet (
                    WorldId TEXT NOT NULL,
                    Id TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    StaticBackground TEXT,
                    DynamicStateJson TEXT,
                    UpdatedAt DATETIME,
                    PRIMARY KEY(WorldId, Id)
                );

                CREATE TABLE IF NOT EXISTS ChatHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL DEFAULT '',
                    MessageType TEXT NOT NULL,
                    SpeakerName TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    TokenCount INTEGER DEFAULT 0,
                    IsArchived INTEGER DEFAULT 0,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_chathistory_world_group_archived
                    ON ChatHistory(WorldId, GroupId, IsArchived, CreatedAt);
                CREATE INDEX IF NOT EXISTS idx_chathistory_world_character
                    ON ChatHistory(WorldId, CharacterId, IsArchived, CreatedAt);

                CREATE TABLE IF NOT EXISTS LongTermMemory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL DEFAULT 0,
                    CharacterId TEXT NOT NULL DEFAULT '',
                    Keywords TEXT NOT NULL,
                    Summary TEXT NOT NULL,
                    NodeType TEXT NOT NULL DEFAULT 'event',
                    Importance REAL NOT NULL DEFAULT 0.5,
                    Tier TEXT NOT NULL DEFAULT 'Session',
                    Heat REAL NOT NULL DEFAULT 0.5,
                    Embedding BLOB,
                    Superseded INTEGER NOT NULL DEFAULT 0,
                    SupersededBy INTEGER,
                    LastUsed DATETIME,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    Confidence REAL NOT NULL DEFAULT 1.0,
                    RawExcerpt TEXT NOT NULL DEFAULT '[]',
                    FoldCount INTEGER NOT NULL DEFAULT 0,
                    SceneId TEXT,
                    EntityId TEXT,
                    MemoryAudience TEXT NOT NULL DEFAULT 'CharacterIC',
                    OwnerCharacterId TEXT NULL,
                    SourceScope TEXT NULL,
                    SourceMessageIds TEXT DEFAULT '[]',
                    IcUsable INTEGER NOT NULL DEFAULT 1,
                    Metadata TEXT DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS idx_ltm_world_group_char
                    ON LongTermMemory(WorldId, GroupId, CharacterId);
                CREATE INDEX IF NOT EXISTS idx_ltm_world_scene
                    ON LongTermMemory(WorldId, SceneId);
                CREATE INDEX IF NOT EXISTS idx_ltm_world_entity
                    ON LongTermMemory(WorldId, EntityId);
                CREATE INDEX IF NOT EXISTS idx_ltm_world_group_char_audience
                    ON LongTermMemory(WorldId, GroupId, CharacterId, MemoryAudience);
                CREATE INDEX IF NOT EXISTS idx_ltm_world_group_audience
                    ON LongTermMemory(WorldId, GroupId, MemoryAudience);
                CREATE INDEX IF NOT EXISTS idx_ltm_world_owner_audience
                    ON LongTermMemory(WorldId, GroupId, OwnerCharacterId, MemoryAudience);

                CREATE TABLE IF NOT EXISTS LlmUsageLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAt TEXT NOT NULL,
                    Provider TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    AgentName TEXT NOT NULL,
                    RequestKind TEXT NOT NULL,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NULL,
                    TurnId TEXT NULL,
                    SourceMessageId TEXT NULL,
                    InputTokens INTEGER NOT NULL DEFAULT 0,
                    OutputTokens INTEGER NOT NULL DEFAULT 0,
                    CachedInputTokens INTEGER NULL,
                    CacheHitTokens INTEGER NULL,
                    CacheMissTokens INTEGER NULL,
                    EstimatedCost REAL NOT NULL DEFAULT 0,
                    Success INTEGER NOT NULL DEFAULT 0,
                    ErrorType TEXT NULL,
                    Metadata TEXT NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS idx_llm_usage_created
                    ON LlmUsageLog(CreatedAt);
                CREATE INDEX IF NOT EXISTS idx_llm_usage_world_group
                    ON LlmUsageLog(WorldId, GroupId, CreatedAt);
                CREATE INDEX IF NOT EXISTS idx_llm_usage_agent
                    ON LlmUsageLog(AgentName, RequestKind, CreatedAt);

                CREATE TABLE IF NOT EXISTS RawArchive (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    MemoryId INTEGER NOT NULL,
                    Content TEXT NOT NULL,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (MemoryId) REFERENCES LongTermMemory(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_raw_archive_world_memory
                    ON RawArchive(WorldId, MemoryId);

                CREATE TABLE IF NOT EXISTS Quest (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Status TEXT NOT NULL DEFAULT 'Active',
                    Priority TEXT NOT NULL DEFAULT 'Normal',
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    CompletedAt DATETIME,
                    UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    LastTouchedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    HiddenFromPrompt INTEGER NOT NULL DEFAULT 0,
                    SourceSceneId TEXT NOT NULL DEFAULT '',
                    LastMentionedSceneId TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS idx_quest_world_group_char
                    ON Quest(WorldId, GroupId, CharacterId, Status);

                CREATE TABLE IF NOT EXISTS EntityCanonical (
                    WorldId TEXT NOT NULL,
                    EntityId TEXT NOT NULL,
                    CurrentDisplayName TEXT NOT NULL,
                    Aliases TEXT NOT NULL,
                    IdentityStatus TEXT NOT NULL DEFAULT 'Tentative',
                    CoreSummary TEXT NOT NULL DEFAULT '',
                    EntityFactSummary TEXT NOT NULL DEFAULT '',
                    PersistentFacts TEXT NOT NULL DEFAULT '[]',
                    Relationships TEXT NOT NULL DEFAULT '{}',
                    Version INTEGER NOT NULL DEFAULT 1,
                    ConflictReason TEXT,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    LastUpdated DATETIME DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY(WorldId, EntityId)
                );
                CREATE INDEX IF NOT EXISTS idx_entitycanonical_world_name
                    ON EntityCanonical(WorldId, CurrentDisplayName);
                CREATE INDEX IF NOT EXISTS idx_entitycanonical_world_updated
                    ON EntityCanonical(WorldId, LastUpdated);

                CREATE TABLE IF NOT EXISTS EventLog (
                    EventId INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                    EventType TEXT NOT NULL,
                    Payload TEXT NOT NULL,
                    SourceEntityId TEXT,
                    TargetEntityId TEXT,
                    SceneId TEXT,
                    Consequences TEXT DEFAULT '[]',
                    SemanticSummary TEXT,
                    NarrativeWeight REAL DEFAULT 0.0,
                    NarrativeTags TEXT DEFAULT '[]',
                    EmotionalWeight REAL DEFAULT 0.0,
                    ArcAffinity TEXT,
                    IsSemanticallyDistilled INTEGER DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_eventlog_world_entity
                    ON EventLog(WorldId, SourceEntityId, TargetEntityId);
                CREATE INDEX IF NOT EXISTS idx_eventlog_world_scene
                    ON EventLog(WorldId, SceneId);
                CREATE INDEX IF NOT EXISTS idx_eventlog_world_time
                    ON EventLog(WorldId, EventId, Timestamp);

                CREATE TABLE IF NOT EXISTS CausalGraph (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL DEFAULT 0,
                    CharacterId TEXT NOT NULL DEFAULT '',
                    SourceEventId INTEGER NOT NULL,
                    TargetEventId INTEGER NOT NULL,
                    EdgeType TEXT NOT NULL,
                    Weight REAL DEFAULT 1.0,
                    CreatedFoldCount INTEGER NOT NULL DEFAULT 0,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_causalgraph_world_group_char
                    ON CausalGraph(WorldId, GroupId, CharacterId);
                CREATE INDEX IF NOT EXISTS idx_causalgraph_world_source
                    ON CausalGraph(WorldId, SourceEventId);
                CREATE INDEX IF NOT EXISTS idx_causalgraph_world_target
                    ON CausalGraph(WorldId, TargetEventId);

                CREATE TABLE IF NOT EXISTS NarrativeMemoryNode (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    Summary TEXT NOT NULL,
                    NarrativeWeight REAL DEFAULT 0.5,
                    EmotionalWeight REAL DEFAULT 0.0,
                    RelationshipImpact REAL DEFAULT 0.0,
                    GoalImpact REAL DEFAULT 0.0,
                    MysteryWeight REAL DEFAULT 0.0,
                    IsResolved INTEGER DEFAULT 0,
                    InvolvedEntities TEXT DEFAULT '[]',
                    ArcTags TEXT DEFAULT '[]',
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                    CreatedFoldCount INTEGER NOT NULL DEFAULT 0,
                    SourceEventId INTEGER NOT NULL,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_narrativememory_world_group
                    ON NarrativeMemoryNode(WorldId, GroupId, CharacterId);
                CREATE INDEX IF NOT EXISTS idx_narrativememory_world_event
                    ON NarrativeMemoryNode(WorldId, SourceEventId);

                CREATE TABLE IF NOT EXISTS CharacterMemory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    MemoryType TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    Confidence REAL DEFAULT 1.0,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP,
                    RelatedEventId INTEGER,
                    Metadata TEXT DEFAULT '{}',
                    IsFoundational INTEGER NOT NULL DEFAULT 0,
                    RelatedEntityId TEXT,
                    FoldCount INTEGER NOT NULL DEFAULT 0,
                    LastAccessedFoldCount INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_charactermemory_world_group
                    ON CharacterMemory(WorldId, GroupId, CharacterId);
                CREATE INDEX IF NOT EXISTS idx_charactermemory_world_type
                    ON CharacterMemory(WorldId, MemoryType);
                CREATE INDEX IF NOT EXISTS idx_charactermemory_world_event
                    ON CharacterMemory(WorldId, RelatedEventId);

                CREATE TABLE IF NOT EXISTS AffectiveTagState (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    TagType TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    SourceKey TEXT NOT NULL,
                    TargetEntityId TEXT NULL,
                    IntensityTier TEXT NOT NULL,
                    Charge REAL DEFAULT 0,
                    ChargeCap REAL DEFAULT 1,
                    RepetitionCount INTEGER DEFAULT 0,
                    AdaptationLevel REAL DEFAULT 0,
                    Status TEXT DEFAULT 'Active',
                    LastEvidence TEXT DEFAULT '',
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    LastAppliedFoldCount INTEGER DEFAULT 0,
                    ExpirePolicy TEXT DEFAULT 'Scene',
                    Metadata TEXT DEFAULT '{}'
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_affectivestate_identity
                    ON AffectiveTagState(WorldId, GroupId, CharacterId, TagType, SourceKey, TargetEntityId);
                CREATE INDEX IF NOT EXISTS idx_affectivestate_active
                    ON AffectiveTagState(WorldId, GroupId, CharacterId, Status, UpdatedAt);

                CREATE TABLE IF NOT EXISTS AffectiveTagEvent (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    SourceEventId INTEGER NULL,
                    TagType TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    SourceKey TEXT NOT NULL,
                    TargetEntityId TEXT NULL,
                    EffectKind TEXT NOT NULL,
                    IntensityTier TEXT NOT NULL,
                    Novelty TEXT DEFAULT 'Medium',
                    Evidence TEXT DEFAULT '',
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    FoldCount INTEGER DEFAULT 0,
                    Metadata TEXT DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS idx_affectiveevent_world_group
                    ON AffectiveTagEvent(WorldId, GroupId, CharacterId, CreatedAt);

                CREATE TABLE IF NOT EXISTS SceneSnapshot (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL DEFAULT 0,
                    CharacterId TEXT NOT NULL DEFAULT '',
                    SceneId TEXT NOT NULL,
                    SceneDescription TEXT NOT NULL DEFAULT '',
                    PresentEntities TEXT NOT NULL DEFAULT '[]',
                    StateProperties TEXT NOT NULL DEFAULT '{}',
                    SnapshotReason TEXT NOT NULL DEFAULT '',
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    EnteredAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PresentEntityIds TEXT NOT NULL DEFAULT '[]',
                    SceneGoals TEXT NOT NULL DEFAULT '[]',
                    OutstandingThreads TEXT NOT NULL DEFAULT '[]',
                    SceneFlags TEXT NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS idx_scenesnapshot_world_scene
                    ON SceneSnapshot(WorldId, SceneId);
                CREATE INDEX IF NOT EXISTS idx_scenesnapshot_world_group_char
                    ON SceneSnapshot(WorldId, GroupId, CharacterId, CreatedAt);

                CREATE TABLE IF NOT EXISTS NpcCanonicalState (
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    NpcId TEXT NOT NULL,
                    DisplayName TEXT NOT NULL DEFAULT '',
                    CoreSummary TEXT NOT NULL DEFAULT '',
                    IdentityState TEXT NOT NULL DEFAULT '',
                    KeyEventsDigest TEXT NOT NULL DEFAULT '',
                    RelationshipState TEXT NOT NULL DEFAULT '',
                    PendingRelationshipDeltaJson TEXT NOT NULL DEFAULT '{}',
                    LastSummaryUpdatedAt TEXT NOT NULL DEFAULT '',
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (WorldId, GroupId, NpcId)
                );

                CREATE TABLE IF NOT EXISTS AiCharacterEntry (
                    WorldId TEXT NOT NULL,
                    CharacterId TEXT NOT NULL,
                    VirtualId INTEGER NOT NULL,
                    OwnerUserId INTEGER NOT NULL,
                    GroupId INTEGER NOT NULL,
                    TeamName TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    StaticBackground TEXT NOT NULL DEFAULT '',
                    DynamicStateJson TEXT NOT NULL DEFAULT '{}',
                    SkillsJson TEXT NOT NULL DEFAULT '{}',
                    InventoryJson TEXT NOT NULL DEFAULT '[]',
                    RuleText TEXT NOT NULL DEFAULT '',
                    IsActive INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY(WorldId, CharacterId)
                );
                CREATE INDEX IF NOT EXISTS idx_aichar_world_team
                    ON AiCharacterEntry(WorldId, TeamName, IsActive);
                CREATE INDEX IF NOT EXISTS idx_aichar_owner_group_team
                    ON AiCharacterEntry(OwnerUserId, GroupId, TeamName);
                CREATE INDEX IF NOT EXISTS idx_aichar_world_virtualid
                    ON AiCharacterEntry(WorldId, VirtualId);

                CREATE TABLE IF NOT EXISTS CharacterInventoryItem (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    ItemKey TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    Quantity REAL NOT NULL DEFAULT 1,
                    Unit TEXT NOT NULL DEFAULT '',
                    State TEXT NOT NULL DEFAULT 'carried',
                    Description TEXT NOT NULL DEFAULT '',
                    LocationHint TEXT NOT NULL DEFAULT '',
                    OwnerEntityId TEXT NOT NULL DEFAULT '',
                    SourceKind TEXT NOT NULL DEFAULT 'InitialSeed',
                    AuthorityRank INTEGER NOT NULL DEFAULT 70,
                    Confidence REAL NOT NULL DEFAULT 1.0,
                    IsAssumed INTEGER NOT NULL DEFAULT 0,
                    IsContradicted INTEGER NOT NULL DEFAULT 0,
                    NeedsReview INTEGER NOT NULL DEFAULT 0,
                    SourceEventId INTEGER NULL,
                    LastEventId INTEGER NULL,
                    LastEvidence TEXT NOT NULL DEFAULT '',
                    IsVisibleToCharacter INTEGER NOT NULL DEFAULT 1,
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    Metadata TEXT NOT NULL DEFAULT '{}',
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(WorldId, GroupId, CharacterId, ItemKey)
                );
                CREATE INDEX IF NOT EXISTS idx_inventory_active
                    ON CharacterInventoryItem(WorldId, GroupId, CharacterId, IsActive, State);
                CREATE INDEX IF NOT EXISTS idx_inventory_source_event
                    ON CharacterInventoryItem(WorldId, SourceEventId, LastEventId);

                CREATE TABLE IF NOT EXISTS CharacterInventorySeedState (
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    SeedHash TEXT NOT NULL DEFAULT '',
                    ImportedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    SourceEventId INTEGER NULL,
                    PRIMARY KEY(WorldId, GroupId, CharacterId)
                );

                CREATE TABLE IF NOT EXISTS SceneDictionary (
                    WorldId TEXT NOT NULL,
                    SceneId TEXT NOT NULL,
                    SceneBaseDesc TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY(WorldId, SceneId)
                );

                CREATE TABLE IF NOT EXISTS CharacterHotMeta (
                    WorldId TEXT NOT NULL,
                    CharId TEXT NOT NULL,
                    ShortTags TEXT NOT NULL DEFAULT '',
                    Aliases TEXT NOT NULL DEFAULT '',
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY(WorldId, CharId)
                );
                CREATE INDEX IF NOT EXISTS idx_characterhotmeta_world_aliases
                    ON CharacterHotMeta(WorldId, Aliases);

                CREATE TABLE IF NOT EXISTS AiCharacterRuntimeControl (
                    WorldId TEXT NOT NULL,
                    CharacterId TEXT NOT NULL,
                    Mode TEXT NOT NULL DEFAULT 'act',
                    UpdatedAt TEXT NOT NULL,
                    UpdatedByUserId INTEGER,
                    PRIMARY KEY (WorldId, CharacterId)
                );

                CREATE TABLE IF NOT EXISTS BehaviorEvidence (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    NpcId TEXT NOT NULL,
                    Trait TEXT NOT NULL,
                    Evidence REAL NOT NULL DEFAULT 0.0,
                    LastUpdated DATETIME NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_behaviorevidence_world
                    ON BehaviorEvidence(WorldId, GroupId, CharacterId, NpcId);

                CREATE TABLE IF NOT EXISTS TimelineNodes (
                    Id TEXT NOT NULL,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    Layer TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    ParentId TEXT,
                    SceneId TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL DEFAULT 'Visible',
                    Importance INTEGER NOT NULL DEFAULT 5,
                    Foreshadowing INTEGER NOT NULL DEFAULT 0,
                    EventSequence INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    PRIMARY KEY(WorldId, Id)
                );
                CREATE INDEX IF NOT EXISTS idx_timeline_world_group
                    ON TimelineNodes(WorldId, GroupId, CharacterId);
                CREATE INDEX IF NOT EXISTS idx_timeline_world_scene
                    ON TimelineNodes(WorldId, GroupId, CharacterId, SceneId);
                CREATE INDEX IF NOT EXISTS idx_timeline_world_parent
                    ON TimelineNodes(WorldId, ParentId);
                CREATE INDEX IF NOT EXISTS idx_timeline_world_status
                    ON TimelineNodes(WorldId, GroupId, CharacterId, Layer, Status);
                """;
            await createCmd.ExecuteNonQueryAsync();
        }

        using (var writeVersionCmd = _connection.CreateCommand())
        {
            writeVersionCmd.Transaction = tx;
            writeVersionCmd.CommandText = """
                INSERT INTO AimodSchemaVersion (Key, Version, UpdatedAt)
                VALUES (@key, @version, @updatedAt)
                ON CONFLICT(Key) DO UPDATE SET
                    Version = @version,
                    UpdatedAt = @updatedAt
                """;
            writeVersionCmd.Parameters.AddWithValue("@key", TrpgSchemaVersionKey);
            writeVersionCmd.Parameters.AddWithValue("@version", TrpgSchemaVersion);
            writeVersionCmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
            await writeVersionCmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public async Task EnsureTrpgWorldAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TrpgWorld (WorldId, OwnerUserId, GroupId, TeamName, CampaignName, DisplayName, IsActive, CreatedAt, UpdatedAt)
            VALUES (@worldId, @ownerUserId, @groupId, @teamName, @campaignName, @displayName, 1, @now, @now)
            ON CONFLICT(WorldId) DO UPDATE SET
                OwnerUserId = @ownerUserId,
                GroupId = @groupId,
                TeamName = @teamName,
                CampaignName = @campaignName,
                DisplayName = @displayName,
                IsActive = 1,
                UpdatedAt = @now
            """;
        var now = DateTime.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@ownerUserId", scope.OwnerUserId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@teamName", scope.TeamName);
        cmd.Parameters.AddWithValue("@campaignName", scope.CampaignName);
        cmd.Parameters.AddWithValue("@displayName", $"{scope.TeamName}/{scope.CampaignName}");
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task MigrateNarrativeMemoryNodeV2Async()
    {
        try
        {
            using var pragmaCmd = _connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA table_info(NarrativeMemoryNode)";
            using var reader = await pragmaCmd.ExecuteReaderAsync();
            var hasCreatedFoldCount = false;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) == "CreatedFoldCount")
                {
                    hasCreatedFoldCount = true;
                    break;
                }
            }

            if (!hasCreatedFoldCount)
            {
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE NarrativeMemoryNode ADD COLUMN CreatedFoldCount INTEGER NOT NULL DEFAULT 0";
                await alterCmd.ExecuteNonQueryAsync();
                _context.Log(LogLevel.Info, "[AIMod:TRPG] Migrated: Added CreatedFoldCount to NarrativeMemoryNode");
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] NarrativeMemoryNode migration skipped: {ex.Message}");
        }
    }

    private async Task MigrateAddCharacterIdColumnAsync()
    {
        try
        {
            using var pragmaCmd = _connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA table_info(ChatHistory)";
            using var reader = await pragmaCmd.ExecuteReaderAsync();
            bool hasCharacterId = false;
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1);
                if (name == "CharacterId")
                {
                    hasCharacterId = true;
                    break;
                }
            }

            if (!hasCharacterId)
            {
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE ChatHistory ADD COLUMN CharacterId TEXT NOT NULL DEFAULT ''";
                await alterCmd.ExecuteNonQueryAsync();
                _context.Log(LogLevel.Info, "[AIMod:TRPG] Migrated: Added CharacterId column to ChatHistory");
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Migration check skipped: {ex.Message}");
        }
    }

    private async Task MigrateLongTermMemoryColumnsAsync()
    {
        try
        {
            var columnsToAdd = new List<(string name, string type)>
            {
                ("GroupId", "INTEGER NOT NULL DEFAULT 0"),
                ("CharacterId", "TEXT NOT NULL DEFAULT ''"),
                ("NodeType", "TEXT NOT NULL DEFAULT 'event'"),
                ("Importance", "REAL NOT NULL DEFAULT 0.5"),
                ("Tier", "TEXT NOT NULL DEFAULT 'Session'"),
                ("Heat", "REAL NOT NULL DEFAULT 0.5"),
                ("Superseded", "INTEGER NOT NULL DEFAULT 0"),
                ("SupersededBy", "INTEGER"),
                ("LastUsed", "DATETIME"),
                ("Confidence", "REAL NOT NULL DEFAULT 1.0"),
                ("RawExcerpt", "TEXT NOT NULL DEFAULT '[]'"),
                ("FoldCount", "INTEGER NOT NULL DEFAULT 0")
            };

            foreach (var (colName, colType) in columnsToAdd)
            {
                using var pragmaCmd = _connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA table_info(LongTermMemory)";
                using var reader = await pragmaCmd.ExecuteReaderAsync();
                bool exists = false;
                while (await reader.ReadAsync())
                {
                    if (reader.GetString(1) == colName)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    using var alterCmd = _connection.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE LongTermMemory ADD COLUMN {colName} {colType}";
                    await alterCmd.ExecuteNonQueryAsync();
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Migrated: Added {colName} to LongTermMemory");
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] LongTermMemory migration skipped: {ex.Message}");
        }
    }

    private async Task EnsureMemoryAudienceSchemaAsync()
    {
        try
        {
            var columnsToAdd = new List<(string name, string type)>
            {
                ("MemoryAudience", "TEXT NOT NULL DEFAULT 'CharacterIC'"),
                ("OwnerCharacterId", "TEXT NULL"),
                ("SourceScope", "TEXT NULL"),
                ("SourceMessageIds", "TEXT DEFAULT '[]'"),
                ("IcUsable", "INTEGER NOT NULL DEFAULT 1"),
                ("Metadata", "TEXT DEFAULT '{}'")
            };

            foreach (var (colName, colType) in columnsToAdd)
            {
                if (await ColumnExistsAsync("LongTermMemory", colName))
                    continue;
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE LongTermMemory ADD COLUMN {colName} {colType}";
                await alterCmd.ExecuteNonQueryAsync();
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] Migrated: Added {colName} to LongTermMemory");
            }

            using var updateCmd = _connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE LongTermMemory
                SET MemoryAudience = COALESCE(NULLIF(MemoryAudience, ''), 'CharacterIC'),
                    OwnerCharacterId = CASE
                        WHEN COALESCE(NULLIF(OwnerCharacterId, ''), '') = '' THEN CharacterId
                        ELSE OwnerCharacterId
                    END,
                    SourceScope = COALESCE(NULLIF(SourceScope, ''), 'IC'),
                    SourceMessageIds = COALESCE(NULLIF(SourceMessageIds, ''), '[]'),
                    IcUsable = CASE WHEN COALESCE(NULLIF(MemoryAudience, ''), 'CharacterIC') = 'PlayerTable' THEN 0 ELSE 1 END,
                    Metadata = COALESCE(NULLIF(Metadata, ''), '{}')
                """;
            await updateCmd.ExecuteNonQueryAsync();

            using var indexCmd = _connection.CreateCommand();
            indexCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_ltm_world_group_char_audience
                    ON LongTermMemory(WorldId, GroupId, CharacterId, MemoryAudience);
                CREATE INDEX IF NOT EXISTS idx_ltm_world_group_audience
                    ON LongTermMemory(WorldId, GroupId, MemoryAudience);
                CREATE INDEX IF NOT EXISTS idx_ltm_world_owner_audience
                    ON LongTermMemory(WorldId, GroupId, OwnerCharacterId, MemoryAudience);
                """;
            await indexCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] MemoryAudience schema migration skipped: {ex.Message}");
        }
    }

    private async Task EnsureLlmUsageLogSchemaAsync()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS LlmUsageLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAt TEXT NOT NULL,
                    Provider TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    AgentName TEXT NOT NULL,
                    RequestKind TEXT NOT NULL,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NULL,
                    TurnId TEXT NULL,
                    SourceMessageId TEXT NULL,
                    InputTokens INTEGER NOT NULL DEFAULT 0,
                    OutputTokens INTEGER NOT NULL DEFAULT 0,
                    CachedInputTokens INTEGER NULL,
                    CacheHitTokens INTEGER NULL,
                    CacheMissTokens INTEGER NULL,
                    EstimatedCost REAL NOT NULL DEFAULT 0,
                    Success INTEGER NOT NULL DEFAULT 0,
                    ErrorType TEXT NULL,
                    Metadata TEXT NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS idx_llm_usage_created
                    ON LlmUsageLog(CreatedAt);
                CREATE INDEX IF NOT EXISTS idx_llm_usage_world_group
                    ON LlmUsageLog(WorldId, GroupId, CreatedAt);
                CREATE INDEX IF NOT EXISTS idx_llm_usage_agent
                    ON LlmUsageLog(AgentName, RequestKind, CreatedAt);
                """;
            await cmd.ExecuteNonQueryAsync();
            await EnsureColumnAsync("LlmUsageLog", "TurnId", "TEXT NULL");
            await EnsureColumnAsync("LlmUsageLog", "SourceMessageId", "TEXT NULL");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] LlmUsageLog schema migration skipped: {ex.Message}");
        }
    }

    private async Task EnsureQuestSchemaAsync()
    {
        try
        {
            await EnsureColumnAsync("Quest", "LastTouchedAt", "DATETIME DEFAULT CURRENT_TIMESTAMP");
            await EnsureColumnAsync("Quest", "HiddenFromPrompt", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync("Quest", "SourceSceneId", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync("Quest", "LastMentionedSceneId", "TEXT NOT NULL DEFAULT ''");

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE Quest
                SET LastTouchedAt = COALESCE(NULLIF(LastTouchedAt, ''), UpdatedAt, CreatedAt),
                    HiddenFromPrompt = COALESCE(HiddenFromPrompt, 0),
                    SourceSceneId = COALESCE(NULLIF(SourceSceneId, ''), ''),
                    LastMentionedSceneId = COALESCE(NULLIF(LastMentionedSceneId, ''), COALESCE(NULLIF(SourceSceneId, ''), ''))
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Quest schema migration skipped: {ex.Message}");
        }
    }

    private async Task EnsureEntityCanonicalSchemaAsync()
    {
        try
        {
            await EnsureColumnAsync("EntityCanonical", "EntityFactSummary", "TEXT NOT NULL DEFAULT ''");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] EntityCanonical schema migration skipped: {ex.Message}");
        }
    }

    private async Task EnsureDebugSchemaAsync()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS AiDebugSetting (
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    DebugEnabled INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY(WorldId, GroupId)
                );

                CREATE TABLE IF NOT EXISTS LlmDebugLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAt TEXT NOT NULL,
                    WorldId TEXT NOT NULL,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NULL,
                    AgentName TEXT NOT NULL,
                    RequestKind TEXT NOT NULL,
                    MessagesJson TEXT NOT NULL,
                    ResponseText TEXT NULL,
                    Success INTEGER NOT NULL DEFAULT 1,
                    Error TEXT NULL,
                    InputCharCount INTEGER NOT NULL DEFAULT 0,
                    OutputCharCount INTEGER NOT NULL DEFAULT 0,
                    Metadata TEXT DEFAULT '{}'
                );

                CREATE INDEX IF NOT EXISTS idx_llm_debug_world_group_created
                    ON LlmDebugLog(WorldId, GroupId, CreatedAt);

                CREATE INDEX IF NOT EXISTS idx_llm_debug_world_group_agent_created
                    ON LlmDebugLog(WorldId, GroupId, AgentName, CreatedAt);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Debug schema initialization skipped: {ex.Message}");
        }
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        using var pragmaCmd = _connection.CreateCommand();
        pragmaCmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = await pragmaCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task EnsureColumnAsync(string tableName, string columnName, string definition)
    {
        if (await ColumnExistsAsync(tableName, columnName))
            return;

        using var alterCmd = _connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        await alterCmd.ExecuteNonQueryAsync();
    }

    private async Task MigrateSceneSnapshotTableAsync()
    {
        try
        {
            using var pragmaCmd = _connection.CreateCommand();
            pragmaCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='SceneSnapshot'";
            var result = await pragmaCmd.ExecuteScalarAsync();
            if (result == null)
            {
                using var createCmd = _connection.CreateCommand();
                createCmd.CommandText = """
                    CREATE TABLE SceneSnapshot (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        GroupId INTEGER NOT NULL DEFAULT 0,
                        CharacterId TEXT NOT NULL DEFAULT '',
                        SceneId TEXT NOT NULL,
                        SceneDescription TEXT NOT NULL DEFAULT '',
                        PresentEntities TEXT NOT NULL DEFAULT '[]',
                        StateProperties TEXT NOT NULL DEFAULT '{}',
                        SnapshotReason TEXT NOT NULL DEFAULT '',
                        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        EnteredAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        PresentEntityIds TEXT NOT NULL DEFAULT '[]',
                        SceneGoals TEXT NOT NULL DEFAULT '[]',
                        OutstandingThreads TEXT NOT NULL DEFAULT '[]',
                        SceneFlags TEXT NOT NULL DEFAULT '{}'
                    )
                    """;
                await createCmd.ExecuteNonQueryAsync();
                _context.Log(LogLevel.Info, "[AIMod:TRPG] Migrated: Created SceneSnapshot table");
            }
            else
            {
                var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var columnsCmd = _connection.CreateCommand();
                columnsCmd.CommandText = "PRAGMA table_info(SceneSnapshot)";
                using var reader = await columnsCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }

                var requiredColumns = new (string Name, string Definition)[]
                {
                    ("GroupId", "INTEGER NOT NULL DEFAULT 0"),
                    ("CharacterId", "TEXT NOT NULL DEFAULT ''"),
                    ("SceneDescription", "TEXT NOT NULL DEFAULT ''"),
                    ("PresentEntities", "TEXT NOT NULL DEFAULT '[]'"),
                    ("StateProperties", "TEXT NOT NULL DEFAULT '{}'"),
                    ("SnapshotReason", "TEXT NOT NULL DEFAULT ''"),
                    ("CreatedAt", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"),
                    ("EnteredAt", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"),
                    ("PresentEntityIds", "TEXT NOT NULL DEFAULT '[]'"),
                    ("SceneGoals", "TEXT NOT NULL DEFAULT '[]'"),
                    ("OutstandingThreads", "TEXT NOT NULL DEFAULT '[]'"),
                    ("SceneFlags", "TEXT NOT NULL DEFAULT '{}'")
                };

                foreach (var (name, definition) in requiredColumns)
                {
                    if (existingColumns.Contains(name))
                        continue;

                    using var alterCmd = _connection.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE SceneSnapshot ADD COLUMN {name} {definition}";
                    await alterCmd.ExecuteNonQueryAsync();
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Migrated: Added {name} to SceneSnapshot");
                }
            }

            using var idxSceneCmd = _connection.CreateCommand();
            idxSceneCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_scenesnapshot_scene ON SceneSnapshot(SceneId)";
            await idxSceneCmd.ExecuteNonQueryAsync();

            using var idxGroupCharCmd = _connection.CreateCommand();
            idxGroupCharCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_scenesnapshot_group_char ON SceneSnapshot(GroupId, CharacterId, CreatedAt)";
            await idxGroupCharCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] SceneSnapshot migration skipped: {ex.Message}");
        }
    }

    private async Task MigrateBehaviorEvidenceTableAsync()
    {
        try
        {
            using var pragmaCmd = _connection.CreateCommand();
            pragmaCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='BehaviorEvidence'";
            var result = await pragmaCmd.ExecuteScalarAsync();
            if (result != null)
                return; // Table already exists

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE BehaviorEvidence (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GroupId INTEGER NOT NULL,
                    CharacterId TEXT NOT NULL,
                    NpcId TEXT NOT NULL,
                    Trait TEXT NOT NULL,
                    Evidence REAL NOT NULL DEFAULT 0.0,
                    LastUpdated DATETIME NOT NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync();
            _context.Log(LogLevel.Info, "[AIMod:TRPG] Migrated: Created BehaviorEvidence table");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] BehaviorEvidence migration skipped: {ex.Message}");
        }
    }

    private async Task RebuildLegacyLongTermMemoryIfNeededAsync()
    {
        try
        {
            using var checkCmd = _connection.CreateCommand();
            checkCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='LongTermMemory'";
            var sqlObj = await checkCmd.ExecuteScalarAsync();
            var sql = sqlObj?.ToString() ?? "";
            if (!sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                return;

            using var tx = _connection.BeginTransaction();
            var dropCmd = _connection.CreateCommand();
            dropCmd.Transaction = tx;
            dropCmd.CommandText = "DROP TABLE IF EXISTS LongTermMemory";
            await dropCmd.ExecuteNonQueryAsync();

            var createCmd = _connection.CreateCommand();
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS LongTermMemory (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    GroupId     INTEGER NOT NULL DEFAULT 0,
                    CharacterId TEXT NOT NULL DEFAULT '',
                    Keywords    TEXT NOT NULL,
                    Summary     TEXT NOT NULL,
                    NodeType    TEXT NOT NULL DEFAULT 'event',
                    Importance  REAL NOT NULL DEFAULT 0.5,
                    Tier        TEXT NOT NULL DEFAULT 'Session',
                    Heat        REAL NOT NULL DEFAULT 0.5,
                    Embedding   BLOB,
                    Superseded  INTEGER NOT NULL DEFAULT 0,
                    SupersededBy INTEGER,
                    LastUsed    DATETIME,
                    CreatedAt   DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                """;
            await createCmd.ExecuteNonQueryAsync();

            tx.Commit();
            _context.Log(LogLevel.Warn, "[AIMod:TRPG] Migrated legacy LongTermMemory virtual table to normal table; old rows were reset.");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Legacy LongTermMemory rebuild skipped: {ex.Message}");
        }
    }

    private async Task MigrateAiCharacterSchemaAsync()
    {
        try
        {
            // 检查旧列是否存在
            using var pragmaCmd = _connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA table_info(AiCharacterEntry)";
            using var reader = await pragmaCmd.ExecuteReaderAsync();
            bool hasOldColumns = false;
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1);
                if (name == "AttributesJson" || name == "SpellsJson")
                {
                    hasOldColumns = true;
                    break;
                }
            }

            if (!hasOldColumns) return; // 已经是新 schema

            // SQLite 不支持 DROP COLUMN，需要重建表
            using var tx = _connection.BeginTransaction();
            try
            {
                var createNew = _connection.CreateCommand();
                createNew.Transaction = tx;
                createNew.CommandText = """
                    CREATE TABLE AiCharacterEntry_new (
                        CharacterId   TEXT PRIMARY KEY,
                        VirtualId     INTEGER NOT NULL,
                        GroupId       INTEGER NOT NULL,
                        TeamName      TEXT NOT NULL,
                        DisplayName  TEXT NOT NULL,
                        StaticBackground TEXT NOT NULL DEFAULT '',
                        DynamicStateJson TEXT NOT NULL DEFAULT '{}',
                        SkillsJson       TEXT NOT NULL DEFAULT '{}',
                        InventoryJson    TEXT NOT NULL DEFAULT '[]',
                        RuleText         TEXT NOT NULL DEFAULT '',
                        IsActive      INTEGER NOT NULL DEFAULT 0,
                        CreatedAt     TEXT NOT NULL,
                        UpdatedAt     TEXT NOT NULL
                    )
                    """;
                await createNew.ExecuteNonQueryAsync();

                var copyData = _connection.CreateCommand();
                copyData.Transaction = tx;
                copyData.CommandText = """
                    INSERT INTO AiCharacterEntry_new
                        (CharacterId, VirtualId, GroupId, TeamName, DisplayName,
                         StaticBackground, DynamicStateJson, SkillsJson,
                         InventoryJson, RuleText, IsActive, CreatedAt, UpdatedAt)
                    SELECT CharacterId, VirtualId, GroupId, TeamName, DisplayName,
                           StaticBackground, DynamicStateJson,
                           COALESCE(NULLIF(SkillsJson, '[]'), '{}'),
                           InventoryJson, COALESCE(RuleText, ''), IsActive, CreatedAt, UpdatedAt
                    FROM AiCharacterEntry
                    """;
                await copyData.ExecuteNonQueryAsync();

                var dropOld = _connection.CreateCommand();
                dropOld.Transaction = tx;
                dropOld.CommandText = "DROP TABLE AiCharacterEntry";
                await dropOld.ExecuteNonQueryAsync();

                var rename = _connection.CreateCommand();
                rename.Transaction = tx;
                rename.CommandText = "ALTER TABLE AiCharacterEntry_new RENAME TO AiCharacterEntry";
                await rename.ExecuteNonQueryAsync();

                var idx1 = _connection.CreateCommand();
                idx1.Transaction = tx;
                idx1.CommandText = "CREATE INDEX IF NOT EXISTS idx_aichar_group_team ON AiCharacterEntry(GroupId, TeamName)";
                await idx1.ExecuteNonQueryAsync();

                var idx2 = _connection.CreateCommand();
                idx2.Transaction = tx;
                idx2.CommandText = "CREATE INDEX IF NOT EXISTS idx_aichar_virtualid ON AiCharacterEntry(VirtualId)";
                await idx2.ExecuteNonQueryAsync();

                tx.Commit();
                _context.Log(LogLevel.Info, "[AIMod:TRPG] Migrated: Rebuilt AiCharacterEntry table (removed AttributesJson/SpellsJson, SkillsJson defaults to '{}')");
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] AiCharacterEntry migration skipped: {ex.Message}");
        }
    }

    private async Task MigrateEntityCanonicalSchemaAsync()
    {
        try
        {
            // 检查新列是否存在
            using var pragmaCmd = _connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA table_info(EntityCanonical)";
            using var reader = await pragmaCmd.ExecuteReaderAsync();
            bool hasNewColumns = false;
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1);
                if (name == "CoreSummary" || name == "PersistentFacts" || name == "Relationships")
                {
                    hasNewColumns = true;
                    break;
                }
            }

            if (hasNewColumns) return; // 已经是新 schema

            // SQLite 不支持 DROP COLUMN，需要重建表
            using var tx = _connection.BeginTransaction();
            try
            {
                var createNew = _connection.CreateCommand();
                createNew.Transaction = tx;
                createNew.CommandText = """
                    CREATE TABLE EntityCanonical_new (
                        EntityId TEXT PRIMARY KEY,
                        CurrentDisplayName TEXT NOT NULL,
                        Aliases TEXT NOT NULL,
                        IdentityStatus TEXT NOT NULL DEFAULT 'Tentative',
                        CoreSummary TEXT NOT NULL DEFAULT '',
                        PersistentFacts TEXT NOT NULL DEFAULT '[]',
                        Relationships TEXT NOT NULL DEFAULT '{}',
                        Version INTEGER NOT NULL DEFAULT 1,
                        ConflictReason TEXT,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        LastUpdated DATETIME DEFAULT CURRENT_TIMESTAMP
                    )
                    """;
                await createNew.ExecuteNonQueryAsync();

                var copyData = _connection.CreateCommand();
                copyData.Transaction = tx;
                copyData.CommandText = """
                    INSERT INTO EntityCanonical_new
                        (EntityId, CurrentDisplayName, Aliases, IdentityStatus, CreatedAt, LastUpdated)
                    SELECT EntityId, CurrentDisplayName, Aliases, IdentityStatus, CreatedAt, LastUpdated
                    FROM EntityCanonical
                    """;
                await copyData.ExecuteNonQueryAsync();

                var dropOld = _connection.CreateCommand();
                dropOld.Transaction = tx;
                dropOld.CommandText = "DROP TABLE EntityCanonical";
                await dropOld.ExecuteNonQueryAsync();

                var rename = _connection.CreateCommand();
                rename.Transaction = tx;
                rename.CommandText = "ALTER TABLE EntityCanonical_new RENAME TO EntityCanonical";
                await rename.ExecuteNonQueryAsync();

                tx.Commit();
                _context.Log(LogLevel.Info, "[AIMod:TRPG] Migrated: Rebuilt EntityCanonical table (added CoreSummary, PersistentFacts, Relationships, Version, ConflictReason)");
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] EntityCanonical migration skipped: {ex.Message}");
        }
    }

    // ── ChatHistory CRUD ──

    public async Task<int> InsertHistoryAsync(TrpgScope scope, string characterId, string messageType, string speakerName, string role, string content)
    {
        var groupId = scope.GroupId;
        var tokenCount = EstimateTokenCount(content);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ChatHistory (WorldId, GroupId, CharacterId, MessageType, SpeakerName, Role, Content, TokenCount, IsArchived, CreatedAt)
            VALUES (@worldId, @groupId, @characterId, @messageType, @speakerName, @role, @content, @tokenCount, 0, @createdAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@messageType", messageType);
        cmd.Parameters.AddWithValue("@speakerName", speakerName);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@tokenCount", tokenCount);
        cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : -1;
    }

    public async Task<List<ChatHistoryEntry>> GetActiveHistoryAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        var entries = new List<ChatHistoryEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, MessageType, SpeakerName, Role, Content, TokenCount, IsArchived, CreatedAt
            FROM ChatHistory
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND IsArchived = 0 AND MessageType != 'SYSTEM'
            ORDER BY CreatedAt ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new ChatHistoryEntry
            {
                Id = reader.GetInt32(0),
                WorldId = reader.GetString(1),
                GroupId = reader.GetInt64(2),
                CharacterId = reader.IsDBNull(3) ? "" : reader.GetString(3),
                MessageType = reader.GetString(4),
                SpeakerName = reader.GetString(5),
                Role = reader.GetString(6),
                Content = reader.GetString(7),
                TokenCount = reader.GetInt32(8),
                IsArchived = reader.GetInt32(9),
                CreatedAt = DateTime.Parse(reader.GetString(10))
            });
        }
        return entries;
    }

    public async Task<MemoryNode?> SearchBestMemoryNodeAsync(TrpgScope scope, string characterId, string query, double minSimilarity, float[]? queryEmbedding = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var candidates = await GetAllMemoryNodesAsync(scope, characterId, limit: 50);
        MemoryNode? best = null;
        double bestScore = 0;

        foreach (var node in candidates)
        {
            double score;
            if (queryEmbedding != null && node.Embedding != null)
            {
                score = VectorSearch.CosineSimilarity(queryEmbedding, node.Embedding);
            }
            else
            {
                score = ComputeSimilarity(query, $"{node.Keywords} {node.Summary}");
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        if (best == null || bestScore < minSimilarity)
            return null;

        await UpdateMemoryNodeLastUsedAsync(scope, best.Id);
        return best;
    }

    public async Task<List<MemoryNode>> SearchMemoryNodesBySimilarityAsync(TrpgScope scope, string characterId, string query, double minSimilarity, int topK, float[]? queryEmbedding = null, List<string>? currentEntities = null, string? currentSceneId = null)
    {
        var result = new List<MemoryNode>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        var candidates = await GetAllMemoryNodesAsync(scope, characterId, limit: 150, semanticRecallOnly: true);
        var candidatePool = candidates
            .Select(node =>
            {
                var keywordScore = ComputeKeywordScore(query, node.Keywords, node.Summary);
                var embeddingScore = queryEmbedding != null && node.Embedding != null
                    ? VectorSearch.CosineSimilarity(queryEmbedding, node.Embedding)
                    : ComputeSimilarity(query, node.Summary);
                var recency = ComputeRecencyScore(node.LastUsed, node.CreatedAt);
                var entityOverlap = ComputeEntityOverlap(node.Keywords, currentEntities);
                var sceneRelevance = ComputeSceneRelevance(node.Keywords, currentSceneId);

                // Keyword and semantic similarity dominate; recency is only a small helper.
                var effective = CalculateMemoryRecallScoreForTest(
                    keywordScore,
                    embeddingScore,
                    entityOverlap,
                    sceneRelevance,
                    node.Importance,
                    recency);

                return (Node: node, Relevance: effective, KeywordScore: keywordScore);
            })
            .Where(x => x.Relevance >= minSimilarity || x.KeywordScore >= 0.35)
            .OrderByDescending(x => x.Relevance)
            .ThenByDescending(x => x.KeywordScore)
            .ThenByDescending(x => x.Node.Importance)
            .Take(Math.Max(6, Math.Max(1, topK) * 4))
            .ToList();

        if (candidatePool.Count == 0)
            return result;

        const double lambda = 0.7;
        var selected = new List<(MemoryNode Node, double Relevance, double KeywordScore)>();
        while (selected.Count < Math.Max(1, topK) && candidatePool.Count > 0)
        {
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;

            for (int i = 0; i < candidatePool.Count; i++)
            {
                var candidate = candidatePool[i];
                var redundancy = selected.Count == 0
                    ? 0
                    : selected.Max(s => ComputeNodeSimilarity(candidate.Node, s.Node));
                var mmrScore = (lambda * candidate.Relevance) - ((1 - lambda) * redundancy);
                if (mmrScore > bestScore)
                {
                    bestScore = mmrScore;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                break;

            selected.Add(candidatePool[bestIndex]);
            candidatePool.RemoveAt(bestIndex);
        }

        foreach (var item in selected)
        {
            await UpdateMemoryNodeLastUsedAsync(scope, item.Node.Id);
            result.Add(item.Node);
        }

        return result;
    }

    private static double ComputeNodeSimilarity(MemoryNode left, MemoryNode right)
    {
        if (left.Embedding != null && right.Embedding != null)
            return VectorSearch.CosineSimilarity(left.Embedding, right.Embedding);

        return ComputeSimilarity($"{left.Keywords} {left.Summary}", $"{right.Keywords} {right.Summary}");
    }

    private static double ComputeKeywordScore(string query, string keywords, string summary)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
            return 0;

        var keywordTokens = Tokenize(keywords);
        var haystackTokens = keywordTokens.Count > 0 ? keywordTokens : Tokenize(summary);
        if (haystackTokens.Count == 0)
            return 0;

        var matches = haystackTokens.Count(term =>
            queryTokens.Any(queryToken =>
                string.Equals(queryToken, term, StringComparison.OrdinalIgnoreCase)
                || queryToken.Contains(term, StringComparison.OrdinalIgnoreCase)
                || term.Contains(queryToken, StringComparison.OrdinalIgnoreCase)));
        var denominator = Math.Min(queryTokens.Count, haystackTokens.Count);
        return denominator == 0 ? 0 : Math.Clamp((double)matches / denominator, 0, 1);
    }

    internal static double CalculateMemoryRecallScoreForTest(
        double keywordScore,
        double embeddingScore,
        double entityScore,
        double sceneScore,
        double importanceScore,
        double recencyScore)
    {
        return (0.35 * keywordScore)
            + (0.25 * embeddingScore)
            + (0.15 * entityScore)
            + (0.10 * sceneScore)
            + (0.10 * importanceScore)
            + (0.05 * recencyScore);
    }

    public async Task<int> GetActiveTokenCountAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(TokenCount), 0) FROM ChatHistory
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND IsArchived = 0
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    public async Task ArchiveAsync(TrpgScope scope, List<int> ids)
    {
        if (ids.Count == 0) return;
        var idParams = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"UPDATE ChatHistory SET IsArchived = 1 WHERE WorldId = @worldId AND Id IN ({idParams})";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetCurrentFoldCountAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(FoldCount) FROM (
                SELECT FoldCount FROM LongTermMemory WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND MemoryAudience = 'CharacterIC'
                UNION ALL
                SELECT FoldCount FROM CharacterMemory WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            )
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        var value = await cmd.ExecuteScalarAsync();
        return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// 已废弃：请使用 EntityCanonicalizer.GetEntityAsync 替代
    /// </summary>
    [Obsolete("请使用 EntityCanonicalizer.GetEntityAsync 替代")]
    public async Task<NpcCanonicalState?> GetNpcCanonicalStateAsync(TrpgScope scope, string npcId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT WorldId, GroupId, NpcId, DisplayName, CoreSummary, IdentityState, KeyEventsDigest,
                   RelationshipState, PendingRelationshipDeltaJson, LastSummaryUpdatedAt, UpdatedAt
            FROM NpcCanonicalState
            WHERE WorldId = @worldId AND GroupId = @groupId AND NpcId = @npcId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@npcId", npcId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new NpcCanonicalState
        {
            WorldId = reader.GetString(0),
            GroupId = reader.GetInt64(1),
            NpcId = reader.GetString(2),
            DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
            CoreSummary = reader.IsDBNull(4) ? "" : reader.GetString(4),
            IdentityState = reader.IsDBNull(5) ? "" : reader.GetString(5),
            KeyEventsDigest = reader.IsDBNull(6) ? "" : reader.GetString(6),
            RelationshipState = reader.IsDBNull(7) ? "" : reader.GetString(7),
            PendingRelationshipDeltaJson = reader.IsDBNull(8) ? "{}" : reader.GetString(8),
            LastSummaryUpdatedAt = reader.IsDBNull(9) || string.IsNullOrWhiteSpace(reader.GetString(9)) ? DateTime.MinValue : DateTime.Parse(reader.GetString(9)),
            UpdatedAt = reader.IsDBNull(10) || string.IsNullOrWhiteSpace(reader.GetString(10)) ? DateTime.MinValue : DateTime.Parse(reader.GetString(10))
        };
    }

    /// <summary>
    /// 已废弃：请使用 StateMutationPipeline 处理状态变更
    /// </summary>
    [Obsolete("请使用 StateMutationPipeline 处理状态变更")]
    public async Task UpsertNpcCanonicalStateAsync(TrpgScope scope, NpcCanonicalState state)
    {
        state.WorldId = scope.WorldId;
        state.GroupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO NpcCanonicalState (WorldId, GroupId, NpcId, DisplayName, CoreSummary, IdentityState, KeyEventsDigest,
                RelationshipState, PendingRelationshipDeltaJson, LastSummaryUpdatedAt, UpdatedAt)
            VALUES (@worldId, @groupId, @npcId, @displayName, @coreSummary, @identityState, @keyEventsDigest,
                @relationshipState, @pendingDelta, @lastSummaryUpdatedAt, @updatedAt)
            ON CONFLICT(WorldId, GroupId, NpcId) DO UPDATE SET
                DisplayName = @displayName,
                CoreSummary = @coreSummary,
                IdentityState = @identityState,
                KeyEventsDigest = @keyEventsDigest,
                RelationshipState = @relationshipState,
                PendingRelationshipDeltaJson = @pendingDelta,
                LastSummaryUpdatedAt = @lastSummaryUpdatedAt,
                UpdatedAt = @updatedAt
            """;
        cmd.Parameters.AddWithValue("@worldId", state.WorldId);
        cmd.Parameters.AddWithValue("@groupId", state.GroupId);
        cmd.Parameters.AddWithValue("@npcId", state.NpcId);
        cmd.Parameters.AddWithValue("@displayName", state.DisplayName ?? "");
        cmd.Parameters.AddWithValue("@coreSummary", state.CoreSummary ?? "");
        cmd.Parameters.AddWithValue("@identityState", state.IdentityState ?? "");
        cmd.Parameters.AddWithValue("@keyEventsDigest", state.KeyEventsDigest ?? "");
        cmd.Parameters.AddWithValue("@relationshipState", state.RelationshipState ?? "");
        cmd.Parameters.AddWithValue("@pendingDelta", state.PendingRelationshipDeltaJson ?? "{}");
        cmd.Parameters.AddWithValue("@lastSummaryUpdatedAt", state.LastSummaryUpdatedAt == DateTime.MinValue ? "" : state.LastSummaryUpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 已废弃：关系推断应通过四层架构标签驱动，由 StateMutationPipeline 处理
    /// </summary>
    [Obsolete("关系推断应通过四层架构标签驱动，由 StateMutationPipeline 处理")]
    public async Task<bool> AccumulateNpcRelationshipDeltaAsync(TrpgScope scope, string npcId, string metric, int delta, int threshold, TimeSpan summaryCooldown)
    {
        var canonical = await GetNpcCanonicalStateAsync(scope, npcId) ?? new NpcCanonicalState
        {
            WorldId = scope.WorldId,
            GroupId = scope.GroupId,
            NpcId = npcId,
            DisplayName = npcId,
            UpdatedAt = DateTime.UtcNow
        };

        Dictionary<string, int> pending;
        try
        {
            pending = JsonSerializer.Deserialize<Dictionary<string, int>>(canonical.PendingRelationshipDeltaJson) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        pending.TryGetValue(metric, out var oldVal);
        pending[metric] = oldVal + delta;
        canonical.PendingRelationshipDeltaJson = JsonSerializer.Serialize(pending);
        canonical.UpdatedAt = DateTime.UtcNow;
        await UpsertNpcCanonicalStateAsync(scope, canonical);

        var passedThreshold = Math.Abs(pending[metric]) >= Math.Max(1, threshold);
        var cooldownOk = canonical.LastSummaryUpdatedAt == DateTime.MinValue || (DateTime.UtcNow - canonical.LastSummaryUpdatedAt) >= summaryCooldown;
        return passedThreshold && cooldownOk;
    }

    public async Task<List<MemoryNode>> SearchNpcRelatedMemoryNodesAsync(TrpgScope scope, string characterId, IEnumerable<string> npcAliases, int limit = 6)
    {
        var groupId = scope.GroupId;
        var aliases = npcAliases
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        if (aliases.Count == 0) return new List<MemoryNode>();

        var likeConditions = string.Join(" OR ", aliases.Select((_, i) => $"Keywords LIKE @kw{i} OR Summary LIKE @kw{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, WorldId, GroupId, CharacterId, Keywords, Summary, NodeType, Importance, Tier, Heat, Embedding, Superseded, SupersededBy, LastUsed, CreatedAt
            FROM LongTermMemory
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
              AND MemoryAudience = 'CharacterIC'
              AND COALESCE(Superseded, 0) = 0
              AND ({likeConditions})
            ORDER BY Importance DESC, Heat DESC, COALESCE(LastUsed, CreatedAt) DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@limit", limit);
        for (int i = 0; i < aliases.Count; i++)
            cmd.Parameters.AddWithValue($"@kw{i}", "%" + aliases[i] + "%");

        var entries = new List<MemoryNode>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            float[]? embedding = null;
            if (!reader.IsDBNull(10))
            {
                var blob = (byte[])reader.GetValue(10);
                embedding = new float[blob.Length / 4];
                Buffer.BlockCopy(blob, 0, embedding, 0, blob.Length);
            }

            entries.Add(new MemoryNode
            {
                Id = reader.GetInt32(0),
                WorldId = reader.GetString(1),
                GroupId = reader.GetInt64(2),
                CharacterId = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Keywords = reader.GetString(4),
                Summary = reader.GetString(5),
                NodeType = reader.GetString(6),
                Importance = reader.GetDouble(7),
                Tier = reader.IsDBNull(8) ? "Session" : reader.GetString(8),
                Heat = reader.IsDBNull(9) ? 0.5 : reader.GetDouble(9),
                Embedding = embedding,
                Superseded = !reader.IsDBNull(11) && reader.GetInt32(11) == 1,
                SupersededBy = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                LastUsed = reader.IsDBNull(13) ? (DateTime?)null : reader.GetDateTime(13),
                CreatedAt = reader.IsDBNull(14) ? DateTime.MinValue : reader.GetDateTime(14)
            });
        }

        return entries;
    }

    public async Task UpsertSceneDictionaryAsync(TrpgScope scope, string sceneId, string sceneBaseDesc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SceneDictionary (WorldId, SceneId, SceneBaseDesc, UpdatedAt)
            VALUES (@worldId, @sceneId, @sceneBaseDesc, @updatedAt)
            ON CONFLICT(WorldId, SceneId) DO UPDATE SET
                SceneBaseDesc = @sceneBaseDesc,
                UpdatedAt = @updatedAt
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@sceneId", sceneId);
        cmd.Parameters.AddWithValue("@sceneBaseDesc", sceneBaseDesc ?? "");
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> GetSceneBaseDescAsync(TrpgScope scope, string sceneId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT SceneBaseDesc FROM SceneDictionary WHERE WorldId = @worldId AND SceneId = @sceneId";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@sceneId", sceneId);
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    public async Task UpsertCharacterHotMetaAsync(TrpgScope scope, string charId, string shortTags, string aliases)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CharacterHotMeta (WorldId, CharId, ShortTags, Aliases, UpdatedAt)
            VALUES (@worldId, @charId, @shortTags, @aliases, @updatedAt)
            ON CONFLICT(WorldId, CharId) DO UPDATE SET
                ShortTags = @shortTags,
                Aliases = @aliases,
                UpdatedAt = @updatedAt
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@charId", charId);
        cmd.Parameters.AddWithValue("@shortTags", shortTags ?? "");
        cmd.Parameters.AddWithValue("@aliases", aliases ?? "");
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<CharacterHotMetaEntry>> GetCharacterHotMetaByIdsAsync(TrpgScope scope, List<string> charIds)
    {
        var result = new List<CharacterHotMetaEntry>();
        if (charIds.Count == 0) return result;

        var idParams = string.Join(",", charIds.Select((_, i) => $"@id{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT WorldId, CharId, ShortTags, Aliases, UpdatedAt FROM CharacterHotMeta WHERE WorldId = @worldId AND CharId IN ({idParams})";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        for (int i = 0; i < charIds.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", charIds[i]);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new CharacterHotMetaEntry
            {
                WorldId = reader.GetString(0),
                CharId = reader.GetString(1),
                ShortTags = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Aliases = reader.IsDBNull(3) ? "" : reader.GetString(3),
                UpdatedAt = reader.IsDBNull(4) ? DateTime.MinValue : DateTime.Parse(reader.GetString(4))
            });
        }

        return result;
    }

    public async Task<string?> ResolveCharacterIdByAliasAsync(TrpgScope scope, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;

        using var aiCmd = _connection.CreateCommand();
        aiCmd.CommandText = "SELECT CharacterId FROM AiCharacterEntry WHERE WorldId = @worldId AND GroupId = @groupId AND DisplayName = @alias LIMIT 1";
        aiCmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        aiCmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        aiCmd.Parameters.AddWithValue("@alias", alias);
        var aiResult = await aiCmd.ExecuteScalarAsync();
        if (aiResult != null) return aiResult.ToString();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT CharId FROM CharacterHotMeta WHERE WorldId = @worldId AND Aliases LIKE @alias LIMIT 1";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@alias", "%" + alias + "%");
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    private static double ComputeSimilarity(string left, string right)
    {
        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0;

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        var denominator = Math.Max(leftTokens.Count, rightTokens.Count);
        return denominator == 0 ? 0 : (double)intersection / denominator;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = text.Split(new[] { ' ', '\n', '\r', '\t', '，', ',', '。', '！', '？', '、', ':', '：', '-', '_', '[', ']', '(', ')', '（', '）', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var trimmed = token.Trim();
            if (trimmed.Length >= 2)
                set.Add(trimmed);
        }
        return set;
    }

    // ── CharacterSheet CRUD ──

    public async Task<CharacterSheetEntry?> GetCharacterAsync(TrpgScope scope, string characterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT WorldId, Id, Name, StaticBackground, DynamicStateJson, UpdatedAt
            FROM CharacterSheet WHERE WorldId = @worldId AND Id = @id
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new CharacterSheetEntry
            {
                WorldId = reader.GetString(0),
                Id = reader.GetString(1),
                Name = reader.GetString(2),
                StaticBackground = reader.IsDBNull(3) ? "" : reader.GetString(3),
                DynamicStateJson = reader.IsDBNull(4) ? "{}" : reader.GetString(4),
                UpdatedAt = reader.IsDBNull(5) ? DateTime.MinValue : DateTime.Parse(reader.GetString(5))
            };
        }
        return null;
    }

    public async Task UpsertCharacterAsync(TrpgScope scope, CharacterSheetEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CharacterSheet (WorldId, Id, Name, StaticBackground, DynamicStateJson, UpdatedAt)
            VALUES (@worldId, @id, @name, @staticBg, @dynamicState, @updatedAt)
            ON CONFLICT(WorldId, Id) DO UPDATE SET
                Name = @name,
                StaticBackground = @staticBg,
                DynamicStateJson = @dynamicState,
                UpdatedAt = @updatedAt
            """;
        entry.WorldId = scope.WorldId;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@name", entry.Name);
        cmd.Parameters.AddWithValue("@staticBg", entry.StaticBackground ?? "");
        cmd.Parameters.AddWithValue("@dynamicState", entry.DynamicStateJson ?? "{}");
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    // ── AiCharacterEntry CRUD ──

    public async Task<AiCharacterEntry?> GetAiCharacterAsync(TrpgScope scope, string characterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT WorldId, CharacterId, VirtualId, OwnerUserId, GroupId, TeamName, DisplayName,
                   StaticBackground, DynamicStateJson, SkillsJson,
                   InventoryJson, RuleText, IsActive, CreatedAt, UpdatedAt
            FROM AiCharacterEntry WHERE WorldId = @worldId AND CharacterId = @id
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadAiCharacterFromReader(reader);
        }
        return null;
    }

    public async Task<List<AiCharacterEntry>> GetAiCharactersForTeamAsync(TrpgScope scope)
    {
        var entries = new List<AiCharacterEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT WorldId, CharacterId, VirtualId, OwnerUserId, GroupId, TeamName, DisplayName,
                   StaticBackground, DynamicStateJson, SkillsJson,
                   InventoryJson, RuleText, IsActive, CreatedAt, UpdatedAt
            FROM AiCharacterEntry WHERE WorldId = @worldId AND GroupId = @groupId AND TeamName = @teamName
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@teamName", scope.TeamName);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadAiCharacterFromReader(reader));
        }
        return entries;
    }

    public async Task<List<AiCharacterEntry>> GetActiveAiCharactersAsync(TrpgScope scope)
    {
        var entries = new List<AiCharacterEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT WorldId, CharacterId, VirtualId, OwnerUserId, GroupId, TeamName, DisplayName,
                   StaticBackground, DynamicStateJson, SkillsJson,
                   InventoryJson, RuleText, IsActive, CreatedAt, UpdatedAt
            FROM AiCharacterEntry WHERE WorldId = @worldId AND GroupId = @groupId AND TeamName = @teamName AND IsActive = 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@teamName", scope.TeamName);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadAiCharacterFromReader(reader));
        }
        return entries;
    }

    public async Task UpsertAiCharacterAsync(TrpgScope scope, AiCharacterEntry entry)
    {
        entry.WorldId = scope.WorldId;
        entry.OwnerUserId = scope.OwnerUserId;
        entry.GroupId = scope.GroupId;
        entry.TeamName = scope.TeamName;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AiCharacterEntry (WorldId, CharacterId, VirtualId, OwnerUserId, GroupId, TeamName, DisplayName,
                StaticBackground, DynamicStateJson, SkillsJson,
                InventoryJson, RuleText, IsActive, CreatedAt, UpdatedAt)
            VALUES (@worldId, @id, @virtualId, @ownerUserId, @groupId, @teamName, @displayName,
                @staticBg, @dynamicState, @skills,
                @inventory, @ruleText, @isActive, @createdAt, @updatedAt)
            ON CONFLICT(WorldId, CharacterId) DO UPDATE SET
                VirtualId = @virtualId, OwnerUserId = @ownerUserId, GroupId = @groupId, TeamName = @teamName,
                DisplayName = @displayName, StaticBackground = @staticBg,
                DynamicStateJson = @dynamicState, SkillsJson = @skills,
                InventoryJson = @inventory, RuleText = @ruleText, IsActive = @isActive, UpdatedAt = @updatedAt
            """;
        cmd.Parameters.AddWithValue("@worldId", entry.WorldId);
        cmd.Parameters.AddWithValue("@id", entry.CharacterId);
        cmd.Parameters.AddWithValue("@virtualId", entry.VirtualId);
        cmd.Parameters.AddWithValue("@ownerUserId", entry.OwnerUserId);
        cmd.Parameters.AddWithValue("@groupId", entry.GroupId);
        cmd.Parameters.AddWithValue("@teamName", entry.TeamName);
        cmd.Parameters.AddWithValue("@displayName", entry.DisplayName);
        cmd.Parameters.AddWithValue("@staticBg", entry.StaticBackground ?? "");
        cmd.Parameters.AddWithValue("@dynamicState", entry.DynamicStateJson ?? "{}");
        cmd.Parameters.AddWithValue("@skills", entry.SkillsJson ?? "{}");
        cmd.Parameters.AddWithValue("@inventory", entry.InventoryJson ?? "[]");
        cmd.Parameters.AddWithValue("@ruleText", entry.RuleText ?? "");
        cmd.Parameters.AddWithValue("@isActive", entry.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt", entry.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> DeleteAiCharacterAsync(TrpgScope scope, string characterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM AiCharacterEntry WHERE WorldId = @worldId AND CharacterId = @id";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", characterId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task SetAiCharacterActiveAsync(TrpgScope scope, string characterId, bool isActive)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE AiCharacterEntry SET IsActive = @isActive, UpdatedAt = @updatedAt WHERE WorldId = @worldId AND CharacterId = @id";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", characterId);
        cmd.Parameters.AddWithValue("@isActive", isActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetNextVirtualIdAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MIN(VirtualId), -1000) - 1 FROM AiCharacterEntry WHERE WorldId = @worldId AND VirtualId < 0";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : -1001;
    }

    // ── AiCharacterRuntimeControl CRUD ──

    public async Task<AiRuntimeMode> GetAiRuntimeModeAsync(TrpgScope scope, string characterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Mode
            FROM AiCharacterRuntimeControl
            WHERE WorldId = @worldId AND CharacterId = @characterId
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        var result = await cmd.ExecuteScalarAsync();
        var raw = result?.ToString();
        if (string.IsNullOrEmpty(raw))
            return AiRuntimeMode.Act;
        AiRuntimeModeParser.TryParse(raw, out var mode);
        return mode;
    }

    public async Task SetAiRuntimeModeAsync(TrpgScope scope, string characterId, AiRuntimeMode mode, long updatedByUserId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AiCharacterRuntimeControl
                (WorldId, CharacterId, Mode, UpdatedAt, UpdatedByUserId)
            VALUES
                (@worldId, @characterId, @mode, @updatedAt, @updatedByUserId)
            ON CONFLICT(WorldId, CharacterId) DO UPDATE SET
                Mode = excluded.Mode,
                UpdatedAt = excluded.UpdatedAt,
                UpdatedByUserId = excluded.UpdatedByUserId;
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@mode", AiRuntimeModeParser.ToStorageValue(mode));
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@updatedByUserId", updatedByUserId);
        await cmd.ExecuteNonQueryAsync();
    }

    private AiCharacterEntry ReadAiCharacterFromReader(System.Data.Common.DbDataReader reader)
    {
        return new AiCharacterEntry
        {
            WorldId = reader.GetString(0),
            CharacterId = reader.GetString(1),
            VirtualId = reader.GetInt64(2),
            OwnerUserId = reader.GetInt64(3),
            GroupId = reader.GetInt64(4),
            TeamName = reader.GetString(5),
            DisplayName = reader.GetString(6),
            StaticBackground = reader.IsDBNull(7) ? "" : reader.GetString(7),
            DynamicStateJson = reader.IsDBNull(8) ? "{}" : reader.GetString(8),
            SkillsJson = reader.IsDBNull(9) ? "{}" : reader.GetString(9),
            InventoryJson = reader.IsDBNull(10) ? "[]" : reader.GetString(10),
            RuleText = reader.IsDBNull(11) ? "" : reader.GetString(11),
            IsActive = reader.GetInt32(12) == 1,
            CreatedAt = reader.IsDBNull(13) ? DateTime.MinValue : DateTime.Parse(reader.GetString(13)),
            UpdatedAt = reader.IsDBNull(14) ? DateTime.MinValue : DateTime.Parse(reader.GetString(14))
        };
    }

    // ── LongTermMemory (FTS5) ──

    public Task<long> InsertMemoryNodeAsync(TrpgScope scope, string characterId, string keywords, string summary, string nodeType, double importance, float[]? embedding = null, double confidence = 1.0, List<string>? rawExcerpts = null)
    {
        return InsertCharacterMemoryNodeAsync(scope, characterId, keywords, summary, nodeType, importance, embedding, confidence, rawExcerpts);
    }

    public Task<long> InsertCharacterMemoryNodeAsync(TrpgScope scope, string characterId, string keywords, string summary, string nodeType, double importance, float[]? embedding = null, double confidence = 1.0, List<string>? rawExcerpts = null, List<string>? sourceMessageIds = null, string sourceScope = MemorySourceScope.IC, string metadata = "{}")
    {
        return InsertMemoryNodeWithAudienceAsync(
            scope, characterId, characterId, MemoryAudience.CharacterIC, keywords, summary, nodeType, importance,
            embedding, confidence, rawExcerpts, sourceMessageIds, sourceScope, icUsable: true, metadata);
    }

    public Task<long> InsertPlayerTableMemoryNodeAsync(TrpgScope scope, string keywords, string summary, string nodeType, double importance, float[]? embedding = null, double confidence = 1.0, List<string>? rawExcerpts = null, List<string>? sourceMessageIds = null, string sourceScope = MemorySourceScope.PL, string metadata = "{}")
    {
        return InsertMemoryNodeWithAudienceAsync(
            scope, "", null, MemoryAudience.PlayerTable, keywords, summary, nodeType, importance,
            embedding, confidence, rawExcerpts, sourceMessageIds, sourceScope, icUsable: false, metadata);
    }

    private async Task<long> InsertMemoryNodeWithAudienceAsync(
        TrpgScope scope,
        string characterId,
        string? ownerCharacterId,
        string audience,
        string keywords,
        string summary,
        string nodeType,
        double importance,
        float[]? embedding = null,
        double confidence = 1.0,
        List<string>? rawExcerpts = null,
        List<string>? sourceMessageIds = null,
        string sourceScope = MemorySourceScope.Unknown,
        bool icUsable = true,
        string metadata = "{}")
    {
        var groupId = scope.GroupId;
        var tier = ResolveTier(nodeType);
        var heat = ComputeInitialHeat(importance, tier);
        var rawExcerptJson = rawExcerpts != null && rawExcerpts.Count > 0
            ? JsonSerializer.Serialize(rawExcerpts)
            : "[]";
        var sourceMessageIdsJson = sourceMessageIds != null && sourceMessageIds.Count > 0
            ? JsonSerializer.Serialize(sourceMessageIds)
            : "[]";
        if (string.IsNullOrWhiteSpace(metadata))
            metadata = "{}";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LongTermMemory
                (WorldId, GroupId, CharacterId, Keywords, Summary, NodeType, Importance, Tier, Heat, Embedding,
                 Superseded, SupersededBy, LastUsed, CreatedAt, Confidence, RawExcerpt, FoldCount,
                 MemoryAudience, OwnerCharacterId, SourceScope, SourceMessageIds, IcUsable, Metadata)
            VALUES
                (@worldId, @groupId, @characterId, @keywords, @summary, @nodeType, @importance, @tier, @heat, @embedding,
                 0, NULL, datetime('now'), datetime('now'), @confidence, @rawExcerpt, 0,
                 @memoryAudience, @ownerCharacterId, @sourceScope, @sourceMessageIds, @icUsable, @metadata);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@keywords", keywords);
        cmd.Parameters.AddWithValue("@summary", summary);
        cmd.Parameters.AddWithValue("@nodeType", nodeType);
        cmd.Parameters.AddWithValue("@importance", importance);
        cmd.Parameters.AddWithValue("@tier", tier);
        cmd.Parameters.AddWithValue("@heat", heat);
        if (embedding != null && embedding.Length > 0)
        {
            var bytes = new byte[embedding.Length * 4];
            Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
            cmd.Parameters.AddWithValue("@embedding", bytes);
        }
        else
        {
            cmd.Parameters.AddWithValue("@embedding", DBNull.Value);
        }
        cmd.Parameters.AddWithValue("@confidence", confidence);
        cmd.Parameters.AddWithValue("@rawExcerpt", rawExcerptJson);
        cmd.Parameters.AddWithValue("@memoryAudience", audience);
        cmd.Parameters.AddWithValue("@ownerCharacterId", ownerCharacterId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceScope", sourceScope);
        cmd.Parameters.AddWithValue("@sourceMessageIds", sourceMessageIdsJson);
        cmd.Parameters.AddWithValue("@icUsable", icUsable ? 1 : 0);
        cmd.Parameters.AddWithValue("@metadata", metadata);

        var memoryId = (long)(await cmd.ExecuteScalarAsync() ?? 0);

        // 如果有原始档案，插入到 RawArchive 表
        if (rawExcerpts != null && rawExcerpts.Count > 0)
        {
            foreach (var excerpt in rawExcerpts)
            {
                using var rawCmd = _connection.CreateCommand();
                rawCmd.CommandText = """
                    INSERT INTO RawArchive (WorldId, MemoryId, Content)
                    VALUES (@worldId, @memoryId, @content)
                    """;
                rawCmd.Parameters.AddWithValue("@worldId", scope.WorldId);
                rawCmd.Parameters.AddWithValue("@memoryId", memoryId);
                rawCmd.Parameters.AddWithValue("@content", excerpt);
                await rawCmd.ExecuteNonQueryAsync();
            }
        }

        return memoryId;
    }

    public Task<List<MemoryNode>> SearchMemoryNodesAsync(TrpgScope scope, string characterId, string query, int limit = 5)
        => SearchCharacterMemoryNodesAsync(scope, characterId, query, limit);

    public async Task<List<MemoryNode>> SearchCharacterMemoryNodesAsync(TrpgScope scope, string characterId, string query, int limit = 5)
    {
        return await SearchMemoryNodesByAudienceAsync(scope, characterId, MemoryAudience.CharacterIC, query, limit);
    }

    public async Task<List<MemoryNode>> SearchPlayerTableMemoryNodesAsync(TrpgScope scope, string query, int limit = 5)
    {
        return await SearchMemoryNodesByAudienceAsync(scope, null, MemoryAudience.PlayerTable, query, limit);
    }

    private async Task<List<MemoryNode>> SearchMemoryNodesByAudienceAsync(TrpgScope scope, string? characterId, string audience, string query, int limit)
    {
        var entries = new List<MemoryNode>();
        if (string.IsNullOrWhiteSpace(query))
            return entries;
        var tokens = query
            .Split(new[] { ' ', '\t', '\r', '\n', '，', ',', '。', '！', '？', '、', ':', '：', ';', '；', '|', '/', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (tokens.Count == 0)
            tokens.Add(query.Trim());

        var likeClauses = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
            likeClauses.Add($"(Keywords LIKE @kw{i} OR Summary LIKE @kw{i})");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, WorldId, GroupId, CharacterId, Keywords, Summary, NodeType, Importance, Tier, Heat, Embedding,
                   Superseded, SupersededBy, LastUsed, CreatedAt, Confidence, RawExcerpt, FoldCount,
                   MemoryAudience, OwnerCharacterId, SourceScope, SourceMessageIds, IcUsable, Metadata
            FROM LongTermMemory
            WHERE WorldId = @worldId AND GroupId = @groupId
              AND MemoryAudience = @audience
              AND (@audience != 'CharacterIC' OR CharacterId = @characterId)
              AND COALESCE(Superseded, 0) = 0
              AND ({string.Join(" OR ", likeClauses)})
            ORDER BY ((Importance * 0.3) + (Heat * 0.3) + (1.0 / (1.0 + ((julianday('now') - julianday(COALESCE(LastUsed, CreatedAt))) * 24.0) / 72.0)) * 0.4) DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@audience", audience);
        for (int i = 0; i < tokens.Count; i++)
            cmd.Parameters.AddWithValue($"@kw{i}", $"%{tokens[i]}%");
        cmd.Parameters.AddWithValue("@limit", limit);
        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                entries.Add(ReadMemoryNode(reader));
        }
        catch (SQLiteException ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Memory node search error: {ex.Message}");
        }
        return entries;
    }

    public Task<List<MemoryNode>> GetAllMemoryNodesAsync(TrpgScope scope, string characterId, int limit = 10, bool semanticRecallOnly = false)
        => GetMemoryNodesByAudienceAsync(scope, characterId, MemoryAudience.CharacterIC, limit, semanticRecallOnly);

    public Task<List<MemoryNode>> GetPlayerTableMemoryNodesAsync(TrpgScope scope, int limit = 10, bool semanticRecallOnly = false)
        => GetMemoryNodesByAudienceAsync(scope, null, MemoryAudience.PlayerTable, limit, semanticRecallOnly);

    private async Task<List<MemoryNode>> GetMemoryNodesByAudienceAsync(TrpgScope scope, string? characterId, string audience, int limit, bool semanticRecallOnly)
    {
        var entries = new List<MemoryNode>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, Keywords, Summary, NodeType, Importance, Tier, Heat, Embedding,
                   Superseded, SupersededBy, LastUsed, CreatedAt, Confidence, RawExcerpt, FoldCount,
                   MemoryAudience, OwnerCharacterId, SourceScope, SourceMessageIds, IcUsable, Metadata
            FROM LongTermMemory
            WHERE WorldId = @worldId AND GroupId = @groupId
              AND MemoryAudience = @audience
              AND (@audience != 'CharacterIC' OR CharacterId = @characterId)
              AND COALESCE(Superseded, 0) = 0
              AND (@semanticRecallOnly = 0 OR LOWER(TRIM(COALESCE(NodeType, ''))) NOT IN ('timeline', 'timeline_rollup', 'scene_transition', 'flow'))
            ORDER BY ((Importance * 0.3) + (Heat * 0.3) + (1.0 / (1.0 + ((julianday('now') - julianday(COALESCE(LastUsed, CreatedAt))) * 24.0) / 72.0)) * 0.4) DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@audience", audience);
        cmd.Parameters.AddWithValue("@semanticRecallOnly", semanticRecallOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(ReadMemoryNode(reader));
        return entries;
    }

    private static MemoryNode ReadMemoryNode(System.Data.Common.DbDataReader reader)
    {
        float[]? embedding = null;
        if (!reader.IsDBNull(10))
        {
            var blob = (byte[])reader.GetValue(10);
            embedding = new float[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, embedding, 0, blob.Length);
        }

        return new MemoryNode
        {
            Id = reader.GetInt32(0),
            WorldId = reader.GetString(1),
            GroupId = reader.GetInt64(2),
            CharacterId = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Keywords = reader.GetString(4),
            Summary = reader.GetString(5),
            NodeType = reader.GetString(6),
            Importance = reader.GetDouble(7),
            Tier = reader.IsDBNull(8) ? "Session" : reader.GetString(8),
            Heat = reader.IsDBNull(9) ? 0.5 : reader.GetDouble(9),
            Embedding = embedding,
            Superseded = !reader.IsDBNull(11) && reader.GetInt32(11) == 1,
            SupersededBy = reader.IsDBNull(12) ? null : reader.GetInt32(12),
            LastUsed = reader.IsDBNull(13) ? (DateTime?)null : reader.GetDateTime(13),
            CreatedAt = reader.IsDBNull(14) ? DateTime.MinValue : reader.GetDateTime(14),
            Confidence = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetDouble(15) : 1.0,
            RawExcerpt = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : "[]",
            FoldCount = reader.FieldCount > 17 && !reader.IsDBNull(17) ? reader.GetInt32(17) : 0,
            MemoryAudience = reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetString(18) : MemoryAudience.CharacterIC,
            OwnerCharacterId = reader.FieldCount > 19 && !reader.IsDBNull(19) ? reader.GetString(19) : null,
            SourceScope = reader.FieldCount > 20 && !reader.IsDBNull(20) ? reader.GetString(20) : null,
            SourceMessageIds = reader.FieldCount > 21 && !reader.IsDBNull(21) ? reader.GetString(21) : "[]",
            IcUsable = reader.FieldCount <= 22 || reader.IsDBNull(22) || reader.GetInt32(22) == 1,
            Metadata = reader.FieldCount > 23 && !reader.IsDBNull(23) ? reader.GetString(23) : "{}"
        };
    }

    public async Task UpdateMemoryNodeLastUsedAsync(TrpgScope scope, int nodeId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE LongTermMemory SET LastUsed = datetime('now'), Heat = MIN(1.0, COALESCE(Heat, 0.5) + 0.05) WHERE WorldId = @worldId AND Id = @id";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", nodeId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertLlmUsageLogAsync(LlmUsageLogEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LlmUsageLog
                (CreatedAt, Provider, Model, AgentName, RequestKind, WorldId, GroupId, CharacterId, TurnId, SourceMessageId,
                 InputTokens, OutputTokens, CachedInputTokens, CacheHitTokens, CacheMissTokens,
                 EstimatedCost, Success, ErrorType, Metadata)
            VALUES
                (@createdAt, @provider, @model, @agentName, @requestKind, @worldId, @groupId, @characterId, @turnId, @sourceMessageId,
                 @inputTokens, @outputTokens, @cachedInputTokens, @cacheHitTokens, @cacheMissTokens,
                 @estimatedCost, @success, @errorType, @metadata)
            """;
        cmd.Parameters.AddWithValue("@createdAt", entry.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@provider", entry.Provider);
        cmd.Parameters.AddWithValue("@model", entry.Model);
        cmd.Parameters.AddWithValue("@agentName", entry.AgentName);
        cmd.Parameters.AddWithValue("@requestKind", entry.RequestKind);
        cmd.Parameters.AddWithValue("@worldId", entry.WorldId);
        cmd.Parameters.AddWithValue("@groupId", entry.GroupId);
        cmd.Parameters.AddWithValue("@characterId", entry.CharacterId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@turnId", entry.TurnId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceMessageId", entry.SourceMessageId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@inputTokens", entry.InputTokens);
        cmd.Parameters.AddWithValue("@outputTokens", entry.OutputTokens);
        cmd.Parameters.AddWithValue("@cachedInputTokens", entry.CachedInputTokens ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@cacheHitTokens", entry.CacheHitTokens ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@cacheMissTokens", entry.CacheMissTokens ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@estimatedCost", (double)entry.EstimatedCost);
        cmd.Parameters.AddWithValue("@success", entry.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("@errorType", entry.ErrorType ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", string.IsNullOrWhiteSpace(entry.Metadata) ? "{}" : entry.Metadata);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<LlmCostReport> GetLlmCostReportAsync(DateTime fromUtc, DateTime toUtc, string? providerFilter = null)
    {
        var rows = new List<LlmUsageLogEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CreatedAt, Provider, Model, AgentName, RequestKind, WorldId, GroupId, CharacterId, TurnId, SourceMessageId,
                   InputTokens, OutputTokens, CachedInputTokens, CacheHitTokens, CacheMissTokens,
                   EstimatedCost, Success, ErrorType, Metadata
            FROM LlmUsageLog
            WHERE CreatedAt >= @fromUtc AND CreatedAt <= @toUtc
              AND (@providerFilter IS NULL OR Provider = @providerFilter)
            ORDER BY CreatedAt DESC
            """;
        cmd.Parameters.AddWithValue("@fromUtc", fromUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@toUtc", toUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@providerFilter", providerFilter ?? (object)DBNull.Value);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LlmUsageLogEntry
            {
                Id = reader.GetInt64(0),
                CreatedAt = DateTime.Parse(reader.GetString(1)),
                Provider = reader.GetString(2),
                Model = reader.GetString(3),
                AgentName = reader.GetString(4),
                RequestKind = reader.GetString(5),
                WorldId = reader.GetString(6),
                GroupId = reader.GetInt64(7),
                CharacterId = reader.IsDBNull(8) ? null : reader.GetString(8),
                TurnId = reader.IsDBNull(9) ? null : reader.GetString(9),
                SourceMessageId = reader.IsDBNull(10) ? null : reader.GetString(10),
                InputTokens = reader.GetInt64(11),
                OutputTokens = reader.GetInt64(12),
                CachedInputTokens = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                CacheHitTokens = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                CacheMissTokens = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                EstimatedCost = Convert.ToDecimal(reader.GetDouble(16)),
                Success = reader.GetInt32(17) == 1,
                ErrorType = reader.IsDBNull(18) ? null : reader.GetString(18),
                Metadata = reader.IsDBNull(19) ? "{}" : reader.GetString(19)
            });
        }

        var report = new LlmCostReport
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            RequestCount = rows.Count,
            SuccessCount = rows.Count(r => r.Success),
            FailureCount = rows.Count(r => !r.Success),
            InputTokens = rows.Sum(r => r.InputTokens),
            OutputTokens = rows.Sum(r => r.OutputTokens),
            CachedInputTokens = rows.Sum(r => r.CachedInputTokens ?? 0),
            CacheHitTokens = rows.Sum(r => r.CacheHitTokens ?? 0),
            CacheMissTokens = rows.Sum(r => r.CacheMissTokens ?? 0),
            EstimatedCost = rows.Sum(r => r.EstimatedCost)
        };
        report.ProviderModels = BuildBreakdown(rows, r => $"{r.Provider}/{r.Model}", 10);
        report.TopAgents = BuildBreakdown(rows, r => r.AgentName, 10);
        report.TopRequestKinds = BuildBreakdown(rows, r => r.RequestKind, 10);
        return report;
    }

    public async Task<List<LlmTurnCostRow>> GetRecentLlmTurnCostsAsync(int recentTurns)
    {
        var rows = new List<LlmUsageLogEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CreatedAt, Provider, Model, AgentName, RequestKind, WorldId, GroupId, CharacterId, TurnId, SourceMessageId,
                   InputTokens, OutputTokens, CachedInputTokens, CacheHitTokens, CacheMissTokens,
                   EstimatedCost, Success, ErrorType, Metadata
            FROM LlmUsageLog
            ORDER BY CreatedAt DESC
            LIMIT 1000
            """;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LlmUsageLogEntry
            {
                Id = reader.GetInt64(0),
                CreatedAt = DateTime.Parse(reader.GetString(1)),
                Provider = reader.GetString(2),
                Model = reader.GetString(3),
                AgentName = reader.GetString(4),
                RequestKind = reader.GetString(5),
                WorldId = reader.GetString(6),
                GroupId = reader.GetInt64(7),
                CharacterId = reader.IsDBNull(8) ? null : reader.GetString(8),
                TurnId = reader.IsDBNull(9) ? null : reader.GetString(9),
                SourceMessageId = reader.IsDBNull(10) ? null : reader.GetString(10),
                InputTokens = reader.GetInt64(11),
                OutputTokens = reader.GetInt64(12),
                CachedInputTokens = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                CacheHitTokens = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                CacheMissTokens = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                EstimatedCost = Convert.ToDecimal(reader.GetDouble(16)),
                Success = reader.GetInt32(17) == 1,
                ErrorType = reader.IsDBNull(18) ? null : reader.GetString(18),
                Metadata = reader.IsDBNull(19) ? "{}" : reader.GetString(19)
            });
        }

        return rows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.TurnId) ? $"fallback:{row.CreatedAt:O}:{row.SourceMessageId}" : row.TurnId!)
            .OrderByDescending(group => group.Max(x => x.CreatedAt))
            .Take(Math.Max(1, recentTurns))
            .Select(group =>
            {
                var latest = group.OrderByDescending(x => x.CreatedAt).First();
                var mostExpensive = group
                    .GroupBy(x => $"{x.AgentName}/{x.RequestKind}")
                    .Select(g => new { Name = g.Key, Cost = g.Sum(x => x.EstimatedCost) })
                    .OrderByDescending(x => x.Cost)
                    .FirstOrDefault();
                return new LlmTurnCostRow
                {
                    TurnId = group.Key,
                    SourceMessageId = latest.SourceMessageId ?? "",
                    SourceSummary = TryReadSourceSummary(latest.Metadata),
                    StartedAt = group.Min(x => x.CreatedAt),
                    RequestCount = group.Count(),
                    InputTokens = group.Sum(x => x.InputTokens),
                    OutputTokens = group.Sum(x => x.OutputTokens),
                    CachedInputTokens = group.Sum(x => x.CachedInputTokens ?? 0),
                    CacheHitTokens = group.Sum(x => x.CacheHitTokens ?? 0),
                    CacheMissTokens = group.Sum(x => x.CacheMissTokens ?? 0),
                    EstimatedCost = group.Sum(x => x.EstimatedCost),
                    MostExpensiveAgent = mostExpensive?.Name ?? ""
                };
            })
            .OrderByDescending(x => x.StartedAt)
            .ToList();
    }

    private static List<LlmCostBreakdown> BuildBreakdown(IEnumerable<LlmUsageLogEntry> rows, Func<LlmUsageLogEntry, string> keySelector, int limit)
    {
        return rows
            .GroupBy(keySelector)
            .Select(g => new LlmCostBreakdown
            {
                Name = string.IsNullOrWhiteSpace(g.Key) ? "unknown" : g.Key,
                RequestCount = g.Count(),
                SuccessCount = g.Count(x => x.Success),
                FailureCount = g.Count(x => !x.Success),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                CachedInputTokens = g.Sum(x => x.CachedInputTokens ?? 0),
                CacheHitTokens = g.Sum(x => x.CacheHitTokens ?? 0),
                CacheMissTokens = g.Sum(x => x.CacheMissTokens ?? 0),
                EstimatedCost = g.Sum(x => x.EstimatedCost)
            })
            .OrderByDescending(x => x.EstimatedCost)
            .ThenByDescending(x => x.RequestCount)
            .Take(limit)
            .ToList();
    }

    private static string TryReadSourceSummary(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (doc.RootElement.TryGetProperty("source_summary", out var summaryElement))
            {
                var summary = summaryElement.GetString() ?? "";
                return summary.Length <= 80 ? summary : summary[..80];
            }
        }
        catch
        {
        }

        return "";
    }

    /// <summary>
    /// 衰减所有记忆节点的 Heat 值
    /// heat *= 0.98/day
    /// NPC_STATE 节点的上限更低（0.6）
    /// </summary>
    public async Task DecayMemoryHeatAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        // 计算天数衰减：每衰减 0.02（即 0.98）
        cmd.CommandText = """
            UPDATE LongTermMemory
            SET Heat = CASE
                WHEN NodeType = 'NPC_STATE' THEN MIN(0.6, Heat * 0.98)
                ELSE MIN(1.0, Heat * 0.98)
            END
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND MemoryAudience = 'CharacterIC'
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // LLM Debug 相关方法
    // ════════════════════════════════════════════════════════════════════════════════════

    public async Task SetGlobalDebugEnabledAsync(TrpgScope scope, bool enabled)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO AiDebugSetting (WorldId, GroupId, DebugEnabled, UpdatedAt)
            VALUES (@worldId, @groupId, @enabled, @updatedAt)
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsGlobalDebugEnabledAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(DebugEnabled, 0) FROM AiDebugSetting
            WHERE WorldId = @worldId AND GroupId = @groupId
            LIMIT 1
        """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value && Convert.ToInt64(result) != 0;
    }

    public async Task InsertLlmDebugLogAsync(LlmDebugLogEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LlmDebugLog
                (CreatedAt, WorldId, GroupId, CharacterId, AgentName, RequestKind,
                 MessagesJson, ResponseText, Success, Error, InputCharCount, OutputCharCount, Metadata)
            VALUES
                (@createdAt, @worldId, @groupId, @characterId, @agentName, @requestKind,
                 @messagesJson, @responseText, @success, @error, @inputCharCount, @outputCharCount, @metadata)
            """;
        cmd.Parameters.AddWithValue("@createdAt", entry.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@worldId", entry.WorldId);
        cmd.Parameters.AddWithValue("@groupId", entry.GroupId);
        cmd.Parameters.AddWithValue("@characterId", entry.CharacterId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@agentName", entry.AgentName);
        cmd.Parameters.AddWithValue("@requestKind", entry.RequestKind);
        cmd.Parameters.AddWithValue("@messagesJson", entry.MessagesJson ?? "[]");
        cmd.Parameters.AddWithValue("@responseText", entry.ResponseText ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@success", entry.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("@error", entry.Error ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@inputCharCount", entry.InputCharCount);
        cmd.Parameters.AddWithValue("@outputCharCount", entry.OutputCharCount);
        cmd.Parameters.AddWithValue("@metadata", string.IsNullOrWhiteSpace(entry.Metadata) ? "{}" : entry.Metadata);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountLlmDebugLogsAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM LlmDebugLog
            WHERE WorldId = @worldId AND GroupId = @groupId
        """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value ? Convert.ToInt32(Convert.ToInt64(result)) : 0;
    }

    public async Task<List<LlmDebugLogEntry>> GetRecentLlmDebugLogsAsync(TrpgScope scope, int limit = 50)
    {
        var entries = new List<LlmDebugLogEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CreatedAt, WorldId, GroupId, CharacterId, AgentName, RequestKind,
                   MessagesJson, ResponseText, Success, Error, InputCharCount, OutputCharCount, Metadata
            FROM LlmDebugLog
            WHERE WorldId = @worldId AND GroupId = @groupId
            ORDER BY CreatedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new LlmDebugLogEntry
            {
                Id = reader.GetInt64(0),
                CreatedAt = DateTime.Parse(reader.GetString(1)),
                WorldId = reader.GetString(2),
                GroupId = reader.GetInt64(3),
                CharacterId = reader.IsDBNull(4) ? null : reader.GetString(4),
                AgentName = reader.GetString(5),
                RequestKind = reader.GetString(6),
                MessagesJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7),
                ResponseText = reader.IsDBNull(8) ? null : reader.GetString(8),
                Success = ReadInt64Value(reader, 9) != 0,
                Error = reader.IsDBNull(10) ? null : reader.GetString(10),
                InputCharCount = ReadInt32Value(reader, 11),
                OutputCharCount = ReadInt32Value(reader, 12),
                Metadata = reader.IsDBNull(13) ? "{}" : reader.GetString(13)
            });
        }
        return entries;
    }

    public async Task<List<LlmDebugLogEntry>> GetRecentLlmDebugLogsByAgentAsync(TrpgScope scope, string agentName, int limit = 50)
    {
        var entries = new List<LlmDebugLogEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CreatedAt, WorldId, GroupId, CharacterId, AgentName, RequestKind,
                   MessagesJson, ResponseText, Success, Error, InputCharCount, OutputCharCount, Metadata
            FROM LlmDebugLog
            WHERE WorldId = @worldId AND GroupId = @groupId AND AgentName = @agentName
            ORDER BY CreatedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@agentName", agentName);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new LlmDebugLogEntry
            {
                Id = reader.GetInt64(0),
                CreatedAt = DateTime.Parse(reader.GetString(1)),
                WorldId = reader.GetString(2),
                GroupId = reader.GetInt64(3),
                CharacterId = reader.IsDBNull(4) ? null : reader.GetString(4),
                AgentName = reader.GetString(5),
                RequestKind = reader.GetString(6),
                MessagesJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7),
                ResponseText = reader.IsDBNull(8) ? null : reader.GetString(8),
                Success = ReadInt64Value(reader, 9) != 0,
                Error = reader.IsDBNull(10) ? null : reader.GetString(10),
                InputCharCount = ReadInt32Value(reader, 11),
                OutputCharCount = ReadInt32Value(reader, 12),
                Metadata = reader.IsDBNull(13) ? "{}" : reader.GetString(13)
            });
        }
        return entries;
    }

    private static int ReadInt32Value(System.Data.Common.DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;

        return Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static long ReadInt64Value(System.Data.Common.DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0L;

        return Convert.ToInt64(reader.GetValue(ordinal));
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // Entity Salience 相关方法
    // ════════════════════════════════════════════════════════════════════════════════════

    public async Task TouchEntityHeatAsync(
        TrpgScope scope,
        string entityId,
        double deltaHeat,
        string? source = null,
        string? evidence = null,
        string? sceneId = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO EntitySalience
                (WorldId, GroupId, EntityId, Heat, LastMentionedAt, MentionCount, LastSceneId, LastMentionSource, LastMentionEvidence)
            VALUES
                (@worldId, @groupId, @entityId,
                 MAX(0, MIN(10, COALESCE((SELECT Heat FROM EntitySalience 
                    WHERE WorldId = @worldId AND GroupId = @groupId AND EntityId = @entityId), 0) + @deltaHeat)),
                 @now, 
                 COALESCE((SELECT MentionCount FROM EntitySalience 
                    WHERE WorldId = @worldId AND GroupId = @groupId AND EntityId = @entityId), 0) + 1,
                 @sceneId, @source, @evidence)
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@entityId", entityId);
        cmd.Parameters.AddWithValue("@deltaHeat", deltaHeat);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@sceneId", sceneId ?? "");
        cmd.Parameters.AddWithValue("@source", source ?? "");
        cmd.Parameters.AddWithValue("@evidence", evidence ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DecayEntityHeatAsync(TrpgScope scope, int currentFoldCount, int halfLifeFolds = 8)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE EntitySalience
            SET Heat = Heat * POWER(0.5, @deltaFold / {halfLifeFolds}.0)
            WHERE WorldId = @worldId AND GroupId = @groupId AND Heat > 0.01
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@deltaFold", currentFoldCount);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<(string EntityId, double Heat)>> GetHotEntitiesAsync(TrpgScope scope, int limit = 20)
    {
        var result = new List<(string, double)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT EntityId, Heat FROM EntitySalience
            WHERE WorldId = @worldId AND GroupId = @groupId
            ORDER BY Heat DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.GetDouble(1)));
        }
        return result;
    }

    private static string ResolveTier(string nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType)) return "Session";
        var normalized = nodeType.Trim().ToLowerInvariant();
        if (normalized is "rule" or "lore" or "world" or "core") return "CoreLore";
        if (normalized is "timeline" or "major" or "event") return "MajorEvent";
        if (normalized is "relationship" or "relation" or "npc") return "Relationship";
        if (normalized is "noise" or "ooc") return "Noise";
        return "Session";
    }

    private static double ComputeInitialHeat(double importance, string tier)
    {
        var tierBoost = tier switch
        {
            "CoreLore" => 0.30,
            "MajorEvent" => 0.20,
            "Relationship" => 0.15,
            "Session" => 0.05,
            _ => 0.0
        };
        return Math.Clamp((importance * 0.6) + 0.2 + tierBoost, 0.0, 1.0);
    }

    private static double ComputeRecencyScore(DateTime? lastUsed, DateTime createdAt)
    {
        var pivot = lastUsed ?? createdAt;
        if (pivot == DateTime.MinValue) return 0;
        var ageHours = Math.Max(0, (DateTime.UtcNow - pivot).TotalHours);
        return 1.0 / (1.0 + (ageHours / 72.0));
    }

    private static double ComputeEntityOverlap(string keywords, List<string>? currentEntities)
    {
        if (currentEntities == null || currentEntities.Count == 0)
            return 0;

        var keywordLower = keywords.ToLower();
        var overlapCount = currentEntities.Count(e => keywordLower.Contains(e.ToLower()));
        return (double)overlapCount / currentEntities.Count;
    }

    private static double ComputeSceneRelevance(string keywords, string? currentSceneId)
    {
        if (string.IsNullOrWhiteSpace(currentSceneId))
            return 0;

        var keywordLower = keywords.ToLower();
        var sceneLower = currentSceneId.ToLower();
        return keywordLower.Contains(sceneLower) ? 1.0 : 0;
    }

    private static double ComputeMemoryTypeWeight(string nodeType, double confidence, int foldCount)
    {
        nodeType = nodeType.Trim().ToLowerInvariant();

        // fact 类型权重更高，interpretation 类型根据 confidence 调整
        if (nodeType == "fact")
            return 1.0;
        if (nodeType == "interpretation")
        {
            // confidence 衰减：基于团内时间（每 20 次折叠衰减一次）
            var decayFactor = Math.Pow(0.95, foldCount / 20.0); // 每 20 次折叠衰减 5%
            var decayedConfidence = confidence * decayFactor;
            return decayedConfidence;
        }
        return 0.8; // 其他类型默认权重
    }

    public async Task DeleteHistoryEntriesAsync(TrpgScope scope, List<int> ids)
    {
        if (ids.Count == 0) return;
        var idParams = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM ChatHistory WHERE WorldId = @worldId AND Id IN ({idParams})";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertSceneSnapshotAsync(TrpgScope scope, SceneSnapshot snapshot)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SceneSnapshot (WorldId, GroupId, CharacterId, SceneId, SceneDescription, PresentEntities, StateProperties, SnapshotReason, CreatedAt)
            VALUES (@worldId, @groupId, @characterId, @sceneId, @sceneDesc, @entities, @properties, @reason, @createdAt)
            """;
        snapshot.WorldId = scope.WorldId;
        snapshot.GroupId = scope.GroupId;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", snapshot.GroupId);
        cmd.Parameters.AddWithValue("@characterId", snapshot.CharacterId ?? "");
        cmd.Parameters.AddWithValue("@sceneId", snapshot.SceneId ?? "");
        cmd.Parameters.AddWithValue("@sceneDesc", snapshot.SceneDescription ?? "");
        cmd.Parameters.AddWithValue("@entities", System.Text.Json.JsonSerializer.Serialize(snapshot.PresentEntities));
        cmd.Parameters.AddWithValue("@properties", System.Text.Json.JsonSerializer.Serialize(snapshot.StateProperties));
        cmd.Parameters.AddWithValue("@reason", snapshot.SnapshotReason ?? "");
        cmd.Parameters.AddWithValue("@createdAt", snapshot.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpsertBehaviorEvidenceAsync(TrpgScope scope, BehaviorEvidence evidence)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BehaviorEvidence (WorldId, GroupId, CharacterId, NpcId, Trait, Evidence, LastUpdated)
            VALUES (@worldId, @groupId, @characterId, @npcId, @trait, @evidence, @lastUpdated)
            """;
        evidence.WorldId = scope.WorldId;
        evidence.GroupId = scope.GroupId;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", evidence.GroupId);
        cmd.Parameters.AddWithValue("@characterId", evidence.CharacterId ?? "");
        cmd.Parameters.AddWithValue("@npcId", evidence.NpcId ?? "");
        cmd.Parameters.AddWithValue("@trait", evidence.Trait ?? "");
        cmd.Parameters.AddWithValue("@evidence", evidence.Evidence);
        cmd.Parameters.AddWithValue("@lastUpdated", evidence.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<BehaviorEvidence>> GetBehaviorEvidenceAsync(TrpgScope scope, string characterId, string npcId)
    {
        var groupId = scope.GroupId;
        var result = new List<BehaviorEvidence>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT NpcId, Trait, Evidence, LastUpdated
            FROM BehaviorEvidence
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND NpcId = @npcId
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@npcId", npcId ?? "");

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new BehaviorEvidence
            {
                WorldId = scope.WorldId,
                GroupId = groupId,
                CharacterId = characterId ?? "",
                NpcId = reader.GetString(0),
                Trait = reader.GetString(1),
                Evidence = reader.GetDouble(2),
                LastUpdated = DateTime.Parse(reader.GetString(3))
            });
        }
        return result;
    }

    public async Task DecayBehaviorEvidenceAsync(TrpgScope scope, string characterId, double decayFactor = 0.5)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE BehaviorEvidence
            SET Evidence = Evidence * @decayFactor,
                LastUpdated = datetime('now')
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@decayFactor", decayFactor);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteMemoryNodesAsync(TrpgScope scope, List<long> ids)
    {
        if (ids.Count == 0) return;
        var idParams = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM LongTermMemory WHERE WorldId = @worldId AND Id IN ({idParams})";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateMemoryConfidenceAsync(TrpgScope scope, long id, double newConfidence)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE LongTermMemory SET Confidence = @confidence WHERE WorldId = @worldId AND Id = @id";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@confidence", newConfidence);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task IncrementFoldCountAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE LongTermMemory SET FoldCount = FoldCount + 1 WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND MemoryAudience = 'CharacterIC'";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Token Estimation ──

    public static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int cjkCount = 0;
        int wordCount = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            // CJK Unified Ideographs + Extension A + Compatibility
            if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF') || (c >= '\uF900' && c <= '\uFAFF'))
            {
                cjkCount++;
                inWord = false;
            }
            else if (char.IsLetterOrDigit(c))
            {
                if (!inWord)
                {
                    wordCount++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
            }
        }
        return (int)(cjkCount * 0.9 + wordCount * 1.3 + 1);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }

    // ==================== 四层架构数据库操作 ====================

    // Quest 表操作
    public async Task InsertQuestAsync(TrpgScope scope, string characterId, string description, QuestStatus status, QuestPriority priority, string? sourceSceneId = null, bool hiddenFromPrompt = false)
    {
        var groupId = scope.GroupId;
        var now = DateTime.UtcNow.ToString("o");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Quest (WorldId, GroupId, CharacterId, Description, Status, Priority, CreatedAt, UpdatedAt, LastTouchedAt, HiddenFromPrompt, SourceSceneId, LastMentionedSceneId)
            VALUES (@worldId, @groupId, @characterId, @description, @status, @priority, @createdAt, @updatedAt, @lastTouchedAt, @hiddenFromPrompt, @sourceSceneId, @lastMentionedSceneId)
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@priority", priority.ToString());
        cmd.Parameters.AddWithValue("@createdAt", now);
        cmd.Parameters.AddWithValue("@updatedAt", now);
        cmd.Parameters.AddWithValue("@lastTouchedAt", now);
        cmd.Parameters.AddWithValue("@hiddenFromPrompt", hiddenFromPrompt ? 1 : 0);
        cmd.Parameters.AddWithValue("@sourceSceneId", sourceSceneId ?? "");
        cmd.Parameters.AddWithValue("@lastMentionedSceneId", sourceSceneId ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateQuestStatusAsync(TrpgScope scope, string characterId, string description, QuestStatus status)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Quest
            SET Status = @status, UpdatedAt = @updatedAt, CompletedAt = @completedAt
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND Description = @description
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@completedAt", status == QuestStatus.Completed ? DateTime.UtcNow.ToString("o") : (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<QuestObjective>> GetActiveQuestsAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Description, Status, Priority, CreatedAt, UpdatedAt, LastTouchedAt, CompletedAt, HiddenFromPrompt, SourceSceneId, LastMentionedSceneId
            FROM Quest
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND Status = 'Active'
            ORDER BY HiddenFromPrompt ASC, Priority DESC, LastTouchedAt DESC, UpdatedAt DESC, CreatedAt ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);

        var results = new List<QuestObjective>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var objective = new QuestObjective
            {
                Id = reader.GetInt64(0),
                Description = reader.GetString(1),
                Status = Enum.Parse<QuestStatus>(reader.GetString(2)),
                Priority = Enum.Parse<QuestPriority>(reader.GetString(3)),
                CreatedAt = DateTime.Parse(reader.GetString(4)),
                UpdatedAt = DateTime.Parse(reader.GetString(5)),
                LastTouchedAt = DateTime.Parse(reader.GetString(6)),
                CompletedAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                HiddenFromPrompt = !reader.IsDBNull(8) && reader.GetInt32(8) == 1,
                SourceSceneId = reader.IsDBNull(9) ? "" : reader.GetString(9),
                LastMentionedSceneId = reader.IsDBNull(10) ? "" : reader.GetString(10)
            };
            results.Add(objective);
        }
        return results;
    }

    public async Task<List<QuestObjective>> GetQuestsAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Description, Status, Priority, CreatedAt, UpdatedAt, LastTouchedAt, CompletedAt, HiddenFromPrompt, SourceSceneId, LastMentionedSceneId
            FROM Quest
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            ORDER BY UpdatedAt DESC, CreatedAt DESC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);

        var results = new List<QuestObjective>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new QuestObjective
            {
                Id = reader.GetInt64(0),
                Description = reader.GetString(1),
                Status = Enum.Parse<QuestStatus>(reader.GetString(2)),
                Priority = Enum.Parse<QuestPriority>(reader.GetString(3)),
                CreatedAt = DateTime.Parse(reader.GetString(4)),
                UpdatedAt = DateTime.Parse(reader.GetString(5)),
                LastTouchedAt = DateTime.Parse(reader.GetString(6)),
                CompletedAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                HiddenFromPrompt = !reader.IsDBNull(8) && reader.GetInt32(8) == 1,
                SourceSceneId = reader.IsDBNull(9) ? "" : reader.GetString(9),
                LastMentionedSceneId = reader.IsDBNull(10) ? "" : reader.GetString(10)
            });
        }

        return results;
    }

    public async Task UpdateQuestAsync(TrpgScope scope, string characterId, QuestObjective objective)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Quest
            SET Description = @description,
                Status = @status,
                Priority = @priority,
                UpdatedAt = @updatedAt,
                LastTouchedAt = @lastTouchedAt,
                CompletedAt = @completedAt,
                HiddenFromPrompt = @hiddenFromPrompt,
                SourceSceneId = @sourceSceneId,
                LastMentionedSceneId = @lastMentionedSceneId
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND Id = @id
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@id", objective.Id);
        cmd.Parameters.AddWithValue("@description", objective.Description);
        cmd.Parameters.AddWithValue("@status", objective.Status.ToString());
        cmd.Parameters.AddWithValue("@priority", objective.Priority.ToString());
        cmd.Parameters.AddWithValue("@updatedAt", objective.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@lastTouchedAt", objective.LastTouchedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@completedAt", objective.CompletedAt?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@hiddenFromPrompt", objective.HiddenFromPrompt ? 1 : 0);
        cmd.Parameters.AddWithValue("@sourceSceneId", objective.SourceSceneId ?? "");
        cmd.Parameters.AddWithValue("@lastMentionedSceneId", objective.LastMentionedSceneId ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateNarrativeMemoryNodeAsync(TrpgScope scope, NarrativeMemoryNode node)
    {
        node.WorldId = scope.WorldId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE NarrativeMemoryNode
            SET Summary = @summary,
                NarrativeWeight = @narrativeWeight,
                EmotionalWeight = @emotionalWeight,
                RelationshipImpact = @relationshipImpact,
                GoalImpact = @goalImpact,
                MysteryWeight = @mysteryWeight,
                IsResolved = @isResolved,
                InvolvedEntities = @involvedEntities,
                ArcTags = @arcTags,
                Timestamp = @timestamp,
                CreatedFoldCount = @createdFoldCount,
                SourceEventId = @sourceEventId
            WHERE WorldId = @worldId AND Id = @id
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", node.Id);
        cmd.Parameters.AddWithValue("@summary", node.Summary ?? "");
        cmd.Parameters.AddWithValue("@narrativeWeight", node.NarrativeWeight);
        cmd.Parameters.AddWithValue("@emotionalWeight", node.EmotionalWeight);
        cmd.Parameters.AddWithValue("@relationshipImpact", node.RelationshipImpact);
        cmd.Parameters.AddWithValue("@goalImpact", node.GoalImpact);
        cmd.Parameters.AddWithValue("@mysteryWeight", node.MysteryWeight);
        cmd.Parameters.AddWithValue("@isResolved", node.IsResolved ? 1 : 0);
        cmd.Parameters.AddWithValue("@involvedEntities", JsonSerializer.Serialize(node.InvolvedEntities ?? new List<string>()));
        cmd.Parameters.AddWithValue("@arcTags", JsonSerializer.Serialize(node.ArcTags ?? new List<string>()));
        cmd.Parameters.AddWithValue("@timestamp", node.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("@createdFoldCount", node.CreatedFoldCount);
        cmd.Parameters.AddWithValue("@sourceEventId", node.SourceEventId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> BackfillNarrativeMemoryNodeMetadataAsync(TrpgScope scope, string characterId)
    {
        var nodes = await QueryNarrativeMemoryNodesAsync(scope, characterId);
        var updated = 0;

        foreach (var node in nodes)
        {
            var changed = false;
            var tags = node.ArcTags ?? new List<string>();

            if (node.NarrativeWeight <= 0f)
            {
                node.NarrativeWeight = 0.3f;
                changed = true;
            }

            if (node.InvolvedEntities == null)
            {
                node.InvolvedEntities = new List<string>();
                changed = true;
            }

            if (tags.Count == 0)
            {
                node.ArcTags = NarrativeMemoryHeuristics.InferArcTags(node.Summary);
                tags = node.ArcTags;
                changed = true;
            }

            if (node.RelationshipImpact <= 0f)
            {
                node.RelationshipImpact = NarrativeMemoryHeuristics.InferRelationshipImpact(node.Summary, tags);
                changed = true;
            }

            if (node.GoalImpact <= 0f)
            {
                node.GoalImpact = NarrativeMemoryHeuristics.InferGoalImpact(node.Summary, tags);
                changed = true;
            }

            if (node.MysteryWeight <= 0f)
            {
                node.MysteryWeight = NarrativeMemoryHeuristics.InferMysteryWeight(node.Summary, tags);
                changed = true;
            }

            if (changed)
            {
                await UpdateNarrativeMemoryNodeAsync(scope, node);
                updated++;
            }
        }

        return updated;
    }

    // EntityCanonical 表操作
    public async Task UpsertEntityCanonicalAsync(TrpgScope scope, EntityCanonicalRecord record)
    {
        record.WorldId = scope.WorldId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO EntityCanonical (WorldId, EntityId, CurrentDisplayName, Aliases, IdentityStatus, CoreSummary, EntityFactSummary, PersistentFacts, Relationships, Version, ConflictReason, CreatedAt, LastUpdated)
            VALUES (@worldId, @entityId, @displayName, @aliases, @status, @coreSummary, @entityFactSummary, @persistentFacts, @relationships, @version, @conflictReason, @createdAt, @lastUpdated)
            ON CONFLICT(WorldId, EntityId) DO UPDATE SET
                CurrentDisplayName = @displayName,
                Aliases = @aliases,
                IdentityStatus = @status,
                CoreSummary = @coreSummary,
                EntityFactSummary = @entityFactSummary,
                PersistentFacts = @persistentFacts,
                Relationships = @relationships,
                Version = @version,
                ConflictReason = @conflictReason,
                LastUpdated = @lastUpdated
            """;
        cmd.Parameters.AddWithValue("@worldId", record.WorldId);
        cmd.Parameters.AddWithValue("@entityId", record.EntityId);
        cmd.Parameters.AddWithValue("@displayName", record.CurrentDisplayName);
        cmd.Parameters.AddWithValue("@aliases", JsonSerializer.Serialize(record.Aliases));
        cmd.Parameters.AddWithValue("@status", record.IdentityStatus.ToString());
        cmd.Parameters.AddWithValue("@coreSummary", record.CoreSummary ?? "");
        cmd.Parameters.AddWithValue("@entityFactSummary", record.EntityFactSummary ?? "");
        cmd.Parameters.AddWithValue("@persistentFacts", JsonSerializer.Serialize(record.PersistentFacts));
        cmd.Parameters.AddWithValue("@relationships", JsonSerializer.Serialize(record.Relationships));
        cmd.Parameters.AddWithValue("@version", record.Version);
        cmd.Parameters.AddWithValue("@conflictReason", record.ConflictReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", record.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@lastUpdated", record.LastUpdated.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<EntityCanonicalRecord>> GetAllEntityCanonicalAsync(TrpgScope scope)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT WorldId, EntityId, CurrentDisplayName, Aliases, IdentityStatus, CoreSummary, EntityFactSummary, PersistentFacts, Relationships, Version, ConflictReason, CreatedAt, LastUpdated FROM EntityCanonical WHERE WorldId = @worldId";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);

        var results = new List<EntityCanonicalRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var record = new EntityCanonicalRecord
            {
                WorldId = reader.GetString(0),
                EntityId = reader.GetString(1),
                CurrentDisplayName = reader.GetString(2),
                Aliases = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? new(),
                IdentityStatus = Enum.Parse<EntityIdentityStatus>(reader.GetString(4)),
                CoreSummary = reader.IsDBNull(5) ? "" : reader.GetString(5),
                EntityFactSummary = reader.IsDBNull(6) ? "" : reader.GetString(6),
                PersistentFacts = JsonSerializer.Deserialize<List<PersistentFact>>(reader.IsDBNull(7) ? "[]" : reader.GetString(7)) ?? new(),
                Relationships = JsonSerializer.Deserialize<Dictionary<string, DynamicRelationship>>(reader.IsDBNull(8) ? "{}" : reader.GetString(8)) ?? new(),
                Version = reader.IsDBNull(9) ? 1 : reader.GetInt32(9),
                ConflictReason = reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAt = DateTime.Parse(reader.GetString(11)),
                LastUpdated = DateTime.Parse(reader.GetString(12))
            };
            results.Add(record);
        }
        return results;
    }

    public async Task<EntityCanonicalRecord?> GetEntityCanonicalAsync(TrpgScope scope, string entityId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT WorldId, EntityId, CurrentDisplayName, Aliases, IdentityStatus, CoreSummary, EntityFactSummary, PersistentFacts, Relationships, Version, ConflictReason, CreatedAt, LastUpdated FROM EntityCanonical WHERE WorldId = @worldId AND EntityId = @entityId";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@entityId", entityId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new EntityCanonicalRecord
            {
                WorldId = reader.GetString(0),
                EntityId = reader.GetString(1),
                CurrentDisplayName = reader.GetString(2),
                Aliases = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? new(),
                IdentityStatus = Enum.Parse<EntityIdentityStatus>(reader.GetString(4)),
                CoreSummary = reader.IsDBNull(5) ? "" : reader.GetString(5),
                EntityFactSummary = reader.IsDBNull(6) ? "" : reader.GetString(6),
                PersistentFacts = JsonSerializer.Deserialize<List<PersistentFact>>(reader.IsDBNull(7) ? "[]" : reader.GetString(7)) ?? new(),
                Relationships = JsonSerializer.Deserialize<Dictionary<string, DynamicRelationship>>(reader.IsDBNull(8) ? "{}" : reader.GetString(8)) ?? new(),
                Version = reader.IsDBNull(9) ? 1 : reader.GetInt32(9),
                ConflictReason = reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAt = DateTime.Parse(reader.GetString(11)),
                LastUpdated = DateTime.Parse(reader.GetString(12))
            };
        }
        return null;
    }

    // EventLog 表操作
    public async Task<long> InsertEventLogAsync(TrpgScope scope, WorldEvent worldEvent)
    {
        worldEvent.WorldId = scope.WorldId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO EventLog (WorldId, Timestamp, EventType, Payload, SourceEntityId, TargetEntityId, SceneId, Consequences)
            VALUES (@worldId, @timestamp, @eventType, @payload, @sourceEntityId, @targetEntityId, @sceneId, @consequences)
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@timestamp", worldEvent.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("@eventType", worldEvent.EventType);
        cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(worldEvent.Payload));
        cmd.Parameters.AddWithValue("@sourceEntityId", worldEvent.SourceEntityId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@targetEntityId", worldEvent.TargetEntityId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sceneId", worldEvent.SceneId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@consequences", JsonSerializer.Serialize(worldEvent.Consequences));
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private const string EventLogSelectColumns = """
        EventId,
        WorldId,
        Timestamp,
        EventType,
        Payload,
        SourceEntityId,
        TargetEntityId,
        SceneId,
        Consequences,
        SemanticSummary,
        NarrativeWeight,
        NarrativeTags,
        EmotionalWeight,
        ArcAffinity,
        IsSemanticallyDistilled
        """;

    public async Task<List<WorldEvent>> QueryEventLogAsync(TrpgScope scope, long fromEventId, long? toEventId = null, int limit = 100)
    {
        using var cmd = _connection.CreateCommand();
        if (toEventId.HasValue)
        {
            cmd.CommandText = $"""
                SELECT {EventLogSelectColumns}
                FROM EventLog
                WHERE WorldId = @worldId AND EventId >= @fromEventId AND EventId <= @toEventId
                ORDER BY EventId ASC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@toEventId", toEventId.Value);
        }
        else
        {
            cmd.CommandText = $"""
                SELECT {EventLogSelectColumns}
                FROM EventLog
                WHERE WorldId = @worldId AND EventId >= @fromEventId
                ORDER BY EventId ASC
                LIMIT @limit
                """;
        }
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@fromEventId", fromEventId);
        cmd.Parameters.AddWithValue("@limit", limit);

        return await ReadWorldEventsAsync(cmd);
    }

    public async Task<List<WorldEvent>> QueryEventsByEntityAsync(TrpgScope scope, string entityId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {EventLogSelectColumns}
            FROM EventLog
            WHERE WorldId = @worldId AND (SourceEntityId = @entityId OR TargetEntityId = @entityId)
            ORDER BY EventId ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@entityId", entityId);

        return await ReadWorldEventsAsync(cmd);
    }

    public async Task<List<WorldEvent>> QueryEventsBySceneAsync(TrpgScope scope, string sceneId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {EventLogSelectColumns}
            FROM EventLog
            WHERE WorldId = @worldId AND SceneId = @sceneId
            ORDER BY EventId ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@sceneId", sceneId);

        return await ReadWorldEventsAsync(cmd);
    }

    public async Task<List<WorldEvent>> QueryEventsByTypeAsync(TrpgScope scope, string eventType)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {EventLogSelectColumns}
            FROM EventLog
            WHERE WorldId = @worldId AND EventType = @eventType
            ORDER BY EventId ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@eventType", eventType);

        return await ReadWorldEventsAsync(cmd);
    }

    public async Task<List<WorldEvent>> QueryUndistilledEventsAsync(TrpgScope scope, int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {EventLogSelectColumns}
            FROM EventLog
            WHERE WorldId = @worldId
              AND COALESCE(IsSemanticallyDistilled, 0) = 0
            ORDER BY EventId ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@limit", limit);

        return await ReadWorldEventsAsync(cmd);
    }

    private static async Task<List<WorldEvent>> ReadWorldEventsAsync(SQLiteCommand cmd)
    {
        var results = new List<WorldEvent>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var evt = new WorldEvent
            {
                EventId = reader.GetInt64(0),
                WorldId = reader.GetString(1),
                Timestamp = DateTime.Parse(reader.GetString(2)),
                EventType = reader.GetString(3),
                Payload = JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(4)) ?? new(),
                SourceEntityId = reader.IsDBNull(5) ? null : reader.GetString(5),
                TargetEntityId = reader.IsDBNull(6) ? null : reader.GetString(6),
                SceneId = reader.IsDBNull(7) ? null : reader.GetString(7),
                Consequences = JsonSerializer.Deserialize<List<long>>(reader.GetString(8)) ?? new List<long>(),
                SemanticSummary = reader.IsDBNull(9) ? null : reader.GetString(9),
                NarrativeWeight = reader.IsDBNull(10) ? 0.0 : reader.GetDouble(10),
                NarrativeTags = JsonSerializer.Deserialize<List<string>>(reader.IsDBNull(11) ? "[]" : reader.GetString(11)) ?? new(),
                EmotionalWeight = reader.IsDBNull(12) ? 0.0 : reader.GetDouble(12),
                ArcAffinity = reader.IsDBNull(13) ? null : reader.GetString(13),
                IsSemanticallyDistilled = !reader.IsDBNull(14) && reader.GetInt32(14) != 0
            };
            results.Add(evt);
        }
        return results;
    }

    /// <summary>
    /// 更新事件的因果链
    /// </summary>
    public async Task UpdateEventConsequencesAsync(TrpgScope scope, long eventId, List<long> consequences)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE EventLog SET Consequences = @consequences WHERE WorldId = @worldId AND EventId = @eventId";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@consequences", JsonSerializer.Serialize(consequences));
        cmd.Parameters.AddWithValue("@eventId", eventId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 更新事件的语义元数据
    /// </summary>
    public async Task UpdateEventSemanticMetadataAsync(TrpgScope scope, long eventId, WorldEvent worldEvent)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE EventLog 
            SET SemanticSummary = @semanticSummary,
                NarrativeWeight = @narrativeWeight,
                NarrativeTags = @narrativeTags,
                EmotionalWeight = @emotionalWeight,
                ArcAffinity = @arcAffinity,
                IsSemanticallyDistilled = @isSemanticallyDistilled
            WHERE WorldId = @worldId AND EventId = @eventId
        """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@semanticSummary", worldEvent.SemanticSummary ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@narrativeWeight", worldEvent.NarrativeWeight);
        cmd.Parameters.AddWithValue("@narrativeTags", JsonSerializer.Serialize(worldEvent.NarrativeTags));
        cmd.Parameters.AddWithValue("@emotionalWeight", worldEvent.EmotionalWeight);
        cmd.Parameters.AddWithValue("@arcAffinity", worldEvent.ArcAffinity ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@isSemanticallyDistilled", worldEvent.IsSemanticallyDistilled ? 1 : 0);
        cmd.Parameters.AddWithValue("@eventId", eventId);
        await cmd.ExecuteNonQueryAsync();
    }

    // NarrativeMemoryNode 表操作
    public async Task<long> InsertNarrativeMemoryNodeAsync(TrpgScope scope, string characterId, NarrativeMemoryNode node)
    {
        var groupId = scope.GroupId;
        node.WorldId = scope.WorldId;
        node.CreatedFoldCount = await GetCurrentFoldCountAsync(scope, characterId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO NarrativeMemoryNode (WorldId, GroupId, CharacterId, Summary, NarrativeWeight, EmotionalWeight, RelationshipImpact, GoalImpact, MysteryWeight, IsResolved, InvolvedEntities, ArcTags, Timestamp, CreatedFoldCount, SourceEventId)
            VALUES (@worldId, @groupId, @characterId, @summary, @narrativeWeight, @emotionalWeight, @relationshipImpact, @goalImpact, @mysteryWeight, @isResolved, @involvedEntities, @arcTags, @timestamp, @createdFoldCount, @sourceEventId)
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@summary", node.Summary);
        cmd.Parameters.AddWithValue("@narrativeWeight", node.NarrativeWeight);
        cmd.Parameters.AddWithValue("@emotionalWeight", node.EmotionalWeight);
        cmd.Parameters.AddWithValue("@relationshipImpact", node.RelationshipImpact);
        cmd.Parameters.AddWithValue("@goalImpact", node.GoalImpact);
        cmd.Parameters.AddWithValue("@mysteryWeight", node.MysteryWeight);
        cmd.Parameters.AddWithValue("@isResolved", node.IsResolved ? 1 : 0);
        cmd.Parameters.AddWithValue("@involvedEntities", JsonSerializer.Serialize(node.InvolvedEntities));
        cmd.Parameters.AddWithValue("@arcTags", JsonSerializer.Serialize(node.ArcTags));
        cmd.Parameters.AddWithValue("@timestamp", node.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("@createdFoldCount", node.CreatedFoldCount);
        cmd.Parameters.AddWithValue("@sourceEventId", node.SourceEventId);

        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<NarrativeMemoryNode>> QueryNarrativeMemoryNodesAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, Summary, NarrativeWeight, EmotionalWeight, RelationshipImpact, GoalImpact, MysteryWeight, IsResolved, InvolvedEntities, ArcTags, Timestamp, CreatedFoldCount, SourceEventId
            FROM NarrativeMemoryNode
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            ORDER BY Timestamp DESC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", groupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);

        var results = new List<NarrativeMemoryNode>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var node = new NarrativeMemoryNode
            {
                Id = reader.GetInt64(0),
                WorldId = reader.GetString(1),
                Summary = reader.GetString(2),
                NarrativeWeight = reader.GetFloat(3),
                EmotionalWeight = reader.GetFloat(4),
                RelationshipImpact = reader.GetFloat(5),
                GoalImpact = reader.GetFloat(6),
                MysteryWeight = reader.GetFloat(7),
                IsResolved = reader.GetInt32(8) == 1,
                InvolvedEntities = JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? new(),
                ArcTags = JsonSerializer.Deserialize<List<string>>(reader.GetString(10)) ?? new(),
                Timestamp = DateTime.Parse(reader.GetString(11)),
                CreatedFoldCount = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                SourceEventId = reader.GetInt64(13)
            };
            results.Add(node);
        }
        return results;
    }

    // CausalGraph 表操作
    public async Task<int> ResolveNarrativeMemoryNodesByEventAsync(
        TrpgScope scope,
        string characterId,
        WorldEvent evt)
    {
        if (evt == null || !IsNarrativeResolutionEvent(evt.EventType))
            return 0;

        var nodes = await QueryNarrativeMemoryNodesAsync(scope, characterId);
        var unresolved = nodes.Where(n => !n.IsResolved).ToList();
        if (unresolved.Count == 0)
            return 0;

        var eventEntities = BuildEventEntitySet(evt);
        var eventTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in evt.NarrativeTags ?? new List<string>())
            AddResolutionTerm(eventTags, tag);
        AddResolutionTerm(eventTags, evt.ArcAffinity);
        AddResolutionTerm(eventTags, evt.EventType);

        var terms = ExtractNarrativeResolutionTerms(evt);
        var resolved = 0;

        foreach (var node in unresolved)
        {
            var nodeEntities = (node.InvolvedEntities ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entityMatched = eventEntities.Count > 0 && nodeEntities.Overlaps(eventEntities);
            var tagMatched = eventTags.Count > 0
                && (node.ArcTags ?? new List<string>()).Any(tag => eventTags.Any(eventTag => TextLooseMatch(tag, eventTag)));
            var summaryMatched = terms.Count > 0
                && terms.Any(term => TextLooseMatch(node.Summary, term));

            if (!entityMatched && !tagMatched && !summaryMatched)
                continue;

            node.IsResolved = true;
            await UpdateNarrativeMemoryNodeAsync(scope, node);
            resolved++;
        }

        return resolved;
    }

    private static bool IsNarrativeResolutionEvent(string? eventType)
    {
        var type = eventType?.Trim().ToLowerInvariant() ?? "";
        return type is "objective_complete"
            or "objective_failure"
            or "objective_failed"
            or "objective_cancelled"
            or "npc_identity_reveal"
            or "identity_reveal"
            or "mystery_reveal"
            or "item_acquisition"
            or "item_loss"
            or "item_consume"
            or "item_consumed"
            or "inventory_change"
            or "gm_correction";
    }

    private static HashSet<string> BuildEventEntitySet(WorldEvent evt)
    {
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var actor in evt.Actors ?? new List<string>())
            AddResolutionTerm(entities, actor);
        AddResolutionTerm(entities, evt.SourceEntityId);
        AddResolutionTerm(entities, evt.TargetEntityId);
        return entities;
    }

    private static HashSet<string> ExtractNarrativeResolutionTerms(WorldEvent evt)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddResolutionTerm(terms, evt.Result);
        AddResolutionTerm(terms, evt.Location);
        AddResolutionTerm(terms, evt.SceneId);
        AddResolutionTerm(terms, evt.SemanticSummary);
        AddResolutionTerm(terms, evt.ArcAffinity);

        foreach (var tag in evt.NarrativeTags ?? new List<string>())
            AddResolutionTerm(terms, tag);

        foreach (var actor in evt.Actors ?? new List<string>())
            AddResolutionTerm(terms, actor);

        foreach (var payload in evt.Payload ?? new Dictionary<string, object>())
        {
            AddResolutionTerm(terms, payload.Key);
            AddResolutionTerm(terms, payload.Value?.ToString());
        }

        foreach (var generic in new[]
        {
            "objective", "complete", "failed", "failure", "identity", "reveal",
            "mystery", "item", "acquisition", "loss", "consume", "inventory",
            "goal", "quest", "resolved", "correction"
        })
        {
            terms.Remove(generic);
        }

        return terms;
    }

    private static void AddResolutionTerm(HashSet<string> terms, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalized = NormalizeLooseText(value);
        if (normalized.Length < 2 || normalized.Length > 80)
            return;

        terms.Add(normalized);
    }

    private static bool TextLooseMatch(string? haystack, string? needle)
    {
        var h = NormalizeLooseText(haystack);
        var n = NormalizeLooseText(needle);
        if (h.Length < 2 || n.Length < 2)
            return false;

        return h.Contains(n, StringComparison.OrdinalIgnoreCase)
            || n.Contains(h, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLooseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var chars = normalized
            .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
            .ToArray();
        return new string(chars);
    }

    public async Task InsertCausalEdgeAsync(TrpgScope scope, CausalGraph.CausalEdge edge)
    {
        edge.WorldId = scope.WorldId;
        edge.GroupId = scope.GroupId;
        using (var checkCmd = _connection.CreateCommand())
        {
            checkCmd.CommandText = """
                SELECT Weight
                FROM CausalGraph
                WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
                  AND SourceEventId = @sourceEventId AND TargetEventId = @targetEventId AND EdgeType = @edgeType
                LIMIT 1
                """;
            checkCmd.Parameters.AddWithValue("@worldId", edge.WorldId);
            checkCmd.Parameters.AddWithValue("@groupId", edge.GroupId);
            checkCmd.Parameters.AddWithValue("@characterId", edge.CharacterId ?? "");
            checkCmd.Parameters.AddWithValue("@sourceEventId", edge.SourceEventId);
            checkCmd.Parameters.AddWithValue("@targetEventId", edge.TargetEventId);
            checkCmd.Parameters.AddWithValue("@edgeType", edge.EdgeType.ToString());
            var existingWeight = await checkCmd.ExecuteScalarAsync();
            if (existingWeight != null && existingWeight != DBNull.Value)
            {
                var mergedWeight = Math.Max(Convert.ToDouble(existingWeight), edge.Weight);
                using var updateCmd = _connection.CreateCommand();
                updateCmd.CommandText = """
                    UPDATE CausalGraph
                    SET Weight = @weight, CreatedAt = datetime('now'), CreatedFoldCount = @createdFoldCount
                    WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
                      AND SourceEventId = @sourceEventId AND TargetEventId = @targetEventId AND EdgeType = @edgeType
                    """;
                updateCmd.Parameters.AddWithValue("@worldId", edge.WorldId);
                updateCmd.Parameters.AddWithValue("@weight", mergedWeight);
                updateCmd.Parameters.AddWithValue("@createdFoldCount", edge.CreatedFoldCount);
                updateCmd.Parameters.AddWithValue("@groupId", edge.GroupId);
                updateCmd.Parameters.AddWithValue("@characterId", edge.CharacterId ?? "");
                updateCmd.Parameters.AddWithValue("@sourceEventId", edge.SourceEventId);
                updateCmd.Parameters.AddWithValue("@targetEventId", edge.TargetEventId);
                updateCmd.Parameters.AddWithValue("@edgeType", edge.EdgeType.ToString());
                await updateCmd.ExecuteNonQueryAsync();
                return;
            }
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CausalGraph (WorldId, GroupId, CharacterId, SourceEventId, TargetEventId, EdgeType, Weight, CreatedFoldCount)
            VALUES (@worldId, @groupId, @characterId, @sourceEventId, @targetEventId, @edgeType, @weight, @createdFoldCount)
            """;
        cmd.Parameters.AddWithValue("@worldId", edge.WorldId);
        cmd.Parameters.AddWithValue("@groupId", edge.GroupId);
        cmd.Parameters.AddWithValue("@characterId", edge.CharacterId ?? "");
        cmd.Parameters.AddWithValue("@sourceEventId", edge.SourceEventId);
        cmd.Parameters.AddWithValue("@targetEventId", edge.TargetEventId);
        cmd.Parameters.AddWithValue("@edgeType", edge.EdgeType.ToString());
        cmd.Parameters.AddWithValue("@weight", edge.Weight);
        cmd.Parameters.AddWithValue("@createdFoldCount", edge.CreatedFoldCount);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<CausalGraph.CausalEdge>> GetCausalEdgesBySourceAsync(TrpgScope scope, long sourceEventId, string? characterId = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(characterId)
            ? """
                SELECT WorldId, GroupId, CharacterId, SourceEventId, TargetEventId, EdgeType, Weight, CreatedAt, CreatedFoldCount
                FROM CausalGraph
                WHERE WorldId = @worldId AND SourceEventId = @sourceEventId
                ORDER BY CreatedAt DESC
                """
            : """
                SELECT WorldId, GroupId, CharacterId, SourceEventId, TargetEventId, EdgeType, Weight, CreatedAt, CreatedFoldCount
                FROM CausalGraph
                WHERE WorldId = @worldId AND SourceEventId = @sourceEventId AND GroupId = @groupId AND CharacterId = @characterId
                ORDER BY CreatedAt DESC
                """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@sourceEventId", sourceEventId);

        return await ReadCausalEdgesAsync(cmd);
    }

    public async Task<List<CausalGraph.CausalEdge>> GetCausalEdgesByTargetAsync(TrpgScope scope, long targetEventId, string? characterId = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(characterId)
            ? """
                SELECT WorldId, GroupId, CharacterId, SourceEventId, TargetEventId, EdgeType, Weight, CreatedAt, CreatedFoldCount
                FROM CausalGraph
                WHERE WorldId = @worldId AND TargetEventId = @targetEventId
                ORDER BY CreatedAt DESC
                """
            : """
                SELECT WorldId, GroupId, CharacterId, SourceEventId, TargetEventId, EdgeType, Weight, CreatedAt, CreatedFoldCount
                FROM CausalGraph
                WHERE WorldId = @worldId AND TargetEventId = @targetEventId AND GroupId = @groupId AND CharacterId = @characterId
                ORDER BY CreatedAt DESC
                """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@targetEventId", targetEventId);

        return await ReadCausalEdgesAsync(cmd);
    }

    public async Task<List<CausalGraph.CausalEdge>> GetAllCausalEdgesAsync(TrpgScope scope, string? characterId = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(characterId)
            ? """
                SELECT WorldId, GroupId, CharacterId, SourceEventId, TargetEventId, EdgeType, Weight, CreatedAt, CreatedFoldCount
                FROM CausalGraph
                WHERE WorldId = @worldId
                ORDER BY CreatedAt DESC
                """
            : """
                SELECT WorldId, GroupId, CharacterId, SourceEventId, TargetEventId, EdgeType, Weight, CreatedAt, CreatedFoldCount
                FROM CausalGraph
                WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
                ORDER BY CreatedAt DESC
                """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");

        return await ReadCausalEdgesAsync(cmd);
    }

    public async Task DeleteCausalEdgeAsync(TrpgScope scope, long sourceEventId, long targetEventId, string? characterId = null)
    {
        using var cmd = _connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(characterId))
        {
            cmd.CommandText = "DELETE FROM CausalGraph WHERE WorldId = @worldId AND SourceEventId = @sourceEventId AND TargetEventId = @targetEventId AND GroupId = @groupId AND CharacterId = @characterId";
            cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
            cmd.Parameters.AddWithValue("@characterId", characterId);
        }
        else
        {
            cmd.CommandText = "DELETE FROM CausalGraph WHERE WorldId = @worldId AND SourceEventId = @sourceEventId AND TargetEventId = @targetEventId";
        }
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@sourceEventId", sourceEventId);
        cmd.Parameters.AddWithValue("@targetEventId", targetEventId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateCausalEdgeWeightAsync(TrpgScope scope, long sourceEventId, long targetEventId, double weight, string? characterId = null)
    {
        using var cmd = _connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(characterId))
        {
            cmd.CommandText = "UPDATE CausalGraph SET Weight = @weight WHERE WorldId = @worldId AND SourceEventId = @sourceEventId AND TargetEventId = @targetEventId AND GroupId = @groupId AND CharacterId = @characterId";
            cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
            cmd.Parameters.AddWithValue("@characterId", characterId);
        }
        else
        {
            cmd.CommandText = "UPDATE CausalGraph SET Weight = @weight WHERE WorldId = @worldId AND SourceEventId = @sourceEventId AND TargetEventId = @targetEventId";
        }
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@weight", weight);
        cmd.Parameters.AddWithValue("@sourceEventId", sourceEventId);
        cmd.Parameters.AddWithValue("@targetEventId", targetEventId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<CausalGraph.CausalEdge>> ReadCausalEdgesAsync(SQLiteCommand cmd)
    {
        var results = new List<CausalGraph.CausalEdge>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new CausalGraph.CausalEdge
            {
                WorldId = reader.GetString(0),
                GroupId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                CharacterId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                SourceEventId = reader.GetInt64(3),
                TargetEventId = reader.GetInt64(4),
                EdgeType = Enum.Parse<CausalGraph.EdgeType>(reader.GetString(5)),
                Weight = reader.GetDouble(6),
                CreatedAt = DateTime.Parse(reader.GetString(7)),
                CreatedFoldCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8)
            });
        }
        return results;
    }

    private async Task MigrateCharacterMemoryV2Async()
    {
        try
        {
            using var pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(CharacterMemory)";
            using var reader = await pragma.ExecuteReaderAsync();
            bool hasIsFoundational = false, hasRelatedEntityId = false, hasFoldCount = false, hasLastAccessedFoldCount = false;
            while (await reader.ReadAsync())
            {
                var col = reader.GetString(1);
                if (col == "IsFoundational") hasIsFoundational = true;
                if (col == "RelatedEntityId") hasRelatedEntityId = true;
                if (col == "FoldCount") hasFoldCount = true;
                if (col == "LastAccessedFoldCount") hasLastAccessedFoldCount = true;
            }
            if (!hasIsFoundational)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE CharacterMemory ADD COLUMN IsFoundational INTEGER DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }
            if (!hasRelatedEntityId)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE CharacterMemory ADD COLUMN RelatedEntityId TEXT";
                await cmd.ExecuteNonQueryAsync();
            }
            if (!hasFoldCount)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE CharacterMemory ADD COLUMN FoldCount INTEGER DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }
            if (!hasLastAccessedFoldCount)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE CharacterMemory ADD COLUMN LastAccessedFoldCount INTEGER DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod] MigrateCharacterMemoryV2: {ex.Message}");
        }
    }

    private async Task MigrateCausalGraphV2Async()
    {
        try
        {
            using var pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(CausalGraph)";
            using var reader = await pragma.ExecuteReaderAsync();
            bool hasGroupId = false, hasCharacterId = false, hasCreatedFoldCount = false;
            while (await reader.ReadAsync())
            {
                var col = reader.GetString(1);
                if (col == "GroupId") hasGroupId = true;
                if (col == "CharacterId") hasCharacterId = true;
                if (col == "CreatedFoldCount") hasCreatedFoldCount = true;
            }

            if (!hasGroupId)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE CausalGraph ADD COLUMN GroupId INTEGER NOT NULL DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }
            if (!hasCharacterId)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE CausalGraph ADD COLUMN CharacterId TEXT NOT NULL DEFAULT ''";
                await cmd.ExecuteNonQueryAsync();
            }
            if (!hasCreatedFoldCount)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE CausalGraph ADD COLUMN CreatedFoldCount INTEGER NOT NULL DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }

            using var idxCmd = _connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_causalgraph_group_char ON CausalGraph(GroupId, CharacterId)";
            await idxCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod] MigrateCausalGraphV2: {ex.Message}");
        }
    }

    // CharacterMemory 表操作
    public async Task InsertCharacterMemoryAsync(
        TrpgScope scope,
        string characterId,
        string memoryType,
        string content,
        double confidence = 0.8,
        long? relatedEventId = null,
        string? relatedEntityId = null,
        string? metadataJson = null,
        bool isFoundational = false,
        int foldCount = 0)
    {
        var normalizedType = NormalizeCharacterMemoryType(memoryType);
        var memory = new EpisodicMemory.CharacterMemory
        {
            WorldId = scope.WorldId,
            GroupId = scope.GroupId,
            CharacterId = characterId,
            MemoryType = normalizedType,
            Content = content,
            Confidence = confidence,
            RelatedEventId = relatedEventId,
            Metadata = string.IsNullOrWhiteSpace(metadataJson)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) ?? new Dictionary<string, object>(),
            IsFoundational = isFoundational,
            RelatedEntityId = relatedEntityId,
            FoldCount = foldCount,
            LastAccessedFoldCount = foldCount
        };

        await InsertCharacterMemoryAsync(scope, memory);
    }

    public async Task InsertCharacterMemoryAsync(TrpgScope scope, EpisodicMemory.CharacterMemory memory)
    {
        memory.WorldId = scope.WorldId;
        memory.GroupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CharacterMemory (WorldId, GroupId, CharacterId, MemoryType, Content, Confidence, RelatedEventId, Metadata, IsFoundational, RelatedEntityId, FoldCount, LastAccessedFoldCount)
            VALUES (@worldId, @groupId, @characterId, @memoryType, @content, @confidence, @relatedEventId, @metadata, @isFoundational, @relatedEntityId, @foldCount, @lastAccessedFoldCount)
            """;
        cmd.Parameters.AddWithValue("@worldId", memory.WorldId);
        cmd.Parameters.AddWithValue("@groupId", memory.GroupId);
        cmd.Parameters.AddWithValue("@characterId", memory.CharacterId);
        cmd.Parameters.AddWithValue("@memoryType", memory.MemoryType.ToString());
        cmd.Parameters.AddWithValue("@content", memory.Content);
        cmd.Parameters.AddWithValue("@confidence", memory.Confidence);
        cmd.Parameters.AddWithValue("@relatedEventId", memory.RelatedEventId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(memory.Metadata));
        cmd.Parameters.AddWithValue("@isFoundational", memory.IsFoundational ? 1 : 0);
        cmd.Parameters.AddWithValue("@relatedEntityId", memory.RelatedEntityId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@foldCount", memory.FoldCount);
        cmd.Parameters.AddWithValue("@lastAccessedFoldCount", memory.LastAccessedFoldCount);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<EpisodicMemory.CharacterMemory>> GetCharacterMemoriesAsync(TrpgScope scope, string characterId, int limit = 12)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, MemoryType, Content, Confidence, CreatedAt, LastAccessed, RelatedEventId, Metadata, IsFoundational, RelatedEntityId, FoldCount, LastAccessedFoldCount
            FROM CharacterMemory
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            ORDER BY IsFoundational DESC, Confidence DESC, CreatedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<EpisodicMemory.CharacterMemory>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(MapCharacterMemory(reader));
        }
        return results;
    }

    public async Task<long> CountCharacterMemoriesAsync(TrpgScope scope, string characterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1)
            FROM CharacterMemory
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        var value = await cmd.ExecuteScalarAsync();
        return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
    }

    public async Task<List<EpisodicMemory.CharacterMemory>> GetFoundationalCharacterMemoriesAsync(TrpgScope scope, string characterId, int limit = 25)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, MemoryType, Content, Confidence, CreatedAt, LastAccessed, RelatedEventId, Metadata, IsFoundational, RelatedEntityId, FoldCount, LastAccessedFoldCount
            FROM CharacterMemory
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @characterId AND IsFoundational = 1
            ORDER BY Confidence DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<EpisodicMemory.CharacterMemory>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(MapCharacterMemory(reader));
        }
        return results;
    }

    private static EpisodicMemory.CharacterMemory MapCharacterMemory(System.Data.Common.DbDataReader reader)
    {
        return new EpisodicMemory.CharacterMemory
        {
            Id = reader.GetInt64(0),
            WorldId = reader.GetString(1),
            GroupId = reader.GetInt64(2),
            CharacterId = reader.GetString(3),
            MemoryType = Enum.Parse<EpisodicMemory.MemoryType>(reader.GetString(4)),
            Content = reader.GetString(5),
            Confidence = reader.GetDouble(6),
            CreatedAt = DateTime.Parse(reader.GetString(7)),
            LastAccessed = DateTime.Parse(reader.GetString(8)),
            RelatedEventId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(10)) ?? new(),
            IsFoundational = !reader.IsDBNull(11) && reader.GetInt32(11) == 1,
            RelatedEntityId = reader.IsDBNull(12) ? null : reader.GetString(12),
            FoldCount = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
            LastAccessedFoldCount = reader.IsDBNull(14) ? 0 : reader.GetInt32(14)
        };
    }

    public async Task UpdateCharacterMemoryLastAccessedAsync(TrpgScope scope, long memoryId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE CharacterMemory SET LastAccessed = @lastAccessed WHERE WorldId = @worldId AND Id = @id";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@lastAccessed", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", memoryId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteCharacterMemoryAsync(TrpgScope scope, long memoryId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM CharacterMemory WHERE WorldId = @worldId AND Id = @id";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", memoryId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateCharacterMemoryConfidenceAsync(TrpgScope scope, long memoryId, double confidence)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE CharacterMemory SET Confidence = @confidence WHERE WorldId = @worldId AND Id = @id";
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@confidence", confidence);
        cmd.Parameters.AddWithValue("@id", memoryId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<EpisodicMemory.CharacterMemory?> GetCharacterMemoryByIdAsync(TrpgScope scope, long memoryId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, MemoryType, Content, Confidence, CreatedAt, LastAccessed, RelatedEventId, Metadata, IsFoundational, RelatedEntityId
            , FoldCount, LastAccessedFoldCount
            FROM CharacterMemory
            WHERE WorldId = @worldId AND Id = @id
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", memoryId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapCharacterMemory(reader);
        return null;
    }

    public async Task UpdateCharacterMemoryAsync(TrpgScope scope, EpisodicMemory.CharacterMemory memory)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE CharacterMemory
            SET Confidence = @confidence, LastAccessed = @lastAccessed, Metadata = @metadata,
                FoldCount = @foldCount, LastAccessedFoldCount = @lastAccessedFoldCount
            WHERE WorldId = @worldId AND Id = @id
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@confidence", memory.Confidence);
        cmd.Parameters.AddWithValue("@lastAccessed", memory.LastAccessed.ToString("o"));
        cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(memory.Metadata));
        cmd.Parameters.AddWithValue("@foldCount", memory.FoldCount);
        cmd.Parameters.AddWithValue("@lastAccessedFoldCount", memory.LastAccessedFoldCount);
        cmd.Parameters.AddWithValue("@id", memory.Id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static EpisodicMemory.MemoryType NormalizeCharacterMemoryType(string memoryType)
    {
        if (string.IsNullOrWhiteSpace(memoryType))
            return EpisodicMemory.MemoryType.Semantic;

        var normalized = memoryType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "event" => EpisodicMemory.MemoryType.Episodic,
            "fact" => EpisodicMemory.MemoryType.Semantic,
            "scene" => EpisodicMemory.MemoryType.Semantic,
            "item" => EpisodicMemory.MemoryType.Semantic,
            "threat" => EpisodicMemory.MemoryType.Suspicion,
            "relationship" => EpisodicMemory.MemoryType.Semantic,
            "emotion" => EpisodicMemory.MemoryType.Emotional,
            "other" => EpisodicMemory.MemoryType.Semantic,
            _ => Enum.TryParse<EpisodicMemory.MemoryType>(memoryType, true, out var parsed) ? parsed : EpisodicMemory.MemoryType.Semantic
        };
    }

    // SceneSnapshot 表操作
    public async Task InsertSceneSnapshotAsync(TrpgScope scope, SceneSnapshotExtended snapshot, string characterId = "")
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SceneSnapshot (WorldId, GroupId, CharacterId, SceneId, EnteredAt, PresentEntityIds, SceneGoals, OutstandingThreads, SceneFlags)
            VALUES (@worldId, @groupId, @characterId, @sceneId, @enteredAt, @presentEntityIds, @sceneGoals, @outstandingThreads, @sceneFlags)
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@characterId", characterId ?? "");
        cmd.Parameters.AddWithValue("@sceneId", snapshot.SceneId);
        cmd.Parameters.AddWithValue("@enteredAt", snapshot.EnteredAt.ToString("o"));
        cmd.Parameters.AddWithValue("@presentEntityIds", JsonSerializer.Serialize(snapshot.PresentEntityIds));
        cmd.Parameters.AddWithValue("@sceneGoals", JsonSerializer.Serialize(snapshot.SceneGoals));
        cmd.Parameters.AddWithValue("@outstandingThreads", JsonSerializer.Serialize(snapshot.OutstandingThreads));
        cmd.Parameters.AddWithValue("@sceneFlags", JsonSerializer.Serialize(snapshot.SceneFlags));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<SceneSnapshotExtended?> GetLatestSceneSnapshotAsync(TrpgScope scope, string sceneId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT SceneId, EnteredAt, PresentEntityIds, SceneGoals, OutstandingThreads, SceneFlags
            FROM SceneSnapshot
            WHERE WorldId = @worldId AND SceneId = @sceneId
            ORDER BY EnteredAt DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@sceneId", sceneId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new SceneSnapshotExtended
            {
                SceneId = reader.GetString(0),
                EnteredAt = DateTime.Parse(reader.GetString(1)),
                PresentEntityIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? new(),
                SceneGoals = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? new(),
                OutstandingThreads = JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? new(),
                SceneFlags = JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(5)) ?? new()
            };
        }
        return null;
    }
}

public class ChatHistoryEntry
{
    public int Id { get; set; }
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string MessageType { get; set; } = "";
    public string SpeakerName { get; set; } = "";
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public int TokenCount { get; set; }
    public int IsArchived { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CharacterSheetEntry
{
    public string WorldId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string StaticBackground { get; set; } = "";
    public string DynamicStateJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; }
}

public class LongTermMemoryEntry
{
    public string Keywords { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// MemoryNode - 语义索引节点
/// 
/// 职责：作为语义检索的索引层，而非记忆真相来源
/// 
/// 定位：
/// - Semantic Index：用于语义相似度检索和 MMR 算法
/// - NOT Memory Truth：不作为记忆真相，记忆真相由 EpisodicMemory 提供
/// - Retrieval Layer：属于检索层，与 embedding、MMR、semantic recall 配合
/// 
/// 使用场景：
/// - 语义相似度检索（embedding + 余弦相似度）
/// - MMR（Maximal Marginal Relevance）去重
/// - 关键词索引
/// 
/// 与 EpisodicMemory 的关系：
/// - MemoryNode：语义索引，用于检索（快速、高效）
/// - EpisodicMemory：记忆真相，用于内容（完整、准确）
/// - 检索流程：MemoryNode 索引 → EpisodicMemory 获取完整内容
/// </summary>
public class MemoryNode
{
    public int Id { get; set; }
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Summary { get; set; } = "";
    public string NodeType { get; set; } = "event";
    public double Importance { get; set; }
    public string Tier { get; set; } = "Session";
    public double Heat { get; set; } = 0.5;
    public float[]? Embedding { get; set; }
    public bool Superseded { get; set; }
    public int? SupersededBy { get; set; }
    public DateTime? LastUsed { get; set; }
    public DateTime CreatedAt { get; set; }

    // 新增字段：叙事理解置信度（0~1），仅用于 interpretation 类型节点
    public double Confidence { get; set; } = 1.0;

    // 新增字段：关键原文切片（JSON 数组），用于保留 Boss 战、NPC 死亡等关键事件的原文
    public string RawExcerpt { get; set; } = "[]";

    // 新增字段：折叠次数，用于基于团内时间的 confidence 衰减
    public int FoldCount { get; set; } = 0;

    public string MemoryAudience { get; set; } = "CharacterIC";
    public string? OwnerCharacterId { get; set; }
    public string? SourceScope { get; set; }
    public string SourceMessageIds { get; set; } = "[]";
    public bool IcUsable { get; set; } = true;
    public string Metadata { get; set; } = "{}";
}

public partial class ChatDatabase
{
    // ──────────────────────────── TimelineNodes ────────────────────────────

    private async Task MigrateTimelineNodesTableAsync()
    {
        try
        {
            using var checkCmd = _connection.CreateCommand();
            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='TimelineNodes'";
            var exists = await checkCmd.ExecuteScalarAsync() != null;
            if (!exists)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS TimelineNodes (
                        Id TEXT PRIMARY KEY,
                        GroupId INTEGER NOT NULL,
                        CharacterId TEXT NOT NULL,
                        Layer TEXT NOT NULL,
                        Content TEXT NOT NULL,
                        ParentId TEXT,
                        SceneId TEXT NOT NULL DEFAULT '',
                        Status TEXT NOT NULL DEFAULT 'Visible',
                        Importance INTEGER NOT NULL DEFAULT 5,
                        Foreshadowing INTEGER NOT NULL DEFAULT 0,
                        EventSequence INTEGER NOT NULL DEFAULT 0,
                        CreatedAt TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_timeline_group  ON TimelineNodes(GroupId, CharacterId);
                    CREATE INDEX IF NOT EXISTS idx_timeline_scene  ON TimelineNodes(GroupId, CharacterId, SceneId);
                    CREATE INDEX IF NOT EXISTS idx_timeline_parent ON TimelineNodes(ParentId);
                    CREATE INDEX IF NOT EXISTS idx_timeline_status ON TimelineNodes(GroupId, CharacterId, Layer, Status);
                    """;
                await cmd.ExecuteNonQueryAsync();
                _context.Log(LogLevel.Info, "[AIMod:TRPG] Migrated: Created TimelineNodes table");
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] TimelineNodes migration skipped: {ex.Message}");
        }
    }

    public async Task InsertTimelineNodeAsync(TrpgScope scope, TimelineNode node)
    {
        node.Content = TimelineContentCleaner.Clean(node.Content);
        if (string.IsNullOrWhiteSpace(node.Content))
            return;

        node.WorldId = scope.WorldId;
        node.GroupId = scope.GroupId;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TimelineNodes (Id, WorldId, GroupId, CharacterId, Layer, Content, ParentId, SceneId, Status, Importance, Foreshadowing, EventSequence, CreatedAt)
            VALUES (@id, @worldId, @groupId, @charId, @layer, @content, @parentId, @sceneId, @status, @imp, @fore, @seq, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@id",        node.Id);
        cmd.Parameters.AddWithValue("@worldId",   node.WorldId);
        cmd.Parameters.AddWithValue("@groupId",   node.GroupId);
        cmd.Parameters.AddWithValue("@charId",    node.CharacterId);
        cmd.Parameters.AddWithValue("@layer",     node.Layer.ToString());
        cmd.Parameters.AddWithValue("@content",   node.Content);
        cmd.Parameters.AddWithValue("@parentId",  node.ParentId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sceneId",   node.SceneId);
        cmd.Parameters.AddWithValue("@status",    node.Status.ToString());
        cmd.Parameters.AddWithValue("@imp",       node.Importance);
        cmd.Parameters.AddWithValue("@fore",      node.Foreshadowing ? 1 : 0);
        cmd.Parameters.AddWithValue("@seq",       node.EventSequence);
        cmd.Parameters.AddWithValue("@createdAt", node.CreatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<TimelineNode>> GetVisibleTimelineNodesAsync(TrpgScope scope, string characterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, Layer, Content, ParentId, SceneId, Status, Importance, Foreshadowing, EventSequence, CreatedAt
            FROM TimelineNodes
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @charId AND Status = 'Visible'
            ORDER BY EventSequence ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@charId",  characterId);
        return await ReadTimelineNodesAsync(cmd);
    }

    public async Task<List<TimelineNode>> GetTimelineNodesByLayerAsync(TrpgScope scope, string characterId, TimelineLayer layer, TimelineNodeStatus status = TimelineNodeStatus.Visible)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, Layer, Content, ParentId, SceneId, Status, Importance, Foreshadowing, EventSequence, CreatedAt
            FROM TimelineNodes
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @charId AND Layer = @layer AND Status = @status
            ORDER BY EventSequence ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@charId",  characterId);
        cmd.Parameters.AddWithValue("@layer",   layer.ToString());
        cmd.Parameters.AddWithValue("@status",  status.ToString());
        return await ReadTimelineNodesAsync(cmd);
    }

    public async Task<List<TimelineNode>> GetTimelineNodesBySceneAsync(TrpgScope scope, string characterId, string sceneId, TimelineLayer? layer = null)
    {
        using var cmd = _connection.CreateCommand();
        var layerFilter = layer.HasValue ? "AND Layer = @layer" : "";
        cmd.CommandText = $"""
            SELECT Id, WorldId, GroupId, CharacterId, Layer, Content, ParentId, SceneId, Status, Importance, Foreshadowing, EventSequence, CreatedAt
            FROM TimelineNodes
            WHERE WorldId = @worldId AND GroupId = @groupId AND CharacterId = @charId AND SceneId = @sceneId {layerFilter}
            ORDER BY EventSequence ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@groupId", scope.GroupId);
        cmd.Parameters.AddWithValue("@charId",  characterId);
        cmd.Parameters.AddWithValue("@sceneId", sceneId);
        if (layer.HasValue) cmd.Parameters.AddWithValue("@layer", layer.Value.ToString());
        return await ReadTimelineNodesAsync(cmd);
    }

    public async Task<List<TimelineNode>> GetTimelineChildNodesAsync(TrpgScope scope, string parentId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, Layer, Content, ParentId, SceneId, Status, Importance, Foreshadowing, EventSequence, CreatedAt
            FROM TimelineNodes WHERE WorldId = @worldId AND ParentId = @parentId ORDER BY EventSequence ASC
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@parentId", parentId);
        return await ReadTimelineNodesAsync(cmd);
    }

    public async Task<TimelineNode?> GetTimelineNodeByIdAsync(TrpgScope scope, string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, WorldId, GroupId, CharacterId, Layer, Content, ParentId, SceneId, Status, Importance, Foreshadowing, EventSequence, CreatedAt
            FROM TimelineNodes WHERE WorldId = @worldId AND Id = @id
            """;
        cmd.Parameters.AddWithValue("@worldId", scope.WorldId);
        cmd.Parameters.AddWithValue("@id", id);
        var list = await ReadTimelineNodesAsync(cmd);
        return list.Count > 0 ? list[0] : null;
    }

    public async Task<int> CountTimelineNodesByLayerAsync(TrpgScope scope, string characterId, TimelineLayer layer, TimelineNodeStatus status)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM TimelineNodes WHERE WorldId=@w AND GroupId=@g AND CharacterId=@c AND Layer=@l AND Status=@s";
        cmd.Parameters.AddWithValue("@w", scope.WorldId);
        cmd.Parameters.AddWithValue("@g", scope.GroupId);
        cmd.Parameters.AddWithValue("@c", characterId);
        cmd.Parameters.AddWithValue("@l", layer.ToString());
        cmd.Parameters.AddWithValue("@s", status.ToString());
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<int> GetNextEventSequenceAsync(TrpgScope scope, string characterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(EventSequence),0)+1 FROM TimelineNodes WHERE WorldId=@w AND GroupId=@g AND CharacterId=@c";
        cmd.Parameters.AddWithValue("@w", scope.WorldId);
        cmd.Parameters.AddWithValue("@g", scope.GroupId);
        cmd.Parameters.AddWithValue("@c", characterId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task UpdateTimelineNodeStatusAsync(TrpgScope scope, string id, TimelineNodeStatus status)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE TimelineNodes SET Status=@s WHERE WorldId=@w AND Id=@id";
        cmd.Parameters.AddWithValue("@w", scope.WorldId);
        cmd.Parameters.AddWithValue("@s",  status.ToString());
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateTimelineNodeContentAsync(TrpgScope scope, string id, string content)
    {
        var cleanedContent = TimelineContentCleaner.Clean(content);
        if (string.IsNullOrWhiteSpace(cleanedContent))
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE TimelineNodes SET Content=@c WHERE WorldId=@w AND Id=@id";
        cmd.Parameters.AddWithValue("@w", scope.WorldId);
        cmd.Parameters.AddWithValue("@c",  cleanedContent);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task BulkUpdateTimelineNodeStatusAsync(TrpgScope scope, IEnumerable<string> ids, TimelineNodeStatus status)
    {
        foreach (var id in ids)
            await UpdateTimelineNodeStatusAsync(scope, id, status);
    }

    private async Task<List<TimelineNode>> ReadTimelineNodesAsync(SQLiteCommand cmd)
    {
        var list = new List<TimelineNode>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new TimelineNode
            {
                Id            = reader.GetString(0),
                WorldId       = reader.GetString(1),
                GroupId       = reader.GetInt64(2),
                CharacterId   = reader.GetString(3),
                Layer         = Enum.Parse<TimelineLayer>(reader.GetString(4)),
                Content       = reader.GetString(5),
                ParentId      = reader.IsDBNull(6) ? null : reader.GetString(6),
                SceneId       = reader.GetString(7),
                Status        = Enum.Parse<TimelineNodeStatus>(reader.GetString(8)),
                Importance    = reader.GetInt32(9),
                Foreshadowing = reader.GetInt32(10) == 1,
                EventSequence = reader.GetInt32(11),
                CreatedAt     = DateTime.Parse(reader.GetString(12))
            });
        }
        return list;
    }
}

public class RecallRequest
{
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class AiCharacterEntry
{
    public string WorldId { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public long VirtualId { get; set; }
    public long OwnerUserId { get; set; }
    public long GroupId { get; set; }
    public string TeamName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string StaticBackground { get; set; } = "";
    public string DynamicStateJson { get; set; } = "{}";
    public string SkillsJson { get; set; } = "{}";
    public string InventoryJson { get; set; } = "[]";
    public string InitialInventoryJson
    {
        get => InventoryJson;
        set => InventoryJson = value;
    }
    public string RuleText { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
