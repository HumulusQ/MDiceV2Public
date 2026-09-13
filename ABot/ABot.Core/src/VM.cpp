/**
 * @file VM.cpp
 * @brief ABOT 虚拟机的实现
 */

#include "VM.h"
#include "ExecutionEnvironment.h"
#include "Character.h"
#include "PresetSystem.h"
#include "RoundManager.h"
#include "SchemaValue.h"
#include <inttypes.h>

// 前向声明 - 全局指针（在RoundManager.cpp中定义，命名空间外部）
extern abot::RoundManager* g_current_round_manager;

namespace abot {

// 日志帮助函数 - 输出到battleinfo
// 尝试通过多个渠道输出诊断日志（优先级由高到低）
static void LogDiagnosticToBattleInfo(const std::string& message) {
    // 【方法1】使用全局指针（可能已失效）
    if (g_current_round_manager != nullptr) {
        g_current_round_manager->AppendSkillTriggerLog(message + "\n");
        return;
    }
    
    // 【方法2】通过 ExecutionEnvironment 获取诊断日志目标
    ExecutionEnvironment* env = ExecutionEnvironment::Current();
    if (env != nullptr) {
        // 尝试 append 到 env 的诊断日志缓冲区
        env->AppendDiagnosticLog(message + "\n");
        return;
    }
    
    // 【方法3】都失败了，输出到 stderr
    fprintf(stderr, "[DIAGNOSTIC-VM] %s\n", message.c_str());
    fflush(stderr);
}

// 获取ValueType的字符串表示
static const char* GetValueTypeName(ValueType type) {
    switch(type) {
        case ValueType::Null: return "Null";
        case ValueType::Bool: return "Bool";
        case ValueType::Int: return "Int";
        case ValueType::Double: return "Double";
        case ValueType::String: return "String";
        case ValueType::Dice: return "Dice";
        case ValueType::Schema: return "Schema";
        case ValueType::Array: return "Array";
        case ValueType::Function: return "Function";
        default: return "Unknown";
    }
}

VM::VM()
    : program_(nullptr), scope_(nullptr), ip_(0), 
      is_running_(false), has_error_(false) {
}

VM::~VM() {
}

bool VM::Execute(const BytecodeProgram* program, ScopeStack* scope) {
    // 【硬日志】入口 - 输出到 battleinfo
    if (g_current_round_manager != nullptr) {
        g_current_round_manager->AppendSkillTriggerLog("[DEBUG] [HARDLOG] Enter VM::Execute\n");
    }
    
    program_ = program;
    scope_ = scope;
    ip_ = 0;
    is_running_ = true;
    
    if (!program_) {
        Error("Program is null");
        return false;
    }
    
    // 【关键诊断】VM执行开始 - 记录是否能进入 VM 执行循环
    {
        char vm_start[256];
        snprintf(vm_start, sizeof(vm_start),
                "[DEBUG] [VM_EXECUTE_START] program_size=%u g_rm=%s",
                (unsigned int)program_->instructions.size(),
                g_current_round_manager != nullptr ? "valid" : "NULL");
        if (g_current_round_manager != nullptr) {
            g_current_round_manager->AppendSkillTriggerLog(vm_start);
        }
    }
    
    while (is_running_ && !has_error_ && ip_ < program_->instructions.size()) {
        const Instruction& instr = program_->instructions[ip_];
        
        if (!ExecuteInstruction(instr)) {
            return false;
        }
        
        ip_++;
    }
    return !has_error_;
}

bool VM::ExecuteInstruction(const Instruction& instr) {
    // 【诊断】记录每条指令的执行
    {
        const char* op_name = "UNKNOWN";
        switch(instr.opcode) {
            case Opcode::LOAD_INT: op_name = "LOAD_INT"; break;
            case Opcode::LOAD_DOUBLE: op_name = "LOAD_DOUBLE"; break;
            case Opcode::LOAD_BOOL: op_name = "LOAD_BOOL"; break;
            case Opcode::LOAD_STRING: op_name = "LOAD_STRING"; break;
            case Opcode::LOAD_NULL: op_name = "LOAD_NULL"; break;
            case Opcode::LOAD_VAR: op_name = "LOAD_VAR"; break;
            case Opcode::STORE_VAR: op_name = "STORE_VAR"; break;
            case Opcode::ADD: op_name = "ADD"; break;
            case Opcode::SUB: op_name = "SUB"; break;
            case Opcode::MUL: op_name = "MUL"; break;
            case Opcode::DIV: op_name = "DIV"; break;
            case Opcode::MOD: op_name = "MOD"; break;
            case Opcode::TABLE_ACCESS: op_name = "TABLE_ACCESS"; break;
            case Opcode::TABLE_SET: op_name = "TABLE_SET"; break;
            case Opcode::CALL: op_name = "CALL"; break;
            default: op_name = "OTHER"; break;
        }
        
        char instr_trace[512];
        snprintf(instr_trace, sizeof(instr_trace),
                "[VM_IP_%u] %s(arg='%s') stack=%u",
                (unsigned int)ip_, op_name, instr.arg_string.c_str(), 
                (unsigned int)value_stack_.size());
        LogDiagnosticToBattleInfo(instr_trace);
    }
    
    // 【强制诊断】针对所有LOAD_VAR和STORE_VAR，输出完整指令信息到battle info
    if (instr.opcode == Opcode::LOAD_VAR || instr.opcode == Opcode::STORE_VAR) {
        char trace[512];
        const char* op_name = (instr.opcode == Opcode::LOAD_VAR) ? "LOAD_VAR" : "STORE_VAR";
        snprintf(trace, sizeof(trace),
                "[📋 全指令追踪] IP:%u | %s | arg_string='%s' (len=%zu, empty=%d) | stack_depth=%u",
                (unsigned int)ip_, op_name, instr.arg_string.c_str(),
                instr.arg_string.length(),
                instr.arg_string.empty() ? 1 : 0,
                (unsigned int)value_stack_.size());
        LogDiagnosticToBattleInfo(trace);
    }
    
    // 对于关键指令，记录执行前的栈状态
    if ((instr.opcode == Opcode::LOAD_VAR || 
         instr.opcode == Opcode::STORE_VAR || 
         instr.opcode == Opcode::TABLE_ACCESS ||
         instr.opcode == Opcode::TABLE_SET) &&
        (instr.arg_string == "multiplier" || 
         instr.arg_string == "turn" ||
         instr.arg_string == "__tmp_result__" ||
         instr.arg_string == "__tmp_nested__" ||
         instr.arg_string.empty())) {  // 也记录空字符串的情况
        
        char pre_diag[512];
        const char* op_name = "UNKNOWN";
        switch(instr.opcode) {
            case Opcode::LOAD_VAR: op_name = "LOAD_VAR"; break;
            case Opcode::STORE_VAR: op_name = "STORE_VAR"; break;
            case Opcode::TABLE_ACCESS: op_name = "TABLE_ACCESS"; break;
            case Opcode::TABLE_SET: op_name = "TABLE_SET"; break;
            default: break;
        }
        
        snprintf(pre_diag, sizeof(pre_diag),
                "[指令执行前] IP:%u %s(%s) | 栈深:%u | IP_after:LOAD_VAR/STORE_VAR会改变",
                (unsigned int)ip_, op_name, instr.arg_string.c_str(), 
                (unsigned int)value_stack_.size());
        LogDiagnosticToBattleInfo(pre_diag);
    }
    
    // 诊断日志已注释
    // ExecutionEnvironment* env = ExecutionEnvironment::Current();
    // if (env) {
    //     char op_trace[256];
    //     snprintf(op_trace, sizeof(op_trace), "[OPCODE_EXEC] IP:%zu opcode=%d\n", ip_, (int)instr.opcode);
    //     env->AppendDiagnosticLog(op_trace);
    // }
    
    switch (instr.opcode) {
        case Opcode::LOAD_INT:
            HandleLoadInt(instr.arg_int);
            break;
        case Opcode::LOAD_DOUBLE:
            HandleLoadDouble(instr.arg_double);
            break;
        case Opcode::LOAD_BOOL:
            HandleLoadBool(instr.arg_bool);
            break;
        case Opcode::LOAD_STRING:
            HandleLoadString(instr.arg_string);
            break;
        case Opcode::LOAD_NULL:
            Push(Value(nullptr));
            break;
        case Opcode::LOAD_VAR:
            HandleLoadVar(instr.arg_string);
            break;
        case Opcode::STORE_VAR:
            HandleStoreVar(instr.arg_string);
            break;
        case Opcode::ADD:
            HandleAdd();
            break;
        case Opcode::SUB:
            HandleSub();
            break;
        case Opcode::MUL:
            HandleMul();
            break;
        case Opcode::DIV:
            HandleDiv();
            break;
        case Opcode::MOD:
            HandleMod();
            break;
        case Opcode::CMP_EQ: {
            Value right = Pop();
            Value left = Pop();
            Push(Value(left.ToInt() == right.ToInt()));
            break;
        }
        case Opcode::CMP_NE: {
            Value right = Pop();
            Value left = Pop();
            Push(Value(left.ToInt() != right.ToInt()));
            break;
        }
        case Opcode::CMP_LT: {
            Value right = Pop();
            Value left = Pop();
            Push(Value(left.ToInt() < right.ToInt()));
            break;
        }
        case Opcode::CMP_LE: {
            Value right = Pop();
            Value left = Pop();
            Push(Value(left.ToInt() <= right.ToInt()));
            break;
        }
        case Opcode::CMP_GT: {
            Value right = Pop();
            Value left = Pop();
            Push(Value(left.ToInt() > right.ToInt()));
            break;
        }
        case Opcode::CMP_GE: {
            Value right = Pop();
            Value left = Pop();
            Push(Value(left.ToInt() >= right.ToInt()));
            break;
        }
        case Opcode::AND: {
            Value right = Pop();
            Value left = Pop();
            Push(Value(left.ToBool() && right.ToBool()));
            break;
        }
        case Opcode::OR: {
            Value right = Pop();
            Value left = Pop();
            Push(Value(left.ToBool() || right.ToBool()));
            break;
        }
        case Opcode::NOT: {
            Value val = Pop();
            Push(Value(!val.ToBool()));
            break;
        }
        case Opcode::JMP:
            HandleJmp(instr.arg_addr);
            break;
        case Opcode::JMP_IF_FALSE:
            HandleJmpIfFalse(instr.arg_addr);
            break;
        case Opcode::JMP_IF_TRUE:
            HandleJmpIfTrue(instr.arg_addr);
            break;
        case Opcode::HALT:
            HandleHalt();
            break;
        case Opcode::RETURN:
            HandleReturn();
            break;
        
        // ✅ TABLE_ACCESS: 从 Schema 对象读取字段
        // ⭐ TABLE_ACCESS: 访问 Schema/Handle 字段 - 支持 Handle 系统
        // 如果栈上是 Handle，从 ObjectTable 读取字段
        // 如果是 Schema，直接读取字段
        case Opcode::TABLE_ACCESS: {
            Value obj_val = Pop();
            std::string key = instr.arg_string;
            
            // 【关键诊断】TABLE_ACCESS 执行开始
            if (g_current_round_manager != nullptr) {
                char access_start[256];
                snprintf(access_start, sizeof(access_start),
                        "[TABLE_ACCESS_ENTRY] IP:%u key='%s' type=%d handle=%d",
                        (unsigned int)ip_, key.c_str(), (int)obj_val.GetType(), obj_val.IsHandle() ? 1 : 0);
                g_current_round_manager->AppendSkillTriggerLog(access_start);
                g_current_round_manager->AppendSkillTriggerLog("\n");
            }
            
            Value field_value;
            
            // ⭐ Handle 模式：从 ObjectTable 读取
            // 🟥【Phase 2】设置 owner/path 用于 declare 系统
            if (obj_val.IsHandle()) {
                ObjectHandle handle = obj_val.GetHandle();
                ExecutionEnvironment* env = ExecutionEnvironment::Current();
                
                if (handle.IsValid()) {
                    try {
                        if (!env) {
                            throw std::runtime_error("ExecutionEnvironment::Current() is null");
                        }
                        ObjectTable* obj_table = env->GetObjectTable();
                        const SchemaValue& stored_schema = obj_table->Get(handle);
                        field_value = stored_schema.GetField(key);
                        
                        // 🟥【Phase 2 核心】设置 owner 信息
                        // 当从 handle 访问字段时，记录该字段属于这个 handle，且路径就是 key
                        field_value.SetOwnerHandle(handle);
                        field_value.SetOwnerPath(key);
                        
                        char buf[512];
                        snprintf(buf, sizeof(buf),
                                "[DECLARE][ACCESS] Handle(%llu).%s -> owner=%llu, path='%s', type=%d\n",
                                (unsigned long long)handle.GetID(), key.c_str(),
                                (unsigned long long)handle.GetID(), key.c_str(), (int)field_value.GetType());
                        if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(buf);
                    } catch (const std::exception& ex) {
                        char err_buf[512];
                        snprintf(err_buf, sizeof(err_buf),
                                "[TABLE_ACCESS-EXCEPTION-Handle] Handle(%llu).%s threw: %s\n",
                                (unsigned long long)handle.GetID(), key.c_str(), ex.what());
                        if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(err_buf);
                        Error("TABLE_ACCESS: Invalid handle " + std::to_string(handle.GetID()));
                        field_value = Value();
                    }
                } else {
                    Error("TABLE_ACCESS: Null handle");
                    field_value = Value();
                }
            } 
            // Schema 模式：直接访问（向后兼容）
            // 🟥【Phase 2】处理嵌套对象的 owner/path
            else if (obj_val.GetType() == ValueType::Schema) {
                // 【诊断】字段快照 - 列出 Schema 中的所有字段
                {
                    try {
                        const auto& all_fields = obj_val.GetAllFields();
                        std::string field_names;
                        size_t field_count = 0;
                        
                        for (const auto& pair : all_fields) {
                            if (!field_names.empty()) field_names += ", ";
                            field_names += pair.first;
                            field_count++;
                            if (field_count > 50) {
                                field_names += ", ...";
                                break;
                            }
                        }
                        
                        char snapshot[1024];
                        snprintf(snapshot, sizeof(snapshot),
                                "[TABLE_ACCESS-SCHEMA-SNAPSHOT] key='%s' | fields={%s}\n",
                                key.c_str(), field_names.c_str());
                        if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(snapshot);
                    } catch (const std::exception& ex) {
                        char err[256];
                        snprintf(err, sizeof(err), "[TABLE_ACCESS-SCHEMA-SNAPSHOT-ERROR] %s\n", ex.what());
                        if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(err);
                    }
                }
                
                // 【诊断】HasField 前置检查
                if (!obj_val.HasField(key)) {
                    char missing[512];
                    snprintf(missing, sizeof(missing),
                            "[TABLE_ACCESS-MISSING] key='%s' NOT FOUND\n",
                            key.c_str());
                    if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(missing);
                }
                
                // 执行 GetField - 有异常处理
                try {
                    field_value = obj_val.GetField(key);
                    
                    // 🟥【Phase 2 核心】处理嵌套对象的 owner/path
                    if (obj_val.HasOwner()) {
                        // 从父对象继承 owner_handle
                        ObjectHandle parent_owner = obj_val.GetOwnerHandle();
                        std::string parent_path = obj_val.GetOwnerPath();
                        std::string new_path = parent_path.empty() ? key : (parent_path + "." + key);
                        
                        field_value.SetOwnerHandle(parent_owner);
                        field_value.SetOwnerPath(new_path);
                        
                        char nested_log[512];
                        snprintf(nested_log, sizeof(nested_log),
                                "[DECLARE][NESTED] parent_path='%s' + key='%s' -> owner=%llu, path='%s', type=%d\n",
                                parent_path.c_str(), key.c_str(),
                                (unsigned long long)parent_owner.GetID(), new_path.c_str(), (int)field_value.GetType());
                        if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(nested_log);
                    }
                    
                    char success_buf[256];
                    snprintf(success_buf, sizeof(success_buf),
                            "[TABLE_ACCESS-SUCCESS] Schema.%s retrieved, type=%d, has_owner=%d\n",
                            key.c_str(), (int)field_value.GetType(), field_value.HasOwner() ? 1 : 0);
                    if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(success_buf);
                } catch (const std::exception& ex) {
                    char err_buf[512];
                    snprintf(err_buf, sizeof(err_buf),
                            "[TABLE_ACCESS-EXCEPTION-CAUGHT] Schema.%s threw: %s\n",
                            key.c_str(), ex.what());
                    if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(err_buf);
                    Error("TABLE_ACCESS: Schema field not found: " + key);
                    field_value = Value();
                }
            }
            else {
                char buf[256];
                snprintf(buf, sizeof(buf),
                        "[TABLE_ACCESS] ERROR: requires Handle or Schema, got type=%d\n",
                        (int)obj_val.GetType());
                if (g_current_round_manager) g_current_round_manager->AppendSkillTriggerLog(buf);
                Error("TABLE_ACCESS requires Handle or Schema, but got type: " + std::to_string((int)obj_val.GetType()));
            }
            
            Push(field_value);
            
            // 【执行后诊断】TABLE_ACCESS执行后栈的样子
            if (key == "turn" || key == "multiplier") {
                char post_diag[256];
                snprintf(post_diag, sizeof(post_diag),
                        "[TABLE_ACCESS执行后-%s] Push后栈深:%u | 推入值类型:%s | field_is_handle=%d",
                        key.c_str(), (unsigned int)value_stack_.size(),
                        GetValueTypeName(field_value.GetType()), field_value.IsHandle() ? 1 : 0);
                LogDiagnosticToBattleInfo(post_diag);
            }
            
            break;
        }
        
        // ⭐ TABLE_SET: 修改 Schema/Handle 字段 - 使用 Handle 系统直接修改
        // 如果栈上是 Handle，直接修改 ObjectTable 中的对象
        // 这是关键：修改持久化，不是拷贝！
        case Opcode::TABLE_SET: {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            std::string key = instr.arg_string;
            
            char set_start[256];
            snprintf(set_start, sizeof(set_start),
                    "[TABLE_SET_START] IP:%u key='%s' stack_depth=%u",
                    (unsigned int)ip_, key.c_str(), (unsigned int)value_stack_.size());
            LogDiagnosticToBattleInfo(set_start);

            // 【详细栈诊断】在Pop前打印栈中的所有元素
            if (key == "multiplier" || key == "turn") {
                int stack_size = value_stack_.size();
                char detailed_stack[2048];
                snprintf(detailed_stack, sizeof(detailed_stack),
                        "[TABLE_SET%s前-详细栈] 栈深度=%d | 期望Pop顺序：1st=value, 2nd=obj",
                        key.c_str(), stack_size);
                LogDiagnosticToBattleInfo(detailed_stack);
            }

            Value value = Pop();
            Value obj_val = Pop();

            // 【基础诊断】输出到battleinfo，确认TABLE_SET被执行 
            if (key == "multiplier" || key == "turn") {
                char diag_start[512];
                const char* obj_type_name = "Unknown";
                switch(obj_val.GetType()) {
                    case ValueType::Null: obj_type_name = "Null"; break;
                    case ValueType::Bool: obj_type_name = "Bool"; break;
                    case ValueType::Int: obj_type_name = "Int"; break;
                    case ValueType::Double: obj_type_name = "Double"; break;
                    case ValueType::String: obj_type_name = "String"; break;
                    case ValueType::Dice: obj_type_name = "Dice"; break;
                    case ValueType::Schema: obj_type_name = "Schema"; break;
                    case ValueType::Array: obj_type_name = "Array"; break;
                    case ValueType::Function: obj_type_name = "Function"; break;
                    default: obj_type_name = "Unknown"; break;
                }
                const char* val_type_name = "Unknown";
                switch(value.GetType()) {
                    case ValueType::Null: val_type_name = "Null"; break;
                    case ValueType::Bool: val_type_name = "Bool"; break;
                    case ValueType::Int: val_type_name = "Int"; break;
                    case ValueType::Double: val_type_name = "Double"; break;
                    case ValueType::String: val_type_name = "String"; break;
                    case ValueType::Dice: val_type_name = "Dice"; break;
                    case ValueType::Schema: val_type_name = "Schema"; break;
                    case ValueType::Array: val_type_name = "Array"; break;
                    case ValueType::Function: val_type_name = "Function"; break;
                    default: val_type_name = "Unknown"; break;
                }
                snprintf(diag_start, sizeof(diag_start),
                        "[TABLE_SET诊断] %s | obj=%s(%d) handle=%d value=%s(%d)",
                        key.c_str(), obj_type_name, (int)obj_val.GetType(), obj_val.IsHandle() ? 1 : 0,
                        val_type_name, (int)value.GetType());
                LogDiagnosticToBattleInfo(diag_start);
            }

            if (obj_val.IsHandle()) {
                ObjectHandle handle = obj_val.GetHandle();
                char handle_diag[256];
                snprintf(handle_diag, sizeof(handle_diag),
                        "[TABLE_SET] HANDLE BRANCH: key=%s obj_handle=%llu value_type=%d\n",
                        key.c_str(), (unsigned long long)handle.GetID(), (int)value.GetType());
                LogDiagnosticToBattleInfo(handle_diag);
                if (handle.IsValid()) {
                    try {
                        ObjectTable* obj_table = env->GetObjectTable();
                        SchemaValue& real = obj_table->Get(handle);
                        
                        // 🟥【Phase 3 核心】检查 value 是否有 owner/path 信息
                        // 如果有，说明这是一个来自嵌套访问的字段，需要用 owner/path 写回
                        if (value.HasOwner()) {
                            // 🔍 嵌套写回路径
                            ObjectHandle owner = value.GetOwnerHandle();
                            std::string owner_path = value.GetOwnerPath();
                            
                            char nested_set_log[512];
                            snprintf(nested_set_log, sizeof(nested_set_log),
                                    "[DECLARE][SET_NESTED] handle=%llu, owner_path='%s' + key='%s' -> full_path='%s.%s'\n",
                                    (unsigned long long)owner.GetID(),
                                    owner_path.c_str(), key.c_str(),
                                    owner_path.c_str(), key.c_str());
                            LogDiagnosticToBattleInfo(nested_set_log);
                            
                            // 从 ObjectTable 获取 owner 对象
                            SchemaValue& owner_obj = obj_table->Get(owner);
                            
                            // 按路径分割并逐层访问/创建对象
                            std::string full_path = owner_path + "." + key;
                            std::vector<std::string> path_parts;
                            size_t start = 0;
                            size_t end = full_path.find('.');
                            while (end != std::string::npos) {
                                path_parts.push_back(full_path.substr(start, end - start));
                                start = end + 1;
                                end = full_path.find('.', start);
                            }
                            path_parts.push_back(full_path.substr(start)); // 最后一部分
                            
                            // 逐层遍历路径，最后一层设置值
                            SchemaValue* current = &owner_obj;
                            for (size_t i = 0; i < path_parts.size(); ++i) {
                                const std::string& part = path_parts[i];
                                if (i == path_parts.size() - 1) {
                                    // 最后一层：设置新值
                                    current->SetField(part, value);
                                    
                                    char set_leaf_log[512];
                                    snprintf(set_leaf_log, sizeof(set_leaf_log),
                                            "[DECLARE][SET_LEAF] path='%s.%s' -> value_type=%d\n",
                                            owner_path.c_str(), key.c_str(), (int)value.GetType());
                                    LogDiagnosticToBattleInfo(set_leaf_log);
                                } else {
                                    // 中间层：获取或创建对象
                                    Value intermediate = current->GetField(part);
                                    if (!intermediate.IsSchema()) {
                                        // 如果中间层不是 Schema，创建一个新的 Schema
                                        intermediate = Value::CreateSchema();
                                    }
                                    // 获取中间对象的可变引用继续遍历
                                    current = intermediate.GetSchemaValuePtr();
                                    if (!current) {
                                        throw std::runtime_error("Failed to get schema pointer for path: " + part);
                                    }
                                }
                            }
                        } else {
                            // 普通的顶级字段设置
                            real.SetField(key, value);
                            
                            char simple_set_log[512];
                            snprintf(simple_set_log, sizeof(simple_set_log),
                                    "[DECLARE][SET_SIMPLE] handle=%llu, key='%s' -> value_type=%d\n",
                                    (unsigned long long)handle.GetID(), key.c_str(), (int)value.GetType());
                            LogDiagnosticToBattleInfo(simple_set_log);
                        }
                        
                        Push(obj_val);
                    } catch (const std::exception& ex) {
                        char buf[256];
                        snprintf(buf, sizeof(buf),
                                "[TABLE_SET-ERROR] Handle(%llu).%s: %s",
                                (unsigned long long)handle.GetID(), key.c_str(), ex.what());
                        LogDiagnosticToBattleInfo(buf);
                        Error("TABLE_SET: Invalid handle " + std::to_string(handle.GetID()));
                    }
                } else {
                    char buf[256];
                    snprintf(buf, sizeof(buf),
                            "[TABLE_SET] ERROR: Null handle for key=%s\n", key.c_str());
                    LogDiagnosticToBattleInfo(buf);
                    Error("TABLE_SET: Null handle");
                }
            } else if (obj_val.GetType() == ValueType::Schema) {
                // 🟥【Phase 3】Schema 模式也需要处理 owner/path
                if (value.HasOwner()) {
                    // 这是一个带 owner 信息的值，需要通过 owner/path 写回 ObjectTable
                    ObjectHandle owner = value.GetOwnerHandle();
                    std::string owner_path = value.GetOwnerPath();
                    std::string full_path = owner_path + "." + key;
                    
                    try {
                        ObjectTable* obj_table = env->GetObjectTable();
                        SchemaValue& owner_obj = obj_table->Get(owner);
                        
                        // 按路径分割并逐层访问/创建对象
                        std::vector<std::string> path_parts;
                        size_t start = 0;
                        size_t end = full_path.find('.');
                        while (end != std::string::npos) {
                            path_parts.push_back(full_path.substr(start, end - start));
                            start = end + 1;
                            end = full_path.find('.', start);
                        }
                        path_parts.push_back(full_path.substr(start));
                        
                        SchemaValue* current = &owner_obj;
                        for (size_t i = 0; i < path_parts.size(); ++i) {
                            const std::string& part = path_parts[i];
                            if (i == path_parts.size() - 1) {
                                // 最后一层：设置新值
                                current->SetField(part, value);
                                
                                char set_via_owner_log[512];
                                snprintf(set_via_owner_log, sizeof(set_via_owner_log),
                                        "[DECLARE][SET_VIA_OWNER] path='%s' -> value_type=%d\n",
                                        full_path.c_str(), (int)value.GetType());
                                LogDiagnosticToBattleInfo(set_via_owner_log);
                            } else {
                                Value intermediate = current->GetField(part);
                                if (!intermediate.IsSchema()) {
                                    intermediate = Value::CreateSchema();
                                }
                                current = intermediate.GetSchemaValuePtr();
                                if (!current) {
                                    throw std::runtime_error("Failed to get schema pointer");
                                }
                            }
                        }
                    } catch (const std::exception& ex) {
                        char err[512];
                        snprintf(err, sizeof(err),
                                "[DECLARE][SET_VIA_OWNER-ERROR] path='%s': %s\n",
                                full_path.c_str(), ex.what());
                        LogDiagnosticToBattleInfo(err);
                        // 回退到普通 schema 设置
                        obj_val.SetField(key, value);
                    }
                } else {
                    // 普通的 schema 字段设置（无 owner 信息）
                    obj_val.SetField(key, value);
                    
                    char simple_schema_set[256];
                    snprintf(simple_schema_set, sizeof(simple_schema_set),
                            "[TABLE_SET-SIMPLE-SCHEMA] key='%s' value_type=%d\n",
                            key.c_str(), (int)value.GetType());
                    LogDiagnosticToBattleInfo(simple_schema_set);
                }
                
                // 【诊断】验证SetField是否成功
                Value verify_val = obj_val.GetField(key);
                double verify_double = -999.0;
                if (verify_val.IsDouble()) {
                    verify_double = verify_val.GetDouble();
                } else if (verify_val.IsInt()) {
                    verify_double = (double)verify_val.GetInt();
                }
                char verify_buf[256];
                snprintf(verify_buf, sizeof(verify_buf),
                        "[TABLE_SET-Schema诊断] %s: SET=%.6f -> VERIFY=%.6f",
                        key.c_str(),
                        value.IsDouble() ? value.GetDouble() : (value.IsInt() ? (double)value.GetInt() : -999.0),
                        verify_double);
                LogDiagnosticToBattleInfo(verify_buf);
                // 【特别诊断】如果修改的是turn字段，打印其内部的multiplier值
                if (key == "turn" && value.IsSchema()) {
                    Value mult_in_modified_turn = value.GetField("multiplier");
                    char turn_internal_diag[256];
                    snprintf(turn_internal_diag, sizeof(turn_internal_diag),
                            "[TABLE_SET(turn)后检查] 新turn中的multiplier = %.6f",
                            mult_in_modified_turn.IsDouble() ? mult_in_modified_turn.GetDouble() :
                            (mult_in_modified_turn.IsInt() ? (double)mult_in_modified_turn.GetInt() : -999.0));
                    LogDiagnosticToBattleInfo(turn_internal_diag);
                }
                // 【关键修复】如果修改的是self或self的子字段，必须同步到scope
                if (scope_) {
                    Value current_self = scope_->GetVariable("self");
                    if (current_self.IsSchema() && obj_val.IsSchema()) {
                        scope_->SetVariable("self", obj_val);
                        if (key == "multiplier" || key == "turn") {
                            Value after_set = obj_val.GetField(key);
                            double final_val = after_set.IsDouble() ? after_set.GetDouble() : 
                                              (after_set.IsInt() ? (double)after_set.GetInt() : -999.0);
                            char diag_msg[256];
                            snprintf(diag_msg, sizeof(diag_msg),
                                    "[Table_SET同步] %s: 已同步到Scope, 最终值=%.6f",
                                    key.c_str(), final_val);
                            LogDiagnosticToBattleInfo(diag_msg);
                        }
                    }
                }
                Push(obj_val);
            } else {
                char buf[256];
                snprintf(buf, sizeof(buf),
                        "[TABLE_SET] ERROR: requires Handle or Schema, got type=%d\n",
                        (int)obj_val.GetType());
                env->AppendDiagnosticLog(buf);
                Error("TABLE_SET requires Handle or Schema, but got type: " + std::to_string((int)obj_val.GetType()));
            }
            break;
        }
        
        // ★【新指令】TABLE_SET_SELF: 修改self的字段 - **纯 handle 模式**
        case Opcode::TABLE_SET_SELF: {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            std::string key = instr.arg_string;
            
            if (value_stack_.size() < 2) {
                Error("TABLE_SET_SELF: stack underflow");
                return false;
            }
            
            // 弹栈：值，然后是 self
            Value value = Pop();
            Value self_handle = Pop();
            
            // 🟥【任务6】self **必须是纯 handle**（IsHandle=1 && IsSchema=0）
            if (!self_handle.IsHandle() || self_handle.IsSchema()) {
                char err_msg[256];
                snprintf(err_msg, sizeof(err_msg),
                    "[DEBUG] TABLE_SET_SELF: self must be pure handle (IsHandle=%d IsSchema=%d)",
                    self_handle.IsHandle() ? 1 : 0,
                    self_handle.IsSchema() ? 1 : 0);
                LogDiagnosticToBattleInfo(err_msg);
                Error(err_msg);
                return false;
            }
            
            // ✔ self 是纯 handle，直接修改 ObjectTable
            ObjectHandle h = self_handle.GetHandle();
            if (!h.IsValid()) {
                Error("TABLE_SET_SELF: Invalid handle");
                return false;
            }
            
            try {
                ObjectTable* obj_table = env->GetObjectTable();
                if (!obj_table) {
                    Error("TABLE_SET_SELF: No ObjectTable");
                    return false;
                }
                
                // 从 ObjectTable 获取真实的 self 对象
                SchemaValue& real_self = obj_table->Get(h);
                
                // 直接修改真实对象
                real_self.SetField(key, value);
                
                if (g_current_round_manager) {
                    char buf[256];
                    snprintf(buf, sizeof(buf),
                        "[TABLE_SET_SELF] Modified ObjectTable[%llu].%s successfully",
                        h.GetID(), key.c_str());
                    g_current_round_manager->AppendSkillTriggerLog(buf);
                }
                
                // 推回纯 handle（不是 schema）
                Push(self_handle);
                break;
                
            } catch (const std::exception& ex) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[DEBUG] TABLE_SET_SELF: Exception at ObjectTable[%llu].%s: %s",
                    h.GetID(), key.c_str(), ex.what());
                LogDiagnosticToBattleInfo(buf);
                Error(buf);
                return false;
            }
        }
        
