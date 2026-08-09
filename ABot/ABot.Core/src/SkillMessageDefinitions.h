/**
 * @file SkillMessageDefinitions.h
 * @brief 技能系统的15种消息类型定义
 * 
 * 定义所有15种技能类型接受的消息参数
 * 用于消息验证和脚本参数注入
 */

#ifndef ABOT_SKILL_MESSAGE_DEFINITIONS_H
#define ABOT_SKILL_MESSAGE_DEFINITIONS_H

#include "SkillTriggerSystem.h"
#include <map>
#include <vector>

namespace abot {

/**
 * @brief 技能消息签名库 - 定义所有15种技能的消息格式
 * 
 * 这个类负责初始化和管理所有技能的消息签名定义
 */
class SkillMessageDefinitions {
public:
    /**
     * @brief 初始化所有15种技能的消息签名
     * 应在系统启动时调用
     */
    static void InitializeAllSignatures();
    
private:
    /**
     * @brief 创建 ActSkill 的消息签名
     * 主动技能 - 无消息参数
     */
    static SkillMessageSignature CreateActSkill() {
        SkillMessageSignature sig("ActSkill");
        return sig;
    }
    
    /**
     * @brief 创建 OnTurnStart / OnTurnStartSkill 的消息签名
     * 回合开始触发 - 无消息参数
     */
    static SkillMessageSignature CreateOnTurnStart() {
        SkillMessageSignature sig("OnTurnStart");
        return sig;
    }
    
    /**
     * @brief 创建 OnTurnEnd / OnTurnEndSkill 的消息签名
     * 回合结束触发 - 无消息参数
     * 注：代码中可能拼写为 OnTrunEndSkill，需要同时注册两个版本
     */
    static SkillMessageSignature CreateOnTurnEnd() {
        SkillMessageSignature sig("OnTurnEnd");
        return sig;
    }
    
    /**
     * @brief 创建 OnAttackerShifted 的消息签名
     * 攻击权转移时触发
     * 消息参数：From (原攻击手), To (新攻击手)
     */
    static SkillMessageSignature CreateOnAttackerShifted() {
        SkillMessageSignature sig("OnAttackerShifted");
        sig.AddParam(MessageParamDef("From", MessageParamType::STRING, true, "原攻击手名称"));
        sig.AddParam(MessageParamDef("To", MessageParamType::STRING, true, "新攻击手名称"));
        return sig;
    }
    
    /**
     * @brief 创建 onHitDealt / onHitDealtSkill 的消息签名
     * 攻击者成功命中目标时触发
     * 消息参数：Name (目标名称)
     */
    static SkillMessageSignature CreateOnHitDealt() {
        SkillMessageSignature sig("onHitDealt");
        sig.AddParam(MessageParamDef("Name", MessageParamType::STRING, true, "目标名称"));
        return sig;
    }
    
    /**
     * @brief 创建 onHitTaken / onHitTakenSkill 的消息签名
     * 目标被命中时触发
     * 消息参数：Name (攻击来源名称)
     */
    static SkillMessageSignature CreateOnHitTaken() {
        SkillMessageSignature sig("onHitTaken");
        sig.AddParam(MessageParamDef("Name", MessageParamType::STRING, true, "攻击者名称"));
        return sig;
    }
    
    /**
     * @brief 创建 onDamageDealt / onDamageDealtSkill 的消息签名
     * 攻击者造成伤害时触发
     * 消息参数：Name (目标), Dmg (伤害值), Tag (伤害类型)
     */
    static SkillMessageSignature CreateOnDamageDealt() {
        SkillMessageSignature sig("onDamageDealt");
        sig.AddParam(MessageParamDef("Name", MessageParamType::STRING, true, "目标名称"));
        sig.AddParam(MessageParamDef("Dmg", MessageParamType::INT, true, "伤害值"));
        sig.AddParam(MessageParamDef("Tag", MessageParamType::STRING, false, "伤害类型标签"));
        return sig;
    }
    
