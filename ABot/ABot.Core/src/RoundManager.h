/**
 * @file RoundManager.h
 * @brief ABOT 回合管理器
 * 
 * 管理战斗的回合流程，支持UI交互和指令触发
 */

#ifndef ABOT_ROUND_MANAGER_H
#define ABOT_ROUND_MANAGER_H

#include "Battle.h"
#include "Character.h"
#include "SkillTriggerSystem.h"
#include <vector>
#include <memory>
#include <string>
#include <queue>

namespace abot {

/**
 * @brief 回合事件类型
 */
enum class RoundEventType {
    ROUND_START,        // 回合开始
    ACTOR_TURN_START,   // 行动者回合开始
    ACTOR_ACTION,       // 行动者执行动作
    ACTOR_TURN_END,     // 行动者回合结束
    ROUND_END,          // 回合结束
    BATTLE_END,         // 战斗结束
    ERROR               // 错误
};

/**
 * @brief 回合事件结构
 */
struct RoundEvent {
    RoundEventType type;
    std::string description;
    int round_number;
    std::shared_ptr<Character> actor;
    std::shared_ptr<Character> target;
    int parameter;      // 用于存储伤害值、治疗值等
};

/**
 * @brief 回合管理器 - 管理战斗流程
 * 
 * 功能：
 * - 初始化战斗状态
 * - 管理每个回合的执行
 * - 记录战斗事件
 * - 支持UI查询当前状态
 * - 支持指令触发回合推进
 * 
 * 架构设计（可扩展）：
 * 
 * 初始化阶段:
 *   AddCharacter() → Initialize()
 *   
 * 执行阶段:
 *   ExecuteNextRound()
 *     → 选择行动者
 *     → 执行技能脚本
 *     → 应用结果
 *     → 记录事件
 *   
 * 查询阶段:
 *   GetCurrentState() / GetCurrentActor() / GetEvents()
 * 
 * 指令触发:
 *   ExecuteCommand("advance") / ExecuteCommand("skip") 等
 */
class RoundManager {
public:
    // ============ 初始化 ============
    
    /**
     * @brief 构造函数
     */
    RoundManager();
    
    /**
     * @brief 析构函数
     */
    ~RoundManager();
    
    /**
     * @brief 添加参战角色
     * @param character 角色指针
     * @return 添加是否成功
     */
    bool AddCharacter(std::shared_ptr<Character> character);
    
    /**
     * @brief 初始化战斗管理器
     * @return 初始化是否成功
     */
    bool Initialize();
    
    /**
     * @brief 强制启动RoundManager（用于从保存状态恢复）
     * 
     * 当从保存状态恢复时，Initialize() 可能因缺少角色而失败。
     * 此方法强制设置 is_running_ = true，允许战斗继续。
     * 调用者需要确保角色已通过 AddCharacter() 添加。
     */
    void ForceStart();
    
    /**
     * @brief 清除所有参战角色并重置战斗状态
     * 
     * 用于开始新的战斗脚本时，清空旧的战斗状态。
     * 清除：角色列表、回合计数、事件记录、当前行动者等
     */
    void ClearAllCharacters();
    
    // ============ 回合执行 ============
    
    /**
     * @brief 执行下一个回合
     * @return 回合是否执行成功
     */
    bool ExecuteNextRound();
    
    /**
     * @brief 执行指定数量的回合
     * @param count 要执行的回合数
     * @return 实际执行的回合数
     */
    int ExecuteRounds(int count);
    
    /**
     * @brief 跳过当前回合
     * @return 是否成功跳过
     */
    bool SkipCurrentRound();
    
    /**
     * @brief 暂停战斗（保持状态，等待指令）
     */
    void Pause();
    
    /**
     * @brief 恢复战斗
     */
    void Resume();
    
    // ============ 状态查询 ============
    
    /**
     * @brief 检查战斗是否在进行中
     */
    bool IsRunning() const;
    
    /**
     * @brief 检查战斗是否已结束
     */
    bool IsFinished() const;
    
