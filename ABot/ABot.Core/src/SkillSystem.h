/**
 * @file SkillSystem.h
 * @brief ABOT 技能系统 - 预设技能集定义
 * 
 * 定义各种预设技能类型和执行逻辑
 */

#ifndef ABOT_SKILL_SYSTEM_H
#define ABOT_SKILL_SYSTEM_H

#include "Character.h"
#include <string>
#include <vector>
#include <memory>
#include <map>

namespace abot {

// 前置声明
class Battle;

/**
 * @brief 技能执行结果
 */
struct SkillResult {
    bool success;                   // 是否成功执行
    std::string message;            // 执行信息
    int damage_dealt;               // 造成的伤害
    std::vector<std::string> states_applied;  // 施加的状态
};

/**
 * @brief 基础技能类
 */
class Skill {
public:
    virtual ~Skill() = default;
    
    /**
     * @brief 执行技能
     * @param caster 施放者
     * @param targets 目标列表
     * @param battle 战斗系统引用
     * @return 技能执行结果
     */
    virtual SkillResult Execute(std::shared_ptr<Character> caster,
                               std::vector<std::shared_ptr<Character>> targets,
                               Battle* battle) = 0;
    
    /**
     * @brief 检查技能是否可以触发
     * @return 是否可以触发（考虑冷却、是否禁用等）
     */
    virtual bool CanTrigger(std::shared_ptr<Character> caster) const = 0;
    
    /**
     * @brief 每回合结束时更新技能冷却
     */
    virtual void UpdateCooldown(std::shared_ptr<Character> caster) = 0;
    
    /**
     * @brief 获取技能名称
     */
    virtual std::string GetName() const = 0;
    
    /**
     * @brief 获取技能类型
     */
    virtual std::string GetType() const = 0;
};

/**
 * @brief 普通伤害技能
 * 
 * 对目标造成基于伤害数组(d1-d4)的伤害
 */
class DmgSkill : public Skill {
public:
    explicit DmgSkill(const SkillParam& param);
    
    SkillResult Execute(std::shared_ptr<Character> caster,
                       std::vector<std::shared_ptr<Character>> targets,
                       Battle* battle) override;
    
    bool CanTrigger(std::shared_ptr<Character> caster) const override;
    
    void UpdateCooldown(std::shared_ptr<Character> caster) override;
    
    std::string GetName() const override { return param_.name; }
    
    std::string GetType() const override { return param_.type; }

private:
    SkillParam param_;
    
    /**
     * @brief 根据伤害数组和权重随机选择伤害值
     * @param attacker 攻击者
     * @return 伤害值
     */
    int SelectDamage(std::shared_ptr<Character> attacker);
};

/**
 * @brief 造成伤害 + 施加状态的技能
 * 
 * 对目标造成伤害，并有概率施加特定状态
 */
class ApplyStateSkill : public Skill {
public:
    explicit ApplyStateSkill(const SkillParam& param);
    
    SkillResult Execute(std::shared_ptr<Character> caster,
                       std::vector<std::shared_ptr<Character>> targets,
                       Battle* battle) override;
    
    bool CanTrigger(std::shared_ptr<Character> caster) const override;
    
    void UpdateCooldown(std::shared_ptr<Character> caster) override;
    
    std::string GetName() const override { return param_.name; }
    
    std::string GetType() const override { return param_.type; }

private:
    SkillParam param_;
    
    int SelectDamage(std::shared_ptr<Character> attacker);
};

/**
 * @brief 治疗技能
 * 
 * 为目标恢复 HP
 */
class HealSkill : public Skill {
public:
    explicit HealSkill(const SkillParam& param);
    
    SkillResult Execute(std::shared_ptr<Character> caster,
                       std::vector<std::shared_ptr<Character>> targets,
                       Battle* battle) override;
    
    bool CanTrigger(std::shared_ptr<Character> caster) const override;
    
    void UpdateCooldown(std::shared_ptr<Character> caster) override;
    
    std::string GetName() const override { return param_.name; }
    
    std::string GetType() const override { return param_.type; }

private:
    SkillParam param_;
};

/**
 * @brief 增益状态技能
 * 
 * 为目标施加增益状态（如攻击力提升）
 */
class BuffSkill : public Skill {
public:
    explicit BuffSkill(const SkillParam& param);
    
    SkillResult Execute(std::shared_ptr<Character> caster,
                       std::vector<std::shared_ptr<Character>> targets,
                       Battle* battle) override;
    
    bool CanTrigger(std::shared_ptr<Character> caster) const override;
    
    void UpdateCooldown(std::shared_ptr<Character> caster) override;
    
    std::string GetName() const override { return param_.name; }
    
    std::string GetType() const override { return param_.type; }

private:
    SkillParam param_;
};

/**
 * @brief 减益状态技能（控制技能）
 * 
 * 为目标施加减益状态（如眩晕、沉默）
 */
class CrowdControlSkill : public Skill {
public:
    explicit CrowdControlSkill(const SkillParam& param);
    
    SkillResult Execute(std::shared_ptr<Character> caster,
                       std::vector<std::shared_ptr<Character>> targets,
                       Battle* battle) override;
    
    bool CanTrigger(std::shared_ptr<Character> caster) const override;
    
    void UpdateCooldown(std::shared_ptr<Character> caster) override;
    
    std::string GetName() const override { return param_.name; }
    
    std::string GetType() const override { return param_.type; }

private:
    SkillParam param_;
};

/**
 * @brief 技能工厂类 - 根据技能参数创建对应的技能对象
 */
class SkillFactory {
public:
    /**
     * @brief 根据技能参数创建技能对象
     * @param param 技能参数
     * @return 技能对象指针（失败返回 nullptr）
     */
    static std::shared_ptr<Skill> CreateSkill(const SkillParam& param);
};

}  // namespace abot

#endif  // ABOT_SKILL_SYSTEM_H
