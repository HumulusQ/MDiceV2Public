/**
 * @file Character.cpp
 * @brief Character 类实现
 */

#include "Character.h"
#include "ValueV2.h"
#include <algorithm>
#include <sstream>
#include <iomanip>

namespace abot {

Character::Character()
    : name(""), camp(0), atk(0), hp(0), max_hp(0),
      hp_restore(0), temp_hp(0), aggro(0),
      is_alive(true) {
    // 初始化伤害数组
    for (int i = 0; i < 4; i++) {
        dmg[i] = 0;
    }
}

int Character::TakeDamage(int damage) {
    if (!is_alive || damage < 0) {
        return 0;
    }
    
    // 步骤 1: 临时 HP 先承伤
    int to_temp = std::min(damage, temp_hp);
    temp_hp -= to_temp;
    int remaining_damage = damage - to_temp;
    
    // 步骤 2: 护甲减伤（总护甲值）
    int total_defense = 0;
    for (const auto& def : defenses) {
        total_defense += def.value;
    }
    int after_armor = std::max(0, remaining_damage - total_defense);
    
    // 步骤 3: 伤害减免（总减免率）
    float total_dr = 0.0f;
    for (const auto& dr : damage_reductions) {
        total_dr += dr.value;
    }
    // 限制在 [0, 1] 范围内
    total_dr = std::min(1.0f, std::max(0.0f, total_dr));
    int final_damage = (int)(after_armor * (1.0f - total_dr));
    
    // 步骤 4: 应用伤害到实际 HP
    hp = std::max(0, hp - final_damage);
    
    // 检查是否死亡
    if (hp <= 0) {
        is_alive = false;
        hp = 0;
    }
    
    return final_damage;
}

void Character::Heal(int healing) {
    if (!is_alive || healing < 0) {
        return;
    }
    
    hp = std::min(hp + healing, max_hp);
}

std::string Character::GetBasicInfoDebug() const {
    std::ostringstream oss;
    oss << "=== Character Basic Info ===\n";
    oss << "  Name: " << name << "\n";
    oss << "  Camp: " << camp << "\n";
    oss << "  Status: " << (is_alive ? "Alive" : "Dead") << "\n";
    oss << "  HP: " << hp << " / " << max_hp;
    if (temp_hp > 0) oss << " (+" << temp_hp << " Temp)";
    oss << "\n";
    oss << "  ATK: " << atk << "\n";
    oss << "  Aggro: " << aggro << "\n";
    
    if (hp_restore != 0) oss << "  HP Restore/turn: " << hp_restore << "\n";
    
    oss << "  Damage: [" << dmg[0] << ", " << dmg[1] << ", " << dmg[2] << ", " << dmg[3] << "]\n";
    
    if (!defenses.empty()) {
        oss << "  Defenses:\n";
        for (const auto& def : defenses) {
            oss << "    - " << def.value;
            if (!def.tag.empty()) oss << " (tag: " << def.tag << ")";
            oss << "\n";
        }
    }
    
    if (!damage_reductions.empty()) {
        oss << "  Damage Reductions:\n";
        for (const auto& dr : damage_reductions) {
            oss << "    - " << std::fixed << std::setprecision(2) << (dr.value * 100) << "%";
            if (!dr.tag.empty()) oss << " (tag: " << dr.tag << ")";
            oss << "\n";
        }
    }
    
    return oss.str();
}

std::string Character::GetSkillsDebug() const {
    std::ostringstream oss;
    oss << "=== Character Skills ===\n";
    if (skills.empty()) {
        oss << "  (No skills)\n";
        return oss.str();
    }
    
    for (size_t i = 0; i < skills.size(); i++) {
        const auto& skill = skills[i];
        oss << "  [" << (i + 1) << "] " << skill.name << "\n";
        oss << "      Type: " << skill.type << "\n";
        oss << "      ID: " << skill.id << "\n";
        oss << "      CD: " << skill.cd << " turns\n";
        oss << "      Rate: " << skill.rate << "%\n";
        if (skill.disabled) oss << "      [DISABLED]\n";
        
        if (!skill.skillpara.empty()) {
            oss << "      Params:\n";
            for (const auto& param : skill.skillpara) {
                oss << "        - " << param.first << " = " << param.second << "\n";
            }
        }
    }
    
    return oss.str();
}

std::string Character::GetStatesDebug() const {
    std::ostringstream oss;
    oss << "=== Character States ===\n";
    if (states.empty()) {
        oss << "  (No states)\n";
        return oss.str();
    }
    
    for (size_t i = 0; i < states.size(); i++) {
        const auto& state = states[i];
        oss << "  [" << (i + 1) << "] " << state.name << "\n";
        oss << "      Type: " << state.type << "\n";
        oss << "      ID: " << state.id << "\n";
        oss << "      Duration: " << (state.duration == -1 ? "Permanent" : std::to_string(state.duration) + " turns") << "\n";
        
        if (!state.params.empty()) {
            oss << "      Params:\n";
            for (const auto& param : state.params) {
                oss << "        - " << param.first << " = " << param.second << "\n";
            }
        }
    }
    
    return oss.str();
}

std::string Character::GetCompleteDebug() const {
    std::ostringstream oss;
    oss << GetBasicInfoDebug() << "\n";
    oss << GetSkillsDebug() << "\n";
    oss << GetStatesDebug();
    
    if (!tags.empty()) {
        oss << "=== Tags ===\n";
        for (size_t i = 0; i < tags.size(); i++) {
            oss << "  - " << tags[i] << "\n";
        }
    }
    
    return oss.str();
}

/**
 * @brief 实现GetAsValueV2 - 返回角色的ValueV2引用封装
 * 
 * 该实现创建一个Schema ValueV2对象，包含Character的所有可访问字段。
 * 当脚本读取或修改这些字段时，它们会影响原始Character对象。
 * 
 * 返回的ValueV2包含以下字段：
 * - name: 角色名称
 * - camp: 阵营
 * - atk: 攻击值
 * - hp: 当前HP
 * - max_hp: 最大HP
 * - hp_restore: 每回合自动回血
 * - temp_hp: 临时HP
 * - dmg_min/low/high/max: 伤害数组
 * - aggro: 仇恨值
 * - is_alive: 存活状态
 * - __character_ptr__: 指向Character对象的指针（用于写回）
 */
/**
 * @brief 实现GetAsValueV2 - 返回角色的ValueV2引用封装
 * 
 * 返回包含角色所有字段的Schema对象，使脚本可以通过
 * self.field_name 的方式访问和修改角色属性。
 * 
 * 实现状态：目前返回空ValueV2，完整实现需要ValueV2系统完成
 * 
 * TODO: 完整实现流程
 * 1. 用 Character 的所有字段初始化 Schema
 * 2. 返回该 Schema 作为引用系统的入口点
 * 3. 当 Schema 中的字段被修改时，同步更新 Character 本身
 */
ValueV2 Character::GetAsValueV2() const {
    // Minimal implementation during ValueV2 system development
    // Full implementation pending when ValueV2 is complete
    return ValueV2();  // Returns null ValueV2
}

// ============ 技能索引管理实现 ============

void SkillIndex::BuildIndex(std::vector<SkillParam>& skills) {
    by_trigger_type.clear();
    
    for (auto& skill : skills) {
        // 规范化触发类型（转小写）
        std::string normalized_type = skill.type;
        std::transform(normalized_type.begin(), normalized_type.end(),
                      normalized_type.begin(), ::tolower);
        
        // 添加指针到对应的索引桶
        by_trigger_type[normalized_type].push_back(&skill);
    }
}

void Character::RebuildSkillIndex() {
    skill_index.BuildIndex(skills);
}

void Character::AddSkill(const SkillParam& skill) {
    // 添加技能到列表
    skills.push_back(skill);
    
    // 更新索引
    std::string normalized_type = skill.type;
    std::transform(normalized_type.begin(), normalized_type.end(),
                  normalized_type.begin(), ::tolower);
    skill_index.by_trigger_type[normalized_type].push_back(&skills.back());
}

std::vector<SkillParam*> Character::GetSkillsByType(const std::string& trigger_type) {
    // Phase 3 优化：惰性初始化索引
    // 在首次查询时检查索引是否为空，如果为空则构建
    if (skill_index.by_trigger_type.empty() && !skills.empty()) {
        RebuildSkillIndex();
    }
    
    // 规范化查询类型
    std::string normalized = trigger_type;
    std::transform(normalized.begin(), normalized.end(),
                  normalized.begin(), ::tolower);
    
    // 返回索引中的技能列表
    return skill_index.GetSkillsByType(normalized);
}

}  // namespace abot
