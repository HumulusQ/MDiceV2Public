using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Episodic Memory - 角色情景记忆
/// 
/// 职责：维护角色的情景记忆，区分世界真实状态与角色认知
/// 
/// 核心思想：
/// - 世界真实状态 ≠ 角色认知
/// - AI 只能基于角色认知行动
/// - 角色只能知道它经历过的事件或被告知的信息
/// 
/// 记忆类型：
/// - Episodic Memory: 经历过的事件
/// - Semantic Memory: 稳定认知
/// - Suspicion Memory: 怀疑
/// - Emotional Memory: 情绪偏向
/// - Rumor / False Memory: 错误认知
/// </summary>
public class EpisodicMemory
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly bool _enableAffectiveMemoryEncoding;

    public EpisodicMemory(IModContext context, ChatDatabase db, EventLog eventLog, bool enableAffectiveMemoryEncoding = true)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _enableAffectiveMemoryEncoding = enableAffectiveMemoryEncoding;
    }

    /// <summary>
    /// 记忆类型
    /// </summary>
    public enum MemoryType
    {
        Episodic,        // 情景记忆
        Semantic,        // 语义记忆
        Suspicion,       // 怀疑
        Emotional,       // 情绪
        Rumor,           // 谣言
        FalseBelief,     // 错误认知
        WorldFact,       // 世界事实（永久）
        CharacterBelief, // 角色认知（永久）
        Objective,       // 目标（永久）
        Suspense         // 悬念（永久）
    }

    /// <summary>
    /// 角色记忆
    /// </summary>
    public class CharacterMemory
    {
        public long Id { get; set; }
        public string WorldId { get; set; } = "";
        public long GroupId { get; set; }
        public string CharacterId { get; set; } = "";
        public MemoryType MemoryType { get; set; }
        public string Content { get; set; } = "";
        public double Confidence { get; set; } = 1.0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
        public long? RelatedEventId { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public bool IsFoundational { get; set; } = false; // true = 永不遗忘
        public string? RelatedEntityId { get; set; }       // CharacterBelief 关联角色
        public int FoldCount { get; set; } = 0;  // 创建时的折叠计数
        public int LastAccessedFoldCount { get; set; } = 0;  // 最后访问时的折叠计数

        // 重新激活相关数据（存储在Metadata中）
        public int ReactivationCount
        {
            get => Metadata.TryGetValue("reactivation_count", out var rc) ? Convert.ToInt32(rc) : 0;
            set => Metadata["reactivation_count"] = value;
        }
        public double ReactivationBoost
        {
            get => Metadata.TryGetValue("reactivation_boost", out var rb) ? Convert.ToDouble(rb) : 0.0;
            set => Metadata["reactivation_boost"] = value;
        }
    }

    /// <summary>
    /// 添加角色记忆
    /// </summary>
    public async Task AddMemoryAsync(
        TrpgScope scope,
        string characterId,
        MemoryType memoryType,
        string content,
        double confidence = 1.0,
        long? relatedEventId = null,
        Dictionary<string, object>? metadata = null,
        string? relatedEntityId = null)
    {
        // 获取当前FoldCount
        int currentFoldCount = 0;
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count > 0)
        {
            currentFoldCount = memories[0].FoldCount;
        }

        var memory = new CharacterMemory
        {
            WorldId = scope.WorldId,
            GroupId = scope.GroupId,
            CharacterId = characterId,
            MemoryType = memoryType,
            Content = content,
            Confidence = confidence,
            RelatedEventId = relatedEventId,
            Metadata = metadata ?? new Dictionary<string, object>(),
            RelatedEntityId = relatedEntityId,
            FoldCount = currentFoldCount,
            LastAccessedFoldCount = currentFoldCount
        };

        _context.Log(LogLevel.Debug, $"[AIMod:TRPG] 创建情景记忆 | Type={memoryType} | CharacterId={characterId} | Content={content.Substring(0, Math.Min(60, content.Length))} | Confidence={confidence:F2} | RelatedEventId={relatedEventId}");

        await _db.InsertCharacterMemoryAsync(scope, memory);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] 情景记忆已保存 [{memoryType}] - {characterId}: {content} (FoldCount={currentFoldCount})");
    }

    /// <summary>
    /// 获取角色的所有记忆
    /// </summary>
    public async Task<List<CharacterMemory>> GetMemoriesAsync(TrpgScope scope, string characterId)
    {
        return await _db.GetCharacterMemoriesAsync(scope, characterId, limit: 200);
    }

    /// <summary>
    /// 获取永久基础记忆（IsFoundational=true），按置信度降序
    /// </summary>
    public async Task<List<CharacterMemory>> GetFoundationalMemoriesAsync(TrpgScope scope, string characterId, int limit = 25)
    {
        return await _db.GetFoundationalCharacterMemoriesAsync(scope, characterId, limit);
    }

    /// <summary>
    /// 从事件中提取基础叙事信息，直接写入 EpisodicMemory（IsFoundational=true）
    /// </summary>
    public async Task DigestEventAsFoundationalAsync(TrpgScope scope, string characterId, WorldEvent evt)
    {
        switch (evt.EventType.ToLower())
        {
            case "discovery":
                if (evt.Payload.TryGetValue("discovery", out var disc))
                    await AddFoundationalAsync(scope, characterId, MemoryType.WorldFact, $"发现: {disc}", evt.EventId);
                break;
            case "item_acquisition":
                if (evt.Payload.TryGetValue("item_id", out var item))
                    await AddFoundationalAsync(scope, characterId, MemoryType.WorldFact, $"物品存在: {item}", evt.EventId);
                break;
            case "npc_death":
                if (evt.SourceEntityId != null)
                    await AddFoundationalAsync(scope, characterId, MemoryType.WorldFact, $"角色死亡: {evt.SourceEntityId}", evt.EventId);
                break;
            case "scene_transition":
                if (evt.Payload.TryGetValue("scene_id", out var sceneId))
                    await AddFoundationalAsync(scope, characterId, MemoryType.WorldFact, $"场景存在: {sceneId}", evt.EventId);
                break;
        }

        string? belief = evt.EventType.ToLower() switch
        {
            "discovery"   => $"相信: {evt.Result}",
            "dialogue"    => $"得知: {evt.Result}",
            "observation" => $"观察到: {evt.Result}",
            _ => null
        };
        if (belief != null)
        {
            foreach (var entityId in new[] { evt.SourceEntityId, evt.TargetEntityId }
                .Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                await AddFoundationalAsync(scope, characterId, MemoryType.CharacterBelief, belief,
                    evt.EventId, relatedEntityId: entityId);
            }
        }

        if (evt.EventType.ToLower() == "objective_change" &&
            evt.Payload.TryGetValue("objective", out var obj) && obj is string objStr)
        {
            await AddFoundationalAsync(scope, characterId, MemoryType.Objective, objStr, evt.EventId);
        }

        var suspenseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "discovery", "core_secret_reveal", "dialogue" };
        if (suspenseTypes.Contains(evt.EventType) && !string.IsNullOrWhiteSpace(evt.Result))
            await AddFoundationalAsync(scope, characterId, MemoryType.Suspense, evt.Result, evt.EventId);
    }

    private async Task AddFoundationalAsync(TrpgScope scope, string characterId, MemoryType memoryType,
        string content, long? relatedEventId = null, string? relatedEntityId = null)
    {
        var memory = new CharacterMemory
        {
            WorldId = scope.WorldId,
            GroupId = scope.GroupId,
            CharacterId = characterId,
            MemoryType = memoryType,
            Content = content,
            Confidence = 1.0,
            RelatedEventId = relatedEventId,
            RelatedEntityId = relatedEntityId,
            IsFoundational = true
        };
        await _db.InsertCharacterMemoryAsync(scope, memory);
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] EpisodicMemory: 添加基础记忆 [{memoryType}] {content}");
    }

    /// <summary>
    /// 将永久基础记忆格式化为 Prompt 字符串
    /// </summary>
    public string FormatFoundationalMemories(List<CharacterMemory> memories)
    {
        if (memories.Count == 0) return "无";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【故事骨架层（Foundational Canon）】");
        sb.AppendLine("========================");
        sb.AppendLine("（系统硬约束）这是永不蒸发的故事骨架，构成故事的基础结构。");

        foreach (var grp in memories.GroupBy(m => m.MemoryType))
        {
            sb.AppendLine($"\n[{FoundationalLabel(grp.Key)}]");
            foreach (var m in grp)
            {
                var entity = m.RelatedEntityId != null ? $" ({m.RelatedEntityId})" : "";
                sb.AppendLine($"  • {m.Content}{entity}");
            }
        }
        return sb.ToString();
    }

    private static string FoundationalLabel(MemoryType t) => t switch
    {
        MemoryType.WorldFact       => "世界事实",
        MemoryType.CharacterBelief => "角色认知",
        MemoryType.Objective       => "目标",
        MemoryType.Suspense        => "悬念",
        _ => t.ToString()
    };

    /// <summary>
    /// 获取角色的指定类型记忆
    /// </summary>
    public async Task<List<CharacterMemory>> GetMemoriesByTypeAsync(TrpgScope scope, string characterId, MemoryType memoryType)
    {
        var allMemories = await GetMemoriesAsync(scope, characterId);
        return allMemories.Where(m => m.MemoryType == memoryType).ToList();
    }

    /// <summary>
    /// 重新激活记忆
    /// 当NPC再次出现、地点重返、关键词出现、旧事件被提及、情绪被触发时调用
    /// </summary>
    public async Task ReactivateMemoryAsync(TrpgScope scope, long memoryId, double boostAmount = 0.2)
    {
        var memory = await _db.GetCharacterMemoryByIdAsync(scope, memoryId);
        if (memory == null) return;

        // 获取当前FoldCount
        var memories = await _db.GetAllMemoryNodesAsync(scope, memory.CharacterId, limit: 1);
        if (memories.Count > 0)
        {
            memory.LastAccessedFoldCount = memories[0].FoldCount;
        }

        // 增加重新激活次数和增益
        memory.ReactivationCount++;
        memory.ReactivationBoost = Math.Min(2.0, memory.ReactivationBoost + boostAmount);  // 最大增益为2.0

        // 提高置信度（受重新激活增益影响）
        memory.Confidence = Math.Min(1.0, memory.Confidence + (boostAmount * 0.5));

        await _db.UpdateCharacterMemoryAsync(scope, memory);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] EpisodicMemory: 重新激活记忆 ID={memoryId} (ReactivationCount={memory.ReactivationCount}, Boost={memory.ReactivationBoost:F2})");
    }

    /// <summary>
    /// 根据关键词重新激活相关记忆
    /// </summary>
    public async Task ReactivateMemoriesByKeywordsAsync(TrpgScope scope, string characterId, List<string> keywords)
    {
        var allMemories = await GetMemoriesAsync(scope, characterId);
        var reactivatedCount = 0;

        foreach (var memory in allMemories)
        {
            // 检查记忆内容是否包含关键词
            var contentLower = memory.Content.ToLower();
            var hasKeyword = keywords.Any(kw => contentLower.Contains(kw.ToLower()));

            if (hasKeyword)
            {
                await ReactivateMemoryAsync(scope, memory.Id, 0.15);
                reactivatedCount++;
            }
        }

        if (reactivatedCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] EpisodicMemory: 通过关键词重新激活了 {reactivatedCount} 条记忆");
        }
    }

    /// <summary>
    /// 根据实体ID重新激活相关记忆
    /// </summary>
    public async Task ReactivateMemoriesByEntityAsync(TrpgScope scope, string characterId, string entityId)
    {
        var allMemories = await GetMemoriesAsync(scope, characterId);
        var reactivatedCount = 0;

        foreach (var memory in allMemories)
        {
            // 检查记忆内容是否包含实体ID
            if (memory.Content.Contains(entityId) || memory.Metadata.ContainsKey("entity_id"))
            {
                await ReactivateMemoryAsync(scope, memory.Id, 0.2);
                reactivatedCount++;
            }
        }

        if (reactivatedCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] EpisodicMemory: 通过实体重新激活了 {reactivatedCount} 条记忆 (Entity={entityId})");
        }
    }

    /// <summary>
    /// 根据事件ID重新激活相关记忆
    /// </summary>
    public async Task ReactivateMemoriesByEventAsync(TrpgScope scope, string characterId, long eventId)
    {
        var allMemories = await GetMemoriesAsync(scope, characterId);
        var reactivatedCount = 0;

        foreach (var memory in allMemories)
        {
            // 检查记忆是否与该事件相关
            if (memory.RelatedEventId == eventId)
            {
                await ReactivateMemoryAsync(scope, memory.Id, 0.25);
                reactivatedCount++;
            }
        }

        if (reactivatedCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] EpisodicMemory: 通过事件重新激活了 {reactivatedCount} 条记忆 (EventId={eventId})");
        }
    }

    /// <summary>
    /// 从事件自动生成情景记忆
    /// </summary>
    public async Task AutoGenerateEpisodicMemoryAsync(TrpgScope scope, string characterId, WorldEvent evt)
    {
        // 只为在场实体生成记忆
        if (string.IsNullOrWhiteSpace(evt.SourceEntityId) && string.IsNullOrWhiteSpace(evt.TargetEntityId))
            return;

        var entities = new List<string>();
        if (!string.IsNullOrWhiteSpace(evt.SourceEntityId))
            entities.Add(evt.SourceEntityId);
        if (!string.IsNullOrWhiteSpace(evt.TargetEntityId))
            entities.Add(evt.TargetEntityId);

        foreach (var entityId in entities)
        {
            // 生成情景记忆
            var activeTags = _enableAffectiveMemoryEncoding
                ? await _db.GetActiveAffectiveTagStatesAsync(scope, entityId, 6)
                : new List<AffectiveTagState>();

            var memoryContent = activeTags.Count == 0
                ? GenerateMemoryContent(evt)
                : GenerateAffectiveMemoryContent(evt, activeTags);
            var memoryType = activeTags.Count == 0
                ? MemoryType.Episodic
                : ChooseAffectiveMemoryType(evt, activeTags);
            var metadata = activeTags.Count == 0
                ? null
                : BuildAffectiveEncodingMetadata(evt, activeTags);

            await AddMemoryAsync(scope, entityId, memoryType, memoryContent, 0.9, evt.EventId, metadata);

            // 根据事件类型生成其他类型记忆
            if (evt.EventType == "discovery")
            {
                await AddMemoryAsync(scope, entityId, MemoryType.Semantic, $"发现: {evt.Result}", 0.8, evt.EventId);
            }

            if (evt.EventType == "relationship_change")
            {
                await AddMemoryAsync(scope, entityId, MemoryType.Emotional, $"关系变化: {evt.Result}", 0.7, evt.EventId);
            }
        }
    }

    /// <summary>
    /// 生成记忆内容
    /// </summary>
    private string GenerateMemoryContent(WorldEvent evt)
    {
        return $"[{evt.Timestamp:MM-dd HH:mm}] {evt.EventType}: {evt.Result}";
    }

    private string GenerateAffectiveMemoryContent(WorldEvent evt, List<AffectiveTagState> activeTags)
    {
        var baseContent = GenerateMemoryContent(evt);
        var frame = DescribeAffectiveFrame(activeTags);
        return string.IsNullOrWhiteSpace(frame) ? baseContent : $"{baseContent} | 情感着色：{frame}";
    }

    private MemoryType ChooseAffectiveMemoryType(WorldEvent evt, List<AffectiveTagState> activeTags)
    {
        if (activeTags.Any(t => t.TagType.StartsWith("Trust.", StringComparison.OrdinalIgnoreCase) ||
                                t.TagType.StartsWith("Suspicion.", StringComparison.OrdinalIgnoreCase)))
            return MemoryType.Suspicion;

        if (string.Equals(evt.EventType, "relationship_change", StringComparison.OrdinalIgnoreCase) ||
            activeTags.Any(t => IsEmotionalTag(t.TagType)))
            return MemoryType.Emotional;

        return MemoryType.Episodic;
    }

    private static bool IsEmotionalTag(string tagType)
    {
        return tagType.StartsWith("Fear.", StringComparison.OrdinalIgnoreCase) ||
               tagType.StartsWith("Anger.", StringComparison.OrdinalIgnoreCase) ||
               tagType.StartsWith("Sadness.", StringComparison.OrdinalIgnoreCase) ||
               tagType.StartsWith("Shame.", StringComparison.OrdinalIgnoreCase) ||
               tagType.StartsWith("Affection.", StringComparison.OrdinalIgnoreCase) ||
               tagType.StartsWith("Stress.", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tagType, "NeedForReassurance", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tagType, "CombatReadiness", StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, object> BuildAffectiveEncodingMetadata(WorldEvent evt, List<AffectiveTagState> activeTags)
    {
        var dominant = activeTags
            .OrderByDescending(t => t.Charge)
            .ThenByDescending(t => t.UpdatedAt)
            .FirstOrDefault();

        var emotionTags = activeTags
            .OrderByDescending(t => t.Charge)
            .Take(6)
            .Select(t => new Dictionary<string, object?>
            {
                ["tag_type"] = t.TagType,
                ["display_name"] = t.DisplayName,
                ["source_key"] = t.SourceKey,
                ["target_entity_id"] = t.TargetEntityId,
                ["intensity_tier"] = t.IntensityTier,
                ["charge"] = t.Charge,
                ["status"] = t.Status,
                ["last_evidence"] = t.LastEvidence
            })
            .ToList();

        var encoding = new Dictionary<string, object?>
        {
            ["schema"] = "affective_memory_encoding.v1",
            ["objective_anchor"] = GetObjectiveAnchor(evt),
            ["subjective_framing"] = DescribeAffectiveFrame(activeTags),
            ["dominant_affect"] = dominant?.DisplayName ?? "",
            ["dominant_tag_type"] = dominant?.TagType ?? "",
            ["emotional_charge"] = dominant?.IntensityTier ?? "None",
            ["retention_modifier"] = dominant != null && dominant.Charge >= 0.5 ? "SlowDecay" : "Normal",
            ["expression_bias"] = dominant == null ? "" : $"受{dominant.DisplayName}影响语气，但不虚构事实。",
            ["emotion_tags"] = emotionTags,
            ["reactivation_count"] = 0,
            ["last_reactivated_by_event_id"] = null
        };

        return new Dictionary<string, object>
        {
            ["encoding"] = encoding
        };
    }

    private static string DescribeAffectiveFrame(List<AffectiveTagState> activeTags)
    {
        var parts = activeTags
            .Where(t => !string.IsNullOrWhiteSpace(t.DisplayName))
            .OrderByDescending(t => t.Charge)
            .Take(3)
            .Select(t =>
            {
                var target = string.IsNullOrWhiteSpace(t.TargetEntityId) ? "" : $"，对象={t.TargetEntityId}";
                return $"{t.DisplayName}（{t.IntensityTier}{target}，来源={t.SourceKey}）";
            })
            .ToList();

        return string.Join("; ", parts);
    }

    private static string GetObjectiveAnchor(WorldEvent evt)
    {
        var result = string.IsNullOrWhiteSpace(evt.Result) ? "(no result text)" : evt.Result.Trim();
        return $"{evt.EventType}: {result}";
    }

    /// <summary>
    /// 获取角色认知的世界状态
    /// 基于角色的记忆重建其对世界的认知
    /// </summary>
    public async Task<CharacterWorldView> GetCharacterWorldViewAsync(TrpgScope scope, string characterId)
    {
        var memories = await GetMemoriesAsync(scope, characterId);
        
        var worldView = new CharacterWorldView
        {
            CharacterId = characterId,
            KnownEntities = new HashSet<string>(),
            KnownLocations = new HashSet<string>(),
            KnownEvents = new List<long>(),
            Suspicions = new List<string>(),
            EmotionalStates = new Dictionary<string, double>()
        };

        foreach (var memory in memories)
        {
            // 更新最后访问时间
            memory.LastAccessed = DateTime.UtcNow;
            await _db.UpdateCharacterMemoryLastAccessedAsync(scope, memory.Id);

            switch (memory.MemoryType)
            {
                case MemoryType.Episodic:
                    if (memory.RelatedEventId.HasValue)
                        worldView.KnownEvents.Add(memory.RelatedEventId.Value);
                    break;

                case MemoryType.Semantic:
                    // 从语义记忆中提取实体和位置
                    ExtractEntitiesAndLocations(memory.Content, worldView);
                    break;

                case MemoryType.Suspicion:
                    worldView.Suspicions.Add(memory.Content);
                    break;

                case MemoryType.Emotional:
                    // 从情绪记忆中提取情绪状态
                    ExtractEmotionalState(memory.Content, worldView);
                    break;
            }
        }

        return worldView;
    }

    /// <summary>
    /// 从记忆内容中提取实体和位置
    /// </summary>
    private void ExtractEntitiesAndLocations(string content, CharacterWorldView worldView)
    {
        // 简化处理：实际应该使用 NER
        if (content.Contains("发现") || content.Contains("遇到"))
        {
            // 假设内容中包含实体名称
            var parts = content.Split(new[] { ' ', ':', '，', '。' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Length > 1 && part.Length < 20)
                {
                    worldView.KnownEntities.Add(part);
                }
            }
        }
    }

    /// <summary>
    /// 从记忆内容中提取情绪状态
    /// </summary>
    private void ExtractEmotionalState(string content, CharacterWorldView worldView)
    {
        if (content.Contains("信任") || content.Contains("友好"))
        {
            worldView.EmotionalStates["trust"] = 0.8;
        }
        else if (content.Contains("怀疑") || content.Contains("敌意"))
        {
            worldView.EmotionalStates["trust"] = -0.5;
        }

        if (content.Contains("恐惧"))
        {
            worldView.EmotionalStates["fear"] = 0.7;
        }

        if (content.Contains("愤怒"))
        {
            worldView.EmotionalStates["anger"] = 0.8;
        }
    }

    /// <summary>
    /// 验证角色是否应该知道某信息
    /// </summary>
    public async Task<bool> ShouldKnowAsync(TrpgScope scope, string characterId, string information)
    {
        var worldView = await GetCharacterWorldViewAsync(scope, characterId);
        
        // 检查记忆中是否包含该信息
        var memories = await GetMemoriesAsync(scope, characterId);
        return memories.Any(m => m.Content.Contains(information, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 生成用于 Prompt 的记忆字符串
    /// </summary>
    public string ToPromptString(TrpgScope scope, string characterId, int maxMemories = 10)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【角色记忆】");
        sb.AppendLine("========================");

        var memories = GetMemoriesAsync(scope, characterId).Result
            .OrderByDescending(m => m.LastAccessed)
            .Take(maxMemories)
            .ToList();

        if (memories.Count == 0)
        {
            sb.AppendLine("无记忆记录");
            return sb.ToString();
        }

        foreach (var memory in memories)
        {
            sb.AppendLine($"[{memory.MemoryType}] {memory.Content} (置信度: {memory.Confidence})");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 遗忘机制（基于FoldCount）
    /// 根据记忆的折叠次数和访问频率衰减置信度
    /// </summary>
    public async Task ApplyForgettingAsync(TrpgScope scope, string characterId)
    {
        // 获取当前FoldCount
        var allMemories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (allMemories.Count == 0) return;
        var currentFoldCount = allMemories[0].FoldCount;

        var memories = await GetMemoriesAsync(scope, characterId);
        var forgottenCount = 0;

        foreach (var memory in memories)
        {
            if (memory.IsFoundational) continue;

            var foldsSinceCreated = currentFoldCount - memory.FoldCount;
            var foldsSinceAccess = currentFoldCount - memory.LastAccessedFoldCount;

            // 计算遗忘因子（包含重新激活增益）
            var forgettingFactor = CalculateForgettingFactor(memory.MemoryType, foldsSinceCreated, foldsSinceAccess, memory.ReactivationBoost);

            if (forgettingFactor < 0.1)
            {
                // 置信度过低，删除记忆
                await _db.DeleteCharacterMemoryAsync(scope, memory.Id);
                forgottenCount++;
            }
            else if (forgettingFactor < 1.0)
            {
                // 衰减置信度
                memory.Confidence *= forgettingFactor;
                await _db.UpdateCharacterMemoryConfidenceAsync(scope, memory.Id, memory.Confidence);
                forgottenCount++;
            }
        }

        if (forgottenCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] EpisodicMemory: 遗忘完成，处理了 {forgottenCount} 条记忆 (FoldCount={currentFoldCount})");
        }
    }

    /// <summary>
    /// 计算遗忘因子（基于FoldCount）
    /// </summary>
    private double CalculateForgettingFactor(MemoryType memoryType, int foldsSinceCreated, int foldsSinceAccess, double reactivationBoost)
    {
        // 不同类型的记忆有不同的遗忘速率（以折叠次数为单位，1天=40次折叠）
        // 错误认知往往更稳定、更情绪化、更 resistant，所以半衰期更长
        var halfLife = memoryType switch
        {
            MemoryType.Episodic => 280,      // 情景记忆中等：7天=280次折叠
            MemoryType.Semantic => 1200,      // 语义记忆慢：30天=1200次折叠
            MemoryType.Suspicion => 60,     // 怀疑：1.5天=60次折叠（比真实记忆更顽固）
            MemoryType.Emotional => 560,     // 情绪记忆较慢：14天=560次折叠
            MemoryType.Rumor => 40,          // 谣言：1天=40次折叠（比真实记忆更顽固）
            MemoryType.FalseBelief => 80,     // 错误认知：2天=80次折叠（比真实记忆更顽固）
            _ => 280
        };

        // 考虑最后访问时间：如果最近访问过，使用访问时间
        var effectiveFolds = foldsSinceAccess > 5 ? foldsSinceAccess : foldsSinceCreated;

        // 重新激活增益：记忆被重新激活后，衰减速度减慢
        var adjustedHalfLife = halfLife * (1.0 + reactivationBoost);

        // 指数衰减
        var forgettingFactor = Math.Pow(0.5, effectiveFolds / adjustedHalfLife);
        return forgettingFactor;
    }
}

/// <summary>
/// 角色世界观
/// </summary>
public class CharacterWorldView
{
    /// <summary>
    /// 角色ID
    /// </summary>
    public string CharacterId { get; set; } = "";

    /// <summary>
    /// 已知实体
    /// </summary>
    public HashSet<string> KnownEntities { get; set; } = new();

    /// <summary>
    /// 已知位置
    /// </summary>
    public HashSet<string> KnownLocations { get; set; } = new();

    /// <summary>
    /// 已知事件
    /// </summary>
    public List<long> KnownEvents { get; set; } = new();

    /// <summary>
    /// 怀疑列表
    /// </summary>
    public List<string> Suspicions { get; set; } = new();

    /// <summary>
    /// 情绪状态
    /// </summary>
    public Dictionary<string, double> EmotionalStates { get; set; } = new();
}
