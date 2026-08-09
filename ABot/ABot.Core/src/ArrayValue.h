#pragma once

#include <vector>
#include <string>
#include "Value.h"

namespace abot {

class ArrayValue {
private:
    std::vector<Value> elements_;

public:
    ArrayValue() = default;
    Value GetElement(size_t index) const;
    void SetElement(size_t index, const Value& value);
    void AppendElement(const Value& value);
    void PushBack(const Value& value);
    void PopBack();
    size_t GetSize() const;
    std::string ToString() const;
};

} // namespace abot
