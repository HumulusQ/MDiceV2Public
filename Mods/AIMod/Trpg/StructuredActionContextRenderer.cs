using System.Linq;
using System.Text;

namespace AIMod.Trpg;

public sealed class StructuredActionContextRenderer
{
    public string Render(TrpgAgentContextPack pack)
    {
        var sb = new StringBuilder();
        TrpgAgentContextPack.AppendLineBlock(sb, "当前场景", pack.CurrentSceneText);
        TrpgAgentContextPack.AppendTimeline(sb, "活跃时间线", pack.ActiveTimelineSkeleton.Take(6));
        TrpgAgentContextPack.AppendCharacterMemories(sb, "角色 IC 记忆", pack.CharacterICMemory.Take(5));
        TrpgAgentContextPack.AppendLineBlock(sb, "角色事实性认知", pack.FactualAwareness.Count == 0 ? "无" : string.Join("\n", pack.FactualAwareness.Take(8).Select(x => $"- {x}")));
        TrpgAgentContextPack.AppendEntities(sb, "当前实体", pack.PresentEntities.Take(4));
        TrpgAgentContextPack.AppendMemory(sb, "PL 桌面记忆（仅用于明知故演，不得作为 IC 行动依据）", pack.PlayerTableMemory.Take(3));
        TrpgAgentContextPack.AppendLineBlock(sb, "当前目标", pack.CurrentObjectives);
        TrpgAgentContextPack.AppendLineBlock(sb, "当前物品/稳定认知", pack.InventoryState);
        TrpgAgentContextPack.AppendLineBlock(sb, "当前情感框架", pack.AffectiveState);
        TrpgAgentContextPack.AppendLineBlock(sb, "未解决线索/最近尝试结果", pack.IdentityHints.Count == 0 ? "无" : string.Join("\n", pack.IdentityHints.Take(4)));
        TrpgAgentContextPack.AppendLineBlock(sb, "明知故演边界", "PL 桌面记忆只能帮助你避免玩家层面重复提问或演出迟疑；角色行动依据只能来自 GM 最新叙述、角色 IC 记忆、角色事实性认知和当前可感知场景。");
        TrpgAgentContextPack.AppendHistory(sb, "最近原文", pack.RecentActiveHistory.TakeLast(8));
        return sb.ToString().TrimEnd();
    }
}
