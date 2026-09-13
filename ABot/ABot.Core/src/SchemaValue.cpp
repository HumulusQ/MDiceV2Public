#include "SchemaValue.h"
#include <stdexcept>
#include <sstream>

namespace abot {

void SchemaValue::SetField(const std::string& k, const Value& v) { fields_[k] = v; }
Value SchemaValue::GetField(const std::string& k) const {
    auto it = fields_.find(k);
    if (it == fields_.end()) throw std::runtime_error("Field not found");
    return it->second;
}
bool SchemaValue::HasField(const std::string& k) const {
    return fields_.find(k) != fields_.end();
}
std::vector<std::string> SchemaValue::GetKeys() const {
    std::vector<std::string> keys;
    keys.reserve(fields_.size());
    for (const auto& pair : fields_) {
        keys.push_back(pair.first);
    }
    return keys;
}
std::unordered_map<std::string, Value>& SchemaValue::GetAllFields() { return fields_; }
const std::unordered_map<std::string, Value>& SchemaValue::GetAllFields() const { return fields_; }
std::unordered_map<std::string, Value>& SchemaValue::GetAllFieldsMutable() { return fields_; }
std::string SchemaValue::ToString() const {
    std::ostringstream oss;
    oss << "{ ";
    bool first = true;
    for (const auto& pair : fields_) {
        if (!first) oss << ", ";
        oss << '"' << pair.first << "\": " << pair.second.ToString();
        first = false;
    }
    oss << " }";
    return oss.str();
}

} // namespace abot
