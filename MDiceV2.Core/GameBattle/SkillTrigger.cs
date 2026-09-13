// Copyright (c) 2025 MDiceV2
using MoonSharp.Interpreter;
using MDiceV2.Models;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 技能触发时机枚举
    /// </summary>
    public enum SkillTrigger
    {
        /// <summary>
        /// 角色登场时触发
        /// </summary>
        Entrance,

        /// <summary>
        /// 回合结束时触发
        /// </summary>
        TurnEnd,

        /// <summary>
        /// 在场技能（概率触发）
        /// </summary>
        Field,

        /// <summary>
        /// 连携技能
        /// </summary>
        Chain,

        /// <summary>
        /// 事件技能
        /// </summary>
        Event,

        /// <summary>
        /// 立即生效（特殊卡使用）
        /// </summary>
        Immediate
    }

    /// <summary>
    /// Lua技能类 - 在Lua中定义的技能实例
    /// </summary>
    [MoonSharpUserData]
    public class LuaSkill
    {
        /// <summary>
        /// 技能ID
        /// </summary>
        public string SkillId { get; set; }

        /// <summary>
        /// 技能名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 技能描述
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 触发时机
        /// </summary>
        public SkillTrigger Trigger { get; set; }

        /// <summary>
        /// Lua脚本中的技能函数名
        /// </summary>
        public string LuaFunctionName { get; set; }

        /// <summary>
        /// 技能参数（可选，用于初始化）
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// 关联的Lua脚本对象
        /// </summary>
        public Script LuaScript { get; set; }

        /// <summary>
        /// 执行技能
        /// </summary>
        public void Execute(ISkillContext context)
        {
            try
            {
                // Narrative text is managed inside Lua skill implementations.

                // 调用Lua中的技能函数
                var luaFunction = LuaScript.Globals.Get(LuaFunctionName);
                if (luaFunction.Type != DataType.Function)
                {
                    Log.Warn($"Lua function '{LuaFunctionName}' not found for skill '{SkillId}'");
                    context.LogMessage($"[技能系统] 错误: 技能函数 '{LuaFunctionName}' 未找到");
                    return;
                }

                // 传递上下文到Lua
                LuaScript.Call(luaFunction, context, this);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to execute Lua skill '{SkillId}': {ex.Message}");
                context.LogMessage($"[技能系统] 错误: 技能 {Name} 执行失败 - {ex.Message}");
            }
        }

        public override string ToString()
        {
            return $"{Name} ({SkillId})";
        }
    }

    /// <summary>
    /// 登场技能 - 角色被置入场中时激活，调整对应场地三维
    /// </summary>
    [MoonSharpUserData]
    public class EntranceSkill : LuaSkill
    {
        /// <summary>
        /// 调整的前场武力值
        /// </summary>
        public int FrontPowerBonus { get; set; } = 0;

        /// <summary>
        /// 调整的中场财力值
        /// </summary>
        public int MiddleWealthBonus { get; set; } = 0;

        /// <summary>
        /// 调整的后场名声值
        /// </summary>
        public int BackFameBonus { get; set; } = 0;

        public EntranceSkill()
        {
            Trigger = SkillTrigger.Entrance;
        }

        /// <summary>
        /// 创建指定调整值的登场技能
        /// </summary>
        public static EntranceSkill Create(string skillId, string name, int frontPower, int middleWealth, int backFame)
        {
            return new EntranceSkill
            {
                SkillId = skillId,
                Name = name,
                FrontPowerBonus = frontPower,
                MiddleWealthBonus = middleWealth,
                BackFameBonus = backFame,
                LuaFunctionName = "entrance_skill_adjust_field"
            };
        }
    }
}