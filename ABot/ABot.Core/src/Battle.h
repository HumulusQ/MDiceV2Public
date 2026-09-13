/**
 * @file Battle.h
 * @brief ABOT 战斗系统
 * 
 * 管理战斗循环、伤害计算、胜负判定
 */

#ifndef ABOT_BATTLE_H
#define ABOT_BATTLE_H

#include "Character.h"
#include <vector>
#include <memory>

namespace abot {

/**
 * @brief 战斗状态枚举
 */
enum class BattleState {
    UNINITIALIZED,      // 未初始化
    INITIALIZED,        // 已初始化
    IN_PROGRESS,        // 进行中
    FINISHED,           // 已结束
    ERROR               // 错误
};

/**
 * @brief 战斗系统
 */
class Battle {
public:
    /**
     * @brief 构造函数
     */
    Battle();
    
    /**
     * @brief 析构函数
     */
    ~Battle();
    
    /**
     * @brief 初始化战斗
     * @param characters 参战角色列表
     * @return 初始化是否成功
     */
    bool Initialize(const std::vector<std::shared_ptr<Character>>& characters);
    
    /**
     * @brief 检查战斗状态是否有效
     * @return 至少存在 2 个不同阵营
     */
    bool IsValid() const;
    
    /**
     * @brief 开始战斗
     * @return 开始是否成功
     */
    bool Start();
    
    /**
     * @brief 执行一个战斗回合
     * @return 回合是否成功执行
     */
    bool ExecuteRound();
    
    /**
     * @brief 检查战斗是否结束
     */
    bool IsFinished() const;
    
    /**
     * @brief 检查战斗是否有胜者
     * @return 若有胜者返回阵营号，否则返回 0
     */
    int GetVictoryCamp() const;
    
    /**
     * @brief 获取战斗状态
     */
    BattleState GetState() const { return state_; }
    
    /**
     * @brief 获取错误信息
     */
    std::string GetLastError() const { return last_error_; }
    
    /**
     * @brief 获取当前回合数
     */
    int GetCurrentRound() const { return current_round_; }
    
    /**
     * @brief 获取存活的角色列表（按阵营）
     */
    std::vector<std::shared_ptr<Character>> GetLiveCharactersByCamp(int camp) const;

private:
    BattleState state_;
    std::string last_error_;
    int current_round_;
    
    std::vector<std::shared_ptr<Character>> characters_;
    std::map<int, std::vector<std::shared_ptr<Character>>> camps_;
    
    /**
     * @brief 从所有存活的角色中选择行动者（ATK 最高）
     */
    std::shared_ptr<Character> SelectActor();
    
    /**
     * @brief 为行动者选择目标（敌对阵营，Aggro 权重）
     */
    std::shared_ptr<Character> SelectTarget(std::shared_ptr<Character> actor);
    
    /**
     * @brief 执行一次普通攻击
     */
    void ExecuteAttack(std::shared_ptr<Character> attacker,
                      std::shared_ptr<Character> target);
    
    /**
     * @brief 计算伤害
     * @return 最终伤害值
     */
    int CalculateDamage(std::shared_ptr<Character> attacker,
                       std::shared_ptr<Character> target);
};

}  // namespace abot

#endif  // ABOT_BATTLE_H
