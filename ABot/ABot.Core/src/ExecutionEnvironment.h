/**
 * @file ExecutionEnvironment.h
 * @brief 执行环境 - 线程本地上下文管理
 * 
 * 提供执行环境的堆栈管理，支持嵌套调用（如 akr 递归）
 * 通过线程本地存储维护当前执行上下文
 */

#pragma once

#include <stack>
#include <map>
#include <string>
#include <memory>
#include <thread>
#include <mutex>
#include <unordered_set>
#include "ObjectTable.h"

namespace abot {

// 前向声明
class Character;
class Battle;
class Value;
class ScopeStack;
class ObjectTable;
class SkillPreset;
class StatePreset;
class AnkePreset;

/**
 * @brief 检查字段是否为内建字段
 * 
 * 注意：此函数仅用于识别需要镜像映射到 C++ 成员的字段。
 * 不用于过滤 extra 的同步！所有 extra 字段都应该被同步。
 * 
 * @param key 字段名
 * @return 如果是内建字段返回true，否则返回false
 */
inline bool IsBuiltinField(const std::string& key) {
    static const std::unordered_set<std::string> builtin = {
        // 基础属性
        "name", "camp",
        // 战斗属性
        "hp", "max_hp", "hp_restore", "temp_hp", "atk",
        // 伤害数组
        "dmg",
        // 扩展属性
        "aggro", "is_alive", "defenses", "damage_reductions",
        // 回合数据
        "turn",
        // 其他
        "tags", "skills", "states", "skill_cooldowns"
    };
    return builtin.count(key) > 0;
}

/**
 * @class ExecutionEnvironment
 * @brief 执行环境栈 - 维护嵌套调用的上下文
 * 
 * 特性：
 * - 线程本地存储：每个线程独立维护执行堆栈
 * - RAII 设计：构造时入栈，析构时出栈
 * - 属性存储：支持int、double、void* 三种类型
 * - 自动恢复：嵌套调用完成后自动恢复外层状态
 * 
 * 使用示例：
 * ```cpp
 * void skill_func(ExecutionEnvironment* env) {
 *     Character* actor = env->GetActor();
 *     Character* target = env->GetTarget();
 *     
 *     // 调用嵌套的 akr 预设
 *     env->SetProperty("damage_multiplier", 1.5);
 *     // 嵌套函数会看到 damage_multiplier = 1.5
 * }
 * ```
 */
class ExecutionEnvironment {
public:
    /**
     * @brief 构造函数 - 创建并入栈执行环境
     * @param actor 当前作用者
     * @param target 当前目标
     * @param battle 当前战斗
     */
    ExecutionEnvironment(Character* actor, Character* target, Battle* battle);
    
    /**
     * @brief 析构函数 - 自动出栈
     */
    ~ExecutionEnvironment();
    
    // 禁止复制
    ExecutionEnvironment(const ExecutionEnvironment&) = delete;
    ExecutionEnvironment& operator=(const ExecutionEnvironment&) = delete;
    
    /**
     * @brief 获取静态当前执行环境
     * @return 当前栈顶环境，若栈为空返回 nullptr
     */
    static ExecutionEnvironment* Current();
    
    /**
     * @brief 获取作用者
     */
    Character* GetActor() const { return actor_; }
    
    /**
     * @brief 获取目标
     */
    Character* GetTarget() const { return target_; }
    
    /**
     * @brief 获取当前战斗
     */
    Battle* GetBattle() const { return battle_; }
    
    /**
     * @brief 设置整数属性
     * @param key 属性名
     * @param value 值
     */
    void SetIntProperty(const std::string& key, int value);
    
    /**
     * @brief 获取整数属性
     * @param key 属性名
     * @param default_val 默认值（不存在时返回）
     * @return 属性值或默认值
     */
    int GetIntProperty(const std::string& key, int default_val = 0) const;
    
    /**
     * @brief 设置浮点数属性
     */
    void SetDoubleProperty(const std::string& key, double value);
    
    /**
     * @brief 获取浮点数属性
     */
    double GetDoubleProperty(const std::string& key, double default_val = 0.0) const;
    
    /**
     * @brief 设置指针属性（用于存储自定义对象）
     */
    void SetPointerProperty(const std::string& key, void* value);
    
    /**
     * @brief 获取指针属性
     */
    void* GetPointerProperty(const std::string& key, void* default_val = nullptr) const;
    
    /**
     * @brief 设置值对象属性
     */
    void SetValueProperty(const std::string& key, const Value& value);
    
    /**
     * @brief 获取值对象属性
     */
    Value GetValueProperty(const std::string& key) const;
    
    /**
     * @brief 检查属性是否存在
     */
    bool HasProperty(const std::string& key) const;
    
    /**
     * @brief 删除属性
     */
    void RemoveProperty(const std::string& key);
    
    /**
     * @brief 清空所有属性
     */
    void ClearProperties();
    
