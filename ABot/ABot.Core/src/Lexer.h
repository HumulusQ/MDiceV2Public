/**
 * @file Lexer.h
 * @brief ABOT 词法分析器 - 将源代码分解为Token流
 * 
 * Token类型：
 * ===========
 * - 关键字：if, else, for, set, declare等
 * - 标识符：变量名、函数名等
 * - 数字：整数、浮点数
 * - 字符串："..."
 * - 操作符：+, -, *, /, =, ==, !=等
 * - 分隔符：(, ), {, }, [, ], ;等
 * - 特殊：#(骰子操作)、do(调用)等
 */

#ifndef ABOT_LEXER_H
#define ABOT_LEXER_H

#include <string>
#include <vector>
#include <memory>

namespace abot {

// Token类型枚举
enum class TokenType : int {
    // 字面量
    Integer,
    Double,
    String,
    Identifier,

    // 关键字
    If, Else, Elif,
    For, While, Do,
    Set, Let, Declare, Return,
    True, False, Null,

    // 操作符
    Plus,           // +
    Minus,          // -
    Star,           // *
    Slash,          // /
    Percent,        // %
    Equal,          // =
    EqualEqual,     // ==
    NotEqual,       // !=
    Less,           // <
    LessEqual,      // <=
    Greater,        // >
    GreaterEqual,   // >=
    And,            // &&
    Or,             // ||
    Not,            // !
    
    // 复合赋值操作符
    PlusEqual,      // +=
    MinusEqual,     // -=
    StarEqual,      // *=
    SlashEqual,     // /=
    PercentEqual,   // %=

    // 分隔符
    LeftParen,      // (
    RightParen,     // )
    LeftBrace,      // {
    RightBrace,     // }
    LeftBracket,    // [
    RightBracket,   // ]
    Semicolon,      // ;
    Comma,          // ,
    Dot,            // .

    // 特殊
    Hash,           // # (骰子)
    Expr,           // expr
    AKR,            // @ (anke roll)

    // 元
    EndOfFile,
    Error,
};

/**
 * @brief 单个Token
 */
struct Token {
    TokenType type;
    std::string lexeme;    // 字符串形式
    int line;              // 行号
    int column;            // 列号
    
    // 字面量值（如果适用）
    union {
        int64_t int_value;
        double double_value;
    };

    Token(TokenType t, const std::string& lex, int l, int c)
        : type(t), lexeme(lex), line(l), column(c), int_value(0) {}
};

/**
 * @brief 词法分析器
 * 将源代码字符串转换为Token序列
 */
class Lexer {
public:
    // ============ 构造函数 ============
    explicit Lexer(const std::string& source);
    ~Lexer();

    // ============ 扫描操作 ============
    
    /**
     * @brief 扫描所有Token
     * @return Token列表
     */
    std::vector<Token> ScanTokens();

    /**
     * @brief 获取下一个Token
     * @return Token，或在EOF时返回类型为EndOfFile的Token
     */
    Token NextToken();

    /**
     * @brief 查看当前Token而不消耗
     */
    Token Peek() const;

    /**
     * @brief 回溯到上一个Token
     */
    void Unget();

    // ============ 错误处理 ============
    
    bool HasError() const { return has_error_; }
    std::string GetErrorMessage() const { return error_message_; }

private:
    std::string source_;
    size_t current_;      // 当前位置
    size_t line_;         // 当前行
    size_t column_;       // 当前列
    
    Token last_token_;    // 最后返回的Token（用于Unget）
    bool has_error_;
    std::string error_message_;

    // ============ 扫描助手 ============
    
    char PeekChar(size_t offset = 0) const;
    char Advance();
    bool Match(char expected);
    bool IsAtEnd() const { return current_ >= source_.length(); }
    
    Token ScanToken();
    Token ScanNumber();
    Token ScanString();
    Token ScanIdentifier();
    Token MakeToken(TokenType type, const std::string& lexeme);
    
    bool IsDigit(char c) const;
    bool IsAlpha(char c) const;
    bool IsAlphaNumeric(char c) const;
    
    std::string GetKeywordOrIdentifier(const std::string& text);
};

}  // namespace abot

#endif  // ABOT_LEXER_H
