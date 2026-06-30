/**
 * @file SkillSystem.cpp
 * @brief 技能系统实现
 */

#include "SkillSystem.h"
#include "Battle.h"
#include "ParameterParser.h"
#include <algorithm>
#include <random>
#include <iostream>

namespace abot {

// ============================================================================
// DmgSkill Implementation
// ============================================================================

DmgSkill::DmgSkill(const SkillParam& param) : param_(param) {}

SkillResult DmgSkill::Execute(std::shared_ptr<Character> caster,
                              std::vector<std::shared_ptr<Character>> targets,
                              Battle* battle) {
    SkillResult result;
    result.success = false;
    result.damage_dealt = 0;
    
    if (!caster || targets.empty() || !battle) {
        result.message = "Invalid parameter for DmgSkill execution";
        return result;
    }
    
    // 检查冷却
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        result.message = "Skill in cooldown";
        return result;
    }
    
    // 对每个目标应用伤害
    for (auto& target : targets) {
        if (!target || !target->is_alive) continue;
        
        int damage = SelectDamage(caster);
        target->TakeDamage(damage);
        result.damage_dealt += damage;
    }
    
    result.success = true;
    result.message = "Damage skill executed";
    
    // 重置冷却
    if (cooldowns.find(param_.id) != cooldowns.end()) {
        cooldowns[param_.id] = param_.cd;
    }
    
    return result;
}

bool DmgSkill::CanTrigger(std::shared_ptr<Character> caster) const {
    if (!caster) return false;
    
    if (param_.disabled) return false;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        return false;
    }
    
    return true;
}

void DmgSkill::UpdateCooldown(std::shared_ptr<Character> caster) {
    if (!caster) return;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        cooldowns[param_.id]--;
    }
}

int DmgSkill::SelectDamage(std::shared_ptr<Character> attacker) {
    // 从 skillpara 中提取伤害值数组：d1, d2, d3, d4
    auto& skillpara = param_.skillpara;
    
    std::vector<int> damages;
    for (int i = 1; i <= 4; ++i) {
        std::string key = "d" + std::to_string(i);
        if (skillpara.find(key) != skillpara.end()) {
            damages.push_back(std::stoi(skillpara.at(key)));
        }
    }
    
    if (damages.empty()) {
        return 10;  // 默认伤害
    }
    
    // 随机选择一个伤害值
    std::random_device rd;
    std::mt19937 gen(rd());
    std::uniform_int_distribution<> dis(0, damages.size() - 1);
    
    return damages[dis(gen)];
}

// ============================================================================
// ApplyStateSkill Implementation
// ============================================================================

ApplyStateSkill::ApplyStateSkill(const SkillParam& param) : param_(param) {}

SkillResult ApplyStateSkill::Execute(std::shared_ptr<Character> caster,
                                     std::vector<std::shared_ptr<Character>> targets,
                                     Battle* battle) {
    SkillResult result;
    result.success = false;
    result.damage_dealt = 0;
    
    if (!caster || targets.empty() || !battle) {
        result.message = "Invalid parameter for ApplyStateSkill execution";
        return result;
    }
    
    // 检查冷却
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        result.message = "Skill in cooldown";
        return result;
    }
    
    // 提取触发概率
    auto& skillpara = param_.skillpara;
    float trigger_rate = 100.0f;
    if (skillpara.find("rate") != skillpara.end()) {
        trigger_rate = std::stof(skillpara.at("rate"));
    }
    
    // 对每个目标应用伤害和状态
    for (auto& target : targets) {
        if (!target || !target->is_alive) continue;
        
        int damage = SelectDamage(caster);
        target->TakeDamage(damage);
        result.damage_dealt += damage;
        
        // 检查是否触发状态效果
        std::random_device rd;
        std::mt19937 gen(rd());
        std::uniform_real_distribution<> dis(0.0, 100.0);
        
        if (dis(gen) <= trigger_rate) {
            // 应用状态
            if (skillpara.find("state") != skillpara.end()) {
                result.states_applied.push_back(skillpara.at("state"));
            }
        }
    }
    
    result.success = true;
    result.message = "Damage + state skill executed";
    
    // 重置冷却
    if (cooldowns.find(param_.id) != cooldowns.end()) {
        cooldowns[param_.id] = param_.cd;
    }
    
    return result;
}

bool ApplyStateSkill::CanTrigger(std::shared_ptr<Character> caster) const {
    if (!caster) return false;
    
    if (param_.disabled) return false;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        return false;
    }
    
    return true;
}

void ApplyStateSkill::UpdateCooldown(std::shared_ptr<Character> caster) {
    if (!caster) return;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        cooldowns[param_.id]--;
    }
}

int ApplyStateSkill::SelectDamage(std::shared_ptr<Character> attacker) {
    auto& skillpara = param_.skillpara;
    
    std::vector<int> damages;
    for (int i = 1; i <= 4; ++i) {
        std::string key = "d" + std::to_string(i);
        if (skillpara.find(key) != skillpara.end()) {
            damages.push_back(std::stoi(skillpara.at(key)));
        }
    }
    
    if (damages.empty()) {
        return 5;  // 默认伤害（通常比单纯伤害技能小）
    }
    
    std::random_device rd;
    std::mt19937 gen(rd());
    std::uniform_int_distribution<> dis(0, damages.size() - 1);
    
    return damages[dis(gen)];
}

