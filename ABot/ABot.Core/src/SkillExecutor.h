/**
 * @file SkillExecutor.h
 * @brief 技能执行引擎 - 负责技能的执行流程控制
 * 
 * 管理目标选择、冷却时间检查、技能触发等核心执行逻辑
 */

#ifndef ABOT_SKILL_EXECUTOR_H
#define ABOT_SKILL_EXECUTOR_H

#include "SkillSystem.h"
#include "Character.h"
#include "Battle.h"
#include <vector>
#include <memory>
#include <random>

namespace abot {

/**
 * @brief 技能执行配置
 */
struct SkillExecutionConfig {
    bool enable_cooldown_check = true;      // 是否检查冷却时间
    bool enable_trigger_probability = true; // 是否检查触发概率
    bool enable_skill_disable_check = true; // 是否检查技能禁用状态
};

/**
 * @brief 技能执行引擎
 * 
 * 核心职责：
 * - 技能执行前检查（冷却、触发概率等）
 * - 目标选择
 * - 技能执行
 * - 冷却时间更新
 */
class SkillExecutor {
public:
    /**
     * @brief 构造函数
     */
    SkillExecutor();
    
    /**
     * @brief 执行技能
     * @param battle 战斗系统
     * @param caster 施放者
     * @param skill 要执行的技能
     * @param config 执行配置
     * @return 执行结果
     */
    SkillResult ExecuteSkill(Battle* battle,
                            std::shared_ptr<Character> caster,
                            std::shared_ptr<Skill> skill,
                            const SkillExecutionConfig& config);
    
    /**
     * @brief 选择技能目标
     * @param battle 战斗系统
     * @param caster 施放者
     * @param target_count 需要的目标数量（来自技能的 capt 参数）
     * @param skill_type 技能类型（用于判断是伤害还是治疗）
     * @return 目标列表
     */
    std::vector<std::shared_ptr<Character>> SelectTargets(
        Battle* battle,
        std::shared_ptr<Character> caster,
        int target_count,
        const std::string& skill_type);
    
    /**
     * @brief 应用技能效果给单个目标
     * @param caster 施放者
     * @param target 目标
     * @param skill 技能
     * @param battle 战斗系统
     * @return 应用结果
     */
    SkillResult ApplySkillEffect(std::shared_ptr<Character> caster,
                                std::shared_ptr<Character> target,
                                std::shared_ptr<Skill> skill,
                                Battle* battle);
    
    /**
     * @brief 检查技能是否触发
     * @param caster 施放者
     * @param skill_param 技能参数
     * @param config 执行配置
     * @return 是否应该触发
     */
    bool CheckSkillTrigger(std::shared_ptr<Character> caster,
                          const SkillParam& skill_param,
                          const SkillExecutionConfig& config);
    
    /**
     * @brief 更新角色所有技能的冷却时间
     * @param character 角色
     */
    void UpdateCharacterSkillCooldowns(std::shared_ptr<Character> character);
    
    /**
     * @brief 设置随机数生成器种子（用于测试）
     */
    void SetRandomSeed(unsigned int seed);

private:
    std::mt19937 random_engine_;
    
    /**
     * @brief 计算目标数量（受战斗中活着的敌人数量限制）
     */
    int CalculateActualTargetCount(int requested_count,
                                   int available_targets);
    
    /**
     * @brief 随机选择一个目标
     */
    std::shared_ptr<Character> SelectRandomTarget(
        const std::vector<std::shared_ptr<Character>>& candidates);
    
    /**
     * @brief 获取对方阵营的活着角色
     */
    std::vector<std::shared_ptr<Character>> GetOpponentTeam(
        Battle* battle,
        std::shared_ptr<Character> actor);
};

}  // namespace abot

#endif  // ABOT_SKILL_EXECUTOR_H
