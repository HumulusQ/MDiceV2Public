using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// TimelineViewRenderer Agent
/// 将 TimelineNodes 格式化为可注入 prompt 的视图字符串
/// 触发时机：每次 TimelineWriter 或 SceneTransitionHandler 更新后
/// </summary>
public class TimelineViewRenderer
{
    private readonly ChatDatabase _db;
    private readonly IModContext _context;

    public TimelineViewRenderer(ChatDatabase db, IModContext context)
    {
        _db = db;
        _context = context;
    }

    /// <summary>
    /// 生成格式化的分层时间轴视图
    /// </summary>
    public async Task<string> RenderAsync(TrpgScope scope, string characterId)
    {
        var allVisible = await _db.GetVisibleTimelineNodesAsync(scope, characterId);
        if (allVisible.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("=== 故事至此 ===");
        RenderL0Section(sb, allVisible);

        sb.AppendLine();
        sb.AppendLine("=== 当前篇章 ===");
        RenderCurrentChapterSection(sb, allVisible);

        return sb.ToString().TrimEnd();
    }

    private static void RenderL0Section(StringBuilder sb, List<TimelineNode> allVisible)
    {
        var l0Nodes = allVisible.Where(n => n.Layer == TimelineLayer.L0).OrderBy(n => n.EventSequence).ToList();
        if (l0Nodes.Count == 0) return;

        for (int i = 0; i < l0Nodes.Count; i++)
        {
            var suffix = i == l0Nodes.Count - 1 ? " [当前篇章]" : "";
            sb.AppendLine($"· {l0Nodes[i].Content}{suffix}");
        }
    }

    private static void RenderCurrentChapterSection(StringBuilder sb, List<TimelineNode> allVisible)
    {
        var l1Nodes = allVisible.Where(n => n.Layer == TimelineLayer.L1).OrderBy(n => n.EventSequence).ToList();
        if (l1Nodes.Count == 0) return;

        foreach (var l1 in l1Nodes)
        {
            var isFolded = l1.Content.EndsWith("[已折叠]");
            var foreshadowSuffix = l1.Foreshadowing ? " [伏笔]" : "";
            sb.AppendLine($"▼ L1 {l1.Content}{foreshadowSuffix}");

            if (isFolded) continue;

            var l2Children = allVisible
                .Where(n => n.Layer == TimelineLayer.L2 && n.ParentId == l1.Id)
                .OrderBy(n => n.EventSequence)
                .ToList();

            foreach (var l2 in l2Children)
            {
                var l2Fore = l2.Foreshadowing ? " [伏笔]" : "";
                sb.AppendLine($"  ▼ L2 {l2.Content}{l2Fore}");

                var l3Children = allVisible
                    .Where(n => n.Layer == TimelineLayer.L3 && n.ParentId == l2.Id)
                    .OrderBy(n => n.EventSequence)
                    .ToList();

                foreach (var l3 in l3Children)
                {
                    var l3Fore = l3.Foreshadowing ? " [伏笔]" : "";
                    sb.AppendLine($"    · {l3.Content}{l3Fore}");
                }
            }
        }
    }
}