    /**
     * @brief 设置技能参数 (para)
     * @param para 参数Schema对象
     */
    void SetPara(std::shared_ptr<Value> para);
    
    /**
     * @brief 获取技能参数 (para)
     * @return 参数Schema对象,如果未设置则返回nullptr
     */
    std::shared_ptr<Value> GetPara() const;
    
    /**
     * @brief 设置触发消息 (message)
     * @param message 消息Schema对象
     */
    void SetMessage(std::shared_ptr<Value> message);
    
    /**
     * @brief 获取触发消息 (message)
     * @return 消息Schema对象,如果未设置则返回nullptr
     */
    std::shared_ptr<Value> GetMessage() const;
    
    /**
     * @brief 获取函数参数 - 便利方法
     * @param index 参数索引 (从0开始)
     * @return 参数Value，如果不存在则返回nullptr
     */
    std::shared_ptr<Value> GetArgument(int index) const;
    
    /**
     * @brief 获取函数参数个数
     * @return 参数个数
     */
    int GetArgumentCount() const;
    
    /**
     * @brief 初始化 Character.extra，使其成为唯一字段真源
     * 在角色进入战斗时调用一次，将所有 C++ 成员字段写入 extra
     * @param character 要初始化的角色对象
     * @note 必须在第一次 RegisterCharacterData 之前调用
     */
    void InitializeCharacterExtra(Character* character);
    
    /**
     * @brief 同步 C++ 原生成员到 Character.extra（字段无关通用同步）
     * 只同步 extra 中已存在的字段，不创建新字段，不破坏用户扩展字段
     * @param character 要同步的角色对象
     * @note 在 RegisterSelf() 开头调用，确保 C++ 修改（如 HP 扣除）反映到 extra
     */
    void SyncNativeToExtra(Character* character);
    
    /**
     * @brief 从Character对象注册数据到ExecutionEnvironment
     * 将Character的各个属性（hp, atk等）映射为可访问的变量
     * @param character 要注册的角色数据
     * @note 这个方法应该在解析character卡后调用
     */
    void RegisterCharacterData(Character* character);
    
    /**
     * 🟥【新增】注册行动者数据到 self 槽位
     * @brief 将行动者角色注册为 "self"，不会覆盖 "target"
     * @param character 要设置为 self 的角色
     * @note 独立使用，不调用 RegisterCharacterData
     */
    void RegisterSelf(Character* character);
    
    /**
     * 🟥【新增】注册目标数据到 target 槽位
     * @brief 将目标角色注册为 "target"，不会覆盖 "self"
     * @param character 要设置为 target 的角色
     * @note 独立使用，不调用 RegisterCharacterData
     */
    void RegisterTarget(Character* character);
    
    /**
     * @brief 同步ExecutionEnvironment中修改过的属性回到Character对象
     * 将ExecutionEnvironment中存储的属性值写回到原始的Character对象
     * @param character 要同步的角色对象
     * @note 这个方法应该在虚拟机执行完毕后调用，以应用修改
     */
    void SyncCharacterData(Character* character);
    
    // 🟦 全局统一字段系统：其他节点类型的 Register/Sync
    
    /**
     * @brief 从SkillPreset对象注册数据到ExecutionEnvironment
     * @param skill 要注册的技能预设
     */
    void RegisterSkillPresetData(class SkillPreset* skill);
    
    /**
     * @brief 同步ExecutionEnvironment中修改过的属性回到SkillPreset对象
     * @param skill 要同步的技能预设
     */
    void SyncSkillPresetData(class SkillPreset* skill);
    
    /**
     * @brief 从StatePreset对象注册数据到ExecutionEnvironment
     * @param state 要注册的状态预设
     */
    void RegisterStatePresetData(class StatePreset* state);
    
    /**
     * @brief 同步ExecutionEnvironment中修改过的属性回到StatePreset对象
     * @param state 要同步的状态预设
     */
    void SyncStatePresetData(class StatePreset* state);
    
    /**
     * @brief 从AnkePreset对象注册数据到ExecutionEnvironment
     * @param anke 要注册的ANKE预设
     */
    void RegisterAnkePresetData(class AnkePreset* anke);
    
    /**
     * @brief 同步ExecutionEnvironment中修改过的属性回到AnkePreset对象
     * @param anke 要同步的ANKE预设
     */
    void SyncAnkePresetData(class AnkePreset* anke);
    
    /**
     * @brief 获取堆栈深度（用于调试）
     */
    static int GetStackDepth();
    
    /**
     * @brief 获取环境堆栈顶部
     */
    static ExecutionEnvironment* GetTop();
    
    /**
     * @brief 追加诊断日志到当前环境的日志缓冲区
     * @param message 日志信息
     */
    void AppendDiagnosticLog(const std::string& message);
    
