/**
 * @file ScriptExecutionContainer.cpp
 * @brief 脚本执行容器的实现
 */

#include "ScriptExecutionContainer.h"
#include "ExecutionEnvironment.h"
#include "Scope.h"
#include "Bytecode.h"
#include "Value.h"
#include "Character.h"
#include "VM.h"
#include "SchemaValue.h"
#include "RoundManager.h"
#include <cstdio>
#include <sstream>

// 前向声明 - 全局指针（在RoundManager.cpp中定义，命名空间外部）
extern abot::RoundManager* g_current_round_manager;

namespace abot {

// 日志输出函数 - 同步到 battleinfo 面板
static void LogContainerExecution(const std::string& message) {
    std::string formatted = std::string("[CONTAINER] ") + message;
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog(formatted + "\n");
    }
}

static void LogEnvSelfState(ExecutionEnvironment* env, const std::string& phase) {
    if (!env) {
        LogContainerExecution(std::string("[ENV_SELF_DIAG][") + phase + "] env is null");
        return;
    }
    Value self = env->GetValueProperty("self");
    bool is_handle = self.IsHandle();
    bool is_schema = self.IsSchema();
    uint64_t handle_id = is_handle ? self.GetHandle().GetID() : 0;
    double multiplier = -999.0;
    if (self.IsSchema()) {
        if (self.HasField("turn")) {
            Value turn_field = self.GetField("turn");
            if (turn_field.IsSchema() && turn_field.HasField("multiplier")) {
                Value mult_field = turn_field.GetField("multiplier");
                if (mult_field.IsDouble()) {
                    multiplier = mult_field.GetDouble();
                } else if (mult_field.IsInt()) {
                    multiplier = (double)mult_field.GetInt();
                }
            }
        }
    }
    uintptr_t self_handle_ptr = reinterpret_cast<uintptr_t>(env->GetPointerProperty("self_handle_id", nullptr));
    char buf[256];
    snprintf(buf, sizeof(buf),
             "[ENV_SELF_DIAG][%s] env.self IsHandle=%d handle_id=%llu IsSchema=%d turn.multiplier=%.6f self_handle_id_ptr=%llu",
             phase.c_str(),
             is_handle ? 1 : 0,
             (unsigned long long)handle_id,
             is_schema ? 1 : 0,
             multiplier,
             (unsigned long long)self_handle_ptr);
    LogContainerExecution(buf);
    if (is_handle) {
        ObjectTable* table = env->GetObjectTable();
        if (table) {
            try {
                const SchemaValue& stored = table->Get(self.GetHandle());
                // 【修复】添加 HasField 检查，保护 ObjectTable 查询
                if (stored.HasField("turn")) {
                    Value turn_field = stored.GetField("turn");
                    if (turn_field.IsSchema() && turn_field.HasField("multiplier")) {
                        Value mult_field = turn_field.GetField("multiplier");
                        double obj_mult = -999.0;
                        if (mult_field.IsDouble()) obj_mult = mult_field.GetDouble();
                        else if (mult_field.IsInt()) obj_mult = (double)mult_field.GetInt();
                        char objbuf[256];
                        snprintf(objbuf, sizeof(objbuf),
                                 "[ENV_SELF_DIAG][%s] ObjectTable self.handle.turn.multiplier=%.6f",
                                 phase.c_str(), obj_mult);
                        LogContainerExecution(objbuf);
                    }
                } else {
                    char objbuf[256];
                    snprintf(objbuf, sizeof(objbuf),
                             "[ENV_SELF_DIAG][%s] ObjectTable stored schema missing 'turn' field",
                             phase.c_str());
                    LogContainerExecution(objbuf);
                }
            } catch (...) {
                char objbuf[256];
                snprintf(objbuf, sizeof(objbuf),
                         "[ENV_SELF_DIAG][%s] ObjectTable self.handle lookup failed for handle_id=%llu",
                         phase.c_str(), (unsigned long long)handle_id);
                LogContainerExecution(objbuf);
            }
        }
    }
}

// ============ 公开接口实现 ============