        // ✅ CALL: 函数调用
        case Opcode::CALL: {
            std::string func_name = instr.arg_string;
            
            fprintf(stderr, "[!!!VM!!!] 调用函数: %s\n", func_name.c_str());
            fflush(stderr);
            
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            
            if (!env) {
                fprintf(stderr, "[!!!VM!!!] 错误: 没有执行环境\n");
                fflush(stderr);
                Error("CALL: No execution environment available");
                return false;
            }
            
            // 从预设注册表查找函数
            PresetRegistry* registry = PresetRegistry::GetInstance();
            PresetBase* preset = registry->GetPreset(PresetType::FUNCTION, func_name);
            
            if (!preset) {
                fprintf(stderr, "[!!!VM!!!] 错误: 找不到函数 '%s'\n", func_name.c_str());
                fflush(stderr);
                Error("Function not found: " + func_name);
                return false;
            }
            
            // 执行函数并获取返回值
            int result = preset->Execute(env);
            
            fprintf(stderr, "[!!!VM!!!] 函数 '%s' 返回值: %d\n", func_name.c_str(), result);
            fflush(stderr);
            
            // 将返回值（0表示成功）推入栈作为函数结果
            Push(Value(static_cast<int64_t>(result)));
            break;
        }
        
        // ✅ DICE_ROLL: 骰子投掷 (格式: d6, d20, 2d6+3 等)
        case Opcode::DICE_ROLL: {
            std::string dice_expr = instr.arg_string;
            // 这里需要解析骰子表达式并生成随机数
            // 目前为简单实现：假设已经是 int/double 结果
            // 完整实现需要 DiceLexer 和 DiceParser
            Error("DICE_ROLL not fully implemented yet");
            return false;
        }
        
