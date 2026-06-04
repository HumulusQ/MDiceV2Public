/**
 * @file Value.h
 * @brief ABOT 值类型系统 - 支持动态类型和自动转换
 * 
 * 值系统设计原则：
 * ================
 * 1. 所有值都用统一的Value结构表示
 * 2. 支持lazy evaluation（按需计算）
 * 3. 自动类型转换（int ↔ double ↔ string）
 * 4. 特殊类型：Dice(骰子)、Schema(映射表)、Array(数组)
 * 5. 新增TypeInfo系统支持（Phase 1升级）
 */

#ifndef ABOT_VALUE_H
#define ABOT_VALUE_H

#include <cstdint>
#include <string>
#include <vector>
#include <unordered_map>
#include "ObjectHandle.h"

namespace abot {

// 前向声明
struct TypeInfo;
class Value;
class DiceValue;
class SchemaValue;
class ArrayValue;
class ObjectTable;

// 值类型枚举（保留向后兼容性）
enum class ValueType : int {
    Null,       // 空值
    Int,        // 整数
    Double,     // 浮点数
    Bool,       // 布尔值
    String,     // 字符串
    Dice,       // 骰子类型 (e.g., #1d20)
    Schema,     // 映射表 (e.g., {name=value, ...})
    Array,      // 数组
    Function,   // 函数引用
    Handle,     // 🟥【新增】纯 handle 类型（不是 schema）
};

/**
 * @brief ABOT值的通用表示
 * 
 * 使用标记联合(tagged union)模式，支持多种数据类型
 */
class Value {
public:
    // ============ 构造函数 ============
    Value();                              // 默认构造 → Null
    explicit Value(std::nullptr_t);       // 显式构造 Null
    explicit Value(int64_t i);            // 构造 Int
    explicit Value(double d);             // 构造 Double
    explicit Value(bool b);               // 构造 Bool
    explicit Value(const std::string& s); // 构造 String
    explicit Value(const char* s);        // C字符串 → String

    // 拷贝和移动
    Value(const Value& other);
    Value(Value&& other) noexcept;
    Value& operator=(const Value& other);
    Value& operator=(Value&& other) noexcept;

    // 析构函数
    ~Value();

    // ============ 类型判断 ============
    ValueType GetType() const { return type_; }
    bool IsNull() const { return type_ == ValueType::Null; }
    bool IsInt() const { return type_ == ValueType::Int; }
    bool IsDouble() const { return type_ == ValueType::Double; }
    bool IsBool() const { return type_ == ValueType::Bool; }
    bool IsString() const { return type_ == ValueType::String; }
    bool IsDice() const { return type_ == ValueType::Dice; }
    
    // 🟥【任务1.2】IsSchema 只检查 type_，确保纯 handle 不会被误判为 schema
    bool IsSchema() const {
        return type_ == ValueType::Schema;
    }
    
    // 🟥【任务1】IsHandle 用来判断是否有 handle
    // 注意：纯 handle 时 IsHandle=true, IsSchema=false
    // 而之前的代码把两者都为 true
    bool IsPureHandle() const {
        return type_ == ValueType::Handle && !schema_handle_.IsNull();
    }
    bool IsArray() const { return type_ == ValueType::Array; }

    // ============ 类型转换 ============
    int64_t ToInt() const;
    double ToDouble() const;
    bool ToBool() const;
    std::string ToString() const;

    // ============ 数据访问 ============
    // 直接访问成员（谨慎使用）
    int64_t GetInt() const { return int_value_; }
    double GetDouble() const { return double_value_; }
    bool GetBool() const { return bool_value_; }
    std::string GetString() const { return string_value_ ? *string_value_ : std::string(); }

    // Schema操作
    Value GetField(const std::string& key) const;
    void SetField(const std::string& key, const Value& value);
    bool ContainsField(const std::string& key) const;
    bool HasField(const std::string& key) const;  // ← 【修复】新添加的 HasField 方法
    std::unordered_map<std::string, Value>& GetAllFields();
    const std::unordered_map<std::string, Value>& GetAllFields() const;

