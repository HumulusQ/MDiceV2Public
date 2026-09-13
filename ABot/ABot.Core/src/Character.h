/**
 * @file Character.h
 * @brief ABOT 角色卡数据结构
 * 
 * 定义角色的属性、技能、状态等
 */

#ifndef ABOT_CHARACTER_H
#define ABOT_CHARACTER_H

#include <string>
#include <vector>
#include <map>
#include <unordered_map>
#include "Value.h"

namespace abot {

class ValueV2;

/**
 * @brief 技能参数结构
 */
struct SkillParam {
    std::string name;                   // 技能名称
    std::string id;                     // 技能模板 ID
    std::string type;                   // 技能类型 (ActSkill, OnTurnStartSkill, etc.)
                                        // 对于 Advance 类型，可以接受多种触发时机的组合
    int cd;                             // 冷却回合数
    int rate;                           // 触发概率 (0-100)
    bool disabled;                      // 是否被禁用
    std::map<std::string, std::string> skillpara;  // 技能参数集
    
    /**
     * @brief 验证技能类型是否遵循两层一致性约束
     * @param def_type SkillDefinition 中定义的类型
     * @return 是否有效（Advance 总是有效，其他类型必须相同）
     */
    bool ValidateTriggerType(const std::string& def_type) const {
        if (type == "Advance" || def_type == "Advance") {
            // Advance 可被多种形式引用
            return true;
        }
        // 其他类型必须完全相同
        return type == def_type;
    }
};

/**
 * @brief 状态参数结构
 */
struct StateParam {
    std::string name;                   // 状态名称
    std::string id;                     // 状态模板 ID
    std::string type;                   // 状态类型 (TagBuff, ExcBuff)
    int duration;                       // 持续回合数 (-1 为永久)
    std::map<std::string, std::string> params;  // 状态参数
};

/**
 * @brief 护甲参数结构（支持多个护甲值，可带tag区分伤害类型）
 */
struct DefenseParam {
    int value;                          // 护甲值
    std::string tag;                    // 伤害类型标签 (空表示通用)
};

/**
 * @brief 伤害减免参数结构（支持多个减免规则，可带tag区分伤害类型）
 */
struct DamageReductionParam {
    float value;                        // 减免率 (0-1)
    std::string tag;                    // 伤害类型标签 (空表示通用)
};

/**
 * @brief 每回合临时数据结构（自动在每回合开始时重置）
 */
struct TurnData {
    double multiplier = 1.0;            // 增伤系数（默认1.0，每回合初始化）
};

/**
 * @brief 技能索引结构 - 用于加速技能查询
 * 按技能触发类型索引，实现 O(N) → O(1) 的查询优化
 */
struct SkillIndex {
    // 按触发类型索引技能（指向 Character::skills 中的元素）
    std::map<std::string, std::vector<SkillParam*>> by_trigger_type;
    
    /**
     * @brief 从角色技能列表构建索引
     * @param skills 角色的技能列表引用
     */
    void BuildIndex(std::vector<SkillParam>& skills);
    
    /**
     * @brief 清除索引
     */
    void Clear() { by_trigger_type.clear(); }
    
    /**
     * @brief 获取指定类型的技能列表
     * @param trigger_type 触发类型（如 "onDamageTaken"）
     * @return 匹配的技能指针列表，如果不存在返回空向量
     */
    std::vector<SkillParam*> GetSkillsByType(const std::string& trigger_type) const {
        auto it = by_trigger_type.find(trigger_type);
        if (it != by_trigger_type.end()) {
            return it->second;
        }
        return {};
    }
};

/**
 * @brief 角色属性结构
 */
struct Character {
    // ============ 基础属性 ============
    std::string name;           // 角色名称
    int camp;                   // 阵营编号 (1, 2, ...)
    
    // ============ 战斗属性 ============
    int atk;                    // 攻击值
    int hp;                     // 当前 HP
    int max_hp;                 // 最大 HP
    int hp_restore;             // 每回合自动回血值
    int temp_hp;                // 临时 HP
    
    // ============ 伤害数组 ============
    int dmg[4];                 // d1=最小, d2=较小, d3=较大, d4=最大
    
    // ============ 扩展属性 ============
    int aggro;                  // 仇恨值（影响目标选择权重）
    std::vector<DamageReductionParam> damage_reductions;  // 伤害减免列表
    std::vector<DefenseParam> defenses;                   // 护甲值列表
    
    // ============ 标签和集合 ============
    std::vector<std::string> tags;      // 角色标签列表
    std::vector<SkillParam> skills;     // 技能列表
    SkillIndex skill_index;             // 技能索引（用于加速查询）
    std::vector<StateParam> states;     // 状态列表
    
    // ============ 运行时状态 ============
    bool is_alive;              // 是否存活
    std::map<std::string, int> skill_cooldowns;  // 技能冷却计时
    TurnData turn;              // 每回合的临时数据（自动重置）
    
    // ============ 动态属性系统 ============
    std::unordered_map<std::string, Value> extra;  // 脚本新增的动态字段表
    
    /**
     * @brief 构造函数 - 初始化为默认值
     */
    Character();
    
    /**
     * @brief 应用伤害
     * @param damage 伤害值
     * @return 实际应用的伤害（不会小于 0）
     */
    int TakeDamage(int damage);
    
    /**
     * @brief 应用治疗
     * @param healing 治疗值
     */
    void Heal(int healing);
    
    /**
     * @brief 检查是否存活
     */
    bool IsAlive() const { return is_alive && hp > 0; }
    
    /**
     * @brief 获取当前 HP 百分比
     */
    float GetHPPercentage() const {
        return max_hp > 0 ? (float)hp / max_hp : 0.0f;
    }
    
    /**
     * @brief 获取角色基础信息调试字符串
     */
    std::string GetBasicInfoDebug() const;
    
    /**
     * @brief 获取角色技能列表调试字符串
     */
    std::string GetSkillsDebug() const;
    
    /**
     * @brief 获取角色状态列表调试字符串
     */
    std::string GetStatesDebug() const;
    
    /**
     * @brief 获取角色完整调试信息
     */
    std::string GetCompleteDebug() const;
    
    /**
     * @brief 重建技能索引
     * 在添加/移除技能或初始化时调用
     */
    void RebuildSkillIndex();
    
    /**
     * @brief 添加技能并更新索引
     * @param skill 要添加的技能
     */
    void AddSkill(const SkillParam& skill);
    
    /**
     * @brief 根据类型获取技能
     * @param trigger_type 触发类型（利用索引，O(1) 查询）
     * @return 匹配的技能列表
     */
    std::vector<SkillParam*> GetSkillsByType(const std::string& trigger_type);
    
    /**
     * @brief 作为ValueV2引用获取（支持脚本修改后自动更新）
     * @return ValueV2对象，包装当前Character
     * @note 返回的ValueV2使用shared_ptr指向Character，支持引用语义
     * @note 实现见Character.cpp（需要包含ValueV2.h）
     */
    ValueV2 GetAsValueV2() const;
};

}  // namespace abot

#endif  // ABOT_CHARACTER_H
