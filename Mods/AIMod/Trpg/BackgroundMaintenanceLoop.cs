using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 后台维护循环：定期清理和优化数据库
/// 触发机制：按会话条数/场景切换触发，而非固定时间周期
/// </summary>
public class BackgroundMaintenanceLoop
{
    private readonly ChatDatabase _db;
    private readonly IModContext _context;
    private readonly int _foldCountThreshold = 10; // 每10次记忆折叠触发一次维护
    private readonly int _sceneChangeThreshold = 5; // 每5次场景切换触发一次维护
    private readonly NarrativeEntropyManager _entropyManager;
    private readonly TemporalLayering _temporalLayering;
    private readonly CausalGraph _causalGraph;
    private readonly EventLog _eventLog;
    private readonly EntityCanonicalizer _entityCanonicalizer;
    private readonly EpisodicMemory _episodicMemory;
    private readonly NarrativeGravityEngine _gravityEngine;
    private readonly TraumaConsolidationAgent _traumaConsolidationAgent;

    public BackgroundMaintenanceLoop(
        ChatDatabase db,
        IModContext context,
        NarrativeEntropyManager? entropyManager = null,
        TemporalLayering? temporalLayering = null,
        CausalGraph? causalGraph = null,
        EventLog? eventLog = null,
        EntityCanonicalizer? entityCanonicalizer = null,
        EpisodicMemory? episodicMemory = null,
        NarrativeGravityEngine? gravityEngine = null,
        TraumaConsolidationAgent? traumaConsolidationAgent = null)
    {
        _db = db;
        _context = context;
        _eventLog = eventLog ?? new EventLog(context, db);
        _entityCanonicalizer = entityCanonicalizer ?? new EntityCanonicalizer(context, db);
        _causalGraph = causalGraph ?? new CausalGraph(context, db, _eventLog);
        _episodicMemory = episodicMemory ?? new EpisodicMemory(context, db, _eventLog);

        // TemporalLayering 需要 HierarchicalTimeline，所以先创建它
        var hierarchicalTimeline = new HierarchicalTimeline(context, db, _eventLog);
        _temporalLayering = temporalLayering ?? new TemporalLayering(context, db, _eventLog, hierarchicalTimeline);

        _entropyManager = entropyManager ?? new NarrativeEntropyManager(context, db, _eventLog, _causalGraph, _temporalLayering, _entityCanonicalizer);

        // 创建新组件
        var objectiveLayer = new ObjectiveLayer(context, db);
        _gravityEngine = gravityEngine ?? new NarrativeGravityEngine(context, db, _eventLog, _causalGraph, objectiveLayer);

        // 创建创伤归并代理（需要AI调用，暂时使用空实现）
        _traumaConsolidationAgent = traumaConsolidationAgent ?? new TraumaConsolidationAgent(
            context,
            db,
            _entityCanonicalizer,
            messages => Task.FromResult<string?>(null)); // 暂时禁用AI调用
    }