bool ScriptExecutionContainer::Execute(
    BytecodeProgram* script,
    ExecutionEnvironment* env,
    ScopeStack* scope,
    const std::vector<ScriptObjectSlotConfig>& slots)
{
    // 🟥【强制诊断】Execute 入口 - 直接写文件和 stderr，不依赖 g_current_round_manager
    FILE* diag_f = nullptr;
    fopen_s(&diag_f, "C:\\Windows\\Temp\\execute_entry_diag.txt", "at");
    if (diag_f) {
        fprintf(diag_f, "[Execute ENTRY] g_current_round_manager=%p script=%p env=%p scope=%p slots.size=%zu\n",
                g_current_round_manager, script, env, scope, slots.size());
        fclose(diag_f);
    }
  
    
    if (!env) {
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("[DEBUG] Execute: ExecutionEnvironment is null\n");
        }
        return false;
    }
    
    Value self = env->GetValueProperty("self");
    if (!self.IsHandle() || self.IsSchema()) {
        char buf[256];
        snprintf(buf, sizeof(buf),
            "[DEBUG] Execute: env.self is not pure handle (IsHandle=%d IsSchema=%d) after initialization",
            self.IsHandle() ? 1 : 0,
            self.IsSchema() ? 1 : 0);
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog(buf);
        }
        return false;  // ❌ 中止执行
    }
    
    if (g_current_round_manager) {
        char buf[256];
        snprintf(buf, sizeof(buf),
            "[EXEC_CHECK] Execute: env.self verified as pure handle (handle_id=%llu)",
            (unsigned long long)self.GetHandle().GetID());
        g_current_round_manager->AppendSkillTriggerLog(buf);
    }
    
    if (!script || !env || !scope) {
        return false;
    }
    
    // 保存原始对象副本，用于同步
    struct SlotSnapshot {
        ScriptObjectSlotConfig config;
        void* object;
        Value original_schema;
    };
    
    std::vector<SlotSnapshot> snapshots;
    
    LogEnvSelfState(env, "EXECUTE_ENTRY");
    // ========== 第1步：为每个槽位创建 Schema 副本并注入 ==========
    // 🟥【强制诊断】Step 1 开始 - 列出所有 slots
    {
        char buf[512];
        snprintf(buf, sizeof(buf), "[DEBUG] Received %zu slots: ", slots.size());
        std::string slot_names = buf;
        for (const auto& s : slots) {
            slot_names += s.slot_name + " ";
        }
        slot_names += " | about to process";
        
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog(slot_names);
        }
        LogContainerExecution(slot_names);
    }
    
    LogContainerExecution(std::string("Step 1: Processing ") + std::to_string(slots.size()) + " slots");
    
    for (const auto& slot : slots) {
        // 获取真实对象
        void* object = slot.getter(env);
        if (!object) {
            // 该槽位暂时不可用，跳过
            LogContainerExecution(std::string("Slot '") + slot.slot_name + "' object is null, skipping");
            continue;
        }
        
        // 【指令 4】对于 self，将纯 handle 解引用为 schema，然后加入 snapshots
        if (slot.slot_name == "self") {
            Value env_self = env->GetValueProperty("self");
            
            // ✔ 必须是纯 handle（IsHandle=1 && IsSchema=0）
            if (env_self.IsHandle() && !env_self.IsSchema()) {
                // 【补丁2：解引用 handle 为 schema】
                ObjectTable* obj_table = env->GetObjectTable();
                if (obj_table) {
                    try {
                        ObjectHandle h = env_self.GetHandle();
                        const SchemaValue& schema_from_table = obj_table->Get(h);
                        
                        // 构造一个 Value 类型的 schema（用于 snapshot）
                        Value self_schema_value = Value::CreateSchema();
                        const auto& fields = schema_from_table.GetAllFields();
                        for (const auto& kv : fields) {
                            self_schema_value.SetField(kv.first, kv.second);
                        }
                        
                        // 使用 schema 作为 snapshot 的值，而不是纯 handle
                        scope->SetVariable(slot.slot_name, self_schema_value);
                        env->SetValueProperty(slot.slot_name, self_schema_value);
                        
                        if (g_current_round_manager) {
                            char buf[256];
                            snprintf(buf, sizeof(buf),
                                "[EXEC_SELF] Pure handle dereferenced to schema: handle_id=%llu fields=%zu",
                                h.GetID(), fields.size());
                            g_current_round_manager->AppendSkillTriggerLog(buf);
                        }
                        
                        // 【关键】立即加入 snapshots，然后 continue
                        snapshots.push_back({slot, object, self_schema_value});
                        LogContainerExecution(std::string("Initialized slot '") + slot.slot_name + "' with schema (dereferenced from handle)");
                        continue;  // ✅ 在 snapshots.push_back 之后 continue
                    }
                    catch (const std::exception& ex) {
                        if (g_current_round_manager) {
                            char buf[256];
                            snprintf(buf, sizeof(buf),
                                "[EXEC_SELF_ERROR] Failed to dereference handle: %s",
                                ex.what());
                            g_current_round_manager->AppendSkillTriggerLog(buf);
                        }
                        continue;
                    }
                }
                
                // 如果没有 obj_table，回退到纯 handle
                scope->SetVariable(slot.slot_name, env_self);
                env->SetValueProperty(slot.slot_name, env_self);
                
                if (g_current_round_manager) {
                    char buf[256];
                    snprintf(buf, sizeof(buf),
                        "[EXEC_SELF] Pure handle injected (no obj_table): handle_id=%llu",
                        env_self.GetHandle().GetID());
                    g_current_round_manager->AppendSkillTriggerLog(buf);
                }
                
                snapshots.push_back({slot, object, env_self});
                LogContainerExecution(std::string("Initialized slot '") + slot.slot_name + "' with pure handle (fallback)");
                continue;
            } else {
                // ❌ 如果 env.self 不是纯 handle，这是致命错误
                if (g_current_round_manager) {
                    char buf[256];
                    snprintf(buf, sizeof(buf),
                        "[DEBUG] Execute: env.self is not pure handle (IsHandle=%d IsSchema=%d)",
                        env_self.IsHandle() ? 1 : 0,
                        env_self.IsSchema() ? 1 : 0);
                    g_current_round_manager->AppendSkillTriggerLog(buf);
                }
                continue;  // ❌ 拒绝执行该槽位
            }
        }
        
        // 【对于非 self 变量的处理】
        Value schema;
        if (!schema.IsSchema() && env && env->HasProperty(slot.slot_name)) {
            Value existing = env->GetValueProperty(slot.slot_name);
            if (existing.IsSchema() || existing.IsHandle()) {
                schema = existing;
                double existing_mult = -999.0;
                bool has_multiplier = false;
                if (existing.HasField("turn")) {
                    Value turn_field = existing.GetField("turn");
                    if (turn_field.IsSchema() && turn_field.HasField("multiplier")) {
                        Value mult_field = turn_field.GetField("multiplier");
                        if (mult_field.IsDouble()) {
                            existing_mult = mult_field.GetDouble();
                            has_multiplier = true;
                        } else if (mult_field.IsInt()) {
                            existing_mult = (double)mult_field.GetInt();
                            has_multiplier = true;
                        }
                    }
                }
                char buf[256];
                snprintf(buf, sizeof(buf),
                        "Reusing env.%s schema/handle for nested execution type=%d handle=%llu turn.multiplier=%.6f",
                        slot.slot_name.c_str(),
                        (int)existing.GetType(),
                        (unsigned long long)(existing.IsHandle() ? existing.GetHandle().GetID() : 0),
                        has_multiplier ? existing_mult : -999.0);
                LogContainerExecution(buf);
            }
        }
        if (!schema.IsSchema() && !schema.IsHandle()) {
            schema = slot.to_schema(object);
            LogContainerExecution(std::string("Created fresh schema for slot '") + slot.slot_name + "'");
        }
        
        // 注入到 ScopeStack 和 ExecutionEnvironment
        scope->SetVariable(slot.slot_name, schema);
        env->SetValueProperty(slot.slot_name, schema);
        
        // 保存快照，用于后续同步
        snapshots.push_back({slot, object, schema});
        
        LogContainerExecution(std::string("Initialized slot '") + slot.slot_name + "' with schema");
    }
    LogContainerExecution(std::string("Step 1 complete: ") + std::to_string(snapshots.size()) + " snapshots created");
    
    // ========== 第2步：执行脚本 ==========
    // 【关键】设置 ScopeStack 到 ExecutionEnvironment，使 builtin 函数能访问脚本变量
    env->SetCurrentScope(scope);
    
    // 【诊断】在执行 VM 前检查 g_current_round_manager 状态
    {
        extern abot::RoundManager* g_current_round_manager;
        char diag_buf[256];
        snprintf(diag_buf, sizeof(diag_buf),
                "[VM_CALL_BEFORE] g_current_round_manager=%s script_size=%u",
                g_current_round_manager != nullptr ? "valid" : "NULL",
                (unsigned int)(script ? script->instructions.size() : 0));
        if (g_current_round_manager != nullptr) {
            g_current_round_manager->AppendSkillTriggerLog(diag_buf);
            g_current_round_manager->AppendSkillTriggerLog("\n");
        } else {
            LogContainerExecution(diag_buf);  // 如果 RM 为 null，至少记录到容器日志
        }
    }
    
    // 【硬日志】在调用 vm.Execute() 前
    if (g_current_round_manager != nullptr) {
        g_current_round_manager->AppendSkillTriggerLog("[DEBUG] [HARDLOG] About to call vm.Execute\n");
    } else {
        LogContainerExecution("[HARDLOG] About to call vm.Execute");
    }
    
    VM vm;
    bool exec_result = vm.Execute(script, scope);
    
    // 【硬日志】vm.Execute() 返回
    if (g_current_round_manager != nullptr) {
        g_current_round_manager->AppendSkillTriggerLog("[DEBUG] [HARDLOG] vm.Execute returned normally\n");
    } else {
        LogContainerExecution("[HARDLOG] vm.Execute returned normally");
    }
    
    // 【关键】脚本执行完成，清除 scope 引用
    env->SetCurrentScope(nullptr);
    
    LogContainerExecution(std::string("VM::Execute returned: ") + std::to_string((int)exec_result) + " (0=false, 1=true)");
    LogContainerExecution(std::string("Script execution ") + (exec_result ? "succeeded" : "failed"));
    
    // ========== 第3步：从 ScopeStack 取回修改后的 Schema，同步到 env ==========
    // ========== 第3步：诊断（不作为同步路径） ==========
    // 【架构】Step 3 仅用于诊断和调试，不再作为同步的 gate。
    // 真正的同步由 ObjectTable 驱动，在 Step 4 完成。
    
    LogContainerExecution(std::string("[ARCH] Step 3: Diagnostic - Inspecting modified schemas from ") + std::to_string(snapshots.size()) + " snapshots (non-gating)");
    
    for (auto& snapshot : snapshots) {
        const std::string& slot_name = snapshot.config.slot_name;
        
        // 从 ScopeStack 取回修改后的值（诊断用）
        Value modified_value = scope->GetVariable(slot_name);
        
        // 【诊断】记录取回的值类型
        char buf_diag[256];
        snprintf(buf_diag, sizeof(buf_diag), 
            "[SYNC_DIAGNOSTIC] slot='%s' retrieved_type=IsSchema:%d IsHandle:%d",
            slot_name.c_str(), 
            modified_value.IsSchema() ? 1 : 0,
            modified_value.IsHandle() ? 1 : 0);
        LogContainerExecution(buf_diag);
        
        // 【架构说明】
        // 注意：即使 modified_value.IsSchema() 为 false，我们也不会阻止后续同步。
        // 真正的数据来源是 ObjectTable 中的 SchemaValue，
        // 而不是这里的 ScopeStack 中的 Value 对象。
        if (!modified_value.IsSchema()) {
            LogContainerExecution(std::string("[SYNC_DIAGNOSTIC] slot='") + slot_name + 
                "' - snapshot IsSchema check failed, but this is non-fatal; relying on ObjectTable");
        }
    }
    LogContainerExecution("[ARCH] Step 3 complete - diagnostic only, proceeding to Step 4 for canonical synchronization");
    
    // ========== 第4步：规范的属性回写（ObjectTable → Character）==========
    // 【架构】Step 4 不再依赖 Step 3 的 IsSchema 检查，而是直接从 ObjectTable 拉数据。
    // SyncSlot + SyncCharacterData 构成从 ObjectTable 到 Character 成员的完整数据链路。
    
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog("[HARDLOG] About to enter Step 4 - canonical synchronization phase\n");
    }
    
    LogContainerExecution("[ARCH] Step 4: Canonical synchronization - writing back from ObjectTable to Character");
    LogContainerExecution(std::string("  - Processing ") + std::to_string(snapshots.size()) + " snapshots");
    
    for (const auto& snapshot : snapshots) {
        if (!snapshot.config.needs_writeback) {
            LogContainerExecution(std::string("  - Slot '") + snapshot.config.slot_name + "' marked as read-only, skipping writeback");
            continue;
        }
        
        char buf[256];
        snprintf(buf, sizeof(buf), "[ARCH] Calling SyncSlot for slot='%s' (object=%p)",
                 snapshot.config.slot_name.c_str(), snapshot.object);
        LogContainerExecution(buf);
        
        // SyncSlot 职责：从 ObjectTable 拉取 schema → 镜像到 character->extra → 回写到 C++ 成员
        SyncSlot(snapshot.config, env, scope, snapshot.object, snapshot.original_schema);
    }
    
    // 【指令 4】如果 snapshots 为空，临时强制对 self 调用一次 SyncSlot（验证链路）
    if (snapshots.empty() && !slots.empty()) {
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("[ARCH] snapshots empty! Forcing SyncSlot(self) for diagnosis\n");
        }
        
        for (const auto& slot : slots) {
            if (slot.slot_name == "self") {
                void* self_object = slot.getter(env);
                Value env_self = env->GetValueProperty("self");
                
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[ARCH] Emergency: Calling SyncSlot(self) with self_object=%p, env.self.IsHandle=%d",
                    self_object, env_self.IsHandle() ? 1 : 0);
                LogContainerExecution(buf);
                
                SyncSlot(slot, env, scope, self_object, env_self);
                break;
            }
        }
    }
    
    // ========== 第5步：SyncCharacterData - 规范的属性回写 ==========
    // 【架构关键】SyncCharacterData 是从 character->extra 回写到 Character 成员字段的主路径。
    // 这不是"backup"，而是标准流程的一部分，确保 extra 中的修改能生效到 C++ 对象。
    
    LogContainerExecution("[ARCH] Step 5: SyncCharacterData - canonical property writeback from extra to C++ members");
    
    for (const auto& snapshot : snapshots) {
        if (snapshot.config.slot_name == "self" && snapshot.config.needs_writeback) {
            Character* character = static_cast<Character*>(snapshot.object);
            if (character && env) {
                LogContainerExecution(std::string("  - SyncCharacterData for character: ") + character->name);
                env->SyncCharacterData(character);
            }
        }
    }
    
    // 【容错设计】如果 VM 返回 false 但 snapshots 已被处理，仍视为成功
    if (!exec_result && snapshots.size() > 0) {
        LogContainerExecution(std::string("[ARCH] VM returned false but ") + std::to_string(snapshots.size()) + 
            " slots were processed via ObjectTable → Character pipeline, returning true");
        return true;
    }
    
    return exec_result;
}