    /**
     * @brief 获取当前回合数
     */
    int GetCurrentRound() const { return current_round_; }
    
    /**
     * @brief 获取当前行动者
     */
    std::shared_ptr<Character> GetCurrentActor() const { return current_actor_; }
    
    /**
     * @brief 获取所有参战角色
     */
    const std::vector<std::shared_ptr<Character>>& GetAllCharacters() const { return characters_; }
    
    /**
     * @brief 获取指定阵营的存活角色
     */
    std::vector<std::shared_ptr<Character>> GetLiveCharactersByCamp(int camp) const;
    
    /**
     * @brief 获取战斗胜者阵营号
     * @return 胜出的阵营号，若未结束返回 0
     */
    int GetVictoryCamp() const;
    
    /**
     * @brief 获取错误信息
     */
    std::string GetLastError() const { return last_error_; }
    
    /**
     * @brief 设置错误信息（用于外部编译器错误）
     */
    void SetLastError(const std::string& error) { last_error_ = error; }
    
    // ============ 事件管理 ============
    
    /**
     * @brief 获取所有已记录的回合事件
     */
    const std::vector<RoundEvent>& GetAllEvents() const { return events_; }
    
    /**
     * @brief 获取最后N个事件
     */
    std::vector<RoundEvent> GetLastEvents(int count) const;
    
    /**
     * @brief 清空事件记录
     */
    void ClearEvents() { events_.clear(); }
    
    // ============ 指令接口（支持外部指令触发）============
    
    /**
     * @brief 执行外部指令
     * @param command 指令名称 (advance, skip, restart, pause, resume等)
     * @param parameters 指令参数 (可选)
     * @return 指令执行结果
     * 
     * 示例指令：
     *   execute_command("advance")      - 推进一回合
     *   execute_command("advance", "5") - 推进5个回合
     *   execute_command("skip")         - 跳过当前回合
     *   execute_command("pause")        - 暂停战斗
     *   execute_command("resume")       - 恢复战斗
     *   execute_command("restart")      - 重新开始战斗
     */
    bool ExecuteCommand(const std::string& command, const std::string& parameters = "");
    
    /**
     * @brief 获取可用指令列表（用于UI提示）
     */
    std::vector<std::string> GetAvailableCommands() const;
    
    // ============ 调试接口 ============
    
    /**
     * @brief 获取战斗状态摘要
     * @return 包含当前状态的字符串
     */
    std::string GetStatusSummary() const;
    
    /**
     * @brief 输出完整的战斗日志
     */
    std::string GetBattleLog() const;
    
    /**
     * @brief 获取技能触发日志
     * @return 包含技能触发事件的日志字符串
     */
    std::string GetSkillTriggerLog() const;
    
    /**
     * @brief 追加技能触发日志
     * @param log_entry 日志条目
     */
    void AppendSkillTriggerLog(const std::string& log_entry);
    
    /**
     * @brief 清空技能触发日志
     */
    void ClearSkillTriggerLog();
    
    /**
     * @brief 转移攻击者 - 基于仇恨度权重随机选择同阵营的其他活单位
     * @param target 当前目标（参考用途）
     * @return 是否成功转移，返回true表示转移完成
     * 
     * 用途：在大失败时调用，将攻击权转交给同阵营的其他角色
     * 机制：
     * 1. 获取当前攻击者的阵营
     * 2. 获取该阵营的所有其他活着的角色
     * 3. 使用aggro属性作为权重进行加权随机选择
     * 4. 设置新的攻击者
     * 5. 触发 OnAttackerShifted 技能
     */
    bool ShiftAttacker(std::shared_ptr<Character> target = nullptr);