        // ✅ LOAD_PARA: 加载技能参数
        case Opcode::LOAD_PARA: {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            if (!env) {
                Error("LOAD_PARA: No execution environment available");
                return false;
            }
            auto para = env->GetPara();
            if (!para) {
                // Para未设置时返回空Schema对象
                Push(Value::CreateSchema());
            } else {
                Push(*para);
            }
            break;
        }
        
        // ✅ LOAD_MESSAGE: 加载触发消息
        case Opcode::LOAD_MESSAGE: {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            if (!env) {
                Error("LOAD_MESSAGE: No execution environment available");
                return false;
            }
            auto message = env->GetMessage();
            if (!message) {
                // Message未设置时返回空Schema对象
                Push(Value::CreateSchema());
            } else {
                Push(*message);
            }
            break;
        }
        
        // ⭐ LOAD_SELF: 加载作用者(Self) - 使用 Handle 系统
        // 返回包含 ObjectHandle 的 Value，而不是 Schema 拷贝
        // 这样脚本对 self 的修改直接作用于 ObjectTable 中的对象
        case Opcode::LOAD_SELF: {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            if (!env) {
                Error("LOAD_SELF: No execution environment available");
                return false;
            }
            
            // 获取 self 对应的 Handle
            uintptr_t handle_ptr = reinterpret_cast<uintptr_t>(env->GetPointerProperty("self_handle_id", nullptr));
            
            Value result_value;
            Value self_schema = env->GetValueProperty("self");
            
            // 【强制诊断】LOAD_SELF到battle info
            {
                char trace[1024];
                const char* type_name = "Unknown";
                switch(self_schema.GetType()) {
                    case ValueType::Null: type_name = "Null"; break;
                    case ValueType::Schema: type_name = "Schema"; break;
                    default: type_name = "Other"; break;
                }
                snprintf(trace, sizeof(trace),
                        "[📋 LOAD_SELF] IP:%u | self_type=%s | handle_ptr=%p | stack_before=%u",
                        (unsigned int)ip_,
                        type_name,
                        (void*)handle_ptr,
                        (unsigned int)value_stack_.size());
                LogDiagnosticToBattleInfo(trace);
            }
            
            // 诊断：打印 LOAD_SELF 加载的 self 信息
            {
                char trace[1024];
                snprintf(trace, sizeof(trace),
                        "[LOAD_SELF] IP:%I64u - LoadingType=%d, IsSchema=%d\n",
                        (unsigned long long)ip_, (int)self_schema.GetType(), self_schema.IsSchema() ? 1 : 0);
                env->AppendDiagnosticLog(trace);
                
                if (self_schema.IsSchema()) {
                    Value turn_val = self_schema.GetField("turn");
                    if (turn_val.IsSchema()) {
                        Value mult_val = turn_val.GetField("multiplier");
                        snprintf(trace, sizeof(trace),
                                "[LOAD_SELF] turn.multiplier initial = %.6f\n",
                                mult_val.GetDouble());
                        env->AppendDiagnosticLog(trace);
                    }
                }
            }
            
            if (handle_ptr != 0) {
                // ✅ Handle 模式（必须使用以实现同步）
                ObjectHandle handle(static_cast<uint64_t>(handle_ptr));
                
                // 【诊断-关键】SetHandle前的状态
                {
                    char trace[512];
                    snprintf(trace, sizeof(trace),
                            "[LOAD_SELF-SetHandle-BEFORE] handle_ptr=%p | result_value type BEFORE=%d",
                            (void*)handle_ptr, (int)result_value.GetType());
                    LogDiagnosticToBattleInfo(trace);
                }
                
                result_value.SetHandle(handle);
                
                // 【诊断-关键】SetHandle后的状态
                {
                    char trace[512];
                    const char* type_after = "Unknown";
                    switch(result_value.GetType()) {
                        case ValueType::Null: type_after = "Null"; break;
                        case ValueType::Schema: type_after = "Schema"; break;
                        default: type_after = "Other"; break;
                    }
                    snprintf(trace, sizeof(trace),
                            "[LOAD_SELF-SetHandle-AFTER] result_value type AFTER=%s | THIS IS WRONG IF NULL!",
                            type_after);
                    LogDiagnosticToBattleInfo(trace);
                }
            } else {
                // 直接使用 Schema 模式
                result_value = self_schema;
            }
            
            Push(result_value);
            
            // 【强制诊断】Push后的栈状态
            {
                char trace[512];
                const char* pushed_type = "Unknown";
                switch(result_value.GetType()) {
                    case ValueType::Null: pushed_type = "Null"; break;
                    case ValueType::Schema: pushed_type = "Schema"; break;
                    default: pushed_type = "Other"; break;
                }
                snprintf(trace, sizeof(trace),
                        "[📋 LOAD_SELF_AFTER] IP:%u | pushed=%s | stack_after=%u",
                        (unsigned int)ip_,
                        pushed_type,
                        (unsigned int)value_stack_.size());
                LogDiagnosticToBattleInfo(trace);
            }
            break;
        }
        
