/**
 * @file ExecutionEnvironment.cpp
 * @brief 执行环境实现 - 线程本地上下文管 
 */

#include "ExecutionEnvironment.h"
#include "Character.h"
#include "Value.h"
#include "SchemaValue.h"
#include "ArrayValue.h"
#include "ObjectTable.h"
#include "SkillPreset.h"
#include "StatePreset.h"
#include "AnkePreset.h"
#include "RoundManager.h"
#include <cstdio>
#include <sstream>

extern abot::RoundManager* g_current_round_manager;

static void AppendBattleInfo(const std::string& message) {
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog(message + "\n");
    }
}

static void LogSelfValueState(const std::string& source, const abot::Value& self) {
    bool is_handle = self.IsHandle();
    bool is_schema = self.IsSchema();
    uint64_t handle_id = is_handle ? self.GetHandle().GetID() : 0;
    double multiplier = -999.0;
    std::string name = "<missing>";
    int64_t camp = INT64_MIN;
    int64_t hp = INT64_MIN;
    int64_t atk = INT64_MIN;
    std::string def_summary = "<missing>";

    if (is_schema) {
        if (self.HasField("name")) {
            abot::Value name_field = self.GetField("name");
            if (name_field.IsString()) {
                name = name_field.GetString();
            }
        }
        if (self.HasField("camp")) {
            abot::Value camp_field = self.GetField("camp");
            if (camp_field.IsInt()) {
                camp = camp_field.GetInt();
            }
        }
        if (self.HasField("hp")) {
            abot::Value hp_field = self.GetField("hp");
            if (hp_field.IsInt()) {
                hp = hp_field.GetInt();
            }
        }
        if (self.HasField("atk")) {
            abot::Value atk_field = self.GetField("atk");
            if (atk_field.IsSchema()) {
                if (atk_field.HasField("value")) {
                    abot::Value atk_value = atk_field.GetField("value");
                    if (atk_value.IsInt()) {
                        atk = atk_value.GetInt();
                    }
                }
            } else if (atk_field.IsInt()) {
                atk = atk_field.GetInt();
            }
        }
        if (self.HasField("def")) {
            abot::Value def_field = self.GetField("def");
            if (!def_field.IsNull()) {
                def_summary = def_field.ToString();
            }
        } else if (self.HasField("defenses")) {
            abot::Value defenses_field = self.GetField("defenses");
            if (!defenses_field.IsNull()) {
                def_summary = defenses_field.ToString();
            }
        }
        if (self.HasField("turn")) {
            abot::Value turn_field = self.GetField("turn");
            if (turn_field.IsSchema() && turn_field.HasField("multiplier")) {
                abot::Value mult_field = turn_field.GetField("multiplier");
                if (mult_field.IsDouble()) {
                    multiplier = mult_field.GetDouble();
                } else if (mult_field.IsInt()) {
                    multiplier = (double)mult_field.GetInt();
                }
            }
        }
    }

    std::ostringstream oss;
    oss << "[SELF_DIAG][" << source << "] "
        << "name=" << name << " "
        << "camp=" << ((camp == INT64_MIN) ? std::string("<missing>") : std::to_string(camp)) << " "
        << "hp=" << ((hp == INT64_MIN) ? std::string("<missing>") : std::to_string(hp)) << " "
        << "atk=" << ((atk == INT64_MIN) ? std::string("<missing>") : std::to_string(atk)) << " "
        << "def=" << def_summary << " "
        << "turn.multiplier=" << multiplier << " "
        << "IsHandle=" << (is_handle ? 1 : 0) << " "
        << "handle_id=" << (unsigned long long)handle_id << " "
        << "IsSchema=" << (is_schema ? 1 : 0);
    AppendBattleInfo(oss.str());
}

