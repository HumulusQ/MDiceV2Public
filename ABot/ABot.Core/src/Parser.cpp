/**
 * @file Parser.cpp
 * @brief ABOT 语法分析器的实现
 */

#include "Parser.h"
#include <cassert>

namespace abot {

// ============ AST节点方法实现 ============

std::string BinaryOp::ToString() const {
    return "(" + op + ")";
}

std::string UnaryOp::ToString() const {
    return "(" + op + ")";
}

std::string Literal::ToString() const {
    return "literal";
}

std::string Variable::ToString() const {
    return name;
}

std::string FunctionCall::ToString() const {
    return name + "()";
}

std::string MemberAccess::ToString() const {
    return "member_access";
}

std::string ExpressionStatement::ToString() const {
    return "expr_stmt";
}

std::string IfStatement::ToString() const {
    return "if";
}

std::string ForStatement::ToString() const {
    return "for";
}

std::string AssignmentStatement::ToString() const {
    return "set " + (target ? target->ToString() : "?") + " " + op;
}

std::string DeclarationStatement::ToString() const {
    return "declare " + name;
}

// ============ Parser类实现 ============

Parser::Parser(const std::vector<Token>& tokens)
    : tokens_(tokens), current_(0), has_error_(false) {
    FILE* log_file = nullptr;
    fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    if (log_file) {
        fprintf(log_file, "[Parser::Constructor] START - tokens count: %zu\n", tokens_.size());
        fclose(log_file);
    }
}

Parser::~Parser() {
}

std::vector<std::unique_ptr<Statement>> Parser::ParseProgram() {
    FILE* log_file = nullptr;
    fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    if (log_file) {
        fprintf(log_file, "[Parser::ParseProgram] START - tokens count: %zu\n", tokens_.size());
        fflush(log_file);
    }
    
    std::vector<std::unique_ptr<Statement>> statements;
    int iteration_count = 0;
    const int MAX_ITERATIONS = 100000;
    size_t last_token_index = SIZE_MAX;  // 追踪 token 位置
    
    while (!IsAtEnd()) {
        iteration_count++;
        int token_type = (int)Peek().type;
        
        // ✅ 检查 token 是否在前进
        if (current_ == last_token_index) {
            if (log_file) {
                fprintf(log_file, "[INFINITE_LOOP_DETECTED] Token not advancing!\n");
                fprintf(log_file, "[INFINITE_LOOP] current=%zu, token_type=%d, lexeme='%s'\n", 
                        current_, token_type, Peek().lexeme.c_str());
                fprintf(log_file, "[INFINITE_LOOP] Dumping remaining tokens:\n");
                for (size_t i = current_; i < tokens_.size() && i < current_ + 20; i++) {
                    fprintf(log_file, "[INFINITE_LOOP]   [%zu] type=%d, lexeme='%s'\n", 
                            i, (int)tokens_[i].type, tokens_[i].lexeme.c_str());
                }
                fflush(log_file);
            }
            break;
        }
        last_token_index = current_;
        
        if (log_file && iteration_count <= 50) {
            fprintf(log_file, "[Parser::ParseProgram] Iter %d: current=%zu, type=%d, IsAtEnd=%d\n", 
                    iteration_count, current_, token_type, IsAtEnd());
            fflush(log_file);
        }
        
        if (iteration_count > MAX_ITERATIONS) {
            if (log_file) {
                fprintf(log_file, "[Parser::ParseProgram] ERROR: MAX_ITERATIONS (%d) exceeded\n", MAX_ITERATIONS);
                fflush(log_file);
            }
            break;
        }
        
        try {
            auto stmt = ParseStatement();
            if (stmt) {
                statements.push_back(std::move(stmt));
                if (log_file && iteration_count <= 20) {
                    fprintf(log_file, "[Parser::ParseProgram] Statement added, count=%zu\n", statements.size());
                    fflush(log_file);
                }
            }
        } catch (const std::exception& e) {
            if (log_file) {
                fprintf(log_file, "[Parser::ParseProgram] EXCEPTION: %s\n", e.what());
                fflush(log_file);
            }
            Error(e.what());
            break;
        }
    }
    
    if (log_file) {
        fprintf(log_file, "[Parser::ParseProgram] FINISHED - statements=%zu, iterations=%d, has_error=%d\n", 
                statements.size(), iteration_count, has_error_);
        fflush(log_file);
        fclose(log_file);
    }
    
    return statements;
}

Token Parser::Peek() const {
    if (current_ >= tokens_.size()) {
        return Token(TokenType::EndOfFile, "", 0, 0);
    }
    return tokens_[current_];
}

Token Parser::Advance() {
    if (!IsAtEnd()) {
        current_++;
    }
    if (current_ > 0) {
        return tokens_[current_ - 1];
    }
    return Token(TokenType::Error, "", 0, 0);
}

bool Parser::Match(TokenType type) {
    if (Peek().type != type) {
        return false;
    }
    Advance();
    return true;
}

Token Parser::Consume(TokenType type, const std::string& message) {
    if (Peek().type != type) {
        Error(message);
        return Token(TokenType::Error, "", 0, 0);
    }
    return Advance();
}

bool Parser::IsAtEnd() const {
    return current_ >= tokens_.size() || Peek().type == TokenType::EndOfFile;
}

void Parser::Error(const std::string& message) {
    has_error_ = true;
    error_message_ = message;
    std::string line_info = " at line " + std::to_string(Peek().line) + 
                           ", column " + std::to_string(Peek().column);
    error_message_ += line_info;
}

std::unique_ptr<Statement> Parser::ParseStatement() {
    if (Match(TokenType::If)) {
        return ParseIfStatement();
    }
    if (Match(TokenType::For)) {
        return ParseForStatement();
    }
    if (Match(TokenType::Return)) {
        // ✅ return语句支持
        auto stmt = std::make_unique<ExpressionStatement>(
            std::make_unique<Literal>(Value(nullptr))
        );
        // 消费结尾的分号
        Match(TokenType::Semicolon);
        return stmt;
    }
    if (Match(TokenType::Set)) {
        // set 语句 - 支持深路径和复合赋值操作符
        // 解析赋值目标（支持深路径如 self.atk, self.Dmg.d1）
        auto target = ParsePostfix();
        if (!target) {
            Error("Expected assignment target after 'set'");
            return nullptr;
        }
        
        // 检测赋值操作符（=, +=, -=, *=, /=, %=）
        TokenType op_type = Peek().type;
        std::string op_str;
        
        if (op_type == TokenType::Equal) {
            op_str = "=";
            Advance();
        } else if (op_type == TokenType::PlusEqual) {
            op_str = "+=";
            Advance();
        } else if (op_type == TokenType::MinusEqual) {
            op_str = "-=";
            Advance();
        } else if (op_type == TokenType::StarEqual) {
            op_str = "*=";
            Advance();
        } else if (op_type == TokenType::SlashEqual) {
            op_str = "/=";
            Advance();
        } else if (op_type == TokenType::PercentEqual) {
            op_str = "%=";
            Advance();
        } else {
            Error("Expected assignment operator (=, +=, -=, *=, /=, %=)");
            return nullptr;
        }
        
        // 解析赋值值
        auto value = ParseExpression();
        if (!value) {
            Error("Expected expression after assignment operator");
            return nullptr;
        }
        
        auto stmt = std::make_unique<AssignmentStatement>();
        stmt->target = std::move(target);
        stmt->value = std::move(value);
        stmt->op = op_str;
        
        // 消费结尾的分号
        Match(TokenType::Semicolon);
        
        return stmt;
    }
    if (Match(TokenType::Declare)) {
        // declare 语句
        std::string name = Consume(TokenType::Identifier, "Expected variable name").lexeme;
        Consume(TokenType::Equal, "Expected '='");
        auto expr = ParseExpression();
        auto stmt = std::make_unique<DeclarationStatement>();
        stmt->name = name;
        stmt->value = std::move(expr);
        // 消费结尾的分号
        Match(TokenType::Semicolon);
        return stmt;
    }
    if (Match(TokenType::Let)) {
        // let 语句 - 与 declare 相同的语义
        std::string name = Consume(TokenType::Identifier, "Expected variable name").lexeme;
        Consume(TokenType::Equal, "Expected '='");
        auto expr = ParseExpression();
        auto stmt = std::make_unique<DeclarationStatement>();
        stmt->name = name;
        stmt->value = std::move(expr);
        // 消费结尾的分号
        Match(TokenType::Semicolon);
        return stmt;
    }
    
    // 默认：表达式语句 (fallback)
    FILE* parse_debug = nullptr;
    fopen_s(&parse_debug, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    
    if (parse_debug) {
        fprintf(parse_debug, "[ParseStatement] Fallback branch: current=%zu, token_type=%d, lexeme='%s'\n", 
                current_, (int)Peek().type, Peek().lexeme.c_str());
        fflush(parse_debug);
    }
    
    auto expr = ParseExpression();
    
    if (parse_debug) {
        fprintf(parse_debug, "[ParseStatement] After ParseExpression: expr=%s, current=%zu\n", 
                expr ? "valid" : "nullptr", current_);
        fflush(parse_debug);
    }
    
    // ✅ 关键修复：如果 ParseExpression 返回 nullptr，说明没有有效的表达式
    // 此时需要消费至少一个 token 以防止无限循环
    if (!expr) {
        if (parse_debug) {
            fprintf(parse_debug, "[ParseStatement] Expression is nullptr! Consuming one token to avoid infinite loop.\n");
            fflush(parse_debug);
        }
        
        // 消费一个 token 以防止无限循环
        if (!IsAtEnd()) {
            Advance();
        }
        
        if (parse_debug) {
            fclose(parse_debug);
        }
        
        return nullptr;  // 返回 nullptr 而不是创建一个无效的 ExpressionStatement
    }
    
    if (Match(TokenType::Semicolon)) {
        // 可选的分号
    }
    
    if (parse_debug) {
        fclose(parse_debug);
    }
    
    return std::make_unique<ExpressionStatement>(std::move(expr));
}

std::unique_ptr<Statement> Parser::ParseIfStatement() {
    // 已消费'if'
    auto stmt = std::make_unique<IfStatement>();
    
    // 条件
    Consume(TokenType::LeftParen, "Expected '(' after 'if'");
    stmt->condition = ParseExpression();
    Consume(TokenType::RightParen, "Expected ')' after if condition");
    
    // then体
    Consume(TokenType::LeftBrace, "Expected '{' after if condition");
    while (Peek().type != TokenType::RightBrace && !IsAtEnd()) {
        auto s = ParseStatement();
        if (s) stmt->then_body.push_back(std::move(s));
    }
    Consume(TokenType::RightBrace, "Expected '}' to close if body");
    
    // elif和else
    while (Match(TokenType::Else)) {
        if (Match(TokenType::If)) {
            // elif分支
            Consume(TokenType::LeftParen, "Expected '(' after 'if'");
            auto elif_cond = ParseExpression();
            stmt->elif_conditions.push_back(std::move(elif_cond));
            Consume(TokenType::RightParen, "Expected ')' after elif condition");
            
            Consume(TokenType::LeftBrace, "Expected '{' after elif condition");
            std::vector<std::unique_ptr<Statement>> elif_stmts;
            while (Peek().type != TokenType::RightBrace && !IsAtEnd()) {
                auto s = ParseStatement();
                if (s) elif_stmts.push_back(std::move(s));
            }
            stmt->elif_bodies.push_back(std::move(elif_stmts));
            Consume(TokenType::RightBrace, "Expected '}' to close elif body");
        } else {
            // else分支
            Consume(TokenType::LeftBrace, "Expected '{' after 'else'");
            while (Peek().type != TokenType::RightBrace && !IsAtEnd()) {
                auto s = ParseStatement();
                if (s) stmt->else_body.push_back(std::move(s));
            }
            Consume(TokenType::RightBrace, "Expected '}' to close else body");
            break;
        }
    }
    
    return stmt;
}

std::unique_ptr<Statement> Parser::ParseForStatement() {
    // 已消费'for'
    auto stmt = std::make_unique<ForStatement>();
    
    // 期望: for (each x in iterable)
    Consume(TokenType::LeftParen, "Expected '(' after 'for'");
    
    // "each" 关键字（可选）
    if (Match(TokenType::Identifier)) {
        if (tokens_[current_ - 1].lexeme != "each") {
            // 这可能是另一种for循环形式，暂不支持
            Error("Unsupported for loop syntax");
            return nullptr;
        }
    }
    
    // 迭代器变量
    stmt->iterator_name = Consume(TokenType::Identifier, "Expected iterator variable").lexeme;
    
    // "in" 关键字
    if (!Match(TokenType::Identifier) || tokens_[current_ - 1].lexeme != "in") {
        Error("Expected 'in' in for loop");
        return nullptr;
    }
    
    // 可迭代对象
    stmt->iterable = ParseExpression();
    
    Consume(TokenType::RightParen, "Expected ')' after for clause");
    
    // 循环体
    Consume(TokenType::LeftBrace, "Expected '{' after for clause");
    while (Peek().type != TokenType::RightBrace && !IsAtEnd()) {
        auto s = ParseStatement();
        if (s) stmt->body.push_back(std::move(s));
    }
    Consume(TokenType::RightBrace, "Expected '}' to close for body");
    
    return stmt;
}

std::unique_ptr<Statement> Parser::ParseExpressionStatement() {
    auto expr = ParseExpression();
    Match(TokenType::Semicolon);  // 可选的分号
    return std::make_unique<ExpressionStatement>(std::move(expr));
}

std::unique_ptr<Expression> Parser::ParseExpression() {
    return ParseLogicalOr();
}

std::unique_ptr<Expression> Parser::ParseLogicalOr() {
    auto expr = ParseLogicalAnd();
    
    while (Peek().type == TokenType::Or) {
        Advance();
        auto right = ParseLogicalAnd();
        auto binary_op = std::make_unique<BinaryOp>();
        binary_op->op = "||";
        binary_op->left = std::move(expr);
        binary_op->right = std::move(right);
        expr = std::move(binary_op);
    }
    
    return expr;
}

std::unique_ptr<Expression> Parser::ParseLogicalAnd() {
    auto expr = ParseEquality();
    
    while (Peek().type == TokenType::And) {
        Advance();
        auto right = ParseEquality();
        auto binary_op = std::make_unique<BinaryOp>();
        binary_op->op = "&&";
        binary_op->left = std::move(expr);
        binary_op->right = std::move(right);
        expr = std::move(binary_op);
    }
    
    return expr;
}

std::unique_ptr<Expression> Parser::ParseEquality() {
    auto expr = ParseComparison();
    
    while (Peek().type == TokenType::EqualEqual || 
           Peek().type == TokenType::NotEqual) {
        std::string op = Peek().lexeme;
        Advance();
        auto right = ParseComparison();
        auto binary_op = std::make_unique<BinaryOp>();
        binary_op->op = op;
        binary_op->left = std::move(expr);
        binary_op->right = std::move(right);
        expr = std::move(binary_op);
    }
    
    return expr;
}

std::unique_ptr<Expression> Parser::ParseComparison() {
    auto expr = ParseAddition();
    
    while (Peek().type == TokenType::Less ||
           Peek().type == TokenType::LessEqual ||
           Peek().type == TokenType::Greater ||
           Peek().type == TokenType::GreaterEqual) {
        std::string op = Peek().lexeme;
        Advance();
        auto right = ParseAddition();
        auto binary_op = std::make_unique<BinaryOp>();
        binary_op->op = op;
        binary_op->left = std::move(expr);
        binary_op->right = std::move(right);
        expr = std::move(binary_op);
    }
    
    return expr;
}

std::unique_ptr<Expression> Parser::ParseAddition() {
    auto expr = ParseMultiplication();
    
    while (Peek().type == TokenType::Plus || 
           Peek().type == TokenType::Minus) {
        std::string op = Peek().lexeme;
        Advance();
        auto right = ParseMultiplication();
        auto binary_op = std::make_unique<BinaryOp>();
        binary_op->op = op;
        binary_op->left = std::move(expr);
        binary_op->right = std::move(right);
        expr = std::move(binary_op);
    }
    
    return expr;
}

std::unique_ptr<Expression> Parser::ParseMultiplication() {
    auto expr = ParseUnary();
    
    while (Peek().type == TokenType::Star ||
           Peek().type == TokenType::Slash ||
           Peek().type == TokenType::Percent) {
        std::string op = Peek().lexeme;
        Advance();
        auto right = ParseUnary();
        auto binary_op = std::make_unique<BinaryOp>();
        binary_op->op = op;
        binary_op->left = std::move(expr);
        binary_op->right = std::move(right);
        expr = std::move(binary_op);
    }
    
    return expr;
}

std::unique_ptr<Expression> Parser::ParseUnary() {
    if (Peek().type == TokenType::Not ||
        Peek().type == TokenType::Minus) {
        std::string op = Peek().lexeme;
        Advance();
        auto operand = ParseUnary();
        auto unary_op = std::make_unique<UnaryOp>();
        unary_op->op = op;
        unary_op->operand = std::move(operand);
        return unary_op;
    }
    
    return ParsePostfix();  // ✅ 修改：调用 ParsePostfix 而不是 ParsePrimary
}

std::unique_ptr<Expression> Parser::ParsePostfix() {
    auto expr = ParsePrimary();
    
    // ✅ 如果 ParsePrimary() 返回 nullptr，直接返回
    if (!expr) {
        return nullptr;
    }
    
    // 处理成员访问 (.)
    while (Peek().type == TokenType::Dot) {
        Advance();  // 消费 '.'
        
        if (Peek().type != TokenType::Identifier) {
            Error("Expected identifier after '.'");
            return nullptr;
        }
        
        std::string member = Peek().lexeme;
        Advance();
        
        expr = std::make_unique<MemberAccess>(std::move(expr), member);
    }
    
    return expr;
}

std::unique_ptr<Expression> Parser::ParsePrimary() {
    if (Match(TokenType::Integer)) {
        int64_t value = tokens_[current_ - 1].int_value;
        return std::make_unique<Literal>(Value(value));
    }
    
    if (Match(TokenType::Double)) {
        double value = tokens_[current_ - 1].double_value;
        return std::make_unique<Literal>(Value(value));
    }
    
    if (Match(TokenType::String)) {
        std::string value = tokens_[current_ - 1].lexeme;
        return std::make_unique<Literal>(Value(value));
    }
    
    if (Match(TokenType::True)) {
        return std::make_unique<Literal>(Value(true));
    }
    
    if (Match(TokenType::False)) {
        return std::make_unique<Literal>(Value(false));
    }
    
    if (Match(TokenType::Null)) {
        return std::make_unique<Literal>(Value(nullptr));
    }
    
    if (Match(TokenType::Identifier)) {
        std::string name = tokens_[current_ - 1].lexeme;
        
        // 检查是否是函数调用
        if (Peek().type == TokenType::LeftParen) {
            Advance();  // 消费'('
            auto func_call = std::make_unique<FunctionCall>(name);
            
            // 解析参数
            while (Peek().type != TokenType::RightParen && !IsAtEnd()) {
                func_call->arguments.push_back(ParseExpression());
                
                // 参数分隔符
                if (Peek().type == TokenType::Comma) {
                    Advance();
                }
            }
            
            Consume(TokenType::RightParen, "Expected ')' after function arguments");
            return func_call;
        }
        
        // 普通变量引用
        return std::make_unique<Variable>(name);
    }
    
    if (Match(TokenType::LeftParen)) {
        auto expr = ParseExpression();
        Consume(TokenType::RightParen, "Expected ')'");
        return expr;
    }
    
    Error("Unexpected token in expression");
    return nullptr;
}

std::unique_ptr<Expression> Parser::ParseFunctionCall() {
    // 此方法已在ParsePrimary中实现
    // 保留以兼容接口
    return nullptr;
}

}  // namespace abot
