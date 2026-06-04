/**
 * @file ScriptExecutionContainer.h
 * @brief 统一的脚本执行容器 - 管理脚本执行生命周期中的对象同步
 * 
 * 解决问题：SchemaValue 在 ScopeStack 和 ExecutionEnvironment 中是独立副本，
 * VM 修改的是 ScopeStack 版本，但 SyncCharacterData 读取的是 env 版本，
 * 导致修改丢失。
 * 
 * 解决方案：容器负责整个同步周期：
 * 1. 前：创建 Schema 副本，注入到 scope 和 env
 * 2. 执行脚本
 * 3. 后：从 scope 取回修改后的 Schema，同步回到 env，再进行 SyncCharacterData
 */

#pragma once

#include <string>
#include <memory>
#include <functional>
#include <vector>

namespace abot {

// 前向声明
class BytecodeProgram;
class ExecutionEnvironment;
class ScopeStack;
class Value;
class Character;

/**
 * @struct ScriptObjectSlotConfig
 * @brief 脚本对象槽位配置 - 定义如何管理一个对象的生命周期
 * 
 * 使用示例：
 * ```cpp
 * ScriptObjectSlotConfig self_config = {
 *     .slot_name = "self",
 *     .getter = [](ExecutionEnvironment* env) -> void* {
 *         return env->GetActor();
 *     },
 *     .to_schema = [](void* object) -> Value {
 *         Character* ch = static_cast<Character*>(object);
 *         return ch->ToSchemaValue();
 *     },
 *     .from_schema = [](void* object, const Value& schema) {
 *         Character* ch = static_cast<Character*>(object);
 *         ch->FromSchemaValue(schema);
 *     },
 *     .needs_writeback = true
 * };
 * ```
 */
struct ScriptObjectSlotConfig {
    /**
     * @brief 槽位名字（如"self", "target", "ally"）
     */
    std::string slot_name;
    
    /**
     * @brief 从环境获取真实对象的回调
     * @return 真实对象指针，nullptr 表示该槽位不可用
     */
    std::function<void* (ExecutionEnvironment*)> getter;
    
    /**
     * @brief 从真实对象创建 Schema 副本的回调
     * @param object 真实对象指针（由 getter 返回）
     * @return Schema Value 副本
     */
    std::function<Value (void*)> to_schema;
    
    /**
     * @brief 从 Schema 回写真实对象的回调
     * @param object 真实对象指针
     * @param schema 修改后的 Schema Value
     */
    std::function<void (void*, const Value&)> from_schema;
    
    /**
     * @brief 是否需要在脚本执行后回写
     * 某些槽位（如 target）可能只读，无需回写
     */
    bool needs_writeback = true;
};

/**
 * @class ScriptExecutionContainer
 * @brief 统一的脚本执行容器
 * 
 * 特性：
 * - 管理脚本执行的完整生命周期
 * - 自动处理 Schema 创建、注入、提取、回写
 * - 支持多个槽位（self, target, ally 等）
 * - 确保 VM 修改能正确同步回真实对象
 * - 高度合成 - 现有代码无需修改，直接替换 vm.Execute 调用
 */
class ScriptExecutionContainer {
public:
    /**
     * @brief 执行脚本，自动管理对象同步
     * 
     * @param script 要执行的字节码脚本
     * @param env 执行环境
     * @param scope 作用域栈
     * @param slots 对象槽位配置列表
     * 
     * @return true 脚本成功执行，false 脚本执行失败或发生错误
     * 
     * 执行流程：
     * 1. 对每个槽位调用 getter，获取真实对象
     * 2. 对真实对象调用 to_schema，创建 Schema 副本
     * 3. 将 Schema 注入到 scope 和 env
     * 4. 执行脚本（vm.Execute）
     * 5. 对每个需要回写的槽位：
     *    - 从 scope 取回修改后的 Schema
     *    - 如果 Schema 中有修改，同步到 env
     * 6. 调用 env->SyncCharacterData() 进行最终同步
     * 
     * @note 如果任何槽位声明了 from_schema 回调，会自动调用
     *       从_schema_被修改会自动更新到 env，再由 SyncCharacterData 最终同步
     */
    static bool Execute(
        BytecodeProgram* script,
        ExecutionEnvironment* env,
        ScopeStack* scope,
        const std::vector<ScriptObjectSlotConfig>& slots);
    
    /**
     * @brief 执行脚本，仅使用 self 槽位（便利方法）
     * 
     * @param script 要执行的字节码脚本
     * @param env 执行环境
     * @param scope 作用域栈
     * 
     * @return 执行结果
     */
    static bool ExecuteWithSelf(
        BytecodeProgram* script,
        ExecutionEnvironment* env,
        ScopeStack* scope);
    
    /**
     * @brief 创建默认的 self 槽位配置
     * 
     * self 槽位的特点：
     * - 获取方式：env->GetActor()
     * - 创建方式：Character::ToSchemaValue()
     * - 回写方式：Character::FromSchemaValue()
     * - 需要回写：是
     * 
     * @return ScriptObjectSlotConfig 默认配置
     */
    static ScriptObjectSlotConfig CreateDefaultSelfSlot();

private:
    /**
     * @brief 内部辅助：为一个槽位执行完整的同步流程
     */
    static void SyncSlot(
        const ScriptObjectSlotConfig& slot,
        ExecutionEnvironment* env,
        ScopeStack* scope,
        void* object,
        const Value& original_schema);
};

}  // namespace abot