bool ScriptExecutionContainer::ExecuteWithSelf(
    BytecodeProgram* script,
    ExecutionEnvironment* env,
    ScopeStack* scope)
{
    
    
    Character* actor = nullptr;
    if (env) {
        actor = env->GetActor();
    }
    
    LogContainerExecution("ExecuteWithSelf ENTRY POINT REACHED");
    
    // 🟥 【任务3】记录 env.self 的 handle
    if (env) {
        Value env_self = env->GetValueProperty("self");
        if (g_current_round_manager) {
            char buf[256];
            snprintf(buf, sizeof(buf),
                "[DIAG][CONTAINER] ExecuteWithSelf: env.self IsHandle=%d handle=%d",
                env_self.IsHandle() ? 1 : 0,
                env_self.IsHandle() ? (int)env_self.GetHandle().GetID() : -1);
            g_current_round_manager->AppendSkillTriggerLog(buf);
        }
    }
    
    if (actor) {
        LogContainerExecution(std::string("Actor: ") + actor->name);
    } else {
        LogContainerExecution("ERROR: Actor is nullptr!");
    }
    
    std::vector<ScriptObjectSlotConfig> slots = {
        CreateDefaultSelfSlot()
    };
    
    
    // 🟥【新增】【关键修复】避免递归时重新初始化 self
    // 检查是否已经有一个 self Handle 存在（来自外层环境）
    bool has_existing_self = false;
    if (env && actor) {
        Value existing_self = env->GetValueProperty("self");
        // 仅在 self 未被初始化时才注册（不是 Handle 且不是 Schema）
        if (existing_self.IsHandle() || existing_self.IsSchema()) {
            has_existing_self = true;
        }
    }
    
    // 只在第一次（外层调用）时初始化 self
    if (env && actor && !has_existing_self) {
        env->RegisterSelf(actor);
    }
    
    LogEnvSelfState(env, "EXECUTE_WITH_SELF_ENTRY");
    if (scope) {
        Value scope_self = scope->GetVariable("self");
        bool scope_is_handle = scope_self.IsHandle();
        bool scope_is_schema = scope_self.IsSchema();
        uint64_t scope_handle_id = scope_is_handle ? scope_self.GetHandle().GetID() : 0;
        double scope_mult = -999.0;
        if (scope_self.IsSchema()) {
            Value turn_field = scope_self.GetField("turn");
            if (turn_field.IsSchema()) {
                Value mult_field = turn_field.GetField("multiplier");
                if (mult_field.IsDouble()) scope_mult = mult_field.GetDouble();
                else if (mult_field.IsInt()) scope_mult = (double)mult_field.GetInt();
            }
        }
        char buf_scope[256];
        snprintf(buf_scope, sizeof(buf_scope),
                 "[EXECUTE_WITH_SELF][SCOPE_SELF] IsHandle=%d handle_id=%llu IsSchema=%d turn.multiplier=%.6f",
                 scope_is_handle ? 1 : 0,
                 (unsigned long long)scope_handle_id,
                 scope_is_schema ? 1 : 0,
                 scope_mult);
        LogContainerExecution(buf_scope);
    }
    
    bool result = Execute(script, env, scope, slots);
    
    if (actor) {
        LogContainerExecution(std::string("After Execute: actor->atk = ") + std::to_string(actor->atk));
    }
    
    return result;
}