    /**
     * @brief 应用伤害并触发相关技能事件
     * @param attacker 发起者
     * @param target 目标
     * @param damage_value 伤害值
     * @param damage_tag 伤害类型标签（可选）
     * @return 实际应用的伤害
     * 
     * 本方法会：
     * 1. 触发目标的 onDamageTaken 技能
     * 2. 触发发起者的 onDamageDealt 技能
     * 3. 触发场上其他单位的 onUnitAttacked/onUnitAttack
     * 4. 应用伤害到目标HP
     * 5. 检查死亡并触发 OnDead 技能
     */
    int ApplyDamageWithTrigger(
        std::shared_ptr<Character> attacker,
        std::shared_ptr<Character> target,
        int damage_value,
        const std::string& damage_tag = "");
    
    /**
     * @brief 应用治疗并触发相关技能事件
     * @param healer 施放者（可选）
     * @param target 目标
     * @param heal_value 治疗值
     * @return 实际应用的治疗
     */
    int ApplyHealWithTrigger(
        std::shared_ptr<Character> healer,
        std::shared_ptr<Character> target,
        int heal_value);

    /**
     * 🟥【关键修复】获取共享的战斗级 ObjectTable
     * @return 所有 ExecutionEnvironment 应该共享的唯一 ObjectTable
     */
    std::shared_ptr<ObjectTable> GetSharedObjectTable() const { return shared_object_table_; }
    
    /**
     * 🟥【Phase 4】获取全局字段容器的 handle
     * @return global 容器的 ObjectHandle（用于 declare global.xxx）
     */
    ObjectHandle GetGlobalHandle() const { return global_handle_; }

private:
    // ============ 私有成员 ============
    
    std::unique_ptr<Battle> battle_;              ///< 底层战斗对象
    std::vector<std::shared_ptr<Character>> characters_;  ///< 所有参战角色
    std::shared_ptr<Character> current_actor_;   ///< 当前行动者
    int current_round_;                          ///< 当前回合数
    int actor_index_;                            ///< 当前行动者索引
    bool is_running_;                            ///< 战斗是否正在进行
    bool is_paused_;                             ///< 战斗是否暂停
    std::vector<RoundEvent> events_;             ///< 事件记录
    std::string last_error_;                     ///< 最后错误信息
    std::string skill_trigger_log_;              ///< 技能触发日志缓冲区
    
    // 🟥【关键修复】共享的 ObjectTable 实例，所有脚本执行都使用这个
    std::shared_ptr<ObjectTable> shared_object_table_;  ///< 战斗级 ObjectTable（由所有环境共享）
    
    // 🟥【declare 系统】全局字段容器
    ObjectHandle global_handle_;                 ///< 全局字段存储（declare global.xxx）
    
    // ============ 私有方法 ============
    
    /**
     * @brief 根据d100+atk计算选择行动者
     * @return 选中的行动者，若无有效角色返回nullptr
     */
    std::shared_ptr<Character> SelectActorByInitiative();
    
    /**
     * @brief 选择下一个行动者
     * @return 是否成功选择
     */
    bool SelectNextActor();
    
    /**
     * @brief 执行行动者的Anke预设（普通攻击或技能）
     * @return Anke是否执行成功
     */
    bool ExecuteAnkeAction();
    
    /**
     * @brief 执行行动者的技能
     * @return 技能是否执行成功
     */
    bool ExecuteActorSkill();
    
    /**
     * @brief 检查回合是否应该结束
     */
    bool ShouldEndRound();
    
    /**
     * @brief 记录事件
     */
    void RecordEvent(const RoundEvent& event);
    
    // ============ 技能触发相关方法 ============
    
    /**
     * @brief 为角色及场上相关角色触发被动技能
     * @param trigger_type 要触发的技能类型
     * @param target_character 接收事件的主要目标（可选）
     * @param message 事件消息参数
     * @return 实际被触发的技能数量
     */
    int TriggerPassiveSkills(
        const std::string& trigger_type,
        std::shared_ptr<Character> target_character,
        const SkillTriggerMessage& message);

};

}  // namespace abot

// 全局RoundManager指针 - 供builtin_shiftattacker等函数使用
// 注意：不使用 thread_local，确保跨线程可访问
extern abot::RoundManager* g_current_round_manager;

#endif  // ABOT_ROUND_MANAGER_H
