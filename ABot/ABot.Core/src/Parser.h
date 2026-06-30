/**
 * @file Parser.h
 * @brief ABOT 语法分析器 - 将Token流转换为AST
 * 
 * AST节点类型：
 * =============
 * - 表达式：BinaryOp, UnaryOp, Literal, Variable等
 * - 语句：IfStatement, ForLoop, Assignment等
 * - 声明：FunctionDef, VariableDecl等
 */

#ifndef ABOT_PARSER_H
#define ABOT_PARSER_H

#include "Lexer.h"
#include "Value.h"
#include <memory>
#include <vector>
#include <string>

namespace abot {

// 前向声明
class ASTNode;
class Expression;
class Statement;

/**
 * @brief 抽象语法树节点的基类
 */
class ASTNode {
public:
    virtual ~ASTNode() = default;
    virtual std::string ToString() const = 0;
};

/**
 * @brief 表达式节点
 */
class Expression : public ASTNode {
public:
    virtual ~Expression() = default;
};

/**
 * @brief 二元操作表达式
 */
class BinaryOp : public Expression {
public:
    std::string op;  // 操作符
    std::unique_ptr<Expression> left;
    std::unique_ptr<Expression> right;
    
    std::string ToString() const override;
};

/**
 * @brief 一元操作表达式
 */
class UnaryOp : public Expression {
public:
    std::string op;   // 操作符
    std::unique_ptr<Expression> operand;
    
    std::string ToString() const override;
};

/**
 * @brief Literal class
 */
class Literal : public Expression {
public:
    Value value;
    
    explicit Literal(const Value& v) : value(v) {}
    std::string ToString() const override;
};

/**
 * @brief 变量引用表达式
 */
class Variable : public Expression {
public:
    std::string name;
    
    explicit Variable(const std::string& n) : name(n) {}
    std::string ToString() const override;
};

/**
 * @brief 函数调用表达式
 */
class FunctionCall : public Expression {
public:
    std::string name;
    std::vector<std::unique_ptr<Expression>> arguments;
    
    explicit FunctionCall(const std::string& n) : name(n) {}
    std::string ToString() const override;
};

/**
 * @brief 成员访问表达式 (例如: para.dmg, self.hp)
 */
class MemberAccess : public Expression {
public:
    std::unique_ptr<Expression> object;  // 对象表达式
    std::string member;                   // 成员名称
    
    MemberAccess(std::unique_ptr<Expression> obj, const std::string& m)
        : object(std::move(obj)), member(m) {}
    std::string ToString() const override;
};

/**
 * @brief 语句节点
 */
class Statement : public ASTNode {
public:
    virtual ~Statement() = default;
};

/**
 * @brief 表达式语句
 */
class ExpressionStatement : public Statement {
public:
    std::unique_ptr<Expression> expression;
    
    explicit ExpressionStatement(std::unique_ptr<Expression> e)
        : expression(std::move(e)) {}
    std::string ToString() const override;
};

/**
 * @brief If语句
 */
class IfStatement : public Statement {
public:
    std::unique_ptr<Expression> condition;
    std::vector<std::unique_ptr<Statement>> then_body;
    std::vector<std::vector<std::unique_ptr<Statement>>> elif_bodies;
    std::vector<std::unique_ptr<Expression>> elif_conditions;
    std::vector<std::unique_ptr<Statement>> else_body;
    
    std::string ToString() const override;
};

/**
 * @brief For循环语句
 */
class ForStatement : public Statement {
public:
    std::unique_ptr<Expression> iterable;  // for (each x in iterable)
    std::string iterator_name;
    std::vector<std::unique_ptr<Statement>> body;
    
    std::string ToString() const override;
};

/**
 * @brief 赋值语句 (set)
 * 支持深路径访问，如 set self.atk += 10
 */
class AssignmentStatement : public Statement {
public:
    std::unique_ptr<Expression> target;  // 赋值目标（可以是路径，如 self.atk）
    std::unique_ptr<Expression> value;   // 赋值值
    std::string op;                      // 操作符："=", "+=", "-="等
    
    std::string ToString() const override;
};

/**
 * @brief 声明语句 (declare)
 */
class DeclarationStatement : public Statement {
public:
    std::string name;
    std::unique_ptr<Expression> value;
    
    std::string ToString() const override;
};

/**
 * 语法分析器
 */
class Parser {
public:
    // ============ 构造函数 ============
    explicit Parser(const std::vector<Token>& tokens);
    ~Parser();

    // ============ 解析方法 ============
    
    /**
     * @brief 解析完整的程序
     * @return AST根节点
     */
    std::vector<std::unique_ptr<Statement>> ParseProgram();

    // ============ 错误处理 ============
    
    bool HasError() const { return has_error_; }
    std::string GetErrorMessage() const { return error_message_; }

private:
    std::vector<Token> tokens_;
    size_t current_;
    bool has_error_;
    std::string error_message_;

    // ============ 解析助手 ============
    
    Token Peek() const;
    Token Advance();
    bool Match(TokenType type);
    Token Consume(TokenType type, const std::string& message);
    bool IsAtEnd() const;
    
    void Error(const std::string& message);

    // ============ 递归下降解析方法 ============
    
    std::unique_ptr<Statement> ParseStatement();
    std::unique_ptr<Statement> ParseIfStatement();
    std::unique_ptr<Statement> ParseForStatement();
    std::unique_ptr<Statement> ParseExpressionStatement();
    
    std::unique_ptr<Expression> ParseExpression();
    std::unique_ptr<Expression> ParseLogicalOr();
    std::unique_ptr<Expression> ParseLogicalAnd();
    std::unique_ptr<Expression> ParseEquality();
    std::unique_ptr<Expression> ParseComparison();
    std::unique_ptr<Expression> ParseAddition();
    std::unique_ptr<Expression> ParseMultiplication();
    std::unique_ptr<Expression> ParseUnary();
    std::unique_ptr<Expression> ParsePostfix();  // ✅ 新增：处理成员访问等后缀操作
    std::unique_ptr<Expression> ParsePrimary();
    std::unique_ptr<Expression> ParseFunctionCall();
};

}  // namespace abot

#endif  // ABOT_PARSER_H
