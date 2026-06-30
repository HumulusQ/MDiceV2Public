#pragma once
#include "Value.h"
#include <unordered_map>
#include <string>
#include <vector>

namespace abot {

class SchemaValue {
public:
    void SetField(const std::string&, const Value&);
    Value GetField(const std::string&) const;
    bool HasField(const std::string&) const;
    std::vector<std::string> GetKeys() const;
    std::unordered_map<std::string, Value>& GetAllFields();
    const std::unordered_map<std::string, Value>& GetAllFields() const;
    std::unordered_map<std::string, Value>& GetAllFieldsMutable();
    std::string ToString() const;

private:
    std::unordered_map<std::string, Value> fields_;
};

} // namespace abot