namespace abot {

// 线程本地存储：维护每个线程的环境 
thread_local std::stack<ExecutionEnvironment*> g_environment_stack;
thread_local std::mutex g_stack_mutex;

// 伤害回调函数指针 - 用于连接到RoundManager进行技能触 
static int (*g_damage_callback)(void*, void*, int, const std::string&) = nullptr;


// ============ ExecutionEnvironment 实现 ============

ExecutionEnvironment::ExecutionEnvironment(Character* actor, Character* target, Battle* battle)
    : actor_(actor), target_(target), battle_(battle), current_scope_(nullptr)
{
    // 入栈
    g_environment_stack.push(this);
}

ExecutionEnvironment::~ExecutionEnvironment()
{
    // 出栈
    if (!g_environment_stack.empty() && g_environment_stack.top() == this) {
        g_environment_stack.pop();
    }
}

ExecutionEnvironment* ExecutionEnvironment::Current()
{
    if (g_environment_stack.empty()) {
        return nullptr;
    }
    return g_environment_stack.top();
}

void ExecutionEnvironment::SetIntProperty(const std::string& key, int value)
{
    int_properties_[key] = value;
}

int ExecutionEnvironment::GetIntProperty(const std::string& key, int default_val) const
{
    auto it = int_properties_.find(key);
    return (it != int_properties_.end()) ? it->second : default_val;
}

void ExecutionEnvironment::SetDoubleProperty(const std::string& key, double value)
{
    double_properties_[key] = value;
}

double ExecutionEnvironment::GetDoubleProperty(const std::string& key, double default_val) const
{
    auto it = double_properties_.find(key);
    return (it != double_properties_.end()) ? it->second : default_val;
}

void ExecutionEnvironment::SetPointerProperty(const std::string& key, void* value)
{
    pointer_properties_[key] = value;
}

void* ExecutionEnvironment::GetPointerProperty(const std::string& key, void* default_val) const
{
    auto it = pointer_properties_.find(key);
    return (it != pointer_properties_.end()) ? it->second : default_val;
}

void ExecutionEnvironment::SetValueProperty(const std::string& key, const Value& value)
{
    // 🟥【任务3】对 self 的严格检查：**必须拒绝任何 schema**
    if (key == "self") {
        if (value.IsSchema()) {
            // ❌ FATAL: self 不得是 schema（无论是否同时是 handle）
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog(
                    "[DEBUG] ExecutionEnvironment::SetValueProperty(\"self\") received schema — forbidden.");
            }
            return;  // ❌ 直接拒绝，不存储
        }
        
        // ✔ 只允许纯 handle（IsHandle=1 && IsSchema=0）
        if (value.IsHandle() && !value.IsSchema()) {
            value_properties_[key] = std::make_shared<Value>(value);
            
            // 🟥【任务1.3】硬日志验证 - env 成功接受纯 handle
            if (g_current_round_manager) {
                char buf[256];
                snprintf(buf, sizeof(buf),
                    "[DIAG][ENV_ACCEPT] ExecutionEnvironment::SetValueProperty('self') accepted pure handle: IsHandle=%d IsSchema=%d type=%d",
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
                "[DEBUG] ExecutionEnvironment::SetValueProperty(\"self\") received non-handle value, type=%d IsHandle=%d IsSchema=%d",
                (int)value.GetType(),
                value.IsHandle() ? 1 : 0,
                value.IsSchema() ? 1 : 0);
            g_current_round_manager->AppendSkillTriggerLog(buf);
        }
        return;  // ❌ 拒绝非 handle 的 self
    }

    value_properties_[key] = std::make_shared<Value>(value);
}

Value ExecutionEnvironment::GetValueProperty(const std::string& key) const
{
    // 🟥 【任务2】记录 GET 操作
    if (key == "self" || key == "enemy") {
        if (g_current_round_manager) {
            auto it = value_properties_.find(key);
            if (it != value_properties_.end() && it->second) {
                char buf[256];
                const Value& val = *it->second;
                snprintf(buf, sizeof(buf),
                    "[DIAG][ENV] GET %s: IsHandle=%d handle=%d IsSchema=%d",
                    key.c_str(),
                    val.IsHandle() ? 1 : 0,
                    val.IsHandle() ? (int)val.GetHandle().GetID() : -1,
                    val.IsSchema() ? 1 : 0);
                g_current_round_manager->AppendSkillTriggerLog(buf);
            }
        }
    }
    
    auto it = value_properties_.find(key);
    if (it != value_properties_.end() && it->second) {
        Value result = *it->second;
        if (key == "self") {
            LogSelfValueState("GETVALUE", result);
        }
        return result;
    }
    if (key == "self") {
        const char* buf = "[SELF_DIAG][GETVALUE] self not found";
        fprintf(stderr, "%s\n", buf);
        AppendBattleInfo(buf);
    }
    return Value();  // 返回空 
}

bool ExecutionEnvironment::HasProperty(const std::string& key) const
{
    return int_properties_.count(key) > 0 ||
           double_properties_.count(key) > 0 ||
           pointer_properties_.count(key) > 0 ||
           value_properties_.count(key) > 0;
}

void ExecutionEnvironment::RemoveProperty(const std::string& key)
{
    int_properties_.erase(key);
    double_properties_.erase(key);
    pointer_properties_.erase(key);
    value_properties_.erase(key);
}

void ExecutionEnvironment::ClearProperties()
{
    int_properties_.clear();
    double_properties_.clear();
    pointer_properties_.clear();
    value_properties_.clear();
}

int ExecutionEnvironment::GetStackDepth()
{
    return static_cast<int>(g_environment_stack.size());
}

ExecutionEnvironment* ExecutionEnvironment::GetTop()
{
    if (g_environment_stack.empty()) {
        return nullptr;
    }
    return g_environment_stack.top();
}

void ExecutionEnvironment::SetPara(std::shared_ptr<Value> para)
{
    para_ = para;
}

std::shared_ptr<Value> ExecutionEnvironment::GetPara() const
{
    return para_;
}

void ExecutionEnvironment::SetMessage(std::shared_ptr<Value> message)
{
    message_ = message;
}

std::shared_ptr<Value> ExecutionEnvironment::GetMessage() const
{
    return message_;
}

std::shared_ptr<Value> ExecutionEnvironment::GetArgument(int index) const
{
    std::string arg_name = "__arg" + std::to_string(index) + "__";
    auto it = value_properties_.find(arg_name);
    if (it != value_properties_.end()) {
        return it->second;
    }
    return nullptr;
}

int ExecutionEnvironment::GetArgumentCount() const
{
    auto it = int_properties_.find("__argc__");
    if (it != int_properties_.end()) {
        return it->second;
    }
    return 0;
}

void ExecutionEnvironment::SetDamageCallback(int (*callback)(void*, void*, int, const std::string&))
{
    g_damage_callback = callback;
}

int (*ExecutionEnvironment::GetDamageCallback())(void*, void*, int, const std::string&)
{
    return g_damage_callback;
}

/**
 * @brief 初始 Character.extra，将所 C++ 成员写入 extra 作为唯一真源
 */
void ExecutionEnvironment::InitializeCharacterExtra(Character* character)
{
    if (!character) {
        return;
    }

    // 【补丁1：改为补全缺失字段而不是直接返回】
    // 即使 extra 已部分初始化，也要确保所有必需字段都存在

    // name - 总是同步
    character->extra["name"] = Value(character->name);

    // camp
    if (character->extra.find("camp") == character->extra.end()) {
        Value camp_schema = Value::CreateSchema();
        camp_schema.SetField("value", Value(static_cast<int64_t>(character->camp)));
        character->extra["camp"] = camp_schema;
    }

    // atk
    if (character->extra.find("atk") == character->extra.end()) {
        Value atk_schema = Value::CreateSchema();
        atk_schema.SetField("value", Value(static_cast<int64_t>(character->atk)));
        character->extra["atk"] = atk_schema;
    }

    // dmg
    if (character->extra.find("dmg") == character->extra.end()) {
        Value dmg_schema = Value::CreateSchema();
        dmg_schema.SetField("d1", Value(static_cast<int64_t>(character->dmg[0])));
        dmg_schema.SetField("d2", Value(static_cast<int64_t>(character->dmg[1])));
        dmg_schema.SetField("d3", Value(static_cast<int64_t>(character->dmg[2])));
        dmg_schema.SetField("d4", Value(static_cast<int64_t>(character->dmg[3])));
        character->extra["dmg"] = dmg_schema;
    }

    // hp
    if (character->extra.find("hp") == character->extra.end()) {
        Value hp_schema = Value::CreateSchema();
        hp_schema.SetField("value", Value(static_cast<int64_t>(character->hp)));
        hp_schema.SetField("max", Value(static_cast<int64_t>(character->max_hp)));
        character->extra["hp"] = hp_schema;
    }

    // 【关键字段】turn - 系统内置函数依赖此字段
    if (character->extra.find("turn") == character->extra.end()) {
        Value turn_schema = Value::CreateSchema();
        turn_schema.SetField("multiplier", Value(character->turn.multiplier));
        character->extra["turn"] = turn_schema;
    }

    // defenses
    if (character->extra.find("defenses") == character->extra.end()) {
        Value defenses_array = Value::CreateArray();
        for (const auto& defense : character->defenses) {
            Value defense_schema = Value::CreateSchema();
            defense_schema.SetField("value", Value(static_cast<int64_t>(defense.value)));
            defense_schema.SetField("tag", Value(defense.tag));
            defenses_array.AppendElement(defense_schema);
        }
        character->extra["defenses"] = defenses_array;
    }

    // damage_reductions
    if (character->extra.find("damage_reductions") == character->extra.end()) {
        Value reductions_array = Value::CreateArray();
        for (const auto& reduction : character->damage_reductions) {
            Value reduction_schema = Value::CreateSchema();
            reduction_schema.SetField("value", Value(static_cast<int64_t>(reduction.value)));
            reduction_schema.SetField("tag", Value(reduction.tag));
            reductions_array.AppendElement(reduction_schema);
        }
        character->extra["damage_reductions"] = reductions_array;
    }

    // tags
    if (character->extra.find("tags") == character->extra.end()) {
        Value tags_array = Value::CreateArray();
        for (const auto& tag : character->tags) {
            tags_array.AppendElement(Value(tag));
        }
        character->extra["tags"] = tags_array;
    }

    // skill_cooldowns
    if (character->extra.find("skill_cooldowns") == character->extra.end()) {
        Value cooldowns_schema = Value::CreateSchema();
        for (auto& cd : character->skill_cooldowns) {
            cooldowns_schema.SetField(cd.first, Value(static_cast<int64_t>(cd.second)));
        }
        character->extra["skill_cooldowns"] = cooldowns_schema;
    }
}

/**
 * @brief 同步 C++ 原生成员到 Character.extra（字段无关通用同步）
 * 只同步 extra 中已存在的字段，不创建新字段，不破坏用户扩展字段
 */
void ExecutionEnvironment::SyncNativeToExtra(Character* character)
{
    if (!character) {
        return;
    }

    // 遍历 extra 中所有已存在的字段
    for (auto& kv : character->extra) {
        const std::string& key = kv.first;
        Value& val = kv.second;

        // hp - Schema{value: int, max: int}
        if (key == "hp") {
            if (val.IsSchema()) {
                if (val.HasField("value")) {
                    val.SetField("value", Value(static_cast<int64_t>(character->hp)));
                }
                if (val.HasField("max")) {
                    val.SetField("max", Value(static_cast<int64_t>(character->max_hp)));
                }
            } else if (val.IsInt()) {
                // Fallback: 如果 extra["hp"] 是纯 int（兼容旧格式）
                val = Value(static_cast<int64_t>(character->hp));
            }
            continue;
        }

        // max_hp - int
        if (key == "max_hp") {
            if (val.IsInt()) {
                val = Value(static_cast<int64_t>(character->max_hp));
            }
            continue;
        }

        // atk - Schema{value: int}
        if (key == "atk") {
            if (val.IsSchema() && val.HasField("value")) {
                val.SetField("value", Value(static_cast<int64_t>(character->atk)));
            }
            continue;
        }

        // dmg - Schema{d1, d2, d3, d4}
        if (key == "dmg") {
            if (val.IsSchema()) {
                for (int i = 0; i < 4; i++) {
                    std::string d = "d" + std::to_string(i + 1);
                    if (val.HasField(d)) {
                        val.SetField(d, Value(static_cast<int64_t>(character->dmg[i])));
                    }
                }
            }
            continue;
        }

        // turn - Schema{multiplier: double}
        if (key == "turn") {
            if (val.IsSchema() && val.HasField("multiplier")) {
                val.SetField("multiplier", Value(character->turn.multiplier));
            }
            continue;
        }

        // defenses - Array of Schema{value, tag}
        if (key == "defenses") {
            if (val.IsArray()) {
                Value arr = Value::CreateArray();
                for (const auto& d : character->defenses) {
                    Value s = Value::CreateSchema();
                    s.SetField("value", Value(static_cast<int64_t>(d.value)));
                    s.SetField("tag", Value(d.tag));
                    arr.AppendElement(s);
                }
                val = arr;
            }
            continue;
        }

        // damage_reductions - Array of Schema{value, tag}
        if (key == "damage_reductions") {
            if (val.IsArray()) {
                Value arr = Value::CreateArray();
                for (const auto& r : character->damage_reductions) {
                    Value s = Value::CreateSchema();
                    s.SetField("value", Value(static_cast<int64_t>(r.value)));
                    s.SetField("tag", Value(r.tag));
                    arr.AppendElement(s);
                }
                val = arr;
            }
            continue;
        }

        // tags - Array of String
        if (key == "tags") {
            if (val.IsArray()) {
                Value arr = Value::CreateArray();
                for (const auto& t : character->tags) {
                    arr.AppendElement(Value(t));
                }
                val = arr;
            }
            continue;
        }

        // skill_cooldowns - Schema{skill_id: int}
        if (key == "skill_cooldowns") {
            if (val.IsSchema()) {
                Value s = Value::CreateSchema();
                for (const auto& cd : character->skill_cooldowns) {
                    s.SetField(cd.first, Value(static_cast<int64_t>(cd.second)));
                }
                val = s;
            }
            continue;
        }

        // camp - Schema{value: int} 或 plain int
        if (key == "camp") {
            if (val.IsSchema() && val.HasField("value")) {
                val.SetField("value", Value(static_cast<int64_t>(character->camp)));
            } else if (val.IsInt()) {
                val = Value(static_cast<int64_t>(character->camp));
            }
            continue;
        }

        // ❗ 对于用户扩展字段：不做任何事
        // extra["custom_field"] 保持不变
    }
}

void ExecutionEnvironment::RegisterCharacterData(Character* character)
{
    if (!character) {
        return;
    }

    //  确保 extra 已初始化
    InitializeCharacterExtra(character);

    SchemaValue schema_value;

    //  单一来源：从 extra 注入所有字 
    for (auto it = character->extra.begin(); it != character->extra.end(); ++it) {
        schema_value.SetField(it->first, it->second);
    }

    ObjectTable* obj_table = GetObjectTable();

    try {
        ObjectHandle handle = obj_table->Create(schema_value);

        SetPointerProperty("self_handle_id",
                           reinterpret_cast<void*>(static_cast<uintptr_t>(handle.GetID())));

        //  设置 handle-backed self
        Value self_handle_value;
        self_handle_value.SetHandle(handle);
        SetValueProperty("self", self_handle_value);

        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_CHARACTER] Handle created: %llu for %s (%zu fields)\n",
                    (unsigned long long)handle.GetID(), character->name.c_str(), character->extra.size());
            fflush(f);
            fclose(f);
        }

    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_CHARACTER] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

