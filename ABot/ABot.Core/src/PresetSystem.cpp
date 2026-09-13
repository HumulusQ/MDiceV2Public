/**
 * @file PresetSystem.cpp
 * @brief 预设系统核心实现
 */

#include "PresetSystem.h"
#include "BuiltinPresets.h"
#include "VM.h"
#include "Scope.h"
#include "Character.h"
#include "RoundManager.h"
#include "ScriptExecutionContainer.h"
#include "SchemaValue.h"
#include "ObjectTable.h"
#include <stdexcept>
#include <algorithm>
#include <cstdlib>
#include <ctime>
#include <cstdio>
#include <iostream>
#include <sstream>
#include <cmath>
#ifdef _WIN32
#include <windows.h>
#endif

// 前向声明 - 全局指针（在RoundManager.cpp中定义，在命名空间外部）
extern abot::RoundManager* g_current_round_manager;

namespace abot {

// 日志帮助函数 - 将日志写入 RoundManager 的缓冲区
static void LogSkillExecution(const std::string& message) {
    if (g_current_round_manager != nullptr) {
        g_current_round_manager->AppendSkillTriggerLog(message + "\n");
    } else {
        // 如果 RoundManager 不可用，至少输出到stderr用于调试
        std::cerr << "[SKILL_EXEC_LOG] " << message << std::endl;
    }
}

// ============ 全局预设注册表单例 ============
// 使用线程安全的静态本地变量模式（Magic Statics）
// 编译器自动保证在多线程环境下只初始化一次
PresetRegistry* PresetRegistry::GetInstance()
{
    static PresetRegistry instance;
    static bool initialized = false;
    
    if (!initialized) {
        initialized = true;
        // 初始化内置预设（NATK等）- 仅在第一次调用时执行一次
        InitializeBuiltinPresets();
    }
    
    return &instance;
}


// ============ PresetRegistry 实现 ============

void PresetRegistry::RegisterFunction(const std::string& name,
                                     FunctionPreset::FunctionPtr func,
                                     bool is_builtin)
{
    auto preset = std::make_unique<FunctionPreset>(name, func, is_builtin);
    functions_[name] = std::move(preset);
}

void PresetRegistry::RegisterAnke(const std::string& name,
                                 std::unique_ptr<AnkePreset> anke)
{
    ankes_[name] = std::move(anke);
}

void PresetRegistry::RegisterSkill(SkillDefinition&& def,
                                  bool is_builtin)
{
    auto preset = std::make_unique<SkillPreset>(std::move(def));
    preset->SetBuiltin(is_builtin);
    skills_[preset->GetName()] = std::move(preset);
}

void PresetRegistry::RegisterState(StateDefinition&& def,
                                  bool is_builtin)
{
    auto preset = std::make_unique<StatePreset>(std::move(def));
    preset->SetBuiltin(is_builtin);
    states_[preset->GetName()] = std::move(preset);
}

PresetBase* PresetRegistry::GetPreset(PresetType type, const std::string& name)
{
    switch (type) {
        case PresetType::FUNCTION:
            return GetFunction(name);
        case PresetType::ANKE:
            return GetAnke(name);
        case PresetType::SKILL:
            return GetSkill(name);
        case PresetType::STATE:
            return GetState(name);
        default:
            return nullptr;
    }
}

FunctionPreset* PresetRegistry::GetFunction(const std::string& name)
{
    auto it = functions_.find(name);
    return (it != functions_.end()) ? it->second.get() : nullptr;
}

AnkePreset* PresetRegistry::GetAnke(const std::string& name)
{
    auto it = ankes_.find(name);
    return (it != ankes_.end()) ? it->second.get() : nullptr;
}

SkillPreset* PresetRegistry::GetSkill(const std::string& name)
{
    auto it = skills_.find(name);
    return (it != skills_.end()) ? it->second.get() : nullptr;
}

StatePreset* PresetRegistry::GetState(const std::string& name)
{
    auto it = states_.find(name);
    return (it != states_.end()) ? it->second.get() : nullptr;
}

bool PresetRegistry::HasPreset(PresetType type, const std::string& name) const
{
    switch (type) {
        case PresetType::FUNCTION:
            return functions_.find(name) != functions_.end();
        case PresetType::ANKE:
            return ankes_.find(name) != ankes_.end();
        case PresetType::SKILL:
            return skills_.find(name) != skills_.end();
        case PresetType::STATE:
            return states_.find(name) != states_.end();
        default:
            return false;
    }
}

int PresetRegistry::ExecutePreset(PresetType type, const std::string& name,
                                 ExecutionEnvironment* env)
{
    PresetBase* preset = GetPreset(type, name);
    if (!preset) {
        return -1;  // Preset not found
    }
    
    return preset->Execute(env);
}

std::vector<std::string> PresetRegistry::ListPresets(PresetType type) const
{
    std::vector<std::string> result;
    
    switch (type) {
        case PresetType::FUNCTION:
            for (const auto& kv : functions_) {
                result.push_back(kv.first);
            }
            break;
        case PresetType::ANKE:
            for (const auto& kv : ankes_) {
                result.push_back(kv.first);
            }
            break;
        case PresetType::SKILL:
            for (const auto& kv : skills_) {
                result.push_back(kv.first);
            }
            break;
        case PresetType::STATE:
            for (const auto& kv : states_) {
                result.push_back(kv.first);
            }
            break;
    }
    
    return result;
}

std::string PresetRegistry::GetPresetInfo(PresetType type, const std::string& name) const
{
    PresetBase* preset = const_cast<PresetRegistry*>(this)->GetPreset(type, name);
    if (!preset) {
        return "Preset not found: " + name;
    }
    
    std::string info = "Preset: " + name + "\n";
    info += "  Type: ";
    
    switch (type) {
        case PresetType::FUNCTION:
            info += "Function";
            break;
        case PresetType::ANKE:
            info += "ANKE";
            break;
        case PresetType::SKILL:
            info += "Skill";
            break;
        case PresetType::STATE:
            info += "State";
            break;
    }
    
    info += "\n  Builtin: " + std::string(preset->IsBuiltin() ? "Yes" : "No") + "\n";
    
    return info;
}


// ============ FunctionPreset 实现 ============

FunctionPreset::FunctionPreset(const std::string& name, FunctionPtr func, bool builtin)
    : name_(name), func_(func), builtin_(builtin)
{
}

int FunctionPreset::Execute(ExecutionEnvironment* env)
{
    if (!func_ || !env) {
        return -1;
    }
    
    return func_(env);
}

std::string FunctionPreset::ToXml() const
{
    std::string xml = "<function name=\"" + name_ + "\"";
    xml += " builtin=\"" + std::string(builtin_ ? "true" : "false") + "\"";
    xml += " />";
    return xml;
}


// ============ AnkePreset 实现 ============

AnkePreset::AnkePreset(const std::string& name)
    : name_(name), selected_(nullptr), total_weight_(0), builtin_(false),
      last_random_val_(0), last_selected_index_(-1), last_execution_result_(0)
{
}

void AnkePreset::AddOption(AnkeOption option)
{
    printf("[ANKE DEBUG] Adding option with weight %d (total before=%d)\n",
            option.weight, total_weight_);
    total_weight_ += option.weight;
    options_.push_back(std::move(option));
    printf("[ANKE DEBUG] Option added. Total weight now: %d\n", total_weight_);
}

int AnkePreset::Execute(ExecutionEnvironment* env)
{
    // 【硬日志】入口
#ifdef _WIN32
    OutputDebugStringA("[HARDLOG] Enter AnkePreset::Execute\n");
#endif
    LogSkillExecution("[HARDLOG] Enter AnkePreset::Execute");
    
    if (options_.empty() || total_weight_ <= 0) {
        return -1;
    }
    
    if (!env) {
        return -1;
    }
    
    // 第1步：生成基础掷骰日志 - "正在投掷..."
    std::string anke_log = "投掷D" + std::to_string(total_weight_) + "...";
    
    // 第2步：加权随机选择
    // ✨ 修复：应该是1-n而不是0-(n-1)
    int random_val = 1 + (rand() % total_weight_);
    last_random_val_ = random_val;
    
    // 立即输出投掷日志
    // (已移除日志输出以简化反击信息)
    
    int accumulated = 0;
    int selected_index = -1;
    
    for (int i = 0; i < (int)options_.size(); i++) {
        accumulated += options_[i].weight;
        
        if (random_val <= accumulated) {
            selected_index = i;
            last_selected_index_ = i;
            selected_ = &options_[i];
            break;
        }
    }
    
    if (selected_index < 0) {
        return -1;
    }
    
    auto& option = options_[selected_index];
    
    // 立即输出选中选项
    // (已移除日志输出以简化反击信息)
    
    // 第3步：检查是否为临界选项配对 (es/ef)
    // 【重要】此时已经在第一步 D10 投掷中选中了 critical（即 D10=10）
    // 现在进行第二步：再投掷 D2 来判定大成功(es) 还是大失败(ef)
    if (option.is_critical_pair) {
        // 【第二步投掷】D2 重大判定：1 大成功，2 大失败
        int critical_roll = (rand() % 2) + 1;  // 1-2
        
        bool is_success = (critical_roll == 1);
        
        // 投掷结果：是大成功还是大失败
        LogSkillExecution("[重大判定] D2=" + std::to_string(critical_roll) + " → " + (is_success ? "大成功(es)" : "大失败(ef)"));
        
        // 【诊断】文件记录大成功判定
        // FILE* diag_critical = fopen("C:\\dodamage_diagnostic.log", "a");
        // if (diag_critical) {
        //     fprintf(diag_critical, "\n╔════════════════════════════════════════╗\n");
        //     fprintf(diag_critical, "║   [AnkePreset::Execute - CRITICAL]     ║\n");
        //     fprintf(diag_critical, "║   大成功/大失败判定                     ║\n");
        //     fprintf(diag_critical, "╚════════════════════════════════════════╝\n");
        //     fprintf(diag_critical, "D2 Roll: %d → %s\n", d2_roll, (is_success ? "大成功(es)" : "大失败(ef)"));
        //     fprintf(diag_critical, "Option name: %s\n", option.name.c_str());
        //     fprintf(diag_critical, "is_critical_pair: true\n");
        //     fprintf(diag_critical, "script_to_execute will be: %s\n\n", 
        //             is_success ? "option.script" : "option.critical_failure_script");
        //     fflush(diag_critical);
        // }
        
        // 标记critical事件类型
        env->SetIntProperty("is_critical_event", 1);
        env->SetIntProperty("is_critical_success", is_success ? 1 : 0);
        env->SetIntProperty("is_critical_failure", is_success ? 0 : 1);
        env->SetIntProperty("anke_last_random_value", random_val);
        env->SetIntProperty("anke_critical_roll_value", critical_roll);
        
        // 选择要执行的脚本
        BytecodeProgram* script_to_execute = is_success ? option.script.get() : option.critical_failure_script.get();
        
        if (script_to_execute) {
            // 【诊断】脚本执行前的状态检查
            // if (diag_critical) {
            //     fprintf(diag_critical, "✓ Script pointer is VALID (not null)\n");
            //     fprintf(diag_critical, "  Instructions count: %d\\n", (int)script_to_execute->instructions.size());
            //     fprintf(diag_critical, "  About to execute script with ScriptExecutionContainer::ExecuteWithSelf()\\n\\n");
            //     fflush(diag_critical);
            // }
            LogSkillExecution("\n[脚本执行] 【临界选项脚本】: " + option.name + " | 类型: " + (is_success ? "大成功(es)" : "大失败(ef)") + " | 总指令数: " + std::to_string(script_to_execute->instructions.size()));
            
            // 【字节码诊断】输出编译后的指令
            {
                // Helper to convert opcode to string
                auto opcode_to_string = [](Opcode op) -> const char* {
                    switch(op) {
                        case Opcode::LOAD_INT: return "LOAD_INT";
                        case Opcode::LOAD_DOUBLE: return "LOAD_DOUBLE";
                        case Opcode::LOAD_STRING: return "LOAD_STRING";
                        case Opcode::LOAD_VAR: return "LOAD_VAR";
                        case Opcode::STORE_VAR: return "STORE_VAR";
                        case Opcode::ADD: return "ADD";
                        case Opcode::MUL: return "MUL";
                        case Opcode::DIV: return "DIV";
                        case Opcode::TABLE_ACCESS: return "TABLE_ACCESS";
                        case Opcode::TABLE_SET: return "TABLE_SET";
                        case Opcode::TABLE_SET_SELF: return "TABLE_SET_SELF";
                        case Opcode::LOAD_SELF: return "LOAD_SELF";
                        case Opcode::CALL: return "CALL";
                        case Opcode::SELF_COMMIT: return "SELF_COMMIT";
                        case Opcode::HALT: return "HALT";
                        default: return "UNKNOWN";
                    }
                };
                
                // Bytecode diagnostics removed for compilation
            }
            
            // 【诊断】检查环境设置
            ExecutionEnvironment* current_env = ExecutionEnvironment::Current();
            /*LogSkillExecution("[DIAGNOSTIC] Critical script - env_param=" + std::string(env ? "valid" : "null") + 
                            " env_current=" + std::string(current_env ? "valid" : "null") + 
                            " stack_depth=" + std::to_string(ExecutionEnvironment::GetStackDepth()));*/
            // 【关键诊断】执行前记录turn.multiplier初始值
            if (env && env->GetActor()) {
                LogSkillExecution("[脚本诊断] 执行前: actor->turn.multiplier 初始化");
            }
            ScopeStack scope;
            VM vm;
            
            // 【关键修复】初始化 self 变量到 ScopeStack
            // 脚本访问 self.dmg.d1 等时，需要从 ScopeStack 获取变量
            if (env && current_env) {
                Value self_schema = Value::CreateSchema();
                
                if (env->GetActor()) {
                    // 基础属性
                    self_schema.SetField("name", Value(env->GetActor()->name));
                    self_schema.SetField("camp", Value((int64_t)env->GetActor()->camp));
                    
                    // ✅ ATK 作为 Schema{value: int} 创建，与脚本期望的 self.atk.value 访问一致
                    Value atk_schema = Value::CreateSchema();
                    atk_schema.SetField("value", Value((int64_t)env->GetActor()->atk));
                    self_schema.SetField("atk", atk_schema);
                    
                    self_schema.SetField("hp", Value((int64_t)env->GetActor()->hp));
                    
                    // 【关键】dmg 子对象
                    Value dmg_schema = Value::CreateSchema();
                    dmg_schema.SetField("d1", Value((int64_t)env->GetActor()->dmg[0]));
                    dmg_schema.SetField("d2", Value((int64_t)env->GetActor()->dmg[1]));
                    dmg_schema.SetField("d3", Value((int64_t)env->GetActor()->dmg[2]));
                    dmg_schema.SetField("d4", Value((int64_t)env->GetActor()->dmg[3]));
                    self_schema.SetField("dmg", dmg_schema);
                    
                    // ✨ 【修复】turn 对象 - 从 actor->turn.multiplier 读取初始值
                    // 使用 double 类型避免精度丧失（脚本需要进行浮点运算）
                    Value turn_schema = Value::CreateSchema();
                    double initial_multiplier = env->GetActor()->turn.multiplier;
                    turn_schema.SetField("multiplier", Value(initial_multiplier));
                    self_schema.SetField("turn", turn_schema);                    
                    // 【修复】补全默认字段，避免日志系统访问不存在字段导致异常
                    // 这些是默认字段，不是固定字段，用户脚本仍可自由扩展
                    self_schema.SetField("def", Value::CreateSchema());
                    self_schema.SetField("defenses", Value::CreateSchema());                    
                    // 【修复】补全默认字段，避免日志系统访问不存在字段导致异常
                    // 这些是默认字段，不是固定字段，用户脚本仍可自由扩展
                    self_schema.SetField("def", Value::CreateSchema());
                    self_schema.SetField("defenses", Value::CreateSchema());
                }
                
                scope.SetVariable("self", self_schema);
                // 【关键修复】也将 self 设置到 ExecutionEnvironment
                // 这样 LOAD_SELF 指令可以获得完整的 schema 对象
                env->SetValueProperty("self", self_schema);
                //LogSkillExecution("[INIT] Self variable initialized in both ScopeStack and ExecutionEnvironment");
            }
            
            // 【关键修复】使用统一的脚本执行容器管理 SchemaValue 同步
            // 容器负责：创建 Schema -> 注入 env/scope -> 执行 -> 同步修改 -> 最终写回
            LogSkillExecution("[HARDLOG] Before ScriptExecutionContainer::ExecuteWithSelf");
            
            bool exec_result = ScriptExecutionContainer::ExecuteWithSelf(script_to_execute, env, &scope);
            
            LogSkillExecution("[HARDLOG] After ScriptExecutionContainer::ExecuteWithSelf");
            
            last_execution_result_ = exec_result ? 0 : -1;
            
            // 【诊断】脚本执行完成后的状态检查
            if (is_success) {
                // 大成功脚本执行后，从 ObjectTable 读取 multiplier 的值
                Character* actor = env->GetActor();
                if (actor) {
                    LogSkillExecution("[脚本诊断] ES执行后: actor->turn.multiplier 执行完成");
                    
                    // 🟩【修复】从 ObjectTable 读取真实的 multiplier，而不是从 Character
                    Value self = env->GetValueProperty("self");
                    if (self.IsHandle()) {
                        try {
                            ObjectHandle h = self.GetHandle();
                            ObjectTable* table = env->GetObjectTable();
                            if (table) {
                                SchemaValue& root = table->Get(h);
                                
                                // 访问嵌套字段：turn.multiplier
                                Value turn_value = root.GetField("turn");
                                Value mult_value = turn_value.GetField("multiplier");
                                double multiplier_in_table = mult_value.GetDouble();
                                
                                // 检查是否成功翻倍（预期 ≈ 2.0）
                                if (fabs(multiplier_in_table - 2.0) < 1e-6) {
                                    LogSkillExecution("[脚本诊断] ✓ turn.multiplier 成功翻倍！");
                                } else {
                                    std::string msg = "[脚本诊断] ✗ turn.multiplier 未正确翻倍（ObjectTable中值=" 
                                        + std::to_string(multiplier_in_table) + ")";
                                    LogSkillExecution(msg);
                                }
                            }
                        } catch (const std::exception& ex) {
                            std::string msg = "[脚本诊断] ✗ 无法从ObjectTable读取multiplier: ";
                            msg += ex.what();
                            LogSkillExecution(msg);
                        }
                    } else {
                        LogSkillExecution("[脚本诊断] ✗ self 不是 Handle，无法读取 ObjectTable");
                    }
                }
            }
            
            std::string crit_result_msg = "[脚本执行] 【结果】: ";
            crit_result_msg += (exec_result ? "成功 ✓" : "失败 ✗");
            LogSkillExecution(crit_result_msg);
            
            return exec_result ? 0 : -1;
        } else {
            last_execution_result_ = -2;
            return -1;
        }
    } else {
        // 普通选项 - 直接执行脚本
        env->SetIntProperty("is_critical_event", 0);
        env->SetIntProperty("anke_last_random_value", random_val);
        
        if (option.script) {
            // (已移除普通选项脚本执行日志以简化反击信息)
            
            // 【诊断】检查环境设置
            ExecutionEnvironment* current_env = ExecutionEnvironment::Current();
            
            ScopeStack scope;
            
            VM vm;
            
            // 🟥【关键修复】删除旧的 self_schema 初始化代码
            // ExecuteWithSelf 会完全负责 self 的初始化为纯 handle
            // 在这里创建 schema 格式的 self 会导致递归调用时被拒绝
            LogSkillExecution("[HARDLOG] Skipping manual self initialization - ExecuteWithSelf will handle it");
            
            bool exec_result = ScriptExecutionContainer::ExecuteWithSelf(option.script.get(), env, &scope);
            
            LogSkillExecution("[HARDLOG] After ScriptExecutionContainer::ExecuteWithSelf");
            last_execution_result_ = exec_result ? 0 : -1;
            
            /*std::string result_msg = "[脚本执行] 【结果】: ";
            result_msg += (exec_result ? "成功 ✓" : "失败 ✗");
            LogSkillExecution(result_msg);*/
            
            // 【诊断】VM 执行后的详细日志
            /*std::string vm_diagnostics = env->GetDiagnosticLog();
            if (!vm_diagnostics.empty()) {
                LogSkillExecution("[VM诊断日志开始]");
                LogSkillExecution(vm_diagnostics);
                LogSkillExecution("[VM诊断日志结束]");
            } else {
                LogSkillExecution("[VM诊断] 无诊断日志收集");
            }*/
            /*
            // 【诊断】脚本执行后检查 self 变量是否被修改
            Value self_after = scope.GetVariable("self");
            if (self_after.IsSchema()) {
                Value dmg_after = self_after.GetField("dmg");
                if (dmg_after.IsSchema()) {
                    LogSkillExecution("[DEBUG] After exec: dmg=[" + 
                        std::to_string(dmg_after.GetField("d1").GetInt()) + "," +
                        std::to_string(dmg_after.GetField("d2").GetInt()) + "," +
                        std::to_string(dmg_after.GetField("d3").GetInt()) + "," +
                        std::to_string(dmg_after.GetField("d4").GetInt()) + "]");
                }
            }
            */
            if (exec_result) {
                LogSkillExecution("[HARDLOG] Exit AnkePreset::Execute success");
                return 0;
            } else {
                LogSkillExecution("[HARDLOG] Exit AnkePreset::Execute failure");
                return -1;
            }
        } else {
            last_execution_result_ = -2;
        }
    }
    return 0;
}

std::string AnkePreset::ToXml() const
{
    std::string xml = "<anke name=\"" + name_ + "\"";
    xml += " builtin=\"" + std::string(builtin_ ? "true" : "false") + "\">\n";
    
    for (const auto& opt : options_) {
        xml += "  <option name=\"" + opt.name + "\" weight=\"" + std::to_string(opt.weight) + "\" />\n";
    }
    
    xml += "</anke>";
    return xml;
}


// ============ SkillPreset 实现 ============

SkillPreset::SkillPreset(SkillDefinition&& def)
    : def_(std::move(def)), builtin_(false)
{
}

int SkillPreset::Execute(ExecutionEnvironment* env)
{
    if (!env) {
        LogSkillExecution("[SKILL EXECUTE] ERROR: ExecutionEnvironment is null");
        fprintf(stderr, "[SKILL EXECUTE] ERROR: ExecutionEnvironment is null\n");
        return -1;
    }
    
    // 如果没有定义脚本字节码，直接返回
    if (!def_.def) {
        LogSkillExecution("[SKILL EXECUTE] WARNING: Skill '" + def_.id + "' has no bytecode");
        fprintf(stderr, "[SKILL EXECUTE] WARNING: Skill '%s' has no bytecode\n", def_.id.c_str());
        return 0;
    }
    
    LogSkillExecution("[SKILL EXECUTE] Executing skill '" + def_.id + "' with bytecode");
    
    // 输出原始表达式（诊断用）
    /*if (!def_.original_expression.empty()) {
        LogSkillExecution("[ORIGINAL_EXPRESSION_BEGIN]");
        LogSkillExecution("Length: " + std::to_string(def_.original_expression.length()));
        LogSkillExecution("Content: " + def_.original_expression);
        LogSkillExecution("[ORIGINAL_EXPRESSION_END]");
    }*/
    
    // 输出编译诊断信息（如果存在）
    /*if (!def_.def->compilation_diagnostics.empty()) {
        LogSkillExecution("[COMPILATION_DIAGNOSTICS_BEGIN]");
        LogSkillExecution(def_.def->compilation_diagnostics);
        LogSkillExecution("[COMPILATION_DIAGNOSTICS_END]");
    }*/
    
    // 将技能参数转换为Schema对象
    auto para_schema = std::make_shared<Value>(Value::CreateSchema());
    for (const auto& param : def_.parameters) {
        // 将参数值存储为schema字段
        para_schema->SetField(param.first, Value(param.second));
    }
    
    // 在环境中设置para参数
    env->SetPara(para_schema);
    
    // 针对技能执行需要特殊处理：虚拟机脚本中的 `self` 引用需要指向正确的目标
    // 这里 env->GetActor() 就是技能作用的对象
    
    try {
        // 💡 关键问题：虚拟机需要能够访问 ExecutionEnvironment
        // 脚本中的 `self` 应该指向 env->GetActor()
        // 虚拟机执行时会通过 ExecutionEnvironment::Current() 获取当前环境
        
        // 💥 CRITICAL: 执行前必须将角色数据导入环境，这样虚拟机修改的是Environment配置而不是空气
        if (env && env->GetActor()) {
            env->RegisterCharacterData(env->GetActor());
        }
        
        // 诊断：检查环境栈状态
        /*ExecutionEnvironment* current_env = ExecutionEnvironment::Current();
        {
            std::ostringstream pre_diag;
            pre_diag << "[PRE_VM_DIAG] env_param=" << (env ? "valid" : "null")
                    << " env_current=" << (current_env ? "valid" : "null")
                    << " stack_depth=" << ExecutionEnvironment::GetStackDepth();
            LogSkillExecution(pre_diag.str());
        }*/
        
        // 创建虚拟机并执行字节码
        ScopeStack scope;
        VM vm;
        
        // 【关键修复】使用统一的脚本执行容器管理 SchemaValue 同步
        // 容器负责：创建 Schema -> 注入 env/scope -> 执行 -> 从 scope 取回修改 -> 同步到 env -> 最终写回
        // 注意：不需要手动初始化 self 变量，容器会自动处理
        bool exec_result = ScriptExecutionContainer::ExecuteWithSelf(def_.def.get(), env, &scope);
        
        // 💥 关键修复：从 ExecutionEnvironment 提取 VM 诊断日志
        /*std::string vm_diag_log = env->GetDiagnosticLog();
        if (!vm_diag_log.empty()) {
            LogSkillExecution("[VM_DIAGNOSTICS_BEGIN]");
            LogSkillExecution(vm_diag_log);
            LogSkillExecution("[VM_DIAGNOSTICS_END]");
        } else {
            LogSkillExecution("[VM_DIAGNOSTICS] No diagnostics collected (log empty)");
        }*/
        
        // � P1诊断：检查VM执行后对象的真实状态
        /*uintptr_t ptr_before_sync = 0;
        if (env && env->GetActor()) {
            Character* actor_before_sync = env->GetActor();
            ptr_before_sync = reinterpret_cast<uintptr_t>(actor_before_sync);
            
            char p1_diag_before[512];
            snprintf(p1_diag_before, sizeof(p1_diag_before),
                    "[P1_DIAGNOSTIC_BEFORE_SYNC] ptr=0x%llx atk=%d hp=%d dmg=[%d,%d,%d,%d]",
                    (unsigned long long)ptr_before_sync, 
                    actor_before_sync->atk, actor_before_sync->hp,
                    actor_before_sync->dmg[0], actor_before_sync->dmg[1],
                    actor_before_sync->dmg[2], actor_before_sync->dmg[3]);
            LogSkillExecution(p1_diag_before);
        }
        
        // � P1诊断：检查SyncCharacterData后的状态（已删除SyncCharacterData调用）
        if (env && env->GetActor()) {
            Character* actor_after_sync = env->GetActor();
            uintptr_t ptr_after_sync = reinterpret_cast<uintptr_t>(actor_after_sync);
            
            char p1_diag_after[512];
            snprintf(p1_diag_after, sizeof(p1_diag_after),
                    "[P1_DIAGNOSTIC_AFTER_SYNC] ptr=0x%llx atk=%d hp=%d dmg=[%d,%d,%d,%d]",
                    (unsigned long long)ptr_after_sync,
                    actor_after_sync->atk, actor_after_sync->hp,
                    actor_after_sync->dmg[0], actor_after_sync->dmg[1],
                    actor_after_sync->dmg[2], actor_after_sync->dmg[3]);
            LogSkillExecution(p1_diag_after);
            
            // 验证指针是否一致
            if (ptr_before_sync != ptr_after_sync) {
                LogSkillExecution("[P1_ALERT] POINTER CHANGED! Object被替换了？");
            }
        }*/
        
        // 诊断：检查 ExecutionEnvironment 是否被修改
        std::ostringstream diag_after_vm;
        diag_after_vm << "[DIAGNOSTIC] AFTER VM EXECUTE - env=" << (env ? "valid" : "null")
                      << " actor_atk=" << (env && env->GetActor() ? env->GetActor()->atk : -1)
                      << " actor_hp=" << (env && env->GetActor() ? env->GetActor()->hp : -1);
        LogSkillExecution(diag_after_vm.str());
        
        /*if (exec_result) {
            LogSkillExecution("[SKILL EXECUTE] SUCCESS: Skill '" + def_.id + "' executed successfully");
            fprintf(stderr, "[SKILL EXECUTE] SUCCESS: Skill '%s' executed successfully\n", def_.id.c_str());
        } else {
            LogSkillExecution("[SKILL EXECUTE] ERROR: Skill '" + def_.id + "' execution returned false");
            fprintf(stderr, "[SKILL EXECUTE] ERROR: Skill '%s' execution returned false\n", def_.id.c_str());
        }*/
        
        // 执行完毕后清除参数
        env->SetPara(nullptr);
        
        return exec_result ? 0 : -1;
    } catch (const std::exception& e) {
        std::ostringstream log_msg;
        log_msg << "[SKILL EXECUTE] EXCEPTION: Skill '" << def_.id << "' threw exception: " << e.what();
        LogSkillExecution(log_msg.str());
        fprintf(stderr, "[SKILL EXECUTE] EXCEPTION: Skill '%s' threw exception: %s\n", def_.id.c_str(), e.what());
        fflush(stderr);
        env->SetPara(nullptr);
        return -1;
    } catch (...) {
        LogSkillExecution("[SKILL EXECUTE] UNKNOWN EXCEPTION: Skill '" + def_.id + "' threw unknown exception");
        fprintf(stderr, "[SKILL EXECUTE] UNKNOWN EXCEPTION: Skill '%s' threw unknown exception\n", def_.id.c_str());
        fflush(stderr);
        env->SetPara(nullptr);
        return -1;
    }
}

std::string SkillPreset::GetParameter(const std::string& key, const std::string& default_val) const
{
    auto it = def_.parameters.find(key);
    return (it != def_.parameters.end()) ? it->second : default_val;
}

bool SkillPreset::HasParameter(const std::string& key) const
{
    return def_.parameters.find(key) != def_.parameters.end();
}

std::string SkillPreset::ToXml() const
{
    std::string xml = "<skill id=\"" + def_.id + "\"";
    xml += " type=\"" + def_.type + "\"";
    xml += " builtin=\"" + std::string(builtin_ ? "true" : "false") + "\"";
    xml += " />";
    return xml;
}


// ============ StatePreset 实现 ============

StatePreset::StatePreset(StateDefinition&& def)
    : def_(std::move(def)), builtin_(false)
{
}

int StatePreset::Execute(ExecutionEnvironment* env)
{
    if (!env) {
        return -1;
    }
    
    // TODO: 实现状态执行逻辑
    // 状态的具体执行由用户在脚本中定义
    return 0;
}

std::string StatePreset::GetParameter(const std::string& key, const std::string& default_val) const
{
    auto it = def_.parameters.find(key);
    return (it != def_.parameters.end()) ? it->second : default_val;
}

bool StatePreset::HasParameter(const std::string& key) const
{
    return def_.parameters.find(key) != def_.parameters.end();
}

std::string StatePreset::ToXml() const
{
    std::string xml = "<state id=\"" + def_.id + "\"";
    xml += " type=\"" + def_.type + "\"";
    xml += " duration=\"" + std::to_string(def_.default_duration) + "\"";
    xml += " builtin=\"" + std::string(builtin_ ? "true" : "false") + "\"";
    xml += " />";
    return xml;
}

}  // namespace abot

