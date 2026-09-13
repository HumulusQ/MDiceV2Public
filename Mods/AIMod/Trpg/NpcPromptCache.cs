using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AIMod.Trpg;

/// <summary>
/// NPC Prompt Cache - NPC 提示缓存层
/// 
/// 重要：这不是真相源（Source of Truth）
/// 这只是从四层架构投影生成的压缩摘要，用于快速提供给 LLM
/// 
/// 真相源应该是：
/// - EventLog（不可变事件流）
/// - EntityCanonical（实体规范化）
/// - ObjectiveLayer（目标管理）
/// - SceneSnapshot（场景快照）
/// 
/// 本缓存层应该定期从四层架构重建，而不是直接修改
/// </summary>
public class NpcPromptCache
{
    public long GroupId { get; set; }
    public string NpcId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    
    /// <summary>
    /// 压缩摘要 - 从 EntityCanonical 和 EventLog 投影生成
    /// </summary>
    public string CompressedSummary { get; set; } = "";
    
    /// <summary>
    /// 关键事件摘要 - 从 EventLog 投影生成（最近N条）
    /// </summary>
    public string RecentEventsDigest { get; set; } = "";
    
    /// <summary>
    /// 关系状态 - 从关系投影层生成（未来实现）
    /// </summary>
    public string RelationshipDigest { get; set; } = "";
    
    /// <summary>
    /// 缓存生成时间
    /// </summary>
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// 缓存过期时间（建议5分钟）
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(5);
    
    /// <summary>
    /// 检查缓存是否过期
    /// </summary>
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    
    /// <summary>
    /// 生成用于 Prompt 的字符串
    /// </summary>
    public string ToPromptString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[NPC] {DisplayName}");
        sb.AppendLine($"summary: {CompressedSummary}");
        if (!string.IsNullOrWhiteSpace(RelationshipDigest))
            sb.AppendLine($"relationship: {RelationshipDigest}");
        if (!string.IsNullOrWhiteSpace(RecentEventsDigest))
            sb.AppendLine($"recent_events: {RecentEventsDigest}");
        return sb.ToString();
    }
}
