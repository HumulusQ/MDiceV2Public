#include "ValueV2.h"
#include "TypeSystem.h"
#include <sstream>
#include <algorithm>
#include <cctype>

namespace abot {

// ============================================================================
// SchemaValueV2 实现
// ============================================================================
// 注意：SchemaValueV2() 构造函数已在头文件中 inline 定义

ValueV2 SchemaValueV2::GetField(const std::string& key) const {
    auto it = fields_->find(key);
    if (it != fields_->end()) {
        return it->second;
    }
    return ValueV2();
}

void SchemaValueV2::SetField(const std::string& key, const ValueV2& value) {
    (*fields_)[key] = value;
}

void SchemaValueV2::RemoveField(const std::string& key) {
    fields_->erase(key);
}

void SchemaValueV2::AddField(const std::string& key, const ValueV2& value) {
    (*fields_)[key] = value;
}

bool SchemaValueV2::HasField(const std::string& key) const {
    return fields_->find(key) != fields_->end();
}

std::vector<std::string> SchemaValueV2::GetKeys() const {
    std::vector<std::string> keys;
    for (const auto& pair : *fields_) {
        keys.push_back(pair.first);
    }
    return keys;
}

ValueV2 SchemaValueV2::GetByPath(const std::string& path) const {
    auto components = ParsePath(path);
    if (components.empty()) {
        return ValueV2();
    }
    return ValueV2();
}

void SchemaValueV2::SetByPath(const std::string& path, const ValueV2& value, bool autoCreate) {
    auto components = ParsePath(path);
    if (components.empty()) {
        return;
    }
}

std::string SchemaValueV2::Serialize() const {
    std::ostringstream oss;
    oss << "{";
    bool first = true;
    for (const auto& pair : *fields_) {
        if (!first) oss << ", ";
        oss << pair.first << "=" << pair.second.ToString();
        first = false;
    }
    oss << "}";
    return oss.str();
}

SchemaValueV2 SchemaValueV2::Deserialize(const std::string& dslText) {
    SchemaValueV2 schema;
    return schema;
}

std::vector<PathComponent> SchemaValueV2::ParsePath(const std::string& path) {
    std::vector<PathComponent> components;
    std::istringstream iss(path);
    std::string part;
    
    while (std::getline(iss, part, '.')) {
        size_t bracePos = part.find('[');
        if (bracePos != std::string::npos) {
            size_t braceEnd = part.find(']', bracePos);
            if (braceEnd != std::string::npos) {
                std::string fieldName = part.substr(0, bracePos);
                std::string indexStr = part.substr(bracePos + 1, braceEnd - bracePos - 1);
                try {
                    int32_t index = std::stoi(indexStr);
                    components.push_back({fieldName, index});
                } catch (...) {
                    components.push_back({part, -1});
                }
            }
        } else {
            components.push_back({part, -1});
        }
    }
    return components;
}

// ============================================================================
// ArrayValueV2 实现
// ============================================================================
ValueV2 ArrayValueV2::GetElement(size_t index) const {
    if (index >= elements_->size()) {
        return ValueV2();
    }
    return (*elements_)[index];
}

void ArrayValueV2::SetElement(size_t index, const ValueV2& value) {
    if (index >= elements_->size()) {
        elements_->resize(index + 1);
    }
    (*elements_)[index] = value;
}

void ArrayValueV2::PushBack(const ValueV2& value) {
    elements_->push_back(value);
}

void ArrayValueV2::PopBack() {
    if (!elements_->empty()) {
        elements_->pop_back();
    }
}

void ArrayValueV2::Insert(size_t index, const ValueV2& value) {
    if (index <= elements_->size()) {
        elements_->insert(elements_->begin() + index, value);
    }
}

void ArrayValueV2::Remove(size_t index) {
    if (index < elements_->size()) {
        elements_->erase(elements_->begin() + index);
    }
}

size_t ArrayValueV2::GetSize() const {
    return elements_->size();
}

std::string ArrayValueV2::Serialize() const {
    std::ostringstream oss;
    oss << "[";
    bool first = true;
    for (const auto& elem : *elements_) {
        if (!first) oss << ", ";
        oss << elem.ToString();
        first = false;
    }
    oss << "]";
    return oss.str();
}

ArrayValueV2 ArrayValueV2::Deserialize(const std::string& dslText) {
    ArrayValueV2 array;
    return array;
}

std::vector<ValueV2> ArrayValueV2::GetElements() const {
    return *elements_;
}

// ============================================================================
// ValueV2 实现
// ============================================================================
ValueV2::ValueV2() : typeInfo_(GetNullTypeInfo()), data_(nullptr) {}

ValueV2::ValueV2(int64_t i) : typeInfo_(GetIntTypeInfo()) {
    data_ = std::make_shared<int64_t>(i);
}

ValueV2::ValueV2(double d) : typeInfo_(GetDoubleTypeInfo()) {
    data_ = std::make_shared<double>(d);
}

ValueV2::ValueV2(bool b) : typeInfo_(GetBoolTypeInfo()) {
    data_ = std::make_shared<bool>(b);
}

ValueV2::ValueV2(const std::string& s) : typeInfo_(GetStringTypeInfo()) {
    data_ = std::make_shared<std::string>(s);
}

ValueV2::ValueV2(const char* s) : typeInfo_(GetStringTypeInfo()) {
    data_ = std::make_shared<std::string>(s);
}

ValueV2::ValueV2(const ValueV2& other) : typeInfo_(other.typeInfo_), data_(other.data_) {}

ValueV2::ValueV2(ValueV2&& other) noexcept : typeInfo_(other.typeInfo_), data_(std::move(other.data_)) {
    other.typeInfo_ = GetNullTypeInfo();
}

ValueV2& ValueV2::operator=(const ValueV2& other) {
    if (this != &other) {
        typeInfo_ = other.typeInfo_;
        data_ = other.data_;
    }
    return *this;
}

ValueV2& ValueV2::operator=(ValueV2&& other) noexcept {
    if (this != &other) {
        typeInfo_ = other.typeInfo_;
        data_ = std::move(other.data_);
        other.typeInfo_ = GetNullTypeInfo();
    }
    return *this;
}

ValueV2::~ValueV2() {}

std::string ValueV2::GetTypeName() const {
    if (typeInfo_) {
        return typeInfo_->name;
    }
    return "unknown";
}

bool ValueV2::IsType(const std::string& typeName) const {
    if (!typeInfo_) return false;
    return typeInfo_->name == typeName;
}

int64_t ValueV2::ToInt() const {
    if (IsType("int")) {
        auto val = std::static_pointer_cast<int64_t>(data_);
        return val ? *val : 0;
    }
    return 0;
}

double ValueV2::ToDouble() const {
    if (IsType("double")) {
        auto val = std::static_pointer_cast<double>(data_);
        return val ? *val : 0.0;
    }
    return 0.0;
}

bool ValueV2::ToBool() const {
    if (IsType("bool")) {
        auto val = std::static_pointer_cast<bool>(data_);
        return val ? *val : false;
    }
    return false;
}

std::string ValueV2::ToString() const {
    if (IsType("string")) {
        auto val = std::static_pointer_cast<std::string>(data_);
        return val ? *val : "";
    } else if (IsType("int")) {
        return std::to_string(ToInt());
    } else if (IsType("double")) {
        return std::to_string(ToDouble());
    } else if (IsType("bool")) {
        return ToBool() ? "true" : "false";
    } else if (IsType("null")) {
        return "null";
    } else if (IsType("schema")) {
        auto schema = std::static_pointer_cast<SchemaValueV2>(data_);
        return schema ? schema->Serialize() : "{}";
    } else if (IsType("array")) {
        auto array = std::static_pointer_cast<ArrayValueV2>(data_);
        return array ? array->Serialize() : "[]";
    }
    return "";
}

ValueV2 ValueV2::GetField(const std::string& key) const {
    if (!IsType("schema")) {
        return ValueV2();
    }
    auto schema = std::static_pointer_cast<SchemaValueV2>(data_);
    return schema->GetField(key);
}

void ValueV2::SetField(const std::string& key, const ValueV2& value) {
    if (!IsType("schema")) {
        return;
    }
    auto schema = std::static_pointer_cast<SchemaValueV2>(data_);
    schema->SetField(key, value);
}

void ValueV2::RemoveField(const std::string& key) {
    if (!IsType("schema")) {
        return;
    }
    auto schema = std::static_pointer_cast<SchemaValueV2>(data_);
    schema->RemoveField(key);
}

void ValueV2::AddField(const std::string& key, const ValueV2& value) {
    if (!IsType("schema")) {
        return;
    }
    auto schema = std::static_pointer_cast<SchemaValueV2>(data_);
    schema->AddField(key, value);
}

ValueV2 ValueV2::GetByPath(const std::string& path) const {
    if (!IsType("schema")) {
        return ValueV2();
    }
    auto schema = std::static_pointer_cast<SchemaValueV2>(data_);
    return schema->GetByPath(path);
}

void ValueV2::SetByPath(const std::string& path, const ValueV2& value) {
    if (!IsType("schema")) {
        return;
    }
    auto schema = std::static_pointer_cast<SchemaValueV2>(data_);
    schema->SetByPath(path, value);
}

ValueV2 ValueV2::GetElement(size_t index) const {
    if (!IsType("array")) {
        return ValueV2();
    }
    auto array = std::static_pointer_cast<ArrayValueV2>(data_);
    return array->GetElement(index);
}

void ValueV2::SetElement(size_t index, const ValueV2& value) {
    if (!IsType("array")) {
        return;
    }
    auto array = std::static_pointer_cast<ArrayValueV2>(data_);
    array->SetElement(index, value);
}

void ValueV2::PushBack(const ValueV2& value) {
    if (!IsType("array")) {
        return;
    }
    auto array = std::static_pointer_cast<ArrayValueV2>(data_);
    array->PushBack(value);
}

void ValueV2::PopBack() {
    if (!IsType("array")) {
        return;
    }
    auto array = std::static_pointer_cast<ArrayValueV2>(data_);
    array->PopBack();
}

size_t ValueV2::GetSize() const {
    if (IsType("array")) {
        auto array = std::static_pointer_cast<ArrayValueV2>(data_);
        return array->GetSize();
    }
    return 0;
}

ValueV2 ValueV2::CreateSchema() {
    auto schema = std::make_shared<SchemaValueV2>();
    ValueV2 v;
    v.typeInfo_ = GetSchemaTypeInfo();
    v.data_ = schema;
    return v;
}

ValueV2 ValueV2::CreateArray() {
    auto array = std::make_shared<ArrayValueV2>();
    ValueV2 v;
    v.typeInfo_ = GetArrayTypeInfo();
    v.data_ = array;
    return v;
}

}  // namespace abot
