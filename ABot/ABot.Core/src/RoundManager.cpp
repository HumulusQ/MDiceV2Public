/**
 * @file RoundManager.cpp
 * @brief ABOT 回合管理器实现
 */

#pragma execution_character_set("utf-8")

#include "RoundManager.h"
#include "SkillTriggerSystem.h"
#include "VM.h"
#include "ExecutionEnvironment.h"
#include "ParameterParser.h"
#include "PresetSystem.h"
#include "SchemaValue.h"
#include <algorithm>
#include <sstream>
#include <set>
#include <cstdlib>
#include <ctime>

namespace abot {

// 全局RoundManager指针的前置声明 - 在命名空间内部
// 实际定义在命名空间外部（见file end）

/**
 * @brief 全局伤害回调函数 - 连接ExecutionEnvironment到RoundManager的技能触发系统
 */
static int GlobalDamageCallback(void* attacker, void* target, int damage, const std::string& damage_tag)
{
    if (!g_current_round_manager || !attacker || !target) {
        // 如果没有RoundManager，直接应用伤害（无技能触发）
        Character* tgt = static_cast<Character*>(target);
        if (tgt) {
            return tgt->TakeDamage(damage);
        }
        return 0;
    }
    
    Character* atk = static_cast<Character*>(attacker);
    Character* tgt = static_cast<Character*>(target);
    
    // 查找对应的shared_ptr从characters列表中
    std::shared_ptr<Character> atk_ptr;
    std::shared_ptr<Character> tgt_ptr;
    
    for (auto& ch : g_current_round_manager->GetAllCharacters()) {
        if (ch.get() == atk) atk_ptr = ch;
        if (ch.get() == tgt) tgt_ptr = ch;
    }
    
    // 如果找到对应的指针，通过RoundManager应用伤害并触发技能
    if (tgt_ptr) {
        return g_current_round_manager->ApplyDamageWithTrigger(
            atk_ptr,  // 可能为nullptr，ApplyDamageWithTrigger会根据需要处理
            tgt_ptr,
            damage,
            damage_tag);
    }
    
    // 如果没找到对应的指针，直接应用伤害
    return tgt->TakeDamage(damage);
}

// ============ 辅助函数 ============

/**
 * @brief 将NATK选项英文名称翻译为中文
 */
static std::string TranslateAnkeOptionName(const std::string& english_name) {
    if (english_name == "evade") return "回避";
    if (english_name == "small_damage") return "小伤害";
    if (english_name == "medium_damage") return "中等伤害";
    if (english_name == "large_damage") return "大伤害";
    if (english_name == "extreme_damage") return "极大伤害";
    if (english_name == "critical_event") return "暴击";
    return english_name;  // 未知选项返回原名
}

/**
 * @brief 将伤害等级索引转换为中文名称
 * d1 = 小伤害, d2 = 中等伤害, d3 = 大伤害, d4 = 极大伤害 
 */
static std::string GetDamageTypeName(int dmg_index) {
    switch (dmg_index) {
        case 0: return "小伤害 (d1)";
        case 1: return "中等伤害 (d2)";
        case 2: return "大伤害 (d3)";
        case 3: return "极大伤害 (d4)";
        default: return "未知伤害类型";
    }
}

RoundManager::RoundManager()
    : battle_(nullptr),
      current_actor_(nullptr),
      current_round_(0),
      actor_index_(0),
      is_running_(false),
      is_paused_(false),
      last_error_("") {
    // 初始化随机数种子，确保每个 RoundManager 实例有不同的随机数序列
    srand((unsigned int)time(nullptr));
}

RoundManager::~RoundManager() {
    battle_.reset();
    characters_.clear();
    events_.clear();
    
    // 清除全局RoundManager指针和伤害回调
    if (g_current_round_manager == this) {
        g_current_round_manager = nullptr;
        ExecutionEnvironment::SetDamageCallback(nullptr);
    }
}

bool RoundManager::AddCharacter(std::shared_ptr<Character> character) {
    if (!character) {
        last_error_ = "Cannot add null character";
        return false;
    }
    
    characters_.push_back(character);
    return true;
}

bool RoundManager::Initialize() {
    if (characters_.empty()) {
        last_error_ = "No characters added to battle";
        return false;
    }
    
    // 🟥【关键修复】创建战斗级 ObjectTable - 所有环境共享
    if (!shared_object_table_) {
        shared_object_table_ = std::make_shared<ObjectTable>();
    }
    
    // 🟥【Phase 4】初始化 global 容器
    // 创建一个空 SchemaValue 作为全局字段存储
    if (global_handle_.IsNull()) {
        try {
            SchemaValue empty_schema;
            global_handle_ = shared_object_table_->Create(empty_schema);
            
            char global_init_log[256];
            snprintf(global_init_log, sizeof(global_init_log),
                    "[DECLARE][GLOBAL_INIT] global_handle initialized: %llu\n",
                    (unsigned long long)global_handle_.GetID());
            AppendSkillTriggerLog(global_init_log);
        } catch (const std::exception& ex) {
            char err_log[512];
            snprintf(err_log, sizeof(err_log),
                    "[DECLARE][GLOBAL_INIT_ERROR] Failed to create global container: %s\n",
                    ex.what());
            AppendSkillTriggerLog(err_log);
            last_error_ = "Failed to create global container: " + std::string(ex.what());
            return false;
        }
    }
    
    // 创建底层战斗对象
    battle_ = std::make_unique<Battle>();
    
    if (!battle_->Initialize(characters_)) {
        last_error_ = "Failed to initialize battle";
        return false;
    }
    
    if (!battle_->Start()) {
        last_error_ = "Failed to start battle";
        return false;
    }
    
    current_round_ = 1;
    actor_index_ = 0;
    is_running_ = true;
    is_paused_ = false;
    current_actor_ = nullptr;
    events_.clear();
    
    // 设置全局RoundManager指针和伤害回调
    g_current_round_manager = this;
    ExecutionEnvironment::SetDamageCallback(GlobalDamageCallback);
    
    // 记录战斗开始事件
    RoundEvent start_event;
    start_event.type = RoundEventType::ROUND_START;
    start_event.description = "Battle initialized with " + std::to_string(characters_.size()) + " characters";
    start_event.round_number = 0;
    start_event.actor = nullptr;
    RecordEvent(start_event);
    
    return true;
}

void RoundManager::ForceStart() {
    // 强制启动RoundManager - 用于从保存状态恢复
    // 当 Initialize() 失败但我们仍想继续战斗时使用
    
    // 简单地设置必要的标志以使战斗能够继续
    is_running_ = true;
    is_paused_ = false;
    
    // 如果 battle_ 还未初始化，记录警告
    if (!battle_) {
        last_error_ = "Warning: ForceStart called without initialized Battle object";
    }
    
    // 如果还没有设置全局指针，设置它
    if (g_current_round_manager != this) {
        g_current_round_manager = this;
        ExecutionEnvironment::SetDamageCallback(GlobalDamageCallback);
    }
}

void RoundManager::ClearAllCharacters() {
    // 清除所有参战角色并重置战斗状态
    // 用于 .abot script 命令开始新战斗时
    
    characters_.clear();        // 清除所有角色
    events_.clear();            // 清除事件记录
    current_round_ = 0;         // 重置回合计数
    actor_index_ = 0;           // 重置行动者索引
    current_actor_ = nullptr;   // 清除当前行动者
    is_running_ = false;        // 停止战斗
    is_paused_ = false;         // 清除暂停状态
    last_error_ = "";           // 清除错误信息
    skill_trigger_log_ = "";    // 清除技能触发日志
    
    if (battle_) {
        battle_.reset();        // 重置战斗对象
    }
}

bool RoundManager::ExecuteNextRound() {
    // 【诊断】在最开始立即输出 fprintf，确保能看到函数被调用
    fprintf(stderr, "[RoundManager::ExecuteNextRound] ENTRY POINT\n");
    fflush(stderr);
    
    // 【诊断】不清空缓冲区，直接追加日志（这样异常发生时日志不会丢失）
    {
        std::string entry_diag = "[RoundManager::ExecuteNextRound] Entered function, current_round=" + std::to_string(current_round_);
        AppendSkillTriggerLog(entry_diag);
        fprintf(stderr, "%s\n", entry_diag.c_str());
        fflush(stderr);
    }
    
    // 清空技能触发日志缓冲区（每个回合开始时）
    ClearSkillTriggerLog();
    
    // 🟥 【诊断 1】Round 开始时的 Character 状态（包括 atk 和 dmg）
    {
        AppendSkillTriggerLog("[ROUND_BEGIN_SNAPSHOT] Round=" + std::to_string(current_round_) + " Characters:");
        for (size_t i = 0; i < characters_.size(); ++i) {
            if (characters_[i]) {
                const auto& ch = characters_[i];
                char buf[512];
                snprintf(buf, sizeof(buf),
                    "[CHAR_ROUND_BEGIN] idx=%zu name=%s atk=%d dmg=[%d,%d,%d,%d] turn.mult=%.2f hp=%d/%d alive=%d",
                    i,
                    ch->name.c_str(),
                    ch->atk,
                    ch->dmg[0], ch->dmg[1], ch->dmg[2], ch->dmg[3],
                    ch->turn.multiplier,
                    ch->hp,
                    ch->max_hp,
                    ch->is_alive ? 1 : 0);
                AppendSkillTriggerLog(buf);
            }
        }
    }

    // 【诊断1.5】Round 开始时的 character->extra 状态
    {
        AppendSkillTriggerLog("[CHAR_EXTRA_ROUND_BEGIN] Round=" + std::to_string(current_round_) + " Characters extra:");
        for (size_t i = 0; i < characters_.size(); ++i) {
            if (characters_[i]) {
                const auto& ch = characters_[i];
                char buf[512];
                int atk_num = 0, d1 = 0, d2 = 0, d3 = 0, d4 = 0;
                if (ch->extra.find("atk") != ch->extra.end()) {
                    Value atk_val = ch->extra["atk"];
                    if (atk_val.IsSchema() && atk_val.HasField("value")) {
                        atk_num = (int)atk_val.GetField("value").GetInt();
                    }
                }
                if (ch->extra.find("dmg") != ch->extra.end()) {
                    Value dmg_val = ch->extra["dmg"];
                    if (dmg_val.IsSchema()) {
                        if (dmg_val.HasField("d1")) d1 = (int)dmg_val.GetField("d1").GetInt();
                        if (dmg_val.HasField("d2")) d2 = (int)dmg_val.GetField("d2").GetInt();
                        if (dmg_val.HasField("d3")) d3 = (int)dmg_val.GetField("d3").GetInt();
                        if (dmg_val.HasField("d4")) d4 = (int)dmg_val.GetField("d4").GetInt();
                    }
                }
                snprintf(buf, sizeof(buf),
                    "[CHAR_EXTRA] idx=%zu name=%s extra.size()=%zu atk=%d dmg=[%d,%d,%d,%d]",
                    i, ch->name.c_str(), ch->extra.size(), atk_num, d1, d2, d3, d4);
                AppendSkillTriggerLog(buf);
            }
        }
    }
    
    // 【诊断】在最开始输出日志，验证代码是否执行到这里
    {
        std::string diag_msg = "[EXEC_ROUND_START] ExecuteNextRound() called, round " + std::to_string(current_round_ + 1);
        AppendSkillTriggerLog(diag_msg);
        fprintf(stderr, "%s\n", diag_msg.c_str());
    }
    
    // ✨ 【新增】每回合开始时重置所有角色的 turn 数据
    {
        std::string reset_msg = "[EXEC_ROUND_RESET_TURN] Resetting turn data for " + std::to_string(characters_.size()) + " characters";
        AppendSkillTriggerLog(reset_msg);
        fprintf(stderr, "%s\n", reset_msg.c_str());
        fflush(stderr);
    }
    
    for (auto& ch : characters_) {
        if (ch && ch->IsAlive()) {
            ch->turn.multiplier = 1.0;
            std::string ch_msg = "[EXEC_ROUND_RESET_TURN] Reset " + ch->name + ".turn.multiplier = 1.0";
            AppendSkillTriggerLog(ch_msg);
        }
    }
    
    {
        std::string check_msg = "[EXEC_ROUND_CHECK_RUNNING] Checking is_running_=" + std::to_string(is_running_ ? 1 : 0);
        AppendSkillTriggerLog(check_msg);
        fprintf(stderr, "%s\n", check_msg.c_str());
        fflush(stderr);
    }
    
    if (!is_running_) {
        last_error_ = "Battle is not running";
        return false;
    }
    
    if (is_paused_) {
        last_error_ = "Battle is paused";
        return false;
    }
    
    // 【安全检查】如果battle_为nullptr，无法继续执行
    // 这发生在状态恢复时Initialize()失败的情况下
    if (!battle_) {
        last_error_ = "Battle object not initialized - cannot execute round without battle context";
        is_running_ = false;
        return false;
    }
    
    if (battle_->IsFinished()) {
        is_running_ = false;
        last_error_ = "Battle already finished";
        return false;
    }
    
    // 检查是否还有两个以上的阵营活着
    std::set<int> live_camps;
    for (const auto& ch : characters_) {
        if (ch && ch->IsAlive()) {
            live_camps.insert(ch->camp);
        }
    }
    
    if (live_camps.size() < 2) {
        is_running_ = false;
        last_error_ = "Not enough camps alive";
        return false;
    }
    
    // 记录回合开始
    RoundEvent round_start;
    round_start.type = RoundEventType::ROUND_START;
    round_start.description = "Round " + std::to_string(current_round_) + " started";
    round_start.round_number = current_round_;
    RecordEvent(round_start);
    
    // 触发所有角色的OnTurnStart技能
    // 根据d100+atk选择行动者
    current_actor_ = SelectActorByInitiative();
    
    if (!current_actor_ || !current_actor_->IsAlive()) {
        last_error_ = "Failed to select actor for round";
        return false;
    }
    
    // 记录行动者信息
    RoundEvent actor_start;
    actor_start.type = RoundEventType::ACTOR_TURN_START;
    actor_start.description = current_actor_->name + " 开始行动";  // 简化，详细信息由SelectActorByInitiative输出
    actor_start.round_number = current_round_;
    actor_start.actor = current_actor_;
    RecordEvent(actor_start);
    
    // 执行行动者的Anke（普通攻击或技能）
    if (!ExecuteAnkeAction()) {
        // Anke执行失败，记录错误但继续回合
        RoundEvent error_event;
        error_event.type = RoundEventType::ERROR;
        error_event.description = "Failed to execute Anke for " + current_actor_->name + ": " + last_error_;
        error_event.round_number = current_round_;
        error_event.actor = current_actor_;
        RecordEvent(error_event);
    }
    
    // 记录行动结束
    /*
    RoundEvent actor_end;
    actor_end.type = RoundEventType::ACTOR_TURN_END;
    actor_end.description = current_actor_->name + "'s action complete";
    actor_end.round_number = current_round_;
    actor_end.actor = current_actor_;
    RecordEvent(actor_end);*/
    
    // 检查战斗是否结束
    int victory_camp = battle_->GetVictoryCamp();
    if (victory_camp != 0) {
        is_running_ = false;
        
        RoundEvent battle_end;
        battle_end.type = RoundEventType::BATTLE_END;
        battle_end.description = "Battle ended! Camp " + std::to_string(victory_camp) + " wins!";
        battle_end.round_number = current_round_;
        battle_end.parameter = victory_camp;
        RecordEvent(battle_end);
        
        return true;
    }
    
    // 🟥 【诊断 2】Round 结束时的 Character 状态（包括 atk 和 dmg）
    {
        AppendSkillTriggerLog("[ROUND_END_SNAPSHOT] Round=" + std::to_string(current_round_) + " Characters:");
        for (size_t i = 0; i < characters_.size(); ++i) {
            if (characters_[i]) {
                const auto& ch = characters_[i];
                char buf[512];
                snprintf(buf, sizeof(buf),
                    "[CHAR_ROUND_END] idx=%zu name=%s atk=%d dmg=[%d,%d,%d,%d] turn.mult=%.2f hp=%d/%d alive=%d",
                    i,
                    ch->name.c_str(),
                    ch->atk,
                    ch->dmg[0], ch->dmg[1], ch->dmg[2], ch->dmg[3],
                    ch->turn.multiplier,
                    ch->hp,
                    ch->max_hp,
                    ch->is_alive ? 1 : 0);
                AppendSkillTriggerLog(buf);
            }
        }
    }

    // 【诊断2.5】Round 结束时的 character->extra 状态
    {
        AppendSkillTriggerLog("[CHAR_EXTRA_ROUND_END] Round=" + std::to_string(current_round_) + " Characters extra:");
        for (size_t i = 0; i < characters_.size(); ++i) {
            if (characters_[i]) {
                const auto& ch = characters_[i];
                char buf[512];
                int atk_num = 0, d1 = 0, d2 = 0, d3 = 0, d4 = 0;
                if (ch->extra.find("atk") != ch->extra.end()) {
                    Value atk_val = ch->extra["atk"];
                    if (atk_val.IsSchema() && atk_val.HasField("value")) {
                        atk_num = (int)atk_val.GetField("value").GetInt();
                    }
                }
                if (ch->extra.find("dmg") != ch->extra.end()) {
                    Value dmg_val = ch->extra["dmg"];
                    if (dmg_val.IsSchema()) {
                        if (dmg_val.HasField("d1")) d1 = (int)dmg_val.GetField("d1").GetInt();
                        if (dmg_val.HasField("d2")) d2 = (int)dmg_val.GetField("d2").GetInt();
                        if (dmg_val.HasField("d3")) d3 = (int)dmg_val.GetField("d3").GetInt();
                        if (dmg_val.HasField("d4")) d4 = (int)dmg_val.GetField("d4").GetInt();
                    }
                }
                snprintf(buf, sizeof(buf),
                    "[CHAR_EXTRA] idx=%zu name=%s extra.size()=%zu atk=%d dmg=[%d,%d,%d,%d]",
                    i, ch->name.c_str(), ch->extra.size(), atk_num, d1, d2, d3, d4);
                AppendSkillTriggerLog(buf);
            }
        }
    }
    
    // 记录回合结束
    RoundEvent round_end;
    round_end.type = RoundEventType::ROUND_END;
    round_end.description = "Round " + std::to_string(current_round_) + " ended";
    round_end.round_number = current_round_;
    RecordEvent(round_end);
    
    // 触发所有角色的OnTurnEnd技能
    for (auto& ch : characters_) {
        if (ch && ch->is_alive) {
            SkillTriggerMessage turn_end_msg;
            TriggerPassiveSkills("OnTrunEndSkill", ch, turn_end_msg);
        }
    }
    
    current_round_++;
    return true;
}

int RoundManager::ExecuteRounds(int count) {
    if (count <= 0) return 0;
    
    int executed = 0;
    for (int i = 0; i < count; ++i) {
        if (!ExecuteNextRound()) {
            break;
        }
        executed++;
        
        if (IsFinished()) {
            break;
        }
    }
    
    return executed;
}

bool RoundManager::SkipCurrentRound() {
    if (!is_running_) {
        last_error_ = "Battle is not running";
        return false;
    }
    
    RoundEvent skip_event;
    skip_event.type = RoundEventType::ROUND_START;
    skip_event.description = "Round " + std::to_string(current_round_) + " skipped";
    skip_event.round_number = current_round_;
    RecordEvent(skip_event);
    
    current_round_++;
    return true;
}

void RoundManager::Pause() {
    is_paused_ = true;
    RoundEvent pause_event;
    pause_event.type = RoundEventType::ROUND_START;
    pause_event.description = "Battle paused at round " + std::to_string(current_round_);
    pause_event.round_number = current_round_;
    RecordEvent(pause_event);
}

void RoundManager::Resume() {
    is_paused_ = false;
    RoundEvent resume_event;
    resume_event.type = RoundEventType::ROUND_START;
    resume_event.description = "Battle resumed from round " + std::to_string(current_round_);
    resume_event.round_number = current_round_;
    RecordEvent(resume_event);
}

bool RoundManager::IsRunning() const {
    return is_running_ && !is_paused_;
}

bool RoundManager::IsFinished() const {
    return !is_running_ || (battle_ && battle_->IsFinished());
}

std::vector<std::shared_ptr<Character>> RoundManager::GetLiveCharactersByCamp(int camp) const {
    std::vector<std::shared_ptr<Character>> result;
    for (const auto& ch : characters_) {
        if (ch && ch->camp == camp && ch->IsAlive()) {
            result.push_back(ch);
        }
    }
    return result;
}

int RoundManager::GetVictoryCamp() const {
    if (battle_) {
        return battle_->GetVictoryCamp();
    }
    return 0;
}

std::vector<RoundEvent> RoundManager::GetLastEvents(int count) const {
    std::vector<RoundEvent> result;
    if (count <= 0) return result;
    
    int start_index = static_cast<int>(events_.size()) - count;
    if (start_index < 0) start_index = 0;
    
    for (int i = start_index; i < static_cast<int>(events_.size()); ++i) {
        result.push_back(events_[i]);
    }
    
    return result;
}

bool RoundManager::ExecuteCommand(const std::string& command, const std::string& parameters) {
    if (command == "advance") {
        // 推进指定数量的回合或单个回合
        if (!parameters.empty()) {
            try {
                int count = std::stoi(parameters);
                int executed = ExecuteRounds(count);
                return executed > 0;
            }
            catch (...) {
                last_error_ = "Invalid parameter for advance command";
                return false;
            }
        }
        return ExecuteNextRound();
    }
    else if (command == "skip") {
        return SkipCurrentRound();
    }
    else if (command == "pause") {
        Pause();
        return true;
    }
    else if (command == "resume") {
        Resume();
        return true;
    }
    else if (command == "restart") {
        // 不重新初始化，而是重置状态
        current_round_ = 1;
        actor_index_ = 0;
        is_running_ = true;
        is_paused_ = false;
        current_actor_ = nullptr;
        events_.clear();
        return true;
    }
    else if (command == "status") {
        // 状态查询不改变状态
        return true;
    }
    else {
        last_error_ = "Unknown command: " + command;
        return false;
    }
}

std::vector<std::string> RoundManager::GetAvailableCommands() const {
    return {
        "advance",      // 推进一回合
        "advance N",    // 推进N个回合
        "skip",         // 跳过当前回合
        "pause",        // 暂停战斗
        "resume",       // 恢复战斗
        "restart",      // 重新开始
        "status"        // 查询状态
    };
}

std::string RoundManager::GetStatusSummary() const {
    std::ostringstream oss;
    
    oss << "=== Battle Status ===\n";
    oss << "当前回合 " << current_round_-1 << "\n";
    oss << "Is Running: " << (is_running_ ? "Yes" : "No") << "\n";
    oss << "Is Paused: " << (is_paused_ ? "Yes" : "No") << "\n";
    oss << "Current Actor: " << (current_actor_ ? current_actor_->name : "None") << "\n";
    oss << "Total Characters: " << characters_.size() << "\n";
    
    int alive_count = 0;
    for (const auto& ch : characters_) {
        if (ch && ch->IsAlive()) alive_count++;
    }
    oss << "Alive Characters: " << alive_count << "\n";
    
    oss << "\nCharacters:\n";
    for (const auto& ch : characters_) {
        if (!ch) continue;
        oss << "  - " << ch->name 
            << " (Camp " << ch->camp 
            << ", HP " << ch->hp << "/" << ch->max_hp 
            << ", Alive: " << (ch->IsAlive() ? "Yes" : "No") << ")\n";
    }
    
    if (IsFinished()) {
        int victory_camp = GetVictoryCamp();
        if (victory_camp != 0) {
            oss << "\nBattle Finished! Camp " << victory_camp << " wins!\n";
        }
    }
    
    return oss.str();
}

std::string RoundManager::GetBattleLog() const {
    std::ostringstream oss;
    
    oss << "=== Battle Log ===\n";
    oss << "Total Events: " << events_.size() << "\n\n";
    
    for (const auto& event : events_) {
        oss << "[Round " << event.round_number << "] ";
        
        switch (event.type) {
            case RoundEventType::ROUND_START:
                oss << "[ROUND START] ";
                break;
            case RoundEventType::ACTOR_TURN_START:
                oss << "[TURN START] ";
                break;
            case RoundEventType::ACTOR_ACTION:
                oss << "[ACTION] ";
                break;
            case RoundEventType::ACTOR_TURN_END:
                oss << "[TURN END] ";
                break;
            case RoundEventType::ROUND_END:
                oss << "[ROUND END] ";
                break;
            case RoundEventType::BATTLE_END:
                oss << "[BATTLE END] ";
                break;
            case RoundEventType::ERROR:
                oss << "[ERROR] ";
                break;
        }
        
        oss << event.description << "\n";
    }
    // 添加技能触发日志（包含所有脚本执行、伤害计算等信息）
    if (!skill_trigger_log_.empty()) {
        //oss << "\n=== Skill Trigger Details ===\n";
        oss << skill_trigger_log_;
    }
    return oss.str();
}

std::string RoundManager::GetSkillTriggerLog() const {
    return skill_trigger_log_;
}

void RoundManager::AppendSkillTriggerLog(const std::string& log_entry) {
    skill_trigger_log_ += log_entry;
}

void RoundManager::ClearSkillTriggerLog() {
    skill_trigger_log_.clear();
}

bool RoundManager::SelectNextActor() {
    std::vector<std::shared_ptr<Character>> live_chars = GetLiveCharactersByCamp(1);
    auto camp2_chars = GetLiveCharactersByCamp(2);
    live_chars.insert(live_chars.end(), camp2_chars.begin(), camp2_chars.end());
    
    if (live_chars.empty()) {
        return false;
    }
    
    if (actor_index_ >= live_chars.size()) {
        actor_index_ = 0;
    }
    
    current_actor_ = live_chars[actor_index_];
    actor_index_++;
    
    return true;
}

std::shared_ptr<Character> RoundManager::SelectActorByInitiative() {
    std::vector<std::shared_ptr<Character>> live_chars;
    
    // 获取所有活着的角色
    for (const auto& ch : characters_) {
        if (ch && ch->IsAlive()) {
            live_chars.push_back(ch);
        }
    }
    
    if (live_chars.empty()) return nullptr;
    
    // 计算每个角色的d100 + atk分数，并记录详细信息
    std::vector<int> rolls;
    std::vector<int> d100_parts;  // 仅D100的结果
    int highest_roll = -1;
    
    std::ostringstream log_stream;
    log_stream << "[行动掷骰]\n";
    
    for (const auto& ch : live_chars) {
        int d100_roll = (rand() % 100) + 1;  // D100: 1-100
        int total_roll = d100_roll + ch->atk;
        
        d100_parts.push_back(d100_roll);
        rolls.push_back(total_roll);
        
        // 构建日志：角色名：d100+ATK = 总值
        log_stream << ch->name << ": d100=" << d100_roll << " + " << ch->atk << " = " << total_roll << "\n";
        
        if (total_roll > highest_roll) {
            highest_roll = total_roll;
        }
    }
    
    // 收集所有掷出最高分的角色
    std::vector<size_t> highest_indices;
    for (size_t i = 0; i < rolls.size(); i++) {
        if (rolls[i] == highest_roll) {
            highest_indices.push_back(i);
        }
    }
    
    // 从最高分的角色中随机选择一个（处理同数值情况）
    size_t selected_index = highest_indices[rand() % highest_indices.size()];
    std::shared_ptr<Character> selected_char = live_chars[selected_index];
    
    // 追加选中结果到日志
    log_stream << "[本轮攻击者] " << selected_char->name << "\n";
    
    // 输出详细日志
    AppendSkillTriggerLog(log_stream.str());
    
    return selected_char;
}

bool RoundManager::ExecuteAnkeAction() {
    // 【绑定】使 VM 能访问当前 RoundManager 实例
    g_current_round_manager = this;
    
    // 🟥 【任务1】添加角色列表快照
    {
        AppendSkillTriggerLog("[DIAG][SNAPSHOT] ExecuteAnkeAction START - Character list:");
        for (size_t i = 0; i < characters_.size(); ++i) {
            if (characters_[i]) {
                const auto& ch = characters_[i];
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "  idx=%zu name=%s camp=%d hp=%d/%d",
                    i,
                    ch->name.c_str(),
                    ch->camp,
                    ch->hp,
                    ch->max_hp);
                AppendSkillTriggerLog(buf);
            }
        }
    }
    
