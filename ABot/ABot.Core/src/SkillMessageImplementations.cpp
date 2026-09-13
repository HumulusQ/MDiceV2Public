/**
 * @file SkillMessageImplementations.cpp
 * @brief 技能消息系统的实现代码
 * 
 * 包含 SkillMessageRegistry、SkillMessageSignature 等的具体实现
 * 这个文件与 SkillTriggerSystem.cpp 配合使用
 */

#pragma execution_character_set("utf-8")

#include "SkillTriggerSystem.h"
#include "SkillMessageDefinitions.h"
#include <algorithm>
#include <sstream>

namespace abot {

// ============ SkillTriggerMessage 的方法实现 ============

bool SkillTriggerMessage::HasStringParam(const std::string& param_name) const {
    if (param_name == "From" && !From.empty()) return true;
    if (param_name == "To" && !To.empty()) return true;
    if (param_name == "Name" && !Name.empty()) return true;
    if (param_name == "Source" && !Source.empty()) return true;
    if (param_name == "Tag" && !Tag.empty()) return true;
    if (param_name == "Skillname" && !Skillname.empty()) return true;
    if (param_name == "Owner" && !Owner.empty()) return true;
    if (param_name == "Skilltype" && !Skilltype.empty()) return true;
    return false;
}

bool SkillTriggerMessage::HasIntParam(const std::string& param_name) const {
    if (param_name == "Dmg") return Dmg != 0;
    if (param_name == "value") return value != 0;
    return false;
}

int SkillTriggerMessage::GetIntParam(const std::string& param_name, int default_val) const {
    if (param_name == "Dmg") return Dmg;
    if (param_name == "value") return value;
    return default_val;
}

std::string SkillTriggerMessage::GetStringParam(const std::string& param_name, const std::string& default_val) const {
    if (param_name == "From") return From.empty() ? default_val : From;
    if (param_name == "To") return To.empty() ? default_val : To;
    if (param_name == "Name") return Name.empty() ? default_val : Name;
    if (param_name == "Source") return Source.empty() ? default_val : Source;
    if (param_name == "Tag") return Tag.empty() ? default_val : Tag;
    if (param_name == "Skillname") return Skillname.empty() ? default_val : Skillname;
    if (param_name == "Owner") return Owner.empty() ? default_val : Owner;
    if (param_name == "Skilltype") return Skilltype.empty() ? default_val : Skilltype;
    return default_val;
}

// ============ SkillMessageSignature 的方法实现 ============

bool SkillMessageSignature::ValidateMessage(const SkillTriggerMessage& msg) const {
    // 检查所有必需参数是否存在
    for (const auto& param_def : parameters) {
        if (param_def.required) {
            bool has_param = false;
            
            if (param_def.type == MessageParamType::STRING) {
                has_param = msg.HasStringParam(param_def.name);
            } else if (param_def.type == MessageParamType::INT) {
                has_param = msg.HasIntParam(param_def.name);
            }
            
            if (!has_param) {
                // 必需参数缺失
                return false;
            }
        }
    }
    
    return true;
}

// ============ SkillMessageRegistry 的实现 ============

// 静态成员初始化
SkillMessageRegistry* SkillMessageRegistry::instance_ = nullptr;

SkillMessageRegistry* SkillMessageRegistry::GetInstance() {
    if (instance_ == nullptr) {
        instance_ = new SkillMessageRegistry();
        instance_->Initialize();
    }
    return instance_;
}

void SkillMessageRegistry::Initialize() {
    // 初始化所有15种技能的消息签名
    SkillMessageDefinitions::InitializeAllSignatures();
}

const SkillMessageSignature* SkillMessageRegistry::GetSignature(const std::string& skill_type) {
    auto it = GetInstance()->signatures_.find(skill_type);
    if (it != GetInstance()->signatures_.end()) {
        return &(it->second);
    }
    return nullptr;
}

void SkillMessageRegistry::RegisterSignature(const SkillMessageSignature& signature) {
    GetInstance()->signatures_[signature.skill_type] = signature;
}

bool SkillMessageRegistry::ValidateMessage(const std::string& skill_type, const SkillTriggerMessage& msg) {
    const auto* signature = GetSignature(skill_type);
    if (signature == nullptr) {
        // 未知的技能类型，不验证
        return true;
    }
    return signature->ValidateMessage(msg);
}

}  // namespace abot
