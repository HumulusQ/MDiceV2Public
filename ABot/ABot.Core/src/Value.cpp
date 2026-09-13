/**
 * @file Value.cpp
 * @brief 动态值类型系统的实现
 */

#include "Value.h"
#include "SchemaValue.h"
#include "ArrayValue.h"
#include "ExecutionEnvironment.h"
#include "ObjectTable.h"
#include "TypeSystem.h"
#include <cmath>
#include <sstream>
#include <stdexcept>
#include <algorithm>
#include <random>

namespace abot {

// ============ Value 构造函数 ============

Value::Value() 
    : type_(ValueType::Null), int_value_(0), string_value_(nullptr),
      dice_value_(nullptr), schema_value_(nullptr), array_value_(nullptr),
      type_info_(nullptr), schema_handle_(),
      owner_handle_(), owner_path_("") {}

Value::Value(int64_t i)
    : type_(ValueType::Int), int_value_(i), string_value_(nullptr),
      dice_value_(nullptr), schema_value_(nullptr), array_value_(nullptr),
      type_info_(nullptr), schema_handle_(),
      owner_handle_(), owner_path_("") {}

Value::Value(double val) 
    : type_(ValueType::Double), double_value_(val), string_value_(nullptr),
      dice_value_(nullptr), schema_value_(nullptr), array_value_(nullptr),
      type_info_(nullptr), schema_handle_(),
      owner_handle_(), owner_path_("") {}

Value::Value(bool val) 
    : type_(ValueType::Bool), bool_value_(val), string_value_(nullptr),
      dice_value_(nullptr), schema_value_(nullptr), array_value_(nullptr),
      type_info_(nullptr), schema_handle_(),
      owner_handle_(), owner_path_("") {}

Value::Value(const std::string& val)
    : type_(ValueType::String), int_value_(0),
      string_value_(new std::string(val)),
      dice_value_(nullptr), schema_value_(nullptr), array_value_(nullptr),
      type_info_(nullptr), schema_handle_(),
      owner_handle_(), owner_path_("") {}

Value::Value(const char* val)
    : type_(ValueType::String), int_value_(0),
      string_value_(new std::string(val)),
      dice_value_(nullptr), schema_value_(nullptr), array_value_(nullptr),
      type_info_(nullptr), schema_handle_(),
      owner_handle_(), owner_path_("") {}

Value::Value(std::nullptr_t)
    : type_(ValueType::Null), int_value_(0), string_value_(nullptr),
      dice_value_(nullptr), schema_value_(nullptr), array_value_(nullptr),
      type_info_(nullptr), schema_handle_(),
      owner_handle_(), owner_path_("") {}

// ============ Value 拷贝和移动 ============

Value::Value(const Value& other) 
    : type_(other.type_), int_value_(other.int_value_),
      string_value_(nullptr), dice_value_(nullptr),
      schema_value_(nullptr), array_value_(nullptr), type_info_(nullptr),
      schema_handle_(other.schema_handle_),
      owner_handle_(other.owner_handle_), owner_path_(other.owner_path_) {  // ← Handle PoC: 保留 handle 而非深拷贝！
    if (other.string_value_) {
        string_value_ = new std::string(*other.string_value_);
    }
    
    // ✅ Handle 模式下不深拷贝，仅保留 handle
    // Legacy 模式下执行深拷贝（向后兼容）
    if (!other.schema_handle_.IsNull()) {
        // Handle 模式：schema_handle_ 已在初始化列表中复制，不需要深拷贝 schema_value_
        schema_value_ = nullptr;
    } else if (other.schema_value_) {
        // Legacy 模式：深拷贝 schema_value_（旧行为）
        schema_value_ = new SchemaValue(*other.schema_value_);
    }
    
    // ✅ 深拷贝 dice_value_
    if (other.dice_value_) {
        dice_value_ = new DiceValue(*other.dice_value_);
    }
    
    // ✅ 深拷贝 array_value_
    if (other.array_value_) {
        array_value_ = new ArrayValue(*other.array_value_);
    }
}

Value::Value(Value&& other) noexcept 
    : type_(other.type_), int_value_(other.int_value_),
      string_value_(other.string_value_),
      dice_value_(other.dice_value_),
      schema_value_(other.schema_value_),
      array_value_(other.array_value_),
      type_info_(nullptr),
      schema_handle_(other.schema_handle_),
      owner_handle_(other.owner_handle_),
      owner_path_(other.owner_path_) {  // ← 移动 handle
    other.string_value_ = nullptr;
    other.dice_value_ = nullptr;
    other.schema_value_ = nullptr;
    other.array_value_ = nullptr;
    other.schema_handle_ = ObjectHandle();  // 清除源的 handle
}

Value& Value::operator=(const Value& other) {
    if (this != &other) {
        Clear();
        type_ = other.type_;
        int_value_ = other.int_value_;
        schema_handle_ = other.schema_handle_;  // ← 复制 handle（PoC 关键）
        owner_handle_ = other.owner_handle_;    // ← 复制 owner_handle
        owner_path_ = other.owner_path_;        // ← 复制 owner_path
        
        if (other.string_value_) {
            string_value_ = new std::string(*other.string_value_);
        }
        
        // ✅ Handle 模式下不深拷贝，仅保留 handle
        // Legacy 模式下执行深拷贝（向后兼容）
        if (!other.schema_handle_.IsNull()) {
            // Handle 模式：schema_handle_ 已复制，不需要深拷贝 schema_value_
            schema_value_ = nullptr;
        } else if (other.schema_value_) {
            // Legacy 模式：深拷贝 schema_value_（旧行为）
            schema_value_ = new SchemaValue(*other.schema_value_);
        }
        
        // ✅ 深拷贝 dice_value_
        if (other.dice_value_) {
            dice_value_ = new DiceValue(*other.dice_value_);
        }
        
        // ✅ 深拷贝 array_value_
        if (other.array_value_) {
            array_value_ = new ArrayValue(*other.array_value_);
        }
    }
    return *this;
}

Value& Value::operator=(Value&& other) noexcept {
    if (this != &other) {
        Clear();
        type_ = other.type_;
        int_value_ = other.int_value_;
        string_value_ = other.string_value_;
        dice_value_ = other.dice_value_;
        schema_value_ = other.schema_value_;
        array_value_ = other.array_value_;
        schema_handle_ = other.schema_handle_;  // ← 移动 handle
        owner_handle_ = other.owner_handle_;    // ← 移动 owner_handle
        owner_path_ = other.owner_path_;        // ← 移动 owner_path
        
        other.string_value_ = nullptr;
        other.dice_value_ = nullptr;
        other.schema_value_ = nullptr;
        other.array_value_ = nullptr;
        other.schema_handle_ = ObjectHandle();  // 清除源的 handle
    }
    return *this;
}

Value::~Value() {
    Clear();
}

void Value::Clear() {
    if (string_value_) {
        delete string_value_;
        string_value_ = nullptr;
    }
    if (dice_value_) {
        delete dice_value_;
        dice_value_ = nullptr;
    }
    if (schema_value_) {
        delete schema_value_;
        schema_value_ = nullptr;
    }
    if (array_value_) {
        delete array_value_;
        array_value_ = nullptr;
    }
    // 🟥【Phase 1 任务】清除 owner/path 信息
    owner_handle_ = ObjectHandle();
    owner_path_ = "";
}

// ============ 类型转换 ============

int64_t Value::ToInt() const {
    switch (type_) {
        case ValueType::Int:
            return int_value_;
        case ValueType::Double:
            return static_cast<int64_t>(double_value_);
        case ValueType::Bool:
            return bool_value_ ? 1 : 0;
        case ValueType::String:
            if (string_value_) {
                try {
                    return std::stoll(*string_value_);
                } catch (...) {
                    return 0;
                }
            }
            return 0;
        default:
            return 0;
    }
}

double Value::ToDouble() const {
    switch (type_) {
        case ValueType::Int:
            return static_cast<double>(int_value_);
        case ValueType::Double:
            return double_value_;
        case ValueType::Bool:
            return bool_value_ ? 1.0 : 0.0;
        case ValueType::String:
            if (string_value_) {
                try {
                    return std::stod(*string_value_);
                } catch (...) {
                    return 0.0;
                }
            }
            return 0.0;
        default:
            return 0.0;
    }
}

bool Value::ToBool() const {
    switch (type_) {
        case ValueType::Null:
            return false;
        case ValueType::Int:
            return int_value_ != 0;
        case ValueType::Double:
            return double_value_ != 0.0;
        case ValueType::Bool:
            return bool_value_;
        case ValueType::String:
            return string_value_ && !string_value_->empty();
        default:
            return false;
    }
}

std::string Value::ToString() const {
    std::string result;
    switch (type_) {
        case ValueType::Null:
            result = "null";
            break;
        case ValueType::Int:
            result = std::to_string(int_value_);
            break;
        case ValueType::Double: {
            std::ostringstream oss;
            oss << double_value_;
            result = oss.str();
            break;
        }
        case ValueType::Bool:
            result = bool_value_ ? "true" : "false";
            break;
        case ValueType::String:
            result = string_value_ ? *string_value_ : "";
            break;
        case ValueType::Dice:
            result = dice_value_ ? dice_value_->ToString() : "";
            break;
        case ValueType::Schema:
            result = schema_value_ ? schema_value_->ToString() : "";
            break;
        case ValueType::Array:
            result = array_value_ ? array_value_->ToString() : "";
            break;
        case ValueType::Handle:
            result = "[Handle]";
            break;
        default:
            result = "[Unknown]";
            break;
    }
    
    // 🟥【Phase 1 任务】添加 owner/path 调试信息
    if (!owner_handle_.IsNull() || !owner_path_.empty()) {
        result += " {owner_handle:" + owner_handle_.ToString() + ", owner_path:" + owner_path_ + "}";
    }
    
    return result;
}

// ============ Schema 操作 ============

Value Value::GetField(const std::string& key) const {
    if (IsHandle()) {
        ExecutionEnvironment* env = ExecutionEnvironment::Current();
        ObjectTable* obj_table = env ? env->GetObjectTable() : nullptr;
        SchemaValue* handle_schema = GetSchemaValuePtr(obj_table);
        if (handle_schema) {
            return handle_schema->GetField(key);
        }
        return Value();
    }
    if (type_ != ValueType::Schema || !schema_value_) {
        return Value();
    }
    return schema_value_->GetField(key);
}

void Value::SetField(const std::string& key, const Value& value) {
    if (IsHandle()) {
        // 禁止 handle 模式下走这里，必须由 VM/TableSet 直接写 ObjectTable
        ExecutionEnvironment* env = ExecutionEnvironment::Current();
        if (env) {
            env->AppendDiagnosticLog("[Value::SetField] Ignored SetField on handle-backed Value for key=" + key);
        }
        return;
    }
    if (type_ != ValueType::Schema) {
        Clear();
        type_ = ValueType::Schema;
        schema_value_ = new SchemaValue();
    }
    if (schema_value_) {
        schema_value_->SetField(key, value);
    }
}

bool Value::ContainsField(const std::string& key) const {
    if (IsHandle()) {
        ExecutionEnvironment* env = ExecutionEnvironment::Current();
        ObjectTable* obj_table = env ? env->GetObjectTable() : nullptr;
        SchemaValue* handle_schema = GetSchemaValuePtr(obj_table);
        return handle_schema ? handle_schema->HasField(key) : false;
    }
    if (type_ != ValueType::Schema || !schema_value_) {
        return false;
    }
    return schema_value_->HasField(key);
}

bool Value::HasField(const std::string& key) const {
    // 【修复】HasField 是 ContainsField 的别名/包装器
    // 确保支持 Handle-backed schema 和 Legacy schema_value_ 两种模式
    if (IsHandle()) {
        ExecutionEnvironment* env = ExecutionEnvironment::Current();
        ObjectTable* obj_table = env ? env->GetObjectTable() : nullptr;
        SchemaValue* handle_schema = GetSchemaValuePtr(obj_table);
        return handle_schema ? handle_schema->HasField(key) : false;
    }
    if (type_ != ValueType::Schema || !schema_value_) {
        return false;
    }
    return schema_value_->HasField(key);
}

std::unordered_map<std::string, Value>& Value::GetAllFields() {
    if (type_ != ValueType::Schema) {
        Clear();
        type_ = ValueType::Schema;
        schema_value_ = new SchemaValue();
    }
    if (!schema_value_) {
        schema_value_ = new SchemaValue();
    }
    return schema_value_->GetAllFieldsMutable();
}

const std::unordered_map<std::string, Value>& Value::GetAllFields() const {
    if (IsHandle()) {
        ExecutionEnvironment* env = ExecutionEnvironment::Current();
        ObjectTable* obj_table = env ? env->GetObjectTable() : nullptr;
        SchemaValue* handle_schema = GetSchemaValuePtr(obj_table);
        if (handle_schema) {
            return handle_schema->GetAllFields();
        }
    }
    if (type_ != ValueType::Schema || !schema_value_) {
        static const std::unordered_map<std::string, Value> empty_fields;
        return empty_fields;
    }
    return schema_value_->GetAllFields();
}

// ============ Array 操作 ============

Value Value::GetElement(size_t index) const {
    if (type_ != ValueType::Array || !array_value_) {
        return Value();
    }
    return array_value_->GetElement(index);
}

void Value::SetElement(size_t index, const Value& value) {
    if (type_ != ValueType::Array) {
        Clear();
        type_ = ValueType::Array;
        array_value_ = new ArrayValue();
    }
    if (array_value_) {
        array_value_->SetElement(index, value);
    }
}

void Value::AppendElement(const Value& value) {
    if (type_ != ValueType::Array) {
        Clear();
        type_ = ValueType::Array;
        array_value_ = new ArrayValue();
    }
    if (array_value_) {
        array_value_->AppendElement(value);
    }
}

size_t Value::ArraySize() const {
    if (type_ != ValueType::Array || !array_value_) {
        return 0;
    }
    return array_value_->GetSize();
}

// ============ 运算符 ============

Value Value::operator+(const Value& other) const {
    if (type_ == ValueType::Int && other.type_ == ValueType::Int) {
        return Value(int_value_ + other.int_value_);
    }
    double a = ToDouble();
    double b = other.ToDouble();
    return Value(a + b);
}

Value Value::operator-(const Value& other) const {
    if (type_ == ValueType::Int && other.type_ == ValueType::Int) {
        return Value(int_value_ - other.int_value_);
    }
    double a = ToDouble();
    double b = other.ToDouble();
    return Value(a - b);
}

Value Value::operator*(const Value& other) const {
    if (type_ == ValueType::Int && other.type_ == ValueType::Int) {
        return Value(int_value_ * other.int_value_);
    }
    double a = ToDouble();
    double b = other.ToDouble();
    return Value(a * b);
}

Value Value::operator/(const Value& other) const {
    double a = ToDouble();
    double b = other.ToDouble();
    if (std::abs(b) < 1e-10) {
        throw std::runtime_error("Division by zero");
    }
    return Value(a / b);
}

bool Value::operator==(const Value& other) const {
    if (type_ != other.type_) return false;
    switch (type_) {
        case ValueType::Null: return true;
        case ValueType::Int: return int_value_ == other.int_value_;
        case ValueType::Double: return std::abs(double_value_ - other.double_value_) < 1e-10;
        case ValueType::Bool: return bool_value_ == other.bool_value_;
        case ValueType::String:
            return (string_value_ == nullptr && other.string_value_ == nullptr) ||
                   (string_value_ && other.string_value_ && *string_value_ == *other.string_value_);
        default:
            return false;
    }
}

bool Value::operator!=(const Value& other) const {
    return !(*this == other);
}

bool Value::operator<(const Value& other) const {
    if (type_ != other.type_) return false;
    switch (type_) {
        case ValueType::Int: return int_value_ < other.int_value_;
        case ValueType::Double: return double_value_ < other.double_value_;
        case ValueType::String:
            if (string_value_ && other.string_value_) {
                return *string_value_ < *other.string_value_;
            }
            return false;
        default:
            return false;
    }
}

bool Value::operator<=(const Value& other) const {
    return *this < other || *this == other;
}

bool Value::operator>(const Value& other) const {
    return other < *this;
}

bool Value::operator>=(const Value& other) const {
    return other <= *this;
}

bool Value::IsSameSchemaObject(const Value& other) const {
    // ★【身份判断】指针比较
    // 两个Value的schema_value_指向同一块内存，说明是同一个对象
    // 注意：这依赖于Value的赋值不重新创建SchemaValue
    
    if (type_ != ValueType::Schema || other.type_ != ValueType::Schema) {
        return false;
    }
    
    // 比较内部指针
    return schema_value_ == other.schema_value_;
}

// ============ 工厂方法 ============

Value Value::CreateDice(int faces, int count) {
    Value v;
    v.type_ = ValueType::Dice;
    v.dice_value_ = new DiceValue(faces, count);
    return v;
}

Value Value::CreateSchema() {
    Value v;
    v.type_ = ValueType::Schema;
    v.schema_value_ = new SchemaValue();
    return v;
}

Value Value::CreateArray() {
    Value v;
    v.type_ = ValueType::Array;
    v.array_value_ = new ArrayValue();
    return v;
}

// ============ DiceValue 实现 ============

int DiceValue::Roll() const {
    static std::random_device rd;
    static std::mt19937 gen(rd());
    
    int total = 0;
    for (int i = 0; i < count_; ++i) {
        std::uniform_int_distribution<> dis(1, faces_);
        total += dis(gen);
    }
    return total;
}

std::string DiceValue::ToString() const {
    return std::to_string(count_) + "d" + std::to_string(faces_);
}

// ============ ArrayValue 实现 ============

Value ArrayValue::GetElement(size_t index) const {
    if (index >= elements_.size()) {
        return Value();
    }
    return elements_[index];
}

void ArrayValue::SetElement(size_t index, const Value& value) {
    if (index >= elements_.size()) {
        elements_.resize(index + 1);
    }
    elements_[index] = value;
}

void ArrayValue::AppendElement(const Value& value) {
    elements_.push_back(value);
}

void ArrayValue::PushBack(const Value& value) {
    elements_.push_back(value);
}

void ArrayValue::PopBack() {
    if (!elements_.empty()) {
        elements_.pop_back();
    }
}

size_t ArrayValue::GetSize() const {
    return elements_.size();
}

std::string ArrayValue::ToString() const {
    std::ostringstream oss;
    oss << "[ ";
    bool first = true;
    for (const auto& elem : elements_) {
        if (!first) oss << ", ";
        oss << elem.ToString();
        first = false;
    }
    oss << " ]";
    return oss.str();
}

// ============ TypeInfo系统支持（Phase 1升级） ============

const TypeInfo* Value::GetTypeInfo() const {
    // 根据当前type_值返回对应的TypeInfo
    // 这实现了从旧ValueType枚举系统到新TypeInfo系统的映射
    
    switch (type_) {
        case ValueType::Null:
            return GetNullTypeInfo();
        case ValueType::Int:
            return GetIntTypeInfo();
        case ValueType::Double:
            return GetDoubleTypeInfo();
        case ValueType::Bool:
            return GetBoolTypeInfo();
        case ValueType::String:
            return GetStringTypeInfo();
        case ValueType::Dice:
            return GetDiceTypeInfo();
        case ValueType::Schema:
            return GetSchemaTypeInfo();
        case ValueType::Array:
            return GetArrayTypeInfo();
        default:
            return GetNullTypeInfo();  // 未知类型返回null
    }
}

Value Value::ConvertByTypeInfo(const TypeInfo* target_type) const {
    if (target_type == nullptr) {
        return Value();  // 返回null
    }
    
    // 使用TypeInfo提供的转换函数
    // 这是一个通用的转换框架，具体实现需要target_type提供的函数指针
    
    try {
        // 如果目标类型和现在类型一致，直接返回副本
        if (this->GetTypeInfo()->name == target_type->name) {
            return Value(*this);
        }
        
        // 否则，尝试进行类型转换
        // 对于基础类型，使用ToString中间转换
        std::string intermediate = this->ToString();
        
        // 创建一个新值并通过TypeInfo反序列化
        Value result;
        // TODO: 使用target_type->deserialize实现真正的转换
        // 目前作为框架，具体实现待完成
        
        return result;
    } catch (...) {
        return Value();  // 转换失败返回null
    }
}

// ============ Handle 系统支持 ============

SchemaValue* Value::GetSchemaValuePtr(ObjectTable* object_table) const {
    // PoC: 支持 handle 和 legacy 两种模式
    
    if (!schema_handle_.IsNull()) {
        if (object_table == nullptr) {
            ExecutionEnvironment* env = ExecutionEnvironment::Current();
            object_table = env ? env->GetObjectTable() : nullptr;
        }
        if (object_table != nullptr) {
            try {
                return &object_table->Get(schema_handle_);
            } catch (const std::exception&) {
                // Handle 无效，降级到 legacy 模式
                return schema_value_;
            }
        }
        return schema_value_;
    }
    
    // Legacy 模式：直接返回指针
    return schema_value_;
}

}  // namespace abot