        // ✅ LOAD_ENEMY: 加载目标(Enemy)
        case Opcode::LOAD_ENEMY: {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            if (!env) {
                Error("LOAD_ENEMY: No execution environment available");
                return false;
            }
            Character* target = env->GetTarget();
            if (!target) {
                // 无目标时返回空Schema
                Push(Value::CreateSchema());
            } else {
                // 自动注册目标属性到环境，使得Enemy.hp等可以访问
                // 注意：这会覆盖Self的属性，所以在实际使用中需要小心
                // 更好的方法是创建单独的Enemy命名空间，但这里先简单实现
                
                // 创建表示目标的Schema对象
                Value schema = Value::CreateSchema();
                schema.SetField("__ptr__", Value(static_cast<int64_t>(reinterpret_cast<uintptr_t>(target))));
                Push(schema);
            }
            break;
        }
        
        // ✅ LOAD_ALLIES: 加载同方角色(Allies)
        case Opcode::LOAD_ALLIES: {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            if (!env) {
                Error("LOAD_ALLIES: No execution environment available");
                return false;
            }
            Battle* battle = env->GetBattle();
            if (!battle) {
                // 无战斗时返回空Schema
                Push(Value::CreateSchema());
            } else {
                // TODO: 从Battle获取同方角色列表
                // 简单实现：返回空Schema，待完整实现
                Push(Value::CreateSchema());
            }
            break;
        }
        