    if (!current_actor_ || !current_actor_->IsAlive()) {
        last_error_ = "Current actor is null or dead";
        g_current_round_manager = nullptr;
        return false;
    }
    
    // 【诊断】ExecuteAnkeAction 开始执行 - 直接用 this
    {
        std::string diag_msg = "[RoundManager] ExecuteAnkeAction started for: " + current_actor_->name;
        AppendSkillTriggerLog(diag_msg);
        fprintf(stderr, "%s\n", diag_msg.c_str());
    }
    
    // 获取Anke预设名称
    // 默认使用"natk"（普通攻击），如果有ActSkill则使用其名称
    std::string anke_name = "natk";
    if (!current_actor_->skills.empty() && current_actor_->skills[0].id == "ActSkill") {
        anke_name = current_actor_->skills[0].name;
    }
    
    // 从PresetRegistry获取Anke预设
    PresetRegistry* registry = PresetRegistry::GetInstance();
    if (!registry) {
        last_error_ = "PresetRegistry not available";
        g_current_round_manager = nullptr;
        return false;
    }
    
    AnkePreset* anke_preset = registry->GetAnke(anke_name);
    if (!anke_preset) {
        last_error_ = "Anke preset not found: " + anke_name;
        g_current_round_manager = nullptr;
        return false;
    }
    