    /// <summary>
    /// 检查是否需要触发维护
    /// </summary>
    public async Task<bool> ShouldTriggerMaintenanceAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        // 检查记忆折叠次数
        var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
        if (memories.Count > 0)
        {
            var foldCount = memories[0].FoldCount;
            if (foldCount >= _foldCountThreshold)
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 维护触发条件满足：记忆折叠次数 {foldCount} >= {_foldCountThreshold}");
                return true;
            }
        }

        // 检查场景切换次数（通过场景快照数量估算）
        // 暂时简化：只在记忆折叠次数达到阈值时触发
        // TODO: 添加场景快照计数方法后可扩展

        return false;
    }

    /// <summary>
    /// 执行所有维护任务
    /// </summary>
    public async Task RunMaintenanceAsync(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] Background Maintenance Loop started (Group={groupId}, Char={characterId})");

        try
        {
            // 原有维护任务
            await MergeDuplicateMemoriesAsync(scope, characterId);
            await DeleteLowValueMarkersAsync(scope, characterId);
            await UpdateConfidenceDecayAsync(scope, characterId);
            await CleanOldEvidenceAsync(scope, characterId);

            // 新架构维护任务
            await _entropyManager.ManageEntropyAsync(scope, characterId);
            await _temporalLayering.AutoLayerAndCompressAsync(scope, characterId);
            await _causalGraph.ApplyEdgeDecayAsync(scope, characterId);
            await _episodicMemory.ApplyForgettingAsync(scope, characterId);

            // Story Permanence Architecture 维护任务
            await RecalculateEventGravityAsync(scope, characterId);

            // 动态关系系统维护任务
            await ConsolidateTraumasAsync(scope, characterId);
            await ApplyRelationshipDecayAsync(scope, characterId);
            await ApplyFactFadeAsync(scope, characterId);

            _context.Log(LogLevel.Info, $"[AIMod:TRPG] Background Maintenance Loop completed (Group={groupId}, Char={characterId})");
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] Background Maintenance Loop failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 合并重复记忆：相似摘要合并
    /// </summary>
    private async Task MergeDuplicateMemoriesAsync(TrpgScope scope, string characterId)
    {
        var allMemories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1000);
        var duplicates = new Dictionary<string, List<long>>();

        // 按摘要分组
        foreach (var memory in allMemories)
        {
            var normalizedSummary = NormalizeSummary(memory.Summary);
            if (!duplicates.ContainsKey(normalizedSummary))
                duplicates[normalizedSummary] = new List<long>();
            duplicates[normalizedSummary].Add(memory.Id);
        }

        // 合并重复项
        foreach (var (summary, ids) in duplicates.Where(x => x.Value.Count > 1))
        {
            var keepId = ids.First();
            var removeIds = ids.Skip(1).ToList();

            if (removeIds.Count > 0)
            {
                await _db.DeleteMemoryNodesAsync(scope, removeIds);
                _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Merged {removeIds.Count} duplicate memories, kept ID={keepId}");
            }
        }
    }

    /// <summary>
    /// 删除低价值 marker：importance < 0.3 且长期未使用
    /// </summary>
    private async Task DeleteLowValueMarkersAsync(TrpgScope scope, string characterId)
    {
        var allMemories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1000);
        var cutoffDate = DateTime.UtcNow.AddDays(-30); // 30 天未使用

        var lowValueIds = allMemories
            .Where(m => m.Importance < 0.3)
            .Where(m => (m.LastUsed ?? m.CreatedAt) < cutoffDate)
            .Select(m => (long)m.Id)
            .ToList();

        if (lowValueIds.Count > 0)
        {
            await _db.DeleteMemoryNodesAsync(scope, lowValueIds);
            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Deleted {lowValueIds.Count} low-value memories");
        }
    }

    /// <summary>
    /// 更新 confidence 衰减：基于折叠次数（已在 ComputeMemoryTypeWeight 中动态计算，此处无需操作）
    /// </summary>
    private async Task UpdateConfidenceDecayAsync(TrpgScope scope, string characterId)
    {
        // confidence 衰减现在基于团内时间（FoldCount），在检索时动态计算
        // 此方法保留为空，用于未来可能的批量更新需求
        await Task.CompletedTask;
    }

    /// <summary>
    /// 清理旧证据：evidence < 0.1 的记录删除
    /// </summary>
    private async Task CleanOldEvidenceAsync(TrpgScope scope, string characterId)
    {
        // 这里需要添加数据库方法来清理低证据记录
        // 暂时使用现有的衰减方法
        await _db.DecayBehaviorEvidenceAsync(scope, characterId, decayFactor: 0.8);
        _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Cleaned old behavior evidence");
    }

    private static string NormalizeSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return "";

        return summary
            .ToLower()
            .Replace(" ", "")
            .Replace("\n", "")
            .Replace("\r", "")
            .Replace("\t", "");
    }

    /// <summary>
    /// 重新计算所有事件的叙事引力
    /// 用于动态更新事件重要性
    /// </summary>
    private async Task RecalculateEventGravityAsync(TrpgScope scope, string characterId)
    {
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var updatedCount = 0;

        foreach (var evt in allEvents)
        {
            var weight = await _gravityEngine.CalculateGravityAsync(scope, characterId, evt);
            
            // TODO: 需要扩展 ChatDatabase 以支持存储 NarrativeWeight
            // 当前仅记录日志
            if (weight.NarrativeGravity > 0.7f)
            {
                updatedCount++;
                _context.Log(LogLevel.Debug, $"[AIMod:TRPG] 高引力事件: EventId={evt.EventId}, Gravity={weight.NarrativeGravity:F2}");
            }
        }

        if (updatedCount > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 重新计算事件引力完成，发现 {updatedCount} 个高引力事件");
        }
    }

    /// <summary>
    /// 创伤归并：归并创伤事件和清理低印象内容
    /// </summary>
    private async Task ConsolidateTraumasAsync(TrpgScope scope, string characterId)
    {
        try
        {
            var consolidatedCount = await _traumaConsolidationAgent.CheckAndConsolidateAsync(scope, characterId);
            if (consolidatedCount > 0)
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 创伤归并完成: 归并了 {consolidatedCount} 个关系");
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 创伤归并失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 应用关系衰减（基于事件折叠）
    /// </summary>
    private async Task ApplyRelationshipDecayAsync(TrpgScope scope, string characterId)
    {
        try
        {
            // 获取当前折叠计数
            var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
            if (memories.Count == 0) return;
            var currentFoldCount = memories[0].FoldCount;

            var allEntities = await _db.GetAllEntityCanonicalAsync(scope);
            var decayedCount = 0;

            foreach (var entity in allEntities)
            {
                foreach (var kvp in entity.Relationships)
                {
                    var rel = kvp.Value;
                    rel.ApplyDecay(currentFoldCount);
                    decayedCount++;
                }
                if (entity.Relationships.Count > 0)
                {
                    entity.LastUpdated = DateTime.UtcNow;
                    await _db.UpsertEntityCanonicalAsync(scope, entity);
                }
            }

            if (decayedCount > 0)
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 关系衰减完成: 处理了 {decayedCount} 个关系 (FoldCount={currentFoldCount})");
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 关系衰减失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 应用事实淡化（基于事件折叠）
    /// </summary>
    private async Task ApplyFactFadeAsync(TrpgScope scope, string characterId)
    {
        try
        {
            // 获取当前折叠计数
            var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
            if (memories.Count == 0) return;
            var currentFoldCount = memories[0].FoldCount;

            var allEntities = await _db.GetAllEntityCanonicalAsync(scope);
            var fadedCount = 0;
            var deactivatedCount = 0;

            foreach (var entity in allEntities)
            {
                foreach (var fact in entity.PersistentFacts)
                {
                    if (!fact.IsActive) continue;

                    var foldsSinceEstablished = currentFoldCount - fact.EstablishedFoldCount;
                    if (foldsSinceEstablished < 10) continue; // 10次折叠内不淡化

                    // 淡化逻辑：每10次折叠，显著性降低10%
                    var fadeFactor = Math.Pow(0.9, foldsSinceEstablished / 10.0);
                    fact.Salience *= fadeFactor;

                    // 如果显著性低于0.3，则停用
                    if (fact.Salience < 0.3)
                    {
                        fact.IsActive = false;
                        deactivatedCount++;
                    }
                    fadedCount++;
                }
                if (fadedCount > 0)
                {
                    entity.LastUpdated = DateTime.UtcNow;
                    await _db.UpsertEntityCanonicalAsync(scope, entity);
                }
            }

            if (fadedCount > 0)
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 事实淡化完成: 处理了 {fadedCount} 个事实，停用 {deactivatedCount} 个 (FoldCount={currentFoldCount})");
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 事实淡化失败: {ex.Message}");
        }
    }
}