    // Array操作
    Value GetElement(size_t index) const;
    void SetElement(size_t index, const Value& value);
    void AppendElement(const Value& value);
    size_t ArraySize() const;

    // ============ 运算符重载 ============
    Value operator+(const Value& other) const;
    Value operator-(const Value& other) const;
    Value operator*(const Value& other) const;
    Value operator/(const Value& other) const;
    bool operator==(const Value& other) const;
    bool operator!=(const Value& other) const;
    bool operator<(const Value& other) const;
    bool operator<=(const Value& other) const;
    bool operator>(const Value& other) const;
    bool operator>=(const Value& other) const;

    // ============ TypeInfo系统支持（Phase 1升级） ============
    const TypeInfo* GetTypeInfo() const;
    Value ConvertByTypeInfo(const TypeInfo* target_type) const;

    bool IsSameSchemaObject(const Value& other) const;

    // ============ Handle系统支持（PoC） ============
    // 🟥【关键修复】区分纯 handle 和 schema
    bool IsHandle() const { return !schema_handle_.IsNull(); }
    ObjectHandle GetHandle() const { return schema_handle_; }
    
    // SetHandle：设置为纯 handle（IsHandle=1, IsSchema=0）
    // ✅ 设置 type_ = Handle，实现"纯 handle"
    void SetHandle(const ObjectHandle& handle) {
        schema_handle_ = handle;
        type_ = ValueType::Handle;  // 🟥【关键】必须是 Handle，不是 Schema
        // 硬日志由调用者在 cpp 文件中实现
    }
    
    // SetSchemaHandle：设置为 schema+handle（IsHandle=1, IsSchema=1）
    // 用于兼容旧代码
    void SetSchemaHandle(const ObjectHandle& handle) {
        schema_handle_ = handle;
        type_ = ValueType::Schema;  // ✅ 这个用于 schema 副本
    }
    
    void ClearHandle() {
        schema_handle_ = ObjectHandle();
    }
    SchemaValue* GetSchemaValuePtr(ObjectTable* object_table = nullptr) const;

    // ============ 🟥【declare 系统】owner/path 跟踪 ============
    // 用于 TABLE_ACCESS / TABLE_SET 的回写机制
    // 当读取嵌套字段时，记录所属 handle 和路径
    // 这样 TABLE_SET 时能正确写回 ObjectTable
    
    bool HasOwner() const {
        return !owner_handle_.IsNull() || !owner_path_.empty();
    }
    
    ObjectHandle GetOwnerHandle() const { return owner_handle_; }
    void SetOwnerHandle(const ObjectHandle& h) { owner_handle_ = h; }
    
    std::string GetOwnerPath() const { return owner_path_; }
    void SetOwnerPath(const std::string& p) { owner_path_ = p; }
    
    // 清空 owner 信息
    void ClearOwner() {
        owner_handle_ = ObjectHandle();
        owner_path_ = "";
    }

    // ============ 工厂方法 ============
    static Value CreateDice(int faces, int count = 1);
    static Value CreateSchema();
    static Value CreateArray();

private:
    mutable const TypeInfo* type_info_;
    ValueType type_;
    union {
        int64_t int_value_;
        double double_value_;
        bool bool_value_;
    };
    std::string* string_value_;
    DiceValue* dice_value_;
    SchemaValue* schema_value_;
    ArrayValue* array_value_;
    ObjectHandle schema_handle_;
    
    // 🟥【declare 系统】owner/path 跟踪
    ObjectHandle owner_handle_;     // 字段所属的 ObjectTable handle
    std::string owner_path_;        // 字段在 schema 中的路径
    
    void Clear();
};

class DiceValue {
private:
    int faces_;
    int count_;
public:
    DiceValue(int f, int c = 1) : faces_(f), count_(c) {}
    int GetFaces() const { return faces_; }
    int GetCount() const { return count_; }
    int Roll() const;
    std::string ToString() const;
};

}  // namespace abot

#endif  // ABOT_VALUE_H
