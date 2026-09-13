#pragma once
#include "Value.h"
#include <unordered_map>
#include <string>

namespace abot {

class StatePreset {
public:
    std::unordered_map<std::string, Value> extra;
    StatePreset() = default;
};

} // namespace abot
