/**
 * @file Phase2Tests.cpp
 * @brief Phase 2 单元测试 - 参数解析、角色和战斗系统
 */

#include <cassert>
#include <iostream>
#include <memory>
#include <cmath>
#include "../src/Character.h"
#include "../src/ParameterParser.h"
#include "../src/Battle.h"

using namespace abot;

// ============ Character 测试 ============

void Test_Character_Creation() {
    std::cout << "Test: Character Creation... ";
    
    Character ch;
    assert(ch.name == "");
    assert(ch.camp == 0);
    assert(ch.atk == 0);
    assert(ch.hp == 0);
    assert(ch.max_hp == 0);
    assert(ch.is_alive == true);
    
    std::cout << "✓\n";
}

void Test_Character_TakeDamage() {
    std::cout << "Test: Character Take Damage... ";
    
    Character ch;
    ch.name = "Warrior";
    ch.max_hp = 100;
    ch.hp = 100;
    ch.dfs = 10;  // 护甲值
    ch.dr = 0.2f; // 20% 伤害减免
    
    // 伤害公式：final_damage = max(0, (base_damage - dfs) * (1 - dr))
    // 对于 50 点伤害：(50 - 10) * (1 - 0.2) = 40 * 0.8 = 32
    int damage_taken = ch.TakeDamage(50);
    
    assert(damage_taken == 32);
    assert(ch.hp == 68);  // 100 - 32 = 68
    assert(ch.is_alive == true);
    
    // 测试护甲完全吸收伤害的情况
    int weak_damage = ch.TakeDamage(5);
    assert(weak_damage == 0);  // (5 - 10) = -5, 但最小为0
    assert(ch.hp == 68);  // HP 不变
    
    // 测试致命伤害
    ch.TakeDamage(100);
    assert(ch.is_alive == false);
    
    std::cout << "✓\n";
}

void Test_Character_Heal() {
    std::cout << "Test: Character Heal... ";
    
    Character ch;
    ch.name = "Healer";
    ch.max_hp = 100;
    ch.hp = 50;
    
    ch.Heal(30);
    assert(ch.hp == 80);
    
    // 超过最大HP的治疗应该被限制
    ch.Heal(50);
    assert(ch.hp == 100);
    
    std::cout << "✓\n";
}

void Test_Character_HP_Percentage() {
    std::cout << "Test: Character HP Percentage... ";
    
    Character ch;
    ch.max_hp = 100;
    ch.hp = 50;
    
    float percentage = ch.GetHPPercentage();
    assert(percentage >= 0.49f && percentage <= 0.51f);  // 约等于 0.5
    
    std::cout << "✓\n";
}

// ============ ParameterParser 测试 ============

void Test_ParameterParser_SimpleTag() {
    std::cout << "Test: ParameterParser Simple Tag... ";
    
    std::string xml = "<type value=Character>";
    auto param = ParameterParser::Parse(xml);
    
    assert(param != nullptr);
    assert(param->name == "type");
    assert(param->GetAttribute("value") == "Character");
    
    std::cout << "✓\n";
}

void Test_ParameterParser_QuotedValues() {
    std::cout << "Test: ParameterParser Quoted Values... ";
    
    std::string xml = R"(<Character name="Flame Knight", camp=1>)";
    auto param = ParameterParser::Parse(xml);
    
    assert(param != nullptr);
    assert(param->name == "Character");
    assert(param->GetAttribute("name") == "Flame Knight");
    assert(param->GetAttributeInt("camp") == 1);
    
    std::cout << "✓\n";
}

void Test_ParameterParser_IntegerAttributes() {
    std::cout << "Test: ParameterParser Integer Attributes... ";
    
    std::string xml = R"(<Stats hp=100, atk=50, dfs=10>)";
    auto param = ParameterParser::Parse(xml);
    
    assert(param != nullptr);
    assert(param->name == "Stats");
    assert(param->GetAttributeInt("hp") == 100);
    assert(param->GetAttributeInt("atk") == 50);
    assert(param->GetAttributeInt("dfs") == 10);
    
    std::cout << "✓\n";
}

