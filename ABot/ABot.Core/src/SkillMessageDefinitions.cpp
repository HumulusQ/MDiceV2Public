/**
 * @file SkillMessageDefinitions.cpp
 * @brief 15种技能消息定义的实现
 */

#pragma execution_character_set("utf-8")

#include "SkillMessageDefinitions.h"

namespace abot {

void SkillMessageDefinitions::InitializeAllSignatures() {
    auto* registry = SkillMessageRegistry::GetInstance();
    
    // 1. ActSkill - 主动技能
    registry->RegisterSignature(CreateActSkill());
    
    // 2. OnTurnStart - 回合开始
    registry->RegisterSignature(CreateOnTurnStart());
    
    // 3. OnTurnEnd - 回合结束（注意代码可能拼写为 OnTrunEndSkill）
    registry->RegisterSignature(CreateOnTurnEnd());
    
    // 4. OnAttackerShifted - 攻击权转移
    registry->RegisterSignature(CreateOnAttackerShifted());
    
    // 5. onHitDealt - 造成命中
    registry->RegisterSignature(CreateOnHitDealt());
    
    // 6. onHitTaken - 受到命中
    registry->RegisterSignature(CreateOnHitTaken());
    
    // 7. onDamageDealt - 造成伤害
    registry->RegisterSignature(CreateOnDamageDealt());
    
    // 8. onDamageTaken - 受到伤害（最常用）
    registry->RegisterSignature(CreateOnDamageTaken());
    
    // 9. onAttack - 发动攻击
    registry->RegisterSignature(CreateOnAttack());
    
    // 10. onHeal - 受到治疗
    registry->RegisterSignature(CreateOnHeal());
    
    // 11. onUnitAttack - 看到单位攻击
    registry->RegisterSignature(CreateOnUnitAttack());
    
    // 12. onUnitAttacked - 看到单位被攻击
    registry->RegisterSignature(CreateOnUnitAttacked());
    
    // 13. OnDead - 角色死亡
    registry->RegisterSignature(CreateOnDead());
    
    // 14. OnSkillTriggler - 技能触发观察
    registry->RegisterSignature(CreateOnSkillTriggler());
    
    // 15. Advance - 推进回合
    registry->RegisterSignature(CreateAdvance());
}

}  // namespace abot
