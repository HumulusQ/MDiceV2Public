using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 实体认知折叠器：防止 NPC/Entity 的 CoreSummary 和 PersistentFacts 无限追加。
/// 
/// 触发条件：
/// 1. entity_profile 写入后 active PersistentFacts > 12
/// 2. MergeIdentityAsync 合并实体后
/// 3. CoreSummary 为空但 active facts >= 3
/// </summary>
public sealed class EntityProfileConsolidator
{
    private readonly ChatDatabase _db;
    private readonly IModContext _context;

    private const int MaxActiveFacts = 10;
    private const int MaxFactsPerCategory = 2;
    private const int MaxKeyBondMoments = 8;
    private const int MaxTraumas = 5;

    public EntityProfileConsolidator(ChatDatabase db, IModContext context)
    {
        _db = db;
        _context = context;
    }

    /// <summary>
    /// 合并实体后触发：target 已包含 source 的 facts/rels，需要压缩
    /// </summary>
    public async Task ConsolidateIfNeededAsync(TrpgScope scope, string entityId, string reason)
    {
        var entity = await _db.GetEntityCanonicalAsync(scope, entityId);
        if (entity == null) return;

        var activeFactsBefore = entity.PersistentFacts.Count(f => f.IsActive);
        var needsConsolidation = reason == "entity_merge"
            || activeFactsBefore > 12
            || (string.IsNullOrWhiteSpace(entity.CoreSummary) && activeFactsBefore >= 3);

        if (!needsConsolidation) return;

        await ConsolidateCoreSummaryAsync(entity);
        await ConsolidatePersistentFactsAsync(entity);
        await ConsolidateRelationshipsAsync(entity);
        entity.EntityFactSummary = BuildEntityFactSummary(entity);

        await _db.UpsertEntityCanonicalAsync(scope, entity);

        var activeFactsAfter = entity.PersistentFacts.Count(f => f.IsActive);
        var archivedCount = activeFactsBefore - activeFactsAfter;

        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] EntityProfileConsolidationDiagnostics | entity={entityId} " +
            $"reason={reason} | active_facts_before={activeFactsBefore} " +
            $"active_facts_after={activeFactsAfter} | archived_facts={archivedCount} " +
            $"core_summary_updated={!string.IsNullOrWhiteSpace(entity.CoreSummary)}");
    }

    private Task ConsolidateCoreSummaryAsync(EntityCanonicalRecord entity)
    {
        // CoreSummary 为空时，从最高 salience facts 拼一个短摘要
        if (string.IsNullOrWhiteSpace(entity.CoreSummary))
        {
            var topFacts = entity.PersistentFacts
                .Where(f => f.IsActive && f.Category != "status")
                .OrderByDescending(f => f.Salience)
                .Take(3)
                .Select(f => f.Fact)
                .ToList();

            if (topFacts.Count > 0)
            {
                var summary = string.Join("；", topFacts);
                entity.CoreSummary = summary.Length <= 160 ? summary : summary.Substring(0, 157) + "...";
            }
        }
        else if (entity.CoreSummary.Length > 200)
        {
            // 过长摘要截断
            entity.CoreSummary = entity.CoreSummary.Substring(0, 197) + "...";
        }

        return Task.CompletedTask;
    }

    private Task ConsolidatePersistentFactsAsync(EntityCanonicalRecord entity)
    {
        var activeFacts = entity.PersistentFacts.Where(f => f.IsActive).ToList();

        // 1. 去重：完全相同文本去重
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<PersistentFact>();
        foreach (var fact in activeFacts)
        {
            var normalized = fact.Fact.Trim().ToLowerInvariant();
            if (seen.Add(normalized))
                deduped.Add(fact);
            else
                fact.IsActive = false; // 重复的标记为 inactive
        }

        // 2. 按 Category 分组，每组保留 salience 最高的前 MaxFactsPerCategory 条
        var grouped = deduped.GroupBy(f => NormalizeCategory(f.Category));
        var kept = new List<PersistentFact>();
        foreach (var group in grouped)
        {
            var top = group.OrderByDescending(f => f.Salience).Take(MaxFactsPerCategory).ToList();
            kept.AddRange(top);
            foreach (var fact in group.Except(top))
            {
                fact.IsActive = false;
                if (string.IsNullOrWhiteSpace(fact.Category) || fact.Category == "other")
                    fact.Category = "consolidated";
            }
        }

        // 3. 全部 active facts 控制在 MaxActiveFacts 条
        if (kept.Count > MaxActiveFacts)
        {
            var final = kept.OrderByDescending(f => f.Salience).Take(MaxActiveFacts).ToList();
            foreach (var fact in kept.Except(final))
            {
                fact.IsActive = false;
                if (string.IsNullOrWhiteSpace(fact.Category) || fact.Category == "other")
                    fact.Category = "consolidated";
            }
        }

        return Task.CompletedTask;
    }

    private static string BuildEntityFactSummary(EntityCanonicalRecord entity)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entity.CoreSummary))
            parts.Add(entity.CoreSummary.Trim());

        parts.AddRange(entity.PersistentFacts
            .Where(f => f.IsActive)
            .OrderByDescending(f => f.Category.Equals("status", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Salience)
            .Select(f => f.Fact.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4));

        var summary = string.Join("；", parts.Distinct(StringComparer.OrdinalIgnoreCase));
        return summary.Length <= 220 ? summary : summary[..220];
    }

    private Task ConsolidateRelationshipsAsync(EntityCanonicalRecord entity)
    {
        foreach (var (_, rel) in entity.Relationships)
        {
            // KeyBondMoments > MaxKeyBondMoments: 保留最重要/最近的
            if (rel.KeyBondMoments.Count > MaxKeyBondMoments)
            {
                rel.KeyBondMoments = rel.KeyBondMoments
                    .OrderByDescending(m => Math.Abs(m.Delta))
                    .ThenByDescending(m => m.OccurredAt)
                    .Take(MaxKeyBondMoments)
                    .ToList();
            }

            // Traumas > MaxTraumas: 保留高强度 trauma
            if (rel.Traumas.Count > MaxTraumas || rel.NeedsTraumaConsolidation())
            {
                rel.Traumas = rel.Traumas
                    .OrderByDescending(t => Math.Abs(t.Delta))
                    .Take(MaxTraumas)
                    .ToList();
            }
        }

        return Task.CompletedTask;
    }

    private static string NormalizeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "other";
        var c = category.ToLowerInvariant().Trim();
        return c switch
        {
            "identity" or "fact" or "knowledge" => "identity",
            "status" or "state" => "status",
            "relationship" or "relation" => "relationship",
            "scene" or "scene_setting" => "scene",
            "item" or "inventory" => "item",
            "merged_summary" => "merged_summary",
            "archived" or "consolidated" => "consolidated",
            _ => "other"
        };
    }
}