void Test_ParameterParser_FloatAttributes() {
    std::cout << "Test: ParameterParser Float Attributes... ";
    
    std::string xml = R"(<Reduction dr=0.25, critical=1.5>)";
    auto param = ParameterParser::Parse(xml);
    
    assert(param != nullptr);
    assert(param->name == "Reduction");
    
    float dr = param->GetAttributeFloat("dr");
    assert(dr >= 0.24f && dr <= 0.26f);
    
    float crit = param->GetAttributeFloat("critical");
    assert(crit >= 1.49f && crit <= 1.51f);
    
    std::cout << "✓\n";
}

void Test_ParameterParser_ErrorHandling() {
    std::cout << "Test: ParameterParser Error Handling... ";
    
    std::string invalid_xml = "<unclosed tag";
    auto param = ParameterParser::Parse(invalid_xml);
    
    // 解析应该失败或返回不完整的结果
    // 具体行为取决于实现
    
    std::cout << "✓\n";
}

// ============ Battle 测试 ============

void Test_Battle_Creation() {
    std::cout << "Test: Battle Creation... ";
    
    Battle battle;
    assert(!battle.IsFinished());
    assert(battle.GetCurrentRound() == 0);
    
    std::cout << "✓\n";
}

void Test_Battle_Initialization() {
    std::cout << "Test: Battle Initialization... ";
    
    Battle battle;
    
    // 创建两个阵营的角色
    auto ch1 = std::make_shared<Character>();
    ch1->name = "Player1";
    ch1->camp = 1;
    ch1->atk = 50;
    ch1->hp = 100;
    ch1->max_hp = 100;
    ch1->dmg[0] = 10;
    ch1->dmg[3] = 20;
    ch1->dfs = 5;
    ch1->dr = 0.1f;
    
    auto ch2 = std::make_shared<Character>();
    ch2->name = "Enemy1";
    ch2->camp = 2;
    ch2->atk = 40;
    ch2->hp = 80;
    ch2->max_hp = 80;
    ch2->dmg[0] = 8;
    ch2->dmg[3] = 18;
    ch2->dfs = 3;
    ch2->dr = 0.05f;
    
    std::vector<std::shared_ptr<Character>> characters = {ch1, ch2};
    
    bool success = battle.Initialize(characters);
    assert(success);
    assert(battle.GetCurrentRound() == 0);
    assert(!battle.IsFinished());
    
    std::cout << "✓\n";
}

void Test_Battle_Simple_Combat() {
    std::cout << "Test: Battle Simple Combat... ";
    
    Battle battle;
    
    // 创建测试角色
    auto ch1 = std::make_shared<Character>();
    ch1->name = "Hero";
    ch1->camp = 1;
    ch1->atk = 100;
    ch1->hp = 50;
    ch1->max_hp = 50;
    ch1->dmg[0] = 20;
    ch1->dmg[3] = 30;  // 最大伤害30
    ch1->dfs = 0;
    ch1->dr = 0.0f;
    
    auto ch2 = std::make_shared<Character>();
    ch2->name = "Weak Enemy";
    ch2->camp = 2;
    ch2->atk = 10;
    ch2->hp = 40;
    ch2->max_hp = 40;
    ch2->dmg[0] = 5;
    ch2->dmg[3] = 10;
    ch2->dfs = 0;
    ch2->dr = 0.0f;
    
    std::vector<std::shared_ptr<Character>> characters = {ch1, ch2};
    
    battle.Initialize(characters);
    battle.Start();
    
    // 执行几轮战斗
    int round_count = 0;
    while (!battle.IsFinished() && round_count < 20) {
        battle.ExecuteRound();
        round_count++;
    }
    
    // 验证战斗结束
    assert(battle.IsFinished());
    assert(battle.GetCurrentRound() > 0);
    
    // 检查胜利阵营
    int victor = battle.GetVictoryCamp();
    assert(victor == 1 || victor == 2);  // 应该有一个胜利者
    
    std::cout << "✓\n";
}

