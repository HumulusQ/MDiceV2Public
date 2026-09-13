using System.Collections.Generic;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 技能执行上下文
    /// </summary>
    public interface ISkillContext
    {
        GameState GameState { get; }
        Character? CurrentCharacter { get; }
        Character? OpponentCharacter { get; }
        Player CurrentPlayer { get; }
        Player OpponentPlayer { get; }
        void LogMessage(string message);
        int GetRandomInt(int min, int max);
        string GetSkillNarrative(string skillId, string trigger);

        // 供Lua技能访问的掷骰与抽卡接口（SkillExecutionContext实现）
        MDiceV2.Models.DiceResult RollDice(string expr);
        Card? DrawOneCardToCurrentPlayer();

        // 移除对方角色的接口
        bool RemoveCharacterFromOpponent(Character character, int fieldType);
        bool RemoveCharacterFromCurrentPlayer(Character character, int fieldType);

        // 场地效果接口
        bool SetFieldEffect(string tag, int intensity, int durationTurns);
        FieldEffect? GetCurrentFieldEffect();
        void RemoveFieldEffect();
    }

    /// <summary>
    /// 技能委托类型（兼容旧系统）
    /// </summary>
    public delegate void SkillAction(ISkillContext context);

    /// <summary>
    /// 代表一个角色，具有三维属性和技能
    /// </summary>
    public class Character
    {
        public string? Name { get; set; }
        public int Power { get; set; } // 武力 (legacy/base value)
        public int Wealth { get; set; } // 财力 (legacy/base value)
        public int Fame { get; set; } // 名声 (legacy/base value)
        // 不再使用登场一次性加成，相关逻辑已移除

        // 每回合长期回复/贡献（在EndTurn阶段按此数值累计到玩家Total*）
        public int PerTurnPower { get; set; } = 0;
        public int PerTurnWealth { get; set; } = 0;
        public int PerTurnFame { get; set; } = 0;

        /// <summary>
        /// 稀有度（用于显示星标）
        /// </summary>
        public Rarity Rarity { get; set; } = Rarity.Common;

        /// <summary>
        /// 场地偏向（主要用于敌方AI）
        /// </summary>
        public FieldType FieldPreference { get; set; }

        // Lua技能实例列表
        public List<LuaSkill> LuaSkills { get; set; } = new List<LuaSkill>();

        // 技能委托列表（兼容旧系统，逐步迁移）
        public List<SkillAction> Skills { get; set; } = new List<SkillAction>();

        /// <summary>
        /// 角色标签（用于技能交互）
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// 亡语技能委托（角色从场地移除时触发）
        /// </summary>
        public SkillAction? OnRemoved { get; set; }

        /// <summary>
        /// 获取指定触发时机的技能
        /// </summary>
        public IEnumerable<LuaSkill> GetSkillsByTrigger(SkillTrigger trigger)
        {
            return LuaSkills.Where(s => s.Trigger == trigger);
        }

        /// <summary>
        /// 添加Lua技能
        /// </summary>
        public void AddLuaSkill(LuaSkill skill)
        {
            LuaSkills.Add(skill);
        }

        // 登场加成方法已移除
    }

}