        // 【修复v3】SELF_COMMIT: 从scope读取最终的self schema并写回env
        case Opcode::SELF_COMMIT: {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            
            // ★ 关键设计：从栈顶拿修改后的 self schema
            // 【理由】
            // - TABLE_SET 不再写 scope，所有修改只在栈上进行
            // - 执行完所有修改指令后，栈顶就是最终的 self schema
            // - SELF_COMMIT 负责把这个最终版本写回 env/scope
            
            Value self_schema = Pop();
            
            if (self_schema.GetType() == ValueType::Schema) {
                // 把栈顶的修改后 schema 写回 env
                if (env) {
                    env->SetValueProperty("self", self_schema);
                    {
                        Value env_self = env->GetValueProperty("self");
                        bool is_handle = env_self.IsHandle();
                        bool is_schema = env_self.IsSchema();
                        uint64_t handle_id = is_handle ? env_self.GetHandle().GetID() : 0;
                        double env_mult = -999.0;
                        if (env_self.IsSchema()) {
                            Value turn_field = env_self.GetField("turn");
                            if (turn_field.IsSchema()) {
                                Value mult_field = turn_field.GetField("multiplier");
                                if (mult_field.IsDouble()) env_mult = mult_field.GetDouble();
                                else if (mult_field.IsInt()) env_mult = (double)mult_field.GetInt();
                            }
                        }
                        char diag_set[256];
                        snprintf(diag_set, sizeof(diag_set),
                                 "[SELF_COMMIT] env.self after commit IsHandle=%d handle_id=%llu IsSchema=%d turn.multiplier=%.6f",
                                 is_handle ? 1 : 0,
                                 (unsigned long long)handle_id,
                                 is_schema ? 1 : 0,
                                 env_mult);
                        env->AppendDiagnosticLog(diag_set);
                        fprintf(stderr, "%s\n", diag_set);
                    }
                    
                    // 同步到 scope（便于容器后续访问）
                    if (scope_) {
                        scope_->SetVariable("self", self_schema);
                    }
                    
                    // 诊断日志
                    char trace[256];
                    snprintf(trace, sizeof(trace),
                            "[SELF_COMMIT] ✅ Popped modified self from stack and synced to ExecutionEnvironment\n");
                    env->AppendDiagnosticLog(trace);
                }
            } else {
                Error("SELF_COMMIT expects Schema on stack, but got type: " + std::to_string((int)self_schema.GetType()));
                return false;
            }
            
            break;
        }
        
