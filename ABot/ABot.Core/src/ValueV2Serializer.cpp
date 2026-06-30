#include "ValueV2Serializer.h"
#include <sstream>
#include <iomanip>
#include <cctype>
#include <cmath>
#include <stdexcept>

namespace ABot {

// ====================================================================
// SerializationError 实现
// ====================================================================

std::string SerializationError::GetFullMessage() const
{
    std::ostringstream oss;
    oss << "Serialization Error";
    if (line_ > 0) {
        oss << " at line " << line_;
        if (column_ > 0) {
            oss << ", column " << column_;
        }
    }
    oss << ": " << message_;
    return oss.str();
}

// ====================================================================
// ValueV2Serializer::Serialize 实现
// ====================================================================

std::string ValueV2Serializer::Serialize(
    const ValueV2& value, 
    bool prettyPrint)
{
    try {
        return SerializeValue(value, 0);
    } catch (const SerializationError& e) {
        throw;
    } catch (const std::exception& e) {
        throw SerializationError(std::string("Serialization failed: ") + e.what());
    }
}

std::string ValueV2Serializer::SerializeValue(
    const ValueV2& value, 
    size_t depth)
{
    // 检查递归深度
    if (depth > MAX_RECURSION_DEPTH) {
        throw SerializationError("Maximum recursion depth exceeded");
    }

    switch (value.GetType()) {
        case ValueType::Null:
            return SerializeNull();
        
        case ValueType::Int:
            return SerializeInt(value.AsInt());
        
        case ValueType::Double:
            return SerializeDouble(value.AsDouble());
        
        case ValueType::Bool:
            return SerializeBool(value.AsBool());
        
        case ValueType::String:
            return SerializeString(value.AsString());
        
        case ValueType::Schema: {
            auto schema = value.GetSchemaPtr();
            if (!schema) {
                return SerializeNull();
            }
            return SerializeSchema(schema.get(), depth + 1);
        }
        
        case ValueType::Array: {
            auto array = value.GetArrayPtr();
            if (!array) {
                return SerializeNull();
            }
            return SerializeArray(array.get(), depth + 1);
        }
        
        default:
            throw SerializationError("Unknown ValueV2 type");
    }
}

std::string ValueV2Serializer::SerializeSchema(
    const SchemaValueV2* schema, 
    size_t depth)
{
    if (!schema) {
        return SerializeNull();
    }

    std::ostringstream oss;
    oss << "{";

    bool first = true;
    for (const auto& pair : schema->fields) {
        if (!first) oss << ", ";
        
        // 键名: 如果包含特殊字符，加引号
        const std::string& key = pair.first;
        bool needQuotes = false;
        for (char c : key) {
            if (!std::isalnum(c) && c != '_') {
                needQuotes = true;
                break;
            }
        }
        
        if (needQuotes) {
            oss << "\"" << key << "\"";
        } else {
            oss << key;
        }
        oss << "=";

        // 值
        oss << SerializeValue(pair.second, depth);
        
        first = false;
    }

    oss << "}";
    return oss.str();
}

std::string ValueV2Serializer::SerializeArray(
    const ArrayValueV2* array, 
    size_t depth)
{
    if (!array) {
        return SerializeNull();
    }

    std::ostringstream oss;
    oss << "[";

    bool first = true;
    for (const auto& elem : array->elements) {
        if (!first) oss << ", ";
        oss << SerializeValue(elem, depth);
        first = false;
    }

    oss << "]";
    return oss.str();
}

std::string ValueV2Serializer::SerializeNull()
{
    return "null";
}

std::string ValueV2Serializer::SerializeInt(int64_t value)
{
    return std::to_string(value);
}

std::string ValueV2Serializer::SerializeDouble(double value)
{
    // 特殊值处理
    if (std::isnan(value)) {
        return "\"NaN\"";
    }
    if (std::isinf(value)) {
        return value > 0 ? "\"Infinity\"" : "\"-Infinity\"";
    }

    // 普通数值：使用15位有效数字精度
    std::ostringstream oss;
    oss << std::setprecision(FLOAT_PRECISION) << value;
    std::string result = oss.str();

    // 移除末尾的0（如果有小数点）
    if (result.find('.') != std::string::npos) {
        while (result.back() == '0') {
            result.pop_back();
        }
        if (result.back() == '.') {
            result.pop_back();
        }
    }

    return result;
}

std::string ValueV2Serializer::SerializeBool(bool value)
{
    return value ? "true" : "false";
}

std::string ValueV2Serializer::SerializeString(const std::string& value)
{
    std::ostringstream oss;
    oss << "\"";

    for (char c : value) {
        switch (c) {
            case '"':  oss << "\\\""; break;
            case '\\': oss << "\\\\"; break;
            case '\n': oss << "\\n"; break;
            case '\r': oss << "\\r"; break;
            case '\t': oss << "\\t"; break;
            case '\b': oss << "\\b"; break;
            case '\f': oss << "\\f"; break;
            default:
                if (c >= 0 && c < 32) {
                    // 控制字符使用Unicode转义
                    oss << "\\u" << std::setfill('0') << std::setw(4) << std::hex << (int)c;
                } else {
                    oss << c;
                }
        }
    }

    oss << "\"";
    return oss.str();
}

// ====================================================================
// ValueV2Serializer::Parser 实现
// ====================================================================

ValueV2Serializer::Parser::Parser(const std::string& input)
    : input_(input), pos_(0), line_(1), column_(1)
{
}

ValueV2 ValueV2Serializer::Parser::Parse()
{
    SkipWhitespace();
    if (IsAtEnd()) {
        Error("Empty input");
    }
    ValueV2 result = ParseValue();
    SkipWhitespace();
    if (!IsAtEnd()) {
        Error("Extra characters after value");
    }
    return result;
}

void ValueV2Serializer::Parser::SkipWhitespace()
{
    while (!IsAtEnd() && std::isspace(input_[pos_])) {
        if (input_[pos_] == '\n') {
            line_++;
            column_ = 1;
        } else {
            column_++;
        }
        pos_++;
    }
}

char ValueV2Serializer::Parser::Current() const
{
    return IsAtEnd() ? '\0' : input_[pos_];
}

char ValueV2Serializer::Parser::Peek(size_t offset) const
{
    size_t nextPos = pos_ + offset;
    return nextPos >= input_.length() ? '\0' : input_[nextPos];
}

void ValueV2Serializer::Parser::Advance()
{
    if (!IsAtEnd()) {
        if (input_[pos_] == '\n') {
            line_++;
            column_ = 1;
        } else {
            column_++;
        }
        pos_++;
    }
}

void ValueV2Serializer::Parser::ExpectChar(char ch)
{
    if (Current() != ch) {
        std::string msg = std::string("Expected '") + ch + "', got '" + Current() + "'";
        Error(msg);
    }
    Advance();
}

bool ValueV2Serializer::Parser::IsAtEnd() const
{
    return pos_ >= input_.length();
}

void ValueV2Serializer::Parser::Error(const std::string& message)
{
    throw SerializationError(message, line_, column_);
}

ValueV2 ValueV2Serializer::Parser::ParseValue()
{
    SkipWhitespace();

    char ch = Current();

    if (ch == '{') {
        return ParseSchema();
    } else if (ch == '[') {
        return ParseArray();
    } else if (ch == '"') {
        return ParseString();
    } else if (ch == 't' || ch == 'f') {
        return ParseBool();
    } else if (ch == 'n') {
        return ParseNull();
    } else if (ch == '-' || std::isdigit(ch)) {
        return ParseNumber();
    } else {
        Error(std::string("Unexpected character: ") + ch);
    }
}

ValueV2 ValueV2Serializer::Parser::ParseSchema()
{
    ExpectChar('{');
    SkipWhitespace();

    auto schema = std::make_shared<SchemaValueV2>();

    // 空Schema
    if (Current() == '}') {
        Advance();
        return ValueV2(schema);
    }

    while (true) {
        SkipWhitespace();

        // 解析键
        std::string key;
        if (Current() == '"') {
            auto strValue = ParseString();
            key = strValue.AsString();
        } else {
            // 无引号的标识符
            while (!IsAtEnd() && (std::isalnum(Current()) || Current() == '_')) {
                key += Current();
                Advance();
            }
            if (key.empty()) {
                Error("Expected key");
            }
        }

        SkipWhitespace();
        ExpectChar('=');
        SkipWhitespace();

        // 解析值
        ValueV2 value = ParseValue();
        SkipWhitespace();

        // 添加到Schema
        schema->fields[key] = value;

        // 检查是否继续
        if (Current() == ',') {
            Advance();
            SkipWhitespace();
            if (Current() == '}') {
                // 允许末尾逗号
                Advance();
                break;
            }
        } else if (Current() == '}') {
            Advance();
            break;
        } else {
            Error("Expected ',' or '}'");
        }
    }

    return ValueV2(schema);
}

ValueV2 ValueV2Serializer::Parser::ParseArray()
{
    ExpectChar('[');
    SkipWhitespace();

    auto array = std::make_shared<ArrayValueV2>();

    // 空Array
    if (Current() == ']') {
        Advance();
        return ValueV2(array);
    }

    while (true) {
        SkipWhitespace();
        ValueV2 value = ParseValue();
        SkipWhitespace();
        array->elements.push_back(value);

        if (Current() == ',') {
            Advance();
            SkipWhitespace();
            if (Current() == ']') {
                // 允许末尾逗号
                Advance();
                break;
            }
        } else if (Current() == ']') {
            Advance();
            break;
        } else {
            Error("Expected ',' or ']'");
        }
    }

    return ValueV2(array);
}

ValueV2 ValueV2Serializer::Parser::ParseString()
{
    ExpectChar('"');

    std::string result;
    while (Current() != '"' && !IsAtEnd()) {
        if (Current() == '\\') {
            Advance();
            char next = Current();
            switch (next) {
                case '"':  result += '"'; break;
                case '\\': result += '\\'; break;
                case 'n':  result += '\n'; break;
                case 'r':  result += '\r'; break;
                case 't':  result += '\t'; break;
                case 'b':  result += '\b'; break;
                case 'f':  result += '\f'; break;
                case 'u': {
                    // Unicode转义: \uXXXX
                    Advance();
                    std::string hexStr;
                    for (int i = 0; i < 4 && !IsAtEnd(); i++) {
                        if (!std::isxdigit(Current())) {
                            Error("Invalid unicode escape");
                        }
                        hexStr += Current();
                        Advance();
                    }
                    pos_--; // 回退一位，因为下面会Advance
                    int codepoint = std::stoi(hexStr, nullptr, 16);
                    if (codepoint <= 127) {
                        result += static_cast<char>(codepoint);
                    }
                    break;
                }
                default:
                    Error(std::string("Invalid escape sequence: \\") + next);
            }
            Advance();
        } else {
            result += Current();
            Advance();
        }
    }

    ExpectChar('"');
    return ValueV2::CreateString(result);
}

ValueV2 ValueV2Serializer::Parser::ParseNumber()
{
    std::string numStr;

    // 符号
    if (Current() == '-') {
        numStr += Current();
        Advance();
    }

    // 整数部分
    if (Current() == '0') {
        numStr += Current();
        Advance();
    } else if (std::isdigit(Current())) {
        while (std::isdigit(Current())) {
            numStr += Current();
            Advance();
        }
    } else {
        Error("Invalid number");
    }

    // 检查是否为浮点数
    bool isDouble = false;
    if (Current() == '.') {
        isDouble = true;
        numStr += Current();
        Advance();

        if (!std::isdigit(Current())) {
            Error("Invalid decimal number");
        }
        while (std::isdigit(Current())) {
            numStr += Current();
            Advance();
        }
    }

    // 指数部分
    if (Current() == 'e' || Current() == 'E') {
        isDouble = true;
        numStr += Current();
        Advance();

        if (Current() == '+' || Current() == '-') {
            numStr += Current();
            Advance();
        }

        if (!std::isdigit(Current())) {
            Error("Invalid exponent");
        }
        while (std::isdigit(Current())) {
            numStr += Current();
            Advance();
        }
    }

    // 转换为ValueV2
    try {
        if (isDouble) {
            double val = std::stod(numStr);
            return ValueV2::CreateDouble(val);
        } else {
            int64_t val = std::stoll(numStr);
            return ValueV2::CreateInt(val);
        }
    } catch (const std::exception& e) {
        Error(std::string("Number parse error: ") + e.what());
    }
}

ValueV2 ValueV2Serializer::Parser::ParseBool()
{
    if (input_.substr(pos_, 4) == "true") {
        pos_ += 4;
        column_ += 4;
        return ValueV2::CreateBool(true);
    } else if (input_.substr(pos_, 5) == "false") {
        pos_ += 5;
        column_ += 5;
        return ValueV2::CreateBool(false);
    } else {
        Error("Invalid boolean value");
    }
}

ValueV2 ValueV2Serializer::Parser::ParseNull()
{
    if (input_.substr(pos_, 4) == "null") {
        pos_ += 4;
        column_ += 4;
        return ValueV2::CreateNull();
    } else {
        Error("Invalid null value");
    }
}

// ====================================================================
// ValueV2Serializer::Deserialize 实现
// ====================================================================

ValueV2 ValueV2Serializer::Deserialize(const std::string& dslString)
{
    try {
        Parser parser(dslString);
        return parser.Parse();
    } catch (const SerializationError& e) {
        throw;
    } catch (const std::exception& e) {
        throw SerializationError(std::string("Deserialization failed: ") + e.what());
    }
}

// ====================================================================
// 辅助方法实现
// ====================================================================

bool ValueV2Serializer::VerifyRoundTrip(const ValueV2& original)
{
    try {
        std::string serialized = Serialize(original);
        ValueV2 deserialized = Deserialize(serialized);
        
        // 简单比较：序列化后的字符串应该相同
        std::string reserialized = Serialize(deserialized);
        return serialized == reserialized;
    } catch (...) {
        return false;
    }
}

size_t ValueV2Serializer::GetSerializedSize(const ValueV2& value)
{
    try {
        std::string serialized = Serialize(value);
        return serialized.length();
    } catch (...) {
        return 0;
    }
}

size_t ValueV2Serializer::GetMaxDepth(const ValueV2& value)
{
    switch (value.GetType()) {
        case ValueType::Null:
        case ValueType::Int:
        case ValueType::Double:
        case ValueType::Bool:
        case ValueType::String:
            return 0;

        case ValueType::Schema: {
            auto schema = value.GetSchemaPtr();
            if (!schema) return 0;
            
            size_t maxChildDepth = 0;
            for (const auto& pair : schema->fields) {
                size_t childDepth = GetMaxDepth(pair.second);
                maxChildDepth = std::max(maxChildDepth, childDepth);
            }
            return maxChildDepth + 1;
        }

        case ValueType::Array: {
            auto array = value.GetArrayPtr();
            if (!array) return 0;
            
            size_t maxChildDepth = 0;
            for (const auto& elem : array->elements) {
                size_t childDepth = GetMaxDepth(elem);
                maxChildDepth = std::max(maxChildDepth, childDepth);
            }
            return maxChildDepth + 1;
        }

        default:
            return 0;
    }
}

} // namespace ABot