    // 确定敌对阵营
    int enemy_camp = (current_actor_->camp == 1) ? 2 : 1;
    
    // 获取敌对阵营中的所有活着的角色
    auto enemies = GetLiveCharactersByCamp(enemy_camp);
    
    if (enemies.empty()) {
        last_error_ = "No valid targets in enemy camp";
        g_current_round_manager = nullptr;
        return false;
    }
    
    // 随机选择一个敌人作为目标
    std::shared_ptr<Character> target = enemies[rand() % enemies.size()];
    
    // 记录目标执行前的HP（用于计算伤害）
    int target_hp_before = target->hp;
    
    // 初始化攻击倍增系数（默认为1）
    // 对于NATK，这会在大成功/大失败时被修改
    int attack_multiplier = 1;
    
    // 创建执行环境并执行Anke
    ExecutionEnvironment env(current_actor_.get(), target.get(), battle_.get());
    
    // 🟥【关键修复】复用战斗级别的 ObjectTable，不创建新的
    if (shared_object_table_) {
        env.SetSharedObjectTable(shared_object_table_);
    }
    
    // 🟥【Phase 4】注册全局变量 global
    if (!global_handle_.IsNull()) {
        Value global_val;
        global_val.SetHandle(global_handle_);
        env.SetValueProperty("global", global_val);
        
        char global_reg_log[512];
        snprintf(global_reg_log, sizeof(global_reg_log),
                "[DECLARE][GLOBAL_REGISTER] Registered global handle: %llu\n",
                (unsigned long long)global_handle_.GetID());
        AppendSkillTriggerLog(global_reg_log);
    } else {
        char global_err_log[256];
        snprintf(global_err_log, sizeof(global_err_log),
                "[DECLARE][GLOBAL_REGISTER_ERROR] global_handle is null\n");
        AppendSkillTriggerLog(global_err_log);
    }
    
