using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Runtime Validator - 验证 AI 输出的四层架构标签
/// 防止 AI hallucination 污染世界状态
/// </summary>
public class RuntimeValidator
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;

    public RuntimeValidator(IModContext context, ChatDatabase db)
    {
        _context = context;
        _db = db;
    }

    /// <summary>
    /// 验证并处理四层架构标签
    /// 返回 (isValid, errorMessage)
    /// </summary>
    public (bool IsValid, string? ErrorMessage) ValidateTag(TrpgScope scope, string tagType, string tagContent, string characterId)
    {
        return tagType.ToLower() switch
        {
            "objective" => ValidateObjective(scope, tagContent, characterId),
            "complete" => ValidateComplete(scope, tagContent, characterId),
            "abandon" => ValidateAbandon(scope, tagContent, characterId),
            "identity_merge" => ValidateIdentityMerge(scope, tagContent, characterId),
            "event" => ValidateEvent(scope, tagContent, characterId),
            "memory" => ValidateMemory(scope, tagContent, characterId),
            "entity_change" => ValidateEntityChange(scope, tagContent, characterId),
            "scene_snapshot" => ValidateSceneSnapshot(scope, tagContent, characterId),
            "fact" => ValidateFact(scope, tagContent, characterId),
            "relationship" => ValidateRelationship(scope, tagContent, characterId),
            _ => (false, $"未知标签类型: {tagType}")
        };
    }

    /// <summary>
    /// 验证 objective 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateObjective(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "目标描述不能为空");

        if (content.Length > 200)
            return (false, "目标描述过长（最大200字符）");

        // 检查是否重复
        var existingObjectives = _db.GetActiveQuestsAsync(scope, characterId).Result;
        if (existingObjectives.Any(q => string.Equals(q.Description, content, StringComparison.OrdinalIgnoreCase)))
            return (false, "目标已存在");

        return (true, null);
    }

    /// <summary>
    /// 验证 complete 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateComplete(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "目标描述不能为空");

        // 检查目标是否存在且未完成
        var existingObjectives = _db.GetActiveQuestsAsync(scope, characterId).Result;
        if (!existingObjectives.Any(q => string.Equals(q.Description, content, StringComparison.OrdinalIgnoreCase)))
            return (false, "目标不存在或已完成");

        return (true, null);
    }

    /// <summary>
    /// 验证 abandon 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateAbandon(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "目标描述不能为空");

        // 检查目标是否存在且未完成
        var existingObjectives = _db.GetActiveQuestsAsync(scope, characterId).Result;
        if (!existingObjectives.Any(q => string.Equals(q.Description, content, StringComparison.OrdinalIgnoreCase)))
            return (false, "目标不存在或已完成");

        return (true, null);
    }

    /// <summary>
    /// 验证 identity_merge 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateIdentityMerge(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "合并描述不能为空");

        // 格式: 源名称->目标名称
        var parts = content.Split("->", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return (false, "合并格式错误，应为: 源名称->目标名称");

        var fromName = parts[0].Trim();
        var toName = parts[1].Trim();

        if (string.IsNullOrWhiteSpace(fromName) || string.IsNullOrWhiteSpace(toName))
            return (false, "源名称或目标名称不能为空");

        if (string.Equals(fromName, toName, StringComparison.OrdinalIgnoreCase))
            return (false, "源名称和目标名称不能相同");

        // 检查源实体是否存在
        var allEntities = _db.GetAllEntityCanonicalAsync(scope).Result;
        var fromEntity = allEntities.FirstOrDefault(e => 
            string.Equals(e.CurrentDisplayName, fromName, StringComparison.OrdinalIgnoreCase) ||
            e.Aliases.Any(a => string.Equals(a, fromName, StringComparison.OrdinalIgnoreCase)));

        if (fromEntity == null)
            return (false, $"源实体不存在: {fromName}");

        // 检查目标实体是否存在（允许不存在，执行时会自动创建）
        var toEntity = allEntities.FirstOrDefault(e =>
            string.Equals(e.CurrentDisplayName, toName, StringComparison.OrdinalIgnoreCase) ||
            e.Aliases.Any(a => string.Equals(a, toName, StringComparison.OrdinalIgnoreCase)));

        // 目标实体不存在时跳过验证，允许执行时创建
        // if (toEntity == null)
        //     return (false, $"目标实体不存在: {toName}");

        // 检查是否已经合并
        if (fromEntity.IdentityStatus == EntityIdentityStatus.Merged)
            return (false, $"源实体已合并: {fromName}");

        return (true, null);
    }

    /// <summary>
    /// 验证 event 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateEvent(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "事件描述不能为空");

        if (content.Length > 500)
            return (false, "事件描述过长（最大500字符）");

        return (true, null);
    }

    /// <summary>
    /// 验证 memory 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateMemory(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "记忆内容不能为空");

        if (content.Length > 1000)
            return (false, "记忆内容过长（最大1000字符）");

        return (true, null);
    }

    /// <summary>
    /// 验证 entity_change 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateEntityChange(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "实体变更描述不能为空");

        // 格式: 实体ID|新显示名称|新别名
        var parts = content.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || parts.Length > 3)
            return (false, "实体变更格式错误，应为: 实体ID|新显示名称|新别名");

        var entityId = parts[0].Trim();
        if (string.IsNullOrWhiteSpace(entityId))
            return (false, "实体ID不能为空");

        // entity_change 支持创建新实体，不需要检查实体是否存在
        // 如果实体不存在，StateMutationPipeline 会自动创建

        return (true, null);
    }

    /// <summary>
    /// 验证 scene_snapshot 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateSceneSnapshot(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "场景快照描述不能为空");

        if (content.Length > 1000)
            return (false, "场景快照描述过长（最大1000字符）");

        return (true, null);
    }

    /// <summary>
    /// 验证 fact 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateFact(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "事实内容不能为空");

        if (content.Length > 500)
            return (false, "事实内容过长（最大500字符）");

        var parts = content.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return (false, "事实格式错误，应为: 实体名|事实描述[|分类]");

        return (true, null);
    }

    /// <summary>
    /// 验证 relationship 标签
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateRelationship(TrpgScope scope, string content, string characterId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "关系内容不能为空");

        if (content.Length > 500)
            return (false, "关系内容过长（最大500字符）");

        var parts = content.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return (false, "关系格式错误，应为: 实体A|实体B|关系类型|变化值[|是否创伤[|原因]]");

        if (!double.TryParse(parts[3].Trim(), out _))
            return (false, "关系变化值必须为数字");

        return (true, null);
    }

    /// <summary>
    /// 时空验证：验证事件的时间顺序是否合理
    /// </summary>
    public (bool IsValid, string? ErrorMessage) ValidateTemporalConsistency(TrpgScope scope, WorldEvent newEvent, string characterId)
    {
        // 获取最新事件
        var latestEvent = _db.QueryEventLogAsync(scope, 0, null, 1).Result.FirstOrDefault();
        
        if (latestEvent != null)
        {
            // 新事件时间不能早于最新事件时间
            if (newEvent.Timestamp < latestEvent.Timestamp)
            {
                return (false, $"时间顺序错误：新事件时间 {newEvent.Timestamp} 早于最新事件时间 {latestEvent.Timestamp}");
            }
            
            // 新事件时间不能晚于当前时间 + 5分钟（防止未来事件）
            if (newEvent.Timestamp > DateTime.UtcNow.AddMinutes(5))
            {
                return (false, $"时间顺序错误：新事件时间 {newEvent.Timestamp} 晚于当前时间");
            }
        }

        // 验证场景切换的时空一致性
        if (newEvent.EventType == "scene_transition")
        {
            // 场景切换必须包含场景ID
            if (string.IsNullOrWhiteSpace(newEvent.SceneId))
            {
                return (false, "场景切换事件必须包含场景ID");
            }
        }

        // 验证实体移动的时空一致性
        if (newEvent.EventType == "entity_enter" || newEvent.EventType == "entity_exit")
        {
            // 实体移动必须包含场景ID
            if (string.IsNullOrWhiteSpace(newEvent.SceneId))
            {
                return (false, "实体移动事件必须包含场景ID");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// 身份验证：验证实体的身份状态是否合理
    /// </summary>
    public (bool IsValid, string? ErrorMessage) ValidateIdentityConsistency(TrpgScope scope, string entityId, string proposedAction, string characterId)
    {
        // 获取实体当前状态
        var entity = _db.GetEntityCanonicalAsync(scope, entityId).Result;
        
        if (entity == null)
        {
            return (false, $"实体不存在: {entityId}");
        }

        // 验证死亡实体的行动
        if (entity.IdentityStatus == EntityIdentityStatus.Merged)
        {
            if (proposedAction == "speak" || proposedAction == "act" || proposedAction == "move")
            {
                return (false, $"实体已合并，无法执行行动: {entityId}");
            }
        }

        // 验证已合并实体的行动
        if (entity.IdentityStatus == EntityIdentityStatus.Merged)
        {
            if (proposedAction == "speak" || proposedAction == "act" || proposedAction == "move")
            {
                return (false, $"实体已合并，无法执行行动: {entityId}");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// 空间验证：验证实体是否可以与目标实体互动
    /// </summary>
    public (bool IsValid, string? ErrorMessage) ValidateSpatialConsistency(TrpgScope scope, string sourceEntityId, string? targetEntityId, string? sceneId, string characterId)
    {
        // 如果没有目标实体，通过
        if (string.IsNullOrWhiteSpace(targetEntityId))
            return (true, null);

        // 获取当前世界状态投影
        // 这里简化处理，实际应该从 WorldStateProjection 获取
        // 暂时只验证实体是否存在
        var sourceEntity = _db.GetEntityCanonicalAsync(scope, sourceEntityId).Result;
        var targetEntity = _db.GetEntityCanonicalAsync(scope, targetEntityId).Result;

        if (sourceEntity == null)
            return (false, $"源实体不存在: {sourceEntityId}");

        if (targetEntity == null)
            return (false, $"目标实体不存在: {targetEntityId}");

        // 如果指定了场景，验证实体是否在该场景
        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            // 这里应该检查实体是否在指定场景
            // 暂时跳过，因为需要 WorldStateProjection
        }

        return (true, null);
    }

    /// <summary>
    /// 知识验证：验证实体是否应该知道某信息
    /// </summary>
    public (bool IsValid, string? ErrorMessage) ValidateKnowledgeConsistency(TrpgScope scope, string entityId, string information, string characterId)
    {
        // 检查实体是否存在
        var entity = _db.GetEntityCanonicalAsync(scope, entityId).Result;
        if (entity == null)
            return (false, $"实体不存在: {entityId}");

        // 这里应该检查实体是否通过事件获得了该信息
        // 暂时跳过，因为需要更复杂的知识图谱
        // 基本原则：实体只能知道它经历过的事件或被告知的信息

        return (true, null);
    }
}