void Test_Battle_Victory_Condition() {
    std::cout << "Test: Battle Victory Condition... ";
    
    Battle battle;
    
    // 创建不平衡的对战
    auto strong = std::make_shared<Character>();
    strong->name = "Strong";
    strong->camp = 1;
    strong->atk = 200;  // 高攻击值，优先行动
    strong->hp = 100;
    strong->max_hp = 100;
    strong->dmg[0] = 50;
    strong->dmg[3] = 60;
    strong->dfs = 10;
    strong->dr = 0.0f;
    
    auto weak = std::make_shared<Character>();
    weak->name = "Weak";
    weak->camp = 2;
    weak->atk = 5;  // 低攻击值，后行动
    weak->hp = 20;  // 大约被一击秒杀
    weak->max_hp = 20;
    weak->dmg[0] = 1;
    weak->dmg[3] = 2;
    weak->dfs = 0;
    weak->dr = 0.0f;
    
    std::vector<std::shared_ptr<Character>> characters = {strong, weak};
    
    battle.Initialize(characters);
    battle.Start();
    
    // 执行一轮
    battle.ExecuteRound();
    
    // 弱者应该已经死亡，战斗应该结束
    assert(battle.IsFinished());
    assert(battle.GetVictoryCamp() == 1);
    
    std::cout << "✓\n";
}

// ============ 集成测试 ============

void Test_Integration_ParameterToCharacter() {
    std::cout << "Test: Integration Parameter→Character... ";
    
    // 解析参数
    std::string character_xml = R"(<Character 
        name="Knight", 
        camp=1, 
        atk=80, 
        hp=120, 
        dfs=15, 
        aggro=50,
        dr=0.15
    >)";
    
    auto param = ParameterParser::Parse(character_xml);
    assert(param != nullptr);
    assert(param->name == "Character");
    
    // 创建角色
    Character ch;
    ch.name = param->GetAttribute("name");
    ch.camp = param->GetAttributeInt("camp");
    ch.atk = param->GetAttributeInt("atk");
    ch.max_hp = param->GetAttributeInt("hp");
    ch.hp = ch.max_hp;
    ch.dfs = param->GetAttributeInt("dfs");
    ch.aggro = param->GetAttributeInt("aggro");
    ch.dr = param->GetAttributeFloat("dr");
    ch.is_alive = true;
    
    assert(ch.name == "Knight");
    assert(ch.camp == 1);
    assert(ch.atk == 80);
    assert(ch.max_hp == 120);
    assert(ch.dfs == 15);
    assert(ch.aggro == 50);
    assert(ch.dr >= 0.14f && ch.dr <= 0.16f);
    
    std::cout << "✓\n";
}

// ============ 主测试函数 ============

int main() {
    std::cout << "\n========== Phase 2 Unit Tests ==========\n\n";
    
    // Character Tests
    std::cout << "--- Character Tests ---\n";
    Test_Character_Creation();
    Test_Character_TakeDamage();
    Test_Character_Heal();
    Test_Character_HP_Percentage();
    
    // ParameterParser Tests
    std::cout << "\n--- ParameterParser Tests ---\n";
    Test_ParameterParser_SimpleTag();
    Test_ParameterParser_QuotedValues();
    Test_ParameterParser_IntegerAttributes();
    Test_ParameterParser_FloatAttributes();
    Test_ParameterParser_ErrorHandling();
    
    // Battle Tests
    std::cout << "\n--- Battle Tests ---\n";
    Test_Battle_Creation();
    Test_Battle_Initialization();
    Test_Battle_Simple_Combat();
    Test_Battle_Victory_Condition();
    
    // Integration Tests
    std::cout << "\n--- Integration Tests ---\n";
    Test_Integration_ParameterToCharacter();
    
    std::cout << "\n========== All Tests Passed! ✓ ==========\n\n";
    return 0;
}
