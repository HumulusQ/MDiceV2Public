using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 阵营枚举
    /// </summary>
    public enum Faction
    {
        Human, // 人类阵营
        Demon  // 魔王军阵营
    }

    /// <summary>
    /// 稀有度枚举（用于抽卡权重，不在游戏中显示）
    /// </summary>
    public enum Rarity
    {
        Common,    // 普通
        Rare,      // 稀有
        Epic,      // 史诗
        Legendary, // 传奇
        Named      // 具名（最高稀有度，用于带名称的特殊角色）
    }

    /// <summary>
    /// 场地类型枚举
    /// </summary>
    public enum FieldType
    {
        Front,
        Middle,
        Back
    }

    /// <summary>
    /// 特殊卡牌类型
    /// </summary>
    public enum SpecialCardType
    {
        Festival,
        Disaster,
        Weather
    }

    /// <summary>
    /// 技能参数项，用于在JSON中定义特定技能的参数
    /// </summary>
    public class SkillParameterEntry
    {
        [JsonPropertyName("skillId")]
        public string? SkillId { get; set; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 角色卡JSON数据结构
    /// </summary>
    public class CharacterCardData
    {
        /// <summary>
        /// 角色的唯一名称
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>
        /// 阵营列表
        /// </summary>
        [JsonPropertyName("factions")]
        public List<Faction> Factions { get; set; } = new List<Faction>();

        /// <summary>
        /// 稀有度（用于抽卡权重）
        /// </summary>
        [JsonPropertyName("rarity")]
        public Rarity Rarity { get; set; }

        /// <summary>
        /// 武力属性
        /// </summary>
        [JsonPropertyName("power")]
        public int Power { get; set; }

        /// <summary>
        /// 财力属性
        /// </summary>
        [JsonPropertyName("wealth")]
        public int Wealth { get; set; }

        /// <summary>
        /// 名声属性
        /// </summary>
        [JsonPropertyName("fame")]
        public int Fame { get; set; }

        /// <summary>
        /// 登场一次性属性加成（可选，用于覆盖默认行为）
        /// 示例格式: { "power": 3, "wealth": 0, "fame": 2 }
        /// </summary>
        [JsonPropertyName("entranceBonus")]
        public EntranceValues? EntranceBonus { get; set; }

        /// <summary>
        /// 每回合长期回复/贡献（可选，覆盖默认的Power/Wealth/Fame作为每回合贡献）
        /// 示例格式: { "power": 1, "wealth": 0, "fame": 0 }
        /// </summary>
        [JsonPropertyName("perTurnRecovery")]
        public PerTurnValues? PerTurnRecovery { get; set; }

        /// <summary>
        /// 固有技能列表（包含参数）
        /// </summary>
        [JsonPropertyName("innateSkills")]
        public List<SkillParameterEntry> InnateSkills { get; set; } = new List<SkillParameterEntry>();

        /// <summary>
        /// 场地倾向性（主要用于敌方AI）
        /// </summary>
        [JsonPropertyName("fieldPreference")]
        public FieldType FieldPreference { get; set; }

        /// <summary>
        /// 连携技能列表（包含参数）
        /// </summary>
        [JsonPropertyName("chainSkills")]
        public List<SkillParameterEntry> ChainSkills { get; set; } = new List<SkillParameterEntry>();

        /// <summary>
        /// 事件技能ID（包含参数）
        /// </summary>
        [JsonPropertyName("eventSkill")]
        public SkillParameterEntry? EventSkill { get; set; }

        /// <summary>
        /// 技能叙述文本映射（键格式：技能名_时机）
        /// </summary>
        [JsonPropertyName("skillNarratives")]
        public Dictionary<string, string> SkillNarratives { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 可选的标签字段，用于触发特殊效果（例如 "royal"、"undead" 等）。
        /// 对现有角色数据不做任何修改，若JSON中不存在该字段则为 null。
        /// </summary>
        [JsonPropertyName("tag")]
        public string? Tag { get; set; }

        /// <summary>
        /// 角色的帮助文本（包含角色描述、技能说明等）
        /// </summary>
        [JsonPropertyName("help")]
        public string? Help { get; set; }

        /// <summary>
        /// 角色标签列表（用于技能交互）
        /// </summary>
        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class EntranceValues
    {
        [JsonPropertyName("power")]
        public int Power { get; set; }

        [JsonPropertyName("wealth")]
        public int Wealth { get; set; }

        [JsonPropertyName("fame")]
        public int Fame { get; set; }
    }

    public class PerTurnValues
    {
        [JsonPropertyName("power")]
        public int Power { get; set; }

        [JsonPropertyName("wealth")]
        public int Wealth { get; set; }

        [JsonPropertyName("fame")]
        public int Fame { get; set; }
    }

    /// <summary>
    /// 特殊卡牌JSON数据结构
    /// </summary>
    public class SpecialCardData
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("factions")]
        public List<Faction> Factions { get; set; } = new List<Faction>();

        [JsonPropertyName("rarity")]
        public Rarity Rarity { get; set; }

        [JsonPropertyName("type")]
        public SpecialCardType Type { get; set; }

        [JsonPropertyName("innateSkills")]
        public List<SkillParameterEntry> InnateSkills { get; set; } = new List<SkillParameterEntry>();

        /// <summary>
        /// 技能叙述文本映射（键格式：技能名_时机）
        /// </summary>
        [JsonPropertyName("skillNarratives")]
        public Dictionary<string, string> SkillNarratives { get; set; } = new Dictionary<string, string>();

        [JsonPropertyName("effect")]
        public string? Effect { get; set; }

        /// <summary>
        /// 特殊卡牌的帮助文本（包含卡牌描述、效果说明等）
        /// </summary>
        [JsonPropertyName("help")]
        public string? Help { get; set; }
    }

    /// <summary>
    /// 角色卡集合数据结构
    /// </summary>
    public class CharacterCardsData
    {
        [JsonPropertyName("characters")]
        public List<CharacterCardData> Characters { get; set; } = new List<CharacterCardData>();
    }

    /// <summary>
    /// 特殊卡牌集合数据结构
    /// </summary>
    public class SpecialCardsData
    {
        [JsonPropertyName("specialCards")]
        public List<SpecialCardData> SpecialCards { get; set; } = new List<SpecialCardData>();
    }
}