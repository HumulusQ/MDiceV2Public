/**
 * @file Scope.cpp
 * @brief 作用域系统实现
 */

#include "Scope.h"
#include "Value.h"
#include "ExecutionEnvironment.h"
#include "RoundManager.h"
#include <cstdio>
#include <iostream>
#include <functional>

extern abot::RoundManager* g_current_round_manager;

static void AppendBattleInfoLog(const std::string& message) {
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog(message + "\n");
    }
}

static void LogScopeSelfState(const std::string& action, const std::string& source, const abot::Value& value) {
    try {
        if (g_current_round_manager) {
            std::string msg = std::string("[DIAG][LOGSELF] ===== ENTER LogScopeSelfState(") + action + "," + source + ") =====\n";
            g_current_round_manager->AppendSkillTriggerLog(msg);
        }
        
        if (!value.IsSchema() && !value.IsHandle()) {
            char buf[256];
            snprintf(buf, sizeof(buf), "[SCOPE_%s][%s] self value is not schema or handle, type=%d\n", action.c_str(), source.c_str(), (int)value.GetType());
            AppendBattleInfoLog(buf);
            return;
        }
        
        bool is_handle = value.IsHandle();
        bool is_schema = value.IsSchema();
        uint64_t handle_id = is_handle ? value.GetHandle().GetID() : 0;
        double multiplier = -999.0;
        std::string name = "<missing>";
        int64_t camp = INT64_MIN;
        int64_t hp = INT64_MIN;
        int64_t atk = INT64_MIN;
        std::string def_summary = "<missing>";

        if (value.IsSchema()) {
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Before GetField(name) - checking HasField\n");
            }
            
            if (value.HasField("name")) {
                abot::Value name_field = value.GetField("name");
                if (name_field.IsString()) {
                    name = name_field.GetString();
                }
            }
            
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Before GetField(camp) - checking HasField\n");
            }
            
            if (value.HasField("camp")) {
                abot::Value camp_field = value.GetField("camp");
                if (camp_field.IsInt()) {
                    camp = camp_field.GetInt();
                }
            }
            
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Before GetField(hp) - checking HasField\n");
            }
            
            if (value.HasField("hp")) {
                abot::Value hp_field = value.GetField("hp");
                if (hp_field.IsInt()) {
                    hp = hp_field.GetInt();
                }
            }
            
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Before GetField(atk) - checking HasField\n");
            }
            
            if (value.HasField("atk")) {
                abot::Value atk_field = value.GetField("atk");
                if (atk_field.IsSchema() && atk_field.HasField("value")) {
                    abot::Value atk_value = atk_field.GetField("value");
                    if (atk_value.IsInt()) {
                        atk = atk_value.GetInt();
                    }
                } else if (atk_field.IsInt()) {
                    atk = atk_field.GetInt();
                }
            }
            
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Before GetField(def) - checking HasField\n");
            }
            
            if (value.HasField("def")) {
                abot::Value def_field = value.GetField("def");
                if (!def_field.IsNull()) {
                    def_summary = def_field.ToString();
                }
            } else if (value.HasField("defenses")) {
                abot::Value defenses_field = value.GetField("defenses");
                if (!defenses_field.IsNull()) {
                    def_summary = defenses_field.ToString();
                }
            } else {
                def_summary = "<not_present>";
            }
            
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] After GetField(def)\n");
            }
            
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Before GetField(turn) - checking HasField\n");
            }
            
            if (value.HasField("turn")) {
                abot::Value turn_field = value.GetField("turn");
                
                if (g_current_round_manager) {
                    g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] After GetField(turn)\n");
                }
                
                if (turn_field.IsSchema() && turn_field.HasField("multiplier")) {
                    abot::Value mult_field = turn_field.GetField("multiplier");
                    
                    if (mult_field.IsDouble()) multiplier = mult_field.GetDouble();
                    else if (mult_field.IsInt()) multiplier = (double)mult_field.GetInt();
                }
            } else {
                if (g_current_round_manager) {
                    g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Field 'turn' not present\n");
                }
            }
        }
        
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Before snprintf\n");
        }
        
        char buf[512];
        snprintf(buf, sizeof(buf), "[SCOPE_%s][%s] name=%s camp=%lld hp=%lld atk=%lld def=%s IsHandle=%d handle_id=%llu IsSchema=%d turn.multiplier=%.6f\n",
                action.c_str(),
                source.c_str(),
                name.c_str(),
                (long long)camp,
                (long long)hp,
                (long long)atk,
                def_summary.c_str(),
                is_handle ? 1 : 0,
                (unsigned long long)handle_id,
                is_schema ? 1 : 0,
                multiplier);
        
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] Before AppendBattleInfoLog\n");
        }
        
        AppendBattleInfoLog(buf);
        
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] ===== EXIT LogScopeSelfState SUCCESS =====\n");
        }
    } catch (const std::exception& ex) {
        if (g_current_round_manager) {
            std::string msg = std::string("[DIAG][LOGSELF] EXCEPTION CAUGHT: ") + ex.what() + "\n";
            g_current_round_manager->AppendSkillTriggerLog(msg);
        }
        throw;
    } catch (...) {
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("[DIAG][LOGSELF] UNKNOWN EXCEPTION CAUGHT\n");
        }
        throw;
    }
}

