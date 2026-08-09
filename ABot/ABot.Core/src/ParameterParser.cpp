/**
 * @file ParameterParser.cpp
 * @brief 参数单元解析器实现
 */

#include "ParameterParser.h"
#include "Character.h"
#include <cctype>
#include <sstream>
#include <algorithm>

namespace abot {

std::string ParameterParser::last_error_;

// ============ 角色卡解析 ============

bool ParameterParser::ParseCharacterCard(const std::string& character_xml, Character& character) {
    last_error_ = "";
    
    // 初始化角色卡为默认值
    character = Character();
    
    // 简单验证：检查是否为非空字符串
    if (character_xml.empty()) {
        last_error_ = "Empty character ABOL";
        return false;
    }
    
    try {
        // 设置基础默认值
        character.name = "Unknown";
        character.camp = 1;
        character.atk = 10;
        character.max_hp = 100;
        character.hp = 100;
        character.hp_restore = 0;
        character.temp_hp = 0;
        character.dmg[0] = 5;
        character.dmg[1] = 8;
        character.dmg[2] = 12;
        character.dmg[3] = 15;
        character.aggro = 0;
        
        // 🟥【UFRS - 通用字段注册系统】
        // 逐行解析ABOL中的各个标签，采用自动字段流向系统
        size_t pos = 0;
        while (pos < character_xml.length()) {
            // 跳过空白
            pos = SkipWhitespace(character_xml, pos);
            if (pos >= character_xml.length()) break;
            
            // 跳过注释 //...
            if (pos + 1 < character_xml.length() && character_xml[pos] == '/' && character_xml[pos + 1] == '/') {
                while (pos < character_xml.length() && character_xml[pos] != '\n') pos++;
                continue;
            }
            
            // 检查标签开始
            if (character_xml[pos] != '<') {
                pos++;
                continue;
            }
            
            // 找到标签结束
            size_t tag_end = character_xml.find('>', pos);
            if (tag_end == std::string::npos) break;
            
            // 使用Parse()解析这个标签
            std::string single_tag = character_xml.substr(pos, tag_end - pos + 1);
            auto node = Parse(single_tag);
            
            if (node) {
                std::string tag_name_lower = node->name;
                std::transform(tag_name_lower.begin(), tag_name_lower.end(), tag_name_lower.begin(), ::tolower);
                
                // 🟥【自动字段处理】第一步：解析字段到 Character 成员（运行时镜像）
                // 💡 同时始终向 extra 中写入 Schema（脚本访问路径）
                
                if (tag_name_lower == "name") {
                    character.name = node->GetAttribute("value", "Unknown");
                    // 🟢 写入 extra（标量）
                    character.extra["name"] = Value(character.name);
                }
                else if (tag_name_lower == "camp") {
                    character.camp = node->GetAttributeInt("value", 1);
                    // 🟢 写入 extra（Schema 包装）
                    Value camp_schema = Value::CreateSchema();
                    camp_schema.SetField("value", Value(static_cast<int64_t>(character.camp)));
                    character.extra["camp"] = camp_schema;
                }
                else if (tag_name_lower == "atk") {
                    character.atk = node->GetAttributeInt("value", 10);
                    // 🟢 写入 extra（Schema 包装）
                    Value atk_schema = Value::CreateSchema();
                    atk_schema.SetField("value", Value(static_cast<int64_t>(character.atk)));
                    character.extra["atk"] = atk_schema;
                }
                else if (tag_name_lower == "hp") {
                    character.hp = node->GetAttributeInt("value", character.hp);
                    character.max_hp = node->GetAttributeInt("max", character.hp);
                    // 🟢 写入 extra（Schema 包装，包含 max 属性）
                    Value hp_schema = Value::CreateSchema();
                    hp_schema.SetField("value", Value(static_cast<int64_t>(character.hp)));
                    hp_schema.SetField("max", Value(static_cast<int64_t>(character.max_hp)));
                    character.extra["hp"] = hp_schema;
                }
                else if (tag_name_lower == "dmg") {
                    // 已有的Parse()会自动提取 d1, d2, d3, d4 键值对
                    character.dmg[0] = node->GetAttributeInt("d1", 5);
                    character.dmg[1] = node->GetAttributeInt("d2", 8);
                    character.dmg[2] = node->GetAttributeInt("d3", 12);
                    character.dmg[3] = node->GetAttributeInt("d4", 15);
                    // 🟢 写入 extra（Schema 包装）
                    Value dmg_schema = Value::CreateSchema();
                    dmg_schema.SetField("d1", Value(static_cast<int64_t>(character.dmg[0])));
                    dmg_schema.SetField("d2", Value(static_cast<int64_t>(character.dmg[1])));
                    dmg_schema.SetField("d3", Value(static_cast<int64_t>(character.dmg[2])));
                    dmg_schema.SetField("d4", Value(static_cast<int64_t>(character.dmg[3])));
                    character.extra["dmg"] = dmg_schema;
                }
                else if (tag_name_lower == "dfs") {
                    // 🟥【新增】防御力字段 - 写入 defenses 向量 + extra
                    int dfs_value = node->GetAttributeInt("value", 0);
                    if (dfs_value > 0) {
                        character.defenses.push_back({dfs_value, ""});
                    }
                    // 🟢 写入 extra（Schema 包装）
                    Value dfs_schema = Value::CreateSchema();
                    dfs_schema.SetField("value", Value(static_cast<int64_t>(dfs_value)));
                    character.extra["dfs"] = dfs_schema;
                }
                else if (tag_name_lower == "skill") {
                    // 解析技能标签：<skill name=..., type=..., id=..., cd=0, rate=100>
                    SkillParam skill;
                    skill.name = node->GetAttribute("name", "Unknown");
                    skill.type = node->GetAttribute("type", "");
                    skill.id = node->GetAttribute("id", "");
                    skill.cd = node->GetAttributeInt("cd", 0);
                    skill.rate = node->GetAttributeInt("rate", 100);
                    skill.disabled = false;  // 🟢 关键修复：初始化禁用状态为 false
                    
                    // 如果至少有 type 和 id，则添加到技能列表
                    if (!skill.type.empty() && !skill.id.empty()) {
                        character.skills.push_back(skill);
                        
                        // 🟢 写入 extra（Schema 包装）
                        Value skill_schema = Value::CreateSchema();
                        skill_schema.SetField("name", Value(skill.name));
                        skill_schema.SetField("type", Value(skill.type));
                        skill_schema.SetField("id", Value(skill.id));
                        skill_schema.SetField("cd", Value(static_cast<int64_t>(skill.cd)));
                        skill_schema.SetField("rate", Value(static_cast<int64_t>(skill.rate)));
                        
                        // 技能集合 extra["skillset"] 是数组
                        if (character.extra.find("skillset") == character.extra.end()) {
                            character.extra["skillset"] = Value::CreateArray();
                        }
                        Value& skillset = character.extra["skillset"];
                        if (skillset.IsArray()) {
                            skillset.AppendElement(skill_schema);
                        }
                    }
                }
                // 🟥【UFRS 通用扩展】自动解析所有未知标签
                // 任何未在上面特殊处理的标签，自动将其所有属性写入 extra[tag_name]
                else {
                    Value generic_schema = Value::CreateSchema();
                    for (auto& attr : node->attributes) {
                        // 尝试解析为整数，失败则作为字符串
                        try {
                            int64_t int_val = std::stoll(attr.second);
                            generic_schema.SetField(attr.first, Value(int_val));
                        } catch (...) {
                            generic_schema.SetField(attr.first, Value(attr.second));
                        }
                    }
                    character.extra[tag_name_lower] = generic_schema;
                }
            }
            
            pos = tag_end + 1;
        }
        
        // 确保 HP 不超过最大值
        if (character.hp > character.max_hp) {
            character.hp = character.max_hp;
        }
        if (character.hp < 0) {
            character.hp = 0;
        }
        
        // 设置存活状态
        character.is_alive = character.hp > 0;
        
        return true;
    } catch (const std::exception& e) {
        last_error_ = std::string("Exception while parsing character card: ") + e.what();
        return false;
    }
}

// ============ 参数单元解析（旧方法） ============

std::string ParameterNode::GetAttribute(const std::string& key,
                                        const std::string& default_value) const {
    auto it = attributes.find(key);
    if (it != attributes.end()) {
        return it->second;
    }
    return default_value;
}

int ParameterNode::GetAttributeInt(const std::string& key, int default_value) const {
    auto value = GetAttribute(key);
    if (value.empty()) {
        return default_value;
    }
    try {
        return std::stoi(value);
    } catch (...) {
        return default_value;
    }
}

float ParameterNode::GetAttributeFloat(const std::string& key, float default_value) const {
    auto value = GetAttribute(key);
    if (value.empty()) {
        return default_value;
    }
    try {
        return std::stof(value);
    } catch (...) {
        return default_value;
    }
}

std::shared_ptr<ParameterNode> ParameterParser::Parse(const std::string& xml) {
    last_error_ = "";
    
    if (xml.empty()) {
        last_error_ = "Empty ABOL input";
        return nullptr;
    }
    
    size_t pos = 0;
    
    // 跳过空白
    pos = SkipWhitespace(xml, pos);
    
    // 检查是否以 '<' 开头
    if (pos >= xml.length() || xml[pos] != '<') {
        last_error_ = "Expected '<' at start of XML";
        return nullptr;
    }
    
    pos++;  // 跳过 '<'
    
    // 提取标签名
    auto node = std::make_shared<ParameterNode>();
    node->name = ExtractTagName(xml, pos);
    
    if (node->name.empty()) {
        last_error_ = "Empty tag name";
        return nullptr;
    }
    
    // 提取属性
    node->attributes = ExtractAttributes(xml, pos);
    
    // 跳过空白
    pos = SkipWhitespace(xml, pos);
    
    // 检查是否以 '>' 结尾
    if (pos >= xml.length() || xml[pos] != '>') {
        last_error_ = "Expected '>' after tag";
        return nullptr;
    }
    
    pos++;  // 跳过 '>'
    
    return node;
}

size_t ParameterParser::SkipWhitespace(const std::string& xml, size_t pos) {
    while (pos < xml.length() && std::isspace(xml[pos])) {
        pos++;
    }
    return pos;
}

std::string ParameterParser::ExtractTagName(const std::string& xml, size_t& pos) {
    std::string name;
    
    pos = SkipWhitespace(xml, pos);
    
    while (pos < xml.length() && (std::isalnum(xml[pos]) || xml[pos] == '_')) {
        name += xml[pos];
        pos++;
    }
    
    return name;
}

std::map<std::string, std::string> ParameterParser::ExtractAttributes(
    const std::string& xml, size_t& pos) {
    
    std::map<std::string, std::string> attrs;
    
    while (pos < xml.length() && xml[pos] != '>') {
        pos = SkipWhitespace(xml, pos);
        
        if (xml[pos] == '>') {
            break;
        }
        
        std::string key, value;
        if (!ExtractKeyValue(xml, pos, key, value)) {
            break;
        }
        
        if (!key.empty()) {
            attrs[key] = value;
        }
        
        // 跳过逗号和空白
        pos = SkipWhitespace(xml, pos);
        if (pos < xml.length() && xml[pos] == ',') {
            pos++;
        }
    }
    
    return attrs;
}

bool ParameterParser::ExtractKeyValue(const std::string& xml, size_t& pos,
                                      std::string& key, std::string& value) {
    key = "";
    value = "";
    
    pos = SkipWhitespace(xml, pos);
    
    // 提取键
    while (pos < xml.length() && (std::isalnum(xml[pos]) || xml[pos] == '_')) {
        key += xml[pos];
        pos++;
    }
    
    if (key.empty()) {
        return false;
    }
    
    pos = SkipWhitespace(xml, pos);
    
    // 检查 '='
    if (pos >= xml.length() || xml[pos] != '=') {
        return false;
    }
    
    pos++;  // 跳过 '='
    pos = SkipWhitespace(xml, pos);
    
    // 提取值
    if (pos < xml.length() && (xml[pos] == '"' || xml[pos] == '\'')) {
        value = ExtractQuotedString(xml, pos);
    } else {
        value = ExtractUnquotedValue(xml, pos);
    }
    
    return true;
}

std::string ParameterParser::ExtractQuotedString(const std::string& xml, size_t& pos) {
    if (pos >= xml.length()) {
        return "";
    }
    
    char quote = xml[pos];
    pos++;
    
    std::string value;
    while (pos < xml.length() && xml[pos] != quote) {
        value += xml[pos];
        pos++;
    }
    
    if (pos < xml.length() && xml[pos] == quote) {
        pos++;
    }
    
    return value;
}

std::string ParameterParser::ExtractUnquotedValue(const std::string& xml, size_t& pos) {
    std::string value;
    
    while (pos < xml.length() && !std::isspace(xml[pos]) &&
           xml[pos] != ',' && xml[pos] != '>') {
        value += xml[pos];
        pos++;
    }
    
    return value;
}

}  // namespace abot
