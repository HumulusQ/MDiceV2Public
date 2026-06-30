using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// SceneTransitionHandler Agent
/// 场景切换时归档旧场景，触发 L1/L0 合并
/// 触发时机：GM 宣告新场景
/// </summary>
public class SceneTransitionHandler
{
    private readonly ChatDatabase _db;
    private readonly IModContext _context;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly ArchiveToGraph _archiveToGraph;
    private readonly LlmCallTracker? _llmCallTracker;

    private const int L1_MERGE_TRIGGER = 15;
    private const int L0_MERGE_TRIGGER = 10;
    private const int L1_MERGE_TARGET  = 10;
    private const int L0_MERGE_TARGET  = 7;

    public SceneTransitionHandler(ChatDatabase db, IModContext context,
        Func<List<ChatMessage>, Task<string?>> apiCaller, ArchiveToGraph archiveToGraph,
        LlmCallTracker? llmCallTracker = null)
    {
        _db = db;
        _context = context;
        _apiCaller = apiCaller;
        _archiveToGraph = archiveToGraph;
        _llmCallTracker = llmCallTracker;
    }

    /// <summary>
    /// 处理场景切换：归档旧场景，按需合并 L1→L0 / L0→L0
    /// </summary>
    public async Task HandleSceneTransitionAsync(TrpgScope scope, string characterId, string oldSceneId)
    {
        var groupId = scope.GroupId;
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] SceneTransitionHandler: 处理场景切换 '{oldSceneId}'");

        // 1. 归档旧场景 L3 / L2
        var oldL3 = await _db.GetTimelineNodesBySceneAsync(scope, characterId, oldSceneId, TimelineLayer.L3);
        var oldL2 = await _db.GetTimelineNodesBySceneAsync(scope, characterId, oldSceneId, TimelineLayer.L2);
        var toArchive = oldL3.Concat(oldL2).Where(n => n.Status == TimelineNodeStatus.Visible).Select(n => n.Id).ToList();
        await _db.BulkUpdateTimelineNodeStatusAsync(scope, toArchive, TimelineNodeStatus.Archived);

        // 2. 旧场景 L1 标记 [已折叠]
        var oldL1 = await _db.GetTimelineNodesBySceneAsync(scope, characterId, oldSceneId, TimelineLayer.L1);
        foreach (var node in oldL1.Where(n => n.Status == TimelineNodeStatus.Visible && !n.Content.EndsWith("[已折叠]")))
            await _db.UpdateTimelineNodeContentAsync(scope, node.Id, node.Content + " [已折叠]");

        // 3. L1 数量 ≥ 15 → 触发 L1→L0 合并
        var visibleL1Count = await _db.CountTimelineNodesByLayerAsync(scope, characterId, TimelineLayer.L1, TimelineNodeStatus.Visible);
        if (visibleL1Count >= L1_MERGE_TRIGGER)
            await MergeL1ToL0Async(scope, characterId);