void ExecutionEnvironment::AppendDiagnosticLog(const std::string& message)
{
    diagnostic_log_ += message;
}

std::string ExecutionEnvironment::GetDiagnosticLog() const
{
    return diagnostic_log_;
}

void ExecutionEnvironment::ClearDiagnosticLog()
{
    diagnostic_log_.clear();
}

void ExecutionEnvironment::RegisterSelf(Character* character)
{
    if (!character) {
        return;
    }

    // 🟥【新增】在构建 ObjectTable 前，先同步 C++ 成员到 extra
    // 确保 C++ 修改（如 HP 扣除）反映到 extra，然后才构建 ObjectTable
    SyncNativeToExtra(character);

    // 🟥 只注册行动者到 "self"，使用独立的句柄
    InitializeCharacterExtra(character);

    SchemaValue schema_value;
    for (auto it = character->extra.begin(); it != character->extra.end(); ++it) {
        schema_value.SetField(it->first, it->second);
    }

    ObjectTable* obj_table = GetObjectTable();

    try {
        ObjectHandle handle = obj_table->Create(schema_value);

        // 【诊断3】RegisterSelf 创建的 Handle 及其 Schema 内容
        {
            char diag_buf[512];
            int atk_num = 0, d1 = 0, d2 = 0, d3 = 0, d4 = 0;
            if (schema_value.HasField("atk")) {
                Value atk_val = schema_value.GetField("atk");
                if (atk_val.IsSchema() && atk_val.HasField("value")) {
                    atk_num = (int)atk_val.GetField("value").GetInt();
                }
            }
            if (schema_value.HasField("dmg")) {
                Value dmg_val = schema_value.GetField("dmg");
                if (dmg_val.IsSchema()) {
                    if (dmg_val.HasField("d1")) d1 = (int)dmg_val.GetField("d1").GetInt();
                    if (dmg_val.HasField("d2")) d2 = (int)dmg_val.GetField("d2").GetInt();
                    if (dmg_val.HasField("d3")) d3 = (int)dmg_val.GetField("d3").GetInt();
                    if (dmg_val.HasField("d4")) d4 = (int)dmg_val.GetField("d4").GetInt();
                }
            }
            snprintf(diag_buf, sizeof(diag_buf),
                "[REGISTER_SELF_CREATE_HANDLE] name=%s handle=%llu atk=%d dmg=[%d,%d,%d,%d]\n",
                character->name.c_str(),
                (unsigned long long)handle.GetID(),
                atk_num, d1, d2, d3, d4);
            fprintf(stderr, "%s", diag_buf);
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog(diag_buf);
            }
        }

        // 🟥 只设置 "self"，不覆盖其他变量
        Value self_handle_value;
        
        // 🔴【硬诊断】SetHandle 前后的类型检查
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_SELF_DIAG] Before SetHandle: type_=%d IsHandle=%d IsSchema=%d\n",
                    (int)self_handle_value.GetType(), 
                    self_handle_value.IsHandle() ? 1 : 0,
                    self_handle_value.IsSchema() ? 1 : 0);
            fflush(f);
            fclose(f);
        }
        
        self_handle_value.SetHandle(handle);
        
        // 再次检查
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_SELF_DIAG] After SetHandle: type_=%d IsHandle=%d IsSchema=%d handle_id=%llu\n",
                    (int)self_handle_value.GetType(), 
                    self_handle_value.IsHandle() ? 1 : 0,
                    self_handle_value.IsSchema() ? 1 : 0,
                    (unsigned long long)handle.GetID());
            fflush(f);
            fclose(f);
        }
        
        SetValueProperty("self", self_handle_value);
        
        // 再检查一次 env 中的 self
        Value env_self = GetValueProperty("self");
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_SELF_DIAG] After SetValueProperty: env.self type_=%d IsHandle=%d IsSchema=%d\n",
                    (int)env_self.GetType(), 
                    env_self.IsHandle() ? 1 : 0,
                    env_self.IsSchema() ? 1 : 0);
            fflush(f);
            fclose(f);
        }

        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_SELF] Handle created: %llu for %s\n",
                    (unsigned long long)handle.GetID(), character->name.c_str());
            fflush(f);
            fclose(f);
        }
    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_SELF] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