ScriptObjectSlotConfig ScriptExecutionContainer::CreateDefaultSelfSlot()
{
    ScriptObjectSlotConfig slot;
    slot.slot_name = "self";
    slot.needs_writeback = true;
    
    // getter：获取 env 的 Actor
    slot.getter = [](ExecutionEnvironment* env) -> void* {
        if (env) {
            return env->GetActor();
        }
        return nullptr;
    };
    
    // to_schema：从 Character 创建纯 Handle，初始化 ObjectTable 中的字段
    // 🟥【任务1-2】改为返回纯 handle 而不是 schema
    slot.to_schema = [](void* object) -> Value {
        Character* character = static_cast<Character*>(object);
        if (!character) {
            return Value();
        }
        
        // 🟥【任务1】获取当前执行环境的 ObjectTable
        ExecutionEnvironment* env = ExecutionEnvironment::Current();
        if (!env) {
            return Value();
        }
        
        ObjectTable* obj_table = env->GetObjectTable();
        if (!obj_table) {
            return Value();
        }
        
        // 🟥【任务2】创建一个新的 handle 来代表 self
        ObjectHandle self_handle;
        try {
            self_handle = obj_table->CreateEmpty();
        } catch (const std::exception& ex) {
            if (g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[ERROR] Failed to create handle in ObjectTable: %s",
                    ex.what());
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
            return Value();
        }
        
        // 🟥【UFRS】从 character->extra 自动构建 ObjectTable 字段
        // 不再手写每个字段，而是遍历 extra 中的所有字段写入
        try {
            SchemaValue& self_schema = obj_table->Get(self_handle);
            
            // 确保 extra 已初始化（如果 ParameterParser 未填充，则 fallback）
            if (character->extra.empty()) {
                // Fallback：从 C++ 成员构建 extra（兼容旧路径）
                env->InitializeCharacterExtra(character);
            }
            
            // 🟥【UFRS】遍历 extra，将所有字段写入 ObjectTable
            for (auto it = character->extra.begin(); it != character->extra.end(); ++it) {
                self_schema.SetField(it->first, it->second);
            }
            
            if (g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[SELF_INIT][UFRS] Initialized ObjectTable handle=%llu for character=%s (%zu extra fields)",
                    (unsigned long long)self_handle.GetID(),
                    character->name.c_str(),
                    character->extra.size());
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
        } catch (const std::exception& ex) {
            if (g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[ERROR] Failed to initialize self handle fields: %s",
                    ex.what());
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
            return Value();
        }
        
        // 🟥【任务2】返回纯 handle（不是 schema）
        Value result;
        result.SetHandle(self_handle);  // ✅ IsHandle=1, IsSchema=0
        
        // 🟥【任务1.3】硬日志验证 - 检查 SetHandle 是否正确生效
        bool is_pure_handle = result.IsHandle() && !result.IsSchema();
        if (g_current_round_manager) {
            char buf[256];
            snprintf(buf, sizeof(buf),
                "[DIAG][SETHANDLE] SetHandle called, is_pure_handle=%d IsHandle=%d IsSchema=%d type=%d",
                is_pure_handle ? 1 : 0,
                result.IsHandle() ? 1 : 0,
                result.IsSchema() ? 1 : 0,
                (int)result.GetType());
            g_current_round_manager->AppendSkillTriggerLog(buf);
        }
        
        if (g_current_round_manager) {
            char buf[256];
            snprintf(buf, sizeof(buf),
                "[EXEC_SELF_INIT] to_schema returning: handle_id=%llu IsHandle=%d IsSchema=%d is_pure=%d",
                (unsigned long long)self_handle.GetID(),
                result.IsHandle() ? 1 : 0,
                result.IsSchema() ? 1 : 0,
                is_pure_handle ? 1 : 0);
            g_current_round_manager->AppendSkillTriggerLog(buf);
        }
        
        return result;
    };
    
    // from_schema：从修改后的 Schema 写回 Character
    // 这与 ExecutionEnvironment::SyncCharacterData 的逻辑相同
    slot.from_schema = [](void* object, const Value& schema) {
        Character* character = static_cast<Character*>(object);
        if (!character || !schema.IsSchema()) {
            fprintf(stderr, "[from_schema] 警告：无效的character或schema\n");
            FILE* f = nullptr;
            fopen_s(&f, "C:\\Windows\\Temp\\container_diag.txt", "at");
            if (f) {
                fprintf(f, "[from_schema] 警告：无效的character或schema\n");
                fflush(f);
                fclose(f);
            }
            return;
        }

        // 【诊断4】from_schema 回调输入的 Schema 内容
        {
            char diag_buf[512];
            int atk_num = 0, d1 = 0, d2 = 0, d3 = 0, d4 = 0;
            if (schema.HasField("atk")) {
                Value atk_val = schema.GetField("atk");
                if (atk_val.IsSchema() && atk_val.HasField("value")) {
                    atk_num = (int)atk_val.GetField("value").GetInt();
                }
            }
            if (schema.HasField("dmg")) {
                Value dmg_val = schema.GetField("dmg");
                if (dmg_val.IsSchema()) {
                    if (dmg_val.HasField("d1")) d1 = (int)dmg_val.GetField("d1").GetInt();
                    if (dmg_val.HasField("d2")) d2 = (int)dmg_val.GetField("d2").GetInt();
                    if (dmg_val.HasField("d3")) d3 = (int)dmg_val.GetField("d3").GetInt();
                    if (dmg_val.HasField("d4")) d4 = (int)dmg_val.GetField("d4").GetInt();
                }
            }
            snprintf(diag_buf, sizeof(diag_buf),
                "[FROM_SCHEMA_INPUT] name=%s schema_atk=%d dmg=[%d,%d,%d,%d]\n",
                character->name.c_str(), atk_num, d1, d2, d3, d4);
            fprintf(stderr, "%s", diag_buf);
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog(diag_buf);
            }
        }
        
        // 🟥【任务4】记录 from_schema 开始 - 记录即将被修改的对象
        if (g_current_round_manager) {
            char buf[256];
            snprintf(buf, sizeof(buf),
                "[DIAG][WRITE] from_schema START: character=%s ptr=%p (THIS WILL BE MODIFIED)",
                character->name.c_str(),
                (void*)character);
            g_current_round_manager->AppendSkillTriggerLog(buf);
        }
        
        // 诊断：from_schema被调用
        fprintf(stderr, "[from_schema] 被调用，开始同步 %s\n", character->name.c_str());
        Value self_schema_summary = schema;
        if (schema.IsSchema() || schema.IsHandle()) {
            bool is_handle = schema.IsHandle();
            bool is_schema = schema.IsSchema();
            uint64_t handle_id = is_handle ? schema.GetHandle().GetID() : 0;
            double mult = -999.0;
            if (schema.IsSchema()) {
                Value turn_field = schema.GetField("turn");
                if (turn_field.IsSchema()) {
                    Value mult_field = turn_field.GetField("multiplier");
                    if (mult_field.IsDouble()) mult = mult_field.GetDouble();
                    else if (mult_field.IsInt()) mult = (double)mult_field.GetInt();
                }
            }
            fprintf(stderr, "[FROM_SCHEMA] schema IsHandle=%d handle_id=%llu IsSchema=%d turn.multiplier=%.6f\n",
                    is_handle ? 1 : 0,
                    (unsigned long long)handle_id,
                    is_schema ? 1 : 0,
                    mult);
        }
        FILE* from_diag = nullptr;
        fopen_s(&from_diag, "C:\\Windows\\Temp\\container_diag.txt", "at");
        if (from_diag) {
            fprintf(from_diag, "[from_schema] 被调用，开始同步 %s\n", character->name.c_str());
            fflush(from_diag);
            fclose(from_diag);
        }
        
        // 🟥【任务4】记录 name 字段修改
        Value name_val = schema.GetField("name");
        if (name_val.IsString()) {
            std::string old_name = character->name;
            std::string new_name = name_val.GetString();
            if (old_name != new_name && g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[DIAG][WRITE] name change: %s -> %s (ptr=%p)",
                    old_name.c_str(),
                    new_name.c_str(),
                    (void*)character);
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
            character->name = new_name;
        }
        
        // 🟥【任务4】记录 camp 字段修改
        Value camp_val = schema.GetField("camp");
        if (camp_val.IsInt()) {
            int old_camp = character->camp;
            int new_camp = (int)camp_val.GetInt();
            if (old_camp != new_camp && g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[DIAG][WRITE] camp change: %d -> %d (ptr=%p name=%s)",
                    old_camp,
                    new_camp,
                    (void*)character,
                    character->name.c_str());
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
            character->camp = new_camp;
        }
        
        // ✅ ATK 现在作为 Schema{value: int} 提取
        Value atk_val = schema.GetField("atk");
        if (atk_val.IsSchema()) {
            Value atk_value_field = atk_val.GetField("value");
            if (atk_value_field.IsInt()) {
                int atk_from_schema = atk_value_field.GetInt();
                {
                    char buf[256];
                    snprintf(buf, sizeof(buf), "[from_schema] 正在同步 atk=%d", atk_from_schema);
                    fprintf(stderr, "%s\n", buf);
                    if (g_current_round_manager) {
                        g_current_round_manager->AppendSkillTriggerLog(std::string(buf) + "\n");
                    }
                }
                character->atk = atk_from_schema;
                // 🟥【任务4】记录 atk 字段修改
                if (g_current_round_manager) {
                    char buf[256];
                    snprintf(buf, sizeof(buf),
                        "[DIAG][WRITE] atk written: %d (ptr=%p name=%s)",
                        atk_from_schema,
                        (void*)character,
                        character->name.c_str());
                    g_current_round_manager->AppendSkillTriggerLog(buf);
                }
            }
        }
        
        Value hp_val = schema.GetField("hp");
        if (hp_val.IsInt()) {
            character->hp = hp_val.GetInt();
        }
        
        Value max_hp_val = schema.GetField("max_hp");
        if (max_hp_val.IsInt()) {
            character->max_hp = max_hp_val.GetInt();
        }
        
        Value hp_restore_val = schema.GetField("hp_restore");
        if (hp_restore_val.IsInt()) {
            character->hp_restore = hp_restore_val.GetInt();
        }
        
        Value temp_hp_val = schema.GetField("temp_hp");
        if (temp_hp_val.IsInt()) {
            character->temp_hp = temp_hp_val.GetInt();
        }
        
        Value aggro_val = schema.GetField("aggro");
        if (aggro_val.IsInt()) {
            character->aggro = aggro_val.GetInt();
        }
        
        Value is_alive_val = schema.GetField("is_alive");
        if (is_alive_val.IsInt()) {
            character->is_alive = (is_alive_val.GetInt() != 0);
        }
        
        // 同步 dmg 子对象
        Value dmg_schema = schema.GetField("dmg");
        if (dmg_schema.IsSchema()) {
            Value d1_val = dmg_schema.GetField("d1");
            if (d1_val.IsInt()) character->dmg[0] = d1_val.GetInt();
            
            Value d2_val = dmg_schema.GetField("d2");
            if (d2_val.IsInt()) character->dmg[1] = d2_val.GetInt();
            
            Value d3_val = dmg_schema.GetField("d3");
            if (d3_val.IsInt()) character->dmg[2] = d3_val.GetInt();
            
            Value d4_val = dmg_schema.GetField("d4");
            if (d4_val.IsInt()) character->dmg[3] = d4_val.GetInt();
        }
        
        // ✨ 【新增】同步 turn 子对象（修复大成功2倍乘数未同步的问题）
        Value turn_schema = schema.GetField("turn");
        if (turn_schema.IsSchema()) {
            Value multiplier_val = turn_schema.GetField("multiplier");
            double new_multiplier = 1.0;  // 默认值
            bool found_multiplier = false;
            
            // 接受 Double 或 Int 类型（脚本运算可能导致Int或Double）
            if (multiplier_val.IsDouble()) {
                new_multiplier = multiplier_val.GetDouble();
                found_multiplier = true;
                fprintf(stderr, "[from_schema] 从 Double 读取 turn.multiplier=%f\n", new_multiplier);
            } else if (multiplier_val.IsInt()) {
                new_multiplier = (double)multiplier_val.GetInt();
                found_multiplier = true;
                fprintf(stderr, "[from_schema] 从 Int 读取 turn.multiplier=%f\n", new_multiplier);
            }
            
            if (found_multiplier) {
                // 【断点4诊断】从ObjectTable/Schema读取的multiplier值
                char diag_msg[256];
                snprintf(diag_msg, sizeof(diag_msg),
                        "[HANDLE断点4] from_schema: 读取turn.multiplier = %.6f (期望2.0)",
                        new_multiplier);
                fprintf(stderr, "%s\n", diag_msg);
                
                FILE* f = nullptr;
                if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
                    fprintf(f, "%s\n", diag_msg);
                    fclose(f);
                }
                
                // 输出到battleinfo
                if (g_current_round_manager != nullptr) {
                    g_current_round_manager->AppendSkillTriggerLog(std::string(diag_msg) + "\n");
                }
                
                // 🟥【任务4】记录 turn.multiplier 修改
                if (g_current_round_manager) {
                    char buf[256];
                    snprintf(buf, sizeof(buf),
                        "[DIAG][WRITE] turn.multiplier written: %.6f (ptr=%p name=%s)",
                        new_multiplier,
                        (void*)character,
                        character->name.c_str());
                    g_current_round_manager->AppendSkillTriggerLog(buf);
                }
                
                double old_mult = character->turn.multiplier;
                character->turn.multiplier = new_multiplier;
                
                FILE* f2 = nullptr;
                fopen_s(&f2, "C:\\dodamage_diagnostic.log", "at");
                if (f2) {
                    fprintf(f2, "[from_schema] ✓ 成功同步 turn.multiplier: %.6f -> %.6f\n", old_mult, new_multiplier);
                    fflush(f2);
                    fclose(f2);
                }
                
                // 【诊断】也输出到VM诊断日志
                FILE* f3 = nullptr;
                if (fopen_s(&f3, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f3) {
                    fprintf(f3, "[SYNC_TO_CHARACTER] turn.multiplier: %.6f -> %.6f (from ObjectTable/Schema)\n", old_mult, new_multiplier);
                    fflush(f3);
                    fclose(f3);
                }
            }
        }

        // 【诊断5】from_schema 回调完成 - 打印写入后的 Character 值
        {
            char diag_buf[512];
            snprintf(diag_buf, sizeof(diag_buf),
                "[FROM_SCHEMA_WRITE] name=%s char_atk=%d char_dmg=[%d,%d,%d,%d]\n",
                character->name.c_str(),
                character->atk,
                character->dmg[0], character->dmg[1], character->dmg[2], character->dmg[3]);
            fprintf(stderr, "%s", diag_buf);
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog(diag_buf);
            }
        }
    };
    
    return slot;
}

