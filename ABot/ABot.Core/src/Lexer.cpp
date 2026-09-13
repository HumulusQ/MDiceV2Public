/**
 * @file Lexer.cpp
 * @brief ABOT 词法分析器的实现
 */

#include "Lexer.h"
#include <cctype>
#include <cassert>
#include <sstream>
#include <iomanip>

namespace abot {

Lexer::Lexer(const std::string& source)
    : source_(source), current_(0), line_(1), column_(1),
      last_token_(TokenType::Error, "", 0, 0), has_error_(false) {
}

Lexer::~Lexer() {
}

std::vector<Token> Lexer::ScanTokens() {
    std::vector<Token> tokens;
    size_t token_count = 0;
    const size_t MAX_TOKENS = 100000;  // 最多100000个Token以防止无限循环
    
    FILE* debug_log = nullptr;
    fopen_s(&debug_log, "C:\\Windows\\Temp\\abot_lexer_debug.log", "at");
    if (debug_log) {
        fprintf(debug_log, "[ScanTokens] Starting, source length=%zu\n", source_.length());
    }
    
    while (!IsAtEnd() && token_count < MAX_TOKENS) {
        size_t pos_before = current_;
        Token token = NextToken();
        
        if (debug_log && token_count < 100) {
            fprintf(debug_log, "[ScanTokens] Token %zu: type=%d, current moved from %zu to %zu\n", 
                    token_count, (int)token.type, pos_before, current_);
        }
        
        if (token.type != TokenType::Error) {
            tokens.push_back(token);
            if (debug_log) {
                fprintf(debug_log, "[ScanTokens_PUSH] Added token %zu: type=%d (IsEOF=%d)\n", 
                        token_count, (int)token.type, token.type == TokenType::EndOfFile ? 1 : 0);
            }
        }
        
        // 检查是否卡住（指针没有前进）
        if (current_ == pos_before && token.type != TokenType::EndOfFile) {
            if (debug_log) {
                fprintf(debug_log, "[ScanTokens] ERROR: Pointer not advancing! Stuck at pos %zu\n", current_);
                fclose(debug_log);
            }
            has_error_ = true;
            error_message_ = "Lexer stuck in infinite loop";
            break;
        }
        
        token_count++;
        
        if (token.type == TokenType::EndOfFile) {
            if (debug_log) {
                fprintf(debug_log, "[ScanTokens_EOF_BREAK] Breaking at EOF, token_count=%zu\n", token_count);
            }
            break;
        }
    }
    
    if (debug_log) {
        fprintf(debug_log, "[ScanTokens] Loop finished, about to add EOF token if not already present\n");
        fprintf(debug_log, "[ScanTokens] tokens.size()=%zu, IsAtEnd=%d\n", tokens.size(), IsAtEnd());
    }
    
    // ✅ 确保总是添加 EOF token 作为最后一个 token
    if (tokens.empty() || tokens.back().type != TokenType::EndOfFile) {
        Token eof_token(TokenType::EndOfFile, "", line_, column_);
        tokens.push_back(eof_token);
        if (debug_log) {
            fprintf(debug_log, "[ScanTokens] Added EOF token explicitly\n");
        }
    }
    
    if (debug_log) {
        fprintf(debug_log, "[ScanTokens] Finished: %zu tokens (including EOF), errors=%d\n", 
                tokens.size(), has_error_ ? 1 : 0);
        fprintf(debug_log, "[ScanTokens] Last token type: %d\n", 
                tokens.empty() ? -1 : (int)tokens.back().type);
        fclose(debug_log);
    }
    
    if (token_count >= MAX_TOKENS) {
        has_error_ = true;
        error_message_ = "Too many tokens (possible infinite loop)";
    }
    
    return tokens;
}

Token Lexer::NextToken() {
    // 跳过空白符
    while (!IsAtEnd()) {
        char c = PeekChar();
        if (c == ' ' || c == '\t' || c == '\r') {
            Advance();
        } else if (c == '\n') {
            Advance();
            line_++;
            column_ = 1;
        } else if (c == '/' && PeekChar(1) == '/') {
            // 单行注释
            while (!IsAtEnd() && PeekChar() != '\n') {
                Advance();
            }
        } else {
            break;
        }
    }
    
    if (IsAtEnd()) {
        return MakeToken(TokenType::EndOfFile, "");
    }
    
    return ScanToken();
}

Token Lexer::Peek() const {
    // 这个方法应该返回下一个Token，但这里我们已经有了NextToken()
    // 所以这个方法应该做的是缓化Token
    // 为简化起见，这里仅声明
    return Token(TokenType::Error, "", line_, column_);
}

void Lexer::Unget() {
    // 简化实现：将current_回溯
    current_ -= last_token_.lexeme.length();
}

char Lexer::PeekChar(size_t offset) const {
    size_t pos = current_ + offset;
    if (pos >= source_.length()) {
        return '\0';
    }
    return source_[pos];
}

char Lexer::Advance() {
    char c = source_[current_++];
    column_++;
    return c;
}

bool Lexer::Match(char expected) {
    if (IsAtEnd()) return false;
    if (PeekChar() != expected) return false;
    Advance();
    return true;
}

Token Lexer::ScanToken() {
    char c = Advance();
    unsigned char uc = static_cast<unsigned char>(c);
    
    // 处理注释：检查当前字符是'/'且下一个字符也是'/'
    if (c == '/' && PeekChar() == '/') {
        // 跳过"//"注释到行末
        while (!IsAtEnd() && PeekChar() != '\n') {
            Advance();
        }
        // 跳过换行符
        if (!IsAtEnd() && PeekChar() == '\n') {
            Advance();
            line_++;
            column_ = 1;
        }
        // 递归调用以获取下一个真实token
        return ScanToken();
    }
    
    // 调试：检查是否是UTF-8多字节
    FILE* debug_log = nullptr;
    fopen_s(&debug_log, "C:\\Windows\\Temp\\abot_lexer_debug.log", "at");
    if (debug_log && uc > 127) {
        fprintf(debug_log, "[ScanToken] UTF-8 byte: 0x%02X (%d), IsAlpha=%d\n", uc, (int)c, IsAlpha(c) ? 1 : 0);
    }
    
    // 单字符Token
    switch (c) {
        case '(': return MakeToken(TokenType::LeftParen, "(");
        case ')': return MakeToken(TokenType::RightParen, ")");
        case '{': return MakeToken(TokenType::LeftBrace, "{");
        case '}': return MakeToken(TokenType::RightBrace, "}");
        case '[': return MakeToken(TokenType::LeftBracket, "[");
        case ']': return MakeToken(TokenType::RightBracket, "]");
        case ';': return MakeToken(TokenType::Semicolon, ";");
        case ',': return MakeToken(TokenType::Comma, ",");
        case '.': return MakeToken(TokenType::Dot, ".");
        case '#': return MakeToken(TokenType::Hash, "#");
        case '@': return MakeToken(TokenType::AKR, "@");
    }
    
    // 处理复合赋值操作符 and 基础操作符
    if (c == '+') {
        if (Match('=')) return MakeToken(TokenType::PlusEqual, "+=");
        return MakeToken(TokenType::Plus, "+");
    }
    if (c == '-') {
        if (Match('=')) return MakeToken(TokenType::MinusEqual, "-=");
        return MakeToken(TokenType::Minus, "-");
    }
    if (c == '*') {
        if (Match('=')) return MakeToken(TokenType::StarEqual, "*=");
        return MakeToken(TokenType::Star, "*");
    }
    if (c == '/') {
        if (Match('=')) return MakeToken(TokenType::SlashEqual, "/=");
        return MakeToken(TokenType::Slash, "/");
    }
    if (c == '%') {
        if (Match('=')) return MakeToken(TokenType::PercentEqual, "%=");
        return MakeToken(TokenType::Percent, "%");
    }
    
    // 多字符Token
    if (c == '=') {
        if (Match('=')) {
            return MakeToken(TokenType::EqualEqual, "==");
        }
        return MakeToken(TokenType::Equal, "=");
    }
    if (c == '!') {
        if (Match('=')) {
            return MakeToken(TokenType::NotEqual, "!=");
        }
        return MakeToken(TokenType::Not, "!");
    }
    if (c == '<') {
        if (Match('=')) {
            return MakeToken(TokenType::LessEqual, "<=");
        }
        return MakeToken(TokenType::Less, "<");
    }
    if (c == '>') {
        if (Match('=')) {
            return MakeToken(TokenType::GreaterEqual, ">=");
        }
        return MakeToken(TokenType::Greater, ">");
    }
    if (c == '&' && Match('&')) {
        return MakeToken(TokenType::And, "&&");
    }
    if (c == '|' && Match('|')) {
        return MakeToken(TokenType::Or, "||");
    }
    
    // 字符串
    if (c == '"') {
        return ScanString();
    }
    
    // 数字
    if (IsDigit(c)) {
        current_--;
        return ScanNumber();
    }
    
    // 标识符或关键字
    if (IsAlpha(c)) {
        if (debug_log) {
            fprintf(debug_log, "[ScanToken] Recognized as identifier start\n");
            fclose(debug_log);
        }
        current_--;
        return ScanIdentifier();
    }
    
    // 未知字符
    if (debug_log) {
        fprintf(debug_log, "[ScanToken] Unknown character: 0x%02X (%d) IsAlpha=%d IsDigit=%d\n", uc, (int)c, IsAlpha(c) ? 1 : 0, IsDigit(c) ? 1 : 0);
        fclose(debug_log);
    }
    has_error_ = true;
    
    // 构建详细的错误消息
    std::stringstream ss;
    ss << "Unexpected character at position " << (current_ - 1) << ": ";
    ss << "0x" << std::hex << std::setw(2) << std::setfill('0') << (unsigned char)c;
    ss << " (decimal " << std::dec << (int)c << ")";
    if (isprint(c)) {
        ss << " ('" << c << "')";
    }
    ss << "\nContext: ...";
    
    // 显示前后文本
    size_t ctx_start = (current_ > 10) ? current_ - 10 : 0;
    size_t ctx_end = std::min(current_ + 10, source_.length());
    ss << source_.substr(ctx_start, current_ - ctx_start) << "[HERE at pos " << (current_ - 1) << "]";
    if (ctx_end > current_) {
        ss << source_.substr(current_, std::min(size_t(10), ctx_end - current_));
    }
    ss << "...";
    
    // 添加周围字节的十六进制转储
    ss << "\nHex bytes around position " << (current_ - 1) << ": ";
    for (size_t i = ctx_start; i < ctx_end; i++) {
        unsigned char byte = static_cast<unsigned char>(source_[i]);
        ss << std::hex << std::setw(2) << std::setfill('0') << (int)byte << " ";
    }
    
    error_message_ = ss.str();
    return MakeToken(TokenType::Error, std::string(1, c));
}

Token Lexer::ScanNumber() {
    size_t start = current_;
    
    while (!IsAtEnd() && IsDigit(PeekChar())) {
        Advance();
    }
    
    // 检查小数点
    if (!IsAtEnd() && PeekChar() == '.' && IsDigit(PeekChar(1))) {
        Advance();  // 消耗点号
        while (!IsAtEnd() && IsDigit(PeekChar())) {
            Advance();
        }
        std::string lexeme = source_.substr(start, current_ - start);
        Token token = MakeToken(TokenType::Double, lexeme);
        token.double_value = std::stod(lexeme);
        return token;
    }
    
    std::string lexeme = source_.substr(start, current_ - start);
    Token token = MakeToken(TokenType::Integer, lexeme);
    token.int_value = std::stoll(lexeme);
    return token;
}

Token Lexer::ScanString() {
    size_t start = current_;
    
    while (!IsAtEnd() && PeekChar() != '"') {
        if (PeekChar() == '\\') {
            Advance();  // 跳过转义字符
        }
        Advance();
    }
    
    if (IsAtEnd()) {
        has_error_ = true;
        error_message_ = "Unterminated string";
        return MakeToken(TokenType::Error, "");
    }
    
    Advance();  // 消耗结尾引号
    std::string lexeme = source_.substr(start, current_ - start);
    return MakeToken(TokenType::String, lexeme);
}

Token Lexer::ScanIdentifier() {
    size_t start = current_;
    size_t iter_count = 0;
    const size_t MAX_ITERATIONS = source_.length() + 100;  // 防止无限循环
    
    FILE* debug_log = nullptr;
    fopen_s(&debug_log, "C:\\Windows\\Temp\\abot_lexer_debug.log", "at");
    
    while (!IsAtEnd() && iter_count < MAX_ITERATIONS) {
        iter_count++;
        char c = PeekChar();
        unsigned char uc = static_cast<unsigned char>(c);
        
        if (debug_log && iter_count < 50) {
            fprintf(debug_log, "[ScanIdentifier iter %zu] byte=0x%02X IsAlphaNum=%d mask_C0=0x%02X\n", 
                    iter_count, uc, IsAlphaNumeric(c) ? 1 : 0, uc & 0xC0);
        }
        
        // 继续标识符如果是：
        // 1. ASCII字母/数字/下划线
        // 2. UTF-8首字节（高位被设置）
        if (IsAlphaNumeric(c)) {
            Advance();
        }
        else {
            break;
        }
    }
    
    if (iter_count >= MAX_ITERATIONS && debug_log) {
        fprintf(debug_log, "[ScanIdentifier] WARNING: Hit iteration limit!\n");
    }
    
    if (debug_log) {
        fprintf(debug_log, "[ScanIdentifier] Total iterations: %zu, start=%zu, current=%zu\n", iter_count, start, current_);
        fclose(debug_log);
    }
    
    std::string lexeme = source_.substr(start, current_ - start);
    std::string keyword = GetKeywordOrIdentifier(lexeme);
    
    if (keyword == "if") return MakeToken(TokenType::If, lexeme);
    if (keyword == "else") return MakeToken(TokenType::Else, lexeme);
    if (keyword == "elif") return MakeToken(TokenType::Elif, lexeme);
    if (keyword == "for") return MakeToken(TokenType::For, lexeme);
    if (keyword == "while") return MakeToken(TokenType::While, lexeme);
    if (keyword == "do") return MakeToken(TokenType::Do, lexeme);
    if (keyword == "set") return MakeToken(TokenType::Set, lexeme);
    if (keyword == "let") return MakeToken(TokenType::Let, lexeme);
    if (keyword == "declare") return MakeToken(TokenType::Declare, lexeme);
    if (keyword == "return") return MakeToken(TokenType::Return, lexeme);
    if (keyword == "true") return MakeToken(TokenType::True, lexeme);
    if (keyword == "false") return MakeToken(TokenType::False, lexeme);
    if (keyword == "null") return MakeToken(TokenType::Null, lexeme);
    if (keyword == "expr") return MakeToken(TokenType::Expr, lexeme);
    
    return MakeToken(TokenType::Identifier, lexeme);
}

Token Lexer::MakeToken(TokenType type, const std::string& lexeme) {
    Token token(type, lexeme, line_, column_);
    last_token_ = token;
    return token;
}

bool Lexer::IsDigit(char c) const {
    return std::isdigit(static_cast<unsigned char>(c));
}

bool Lexer::IsAlpha(char c) const {
    unsigned char uc = static_cast<unsigned char>(c);
    // ASCII字母或下划线
    if (std::isalpha(uc) || c == '_') {
        return true;
    }
    // UTF-8多字节字符的第一个字节（高位被设置）
    // UTF-8编码：
    // - 1字节：0xxxxxxx (ASCII)
    // - 2字节：110xxxxx 10xxxxxx
    // - 3字节：1110xxxx 10xxxxxx 10xxxxxx
    // - 4字节：11110xxx 10xxxxxx 10xxxxxx 10xxxxxx
    // 我们接受任何高位被设置的字节作为非ASCII标识符的一部分
    if ((uc & 0x80) != 0) {
        return true;  // UTF-8多字节序列
    }
    return false;
}

bool Lexer::IsAlphaNumeric(char c) const {
    unsigned char uc = static_cast<unsigned char>(c);
    // 检查是否是ASCII字母、数字或下划线
    if (std::isalnum(uc) || c == '_') {
        return true;
    }
    // UTF-8多字节字符
    if ((uc & 0x80) != 0) {
        return true;
    }
    return false;
}

std::string Lexer::GetKeywordOrIdentifier(const std::string& text) {
    return text;
}

}  // namespace abot