    env.SetIntProperty("attack_multiplier", attack_multiplier);
    
    // �【修复】注册行动者数据 - env.self 必须指向行动者！
    // 【关键】只注册行动者，不调用 RegisterCharacterData(target)，
    // 因为第二次调用会覆盖 env.self！
    if (env.GetActor()) {
        env.RegisterSelf(env.GetActor());     // 行动者 -> self
    }
    if (env.GetTarget()) {
        env.RegisterTarget(env.GetTarget());  // 目标 -> target
    }
    
    // 🟥【任务3】在执行脚本前检查 actor 和 env.self 的一致性
    {
        Value env_self = env.GetValueProperty("self");
        bool env_self_is_handle = env_self.IsHandle();
        int env_self_handle_id = env_self_is_handle ? (int)env_self.GetHandle().GetID() : -1;
        
        char consistency_buf[256];
        snprintf(consistency_buf, sizeof(consistency_buf),
            "[DIAG][CONSISTENCY] Before script: actor=%s ptr=%p vs env.self handle=%d",
            current_actor_->name.c_str(),
            (void*)current_actor_.get(),
            env_self_handle_id);
        AppendSkillTriggerLog(consistency_buf);
    }
    
    int result = anke_preset->Execute(&env);
    
    // 获取AnkePreset生成的掷骰信息
    int anke_random_value = anke_preset->GetLastRandomValue();
    int anke_total_weight = anke_preset->GetTotalWeight();
    
