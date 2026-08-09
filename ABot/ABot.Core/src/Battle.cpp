/**
 * @file Battle.cpp
 * @brief 战斗系统实现
 */

#include "Battle.h"
#include <algorithm>
#include <cstdlib>
#include <ctime>

namespace abot {

Battle::Battle()
    : state_(BattleState::UNINITIALIZED),
      current_round_(0),
      last_error_("") {
    // ✅ 修复：移除static seeded标志，每次创建Battle都重新初始化srand()
    // 【原因】LoadState()恢复状态时会创建新Battle对象
    //        静态标志导致srand()只被调用一次，新Battle对象无法重新初始化
    //        结果：rand()序列错位，导致重复值（如D10=5循环）
    // 【方案】每次都调用srand()：虽然以秒级粒度分布随机序列，但足够产生不同值
    srand((unsigned int)time(nullptr));
}

Battle::~Battle() {
    characters_.clear();
    camps_.clear();
}

bool Battle::Initialize(const std::vector<std::shared_ptr<Character>>& characters) {
    if (characters.empty()) {
        last_error_ = "Character list cannot be empty";
        return false;
    }
    
    characters_ = characters;
    camps_.clear();
    
    // Organize characters by camp
    for (const auto& character : characters_) {
        if (character) {
            camps_[character->camp].push_back(character);
        }
    }
    
    state_ = BattleState::INITIALIZED;
    current_round_ = 0;
    return true;
}

bool Battle::IsValid() const {
    return camps_.size() >= 2;
}

bool Battle::Start() {
    if (state_ != BattleState::INITIALIZED) {
        last_error_ = "Battle not initialized properly";
        return false;
    }
    
    if (!IsValid()) {
        last_error_ = "Battle setup invalid: need at least 2 camps";
        return false;
    }
    
    state_ = BattleState::IN_PROGRESS;
    return true;
}

bool Battle::ExecuteRound() {
    if (state_ != BattleState::IN_PROGRESS) {
        last_error_ = "Battle not in progress";
        return false;
    }
    
    current_round_++;
    
    if (IsFinished()) {
        state_ = BattleState::FINISHED;
        return false;
    }
    
    auto actor = SelectActor();
    if (!actor) {
        state_ = BattleState::FINISHED;
        return false;
    }
    
    auto target = SelectTarget(actor);
    if (!target) {
        state_ = BattleState::FINISHED;
        return false;
    }
    
    ExecuteAttack(actor, target);
    return true;
}

bool Battle::IsFinished() const {
    int alive_camps = 0;
    for (const auto& camp_entry : camps_) {
        for (const auto& character : camp_entry.second) {
            if (character && character->IsAlive()) {
                alive_camps++;
                break;
            }
        }
    }
    return alive_camps <= 1;
}

int Battle::GetVictoryCamp() const {
    if (!IsFinished()) {
        return 0;
    }
    
    for (const auto& camp_entry : camps_) {
        for (const auto& character : camp_entry.second) {
            if (character && character->IsAlive()) {
                return camp_entry.first;
            }
        }
    }
    return 0;
}

std::vector<std::shared_ptr<Character>> Battle::GetLiveCharactersByCamp(int camp) const {
    std::vector<std::shared_ptr<Character>> result;
    
    auto it = camps_.find(camp);
    if (it != camps_.end()) {
        for (const auto& character : it->second) {
            if (character && character->IsAlive()) {
                result.push_back(character);
            }
        }
    }
    
    return result;
}

std::shared_ptr<Character> Battle::SelectActor() {
    std::shared_ptr<Character> best_actor = nullptr;
    int max_atk = -1;
    
    for (const auto& character : characters_) {
        if (character && character->IsAlive() && character->atk > max_atk) {
            max_atk = character->atk;
            best_actor = character;
        }
    }
    
    return best_actor;
}

std::shared_ptr<Character> Battle::SelectTarget(std::shared_ptr<Character> actor) {
    if (!actor) {
        return nullptr;
    }
    
    std::vector<std::shared_ptr<Character>> enemies;
    
    for (const auto& character : characters_) {
        if (character && character->IsAlive() && character->camp != actor->camp) {
            enemies.push_back(character);
        }
    }
    
    if (enemies.empty()) {
        return nullptr;
    }
    
    // Simple priority: highest aggro, then random
    std::shared_ptr<Character> target = nullptr;
    int max_aggro = -1;
    
    for (const auto& enemy : enemies) {
        if (enemy->aggro > max_aggro) {
            max_aggro = enemy->aggro;
            target = enemy;
        }
    }
    
    return target;
}

void Battle::ExecuteAttack(std::shared_ptr<Character> attacker,
                          std::shared_ptr<Character> target) {
    if (!attacker || !target) {
        return;
    }
    
    int damage = CalculateDamage(attacker, target);
    target->TakeDamage(damage);
}

int Battle::CalculateDamage(std::shared_ptr<Character> attacker,
                           std::shared_ptr<Character> target) {
    if (!attacker || !target) {
        return 0;
    }
    
    // Select random damage value
    int dmg_index = rand() % 4;
    int base_damage = attacker->dmg[dmg_index];
    
    // Calculate total defense from defense vector
    int total_defense = 0;
    for (const auto& def : target->defenses) {
        total_defense += def.value;
    }
    int after_armor = std::max(0, base_damage - total_defense);
    
    // Calculate total damage reduction from damage_reductions vector
    float total_reduction = 0.0f;
    for (const auto& dr : target->damage_reductions) {
        total_reduction += dr.value;
    }
    total_reduction = std::max(0.0f, std::min(1.0f, total_reduction));
    int final_damage = (int)(after_armor * (1.0f - total_reduction));
    
    return std::max(0, final_damage);
}

}  // namespace abot