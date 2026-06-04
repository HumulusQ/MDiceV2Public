/**
 * @file SkillExecutor.cpp
 * @brief 技能执行引擎实现
 */

#include "SkillExecutor.h"
#include <cmath>

namespace abot {

SkillExecutor::SkillExecutor() {
    std::random_device rd;
    random_engine_.seed(rd());
}

SkillResult SkillExecutor::ExecuteSkill(Battle* battle,
                                       std::shared_ptr<Character> caster,
                                       std::shared_ptr<Skill> skill,
                                       const SkillExecutionConfig& config) {
    SkillResult result;
    result.success = false;
    result.message = "Skill execution failed";
    result.damage_dealt = 0;
    
    if (!battle || !caster || !skill) {
        result.message = "Invalid parameters for skill execution";
        return result;
    }
    
    // 检查施放者是否已经被击败
    if (!caster->is_alive) {
        result.message = "Caster is defeated";
        return result;
    }
    
    // TODO: 需要通过技能获取技能参数来检查触发概率
    // 这里暂时跳过，因为需要从Skill类中添加GetSkillParam()方法
    
    // 选择目标
    // 提取 capt 参数以确定目标数量
    // 注：这需要从技能中获取参数，暂时使用默认值 1
    int target_count = 1;
    std::string skill_type = skill->GetType();
    
    auto targets = SelectTargets(battle, caster, target_count, skill_type);
    
    if (targets.empty()) {
        result.message = "No valid targets found";
        return result;
    }
    
    // 执行技能
    result = skill->Execute(caster, targets, battle);
    
    // 更新冷却时间（这需要技能参数，暂时由technique自己处理）
    
    return result;
}

std::vector<std::shared_ptr<Character>> SkillExecutor::SelectTargets(
    Battle* battle,
    std::shared_ptr<Character> caster,
    int target_count,
    const std::string& skill_type) {
    
    std::vector<std::shared_ptr<Character>> targets;
    
    if (!battle || !caster) {
        return targets;
    }
    
    // 确定目标方向（伤害类技能选择敌方，治疗类关键选择友方）
    bool is_offensive = (skill_type == "Dmg" || skill_type == "dmg" ||
                        skill_type == "ApplyState" || skill_type == "apply_state" ||
                        skill_type == "CC" || skill_type == "CrowdControl");
    
    if (is_offensive) {
        // 选择敌方目标
        auto opponent_team = GetOpponentTeam(battle, caster);
        
        int actual_count = CalculateActualTargetCount(target_count, opponent_team.size());
        
        // 随机选择目标
        for (int i = 0; i < actual_count && !opponent_team.empty(); ++i) {
            auto target = SelectRandomTarget(opponent_team);
            if (target) {
                targets.push_back(target);
                // 移除已选的目标以避免重复选择
                auto it = std::find(opponent_team.begin(), opponent_team.end(), target);
                if (it != opponent_team.end()) {
                    opponent_team.erase(it);
                }
            }
        }
    } else {
        // 治疗/增益类技能选择友方目标
        // 如果 capt 为 1，选择自己；否则选择队友
        if (target_count == 1) {
            targets.push_back(caster);
        } else {
            // TODO: 实现友方目标选择逻辑
            targets.push_back(caster);
        }
    }
    
    return targets;
}

SkillResult SkillExecutor::ApplySkillEffect(std::shared_ptr<Character> caster,
                                           std::shared_ptr<Character> target,
                                           std::shared_ptr<Skill> skill,
                                           Battle* battle) {
    SkillResult result;
    result.success = false;
    
    if (!caster || !target || !skill) {
        result.message = "Invalid parameters";
        return result;
    }
    
    // 这个方法是对单个目标应用技能效果
    // 实际的执行由各个技能类的Execute()方法处理
    
    result.success = true;
    return result;
}

bool SkillExecutor::CheckSkillTrigger(std::shared_ptr<Character> caster,
                                     const SkillParam& skill_param,
                                     const SkillExecutionConfig& config) {
    
    if (!caster) return false;
    
    // 检查技能是否被禁用
    if (config.enable_skill_disable_check && skill_param.disabled) {
        return false;
    }
    
    // 检查冷却时间
    if (config.enable_cooldown_check) {
        auto& cooldowns = caster->skill_cooldowns;
        if (cooldowns.find(skill_param.id) != cooldowns.end() && 
            cooldowns[skill_param.id] > 0) {
            return false;
        }
    }
    
    // 检查触发概率
    if (config.enable_trigger_probability && skill_param.rate > 0) {
        // rate 通常在 0-100 之间，表示百分比
        std::uniform_real_distribution<> dis(0.0, 100.0);
        float roll = dis(random_engine_);
        
        if (roll > skill_param.rate) {
            return false;
        }
    }
    
    return true;
}

void SkillExecutor::UpdateCharacterSkillCooldowns(std::shared_ptr<Character> character) {
    if (!character) return;
    
    // TODO: 需要访问角色的所有技能以调用UpdateCooldown()
    // 这需要从Character类中添加获取技能列表的方法
    
    // 暂时只更新冷却时间map中的值
    auto& cooldowns = character->skill_cooldowns;
    for (auto& pair : cooldowns) {
        if (pair.second > 0) {
            pair.second--;
        }
    }
}

void SkillExecutor::SetRandomSeed(unsigned int seed) {
    random_engine_.seed(seed);
}

int SkillExecutor::CalculateActualTargetCount(int requested_count,
                                             int available_targets) {
    if (available_targets <= 0) return 0;
    
    // 不能选择比可用目标更多的目标
    return std::min(requested_count, available_targets);
}

std::shared_ptr<Character> SkillExecutor::SelectRandomTarget(
    const std::vector<std::shared_ptr<Character>>& candidates) {
    
    if (candidates.empty()) return nullptr;
    
    std::uniform_int_distribution<> dis(0, candidates.size() - 1);
    return candidates[dis(random_engine_)];
}

std::vector<std::shared_ptr<Character>> SkillExecutor::GetOpponentTeam(
    Battle* battle,
    std::shared_ptr<Character> actor) {
    
    std::vector<std::shared_ptr<Character>> opponents;
    
    if (!battle || !actor) return opponents;
    
    // 确定对方阵营
    int actor_camp = actor->camp;
    int opponent_camp = (actor_camp == 0) ? 1 : 0;
    
    // 获取对方阵营的所有活着的角色
    // TODO: 这需要从Battle类中暴露GetLiveCharactersByCamp()方法
    // 暂时返回空列表
    
    return opponents;
}

}  // namespace abot
