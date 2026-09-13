using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 创伤归并代理：负责归并创伤事件和清理低印象内容
/// </summary>
public class TraumaConsolidationAgent
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EntityCanonicalizer _entityCanonicalizer;
    private readonly Func<List<string>, Task<string?>> _callTrpgApi;

    private const string ConsolidationSystemPrompt = """
你是一个创伤归并系统，负责整理角色的创伤事件和关系记忆。

你的职责：
1. 归并相似的创伤事件：将性质相同或相关的创伤合并为一个概括性描述
2. 删除低印象内容：移除影响较小（|delta| < 5）的KeyBondMoment
3. 生成归并摘要：用简洁的语言概括归并后的创伤状态

输入格式：
- 创伤事件列表：每条包含 Delta（变化值）、Reason（原因）、OccurredAt（时间）
- 关键时刻列表：每条包含 Delta（变化值）、Reason（原因）、IsTrauma（是否创伤）

输出格式：
<consolidated_trauma>
归并后的创伤描述，用分号分隔
</consolidated_trauma>

<removed_moments>
被删除的低印象时刻数量
</removed_moments>

注意事项：
- 归并时保留创伤的核心影响（Delta总和）
- 相似创伤：如"被背叛"和"被欺骗"可以归并为"多次被背叛"
- 低印象时刻：|delta| < 5 的时刻可以删除
- 只输出标签，不要输出其他解释或分析
""";

    public TraumaConsolidationAgent(
        IModContext context,
        ChatDatabase db,
        EntityCanonicalizer entityCanonicalizer,
        Func<List<string>, Task<string?>> callTrpgApi)
    {
        _context = context;
        _db = db;
        _entityCanonicalizer = entityCanonicalizer;
        _callTrpgApi = callTrpgApi;
    }

    /// <summary>
    /// 检查并执行创伤归并
    /// </summary>
    public async Task<int> CheckAndConsolidateAsync(TrpgScope scope, string characterId)
    {
        var allEntities = await _db.GetAllEntityCanonicalAsync(scope);
        var consolidatedCount = 0;

        foreach (var entity in allEntities)
        {
            foreach (var kvp in entity.Relationships)
            {
                var rel = kvp.Value;
                if (rel.NeedsTraumaConsolidation())
                {
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] 检测到需要归并的关系: {entity.EntityId} -> {kvp.Key}");
                    var result = await ConsolidateRelationshipAsync(scope, entity, kvp.Key);
                    if (result)
                    {
                        consolidatedCount++;
                    }
                }
            }
        }

        return consolidatedCount;
    }

    /// <summary>
    /// 归并单个关系
    /// </summary>
    private async Task<bool> ConsolidateRelationshipAsync(TrpgScope scope, EntityCanonicalRecord entity, string relKey)
    {
        try
        {
            var rel = entity.Relationships[relKey];

            // 构建归并prompt
            var prompt = BuildConsolidationPrompt(rel);

            // 调用AI进行归并
            var response = await _callTrpgApi(new List<string> { ConsolidationSystemPrompt, prompt });
            if (string.IsNullOrWhiteSpace(response))
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] 创伤归并AI调用失败: {entity.EntityId} -> {relKey}");
                return false;
            }

            // 解析结果
            var consolidatedTrauma = ExtractTag(response, "consolidated_trauma");
            var removedMoments = ExtractTag(response, "removed_moments");

            // 应用归并
            if (!string.IsNullOrWhiteSpace(consolidatedTrauma))
            {
                // 归并创伤：保留最新的创伤记录，更新Reason
                if (rel.Traumas.Count > 0)
                {
                    var latestTrauma = rel.Traumas.OrderByDescending(t => t.OccurredAt).First();
                    latestTrauma.Reason = consolidatedTrauma;
                    latestTrauma.Delta = rel.Traumas.Sum(t => t.Delta); // 保留总影响
                }

                // 删除旧创伤，只保留归并后的
                rel.Traumas = rel.Traumas.OrderByDescending(t => t.OccurredAt).Take(1).ToList();
            }

            // 删除低印象时刻
            var originalMomentCount = rel.KeyBondMoments.Count;
            rel.KeyBondMoments = rel.KeyBondMoments.Where(m => Math.Abs(m.Delta) >= 5).ToList();
            var removedCount = originalMomentCount - rel.KeyBondMoments.Count;

            // 更新实体
            entity.LastUpdated = DateTime.UtcNow;
            await _db.UpsertEntityCanonicalAsync(scope, entity);

            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 创伤归并完成: {entity.EntityId} -> {relKey}, 删除 {removedCount} 个低印象时刻");
            return true;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 创伤归并失败: {entity.EntityId} -> {relKey}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 构建归并prompt
    /// </summary>
    private string BuildConsolidationPrompt(DynamicRelationship rel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("创伤事件列表：");
        foreach (var trauma in rel.Traumas)
        {
            sb.AppendLine($"- Delta: {trauma.Delta}, Reason: {trauma.Reason}, Time: {trauma.OccurredAt:yyyy-MM-dd HH:mm}");
        }
        sb.AppendLine();
        sb.AppendLine("关键时刻列表：");
        foreach (var moment in rel.KeyBondMoments)
        {
            sb.AppendLine($"- Delta: {moment.Delta}, Reason: {moment.Reason}, IsTrauma: {moment.IsTrauma}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 提取标签内容
    /// </summary>
    private string ExtractTag(string response, string tagName)
    {
        var match = Regex.Match(response, $"<{tagName}>(.*?)</{tagName}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }
}
