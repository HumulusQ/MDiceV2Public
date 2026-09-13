using System.Linq;
using System.Text;

namespace AIMod.Trpg;

public sealed class StructuredActionContextRenderer
{
    public string Render(TrpgAgentContextPack pack)
    {
        var sb = new StringBuilder();
        TrpgAgentContextPack.AppendLineBlock(sb, "当前场景", pack.CurrentSceneText);
        TrpgAgentContextPack.AppendLineBlock(sb, "本轮联想记忆", pack.GraphRecallEvidence);
        TrpgAgentContextPack.AppendLineBlock(sb, "即时心理活动", pack.ThoughtText);
        TrpgAgentContextPack.AppendLineBlock(sb, "即时情感叙述", pack.EmotionText);
        TrpgAgentContextPack.AppendLineBlock(sb, "当前物品/稳定认知", pack.InventoryState);
        TrpgAgentContextPack.AppendHistory(sb, "最近原文", pack.RecentActiveHistory.TakeLast(8));
        TrpgAgentContextPack.AppendLineBlock(
            sb,
            "边界",
            "联想记忆和即时内心状态只影响行动倾向，不替代 GM 最新叙述；弱联想不代表身份确认。");
        return sb.ToString().TrimEnd();
    }
}
