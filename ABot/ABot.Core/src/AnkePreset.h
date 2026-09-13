#pragma once
#include "Value.h"
#include <unordered_map>
#include <string>

namespace abot {

class AnkePreset {
public:
    std::unordered_map<std::string, Value> extra;
    AnkePreset() = default;
};

} // namespace abot