void ExecutionEnvironment::RegisterTarget(Character* character)
{
    if (!character) {
        return;
    }

    // 🟥 只注册目标到 "target"，使用独立的句柄
    InitializeCharacterExtra(character);

    SchemaValue schema_value;
    for (auto it = character->extra.begin(); it != character->extra.end(); ++it) {
        schema_value.SetField(it->first, it->second);
    }

    ObjectTable* obj_table = GetObjectTable();

    try {
        ObjectHandle handle = obj_table->Create(schema_value);

        // 🟥 只设置 "target"，不覆盖其他变量
        Value target_handle_value;
        target_handle_value.SetHandle(handle);
        SetValueProperty("target", target_handle_value);

        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_TARGET] Handle created: %llu for %s\n",
                    (unsigned long long)handle.GetID(), character->name.c_str());
            fflush(f);
            fclose(f);
        }
    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_TARGET] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

void ExecutionEnvironment::SyncCharacterData(Character* character)
{
    if (!character) {
        return;
    }

    // 获取之前 RegisterCharacterData() 保存 Handle ID
    uintptr_t handle_ptr = reinterpret_cast<uintptr_t>(
        GetPointerProperty("self_handle_id", nullptr)
    );

    // 如果没有 Handle，说明未调用 RegisterCharacterData()
    if (!handle_ptr) {
        return;
    }

    ObjectTable* obj_table = GetObjectTable();
    ObjectHandle handle(static_cast<uint64_t>(handle_ptr));

    try {
        //  ObjectTable 获取修改后的 SchemaValue
        const SchemaValue& stored_schema = obj_table->Get(handle);

        //  第一步：全量写回 character.extra（无过滤 
        character->extra.clear();
        const auto& all_fields = stored_schema.GetAllFields();
        for (auto it = all_fields.begin(); it != all_fields.end(); ++it) {
            character->extra[it->first] = it->second;
        }

        FILE* f_sync = nullptr;
        if (fopen_s(&f_sync, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f_sync) {
            fprintf(f_sync, "[SYNC_CHARACTER] Synced %zu fields from ObjectTable to character->extra\n",
                    character->extra.size());
            fflush(f_sync);
            fclose(f_sync);
        }

        //  第二步：镜像映射 - 将内建字段写 C++ 成员

        // 镜像字段：name
        auto it_name = character->extra.find("name");
        if (it_name != character->extra.end()) {
            if (it_name->second.IsString()) {
                character->name = it_name->second.GetString();
            }
        }

        // 镜像字段：camp（UFRS: Schema{value: int} 或 fallback plain int）
        auto it_camp = character->extra.find("camp");
        if (it_camp != character->extra.end()) {
            if (it_camp->second.IsSchema() && it_camp->second.HasField("value")) {
                Value v = it_camp->second.GetField("value");
                if (v.IsInt()) character->camp = static_cast<int>(v.GetInt());
            } else if (it_camp->second.IsInt()) {
                character->camp = static_cast<int>(it_camp->second.GetInt());
            }
        }

        // 镜像字段：hp（UFRS: Schema{value: int, max: int} 或 fallback plain int）
        auto it_hp = character->extra.find("hp");
        if (it_hp != character->extra.end()) {
            if (it_hp->second.IsSchema()) {
                if (it_hp->second.HasField("value")) {
                    Value v = it_hp->second.GetField("value");
                    if (v.IsInt()) character->hp = static_cast<int>(v.GetInt());
                }
                if (it_hp->second.HasField("max")) {
                    Value v = it_hp->second.GetField("max");
                    if (v.IsInt()) character->max_hp = static_cast<int>(v.GetInt());
                }
            } else if (it_hp->second.IsInt()) {
                character->hp = static_cast<int>(it_hp->second.GetInt());
            }
        }

        // 镜像字段：max_hp
        auto it_max_hp = character->extra.find("max_hp");
        if (it_max_hp != character->extra.end()) {
            if (it_max_hp->second.IsInt()) {
                character->max_hp = static_cast<int>(it_max_hp->second.GetInt());
            }
        }

        // 镜像字段：hp_restore
        auto it_hp_restore = character->extra.find("hp_restore");
        if (it_hp_restore != character->extra.end()) {
            if (it_hp_restore->second.IsInt()) {
                character->hp_restore = static_cast<int>(it_hp_restore->second.GetInt());
            }
        }

        // 镜像字段：temp_hp
        auto it_temp_hp = character->extra.find("temp_hp");
        if (it_temp_hp != character->extra.end()) {
            if (it_temp_hp->second.IsInt()) {
                character->temp_hp = static_cast<int>(it_temp_hp->second.GetInt());
            }
        }

        // 镜像字段：atk（嵌 schema 
        auto it_atk = character->extra.find("atk");
        if (it_atk != character->extra.end()) {
            if (it_atk->second.IsSchema()) {
                if (it_atk->second.HasField("value")) {
                    Value atk_value = it_atk->second.GetField("value");
                    int old_atk = character->atk;
                    if (atk_value.IsInt()) {
                        character->atk = static_cast<int>(atk_value.GetInt());
                    }
                    // 诊断：记录 atk 的改变
                    if (g_current_round_manager) {
                        char buf[256];
                        snprintf(buf, sizeof(buf),
                            "[SYNC_CHARACTER_DATA] %s: atk changed from %d to %d",
                            character->name.c_str(), old_atk, character->atk);
                        g_current_round_manager->AppendSkillTriggerLog(buf);
                    }
                }
            }
        } else {
            FILE* f_atk = nullptr;
            if (fopen_s(&f_atk, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f_atk) {
                fprintf(f_atk, "[SYNC_CHARACTER] WARNING: atk field NOT found\n");
                fflush(f_atk);
                fclose(f_atk);
            }
        }

        // 镜像字段：dmg（嵌 schema 
        auto it_dmg = character->extra.find("dmg");
        if (it_dmg != character->extra.end()) {
            if (it_dmg->second.IsSchema()) {
                int old_dmg[4] = {character->dmg[0], character->dmg[1], character->dmg[2], character->dmg[3]};
                if (it_dmg->second.HasField("d1")) {
                    Value d1 = it_dmg->second.GetField("d1");
                    if (d1.IsInt()) character->dmg[0] = static_cast<int>(d1.GetInt());
                }
                if (it_dmg->second.HasField("d2")) {
                    Value d2 = it_dmg->second.GetField("d2");
                    if (d2.IsInt()) character->dmg[1] = static_cast<int>(d2.GetInt());
                }
                if (it_dmg->second.HasField("d3")) {
                    Value d3 = it_dmg->second.GetField("d3");
                    if (d3.IsInt()) character->dmg[2] = static_cast<int>(d3.GetInt());
                }
                if (it_dmg->second.HasField("d4")) {
                    Value d4 = it_dmg->second.GetField("d4");
                    if (d4.IsInt()) character->dmg[3] = static_cast<int>(d4.GetInt());
                }
                // 诊断：记录 dmg 的改变
                if (g_current_round_manager) {
                    char buf[256];
                    snprintf(buf, sizeof(buf),
                        "[SYNC_CHARACTER_DATA] %s: dmg changed from [%d,%d,%d,%d] to [%d,%d,%d,%d]",
                        character->name.c_str(), 
                        old_dmg[0], old_dmg[1], old_dmg[2], old_dmg[3],
                        character->dmg[0], character->dmg[1], character->dmg[2], character->dmg[3]);
                    g_current_round_manager->AppendSkillTriggerLog(buf);
                }
            }
        } else {
            FILE* f_dmg = nullptr;
            if (fopen_s(&f_dmg, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f_dmg) {
                fprintf(f_dmg, "[SYNC_CHARACTER] WARNING: dmg field NOT found\n");
                fflush(f_dmg);
                fclose(f_dmg);
            }
        }

        // 镜像字段：dfs（UFRS: Schema{value: int}）
        auto it_dfs = character->extra.find("dfs");
        if (it_dfs != character->extra.end()) {
            if (it_dfs->second.IsSchema() && it_dfs->second.HasField("value")) {
                Value v = it_dfs->second.GetField("value");
                int dfs_int = v.IsInt() ? static_cast<int>(v.GetInt()) : 0;
                character->defenses.clear();
                if (dfs_int > 0) {
                    character->defenses.push_back({dfs_int, ""});
                }
            }
        }

        // 镜像字段：aggro
        auto it_aggro = character->extra.find("aggro");
        if (it_aggro != character->extra.end()) {
            if (it_aggro->second.IsInt()) {
                character->aggro = static_cast<int>(it_aggro->second.GetInt());
            }
        }

        // 镜像字段：is_alive
        auto it_is_alive = character->extra.find("is_alive");
        if (it_is_alive != character->extra.end()) {
            if (it_is_alive->second.IsInt()) {
                character->is_alive = (it_is_alive->second.GetInt() != 0);
            }
        }

        // 镜像字段：turn（嵌 schema 
        auto it_turn = character->extra.find("turn");
        if (it_turn != character->extra.end()) {
            if (it_turn->second.IsSchema()) {
                if (it_turn->second.HasField("multiplier")) {
                    Value mult = it_turn->second.GetField("multiplier");
                    if (mult.IsDouble()) {
                        character->turn.multiplier = mult.GetDouble();
                    } else if (mult.IsInt()) {
                        character->turn.multiplier = static_cast<double>(mult.GetInt());
                    }
                } else {
                    FILE* f_turn_warn = nullptr;
                    if (fopen_s(&f_turn_warn, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f_turn_warn) {
                        fprintf(f_turn_warn, "[SYNC_CHARACTER] WARNING: turn schema missing multiplier field\n");
                        fflush(f_turn_warn);
                        fclose(f_turn_warn);
                    }
                }
            }
        } else {
            FILE* f_turn_missing = nullptr;
            if (fopen_s(&f_turn_missing, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f_turn_missing) {
                fprintf(f_turn_missing, "[SYNC_CHARACTER] CRITICAL: turn field NOT FOUND in extra. Extra has %zu fields:\n", character->extra.size());
                for (auto it = character->extra.begin(); it != character->extra.end(); ++it) {
                    fprintf(f_turn_missing, "  - %s\n", it->first.c_str());
                }
                fflush(f_turn_missing);
                fclose(f_turn_missing);
            }
        }

        // 镜像字段：defenses（Array[Schema] 
        auto it_defenses = character->extra.find("defenses");
        if (it_defenses != character->extra.end()) {
            if (it_defenses->second.IsArray()) {
                character->defenses.clear();
                const Value& defenses_array = it_defenses->second;
                for (size_t i = 0; i < defenses_array.ArraySize(); ++i) {
                    Value entry = defenses_array.GetElement(i);
                    if (!entry.IsSchema()) continue;

                    DefenseParam param{};
                    // 【修复】添加 HasField 检查
                    if (entry.HasField("value")) {
                        Value value_field = entry.GetField("value");
                        if (value_field.IsInt()) {
                            param.value = static_cast<int>(value_field.GetInt());
                        }
                    }
                    if (entry.HasField("tag")) {
                        Value tag_field = entry.GetField("tag");
                        if (tag_field.IsString()) {
                            param.tag = tag_field.GetString();
                        }
                    }
                    character->defenses.push_back(param);
                }
            }
        }

        // 镜像字段：damage_reductions（Array[Schema] 
        auto it_damage_reductions = character->extra.find("damage_reductions");
        if (it_damage_reductions != character->extra.end()) {
            if (it_damage_reductions->second.IsArray()) {
                character->damage_reductions.clear();
                const Value& reductions_array = it_damage_reductions->second;
                for (size_t i = 0; i < reductions_array.ArraySize(); ++i) {
                    Value entry = reductions_array.GetElement(i);
                    if (!entry.IsSchema()) continue;

                    DamageReductionParam param{};
                    // 【修复】添加 HasField 检查
                    if (entry.HasField("value")) {
                        Value value_field = entry.GetField("value");
                        if (value_field.IsDouble()) {
                            param.value = static_cast<float>(value_field.GetDouble());
                        } else if (value_field.IsInt()) {
                            param.value = static_cast<float>(value_field.GetInt());
                        }
                    }
                    if (entry.HasField("tag")) {
                        Value tag_field = entry.GetField("tag");
                        if (tag_field.IsString()) {
                            param.tag = tag_field.GetString();
                        }
                    }
                    character->damage_reductions.push_back(param);
                }
            }
        }

        // 镜像字段：tags（Array[String] 
        auto it_tags = character->extra.find("tags");
        if (it_tags != character->extra.end()) {
            if (it_tags->second.IsArray()) {
                character->tags.clear();
                const Value& tags_array = it_tags->second;
                for (size_t i = 0; i < tags_array.ArraySize(); ++i) {
                    Value tag = tags_array.GetElement(i);
                    if (tag.IsString()) {
                        character->tags.push_back(tag.GetString());
                    }
                }
            }
        }

        // 镜像字段：skill_cooldowns（Schema 
        auto it_skill_cooldowns = character->extra.find("skill_cooldowns");
        if (it_skill_cooldowns != character->extra.end()) {
            if (it_skill_cooldowns->second.IsSchema()) {
                character->skill_cooldowns.clear();
                const auto& cooldowns_fields = it_skill_cooldowns->second.GetAllFields();
                for (auto cd_it = cooldowns_fields.begin(); cd_it != cooldowns_fields.end(); ++cd_it) {
                    if (cd_it->second.IsInt()) {
                        character->skill_cooldowns[cd_it->first] = 
                            static_cast<int>(cd_it->second.GetInt());
                    }
                }
            }
        }

        FILE* f_mirror = nullptr;
        if (fopen_s(&f_mirror, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f_mirror) {
            fprintf(f_mirror, "[SYNC_CHARACTER] Mirror-mapped builtin fields to C++ members\n");
            fflush(f_mirror);
            fclose(f_mirror);
        }

    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[SYNC_CHARACTER] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

//  Phase 1：对象句柄实 
int ExecutionEnvironment::AllocateObjectHandle(uintptr_t object_ptr)
{
    int handle_id = next_handle_id_++;
    object_handles_[handle_id] = object_ptr;
    return handle_id;
}

uintptr_t ExecutionEnvironment::GetObjectHandle(int handle_id) const
{
    auto it = object_handles_.find(handle_id);
    if (it != object_handles_.end()) {
        return it->second;
    }
    return 0;  // 无效句柄返回 0
}

// ===== 🟦 全局统一字段系统：其他预设类型的 Register/Sync 实现 =====

/**
 * @brief  SkillPreset 注册字段 ObjectTable
 * 所 SkillPreset 字段存储 extra 中， Character 模式一 
 */
void ExecutionEnvironment::RegisterSkillPresetData(SkillPreset* skill)
{
    if (!skill) {
        return;
    }

    ObjectTable* obj_table = GetObjectTable();
    if (!obj_table) {
        return;
    }

    //  definition 中填 extra（如果为空）
    if (skill->extra.empty()) {
        // Note: This is a simplified version - actual implementation
        // would need to access SkillPreset::GetDefinition()
        // For now, just create a basic schema
    }

    SchemaValue schema_value;

    //  extra 注入所有字段到 schema_value
    for (auto it = skill->extra.begin(); it != skill->extra.end(); ++it) {
        schema_value.SetField(it->first, it->second);
    }

    try {
        // 创建 ObjectHandle 并存储到 ObjectTable
        ObjectHandle handle = obj_table->Create(schema_value);

        // 设置 env.self  handle-backed
        Value handle_value;
        handle_value.SetHandle(handle);
        SetValueProperty("self", handle_value);

    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_SKILL] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

/**
 * @brief 同步 ObjectTable 中的修改回到 SkillPreset.extra
 * 
 * 执行流程 
 * 1. 获取当前环境 self（应 Handle-backed 
 * 2.  ObjectTable 获取修改后的 SchemaValue
 * 3. 全量写回 skill->extra
 * 
 * @param skill 技能预设对 
 * @note 必须 RegisterSkillPresetData() 之后调用
 */
void ExecutionEnvironment::SyncSkillPresetData(SkillPreset* skill)
{
    if (!skill) {
        return;
    }

    // 获取当前 env.self
    Value self_value = GetValueProperty("self");
    if (!self_value.IsHandle()) {
        return;  // 没有 Handle，无法同 
    }

    ObjectTable* obj_table = GetObjectTable();
    if (!obj_table) {
        return;
    }

    ObjectHandle handle = self_value.GetHandle();

    try {
        const SchemaValue& stored_schema = obj_table->Get(handle);

        //  全量写回 skill->extra（无过滤 
        skill->extra.clear();
        const auto& all_fields = stored_schema.GetAllFields();
        for (auto it = all_fields.begin(); it != all_fields.end(); ++it) {
            skill->extra[it->first] = it->second;
        }

        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[SYNC_SKILL] Synced %zu fields from ObjectTable to skill->extra\n",
                    skill->extra.size());
            fflush(f);
            fclose(f);
        }

    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[SYNC_SKILL] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

/**
 * @brief  StatePreset 注册字段 ObjectTable
 */
void ExecutionEnvironment::RegisterStatePresetData(StatePreset* state)
{
    if (!state) {
        return;
    }

    ObjectTable* obj_table = GetObjectTable();
    if (!obj_table) {
        return;
    }

    SchemaValue schema_value;

    //  extra 注入所有字 
    for (auto it = state->extra.begin(); it != state->extra.end(); ++it) {
        schema_value.SetField(it->first, it->second);
    }

    try {
        ObjectHandle handle = obj_table->Create(schema_value);
        Value handle_value;
        handle_value.SetHandle(handle);
        SetValueProperty("self", handle_value);

    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_STATE] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

/**
 * @brief 同步 ObjectTable 中的修改回到 StatePreset.extra
 * 
 * 执行流程 
 * 1. 获取当前环境 self（应 Handle-backed 
 * 2.  ObjectTable 获取修改后的 SchemaValue
 * 3. 全量写回 state->extra
 * 
 * @param state 状态预设对 
 * @note 必须 RegisterStatePresetData() 之后调用
 */
void ExecutionEnvironment::SyncStatePresetData(StatePreset* state)
{
    if (!state) {
        return;
    }

    // 获取当前 env.self
    Value self_value = GetValueProperty("self");
    if (!self_value.IsHandle()) {
        return;  // 没有 Handle，无法同 
    }

    ObjectTable* obj_table = GetObjectTable();
    if (!obj_table) {
        return;
    }

    ObjectHandle handle = self_value.GetHandle();

    try {
        const SchemaValue& stored_schema = obj_table->Get(handle);

        //  全量写回 state->extra（无过滤 
        state->extra.clear();
        const auto& all_fields = stored_schema.GetAllFields();
        for (auto it = all_fields.begin(); it != all_fields.end(); ++it) {
            state->extra[it->first] = it->second;
        }

        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[SYNC_STATE] Synced %zu fields from ObjectTable to state->extra\n",
                    state->extra.size());
            fflush(f);
            fclose(f);
        }

    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[SYNC_STATE] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

/**
 * @brief  AnkePreset 注册字段 ObjectTable
 */
void ExecutionEnvironment::RegisterAnkePresetData(AnkePreset* anke)
{
    if (!anke) {
        return;
    }

    ObjectTable* obj_table = GetObjectTable();
    if (!obj_table) {
        return;
    }

    SchemaValue schema_value;

    //  extra 注入所有字 
    for (auto it = anke->extra.begin(); it != anke->extra.end(); ++it) {
        schema_value.SetField(it->first, it->second);
    }

    try {
        ObjectHandle handle = obj_table->Create(schema_value);
        Value handle_value;
        handle_value.SetHandle(handle);
        SetValueProperty("self", handle_value);

    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[REGISTER_ANKE] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

/**
 * @brief 同步 ObjectTable 中的修改回到 AnkePreset.extra
 * 
 * 执行流程 
 * 1. 获取当前环境 self（应 Handle-backed 
 * 2.  ObjectTable 获取修改后的 SchemaValue
 * 3. 全量写回 anke->extra
 * 
 * @param anke ANKE 预设对象
 * @note 必须 RegisterAnkePresetData() 之后调用
 */
void ExecutionEnvironment::SyncAnkePresetData(AnkePreset* anke)
{
    if (!anke) {
        return;
    }

    // 获取当前 env.self
    Value self_value = GetValueProperty("self");
    if (!self_value.IsHandle()) {
        return;  // 没有 Handle，无法同 
    }

    ObjectTable* obj_table = GetObjectTable();
    if (!obj_table) {
        return;
    }

    ObjectHandle handle = self_value.GetHandle();

    try {
        const SchemaValue& stored_schema = obj_table->Get(handle);

        //  全量写回 anke->extra（无过滤 
        anke->extra.clear();
        const auto& all_fields = stored_schema.GetAllFields();
        for (auto it = all_fields.begin(); it != all_fields.end(); ++it) {
            anke->extra[it->first] = it->second;
        }

        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[SYNC_ANKE] Synced %zu fields from ObjectTable to anke->extra\n",
                    anke->extra.size());
            fflush(f);
            fclose(f);
        }

    } catch (const std::exception& ex) {
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\Windows\\Temp\\abot_vm_diagnostic.log", "at") == 0 && f) {
            fprintf(f, "[SYNC_ANKE] Exception: %s\n", ex.what());
            fflush(f);
            fclose(f);
        }
    }
}

}  // namespace abot


