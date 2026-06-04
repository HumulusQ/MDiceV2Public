/**
 * @file SkillTriggerSystem.cpp
 * @brief ABOT 被动技能事件触发系统实现
 */

#pragma execution_character_set("utf-8")

#include "SkillTriggerSystem.h"
#include "Battle.h"
#include "ExecutionEnvironment.h"
#include "PresetSystem.h"
#include "Value.h"
#include "RoundManager.h"
#include <random>
#include <algorithm>
#include <sstream>
#include <iostream>
#include <ctime>
#include <cstdio>

// 前向声明 - 全局指针（在RoundManager.cpp中定义）
extern abot::RoundManager* g_current_round_manager;

namespace abot {

// 日志帮助函数 - 将日志写入 RoundManager 的缓冲区
static void LogSkillTrigger(const std::string& message) {
    if (g_current_round_manager != nullptr) {
        g_current_round_manager->AppendSkillTriggerLog(message);
    } else {
        // 如果 RoundManager 不可用，至少输出到stderr用于调试
        std::cerr << "[SKILL_LOG_UNAVAILABLE] " << message << std::endl;
    }
}

// ============ 静态辅助函数 ============

/**
 * @brief 检查技能是否由于大失败状态被禁用
 */
static bool IsSkillDisabledByStun(std::shared_ptr<Character> character) {
    if (!character) return false;
    
    // 检查是否有Stun tag
    for (const auto& tag : character->tags) {
        if (tag == "Stun") {
            return true;
        }
    }
    return false;
}

/**
 * @brief 从character->skills中查找指定type的技能（优化版 - O(1)索引查询）
 * 
 * 优化说明：
 * - Phase 3优化：使用skill_index中的by_trigger_type Map
 * - 性能：从O(N)改进到O(1)平均查询时间
 * - 备注：GetSkillsByType()返回指针；这里转换为值保持兼容性
 */
static std::vector<SkillParam> FindSkillsByType(
    std::shared_ptr<Character> character,
    const std::string& trigger_type) {
    
    std::vector<SkillParam> result;
    if (!character) return result;
    
    std::ostringstream debug_log;
    
    // Phase 3优化：使用skill_index的O(1)查询替代线性搜索
    auto skill_ptrs = character->GetSkillsByType(trigger_type);
    
    /*debug_log << "      [FIND SKILLS] Using optimized index lookup for type: " << trigger_type;
    LogSkillTrigger(debug_log.str());
    
    debug_log.str("");
    debug_log << "      [FIND SKILLS] Index search found " << skill_ptrs.size() << " skill(s)";
    LogSkillTrigger(debug_log.str());*/
    
    // 转换指针为值，并复制到结果
    for (const auto* skill_ptr : skill_ptrs) {
        if (!skill_ptr) continue;
        
        debug_log.str("");
        debug_log << "[SKILL]- Skill: " << skill_ptr->id << " type=" << skill_ptr->type 
                  << " (cd=" << skill_ptr->cd << ", rate=" << skill_ptr->rate << ")";
        LogSkillTrigger(debug_log.str());
        
        if (skill_ptr->disabled) {
            LogSkillTrigger("          ✗ Skill disabled");
            continue;
        }
        
        debug_log.str("");
        debug_log << "[SKILL]✓ Match! Adding to result";
        LogSkillTrigger(debug_log.str());
        result.push_back(*skill_ptr);  // Dereference pointer to value
    }
    
    return result;
}

/**
 * @brief 检查CD条件
 */
static bool SkillCooldownReady(
    std::shared_ptr<Character> character,
    const std::string& skill_id) {
    
    if (!character) return false;
    
    auto& cooldowns = character->skill_cooldowns;
    if (cooldowns.find(skill_id) != cooldowns.end()) {
        return cooldowns[skill_id] == 0;
    }
    
    // 第一次触发，CD为0
    return true;
}

/**
 * @brief 随机触发概率检查
 */
static bool RollTriggerRate(int rate) {
    if (rate <= 0) return false;
    if (rate >= 100) return true;
    
    static std::mt19937 gen(std::random_device{}());
    std::uniform_int_distribution<> dis(0, 99);
    return dis(gen) < rate;
}

// ============ 公共接口实现 ============

int SkillTriggerSystem::TriggerSkillsByType(
    const std::string& trigger_type,
    const std::vector<std::shared_ptr<Character>>& characters,
    std::shared_ptr<Character> target_character,
    const SkillTriggerMessage& message,
    Battle* battle,
    ExecutionEnvironment* environment) {
    
    if (!battle) return 0;  // 只检查 battle，environment 当前未使用
    
    int triggered_count = 0;
    std::string normalized_type = trigger_type;
    std::transform(normalized_type.begin(), normalized_type.end(), 
                   normalized_type.begin(), ::tolower);
    
    // 添加调试日志
    std::ostringstream debug_log;
    debug_log << "[SKILL TRIGGER START] type=" << trigger_type 
              << " target=" << (target_character ? target_character->name : "null");
    LogSkillTrigger(debug_log.str());
    
    // 根据触发类型决定检查的角色范围
    std::vector<std::shared_ptr<Character>> targets_to_check;
    
    if (normalized_type == "onunitattack" || 
        normalized_type == "onunitattacked" ||
        normalized_type == "ondead" ||
        normalized_type == "onskilltriggler") {
        // 这些类型是场上事件，需要检查所有活着的角色（除了触发源）
        for (const auto& ch : characters) {
            if (ch && ch->is_alive && ch != target_character) {
                targets_to_check.push_back(ch);
            }
        }
    } else if (target_character) {
        // 其他类型是个体事件，只检查目标角色
        if (target_character->is_alive) {
            targets_to_check.push_back(target_character);
        }
    }
    
    debug_log.str("");
    debug_log << "[SKILL]→ Checking " << targets_to_check.size() << " character(s)";
    LogSkillTrigger(debug_log.str());
    
    // 对每个需要检查的角色，查找匹配type的技能
    for (auto& character : targets_to_check) {
        auto matching_skills = FindSkillsByType(character, trigger_type);
        
        debug_log.str("");
        debug_log << "[SKILL]→ Character: " << character->name << " found " << matching_skills.size() << " matching skill(s)";
        LogSkillTrigger(debug_log.str());
        
        for (const auto& skill : matching_skills) {
            debug_log.str("[SKILL]");
            debug_log << "[SKILL]→ Skill: " << skill.id << " (type=" << skill.type << ", cd=" << skill.cd << ", rate=" << skill.rate << ")";
            LogSkillTrigger(debug_log.str());
            
            // 检查ActSkill是否被眩晕禁用
            if ((normalized_type == "actskill" || normalized_type == "actskilll") && 
                IsSkillDisabledByStun(character)) {
                LogSkillTrigger("[SKILL]✗ BLOCKED: ActSkill disabled by Stun");
                continue;
            }
            
            // 检查CD条件
            if (!SkillCooldownReady(character, skill.id)) {
                debug_log.str("");
                debug_log << "[SKILL]      ✗ BLOCKED: CD not ready (cd=" << skill.cd << ")";
                LogSkillTrigger(debug_log.str());
                continue;
            }
            LogSkillTrigger("[SKILL]      ✓ CD ready");
            
            // 检查触发概率
            if (!RollTriggerRate(skill.rate)) {
                debug_log.str("");
                debug_log << "[SKILL]      ✗ BLOCKED: Rate check failed (rate=" << skill.rate << "%)";
                LogSkillTrigger(debug_log.str());
                continue;
            }
            debug_log.str("");
            debug_log << "[SKILL]      ✓ Rate check passed (rate=" << skill.rate << "%)";
            LogSkillTrigger(debug_log.str());
            
            // 执行技能
            if (TriggerSingleSkill(character, skill.id, message, battle, environment)) {
                triggered_count++;
                LogSkillTrigger("[SKILL]    ✓ TRIGGERED: Skill executed successfully");
                
                // OnSkillTriggler特殊处理：不能互相触发
                if (normalized_type == "onskillTriggler") {
                    // 标记已经触发过，避免递归
                    // (在实现中通过environment状态标记)
                }
            } else {
                LogSkillTrigger("[SKILL]    ✗ FAILED: Skill execution returned false");
            }
        }
    }
    
    debug_log.str("");
    debug_log << "[SKILL TRIGGER END] Total triggered: " << triggered_count;
    LogSkillTrigger(debug_log.str());
    
    return triggered_count;
}

bool SkillTriggerSystem::TriggerSingleSkill(
    std::shared_ptr<Character> character,
    const std::string& skill_id_raw,
    const SkillTriggerMessage& message,
    Battle* battle,
    ExecutionEnvironment* environment) {
    
    // 清理 skill_id 中的前后空白和引号
    std::string skill_id = skill_id_raw;
    size_t start = skill_id.find_first_not_of(" \t\r\n\"'");
    size_t end = skill_id.find_last_not_of(" \t\r\n\"'");
    if (start != std::string::npos) {
        skill_id = skill_id.substr(start, end - start + 1);
    }
    
    if (!character) {
        LogSkillTrigger("      ✗ TriggerSingleSkill: character is nullptr");
        return false;
    }
    if (!battle) {
        LogSkillTrigger("      ✗ TriggerSingleSkill: battle is nullptr");
        return false;
    }
    if (!environment) {
        LogSkillTrigger("      ✗ TriggerSingleSkill: environment is nullptr");
        return false;
    }
    
    // 从技能注册表获取技能模板
    auto registry = PresetRegistry::GetInstance();
    if (!registry) {
        LogSkillTrigger("      ✗ TriggerSingleSkill: PresetRegistry not available");
        return false;
    }
    

    
    SkillPreset* skill_preset = registry->GetSkill(skill_id);
    if (!skill_preset) {
        std::ostringstream log;
        log << "      ✗ TriggerSingleSkill: Skill '" << skill_id << "' not found in PresetRegistry";
        LogSkillTrigger(log.str());
        
        // 提供每个细节的诊断信息
        std::ostringstream diag;
        diag << "[SKILL] ]\n";
        diag << "        Query skill_id (cleaned): '" << skill_id << "' (length=" << skill_id.length() << ")\n";
        diag << "        Original skill_id: '" << skill_id_raw << "' (length=" << skill_id_raw.length() << ")";
        LogSkillTrigger(diag.str());
        
        // 调试：列出所有注册的技能
        auto all_skills = registry->ListPresets(PresetType::SKILL);
        
        std::ostringstream registry_info;
        registry_info << "[SKILL] Total skills registered: " << all_skills.size();
        LogSkillTrigger(registry_info.str());
        
        if (!all_skills.empty()) {
            std::ostringstream debug_log;
            debug_log << "[SKILL] Available skills:";
            LogSkillTrigger(debug_log.str());
            
            for (size_t i = 0; i < all_skills.size(); i++) {
                std::ostringstream skill_info;
                skill_info << "        [" << i << "] '" << all_skills[i] << "' (length=" << all_skills[i].length() << ")";
                
                // 检查是否匹配but差异
                if (all_skills[i] == skill_id) {
                    skill_info << " [EXACT MATCH?? - should have been found above!]";
                } else if (all_skills[i].find(skill_id) != std::string::npos || skill_id.find(all_skills[i]) != std::string::npos) {
                    skill_info << " [SUBSTRING MATCH]";
                }
                
                LogSkillTrigger(skill_info.str());
            }
        } else {
            LogSkillTrigger("      [REGISTRY EMPTY] No skills registered at all!!");
            LogSkillTrigger("      → This means RegisterSkillset was never called or failed silently");
        }
        
        return false;
    }
    
    std::ostringstream log;
    log << "[DEBUG]→ Executing skill '" << skill_id << "' via PresetRegistry";
    LogSkillTrigger(log.str());
    
    // 【关键】设置 self 为目标角色的 ObjectHandle
    {
        LogSkillTrigger("[DEBUG] Initializing ExecutionEnvironment.self");
        
        // 调用 RegisterSelf 将 character 转换为 ObjectHandle 并注入环境
        environment->RegisterSelf(character.get());
        
        LogSkillTrigger("[DEBUG] self registered in environment");
        
        // 验证 self 是否正确设置
        Value self_value = environment->GetValueProperty("self");
        if (self_value.IsHandle()) {
            char diag_buf[256];
            snprintf(diag_buf, sizeof(diag_buf),
                    "[DEBUG] ✓ self is Handle (IsHandle=1, IsSchema=%d, type=%d)",
                    self_value.IsSchema() ? 1 : 0,
                    (int)self_value.GetType());
            LogSkillTrigger(diag_buf);
        } else {
            LogSkillTrigger("[DEBUG] ✗ WARNING: self is not Handle after RegisterSelf");
        }
    }
    
    // Phase 4: 消息验证 - 检查触发消息是否与技能类型匹配
    {
        const SkillDefinition& skill_def = skill_preset->GetDefinition();
        if (!SkillMessageRegistry::ValidateMessage(skill_def.type, message)) {
            std::ostringstream warn_log;
            warn_log << "      ⚠️  [MESSAGE VALIDATION FAILED] "
                    << "Skill '" << skill_id << "' type='" << skill_def.type << "' "
                    << "received invalid message parameters - continuing anyway";
            LogSkillTrigger(warn_log.str());
            
            // 注：当前继续执行（不返回false），日志供调试之用
            // 如需更严格的策略，可改为：return false;
        }
    }
    
    // 🔍 显示执行前的属性值
    {
        std::ostringstream before_log;
        before_log << "      [DEBUG] [BEFORE SKILL] " << character->name 
                   << " - dmg [d1=" << character->dmg[0] 
                   << ", d2=" << character->dmg[1] 
                   << ", d3=" << character->dmg[2] 
                   << ", d4=" << character->dmg[3] 
                   << "] atk=" << character->atk 
                   << " hp=" << character->hp;
        LogSkillTrigger(before_log.str());
    }
    
    // 🔍 检查 environment 中的 self 对象结构
    {
        Value env_self = environment->GetValueProperty("self");

        if (env_self.IsSchema()) {

            // 检查 atk 字段
            if (env_self.HasField("atk")) {
                Value atk_field = env_self.GetField("atk");

                // 如果 atk 是 Schema，检查 value 字段
                if (atk_field.IsSchema() && atk_field.HasField("value")) {
                    Value atk_value = atk_field.GetField("value");
                    (void)atk_value; // 避免未使用变量警告
                }
            }

            // 检查 dmg 字段
            if (env_self.HasField("dmg")) {
                Value dmg_field = env_self.GetField("dmg");

                if (dmg_field.IsSchema()) {
                    if (dmg_field.HasField("d1")) {
                        Value d1 = dmg_field.GetField("d1");
                        (void)d1;
                    }
                }
            }

        } else if (env_self.IsHandle()) {
            // Handle 类型不能直接检查字段，需要通过 ObjectTable
            // （原逻辑无实际操作）
        }
    }

    
    // 向环境注入message参数
    InjectMessageToEnvironment(environment, message, skill_id);
    
    // 执行技能脚本
    try {
        int result = skill_preset->Execute(environment);
        std::ostringstream exec_log;
        exec_log << "[DEBUG]→ Skill execution result: " << result;
        LogSkillTrigger(exec_log.str());
        
        if (result == 0) {
            // 🔍 显示执行后的属性值
            {
                std::ostringstream after_log;
                after_log << "      [DEBUG] [AFTER SKILL] " << character->name 
                          << " - dmg [d1=" << character->dmg[0] 
                          << ", d2=" << character->dmg[1] 
                          << ", d3=" << character->dmg[2] 
                          << ", d4=" << character->dmg[3] 
                          << "] atk=" << character->atk 
                          << " hp=" << character->hp;
                LogSkillTrigger(after_log.str());
            }
            
            // 🔍 检查执行后的 environment 中的 self
            {
                Value env_self_after = environment->GetValueProperty("self");
                //LogSkillTrigger("      [ENV_SELF_DIAGNOSTIC_POST]");
                
                char diag_buf[512];
                snprintf(diag_buf, sizeof(diag_buf),
                        "[DEBUG] self: IsHandle=%d, IsSchema=%d, type=%d",
                        env_self_after.IsHandle() ? 1 : 0,
                        env_self_after.IsSchema() ? 1 : 0,
                        (int)env_self_after.GetType());
                LogSkillTrigger(diag_buf);
            }
            
            LogSkillTrigger("[DEBUG]✓ Skill executed successfully");
            return true;
        } else {
            exec_log.str("");
            exec_log << "      ✗ Skill execution failed (result=" << result << ")";
            LogSkillTrigger(exec_log.str());
            
            // 🔍 执行失败时，检查 self 结构
            {
                Value env_self_error = environment->GetValueProperty("self");
                LogSkillTrigger("      [ENV_SELF_DIAGNOSTIC_ERROR]");
                
                char diag_buf[512];
                snprintf(diag_buf, sizeof(diag_buf),
                        "[DEBUG]self: IsHandle=%d, IsSchema=%d, type=%d",
                        env_self_error.IsHandle() ? 1 : 0,
                        env_self_error.IsSchema() ? 1 : 0,
                        (int)env_self_error.GetType());
                LogSkillTrigger(diag_buf);
            }
            
            return false;
        }
    } catch (const std::exception& e) {
        std::ostringstream exc_log;
        exc_log << "      ✗ Skill execution exception: " << e.what();
        LogSkillTrigger(exc_log.str());
        return false;
    } catch (...) {
        LogSkillTrigger("      ✗ Skill execution: Unknown exception");
        return false;
    }
}

bool SkillTriggerSystem::CanSkillTrigger(
    const SkillParam& skill,
    const std::string& trigger_type) {
    
    if (skill.disabled) return false;
    
    // type匹配
    std::string skill_type = skill.type;
    std::string check_type = trigger_type;
    std::transform(skill_type.begin(), skill_type.end(), skill_type.begin(), ::tolower);
    std::transform(check_type.begin(), check_type.end(), check_type.begin(), ::tolower);
    
    if (skill_type != check_type) return false;
    
    // 概率检查
    if (!RollTriggerRate(skill.rate)) return false;
    
    return true;
}

SkillTriggerMessage SkillTriggerSystem::BuildMessage(const std::string& trigger_type) {
    // 基础实现：空消息
    // 具体消息构建由调用方根据触发类型提供参数
    return SkillTriggerMessage();
}

void SkillTriggerSystem::InjectMessageToEnvironment(
    ExecutionEnvironment* environment,
    const SkillTriggerMessage& message,
    const std::string& trigger_type) {
    
    if (!environment) return;
    
    // 注入message参数到环境中
    // 注意：当前实现暂时为空，待ExecutionEnvironment API完善后补充
    // TODO: 实现消息参数注入到脚本执行环境
}

} // namespace abot