    // 获取选中的选项信息
    const AnkeOption* selected_option = anke_preset->GetSelectedOption();
    std::string option_name = (selected_option) ? selected_option->name : "unknown";
    std::string option_name_cn = TranslateAnkeOptionName(option_name);
    
    // 【重要】es/ef 的处理现在由脚本完全负责
    // AnkePreset::Execute() 在执行 es/ef 脚本时会进行 D2 投掷，调用 shiftattacker()，
    // 并输出结果到战斗日志。此处不再进行重复处理。
    
    // 记录Anke执行事件
    RoundEvent action_event;
    action_event.type = RoundEventType::ACTOR_ACTION;
    
    // 构建简洁的描述信息
    std::string anke_name_cn = (anke_name == "natk") ? "普通攻击" : anke_name;
    std::string detail_desc = "【" + current_actor_->name + "】执行" + anke_name_cn + " > 【" + 
                              target->name + "】\n";
    detail_desc += "  投掷D" + std::to_string(anke_total_weight) + "=" + 
                  std::to_string(anke_random_value) + " > 选中【" + option_name_cn + "】\n";
    
    // 计算实际伤害（脚本执行后）
    int target_hp_after = target->hp;
    int actual_damage = target_hp_before - target_hp_after;
    
    if (actual_damage > 0) {
        detail_desc += "  伤害结果: " + std::to_string(target_hp_before) + " → " + 
                      std::to_string(target_hp_after) + " (-" + std::to_string(actual_damage) + ")\n";
    } else if (option_name == "evade") {
        detail_desc += "  [回避成功]\n";
    }
    
