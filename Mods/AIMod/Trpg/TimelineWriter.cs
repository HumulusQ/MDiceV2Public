using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// TimelineWriter Agent
/// 接收 InfoExtractor 输出，写入 TimelineNodes，匹配父节点
/// 触发合并阈值检查
/// </summary>
public class TimelineWriter
{
    private readonly ChatDatabase _db;
    private readonly IModContext _context;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly LlmCallTracker? _llmCallTracker;

    private const int L3_MERGE_THRESHOLD = 5;
    private const int L2_MERGE_THRESHOLD = 8;

    public TimelineWriter(
        ChatDatabase db,
        IModContext context,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        LlmCallTracker? llmCallTracker = null)
    {
        _db = db;
        _context = context;
        _apiCaller = apiCaller;
        _llmCallTracker = llmCallTracker;
    }

    /// <summary>
    /// 写入一批 InfoExtractor 提取的事件
    /// </summary>
    public async Task WriteAsync(TrpgScope scope, string characterId, string sceneId,
        List<TimelineEventExtraction> extractions)
    {
        var groupId = scope.GroupId;
        extractions = extractions
            .Select(CleanExtraction)
            .Where(extraction => extraction != null)
            .Select(extraction => extraction!)
            .Where(extraction => LooksLikeConcreteNarrativeContent(extraction.Content))
            .ToList();
        if (extractions.Count == 0) return;

        var seq = await _db.GetNextEventSequenceAsync(scope, characterId);
        var l1Nodes = await _db.GetTimelineNodesBySceneAsync(scope, characterId, sceneId, TimelineLayer.L1);
        var l2Nodes = await _db.GetTimelineNodesBySceneAsync(scope, characterId, sceneId, TimelineLayer.L2);

        var orphanL3s = new List<TimelineEventExtraction>();

        foreach (var extraction in extractions)
        {
            switch (extraction.Layer)
            {
                case TimelineLayer.L1:
                    var existingL1 = FindSimilarNode(l1Nodes, extraction.Content);
                    if (existingL1 != null)
                    {
                        _context.Log(LogLevel.Debug, $"[AIMod:TRPG] TimelineWriter: 跳过重复L1: {extraction.Content}");
                        break;
                    }
                    var l1Node = await InsertNodeAsync(scope, characterId, sceneId, extraction, seq++, null);
                    if (l1Node != null)
                        l1Nodes.Add(l1Node);
                    break;

                case TimelineLayer.L2:
                    var parentL1 = FindParentNode(l1Nodes, extraction.ParentKeywords);
                    parentL1 ??= l1Nodes
                        .Where(n => n.Status == TimelineNodeStatus.Visible)
                        .OrderBy(n => n.EventSequence)
                        .LastOrDefault();
                    if (parentL1 != null)
                    {
                        if (FindDuplicateSiblingNode(l2Nodes, parentL1.Id, extraction.Layer, extraction.Content) != null)
                            break;
                        var l2Node = await InsertNodeAsync(scope, characterId, sceneId, extraction, seq++, parentL1.Id);
                        if (l2Node != null)
                            l2Nodes.Add(l2Node);
                    }
                    else
                    {
                        var inferredL1 = await CreateInferredL1Async(scope, characterId, sceneId, extraction, seq++);
                        if (inferredL1 == null)
                            break;
                        l1Nodes.Add(inferredL1);
                        if (FindDuplicateSiblingNode(l2Nodes, inferredL1.Id, extraction.Layer, extraction.Content) != null)
                            break;
                        var l2Node = await InsertNodeAsync(scope, characterId, sceneId, extraction, seq++, inferredL1.Id);
                        if (l2Node != null)
                            l2Nodes.Add(l2Node);
                    }
                    break;

                case TimelineLayer.L3:
                    var parentL2 = FindParentNode(l2Nodes, extraction.ParentKeywords);
                    if (parentL2 != null)
                    {
                        var existingChildren = await _db.GetTimelineChildNodesAsync(scope, parentL2.Id);
                        if (FindDuplicateSiblingNode(existingChildren, parentL2.Id, extraction.Layer, extraction.Content) == null)
                            await InsertNodeAsync(scope, characterId, sceneId, extraction, seq++, parentL2.Id);
                    }
                    else
                    {
                        orphanL3s.Add(extraction);
                    }
                    break;
            }
        }

        // 处理 orphan L3（每 3 条尝试组成一个 L2）
        seq = await ProcessOrphanL3sAsync(scope, characterId, sceneId, orphanL3s, l1Nodes, seq);

        // 检查合并阈值
        await CheckMergeThresholdsAsync(scope, characterId, sceneId);

        _context.Log(LogLevel.Info, $"[AIMod:TRPG] TimelineWriter: 写入 {extractions.Count} 条事件 (Scene={sceneId})");
    }