// ============================================================================
// HealSkill Implementation
// ============================================================================

HealSkill::HealSkill(const SkillParam& param) : param_(param) {}

SkillResult HealSkill::Execute(std::shared_ptr<Character> caster,
                              std::vector<std::shared_ptr<Character>> targets,
                              Battle* battle) {
    SkillResult result;
    result.success = false;
    result.damage_dealt = 0;
    
    if (!caster || targets.empty() || !battle) {
        result.message = "Invalid parameter for HealSkill execution";
        return result;
    }
    
    // 检查冷却
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        result.message = "Skill in cooldown";
        return result;
    }
    
    // 从 skillpara 中提取治疗量
    auto& skillpara = param_.skillpara;
    int heal_amount = 10;  // 默认治疗量
    if (skillpara.find("heal") != skillpara.end()) {
        heal_amount = std::stoi(skillpara.at("heal"));
    }
    
    // 对每个目标应用治疗
    for (auto& target : targets) {
        if (!target) continue;
        
        target->Heal(heal_amount);
    }
    
    result.success = true;
    result.message = "Heal skill executed";
    
    // 重置冷却
    if (cooldowns.find(param_.id) != cooldowns.end()) {
        cooldowns[param_.id] = param_.cd;
    }
    
    return result;
}

bool HealSkill::CanTrigger(std::shared_ptr<Character> caster) const {
    if (!caster) return false;
    
    if (param_.disabled) return false;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        return false;
    }
    
    return true;
}

void HealSkill::UpdateCooldown(std::shared_ptr<Character> caster) {
    if (!caster) return;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        cooldowns[param_.id]--;
    }
}

// ============================================================================
// BuffSkill Implementation
// ============================================================================

BuffSkill::BuffSkill(const SkillParam& param) : param_(param) {}

SkillResult BuffSkill::Execute(std::shared_ptr<Character> caster,
                              std::vector<std::shared_ptr<Character>> targets,
                              Battle* battle) {
    SkillResult result;
    result.success = false;
    result.damage_dealt = 0;
    
    if (!caster || targets.empty() || !battle) {
        result.message = "Invalid parameter for BuffSkill execution";
        return result;
    }
    
    // 检查冷却
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        result.message = "Skill in cooldown";
        return result;
    }
    
    // 对每个目标应用增益
    for (auto& target : targets) {
        if (!target) continue;
        
        auto& skillpara = param_.skillpara;
        if (skillpara.find("buff_type") != skillpara.end()) {
            result.states_applied.push_back(skillpara.at("buff_type"));
        }
    }
    
    result.success = true;
    result.message = "Buff skill executed";
    
    // 重置冷却
    if (cooldowns.find(param_.id) != cooldowns.end()) {
        cooldowns[param_.id] = param_.cd;
    }
    
    return result;
}

bool BuffSkill::CanTrigger(std::shared_ptr<Character> caster) const {
    if (!caster) return false;
    
    if (param_.disabled) return false;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        return false;
    }
    
    return true;
}

void BuffSkill::UpdateCooldown(std::shared_ptr<Character> caster) {
    if (!caster) return;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        cooldowns[param_.id]--;
    }
}

// ============================================================================
// CrowdControlSkill Implementation
// ============================================================================

CrowdControlSkill::CrowdControlSkill(const SkillParam& param) : param_(param) {}

SkillResult CrowdControlSkill::Execute(std::shared_ptr<Character> caster,
                                      std::vector<std::shared_ptr<Character>> targets,
                                      Battle* battle) {
    SkillResult result;
    result.success = false;
    result.damage_dealt = 0;
    
    if (!caster || targets.empty() || !battle) {
        result.message = "Invalid parameter for CrowdControlSkill execution";
        return result;
    }
    
    // 检查冷却
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        result.message = "Skill in cooldown";
        return result;
    }
    
    // 对每个目标应用控制效果
    for (auto& target : targets) {
        if (!target) continue;
        
        auto& skillpara = param_.skillpara;
        if (skillpara.find("cc_type") != skillpara.end()) {
            result.states_applied.push_back(skillpara.at("cc_type"));
        }
    }
    
    result.success = true;
    result.message = "Crowd control skill executed";
    
    // 重置冷却
    if (cooldowns.find(param_.id) != cooldowns.end()) {
        cooldowns[param_.id] = param_.cd;
    }
    
    return result;
}

bool CrowdControlSkill::CanTrigger(std::shared_ptr<Character> caster) const {
    if (!caster) return false;
    
    if (param_.disabled) return false;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        return false;
    }
    
    return true;
}

void CrowdControlSkill::UpdateCooldown(std::shared_ptr<Character> caster) {
    if (!caster) return;
    
    auto& cooldowns = caster->skill_cooldowns;
    if (cooldowns.find(param_.id) != cooldowns.end() && 
        cooldowns[param_.id] > 0) {
        cooldowns[param_.id]--;
    }
}

}  // namespace abot
