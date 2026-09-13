#pragma once
#include "Value.h"
#include <unordered_map>
#include <string>

namespace abot {

class SkillPreset {
public:
    std::unordered_map<std::string, Value> extra;
    SkillPreset() = default;
};

} // namespace abot
