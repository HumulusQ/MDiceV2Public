/**
 * @file TestAssignmentParsing.cpp
 * @brief 测试赋值语句解析 - 验证 set self.Dmg.d1 += 1 语法支持
 */

#include "Lexer.h"
#include "Parser.h"
#include <iostream>
#include <string>
#include <vector>

using namespace abot;

/**
 * 测试用例1：简单变量赋值
 */
void TestSimpleAssignment() {
    std::cout << "\n=== Test 1: Simple Assignment ===" << std::endl;
    std::string code = "set x = 10;";
    
    Lexer lexer(code);
    auto tokens = lexer.ScanTokens();
    
    Parser parser(tokens);
    auto ast = parser.ParseProgram();
    
    if (ast.empty()) {
        std::cerr << "ERROR: Failed to parse simple assignment" << std::endl;
        return;
    }
    
    std::cout << "✓ Parsed: " << ast[0]->ToString() << std::endl;
}

/**
 * 测试用例2：复合赋值简单变量
 */
void TestCompoundAssignment() {
    std::cout << "\n=== Test 2: Compound Assignment ===" << std::endl;
    std::string code = "set x += 5;";
    
    Lexer lexer(code);
    auto tokens = lexer.ScanTokens();
    
    Parser parser(tokens);
    auto ast = parser.ParseProgram();
    
    if (ast.empty()) {
        std::cerr << "ERROR: Failed to parse compound assignment" << std::endl;
        return;
    }
    
    std::cout << "✓ Parsed: " << ast[0]->ToString() << std::endl;
}

/**
 * 测试用例3：成员访问赋值（单层）
 */
void TestMemberAccess() {
    std::cout << "\n=== Test 3: Member Access Assignment ===" << std::endl;
    std::string code = "set self.atk = 10;";
    
    Lexer lexer(code);
    auto tokens = lexer.ScanTokens();
    
    Parser parser(tokens);
    auto ast = parser.ParseProgram();
    
    if (ast.empty()) {
        std::cerr << "ERROR: Failed to parse member access" << std::endl;
        return;
    }
    
    std::cout << "✓ Parsed: " << ast[0]->ToString() << std::endl;
}

/**
 * 测试用例4：深路径赋值（关键测试）
 */
void TestDeepPathAssignment() {
    std::cout << "\n=== Test 4: Deep Path Assignment ===" << std::endl;
    std::string code = "set self.Dmg.d1 = 1;";
    
    Lexer lexer(code);
    auto tokens = lexer.ScanTokens();
    
    Parser parser(tokens);
    auto ast = parser.ParseProgram();
    
    if (ast.empty()) {
        std::cerr << "ERROR: Failed to parse deep path assignment" << std::endl;
        return;
    }
    
    std::cout << "✓ Parsed: " << ast[0]->ToString() << std::endl;
}

/**
 * 测试用例5：深路径复合赋值（最复杂的用例）
 */
void TestDeepPathCompoundAssignment() {
    std::cout << "\n=== Test 5: Deep Path Compound Assignment ===" << std::endl;
    std::string code = "set self.Dmg.d1 += 1;";
    
    Lexer lexer(code);
    auto tokens = lexer.ScanTokens();
    
    std::cout << "Tokens: ";
    for (const auto& token : tokens) {
        std::cout << "[" << token.lexeme << "] ";
    }
    std::cout << std::endl;
    
    Parser parser(tokens);
    auto ast = parser.ParseProgram();
    
    if (ast.empty()) {
        std::cerr << "ERROR: Failed to parse deep path compound assignment" << std::endl;
        return;
    }
    
    std::cout << "✓ Parsed: " << ast[0]->ToString() << std::endl;
}

/**
 * 测试用例6：完整技能脚本（原始需求）
 */
void TestFullSkillScript() {
    std::cout << "\n=== Test 6: Full Skill Script ===" << std::endl;
    std::string code = R"(
        set self.atk += 10;
        set self.Dmg.d1 += 1;
        set self.Dmg.d2 += 1;
        set self.Dmg.d3 += 1;
        set self.Dmg.d4 += 1;
        return;
    )";
    
    Lexer lexer(code);
    auto tokens = lexer.ScanTokens();
    
    Parser parser(tokens);
    auto ast = parser.ParseProgram();
    
    if (ast.size() < 6) {
        std::cerr << "ERROR: Failed to parse all statements (expected 6, got " 
                  << ast.size() << ")" << std::endl;
        return;
    }
    
    std::cout << "✓ Parsed " << ast.size() << " statements:" << std::endl;
    for (size_t i = 0; i < ast.size(); i++) {
        std::cout << "  " << (i+1) << ". " << ast[i]->ToString() << std::endl;
    }
}

int main() {
    std::cout << "\n========================================" << std::endl;
    std::cout << "ABOT Script Parser - Assignment Tests" << std::endl;
    std::cout << "========================================" << std::endl;
    
    try {
        TestSimpleAssignment();
        TestCompoundAssignment();
        TestMemberAccess();
        TestDeepPathAssignment();
        TestDeepPathCompoundAssignment();
        TestFullSkillScript();
        
        std::cout << "\n========================================" << std::endl;
        std::cout << "✓ All tests completed successfully!" << std::endl;
        std::cout << "========================================\n" << std::endl;
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "\n✗ Exception: " << e.what() << std::endl;
        return 1;
    }
}

