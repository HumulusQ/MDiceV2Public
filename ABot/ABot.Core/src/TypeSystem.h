/**
 * @file TypeSystem.h
 * @brief ABOT 运行时类型系统（Runtime Type System）
 * 
 * 设计目标：
 * =========
 * 1. 统一的Value对象模型
 * 2. 完整的类型反射和扩展
 * 3. 运行时类型检查和转换
 * 4. 类型安全的操作API
 * 
 * 架构：
 * =====
 * TypeInfo（类型描述）
 *   ├─ 类型名称和分类
 *   ├─ 构造/销毁/复制函数
 *   ├─ 序列化函数
 *   └─ 转换函数
 * 
 * Value（运行时值）
 *   ├─ TypeInfo* - 指向类型描述
 *   ├─ void* data - 指向实际数据
 *   └─ RefCount - 引用计数（for CoW）
 */

#ifndef ABOT_TYPE_SYSTEM_H
#define ABOT_TYPE_SYSTEM_H

#include <string>
#include <map>
#include <memory>
#include <vector>
#include <functional>
#include <cstring>

namespace abot {

// ============================================================================
// 类型系统前向声明
// ============================================================================

class Value;
class TypeInfo;
class SchemaValue;

// 类型分类
enum class TypeCategory {
    Primitive,      // int, double, bool, string
    Composite,      // schema, array
    Special,        // function, null
};

// ============================================================================
// TypeInfo - 运行时类型描述
// ============================================================================

/**
 * @brief 运行时类型信息
 * 
 * 作用：
 * - 提供关于某个类型的完整元数据
 * - 控制该类型的所有操作（构造、销毁、序列化等）
 * - 支持类型检查、转换、反射
 */
class TypeInfo {
public:
    // ---- 类型标识 ----
    std::string name;                    // 类型名称（"int", "schema", "array"等）
    TypeCategory category;               // 类型分类
    size_t size;                         // 数据大小（用于内存分配）
    
    // ---- 构造函数 ----
    using ConstructorFunc = std::function<void*(void)>;
    ConstructorFunc constructor;         // 创建新实例：() -> void*
    
    // ---- 销毁函数 ----
    using DestructorFunc = std::function<void(void*)>;
    DestructorFunc destructor;           // 销毁实例：(void*) -> void
    
    // ---- 复制函数 ----
    using CopyFunc = std::function<void*(const void*)>;
    CopyFunc copy;                       // 深度复制：(src void*) -> (new void*)
    
    // ---- 转换函数 ----
    using ToIntFunc = std::function<int64_t(const void*)>;
    using ToDoubleFunc = std::function<double(const void*)>;
    using ToBoolFunc = std::function<bool(const void*)>;
    using ToStringFunc = std::function<std::string(const void*)>;
    
    ToIntFunc toInt;
    ToDoubleFunc toDouble;
    ToBoolFunc toBool;
    ToStringFunc toString;
    
    // ---- 序列化 ----
    using SerializeFunc = std::function<std::string(const void*)>;
    using DeserializeFunc = std::function<void*(const std::string&)>;
    
    SerializeFunc serialize;             // 序列化为DSL文本
    DeserializeFunc deserialize;         // 从DSL文本反序列化
    
    // ---- Schema特定操作（如果适用） ----
    using SetFieldFunc = std::function<void(void*, const std::string&, const Value&)>;
    using GetFieldFunc = std::function<Value(const void*, const std::string&)>;
    using RemoveFieldFunc = std::function<void(void*, const std::string&)>;
    using GetKeysFunc = std::function<std::vector<std::string>(const void*)>;
    
    SetFieldFunc setField;               // Schema.SetField(key, value)
    GetFieldFunc getField;               // Schema.GetField(key) -> Value
    RemoveFieldFunc removeField;         // Schema.RemoveField(key)
    GetKeysFunc getKeys;                 // Schema.GetKeys() -> [keys]
    
    // ---- Array特定操作 ----
    using GetElementFunc = std::function<Value(const void*, size_t)>;
    using SetElementFunc = std::function<void(void*, size_t, const Value&)>;
    using PushBackFunc = std::function<void(void*, const Value&)>;
    using PopBackFunc = std::function<void(void*)>;
    using GetSizeFunc = std::function<size_t(const void*)>;
    
    GetElementFunc getElement;
    SetElementFunc setElement;
    PushBackFunc pushBack;
    PopBackFunc popBack;
    GetSizeFunc getSize;
    
    // ---- 方法 ----
    TypeInfo(const std::string& n, TypeCategory cat, size_t sz)
        : name(n), category(cat), size(sz) {}
};

// ============================================================================
// 全局类型注册表
// ============================================================================

class TypeRegistry {
public:
    static TypeRegistry& Instance();
    
    // 注册类型
    void RegisterType(TypeInfo* typeInfo);
    
    // 获取类型
    TypeInfo* GetType(const std::string& name) const;
    TypeInfo* GetTypeByName(const std::string& name) const;
    
    // 获取所有已注册类型
    std::vector<TypeInfo*> GetAllTypes() const;
    
private:
    std::map<std::string, TypeInfo*> types_;
    TypeRegistry() = default;
};

// ============================================================================
// 预定义的类型
// ============================================================================

// 获取基础类型的TypeInfo
TypeInfo* GetIntTypeInfo();
TypeInfo* GetDoubleTypeInfo();
TypeInfo* GetBoolTypeInfo();
TypeInfo* GetStringTypeInfo();
TypeInfo* GetDiceTypeInfo();
TypeInfo* GetSchemaTypeInfo();
TypeInfo* GetArrayTypeInfo();
TypeInfo* GetNullTypeInfo();

}  // namespace abot

#endif  // ABOT_TYPE_SYSTEM_H