    private async Task<int> ProcessOrphanL3sAsync(TrpgScope scope, string characterId, string sceneId,
        List<TimelineEventExtraction> orphans, List<TimelineNode> l1Nodes, int seq)
    {
        if (orphans.Count == 0) return seq;

        if (orphans.Count >= 3)
        {
            var keywords = string.Join(" ", orphans.SelectMany(o => o.ParentKeywords.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().Take(5));
            var parentL1 = FindParentNode(l1Nodes, keywords);
            if (parentL1 == null && l1Nodes.Count > 0)
                parentL1 = l1Nodes.Last();

            if (parentL1 != null)
            {
                var inferredL2Content = await InferL2SummaryAsync(scope, characterId, orphans);
                var inferredL2 = CleanExtraction(new TimelineEventExtraction
                {
                    Layer = TimelineLayer.L2,
                    Content = inferredL2Content,
                    Importance = orphans.Max(o => o.Importance)
                });
                if (inferredL2 == null)
                    return seq;

                if (FindDuplicateSiblingNode(await _db.GetTimelineChildNodesAsync(scope, parentL1.Id), parentL1.Id, TimelineLayer.L2, inferredL2.Content) != null)
                    return seq;

                var l2 = await InsertNodeAsync(scope, characterId, sceneId, inferredL2,
                    seq++, parentL1.Id);
                if (l2 == null)
                    return seq;

                foreach (var orphan in orphans)
                    await InsertNodeAsync(scope, characterId, sceneId, orphan, seq++, l2.Id);
            }
        }
        else
        {
            var l1 = l1Nodes.LastOrDefault();
            if (l1 != null)
                foreach (var orphan in orphans)
                    await InsertNodeAsync(scope, characterId, sceneId, orphan, seq++, l1.Id);
        }
        return seq;
    }

    private async Task CheckMergeThresholdsAsync(TrpgScope scope, string characterId, string sceneId)
    {
        var l2Nodes = await _db.GetTimelineNodesBySceneAsync(scope, characterId, sceneId, TimelineLayer.L2);

        foreach (var l2 in l2Nodes.Where(n => n.Status == TimelineNodeStatus.Visible))
        {
            var children = await _db.GetTimelineChildNodesAsync(scope, l2.Id);
            var visibleL3 = children.Where(c => c.Layer == TimelineLayer.L3 && c.Status == TimelineNodeStatus.Visible).ToList();

            if (visibleL3.Count >= L3_MERGE_THRESHOLD)
                await MergeL3NodesAsync(scope, l2.Id, visibleL3);
        }

        var l1Nodes = await _db.GetTimelineNodesBySceneAsync(scope, characterId, sceneId, TimelineLayer.L1);

        foreach (var l1 in l1Nodes.Where(n => n.Status == TimelineNodeStatus.Visible))
        {
            var children = await _db.GetTimelineChildNodesAsync(scope, l1.Id);
            var visibleL2 = children.Where(c => c.Layer == TimelineLayer.L2 && c.Status == TimelineNodeStatus.Visible).ToList();

            if (visibleL2.Count >= L2_MERGE_THRESHOLD)
                await MergeL2NodesAsync(scope, l1.Id, visibleL2);
        }
    }

    private async Task MergeL3NodesAsync(TrpgScope scope, string parentL2Id, List<TimelineNode> l3Nodes)
    {
        var parent = await _db.GetTimelineNodeByIdAsync(scope, parentL2Id);
        if (parent == null) return;

        var contents = l3Nodes.Select(n => n.Content).ToList();
        var merged = await CompressManyAsync(scope, parent.CharacterId, contents, "动作");
        var existingChildren = await _db.GetTimelineChildNodesAsync(scope, parentL2Id);
        foreach (var (content, seq) in merged.Select((c, i) => (c, i)))
        {
            if (FindDuplicateSiblingNode(existingChildren, parentL2Id, TimelineLayer.L3, content) != null)
                continue;

            var node = new TimelineNode
            {
                GroupId = parent.GroupId, CharacterId = parent.CharacterId,
                Layer = TimelineLayer.L3, Content = content, ParentId = parentL2Id,
                SceneId = parent.SceneId, Importance = l3Nodes.Max(n => n.Importance),
                EventSequence = l3Nodes.Max(n => n.EventSequence) + seq + 1
            };
            await _db.InsertTimelineNodeAsync(scope, node);
            existingChildren.Add(node);
        }
        await _db.BulkUpdateTimelineNodeStatusAsync(scope, l3Nodes.Select(n => n.Id), TimelineNodeStatus.Archived);
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] TimelineWriter: L3 合并 {l3Nodes.Count} → {merged.Count} 条");
    }

