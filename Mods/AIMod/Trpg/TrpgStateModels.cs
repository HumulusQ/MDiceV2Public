using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AIMod.Trpg;

/// <summary>
/// 消息现实层类型：只有 IC 类型允许触发 NPC/场景状态更新
/// </summary>
public enum MessageType
{
    IC,      // 场内内容 - 允许触发状态更新
    OOC,     // 场外聊天 - 禁止触发状态更新
    META,    // 规则讨论 - 禁止触发状态更新
    SYSTEM   // 系统消息 - 禁止触发状态更新
}

/// <summary>
/// 注意力标记：主模型观察到的轻量标记，不直接更新长期状态
/// </summary>
public class AttentionMarker
{
    public string Type { get; set; } = ""; // "npc_behavior", "scene_change", "world_state", "relationship"
    public string Target { get; set; } = ""; // NPC ID 或场景 ID
    public List<string> Keywords { get; set; } = new();
    public string SceneId { get; set; } = "";
    public double Importance { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TrpgRuntimeState
{
    public string CurrentSceneId { get; set; } = "scene_default";
    public string PreviousSceneId { get; set; } = "scene_default";
    public List<string> PresentEntities { get; set; } = new();
    public string PlayerStatus { get; set; } = "状态未知";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string LatestGmNarrative { get; set; } = "";
    public string LatestSituationSummary { get; set; } = "";
    public List<string> LatestFacts { get; set; } = new();
    public List<string> LatestEvents { get; set; } = new();
    public DateTime? LastExtractionAt { get; set; }

    // 场景状态独立维护
    public SceneState? SceneState { get; set; }

    // 运行时世界状态（扩展版本）
    public RuntimeWorldState? WorldState { get; set; }
}

/// <summary>
/// 场景状态：独立于聊天记录的场景描述
/// </summary>
public class SceneState
{
    public string SceneId { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, object> Properties { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 判断是否应该更新场景状态
    /// 条件：可交互、可影响行动、重复提及、改变世界状态
    /// </summary>
    public bool ShouldUpdate(string newDescription, List<string> presentEntities)
    {
        // 如果描述变化超过 50%，则更新
        if (ComputeSimilarity(Description, newDescription) < 0.5)
            return true;

        // 如果实体列表变化，则更新
        var oldEntities = Properties.GetValueOrDefault("entities", new List<string>()) as List<string> ?? new List<string>();
        if (!oldEntities.SequenceEqual(presentEntities))
            return true;

        return false;
    }

    private static double ComputeSimilarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var aWords = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bWords = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (aWords.Length == 0 || bWords.Length == 0)
            return 0.0;

        var intersection = aWords.Intersect(bWords, StringComparer.OrdinalIgnoreCase).Count();
        var union = aWords.Union(bWords, StringComparer.OrdinalIgnoreCase).Count();

        return (double)intersection / union;
    }
}

public class CharacterHotMetaEntry
{
    public string WorldId { get; set; } = "";
    public string CharId { get; set; } = "";
    public string ShortTags { get; set; } = "";
    public string Aliases { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class TrpgPromptContext
{
    public string CurrentSceneVar { get; set; } = "无";  // 合并后的当前场景（供 AI 注入）
    public string CurrentSceneId { get; set; } = "";  // 当前场景 ID（供后台逻辑使用）
    public string CurrentVisionVar { get; set; } = "无";  // 保留字段（兼容旧逻辑）
    public string RecalledMemoryVar { get; set; } = "无";  // 语义索引（Semantic Index），用于检索
    public string NpcIntegratedMemoryVar { get; set; } = "无";  // NPC 统合记忆
    public List<string> PresentEntityIds { get; set; } = new();
    public List<string> PresentEntityAliases { get; set; } = new();
    public bool ForceExtendedHistory { get; set; }
    public string RecallKeywordsVar { get; set; } = "无";
    public string WorldStateVar { get; set; } = "无";

    // 四层架构字段
    public string ObjectivesVar { get; set; } = "无";  // Objective Layer
    public string EntitiesVar { get; set; } = "无";     // Canonical Entity Layer
    public string EventsVar { get; set; } = "无";       // Immutable Event Log
    public string SceneSnapshotVar { get; set; } = "无"; // Scene Snapshot

    // 新架构字段
    public string TimelineVar { get; set; } = "无";       // Hierarchical Timeline
    public string EpisodicMemoryVar { get; set; } = "无"; // Episodic Memory（记忆真相）
    public string SalienceReportVar { get; set; } = "无"; // Salience Ranking Report
    public string FoundationalCanonVar { get; set; } = "无"; // Foundational Canon（永久世界骨架）
    public string InventoryStateVar { get; set; } = "无";
    public string AffectiveStateVar { get; set; } = "无";
    public string NarrativeMemoryVar { get; set; } = "无"; // Narrative Memory（认知层记忆）
    public string NarrativeContextVar { get; set; } = "无"; // 兼容字段；默认不再由 LLM 编织
    public string StructuredActionContextVar { get; set; } = "无";
    public TrpgAgentContextPack? AgentContextPack { get; set; }
    public int TimelineNodesCount { get; set; }
    public int CharacterICMemoryCount { get; set; }
    public int PlayerTableMemoryCount { get; set; }
    public int RecalledNodesCount { get; set; }
    public int RecentHistoryCount { get; set; }
    public int InventoryItemsCount { get; set; }
    public int ActionContextChars { get; set; }
}

/// <summary>
/// NpcCanonicalState - 已废弃
/// 
/// 请使用 NpcPromptCache 替代
/// 
/// 旧系统的问题：
/// - 直接修改状态（非事件溯源）
/// - 基于规则推断（非主模型驱动）
/// - 与四层架构真相分叉
/// 
/// 新系统：
/// - NpcPromptCache（从四层架构投影生成）
/// - EntityCanonical（实体规范化）
/// - EventLog（事件流）
/// - WorldStateProjection（世界状态投影）
/// </summary>
[Obsolete("请使用 NpcPromptCache 替代，此类已废弃")]
public class NpcCanonicalState
{
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string NpcId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CoreSummary { get; set; } = "";
    public string IdentityState { get; set; } = "";
    public string KeyEventsDigest { get; set; } = "";
    public string RelationshipState { get; set; } = "";
    public string PendingRelationshipDeltaJson { get; set; } = "{}";
    public DateTime LastSummaryUpdatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // 人格稳定性：0~1，越高越难漂移
    public double PersonalityStability { get; set; } = 0.8;

    // NPC 对玩家的长期印象
    public PlayerImpression? PlayerImpression { get; set; }
}

/// <summary>
/// NPC 对玩家的长期印象
/// </summary>
public class PlayerImpression
{
    public double Trust { get; set; } = 0.5;      // 信任度 0~1
    public double Fear { get; set; } = 0.0;       // 恐惧度 0~1
    public double Respect { get; set; } = 0.5;    // 尊重度 0~1
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 场景快照：关键场景结束时保存的状态快照
/// </summary>
public class SceneSnapshot
{
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string SceneId { get; set; } = "";
    public string SceneDescription { get; set; } = "";
    public List<string> PresentEntities { get; set; } = new();
    public Dictionary<string, object> StateProperties { get; set; } = new();
    public string SnapshotReason { get; set; } = ""; // "scene_change", "combat_end", "location_destroyed", "faction_change"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 行为证据：单次行为累积，达到阈值后影响长期人格
/// </summary>
public class BehaviorEvidence
{
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string NpcId { get; set; } = "";
    public string Trait { get; set; } = ""; // "aggressive", "friendly", "trustworthy", etc.
    public double Evidence { get; set; } = 0.0;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

// ==================== 四层架构数据结构 ====================

/// <summary>
/// 第一层：Objective Layer - 任务目标
/// </summary>
public class QuestObjective
{
    public long Id { get; set; }
    public string Description { get; set; } = "";
    public QuestStatus Status { get; set; } = QuestStatus.Active;
    public QuestPriority Priority { get; set; } = QuestPriority.Normal;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastTouchedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public bool HiddenFromPrompt { get; set; }
    public string SourceSceneId { get; set; } = "";
    public string LastMentionedSceneId { get; set; } = "";
}

public enum QuestStatus
{
    Active,
    Completed,
    Abandoned,
    Superseded,
    Stale
}

public enum QuestPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>
/// 第二层：Canonical Entity Layer - 实体规范化记录
/// </summary>
public class EntityCanonicalRecord
{
    public string WorldId { get; set; } = "";
    public string EntityId { get; set; } = "";  // 唯一标识，如 npc_001
    public string CurrentDisplayName { get; set; } = "";  // 当前显示名称，如"老王"
    public List<string> Aliases { get; set; } = new();  // 别名，如["青年人", "研究人员"]
    public EntityIdentityStatus IdentityStatus { get; set; } = EntityIdentityStatus.Tentative;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;  // 版本号，用于冲突检测和版本管理
    public string? ConflictReason { get; set; }  // 冲突原因，如果有

    // === 核心摘要 ===
    public string CoreSummary { get; set; } = "";  // 角色核心设定摘要
    public string EntityFactSummary { get; set; } = "";

    // === 永久事实层 ===
    public List<PersistentFact> PersistentFacts { get; set; } = new();  // 长期不变的事实

    // === 动态关系系统 ===
    public Dictionary<string, DynamicRelationship> Relationships { get; set; } = new();  // 动态关系状态
}

/// <summary>
/// 永久事实：实体的长期不变事实
/// 例如：老王知道钥匙位置、老王左眼瞎了、老王属于A组织
/// </summary>
public class PersistentFact
{
    public string Fact { get; set; } = "";  // 事实描述
    public string Category { get; set; } = "general";  // 分类：knowledge, physical, affiliation, ability
    public DateTime EstablishedAt { get; set; } = DateTime.UtcNow;  // 确立时间
    public long? RelatedEventId { get; set; } = null;  // 关联事件ID
    public bool IsActive { get; set; } = true;  // 是否仍然有效
    public int EstablishedFoldCount { get; set; } = 0;  // 确立时的事件折叠计数
    public double Salience { get; set; } = 1.0;  // 显著性（用于淡化）
}

/// <summary>
/// 动态关系：支持衰减、波动、创伤的动态关系系统
/// </summary>
public class DynamicRelationship
{
    // === 基础值 ===
    public double BaseValue { get; set; } = 0;  // 基础关系值（-100 ~ 100）

    // === 短期情绪 ===
    public double ShortTermMood { get; set; } = 0;  // 短期情绪波动（-50 ~ 50）
    public DateTime MoodLastUpdated { get; set; } = DateTime.UtcNow;  // 情绪最后更新时间

    // === 长期印象 ===
    public double LongTermImpression { get; set; } = 0;  // 长期印象（-100 ~ 100）
    public int ImpressionEventCount { get; set; } = 0;  // 形成印象的事件数量

    // === 创伤事件 ===
    public List<TraumaEvent> Traumas { get; set; } = new();  // 创伤事件列表

    // === 关系来源记忆（KeyBondMoments） ===
    public List<KeyBondMoment> KeyBondMoments { get; set; } = new();  // 关键关系时刻

    // === 叙事强化 ===
    public Dictionary<string, int> NarrativeTouchCount { get; set; } = new();  // 叙事触碰计数
    public DateTime LastNarrativeTouch { get; set; } = DateTime.UtcNow;  // 最后叙事触碰时间

    // === 衰减参数 ===
    public double DecayRate { get; set; } = 0.01;  // 衰减率（每次事件折叠）
    public int LastDecayFoldCount { get; set; } = 0;  // 上次衰减时的事件折叠计数

    /// <summary>
    /// 计算当前有效关系值
    /// </summary>
    public double GetCurrentValue(int currentFoldCount)
    {
        var moodFactor = CalculateMoodDecay(currentFoldCount);
        var narrativeFactor = CalculateNarrativeReinforcement();
        return BaseValue + (ShortTermMood * moodFactor * narrativeFactor) + (LongTermImpression * narrativeFactor);
    }

    /// <summary>
    /// 计算短期情绪衰减（基于事件折叠）
    /// </summary>
    private double CalculateMoodDecay(int currentFoldCount)
    {
        var foldsSinceUpdate = currentFoldCount - LastDecayFoldCount;
        return Math.Exp(-DecayRate * foldsSinceUpdate);
    }

    /// <summary>
    /// 计算叙事强化因子
    /// </summary>
    private double CalculateNarrativeReinforcement()
    {
        var daysSinceTouch = (DateTime.UtcNow - LastNarrativeTouch).TotalDays;
        var touchCount = NarrativeTouchCount.Values.Sum();
        // 叙事触碰越多，强化越强；时间越近，强化越强
        var reinforcement = 1.0 + (touchCount * 0.05) * Math.Exp(-daysSinceTouch * 0.1);
        return Math.Min(reinforcement, 2.0); // 最大2倍强化
    }

    /// <summary>
    /// 应用关系变化
    /// </summary>
    public void ApplyChange(double delta, bool isTrauma = false, string? traumaReason = null, long? relatedEventId = null)
    {
        if (isTrauma)
        {
            // 创伤事件：直接影响长期印象
            LongTermImpression += delta;
            Traumas.Add(new TraumaEvent
            {
                Delta = delta,
                Reason = traumaReason ?? "未记录",
                OccurredAt = DateTime.UtcNow,
                RelatedEventId = relatedEventId
            });
            ImpressionEventCount++;
        }
        else
        {
            // 普通事件：影响短期情绪
            ShortTermMood += delta;
            MoodLastUpdated = DateTime.UtcNow;
        }

        // 记录关键关系时刻
        if (Math.Abs(delta) >= 10 || isTrauma)
        {
            KeyBondMoments.Add(new KeyBondMoment
            {
                Delta = delta,
                Reason = traumaReason ?? "关系变化",
                OccurredAt = DateTime.UtcNow,
                RelatedEventId = relatedEventId,
                IsTrauma = isTrauma
            });
        }

        // 限制范围
        ShortTermMood = Math.Clamp(ShortTermMood, -50, 50);
        LongTermImpression = Math.Clamp(LongTermImpression, -100, 100);
    }

    /// <summary>
    /// 应用叙事强化
    /// </summary>
    public void ApplyNarrativeTouch(string context)
    {
        if (!NarrativeTouchCount.ContainsKey(context))
            NarrativeTouchCount[context] = 0;
        NarrativeTouchCount[context]++;
        LastNarrativeTouch = DateTime.UtcNow;
    }

    /// <summary>
    /// 应用衰减（基于事件折叠）
    /// </summary>
    public void ApplyDecay(int currentFoldCount)
    {
        var foldsSinceDecay = currentFoldCount - LastDecayFoldCount;
        if (foldsSinceDecay < 1) return;

        // 短期情绪衰减
        var moodFactor = CalculateMoodDecay(currentFoldCount);
        var narrativeFactor = CalculateNarrativeReinforcement();
        ShortTermMood *= moodFactor * narrativeFactor;

        // 长期印象轻微衰减（创伤事件不会轻易消失）
        LongTermImpression *= (1 - DecayRate * 0.1 * foldsSinceDecay);

        LastDecayFoldCount = currentFoldCount;
    }

    /// <summary>
    /// 检查是否需要创伤归并
    /// </summary>
    public bool NeedsTraumaConsolidation()
    {
        return Traumas.Count >= 10 || (LongTermImpression < -50 && Traumas.Count >= 5);
    }
}

/// <summary>
/// 关键关系时刻：关系来源记忆
/// </summary>
public class KeyBondMoment
{
    public double Delta { get; set; }  // 关系变化值
    public string Reason { get; set; } = "";  // 原因
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;  // 发生时间
    public long? RelatedEventId { get; set; } = null;  // 关联事件ID
    public bool IsTrauma { get; set; } = false;  // 是否为创伤事件
}

/// <summary>
/// 创伤事件：对关系产生重大影响的事件
/// </summary>
public class TraumaEvent
{
    public double Delta { get; set; }  // 关系变化值
    public string Reason { get; set; } = "";  // 原因
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;  // 发生时间
    public long? RelatedEventId { get; set; } = null;  // 关联事件ID
}

public enum EntityIdentityStatus
{
    Tentative,  // 临时身份，可能合并
    Confirmed,  // 确认身份
    Merged      // 已合并到其他实体
}

// WorldEvent 已在 WorldEvent.cs 中定义，此处不再重复定义

/// <summary>
/// 第四层：Scene Snapshot - 场景快照（扩展原有定义）
/// </summary>
public class SceneSnapshotExtended
{
    public string SceneId { get; set; } = "";
    public DateTime EnteredAt { get; set; }
    public List<string> PresentEntityIds { get; set; } = new();
    public List<string> SceneGoals { get; set; } = new();
    public List<string> OutstandingThreads { get; set; } = new();
    public Dictionary<string, object> SceneFlags { get; set; } = new();

    /// <summary>
    /// 序列化为 JSON
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    /// <summary>
    /// 从 JSON 反序列化
    /// </summary>
    public static SceneSnapshotExtended? FromJson(string json)
    {
        return JsonSerializer.Deserialize<SceneSnapshotExtended>(json);
    }
}
/// <summary>
/// LLM Debug 日志条目
/// </summary>
public sealed class LlmDebugLogEntry
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string? CharacterId { get; set; }
    public string AgentName { get; set; } = "";
    public string RequestKind { get; set; } = "";
    public string MessagesJson { get; set; } = "[]";
    public string? ResponseText { get; set; }
    public bool Success { get; set; } = true;
    public string? Error { get; set; }
    public int InputCharCount { get; set; }
    public int OutputCharCount { get; set; }
    public string Metadata { get; set; } = "{}";
}