    action_event.description = detail_desc;
    action_event.round_number = current_round_;
    action_event.actor = current_actor_;
    action_event.target = target;
    action_event.parameter = actual_damage;
    RecordEvent(action_event);
    
    g_current_round_manager = nullptr;
    return true;
}

bool RoundManager::ExecuteActorSkill() {
    if (!current_actor_ || !current_actor_->IsAlive()) {
        last_error_ = "Current actor is null or dead";
        return false;
    }
    
    // 获取当前角色的技能列表
    if (current_actor_->skills.empty()) {
        last_error_ = "No skills available for " + current_actor_->name;
        return false;
    }
    
    // 选择第一个有效的技能（可扩展为选择最佳技能的逻辑）
    const SkillParam& skill = current_actor_->skills[0];
    
    // 确定敌对阵营
    int enemy_camp = (current_actor_->camp == 1) ? 2 : 1;
    
    // 获取敌对阵营中的所有活着的角色
    auto enemies = GetLiveCharactersByCamp(enemy_camp);
    
    if (enemies.empty()) {
        last_error_ = "No valid targets in enemy camp";
        return false;
    }
    
    // 随机选择一个敌人作为目标
    std::shared_ptr<Character> target = enemies[rand() % enemies.size()];
    
    // 从 PresetRegistry 获取技能预设
    PresetRegistry* registry = PresetRegistry::GetInstance();
    if (!registry) {
        last_error_ = "PresetRegistry not available";
        return false;
    }
    
    SkillPreset* skill_preset = registry->GetSkill(skill.id);
    if (!skill_preset) {
        last_error_ = "Skill preset not found: " + skill.id;
        return false;
    }
    
    // 创建执行环境（会自动入栈/出栈）
    ExecutionEnvironment env(current_actor_.get(), target.get(), battle_.get());
    
    // 执行技能预设
    int result = skill_preset->Execute(&env);
    
    // 记录技能执行事件
    RoundEvent action_event;
    action_event.type = RoundEventType::ACTOR_ACTION;
    action_event.description = current_actor_->name + " used skill [" + skill.name + "] on " + target->name;
    action_event.round_number = current_round_;
    action_event.actor = current_actor_;
    action_event.parameter = result;  // 存储执行结果
    RecordEvent(action_event);
    
    return true;
}

// ============ 技能触发系统实现 ============

int RoundManager::TriggerPassiveSkills(
    const std::string& trigger_type,
    std::shared_ptr<Character> target_character,
    const SkillTriggerMessage& message) {
    
    {
        char entry_buf[512];
        snprintf(entry_buf, sizeof(entry_buf),
                "[SKILL_TRIGGER_ENTRY] trigger_type=%s, target_character=%s, message.Name=%s\n",
                trigger_type.c_str(),
                target_character ? target_character->name.c_str() : "null",
                message.Name.c_str());
        AppendSkillTriggerLog(entry_buf);
    }
    
    if (!battle_ || !target_character) {
        char error_buf[256];
        snprintf(error_buf, sizeof(error_buf),
                "[SKILL_TRIGGER_ERROR] battle_=%p, target_character=%p, returning 0\n",
                (void*)battle_.get(),
                target_character.get());
        AppendSkillTriggerLog(error_buf);
        return 0;
    }
    
    // 创建技能执行环境
    ExecutionEnvironment env(target_character.get(), nullptr, battle_.get());
    
    AppendSkillTriggerLog("[SKILL_TRIGGER_CALLING_SYSTEM] About to call SkillTriggerSystem::TriggerSkillsByType\n");
    int result = SkillTriggerSystem::TriggerSkillsByType(
        trigger_type,
        characters_,
        target_character,
        message,
        battle_.get(),
        &env);
    
    {
        char result_buf[256];
        snprintf(result_buf, sizeof(result_buf),
                "[SKILL_TRIGGER_RESULT] trigger_type=%s, result=%d\n",
                trigger_type.c_str(),
                result);
        AppendSkillTriggerLog(result_buf);
    }
    
    // 提取VirtualMachine的诊断日志并追加到日志缓冲区
    /*std::string vm_diag_log = env.GetDiagnosticLog();
    {
        std::ostringstream oss;
        oss << "[DIAGNOSTIC_EXTRACTION] skill_type=" << trigger_type 
            << ", vm_diag_log_length=" << vm_diag_log.length();
        AppendSkillTriggerLog(oss.str() + "\n");
    }*/
    
    /*if (!vm_diag_log.empty()) {
        AppendSkillTriggerLog("[VM_DIAGNOSTICS_BEGIN]\n");
        AppendSkillTriggerLog(vm_diag_log);
        AppendSkillTriggerLog("[VM_DIAGNOSTICS_END]\n");
    } else {
        AppendSkillTriggerLog("[WARNING] No VM diagnostics collected\n");
    }*/
    
    return result;
}

