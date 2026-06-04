/**
 * @file SkillTriggerSystem.h
 * @brief ABOT 被动技能事件触发系统
 * 
 * 实现所有15种技能类型的事件驱动触发
 * 支持：ActSkill, OnTurnStart, OnTurnEnd, OnAttackerShifted, onHitDealt, onHitTaken,
 *       onDamageDealt, onDamageTaken, onAttack, onHeal, onUnitAttack, onUnitAttacked,
 *       OnDead, OnSkillTriggler, Advance(预留)
 */

#ifndef ABOT_SKILL_TRIGGER_SYSTEM_H
#define ABOT_SKILL_TRIGGER_SYSTEM_H

#include "Character.h"
#include <memory>
#include <vector>
#include <map>
#include <string>

namespace abot {

// 前置声明
class Battle;
class ExecutionEnvironment;

// ============ 消息参数定义系统 ============

/**
 * @brief 消息参数的数据类型
 */
enum class MessageParamType {
    NONE,       // 无参数
    STRING,     // 字符串类型
    INT,        // 整数类型
    FLOAT       // 浮点数类型
};

/**
 * @brief 单个消息参数的定义
 */
struct MessageParamDef {
    std::string name;              // 参数名 (From, To, Name, Source, Dmg, Tag, value, Skillname, Owner, Skilltype)
    MessageParamType type;         // 参数数据类型
    bool required;                 // 是否为必需参数
    std::string description;       // 参数描述（用于诊断和文档）
    
    MessageParamDef() : type(MessageParamType::NONE), required(false) {}
    MessageParamDef(const std::string& n, MessageParamType t, bool req, const std::string& desc)
        : name(n), type(t), required(req), description(desc) {}
};

// Forward declare SkillTriggerMessage
struct SkillTriggerMessage;

/**
 * @brief 技能的消息签名 - 定义技能接受的所有消息参数
 */
struct SkillMessageSignature {
    std::string skill_type;                    // 技能类型 (ActSkill, onDamageTaken 等)
    std::vector<MessageParamDef> parameters;   // 参数列表
    
    SkillMessageSignature() {}
    explicit SkillMessageSignature(const std::string& type) : skill_type(type) {}
    
    // 添加参数定义
    void AddParam(const MessageParamDef& def) { parameters.push_back(def); }
    
    // 检查消息是否包含所有必需参数（实现在 .cpp 中）
    bool ValidateMessage(const SkillTriggerMessage& msg) const;
};

/**
 * @brief 技能触发事件消息 - 包含所有类型消息的联合体
 * 
 * 不同技能类型使用不同的消息字段：
 * - ActSkill: 无消息
 * - OnTurnStartSkill: 无消息
 * - OnTrunEndSkill: 无消息
 * - OnAttackerShifted: From, To
 * - onHitDealtSkill: Name
 * - onHitTakenSkill: Name
 * - onDamageDealtSkill: Name, Dmg, Tag
 * - onDamageTakenSkill: Source, Dmg, Tag
 * - onAttackSkill: Name
 * - onHealSkill: Name, value
 * - onUnitAttack: Name, Source
 * - onUnitAttacked: Name, Source
 * - OnDead: Name
 * - OnSkillTriggler: Skillname, Owner, Skilltype
 */
struct SkillTriggerMessage {
    // 用于各类型的消息字段
    std::string From;           // OnAttackerShifted - 原攻击手
    std::string To;             // OnAttackerShifted - 新攻击手
    std::string Name;           // 通用字段：单位名称/目标名称
    std::string Source;         // 攻击来源单位名称 (可能为null)
    int Dmg;                    // 伤害数值
    std::string Tag;            // 伤害类型tag
    int value;                  // 恢复数值 (onHealSkill)
    std::string Skillname;      // OnSkillTriggler - 技能名称
    std::string Owner;          // OnSkillTriggler - 技能归属者
    std::string Skilltype;      // OnSkillTriggler - 技能类型
    
    SkillTriggerMessage() : Dmg(0), value(0) {}
    
    // 获取具体字段值（用于验证）
    bool HasStringParam(const std::string& param_name) const;
    bool HasIntParam(const std::string& param_name) const;
    int GetIntParam(const std::string& param_name, int default_val = 0) const;
    std::string GetStringParam(const std::string& param_name, const std::string& default_val = "") const;
};

/**
 * @brief 技能消息签名管理器 - 存储所有15种技能的消息定义
 */
class SkillMessageRegistry {
public:
    // 初始化所有15种技能的消息签名
    static void Initialize();
    
    // 获取指定技能类型的消息签名
    static const SkillMessageSignature* GetSignature(const std::string& skill_type);
    
    // 注册新的消息签名
    static void RegisterSignature(const SkillMessageSignature& signature);
    
    // 验证消息是否与技能类型匹配
    static bool ValidateMessage(const std::string& skill_type, const SkillTriggerMessage& msg);
    
    // 获取全局实例
    static SkillMessageRegistry* GetInstance();
    
private:
    std::map<std::string, SkillMessageSignature> signatures_;
    static SkillMessageRegistry* instance_;
};

/**
 * @brief 技能触发系统 - 管理所有被动技能的事件触发
 */
class SkillTriggerSystem {
public:
    /**
     * @brief 为所有匹配类型的技能触发事件
     * @param trigger_type 触发类型字符串 (e.g., "onDamageTaken")
     * @param characters 战场上所有角色（用于onUnitAttack等场上事件）
     * @param target_character 接收事件的角色（或null表示广播给所有角色）
     * @param message 携带的消息参数
     * @param battle 战斗系统引用（用于检查战斗状态）
     * @param environment 执行环境（用于运行技能脚本）
     * @return 实际触发的技能数量
     */
    static int TriggerSkillsByType(
        const std::string& trigger_type,
        const std::vector<std::shared_ptr<Character>>& characters,
        std::shared_ptr<Character> target_character,
        const SkillTriggerMessage& message,
        Battle* battle,
        ExecutionEnvironment* environment);
    
    /**
     * @brief 为指定角色的特定技能触发
     * @param character 技能所属角色
     * @param skill_id 技能ID (type字段)
     * @param message 消息参数
     * @param battle 战斗系统引用
     * @param environment 执行环境
     * @return 是否成功触发
     */
    static bool TriggerSingleSkill(
        std::shared_ptr<Character> character,
        const std::string& skill_id,
        const SkillTriggerMessage& message,
        Battle* battle,
        ExecutionEnvironment* environment);
    
    /**
     * @brief 检查技能是否满足触发条件
     * @param skill 要检查的技能
     * @param trigger_type 触发的事件类型
     * @return 是否满足CD和rate条件
     */
    static bool CanSkillTrigger(
        const SkillParam& skill,
        const std::string& trigger_type);
    
    /**
     * @brief 为指定类型构建message参数对象
     * @param trigger_type 要触发的技能类型
     * @param ... 可变参数根据类型不同而需要不同的参数
     * @return 构建的SkillTriggerMessage
     * 
     * 便利函数 - 后续可添加特定类型的构建器
     */
    static SkillTriggerMessage BuildMessage(const std::string& trigger_type);
    
    /**
     * @brief 向执行环境赋予message变量用于脚本调用
     * @param environment 执行环境
     * @param message 要传入的消息
     * @param trigger_type 触发的技能类型
     */
    static void InjectMessageToEnvironment(
        ExecutionEnvironment* environment,
        const SkillTriggerMessage& message,
        const std::string& trigger_type);
};

} // namespace abot

#endif // ABOT_SKILL_TRIGGER_SYSTEM_H