namespace abot {

// ============ Scope 实现 ============

Scope::Scope(ScopeType type, Scope* parent)
    : type_(type), parent_(parent) {
}

Scope::~Scope() {
}

void Scope::SetVariable(const std::string& name, const Value& value) {
    if (name == "self") {
        // 🟥【任务2】self 必须是纯 handle - **拒绝任何 schema**
        if (value.IsSchema()) {
            // ❌ FATAL: self 不得是 schema（无论是否同时是 handle）
            if (g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[DEBUG] Scope::SetVariable(\"self\") received schema — this is forbidden. IsHandle=%d IsSchema=%d",
                    value.IsHandle() ? 1 : 0,
                    value.IsSchema() ? 1 : 0);
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
            return;  // ❌ 直接拒绝，不存储
        }
        
        // ✔ 只允许纯 handle（IsHandle=1 && IsSchema=0）
        if (value.IsHandle() && !value.IsSchema()) {
            variables_[name] = value;
            
            // 🟥【任务1.3】硬日志验证 - self 成功接受纯 handle
            if (g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[DIAG][SCOPE_ACCEPT] Scope::SetVariable('self') accepted pure handle: IsHandle=%d IsSchema=%d type=%d",
                    value.IsHandle() ? 1 : 0,
                    value.IsSchema() ? 1 : 0,
                    (int)value.GetType());
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
            return;
        }
        
        // 其他情况（null、int、string 等）也拒绝
        if (g_current_round_manager) {
            char buf[256];
            snprintf(buf, sizeof(buf),
                "[DEBUG] Scope::SetVariable(\"self\") received non-handle value, type=%d IsHandle=%d IsSchema=%d",
                (int)value.GetType(),
                value.IsHandle() ? 1 : 0,
                value.IsSchema() ? 1 : 0);
            g_current_round_manager->AppendSkillTriggerLog(buf);
        }
        return;  // ❌ 拒绝非 handle 的 self
    }
    
    // 🟥【新增】target 变量处理 - 从 env 派生
    if (name == "target") {
        if (ExecutionEnvironment* current_env = ExecutionEnvironment::Current()) {
            Value env_target = current_env->GetValueProperty("target");
            if (env_target.IsHandle()) {
                variables_[name] = env_target;
                return;
            }
        }
        // 如果 env 中没有 target，则使用传入的值
        variables_[name] = value;
        return;
    }
    
    variables_[name] = value;
}

Value Scope::GetVariable(const std::string& name) const {
    auto it = variables_.find(name);
    if (it != variables_.end()) {
        if (name == "self") {
            LogScopeSelfState("GET", "SCOPE", it->second);
        }
        return it->second;
    }
    // 如果当前作用域没有找到，继续向上查找
    if (parent_) {
        return parent_->GetVariable(name);
    }
    return Value();
}

bool Scope::HasVariable(const std::string& name) const {
    return variables_.find(name) != variables_.end();
}

void Scope::DeleteVariable(const std::string& name) {
    variables_.erase(name);
}

Scope* Scope::FindScopeOfType(ScopeType type) {
    if (type_ == type) {
        return this;
    }
    if (parent_) {
        return parent_->FindScopeOfType(type);
    }
    return nullptr;
}

void Scope::PrintVariables() const {
    std::cout << "Scope [" << static_cast<int>(type_) << "]:" << std::endl;
    for (const auto& pair : variables_) {
        std::cout << "  " << pair.first << " = ";
        // 简化输出
        std::cout << pair.second.ToString() << std::endl;
    }
}

// ============ ScopeStack 实现 ============

ScopeStack::ScopeStack() {
    // 创建根作用域（Field级别）
    root_ = new Scope(ScopeType::Field, nullptr);
    current_ = root_;
}

ScopeStack::~ScopeStack() {
    // 从当前作用域回溯到根，然后删除
    while (current_ && current_->GetParent()) {
        Scope* parent = current_->GetParent();
        delete current_;
        current_ = parent;
    }
    // 删除根作用域
    if (root_) {
        delete root_;
    }
}

void ScopeStack::EnterScope(ScopeType type) {
    Scope* newScope = new Scope(type, current_);
    current_ = newScope;
}

void ScopeStack::ExitScope() {
    if (current_ && current_->GetParent()) {
        Scope* parent = current_->GetParent();
        delete current_;
        current_ = parent;
    }
}

Scope* ScopeStack::GetScopeOfType(ScopeType type) {
    if (current_) {
        return current_->FindScopeOfType(type);
    }
    return nullptr;
}

void ScopeStack::SetSelfReference(const Value& self) {
    self_reference_ = self;
}

void ScopeStack::SetEnemyList(const Value& enemies) {
    enemy_list_ = enemies;
}

void ScopeStack::SetAlliesList(const Value& allies) {
    allies_list_ = allies;
}

}  // namespace abot
