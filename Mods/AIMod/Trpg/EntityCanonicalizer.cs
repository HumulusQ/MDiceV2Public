using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 第二层：Canonical Entity Layer - 实体规范化层
/// 职责：维护实体身份，防止 NPC 漂移
/// </summary>
public class EntityCanonicalizer
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EntitySalienceService? _entitySalienceService;

    public EntityCanonicalizer(IModContext context, ChatDatabase db, EntitySalienceService? entitySalienceService = null)
    {
        _context = context;
        _db = db;
        _entitySalienceService = entitySalienceService;
    }

    /// <summary>
    /// 创建新实体
    /// </summary>
    public async Task<string> CreateEntityAsync(TrpgScope scope, string entityId, string displayName, List<string>? aliases = null)
    {
        var record = new EntityCanonicalRecord
        {
            WorldId = scope.WorldId,
            EntityId = entityId,
            CurrentDisplayName = displayName,
            Aliases = aliases ?? new List<string> { displayName },
            IdentityStatus = EntityIdentityStatus.Tentative
        };
        await _db.UpsertEntityCanonicalAsync(scope, record);
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] EntityCanonicalizer: 创建实体 - {entityId} ({displayName})");
        return entityId;
    }

    /// <summary>
    /// 解析名称到 EntityId
    /// </summary>
    public async Task<string?> ResolveEntityIdAsync(TrpgScope scope, string name)
    {
        var entities = await _db.GetAllEntityCanonicalAsync(scope);
        return entities.FirstOrDefault(e => 
            string.Equals(e.CurrentDisplayName, name, StringComparison.OrdinalIgnoreCase) ||
            e.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))?.EntityId;
    }

    /// <summary>
    /// 获取实体记录
    /// </summary>
    public async Task<EntityCanonicalRecord?> GetEntityAsync(TrpgScope scope, string entityId)
    {
        return await _db.GetEntityCanonicalAsync(scope, entityId);
    }

    /// <summary>
    /// 获取所有实体记录
    /// </summary>
    public async Task<List<EntityCanonicalRecord>> GetAllEntitiesAsync(TrpgScope scope)
    {
        return await _db.GetAllEntityCanonicalAsync(scope);
    }

    /// <summary>
    /// 合并实体身份
    /// </summary>
    public async Task MergeIdentityAsync(TrpgScope scope, string fromName, string toName, string targetEntityId)
    {
        var fromEntityId = await ResolveEntityIdAsync(scope, fromName);
        if (fromEntityId == null)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] EntityCanonicalizer: 未找到源实体 - {fromName}");
            return;
        }

        var targetEntity = await GetEntityAsync(scope, targetEntityId);
        if (targetEntity == null)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] EntityCanonicalizer: 未找到目标实体 - {targetEntityId}");
            return;
        }

        // 将源实体的别名添加到目标实体
        var sourceEntity = await GetEntityAsync(scope, fromEntityId);
        if (sourceEntity != null)
        {
            // 合并 aliases（包括 source.CurrentDisplayName）
            foreach (var alias in sourceEntity.Aliases)
            {
                if (!targetEntity.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                    targetEntity.Aliases.Add(alias);
            }
            if (!string.IsNullOrWhiteSpace(sourceEntity.CurrentDisplayName)
                && !targetEntity.Aliases.Contains(sourceEntity.CurrentDisplayName, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(targetEntity.CurrentDisplayName, sourceEntity.CurrentDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                targetEntity.Aliases.Add(sourceEntity.CurrentDisplayName);
            }
            targetEntity.LastUpdated = DateTime.UtcNow;
            await _db.UpsertEntityCanonicalAsync(scope, targetEntity);

            // 标记源实体为已合并
            sourceEntity.IdentityStatus = EntityIdentityStatus.Merged;
            sourceEntity.LastUpdated = DateTime.UtcNow;
            sourceEntity.ConflictReason = $"merged_into:{targetEntityId}";
            await _db.UpsertEntityCanonicalAsync(scope, sourceEntity);

            var mergedAliasesCount = targetEntity.Aliases.Count;
            var mergedFactsCount = 0;
            var mergedRelsCount = 0;
            var summaryPreserved = false;

            // === CoreSummary 合并 ===
            if (string.IsNullOrWhiteSpace(targetEntity.CoreSummary) && !string.IsNullOrWhiteSpace(sourceEntity.CoreSummary))
            {
                targetEntity.CoreSummary = sourceEntity.CoreSummary;
                summaryPreserved = true;
            }
            else if (!string.IsNullOrWhiteSpace(sourceEntity.CoreSummary)
                     && !string.Equals(targetEntity.CoreSummary, sourceEntity.CoreSummary, StringComparison.OrdinalIgnoreCase))
            {
                // 不同摘要，将 source 摘要作为 PersistentFact 附加
                var factExists = targetEntity.PersistentFacts.Any(f =>
                    string.Equals(f.Fact, sourceEntity.CoreSummary, StringComparison.OrdinalIgnoreCase));
                if (!factExists)
                {
                    targetEntity.PersistentFacts.Add(new PersistentFact
                    {
                        Fact = sourceEntity.CoreSummary,
                        Category = "merged_summary",
                        IsActive = true,
                        Salience = 0.8,
                        EstablishedAt = DateTime.UtcNow
                    });
                }
            }

            // === PersistentFacts 合并（去重） ===
            foreach (var fact in sourceEntity.PersistentFacts.Where(f => f.IsActive))
            {
                var exists = targetEntity.PersistentFacts.Any(f =>
                    string.Equals(f.Fact, fact.Fact, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    targetEntity.PersistentFacts.Add(new PersistentFact
                    {
                        Fact = fact.Fact,
                        Category = fact.Category,
                        IsActive = true,
                        Salience = fact.Salience,
                        EstablishedAt = fact.EstablishedAt
                    });
                    mergedFactsCount++;
                }
            }

            // === Relationships 合并 ===
            foreach (var (relKey, sourceRel) in sourceEntity.Relationships)
            {
                if (!targetEntity.Relationships.ContainsKey(relKey))
                {
                    targetEntity.Relationships[relKey] = sourceRel;
                    mergedRelsCount++;
                }
                else
                {
                    // 合并 KeyBondMoments 和 Traumas
                    var targetRel = targetEntity.Relationships[relKey];
                    foreach (var moment in sourceRel.KeyBondMoments)
                    {
                        if (!targetRel.KeyBondMoments.Any(m =>
                            string.Equals(m.Reason, moment.Reason, StringComparison.OrdinalIgnoreCase)
                            && Math.Abs((m.OccurredAt - moment.OccurredAt).TotalSeconds) < 10))
                        {
                            targetRel.KeyBondMoments.Add(moment);
                        }
                    }
                    mergedRelsCount++;
                }
            }

            await _db.UpsertEntityCanonicalAsync(scope, targetEntity);

            _context.Log(LogLevel.Info, $"[AIMod:TRPG] EntityCanonicalizer: 合并实体 - {fromName} -> {toName} ({targetEntityId})");

            // 合并实体热度
            var heatMerged = false;
            double sourceHeat = 0, targetHeatBefore = 0, targetHeatAfter = 0;
            if (_entitySalienceService != null)
            {
                try
                {
                    var hotEntities = await _db.GetHotEntitiesAsync(scope, limit: 100);
                    // 空安全：FirstOrDefault 返回 default((string,double))，Heat 为 0
                    var match = hotEntities.FirstOrDefault(h =>
                        string.Equals(h.EntityId, fromEntityId, StringComparison.OrdinalIgnoreCase));
                    sourceHeat = match.EntityId != null ? match.Heat : 0;
                    match = hotEntities.FirstOrDefault(h =>
                        string.Equals(h.EntityId, targetEntityId, StringComparison.OrdinalIgnoreCase));
                    targetHeatBefore = match.EntityId != null ? match.Heat : 0;
                    var mergedHeat = Math.Max(sourceHeat, targetHeatBefore);

                    if (mergedHeat > targetHeatBefore)
                    {
                        await _entitySalienceService.TouchEntityAsync(
                            scope, targetEntityId,
                            deltaHeat: mergedHeat - targetHeatBefore,
                            source: "MergeIdentity",
                            evidence: $"合并来自 {fromName}");
                        targetHeatAfter = mergedHeat;
                        heatMerged = true;
                    }
                    else
                    {
                        targetHeatAfter = targetHeatBefore;
                    }

                    _context.Log(LogLevel.Debug,
                        $"[AIMod:TRPG] EntitySalience heat merged | from={fromEntityId}(heat={sourceHeat:F2}) | to={targetEntityId}(heat={targetHeatBefore:F2}->{targetHeatAfter:F2})");
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Warn, $"[AIMod:TRPG] EntitySalience heat merge failed: {ex.Message}");
                }
            }

            _context.Log(LogLevel.Info,
                $"[AIMod:TRPG] EntityMergeDiagnostics | source={fromEntityId} target={targetEntityId} " +
                $"aliases_merged={mergedAliasesCount} facts_merged={mergedFactsCount} " +
                $"relationships_merged={mergedRelsCount} summary_preserved={summaryPreserved} " +
                $"heat_merged={heatMerged} source_heat={sourceHeat:F2} " +
                $"target_heat_before={targetHeatBefore:F2} target_heat_after={targetHeatAfter:F2}");

            // 触发 EntityProfileConsolidator 压缩合并后的实体介绍
            var consolidator = new EntityProfileConsolidator(_db, _context);
            await consolidator.ConsolidateIfNeededAsync(scope, targetEntityId, "entity_merge");
        }
    }

    /// <summary>
    /// 更新实体显示名称
    /// </summary>
    public async Task UpdateDisplayNameAsync(TrpgScope scope, string entityId, string newDisplayName)
    {
        var entity = await GetEntityAsync(scope, entityId);
        if (entity == null)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] EntityCanonicalizer: 未找到实体 - {entityId}");
            return;
        }

        // 将旧名称添加到别名
        if (!entity.Aliases.Contains(entity.CurrentDisplayName, StringComparer.OrdinalIgnoreCase))
        {
            entity.Aliases.Add(entity.CurrentDisplayName);
        }

        entity.CurrentDisplayName = newDisplayName;
        entity.IdentityStatus = EntityIdentityStatus.Confirmed;
        entity.LastUpdated = DateTime.UtcNow;
        await _db.UpsertEntityCanonicalAsync(scope, entity);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] EntityCanonicalizer: 更新显示名称 - {entityId}: {entity.CurrentDisplayName} -> {newDisplayName}");
    }

    /// <summary>
    /// 生成实体列表字符串（用于 Prompt）
    /// </summary>
    public string GenerateEntitiesString(List<EntityCanonicalRecord> entities)
    {
        if (entities.Count == 0)
            return "";

        var richEntities = entities
            .Where(e => e.IdentityStatus != EntityIdentityStatus.Merged)
            .Where(e => e.IdentityStatus == EntityIdentityStatus.Confirmed
                     || !string.IsNullOrWhiteSpace(e.CoreSummary)
                     || e.PersistentFacts.Any(f => f.IsActive)
                     || e.Relationships.Count > 0
                     || e.Aliases.Count > 1)
            .ToList();

        if (richEntities.Count == 0)
            return "";

        var sb = new StringBuilder();
        foreach (var entity in richEntities)
        {
            sb.AppendLine($"- {entity.CurrentDisplayName} (ID: {entity.EntityId})");
            if (entity.Aliases.Count > 1)
            {
                var otherAliases = entity.Aliases.Where(a => !string.Equals(a, entity.CurrentDisplayName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (otherAliases.Count > 0)
                    sb.AppendLine($"  别名: {string.Join(", ", otherAliases)}");
            }
            var preferredSummary = !string.IsNullOrWhiteSpace(entity.EntityFactSummary)
                ? entity.EntityFactSummary
                : entity.CoreSummary;
            if (!string.IsNullOrWhiteSpace(preferredSummary))
                sb.AppendLine($"  摘要: {preferredSummary}");
            var activeFacts = entity.PersistentFacts.Where(f => f.IsActive).ToList();
            if (activeFacts.Count > 0 && string.IsNullOrWhiteSpace(entity.EntityFactSummary))
                sb.AppendLine($"  事实: {string.Join("; ", activeFacts.Select(f => f.Fact))}");
            if (entity.Relationships.Count > 0)
            {
                var relStrings = entity.Relationships.Select(kvp =>
                {
                    var rel = kvp.Value;
                    var currentValue = rel.GetCurrentValue(0);
                    return $"{kvp.Key}={currentValue:F1}";
                });
                sb.AppendLine($"  关系: {string.Join(", ", relStrings)}");
            }
        }
        return sb.ToString();
    }
}