int RoundManager::ApplyDamageWithTrigger(
    std::shared_ptr<Character> attacker,
    std::shared_ptr<Character> target,
    int damage_value,
    const std::string& damage_tag) {
    
    if (!target) {
        return 0;
    }
    
    // 诊断：伤害应用开始
    {
        char diag_buf[512];
        snprintf(diag_buf, sizeof(diag_buf),
                "[DAMAGE_APPLY_START] target=%s, damage=%d, attacker=%s\n",
                target->name.c_str(),
                damage_value,
                attacker ? attacker->name.c_str() : "null");
        AppendSkillTriggerLog(diag_buf);
    }
    
    // 阶段1: 命中阶段（进入伤害函数时触发）
    // 触发目标的 onHitTaken 技能
    {
        char trigger_buf[256];
        snprintf(trigger_buf, sizeof(trigger_buf),
                "[TRIGGER_HITTAKEN_PRE] target=%s, trigger_type=onHitTakenSkill\n",
                target->name.c_str());
        AppendSkillTriggerLog(trigger_buf);
    }
    
    SkillTriggerMessage hit_msg_taken;
    hit_msg_taken.Name = attacker ? attacker->name : "";
    int hittaken_result = TriggerPassiveSkills("onHitTakenSkill", target, hit_msg_taken);
    
    {
        char trigger_buf[256];
        snprintf(trigger_buf, sizeof(trigger_buf),
                "[TRIGGER_HITTAKEN_POST] result=%d\n",
                hittaken_result);
        AppendSkillTriggerLog(trigger_buf);
    }
    
    // 触发发起者的 onHitDealt 技能
    if (attacker) {
        {
            char trigger_buf[256];
            snprintf(trigger_buf, sizeof(trigger_buf),
                    "[TRIGGER_HITDEALT_PRE] attacker=%s, trigger_type=onHitDealtSkill\n",
                    attacker->name.c_str());
            AppendSkillTriggerLog(trigger_buf);
        }
        
        SkillTriggerMessage hit_msg_dealt;
        hit_msg_dealt.Name = target->name;
        int hitdealt_result = TriggerPassiveSkills("onHitDealtSkill", attacker, hit_msg_dealt);
        
        {
            char trigger_buf[256];
            snprintf(trigger_buf, sizeof(trigger_buf),
                    "[TRIGGER_HITDEALT_POST] result=%d\n",
                    hitdealt_result);
            AppendSkillTriggerLog(trigger_buf);
        }
    }
    
    // 处理伤害值为0的情况（回避/闪避等）
    if (damage_value <= 0) {
        AppendSkillTriggerLog("[DAMAGE_APPLY_ZERO] Damage <= 0, returning early\n");
        return 0;
    }
    
    // 阶段2: 伤害判定阶段（伤害骰点前）
    // 触发目标的 onDamageTaken 技能
    {
        char trigger_buf[256];
        snprintf(trigger_buf, sizeof(trigger_buf),
                "[TRIGGER_ONDAMAGETAKEN_PRE] target=%s, trigger_type=onDamageTaken\n",
                target->name.c_str());
        AppendSkillTriggerLog(trigger_buf);
    }
    
    SkillTriggerMessage damage_msg;
    damage_msg.Source = attacker ? attacker->name : "";
    damage_msg.Dmg = damage_value;
    damage_msg.Tag = damage_tag;
    
    int ondamage_result = TriggerPassiveSkills("onDamageTaken", target, damage_msg);
    
    {
        char trigger_buf[256];
        snprintf(trigger_buf, sizeof(trigger_buf),
                "[TRIGGER_ONDAMAGETAKEN_POST] result=%d\n",
                ondamage_result);
        AppendSkillTriggerLog(trigger_buf);
    }
    
    // 阶段2: 触发发起者的 onDamageDealt 技能
    if (attacker) {
        {
            char trigger_buf[256];
            snprintf(trigger_buf, sizeof(trigger_buf),
                    "[TRIGGER_ONDAMAGEDEALT_PRE] attacker=%s, trigger_type=onDamageDealt\n",
                    attacker->name.c_str());
            AppendSkillTriggerLog(trigger_buf);
        }
        
        SkillTriggerMessage dealt_msg;
        dealt_msg.Name = target->name;
        dealt_msg.Dmg = damage_value;
        dealt_msg.Tag = damage_tag;
        
        int ondealt_result = TriggerPassiveSkills("onDamageDealt", attacker, dealt_msg);
        
        {
            char trigger_buf[256];
            snprintf(trigger_buf, sizeof(trigger_buf),
                    "[TRIGGER_ONDAMAGEDEALT_POST] result=%d\n",
                    ondealt_result);
            AppendSkillTriggerLog(trigger_buf);
        }
    }
    
    // 阶段3: 触发场上其他单位的 onUnitAttacked、onUnitAttack
    AppendSkillTriggerLog("[TRIGGER_ONUNITATTACKED_START] Checking other units\n");
    for (auto& ch : characters_) {
        if (ch && ch != target && ch->is_alive) {
            SkillTriggerMessage unit_attacked_msg;
            unit_attacked_msg.Name = target->name;
            unit_attacked_msg.Source = attacker ? attacker->name : "";
            
            TriggerPassiveSkills("onUnitAttacked", ch, unit_attacked_msg);
        }
        
        if (ch && ch != attacker && ch->is_alive && attacker) {
            SkillTriggerMessage unit_attack_msg;
            unit_attack_msg.Name = attacker->name;
            unit_attack_msg.Source = attacker->name;
            
            TriggerPassiveSkills("onUnitAttack", ch, unit_attack_msg);
        }
    }
    AppendSkillTriggerLog("[TRIGGER_ONUNITATTACKED_END] Done checking other units\n");
    
    // 阶段4: 应用实际伤害
    AppendSkillTriggerLog("[DAMAGE_APPLY_ACTUAL] Calling target->TakeDamage()\n");
    int actual_damage = target->TakeDamage(damage_value);
    {
        char damage_buf[256];
        snprintf(damage_buf, sizeof(damage_buf),
                "[DAMAGE_APPLY_RESULT] actual_damage=%d, target_hp=%d/%d\n",
                actual_damage,
                target->hp,
                target->max_hp);
        AppendSkillTriggerLog(damage_buf);
    }
    
    // 阶段5: 检查死亡并触发 OnDead 事件
    if (!target->is_alive && actual_damage > 0) {
        AppendSkillTriggerLog("[TRIGGER_ONDEAD_START] Target is dead, triggering OnDead skills\n");
        SkillTriggerMessage dead_msg;
        dead_msg.Name = target->name;
        
        // 广播给所有活着的角色
        for (auto& ch : characters_) {
            if (ch && ch->is_alive && ch != target) {
                TriggerPassiveSkills("OnDead", ch, dead_msg);
            }
        }
        AppendSkillTriggerLog("[TRIGGER_ONDEAD_END] OnDead trigger complete\n");
    } else if (target->is_alive) {
        AppendSkillTriggerLog("[TRIGGER_ONDEAD_SKIP] Target still alive\n");
    }
    
    AppendSkillTriggerLog("[DAMAGE_APPLY_END] ApplyDamageWithTrigger complete\n");
    
    return actual_damage;
}