        default:
            Error("Unknown opcode");
            return false;
    }
    
    return true;
}

Value VM::Pop() {
    if (value_stack_.empty()) {
        Error("Stack underflow");
        return Value();
    }
    Value v = value_stack_.top();
    value_stack_.pop();
    return v;
}

void VM::Push(const Value& value) {
    value_stack_.push(value);
}

Value VM::Peek() const {
    if (value_stack_.empty()) {
        return Value();
    }
    return value_stack_.top();
}

void VM::Error(const std::string& message) {
    has_error_ = true;
    error_message_ = message + " at ip:" + std::to_string(ip_);
}

void VM::Halt() {
    is_running_ = false;
}

void VM::HandleLoadInt(int64_t value) {
    Push(Value(value));
}

void VM::HandleLoadDouble(double value) {
    Push(Value(value));
}

void VM::HandleLoadBool(bool value) {
    Push(Value(value));
}

void VM::HandleLoadString(const std::string& value) {
    Push(Value(value));
}

void VM::HandleLoadVar(const std::string& name) {
    // 【诊断】参数检查
    fprintf(stderr, "[VM::HandleLoadVar] name_size=%zu name_empty=%d name='%s'\n",
            name.size(), name.empty() ? 1 : 0, name.c_str());
    fflush(stderr);
    
    Value value = scope_->GetVariable(name);
    
    // 🔥 关键诊断：如果是 __tmp_modified_obj__，打印其内部的 turn.multiplier
    if (name == "__tmp_modified_obj__" && value.IsSchema()) {
        try {
            Value turn_field = value.GetField("turn");
            if (turn_field.IsSchema()) {
                Value mult_field = turn_field.GetField("multiplier");
                double mult_val = -999.0;
                if (mult_field.IsDouble()) {
                    mult_val = mult_field.GetDouble();
                } else if (mult_field.IsInt()) {
                    mult_val = (double)mult_field.GetInt();
                }
                char diag_critical[256];
                snprintf(diag_critical, sizeof(diag_critical),
                    "[🔥诊断-LOAD_IP:14] __tmp_modified_obj__.turn.multiplier = %.6f (期望2.0)",
                    mult_val);
                LogDiagnosticToBattleInfo(diag_critical);
            }
        } catch (...) {
            LogDiagnosticToBattleInfo("[诊断-LOAD_CRITICAL] 无法读取turn.multiplier");
        }
    }

    // 🔥 关键诊断：记录 handle 信息
    if (value.IsHandle()) {
        char handle_diag[256];
        snprintf(handle_diag, sizeof(handle_diag),
                "[DEBUG] var='%s' handle=%llu (IsHandle=true) stack_before=%u",
                name.c_str(), (unsigned long long)value.GetHandle().GetID(), (unsigned int)value_stack_.size());
        LogDiagnosticToBattleInfo(handle_diag);
    }

    // 所有变量都输出到battleinfo
    char diag[512];
    const char* type_name = "Unknown";
    double dval = 0.0;
    
    switch(value.GetType()) {
        case ValueType::Null: type_name = "Null"; break;
        case ValueType::Bool: type_name = "Bool"; break;
        case ValueType::Int: type_name = "Int"; dval = static_cast<double>(value.GetInt()); break;
        case ValueType::Double: type_name = "Double"; dval = value.GetDouble(); break;
        case ValueType::String: type_name = "String"; break;
        case ValueType::Dice: type_name = "Dice"; break;
        case ValueType::Schema: type_name = "Schema"; break;
        case ValueType::Array: type_name = "Array"; break;
        case ValueType::Function: type_name = "Function"; break;
        default: type_name = "Unknown"; break;
    }
    snprintf(diag, sizeof(diag), "[DEBUG] var='%s' (len=%zu) type=%s val=%.1f stack_before=%u",
             name.c_str(), name.length(), type_name, dval, (unsigned int)value_stack_.size());
    LogDiagnosticToBattleInfo(diag);
    
    Push(value);
}