    /**
     * @brief 创建 onDamageTaken / onDamageTakenSkill 的消息签名
     * 目标受到伤害时触发（最常用的被动技能）
     * 消息参数：Source (攻击来源), Dmg (伤害值), Tag (伤害类型)
     */
    static SkillMessageSignature CreateOnDamageTaken() {
        SkillMessageSignature sig("onDamageTaken");
        sig.AddParam(MessageParamDef("Source", MessageParamType::STRING, true, "攻击来源名称"));
        sig.AddParam(MessageParamDef("Dmg", MessageParamType::INT, true, "伤害值"));
        sig.AddParam(MessageParamDef("Tag", MessageParamType::STRING, false, "伤害类型标签"));
        return sig;
    }
    
    /**
     * @brief 创建 onAttack / onAttackSkill 的消息签名
     * 攻击者发动攻击时触发
     * 消息参数：Name (目标名称)
     */
    static SkillMessageSignature CreateOnAttack() {
        SkillMessageSignature sig("onAttack");
        sig.AddParam(MessageParamDef("Name", MessageParamType::STRING, true, "目标名称"));
        return sig;
    }
    
    /**
     * @brief 创建 onHeal / onHealSkill 的消息签名
     * 目标受到治疗时触发
     * 消息参数：Name (治疗来源), value (治疗值)
     */
    static SkillMessageSignature CreateOnHeal() {
        SkillMessageSignature sig("onHeal");
        sig.AddParam(MessageParamDef("Name", MessageParamType::STRING, true, "治疗来源名称"));
        sig.AddParam(MessageParamDef("value", MessageParamType::INT, true, "治疗值"));
        return sig;
    }
    
    /**
     * @brief 创建 onUnitAttack / onUnitAttackSkill 的消息签名
     * 场上其他单位发动攻击时触发（观察者视角）
     * 消息参数：Name (目标), Source (攻击者)
     */
    static SkillMessageSignature CreateOnUnitAttack() {
        SkillMessageSignature sig("onUnitAttack");
        sig.AddParam(MessageParamDef("Name", MessageParamType::STRING, true, "目标名称"));
        sig.AddParam(MessageParamDef("Source", MessageParamType::STRING, true, "攻击者名称"));
        return sig;
    }
    
    /**
     * @brief 创建 onUnitAttacked / onUnitAttackedSkill 的消息签名
     * 场上其他单位被攻击时触发（观察者视角）
     * 消息参数：Name (被攻击者), Source (攻击者)
     */
    static SkillMessageSignature CreateOnUnitAttacked() {
        SkillMessageSignature sig("onUnitAttacked");
        sig.AddParam(MessageParamDef("Name", MessageParamType::STRING, true, "被攻击者名称"));
        sig.AddParam(MessageParamDef("Source", MessageParamType::STRING, true, "攻击者名称"));
        return sig;
    }
    
    /**
     * @brief 创建 OnDead / OnDeadSkill 的消息签名
     * 角色死亡时触发
     * 消息参数：Name (死亡者)
     */
    static SkillMessageSignature CreateOnDead() {
        SkillMessageSignature sig("OnDead");
        sig.AddParam(MessageParamDef("Name", MessageParamType::STRING, true, "死亡者名称"));
        return sig;
    }
    
    /**
     * @brief 创建 OnSkillTriggler / OnSkillTrigglerSkill 的消息签名
     * 监察其他技能被触发时触发（需防止无限递归）
     * 消息参数：Skillname (技能名), Owner (归属者), Skilltype (技能类型)
     */
    static SkillMessageSignature CreateOnSkillTriggler() {
        SkillMessageSignature sig("OnSkillTriggler");
        sig.AddParam(MessageParamDef("Skillname", MessageParamType::STRING, true, "被触发的技能名称"));
        sig.AddParam(MessageParamDef("Owner", MessageParamType::STRING, true, "技能归属者名称"));
        sig.AddParam(MessageParamDef("Skilltype", MessageParamType::STRING, true, "技能类型"));
        return sig;
    }
    
    /**
     * @brief 创建 Advance 的消息签名
     * 推进回合 - 无消息参数
     * 特殊：Advance 是唯一可被多种形式引用的技能类型
     */
    static SkillMessageSignature CreateAdvance() {
        SkillMessageSignature sig("Advance");
        return sig;
    }
};

}  // namespace abot

#endif // ABOT_SKILL_MESSAGE_DEFINITIONS_H
