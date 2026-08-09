using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 实体 Salience / Heat 系统
/// 追踪实体在当前上下文中的相关度和热度
/// 支持时间线热度衰减
/// </summary>
public sealed class EntitySalienceService
{
    private readonly ChatDatabase _db;
    private readonly IModContext _context;

    public EntitySalienceService(ChatDatabase db, IModContext context)
    {
        _db = db;
        _context = context;
    }

    /// <summary>
    /// 接触实体：增加热度
    /// 规则：
    /// - 当前 GM 文本直接提到实体名/别名：+4
    /// - 当前确认在场：+5
    /// - 当前角色与其互动：+4
    /// - 当前目标相关：+3
    /// - 活跃 L1/L2 时间线相关：+2
    /// - 活跃 L3 相关：+1
    /// </summary>
    public async Task TouchEntityAsync(
        TrpgScope scope,
        string entityId,
        double deltaHeat,
        string? source = null,
        string? evidence = null,
        string? sceneId = null)
    {
        try
        {
            await _db.TouchEntityHeatAsync(scope, entityId, deltaHeat, source, evidence, sceneId);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] TouchEntityAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 衰减实体热度
    /// 规则：heat *= pow(0.5, deltaFold / halfLifeFolds)
    /// </summary>
    public async Task DecayEntityHeatAsync(TrpgScope scope, int currentFoldCount, int halfLifeFolds = 8)
    {
        try
        {
            await _db.DecayEntityHeatAsync(scope, currentFoldCount, halfLifeFolds);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] DecayEntityHeatAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取热实体列表
    /// </summary>
    public async Task<List<(string EntityId, double Heat)>> GetHotEntitiesAsync(TrpgScope scope, int limit = 20)
    {
        try
        {
            return await _db.GetHotEntitiesAsync(scope, limit);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] GetHotEntitiesAsync failed: {ex.Message}");
            return new List<(string, double)>();
        }
    }

    /// <summary>
    /// 获取 InfoExtractor 候选实体
    /// - PresentEntities 和 direct mentions 必须包含
    /// - 按 heat 排序，默认限制 12 个
    /// </summary>
    public async Task<List<string>> GetEntityCandidatesForExtractorAsync(
        TrpgScope scope,
        List<string> presentEntityIds,
        List<string> directMentions,
        int limit = 12)
    {
        var candidates = new HashSet<string>();

        // 优先级 1：当前确认在场
        foreach (var id in presentEntityIds)
            candidates.Add(id);

        // 优先级 2：当前文本直接提及
        foreach (var id in directMentions)
            candidates.Add(id);

        // 优先级 3：热实体（按 heat 排序）
        try
        {
            var hotEntities = await _db.GetHotEntitiesAsync(scope, limit * 2);
            foreach (var (entityId, _) in hotEntities)
            {
                candidates.Add(entityId);
                if (candidates.Count >= limit)
                    break;
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] GetEntityCandidatesForExtractorAsync failed: {ex.Message}");
        }

        return candidates.Take(limit).ToList();
    }
}