void VM::HandleStoreVar(const std::string& name) {
    // 【诊断】参数检查
    fprintf(stderr, "[VM::HandleStoreVar] name_size=%zu name_empty=%d name='%s'\n",
            name.size(), name.empty() ? 1 : 0, name.c_str());
    fflush(stderr);
    
    Value value = Pop();
    
    // 🔥 关键诊断：如果是 __tmp_modified_obj__，打印其内部的 turn.multiplier
    if (name == "__tmp_modified_obj__" && value.IsSchema()) {
        try {
            Value turn_field = value.GetField("turn");
            if (turn_field.IsSchema()) {
                Value mult_field = turn_field.GetField("multiplier");
                double mult_val = -999.0;
                if (mult_field.IsDouble()) {
                    mult_val = mult_field.GetDouble();
                } else if (mult_field.IsInt()) {
                    mult_val = (double)mult_field.GetInt();
                }
                char diag_critical[256];
                snprintf(diag_critical, sizeof(diag_critical),
                    "[🔥诊断-STORE_IP:12] __tmp_modified_obj__.turn.multiplier = %.6f (期望2.0)",
                    mult_val);
                LogDiagnosticToBattleInfo(diag_critical);
            }
        } catch (...) {
            LogDiagnosticToBattleInfo("[诊断-STORE_CRITICAL] 无法读取turn.multiplier");
        }
    }

    // 🔥 关键诊断：记录 handle 信息
    if (value.IsHandle()) {
        char handle_diag[256];
        snprintf(handle_diag, sizeof(handle_diag),
                "[STORE_VAR] var='%s' handle=%llu (IsHandle=true) stack_after=%u",
                name.c_str(), (unsigned long long)value.GetHandle().GetID(), (unsigned int)value_stack_.size());
        LogDiagnosticToBattleInfo(handle_diag);
    }

    // 所有变量都输出到battleinfo
    char diag[512];
    const char* type_name = "Unknown";
    double dval = 0.0;
    
    switch(value.GetType()) {
        case ValueType::Null: type_name = "Null"; break;
        case ValueType::Bool: type_name = "Bool"; break;
        case ValueType::Int: type_name = "Int"; dval = static_cast<double>(value.GetInt()); break;
        case ValueType::Double: type_name = "Double"; dval = value.GetDouble(); break;
        case ValueType::String: type_name = "String"; break;
        case ValueType::Dice: type_name = "Dice"; break;
        case ValueType::Schema: type_name = "Schema"; break;
        case ValueType::Array: type_name = "Array"; break;
        case ValueType::Function: type_name = "Function"; break;
        default: type_name = "Unknown"; break;
    }
    snprintf(diag, sizeof(diag), "[STORE_VAR] var='%s' (len=%zu) type=%s val=%.1f stack_after=%u",
             name.c_str(), name.length(), type_name, dval, (unsigned int)value_stack_.size());
    LogDiagnosticToBattleInfo(diag);
    
    scope_->SetVariable(name, value);
    
    // 特别处理 __argc__ 和 __arg* 参数变量
    // 这些需要同时存储到ExecutionEnvironment，以便builtin函数可以检索它们
    if (name == "__argc__" || (name.size() > 5 && name.substr(0, 5) == "__arg")) {
        ExecutionEnvironment* env = ExecutionEnvironment::Current();
        if (env) {
            if (name == "__argc__") {
                int argc_val = static_cast<int>(value.ToInt());
                env->SetIntProperty(name, argc_val);
            } else {
                env->SetValueProperty(name, value);
            }
        }
    }
}