int RoundManager::ApplyHealWithTrigger(
    std::shared_ptr<Character> healer,
    std::shared_ptr<Character> target,
    int heal_value) {
    
    if (!target || heal_value <= 0) {
        return 0;
    }
    
    int hp_before = target->hp;
    target->Heal(heal_value);
    int actual_heal = target->hp - hp_before;
    
    // 触发 onHealSkill
    if (actual_heal > 0) {
        SkillTriggerMessage heal_msg;
        heal_msg.Name = healer ? healer->name : target->name;
        heal_msg.value = actual_heal;
        
        TriggerPassiveSkills("onHealSkill", target, heal_msg);
    }
    
    return actual_heal;
}

bool RoundManager::ShouldEndRound() {
    // 检查继续条件
    auto live_camps = std::set<int>();
    for (const auto& ch : characters_) {
        if (ch && ch->IsAlive()) {
            live_camps.insert(ch->camp);
        }
    }
    
    // 如果只有一个阵营存活，回合结束
    return live_camps.size() <= 1;
}

void RoundManager::RecordEvent(const RoundEvent& event) {
    events_.push_back(event);
}

bool RoundManager::ShiftAttacker(std::shared_ptr<Character> target) {
    // 检查当前攻击者是否有效
    if (!current_actor_ || !current_actor_->IsAlive()) {
        last_error_ = "Current attacker is null or dead";
        return false;
    }
    
    // 【修正】大失败时转移攻击到敌对阵营
    // 获取敌对阵营（不是同阵营）
    int attacker_camp = current_actor_->camp;
    int enemy_camp = (attacker_camp == 1) ? 2 : 1;
    
    // 获取敌对阵营的所有活着的角色
    auto enemies = GetLiveCharactersByCamp(enemy_camp);
    
    if (enemies.empty()) {
        last_error_ = "No valid enemies to shift attacker to (enemy camp defeated)";
        return false;
    }
    
    // 对敌对阵营的成员进行仇恨值加权随机选择
    std::vector<std::shared_ptr<Character>> candidates;
    for (const auto& enemy : enemies) {
        if (enemy && enemy->IsAlive()) {
            candidates.push_back(enemy);
        }
    }
    
    if (candidates.empty()) {
        last_error_ = "No valid enemies to shift attacker to";
        return false;
    }
    
    // 计算总仇恨度权重
    int total_aggro = 0;
    for (const auto& candidate : candidates) {
        total_aggro += candidate->aggro;
    }
    
    // 如果总仇恨度为0，使用均等权重
    if (total_aggro <= 0) {
        total_aggro = static_cast<int>(candidates.size());
        for (auto& candidate : candidates) {
            candidate->aggro = 1;  // 临时设置为1以进行均等权重选择
        }
    }
    
    // 基于仇恨度权重进行加权随机选择
    int random_val = rand() % total_aggro;
    int accumulated = 0;
    std::shared_ptr<Character> new_attacker = nullptr;
    
    for (const auto& candidate : candidates) {
        accumulated += candidate->aggro;
        if (random_val < accumulated) {
            new_attacker = candidate;
            break;
        }
    }
    
    // 防卫万一没有选中（不应该发生）
    if (!new_attacker && !candidates.empty()) {
        new_attacker = candidates.back();
    }
    
    if (!new_attacker) {
        last_error_ = "Failed to select new attacker from enemy camp";
        return false;
    }
    
    // 记录原始攻击者（即新的目标）
    std::shared_ptr<Character> original_attacker = current_actor_;
    std::string old_attacker_name = original_attacker->name;
    
    // 更新当前攻击者为敌对阵营的新攻击者
    current_actor_ = new_attacker;
    
    // 触发 OnAttackerShifted 技能
    SkillTriggerMessage shift_msg;
    shift_msg.Source = old_attacker_name;
    shift_msg.Name = new_attacker->name;
    TriggerPassiveSkills("OnAttackerShifted", new_attacker, shift_msg);
    
    // 【关键】转移成功后，立即执行新攻击者对原攻击者的反击
    // 创建新的执行环境，让新攻击者反击原攻击者
    if (new_attacker && original_attacker && original_attacker->IsAlive()) {
        
        // 获取新攻击者的 NATK 预设
        PresetRegistry* registry = PresetRegistry::GetInstance();
        if (registry) {
            AnkePreset* natk_preset = registry->GetAnke("natk");
            if (natk_preset) {
                // 创建执行环境（新攻击者对原攻击者）
                ExecutionEnvironment counterattack_env(new_attacker.get(), original_attacker.get(), battle_.get());
                
                // �【修复】使用新 API 分离注册 self 和 target
                if (counterattack_env.GetActor()) {
                    counterattack_env.RegisterSelf(counterattack_env.GetActor());     // 反击者 -> self
                }
                if (counterattack_env.GetTarget()) {
                    counterattack_env.RegisterTarget(counterattack_env.GetTarget());  // 原攻击者 -> target
                }
                
                // 执行反击
                int counterattack_result = natk_preset->Execute(&counterattack_env);
                
                // 记录反击的掷骰信息
                int counterattack_random = natk_preset->GetLastRandomValue();
                int counterattack_total_weight = natk_preset->GetTotalWeight();
                const AnkeOption* counterattack_option = natk_preset->GetSelectedOption();
                std::string counterattack_option_name = (counterattack_option) ? counterattack_option->name : "unknown";
                std::string counterattack_option_cn = TranslateAnkeOptionName(counterattack_option_name);
                
                if (g_current_round_manager) {
                    std::string detail_desc = "  ⚔️ 【" + new_attacker->name + "】反击 > 【" + original_attacker->name + "】\n";
                    detail_desc += "    投掷D" + std::to_string(counterattack_total_weight) + "=" + 
                                  std::to_string(counterattack_random) + " > 选中【" + counterattack_option_cn + "】\n";
                    g_current_round_manager->AppendSkillTriggerLog(detail_desc);
                }
            }
        }
    }
    
    return true;
}

}  // namespace abot

// 全局RoundManager指针定义 - 在命名空间外部
// 注意：移除 thread_local，以确保跨线程可访问（ExecutionEnvironment 的伤害回调可能在不同线程）
abot::RoundManager* g_current_round_manager = nullptr;