    private async Task MergeL2NodesAsync(TrpgScope scope, string parentL1Id, List<TimelineNode> l2Nodes)
    {
        var grouped = l2Nodes.Chunk(3).ToList();
        foreach (var group in grouped.Where(g => g.Length > 1))
        {
            var parent = await _db.GetTimelineNodeByIdAsync(scope, parentL1Id);
            if (parent == null) continue;

            var contents = group.Select(n => n.Content).ToList();
            var merged = await CompressManyAsync(scope, parent.CharacterId, contents, "场景动作");
            var existingChildren = await _db.GetTimelineChildNodesAsync(scope, parentL1Id);
            foreach (var (content, seq) in merged.Select((c, i) => (c, i)))
            {
                if (FindDuplicateSiblingNode(existingChildren, parentL1Id, TimelineLayer.L2, content) != null)
                    continue;

                var node = new TimelineNode
                {
                    GroupId = parent.GroupId, CharacterId = parent.CharacterId,
                    Layer = TimelineLayer.L2, Content = content, ParentId = parentL1Id,
                    SceneId = parent.SceneId, Importance = group.Max(n => n.Importance),
                    EventSequence = group.Max(n => n.EventSequence) + seq + 1
                };
                await _db.InsertTimelineNodeAsync(scope, node);
                existingChildren.Add(node);
            }
            await _db.BulkUpdateTimelineNodeStatusAsync(scope, group.Select(n => n.Id), TimelineNodeStatus.Archived);
        }
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] TimelineWriter: L2 合并 {l2Nodes.Count} 条");
    }

    private async Task<List<string>> CompressManyAsync(TrpgScope scope, string characterId, List<string> items, string context)
    {
        if (items.Count <= 2) return items;
        try
        {
            var prompt = $"请将以下 {items.Count} 条TRPG叙事{context}合并精炼为 2-3 条更简洁的描述，保留最重要的信息：\n" +
                         string.Join("\n", items.Select((s, i) => $"{i + 1}. {s}")) +
                         "\n\n要求：必须写具体剧情、具体结果或明确状态变化；禁止输出“等待反应”“继续行动”“全员可行动阶段”“场景推进”等低信息流程句。\n请直接输出合并后的描述，每行一条：";
            var messages = new List<ChatMessage>
            {
                new("system", $"{AimodPromptPrefixes.BackendCommonPrefixV1}\n\n你是TRPG时间轴压缩助手。只压缩已给出的桌面事件，不补充未确认事实，不替GM判定。"),
                new("user", prompt)
            };
            var response = await CallTrackedAsync(scope, characterId, messages, "TimelineWriter", "TimelineNodeCompression");
            if (!string.IsNullOrWhiteSpace(response))
                return response.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => TimelineContentCleaner.Clean(l.TrimStart('1', '2', '3', '4', '5', '.', ' ', '、').Trim()))
                    .Where(LooksLikeConcreteNarrativeContent)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3).ToList();
        }
        catch { }
        return items
            .Select(TimelineContentCleaner.Clean)
            .Where(LooksLikeConcreteNarrativeContent)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(2)
            .ToList();
    }

    private async Task<string> InferL2SummaryAsync(TrpgScope scope, string characterId, List<TimelineEventExtraction> orphans)
    {
        var contents = orphans.Select(o => o.Content).ToList();
        var summaries = await CompressManyAsync(scope, characterId, contents, "行动");
        return TimelineContentCleaner.Clean(
            summaries.FirstOrDefault(LooksLikeConcreteNarrativeContent)
            ?? contents.FirstOrDefault(LooksLikeConcreteNarrativeContent)
            ?? string.Join("；", contents.Take(2)));
    }

    private async Task<TimelineNode?> CreateInferredL1Async(TrpgScope scope, string characterId, string sceneId,
        TimelineEventExtraction l2Extraction, int seq)
    {
        var content = $"[{sceneId}] {l2Extraction.Content.Split('，').First()}";
        return await InsertNodeAsync(scope, characterId, sceneId,
            new TimelineEventExtraction { Layer = TimelineLayer.L1, Content = content, Importance = l2Extraction.Importance, Foreshadowing = l2Extraction.Foreshadowing },
            seq, null);
    }

    private async Task<TimelineNode?> CreateSceneRootL1Async(TrpgScope scope, string characterId, string sceneId, int seq)
    {
        return await InsertNodeAsync(scope, characterId, sceneId,
            new TimelineEventExtraction
            {
                Layer = TimelineLayer.L1,
                Content = $"[{sceneId}] 场景推进",
                Importance = 4,
                Foreshadowing = false
            },
            seq,
            null);
    }

    private static TimelineNode? FindParentNode(List<TimelineNode> candidates, string keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords) || candidates.Count == 0) return null;
        var kws = keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return candidates
            .Where(n => n.Status == TimelineNodeStatus.Visible)
            .Select(n => (Node: n, Score: kws.Count(kw => n.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Node)
            .FirstOrDefault();
    }

    private async Task<string?> CallTrackedAsync(TrpgScope scope, string characterId, List<ChatMessage> messages, string agentName, string requestKind)
    {
        if (_llmCallTracker != null)
            return await _llmCallTracker.CallAsync(scope, characterId, messages, agentName, requestKind, _apiCaller);

        return await _apiCaller.Invoke(messages);
    }

    public static bool LooksLikeConcreteNarrativeContent(string? text)
    {
        var cleanedText = TimelineContentCleaner.Clean(text);
        if (string.IsNullOrWhiteSpace(cleanedText))
            return false;

        var cleaned = new string(cleanedText
            .Where(ch => !char.IsWhiteSpace(ch))
            .ToArray());
        if (cleaned.Length < 4)
            return false;

        var vagueMarkers = new[]
        {
            "全员可行动阶段",
            "等待反应",
            "继续行动",
            "所有人可以行动",
            "场景推进",
            "叙事事件",
            "事件发生"
        };
        if (vagueMarkers.Any(token => cleaned.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return false;

        var referents = new[]
        {
            "你", "他", "她", "它", "他们", "她们", "我", "自己", "GM", "玩家", "NPC", "角色", "队友", "同伴", "敌人", "对手",
            "老人", "女人", "男人", "少女", "少年", "孩子", "警察", "医生", "老板", "司机", "研究员", "怪物", "尸体",
            "门", "窗", "房间", "走廊", "大厅", "街道", "巷子", "楼梯", "车", "车辆", "箱子", "包", "桌", "椅", "书", "信",
            "文件", "地图", "钥匙", "武器", "刀", "枪", "血", "伤口", "火", "灯", "镜子", "电话", "手机", "电脑", "屏幕",
            "入口", "出口", "目标", "任务", "危险", "威胁", "线索", "真相", "关系"
        };

        var changeMarkers = new[]
        {
            "走", "跑", "看", "听", "说", "问", "拿", "抓", "推", "拉", "打开", "关上", "进入", "离开", "发现", "看到",
            "听到", "触碰", "攻击", "击中", "倒下", "站起", "躲", "转身", "追", "交给", "递给", "放下", "收起", "失去",
            "获得", "变成", "变得", "恢复", "受伤", "暴露", "揭示", "确认", "完成", "放弃", "失败", "成功", "导致", "改变", "升级"
        };

        return referents.Any(marker => cleaned.Contains(marker, StringComparison.OrdinalIgnoreCase))
               && changeMarkers.Any(marker => cleaned.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static TimelineNode? FindSimilarNode(List<TimelineNode> candidates, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || candidates.Count == 0)
            return null;

        return candidates
            .Where(n => n.Status == TimelineNodeStatus.Visible)
            .FirstOrDefault(n => TimelineContentCleaner.AreNearDuplicates(n.Content, content));
    }

    private static TimelineNode? FindDuplicateSiblingNode(
        IEnumerable<TimelineNode> candidates,
        string? parentId,
        TimelineLayer layer,
        string content)
    {
        return candidates
            .Where(n => n.Status == TimelineNodeStatus.Visible)
            .Where(n => n.Layer == layer)
            .Where(n => string.Equals(n.ParentId ?? "", parentId ?? "", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(n => TimelineContentCleaner.AreNearDuplicates(n.Content, content));
    }

    private static TimelineEventExtraction? CleanExtraction(TimelineEventExtraction? extraction)
    {
        if (extraction == null)
            return null;

        var cleanedContent = TimelineContentCleaner.Clean(extraction.Content);
        if (string.IsNullOrWhiteSpace(cleanedContent))
            return null;

        extraction.Content = cleanedContent;
        extraction.ParentKeywords = TimelineContentCleaner.Clean(extraction.ParentKeywords);
        return extraction;
    }

    private async Task<TimelineNode?> InsertNodeAsync(TrpgScope scope, string characterId, string sceneId,
        TimelineEventExtraction extraction, int seq, string? parentId)
    {
        var cleanedExtraction = CleanExtraction(extraction);
        if (cleanedExtraction == null)
            return null;

        extraction = cleanedExtraction;
        var groupId = scope.GroupId;
        var node = new TimelineNode
        {
            GroupId = groupId, CharacterId = characterId, Layer = extraction.Layer,
            Content = extraction.Content, ParentId = parentId, SceneId = sceneId,
            Importance = extraction.Importance, Foreshadowing = extraction.Foreshadowing,
            EventSequence = seq
        };

        var siblingCandidates = string.IsNullOrWhiteSpace(parentId)
            ? await _db.GetTimelineNodesBySceneAsync(scope, characterId, sceneId, extraction.Layer)
            : await _db.GetTimelineChildNodesAsync(scope, parentId);
        if (FindDuplicateSiblingNode(siblingCandidates, parentId, extraction.Layer, extraction.Content) != null)
            return null;

        await _db.InsertTimelineNodeAsync(scope, node);
        return node;
    }
}