void VM::HandleAdd() {
    Value right = Pop();
    Value left = Pop();
    Value result = left + right;
    // 诊断日志已注释
    // fprintf(stderr, "[VM TRACE] ADD: %lld + %lld = %lld\n", left.ToInt(), right.ToInt(), result.ToInt());
    Push(result);
}

void VM::HandleSub() {
    Value right = Pop();
    Value left = Pop();
    Value result = left - right;
    // 诊断日志已注释
    // fprintf(stderr, "[VM TRACE] SUB: %lld - %lld = %lld\n", left.ToInt(), right.ToInt(), result.ToInt());
    Push(result);
}

void VM::HandleMul() {
    Value right = Pop();
    Value left = Pop();
    Value result = left * right;
    
    // 输出详细的MUL操作日志到battleinfo
    char diag[512];
    const char* left_type = "Unknown";
    const char* right_type = "Unknown";
    const char* result_type = "Unknown";
    
    auto get_type_name = [](ValueType t) -> const char* {
        switch(t) {
            case ValueType::Null: return "Null";
            case ValueType::Bool: return "Bool";
            case ValueType::Int: return "Int";
            case ValueType::Double: return "Double";
            case ValueType::String: return "String";
            case ValueType::Dice: return "Dice";
            case ValueType::Schema: return "Schema";
            case ValueType::Array: return "Array";
            case ValueType::Function: return "Function";
            default: return "Unknown";
        }
    };
    
    left_type = get_type_name(left.GetType());
    right_type = get_type_name(right.GetType());
    result_type = get_type_name(result.GetType());
    
    snprintf(diag, sizeof(diag), 
             "[DEBUG] left:%s(%.6f) * right:%s(%.6f) = result:%s(%.6f)",
             left_type, left.ToDouble(), 
             right_type, right.ToDouble(),
             result_type, result.ToDouble());
    LogDiagnosticToBattleInfo(diag);
    
    fprintf(stderr, "[VM TRACE] MUL: %lld * %lld = %lld\n", left.ToInt(), right.ToInt(), result.ToInt());
    Push(result);
    
    // 【执行后诊断】MUL执行后的栈深
    char post_diag[256];
    snprintf(post_diag, sizeof(post_diag),
            "[DEBUG] Push后栈深:%u",
            (unsigned int)value_stack_.size());
    LogDiagnosticToBattleInfo(post_diag);
}

void VM::HandleDiv() {
    Value right = Pop();
    Value left = Pop();
    if (right.ToDouble() == 0) {
        Error("Division by zero");
        return;
    }
    Value result = left / right;
    fprintf(stderr, "[VM TRACE] DIV: %lld / %lld = %lld\n", left.ToInt(), right.ToInt(), result.ToInt());
    Push(result);
}

void VM::HandleMod() {
    Value right = Pop();
    Value left = Pop();
    if (right.ToInt() == 0) {
        Error("Modulo by zero");
        return;
    }
    int64_t mod_result = left.ToInt() % right.ToInt();
    fprintf(stderr, "[VM TRACE] MOD: %lld %% %lld = %lld\n", left.ToInt(), right.ToInt(), mod_result);
    Push(Value(mod_result));
}

void VM::HandleJmp(uint32_t address) {
    ip_ = address - 1;  // -1因为循环会自动ip++
}

void VM::HandleJmpIfFalse(uint32_t address) {
    Value condition = Pop();
    if (!condition.ToBool()) {
        ip_ = address - 1;  // -1因为循环会自动ip++
    }
}

void VM::HandleJmpIfTrue(uint32_t address) {
    Value condition = Pop();
    if (condition.ToBool()) {
        ip_ = address - 1;  // -1因为循环会自动ip++
    }
}

void VM::HandleHalt() {
    halted_ = true;
}

void VM::HandleReturn() {
    // 简化实现：停止执行
    // 在完整实现中，应该从调用栈中弹出，恢复到调用者
    halted_ = true;
}

}  // namespace abot
