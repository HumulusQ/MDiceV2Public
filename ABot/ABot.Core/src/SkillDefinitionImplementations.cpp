/**
 * @file SkillDefinitionImplementations.cpp
 * @brief SkillDefinition 和相关的实现
 */

#pragma execution_character_set("utf-8")

#include "PresetSystem.h"
#include "SkillTriggerSystem.h"

namespace abot {

bool SkillDefinition::ValidateMessage(const SkillTriggerMessage& msg) const {
    // 使用消息注册表验证消息
    return SkillMessageRegistry::ValidateMessage(type, msg);
}

}  // namespace abot