        // 4. L0 数量 ≥ 10 → 触发 L0→L0 合并
        var visibleL0Count = await _db.CountTimelineNodesByLayerAsync(scope, characterId, TimelineLayer.L0, TimelineNodeStatus.Visible);
        if (visibleL0Count >= L0_MERGE_TRIGGER)
            await MergeL0Async(scope, characterId);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] SceneTransitionHandler: 场景 '{oldSceneId}' 归档完成");
    }

    private async Task MergeL1ToL0Async(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        var allL1 = await _db.GetTimelineNodesByLayerAsync(scope, characterId, TimelineLayer.L1);

        // 取最旧的若干条 L1（含 [已折叠] 标记），每 3-5 条合并为 1 条 L0
        var toProcess = allL1.OrderBy(n => n.EventSequence).ToList();
        var processed = 0;

        while (toProcess.Count - processed >= 3 && await _db.CountTimelineNodesByLayerAsync(scope, characterId, TimelineLayer.L1, TimelineNodeStatus.Visible) > L1_MERGE_TARGET)
        {
            var batch = toProcess.Skip(processed).Take(4).ToList();
            if (batch.Count < 3) break;

            // 转存至 Graph
            await _archiveToGraph.ArchiveNodesAsync(scope, characterId, batch);

            // 合并 L1 → L0
            var l0Content = await SummarizeToArcAsync(scope, characterId, batch);
            var maxSeq = batch.Max(n => n.EventSequence);
            var l0 = new TimelineNode
            {
                GroupId = groupId, CharacterId = characterId,
                Layer = TimelineLayer.L0, Content = l0Content, ParentId = null,
                SceneId = batch.Last().SceneId, Importance = batch.Max(n => n.Importance),
                EventSequence = maxSeq + 1
            };
            await _db.InsertTimelineNodeAsync(scope, l0);

            // 归档原 L1 及其子节点
            foreach (var l1Node in batch)
            {
                await _db.UpdateTimelineNodeStatusAsync(scope, l1Node.Id, TimelineNodeStatus.Archived);
                var children = await _db.GetTimelineChildNodesAsync(scope, l1Node.Id);
                await _db.BulkUpdateTimelineNodeStatusAsync(scope, children.Select(c => c.Id), TimelineNodeStatus.Archived);
            }

            _context.Log(LogLevel.Info, $"[AIMod:TRPG] L1→L0 合并: {batch.Count} 条 L1 → \"{l0Content}\"");
            processed += batch.Count;
        }
    }

    private async Task MergeL0Async(TrpgScope scope, string characterId)
    {
        var groupId = scope.GroupId;
        var allL0 = await _db.GetTimelineNodesByLayerAsync(scope, characterId, TimelineLayer.L0);
        var toProcess = allL0.OrderBy(n => n.EventSequence).ToList();

        if (toProcess.Count < 3) return;

        var batch = toProcess.Take(4).ToList();
        var newContent = await SummarizeToArcAsync(scope, characterId, batch);
        var maxSeq = batch.Max(n => n.EventSequence);

        var newL0 = new TimelineNode
        {
            GroupId = groupId, CharacterId = characterId,
            Layer = TimelineLayer.L0, Content = newContent,
            SceneId = batch.Last().SceneId, Importance = batch.Max(n => n.Importance),
            EventSequence = maxSeq + 1
        };
        await _db.InsertTimelineNodeAsync(scope, newL0);
        await _db.BulkUpdateTimelineNodeStatusAsync(scope, batch.Select(n => n.Id), TimelineNodeStatus.Archived);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] L0→L0 合并: {batch.Count} 条 → \"{newContent}\"");
    }

    private async Task<string> SummarizeToArcAsync(TrpgScope scope, string characterId, List<TimelineNode> nodes)
    {
        var contents = nodes.Select(n => n.Content.Replace(" [已折叠]", "")).ToList();
        var prompt = $"请将以下 {contents.Count} 个TRPG场景事件概括为一句话（单句散文描述，不超过30字），概括这段叙事弧的核心内容：\n" +
                     string.Join("\n", contents.Select((c, i) => $"- {c}")) +
                     "\n\n请直接输出一句话概括：";
        try
        {
            var messages = new List<ChatMessage>
            {
                new("system", $"{AimodPromptPrefixes.BackendCommonPrefixV1}\n\n你是TRPG场景弧压缩助手。只概括已给出的桌面事件，不补充未确认事实，不替GM判定。"),
                new("user", prompt)
            };
            var response = await (_llmCallTracker ?? throw new InvalidOperationException("LlmCallTracker is required for AIMod LLM calls."))
                .CallAsync(scope, characterId, messages, "SceneTransitionHandler", "SceneArcCompression", _apiCaller);
            if (!string.IsNullOrWhiteSpace(response))
                return response.Trim().Split('\n').First().Trim();
        }
        catch { }
        return string.Join("→", contents.Take(2).Select(c => c.Split('，').First()));
    }
}