    /**
     * @brief 获取当前环境的诊断日志缓冲区
     * @return 日志内容
     */
    std::string GetDiagnosticLog() const;
    
    /**
     * @brief 清空诊断日志缓冲区
     */
    void ClearDiagnosticLog();
    
    /**
     * @brief 分配对象句柄（Phase 1：对象表）
     * 用于 VM 对环境中真实对象的引用
     * @param object_ptr 对象指针（通常是 Character* 转 uintptr_t）
     * @return 句柄 ID（唯一标识符）
     */
    int AllocateObjectHandle(uintptr_t object_ptr);
    
    /**
     * @brief 查询对象句柄对应的指针
     * @param handle_id 句柄 ID
     * @return 对象指针，如果句柄无效返回 0
     */
    uintptr_t GetObjectHandle(int handle_id) const;
    
    /**
     * @brief 设置当前的 ScopeStack 指针（脚本执行期间）
     * 允许 builtin 函数访问脚本执行中的实时变量修改
     * @param scope ScopeStack 指针，执行完成后传 nullptr
     */
    void SetCurrentScope(ScopeStack* scope) { current_scope_ = scope; }
    
    /**
     * @brief 获取当前的 ScopeStack 指针
     * @return 当前 ScopeStack，如果不在脚本执行期间则返回 nullptr
     */
    ScopeStack* GetCurrentScope() const { return current_scope_; }
    
    /**
     * @brief 获取 ObjectTable（PoC Handle 系统）
     * @return ObjectTable 指针，用于 handle 模式的 Schema 对象管理
     */
    ObjectTable* GetObjectTable() { 
        if (!object_table_) {
            object_table_ = std::make_shared<ObjectTable>();
        }
        return object_table_.get(); 
    }
    
    /**
     * 🟥【关键修复】获取共享的 ObjectTable（用于递归调用时复用）
     */
    std::shared_ptr<ObjectTable> GetSharedObjectTable() {
        if (!object_table_) {
            object_table_ = std::make_shared<ObjectTable>();
        }
        return object_table_;
    }
    
    /**
     * 🟥【关键修复】设置共享的 ObjectTable（用于所有环境复用同一实例）
     */
    void SetSharedObjectTable(std::shared_ptr<ObjectTable> table) {
        object_table_ = table;
    }
    
    /**
     * @brief 设置伤害回调函数
     * 当dodamage被调用时，会调用这个回调来触发被动技能
     * @param callback 回调函数指针，签名为 int(Character*, Character*, int, const std::string&)
     *                 参数为 (attacker, target, damage, tag)，返回值为实际应用的伤害
     */
    static void SetDamageCallback(int (*callback)(void*, void*, int, const std::string&));
    
    /**
     * @brief 获取伤害回调函数
     */
    static int (*GetDamageCallback())(void*, void*, int, const std::string&);
    
private:
    Character* actor_;           ///< 作用者指针
    Character* target_;          ///< 目标指针
    Battle* battle_;             ///< 战斗指针
    ScopeStack* current_scope_;  ///< 当前 ScopeStack（脚本执行期间设置）
    
    std::map<std::string, int> int_properties_;           ///< 整数属性存储
    std::map<std::string, double> double_properties_;     ///< 浮点数属性存储
    std::map<std::string, void*> pointer_properties_;     ///< 指针属性存储
    std::map<std::string, std::shared_ptr<Value>> value_properties_;  ///< 值对象属性
    
    std::shared_ptr<Value> para_;        ///< 技能参数Schema对象
    std::shared_ptr<Value> message_;     ///< 触发消息Schema对象
    std::string diagnostic_log_;         ///< 诊断日志缓冲区
    
    // ✅ Phase 1：ObjectTable（Handle 系统）
    // 🟥【关键修复】改为 shared_ptr，使所有环境能共享同一实例
    std::shared_ptr<ObjectTable> object_table_;  ///< 存储 Schema 对象的表（由所有环境共享）
    
    // ✅ 对象句柄表（VM中真实对象的引用）
    std::map<int, uintptr_t> object_handles_;  ///< handle_id -> object_ptr
    int next_handle_id_ = 1000;                ///< 句柄计数器（避免冲突）
};

/**
 * @struct EnvironmentScope
 * @brief RAII 辅助类 - 简化执行环境的生命周期管理
 * 
 * 使用示例：
 * ```cpp
 * {
 *     EnvironmentScope scope(actor, target, battle);
 *     // 在作用域内执行代码
 *     // 离开作用域时自动清理
 * }
 * ```
 */
struct EnvironmentScope {
    ExecutionEnvironment env;
    
    EnvironmentScope(Character* actor, Character* target, Battle* battle)
        : env(actor, target, battle) {}
    
    ~EnvironmentScope() = default;
    
    // 禁止复制
    EnvironmentScope(const EnvironmentScope&) = delete;
    EnvironmentScope& operator=(const EnvironmentScope&) = delete;
};

}  // namespace abot
