using System.Collections.Generic;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 卡牌类型枚举
    /// </summary>
    public enum CardType
    {
        Character, // 角色卡
        Special // 特殊卡（节日、天气、天灾等）
    }

    /// <summary>
    /// 卡牌基类
    /// </summary>
    public abstract class Card
    {
        public string? Name { get; set; }
        public CardType Type { get; set; }
        public int DrawWeight { get; set; } = 1; // 抽卡权重，自然数
        public List<Faction> Factions { get; set; } = new List<Faction>(); // 所属阵营列表
    }

    /// <summary>
    /// 角色卡
    /// </summary>
    public class CharacterCard : Card
    {
        public Character? Character { get; set; }

        public CharacterCard()
        {
            Type = CardType.Character;
        }
    }

    /// <summary>
    /// 特殊卡
    /// </summary>
    public class SpecialCard : Card
    {
        public SpecialCardType SpecialType { get; set; }
        public string? Effect { get; set; } // 效果描述
        
        // 新增：特殊卡的立即技能
        public LuaSkill? ImmediateSkill { get; set; }

        public SpecialCard()
        {
            Type = CardType.Special;
        }
    }
}