// ============ 内部辅助方法 ============

void ScriptExecutionContainer::SyncSlot(
    const ScriptObjectSlotConfig& slot,
    ExecutionEnvironment* env,
    ScopeStack* scope,
    void* object,
    const Value& original_schema)
{
    // 【指令 6】SyncSlot 入口必须无条件打日志
    // 强制输出到日志，不依赖 g_current_round_manager 检查
    char buf_entry[256];
    snprintf(buf_entry, sizeof(buf_entry),
        "[DEBUG] slot_name=%s object=%p env=%p needs_writeback=%d",
        slot.slot_name.c_str(), object, env, slot.needs_writeback ? 1 : 0);
    
    // 首选：使用 RoundManager 输出
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog(buf_entry);
    }
    
    // 备选：输出到文件 + stderr
    FILE* diag_f = nullptr;
    fopen_s(&diag_f, "C:\\Windows\\Temp\\syncslot_entry_diag.txt", "at");
    if (diag_f) {
        fprintf(diag_f, "%s\n", buf_entry);
        fclose(diag_f);
    }
    fprintf(stderr, "%s\n", buf_entry);
    
    if (!object) {
        return;
    }
    
    // 🟥【任务5修复】对于 self 的纯 handle，**必须同步 ObjectTable 修改回 Character**
    if (slot.slot_name == "self") {
        Character* character = static_cast<Character*>(object);
        if (!character) {
            return;
        }

        // 从 env 获取 self 的 handle
        Value env_self = env->GetValueProperty("self");
        if (!env_self.IsHandle() || env_self.IsSchema()) {
            return;
        }

        ObjectHandle self_handle = env_self.GetHandle();
        ObjectTable* obj_table = env->GetObjectTable();
        if (!obj_table) {
            return;
        }

        try {
            SchemaValue& self_schema = obj_table->Get(self_handle);

            if (g_current_round_manager) {
                char diag_buf[512];
                snprintf(diag_buf, sizeof(diag_buf),
                    "[SYNCSLOT_BEFORE] %s: atk=%d dmg=[%d,%d,%d,%d] mult=%.2f",
                    character->name.c_str(),
                    character->atk,
                    character->dmg[0], character->dmg[1], character->dmg[2], character->dmg[3],
                    character->turn.multiplier);
                g_current_round_manager->AppendSkillTriggerLog(diag_buf);
            }

            // 🟥【UFRS - 通用字段同步】从 ObjectTable[self] 同步回 character->extra
            for (auto& kv : character->extra) {
                const std::string& key = kv.first;
                if (!self_schema.HasField(key)) {
                    continue;
                }

                Value field_from_table = self_schema.GetField(key);
                kv.second = field_from_table;
            }

            // 🟥【镜像同步】将 extra 中的字段值同步回 Character 成员
            {
                auto it_name = character->extra.find("name");
                if (it_name != character->extra.end()) {
                    if (it_name->second.IsString()) {
                        character->name = it_name->second.GetString();
                    }
                }
            }

            {
                auto it_camp = character->extra.find("camp");
                if (it_camp != character->extra.end()) {
                    if (it_camp->second.IsSchema() && it_camp->second.HasField("value")) {
                        Value v = it_camp->second.GetField("value");
                        if (v.IsInt()) character->camp = v.IsInt() ? v.GetInt() : (int)v.GetDouble();
                    } else if (it_camp->second.IsInt()) {
                        character->camp = static_cast<int>(it_camp->second.GetInt());
                    }
                }
            }

            {
                auto it_atk = character->extra.find("atk");
                if (it_atk != character->extra.end()) {
                    Value atk_val = it_atk->second;
                    if (atk_val.IsSchema() && atk_val.HasField("value")) {
                        Value v = atk_val.GetField("value");
                        character->atk = v.IsInt() ? v.GetInt() : (int)v.GetDouble();
                    }
                }
            }

            {
                auto it_hp = character->extra.find("hp");
                if (it_hp != character->extra.end()) {
                    Value hp_val = it_hp->second;
                    if (hp_val.IsSchema()) {
                        if (hp_val.HasField("value")) {
                            Value v = hp_val.GetField("value");
                            character->hp = v.IsInt() ? v.GetInt() : (int)v.GetDouble();
                        }
                        if (hp_val.HasField("max")) {
                            Value v = hp_val.GetField("max");
                            character->max_hp = v.IsInt() ? v.GetInt() : (int)v.GetDouble();
                        }
                    }
                }
            }

            {
                auto it_dmg = character->extra.find("dmg");
                if (it_dmg != character->extra.end()) {
                    Value dmg_val = it_dmg->second;
                    if (dmg_val.IsSchema()) {
                        const char* names[4] = {"d1","d2","d3","d4"};
                        for (int i = 0; i < 4; ++i) {
                            if (dmg_val.HasField(names[i])) {
                                Value v = dmg_val.GetField(names[i]);
                                character->dmg[i] = v.IsInt() ? v.GetInt() : (int)v.GetDouble();
                            }
                        }
                    }
                }
            }

            {
                auto it_dfs = character->extra.find("dfs");
                if (it_dfs != character->extra.end()) {
                    Value dfs_val = it_dfs->second;
                    if (dfs_val.IsSchema() && dfs_val.HasField("value")) {
                        Value v = dfs_val.GetField("value");
                        int dfs_int = v.IsInt() ? v.GetInt() : (int)v.GetDouble();
                        character->defenses.clear();
                        if (dfs_int > 0) {
                            character->defenses.push_back({dfs_int, ""});
                        }
                    }
                }
            }

            {
                auto it_turn = character->extra.find("turn");
                if (it_turn != character->extra.end()) {
                    Value turn_val = it_turn->second;
                    if (turn_val.IsSchema() && turn_val.HasField("multiplier")) {
                        Value v = turn_val.GetField("multiplier");
                        character->turn.multiplier = v.IsDouble() ? v.GetDouble() : (double)v.GetInt();
                    }
                }
            }

            if (g_current_round_manager) {
                char diag_buf[512];
                snprintf(diag_buf, sizeof(diag_buf),
                    "[SYNCSLOT_AFTER] %s: atk=%d hp=%d max_hp=%d dmg=[%d,%d,%d,%d] dfs=%zu turn=%.2f",
                    character->name.c_str(),
                    character->atk,
                    character->hp,
                    character->max_hp,
                    character->dmg[0], character->dmg[1], character->dmg[2], character->dmg[3],
                    character->defenses.size(),
                    character->turn.multiplier);
                g_current_round_manager->AppendSkillTriggerLog(diag_buf);
            }
        } catch (const std::exception& ex) {
            if (g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[ERROR] SyncSlot(self) failed: %s", ex.what());
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
        }

        return;  // ✔ 对 self 的同步已完成
    }
    
    // 对于其他槽位，进行常规的 from_schema 回写
    if (!slot.from_schema) {
        return;
    }
    
    Value modified_schema = scope->GetVariable(slot.slot_name);
    if (!modified_schema.IsSchema()) {
        return;
    }
    
    // 调用槽位的 from_schema 回调，将修改应用到真实对象
    slot.from_schema(object, modified_schema);
}

}  // namespace abot
