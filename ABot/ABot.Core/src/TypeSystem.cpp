/**
 * @file TypeSystem_Implementation_Example.cpp
 * @brief TypeSystem核心实现示例
 * 
 * 这是Phase 1的实际代码实现示例
 * 演示如何建立TypeInfo和类型注册系统
 */

#include "TypeSystem.h"
#include "Value.h"
#include <iostream>
#include <stdexcept>

namespace abot {

// ============================================================================
// 前向声明
// ============================================================================
static TypeInfo* CreateIntTypeInfo();
static TypeInfo* CreateDoubleTypeInfo();
static TypeInfo* CreateBoolTypeInfo();
static TypeInfo* CreateStringTypeInfo();
static TypeInfo* CreateNullTypeInfo();
static TypeInfo* CreateDiceTypeInfo();
static TypeInfo* CreateSchemaTypeInfo();
static TypeInfo* CreateArrayTypeInfo();

// ============================================================================
// TypeRegistry 实现
// ============================================================================

TypeRegistry& TypeRegistry::Instance() {
    static TypeRegistry instance;
    return instance;
}

void TypeRegistry::RegisterType(TypeInfo* typeInfo) {
    if (!typeInfo) {
        throw std::runtime_error("Cannot register null TypeInfo");
    }
    types_[typeInfo->name] = typeInfo;
}

TypeInfo* TypeRegistry::GetType(const std::string& name) const {
    auto it = types_.find(name);
    if (it == types_.end()) {
        return nullptr;  // 类型未找到
    }
    return it->second;
}

TypeInfo* TypeRegistry::GetTypeByName(const std::string& name) const {
    return GetType(name);
}

std::vector<TypeInfo*> TypeRegistry::GetAllTypes() const {
    std::vector<TypeInfo*> result;
    for (const auto& pair : types_) {
        result.push_back(pair.second);
    }
    return result;
}

// ============================================================================
// 预定义类型的工厂函数实现
// ============================================================================

/**
 * @brief 创建Int类型的TypeInfo
 */
static TypeInfo* CreateIntTypeInfo() {
    auto typeInfo = new TypeInfo("int", TypeCategory::Primitive, sizeof(int64_t));
    
    // 构造函数：分配并初始化为0
    typeInfo->constructor = []() {
        return new int64_t(0);
    };
    
    // 析构函数：释放内存
    typeInfo->destructor = [](void* ptr) {
        delete static_cast<int64_t*>(ptr);
    };
    
    // 复制函数：深度复制
    typeInfo->copy = [](const void* src) {
        return new int64_t(*static_cast<const int64_t*>(src));
    };
    
    // 转换函数
    typeInfo->toInt = [](const void* ptr) {
        return *static_cast<const int64_t*>(ptr);
    };
    
    typeInfo->toDouble = [](const void* ptr) {
        return static_cast<double>(*static_cast<const int64_t*>(ptr));
    };
    
    typeInfo->toBool = [](const void* ptr) {
        return *static_cast<const int64_t*>(ptr) != 0;
    };
    
    typeInfo->toString = [](const void* ptr) {
        return std::to_string(*static_cast<const int64_t*>(ptr));
    };
    
    // 序列化函数
    typeInfo->serialize = [](const void* ptr) {
        return std::to_string(*static_cast<const int64_t*>(ptr));
    };
    
    // 反序列化函数
    typeInfo->deserialize = [](const std::string& str) -> void* {
        try {
            return new int64_t(std::stoll(str));
        } catch (...) {
            return new int64_t(0);
        }
    };
    
    return typeInfo;
}

/**
 * @brief 创建Double类型的TypeInfo
 */
static TypeInfo* CreateDoubleTypeInfo() {
    auto typeInfo = new TypeInfo("double", TypeCategory::Primitive, sizeof(double));
    
    typeInfo->constructor = []() { return new double(0.0); };
    typeInfo->destructor = [](void* ptr) { delete static_cast<double*>(ptr); };
    typeInfo->copy = [](const void* src) { return new double(*static_cast<const double*>(src)); };
    
    typeInfo->toInt = [](const void* ptr) { return static_cast<int64_t>(*static_cast<const double*>(ptr)); };
    typeInfo->toDouble = [](const void* ptr) { return *static_cast<const double*>(ptr); };
    typeInfo->toBool = [](const void* ptr) { return *static_cast<const double*>(ptr) != 0.0; };
    typeInfo->toString = [](const void* ptr) { return std::to_string(*static_cast<const double*>(ptr)); };
    
    typeInfo->serialize = [](const void* ptr) { return std::to_string(*static_cast<const double*>(ptr)); };
    typeInfo->deserialize = [](const std::string& str) -> void* {
        try {
            return new double(std::stod(str));
        } catch (...) {
            return new double(0.0);
        }
    };
    
    return typeInfo;
}

/**
 * @brief 创建Bool类型的TypeInfo
 */
static TypeInfo* CreateBoolTypeInfo() {
    auto typeInfo = new TypeInfo("bool", TypeCategory::Primitive, sizeof(bool));
    
    typeInfo->constructor = []() { return new bool(false); };
    typeInfo->destructor = [](void* ptr) { delete static_cast<bool*>(ptr); };
    typeInfo->copy = [](const void* src) { return new bool(*static_cast<const bool*>(src)); };
    
    typeInfo->toInt = [](const void* ptr) { return *static_cast<const bool*>(ptr) ? 1 : 0; };
    typeInfo->toDouble = [](const void* ptr) { return *static_cast<const bool*>(ptr) ? 1.0 : 0.0; };
    typeInfo->toBool = [](const void* ptr) { return *static_cast<const bool*>(ptr); };
    typeInfo->toString = [](const void* ptr) { return *static_cast<const bool*>(ptr) ? "true" : "false"; };
    
    typeInfo->serialize = [](const void* ptr) { return *static_cast<const bool*>(ptr) ? "true" : "false"; };
    typeInfo->deserialize = [](const std::string& str) -> void* {
        bool value = (str == "true" || str == "1");
        return new bool(value);
    };
    
    return typeInfo;
}

/**
 * @brief 创建String类型的TypeInfo
 */
static TypeInfo* CreateStringTypeInfo() {
    auto typeInfo = new TypeInfo("string", TypeCategory::Primitive, sizeof(std::string));
    
    typeInfo->constructor = []() { return new std::string(); };
    typeInfo->destructor = [](void* ptr) { delete static_cast<std::string*>(ptr); };
    typeInfo->copy = [](const void* src) { return new std::string(*static_cast<const std::string*>(src)); };
    
    typeInfo->toInt = [](const void* ptr) {
        try {
            return std::stoll(*static_cast<const std::string*>(ptr));
        } catch (...) {
            return 0LL;
        }
    };
    
    typeInfo->toDouble = [](const void* ptr) {
        try {
            return std::stod(*static_cast<const std::string*>(ptr));
        } catch (...) {
            return 0.0;
        }
    };
    
    typeInfo->toBool = [](const void* ptr) { 
        const auto& s = *static_cast<const std::string*>(ptr);
        return !s.empty() && s != "0" && s != "false";
    };
    
    typeInfo->toString = [](const void* ptr) { 
        return *static_cast<const std::string*>(ptr); 
    };
    
    typeInfo->serialize = [](const void* ptr) { 
        return "\"" + *static_cast<const std::string*>(ptr) + "\""; 
    };
    
    typeInfo->deserialize = [](const std::string& str) -> void* {
        // 移除引号（如果有）
        std::string value = str;
        if (value.size() >= 2 && value[0] == '"' && value[value.size()-1] == '"') {
            value = value.substr(1, value.size() - 2);
        }
        return new std::string(value);
    };
    
    return typeInfo;
}

/**
 * @brief 创建Null类型的TypeInfo
 */
static TypeInfo* CreateNullTypeInfo() {
    auto typeInfo = new TypeInfo("null", TypeCategory::Special, 0);
    
    typeInfo->constructor = []() { return nullptr; };
    typeInfo->destructor = [](void* ptr) { /* 无数据，无需释放 */ };
    typeInfo->copy = [](const void* src) { return nullptr; };
    
    typeInfo->toInt = [](const void* ptr) { return 0LL; };
    typeInfo->toDouble = [](const void* ptr) { return 0.0; };
    typeInfo->toBool = [](const void* ptr) { return false; };
    typeInfo->toString = [](const void* ptr) { return "null"; };
    
    typeInfo->serialize = [](const void* ptr) { return "null"; };
    typeInfo->deserialize = [](const std::string& str) -> void* { return nullptr; };
    
    return typeInfo;
}

// ============================================================================
// 公开工厂函数
// ============================================================================

static TypeInfo* g_intType = nullptr;
static TypeInfo* g_doubleType = nullptr;
static TypeInfo* g_boolType = nullptr;
static TypeInfo* g_stringType = nullptr;
static TypeInfo* g_nullType = nullptr;
static TypeInfo* g_diceType = nullptr;
static TypeInfo* g_schemaType = nullptr;
static TypeInfo* g_arrayType = nullptr;

// 初始化所有预定义类型
static void InitializeBuiltinTypes() {
    auto& registry = TypeRegistry::Instance();
    
    if (g_intType == nullptr) {
        g_intType = CreateIntTypeInfo();
        registry.RegisterType(g_intType);
    }
    if (g_doubleType == nullptr) {
        g_doubleType = CreateDoubleTypeInfo();
        registry.RegisterType(g_doubleType);
    }
    if (g_boolType == nullptr) {
        g_boolType = CreateBoolTypeInfo();
        registry.RegisterType(g_boolType);
    }
    if (g_stringType == nullptr) {
        g_stringType = CreateStringTypeInfo();
        registry.RegisterType(g_stringType);
    }
    if (g_nullType == nullptr) {
        g_nullType = CreateNullTypeInfo();
        registry.RegisterType(g_nullType);
    }
    if (g_diceType == nullptr) {
        g_diceType = CreateDiceTypeInfo();
        registry.RegisterType(g_diceType);
    }
    if (g_schemaType == nullptr) {
        g_schemaType = CreateSchemaTypeInfo();
        registry.RegisterType(g_schemaType);
    }
    if (g_arrayType == nullptr) {
        g_arrayType = CreateArrayTypeInfo();
        registry.RegisterType(g_arrayType);
    }
}

TypeInfo* GetIntTypeInfo() {
    InitializeBuiltinTypes();
    return g_intType;
}

TypeInfo* GetDoubleTypeInfo() {
    InitializeBuiltinTypes();
    return g_doubleType;
}

TypeInfo* GetBoolTypeInfo() {
    InitializeBuiltinTypes();
    return g_boolType;
}

TypeInfo* GetStringTypeInfo() {
    InitializeBuiltinTypes();
    return g_stringType;
}

TypeInfo* GetNullTypeInfo() {
    InitializeBuiltinTypes();
    return g_nullType;
}

/**
 * @brief 创建Dice（骰子）类型的TypeInfo
 * 
 * 骰子格式: #<count>d<faces>
 * 例子: #1d20（1个20面骰子）, #2d6（2个6面骰子）
 */
static TypeInfo* CreateDiceTypeInfo() {
    // 注意：Dice类型需要自定义数据结构
    // 这里假设使用 std::string 存储骰子描述，实际应该使用结构体
    auto typeInfo = new TypeInfo("dice", TypeCategory::Special, 0);
    
    typeInfo->constructor = []() {
        return new std::string("#1d20");  // 默认：1个20面骰子
    };
    
    typeInfo->destructor = [](void* ptr) {
        delete static_cast<std::string*>(ptr);
    };
    
    typeInfo->copy = [](const void* src) {
        return new std::string(*static_cast<const std::string*>(src));
    };
    
    typeInfo->toInt = [](const void* ptr) {
        // 尝试掷骰子并返回结果 (简化版：返回1到20之间的随机数)
        // 实际实现应该解析#<count>d<faces>并计算结果
        return 0LL;  // TODO: 实现骰子计算
    };
    
    typeInfo->toDouble = [](const void* ptr) { return 0.0; };
    typeInfo->toBool = [](const void* ptr) { return true; };  // 骰子总是有效的
    
    typeInfo->toString = [](const void* ptr) {
        return *static_cast<const std::string*>(ptr);
    };
    
    typeInfo->serialize = [](const void* ptr) {
        return *static_cast<const std::string*>(ptr);
    };
    
    typeInfo->deserialize = [](const std::string& str) -> void* {
        // 验证格式：#<count>d<faces>
        if (str[0] == '#') {
            return new std::string(str);
        } else {
            return new std::string("#1d20");  // 默认值
        }
    };
    
    return typeInfo;
}

/**
 * @brief 创建Schema（映射表）类型的TypeInfo
 * 
 * Schema是键值对的集合：{key1=value1, key2=value2, ...}
 */
static TypeInfo* CreateSchemaTypeInfo() {
    // 注意：Schema类型内部存储应该使用 std::map<std::string, Value>
    // 这里假设使用 std::string 存储序列化的Schema表示
    auto typeInfo = new TypeInfo("schema", TypeCategory::Composite, 0);
    
    typeInfo->constructor = []() {
        return new std::string("{}");  // 空Schema
    };
    
    typeInfo->destructor = [](void* ptr) {
        delete static_cast<std::string*>(ptr);
    };
    
    typeInfo->copy = [](const void* src) {
        return new std::string(*static_cast<const std::string*>(src));
    };
    
    typeInfo->toInt = [](const void* ptr) { return 0LL; };
    typeInfo->toDouble = [](const void* ptr) { return 0.0; };
    
    typeInfo->toBool = [](const void* ptr) {
        const auto& s = *static_cast<const std::string*>(ptr);
        return s != "{}";  // 非空Schema为true
    };
    
    typeInfo->toString = [](const void* ptr) {
        return *static_cast<const std::string*>(ptr);
    };
    
    typeInfo->serialize = [](const void* ptr) {
        return *static_cast<const std::string*>(ptr);
    };
    
    typeInfo->deserialize = [](const std::string& str) -> void* {
        // 验证基本格式
        if (!str.empty() && str[0] == '{' && str[str.size()-1] == '}') {
            return new std::string(str);
        } else {
            return new std::string("{}");  // 默认值
        }
    };
    
    // Schema特定操作 (TODO: 这些需要真实实现)
    typeInfo->setField = [](void* ptr, const std::string& key, const Value& value) {
        // TODO: 实现字段设置
    };
    
    typeInfo->getField = [](const void* ptr, const std::string& key) -> Value {
        // TODO: 实现字段获取，返回默认null
        return Value();  // 当前返回null
    };
    
    typeInfo->removeField = [](void* ptr, const std::string& key) {
        // TODO: 实现字段删除
    };
    
    typeInfo->getKeys = [](const void* ptr) -> std::vector<std::string> {
        // TODO: 实现获取所有键
        return std::vector<std::string>();
    };
    
    return typeInfo;
}

/**
 * @brief 创建Array（数组）类型的TypeInfo
 * 
 * Array是有序的值集合：[value1, value2, value3, ...]
 */
static TypeInfo* CreateArrayTypeInfo() {
    // 注意：Array类型内部存储应该使用 std::vector<Value>
    // 这里假设使用 std::string 存储序列化的Array表示
    auto typeInfo = new TypeInfo("array", TypeCategory::Composite, 0);
    
    typeInfo->constructor = []() {
        return new std::string("[]");  // 空数组
    };
    
    typeInfo->destructor = [](void* ptr) {
        delete static_cast<std::string*>(ptr);
    };
    
    typeInfo->copy = [](const void* src) {
        return new std::string(*static_cast<const std::string*>(src));
    };
    
    typeInfo->toInt = [](const void* ptr) {
        const auto& s = *static_cast<const std::string*>(ptr);
        // 数组大小作为整数
        int count = 0;
        for (char c : s) {
            if (c == ',') count++;
        }
        return (s == "[]") ? 0 : count + 1;
    };
    
    typeInfo->toDouble = [](const void* ptr) {
        return static_cast<double>(
            static_cast<const TypeInfo*>(nullptr)->toInt(ptr)  // 调用当前toInt
        );
    };
    
    typeInfo->toBool = [](const void* ptr) {
        const auto& s = *static_cast<const std::string*>(ptr);
        return s != "[]";  // 非空数组为true
    };
    
    typeInfo->toString = [](const void* ptr) {
        return *static_cast<const std::string*>(ptr);
    };
    
    typeInfo->serialize = [](const void* ptr) {
        return *static_cast<const std::string*>(ptr);
    };
    
    typeInfo->deserialize = [](const std::string& str) -> void* {
        // 验证基本格式
        if (!str.empty() && str[0] == '[' && str[str.size()-1] == ']') {
            return new std::string(str);
        } else {
            return new std::string("[]");  // 默认值
        }
    };
    
    // Array特定操作 (TODO: 这些需要真实实现)
    typeInfo->getElement = [](const void* ptr, size_t index) -> Value {
        // TODO: 实现元素获取
        return Value();  // 当前返回null
    };
    
    typeInfo->setElement = [](void* ptr, size_t index, const Value& value) {
        // TODO: 实现元素设置
    };
    
    typeInfo->pushBack = [](void* ptr, const Value& value) {
        // TODO: 实现追加元素
    };
    
    typeInfo->popBack = [](void* ptr) {
        // TODO: 实现弹出最后一个元素
    };
    
    typeInfo->getSize = [](const void* ptr) -> size_t {
        const auto& s = *static_cast<const std::string*>(ptr);
        if (s == "[]") return 0;
        // 粗略计数：数组元素个数 = 逗号数 + 1
        size_t count = 1;
        for (char c : s) {
            if (c == ',') count++;
        }
        return count;
    };
    
    return typeInfo;
}

TypeInfo* GetDiceTypeInfo() {
    InitializeBuiltinTypes();
    return g_diceType;
}

TypeInfo* GetSchemaTypeInfo() {
    InitializeBuiltinTypes();
    return g_schemaType;
}

TypeInfo* GetArrayTypeInfo() {
    InitializeBuiltinTypes();
    return g_arrayType;
}

}  // namespace abot

// ============================================================================
// 使用示例
// ============================================================================

#ifdef EXAMPLE_USAGE
int main() {
    // 获取类型信息
    auto intType = abot::GetIntTypeInfo();
    auto stringType = abot::GetStringTypeInfo();
    
    // 创建值
    void* intVal = intType->constructor();
    *(int64_t*)intVal = 42;
    
    void* strVal = stringType->constructor();
    *(std::string*)strVal = "hello";
    
    // 类型转换和序列化
    std::cout << "Int value: " << intType->toString(intVal) << std::endl;         // 42
    std::cout << "As double: " << intType->toDouble(intVal) << std::endl;        // 42.0
    std::cout << "Serialized: " << intType->serialize(intVal) << std::endl;      // 42
    
    std::cout << "String value: " << stringType->toString(strVal) << std::endl;  // hello
    std::cout << "Serialized: " << stringType->serialize(strVal) << std::endl;   // "hello"
    
    // 复制
    void* intValCopy = intType->copy(intVal);
    std::cout << "Copy: " << intType->toString(intValCopy) << std::endl;         // 42
    
    // 清理
    intType->destructor(intVal);
    intType->destructor(intValCopy);
    stringType->destructor(strVal);
    
    return 0;
}
#endif
