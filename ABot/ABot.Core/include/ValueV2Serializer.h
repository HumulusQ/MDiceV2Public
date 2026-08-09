#pragma once

/**
 * @file ValueV2Serializer.h
 * @brief Phase 4: ValueV2序列化系统 - DSL格式序列化/反序列化
 * 
 * 支持的DSL格式:
 * - Int: 123, -45
 * - Double: 3.14, 1.5e-3
 * - Bool: true, false
 * - String: "hello", "line1\nline2"
 * - Null: null
 * - Array: [1, 2, "three", {a=4}]
 * - Schema: {a=1, b="text", c={x=10}}
 * - 支持任意深度的嵌套
 * 
 * 示例:
 *   ValueV2 v = CreateSchema({
 *     {"name", "Hero"},
 *     {"hp", 100},
 *     {"stats", CreateSchema({
 *       {"atk", 15},
 *       {"def", 10}
 *     })},
 *     {"items", CreateArray({"sword", "shield"})}
 *   });
 *   
 *   string dsl = Serializer::Serialize(v);
 *   // 输出: {name="Hero", hp=100, stats={atk=15, def=10}, items=["sword", "shield"]}
 *   
 *   ValueV2 v2 = Serializer::Deserialize(dsl);
 *   // v2等于v (往返一致性)
 */

#include <string>
#include <memory>
#include <map>
#include <vector>
#include <sstream>
#include "ValueV2.h"

namespace ABot {

/**
 * @class SerializationError
 * @brief 序列化错误异常类
 * 
 * 包含错误位置（行号、列号）和详细错误信息
 */
class SerializationError : public std::exception
{
public:
    explicit SerializationError(
        const std::string& message, 
        size_t line = 0, 
        size_t column = 0)
        : message_(message), line_(line), column_(column) {}
    
    const char* what() const noexcept override { return message_.c_str(); }
    
    size_t GetLine() const { return line_; }
    size_t GetColumn() const { return column_; }
    std::string GetFullMessage() const;

private:
    std::string message_;
    size_t line_;
    size_t column_;
};

/**
 * @class ValueV2Serializer
 * @brief ValueV2对象的序列化/反序列化
 * 
 * 功能:
 * - Serialize(ValueV2) → DSL字符串
 * - Deserialize(DSL字符串) → ValueV2
 * - 往返一致性验证
 * 
 * 特性:
 * - 支持所有ValueV2类型
 * - 递归处理嵌套结构
 * - 完整的错误报告
 * - 浮点数精度: 15位有效数字
 * - 递归深度限制: 100层
 */
class ValueV2Serializer
{
public:
    /**
     * @brief 序列化ValueV2为DSL字符串
     * 
     * @param value 待序列化的ValueV2对象
     * @param prettyPrint 是否格式化输出（默认false，紧凑格式）
     * @return DSL格式字符串
     * 
     * @throw SerializationError 如果遇到不可序列化的类型
     * 
     * 示例:
     *   ValueV2 v = CreateSchema({{"a", 1}, {"b", "text"}});
     *   string dsl = ValueV2Serializer::Serialize(v);
     *   // 输出: {a=1, b="text"}
     */
    static std::string Serialize(
        const ValueV2& value, 
        bool prettyPrint = false);

    /**
     * @brief 反序列化DSL字符串为ValueV2
     * 
     * @param dslString DSL格式字符串
     * @return 反序列化的ValueV2对象
     * 
     * @throw SerializationError 如果格式非法
     *   - 包含行号、列号、错误描述
     * 
     * 示例:
     *   string dsl = "{a=1, b=\"text\"}";
     *   ValueV2 v = ValueV2Serializer::Deserialize(dsl);
     *   // v是一个Schema，包含字段a和b
     */
    static ValueV2 Deserialize(const std::string& dslString);

    /**
     * @brief 验证往返一致性
     * 
     * @param original 原始ValueV2对象
     * @return true 如果 Deserialize(Serialize(original)) == original
     * 
     * 用于测试和验证
     */
    static bool VerifyRoundTrip(const ValueV2& original);

    /**
     * @brief 获取序列化后的大小估计
     * 
     * @param value 待深析的ValueV2对象
     * @return 估计的字符串字节数
     */
    static size_t GetSerializedSize(const ValueV2& value);

    /**
     * @brief 获取Value的最大递归深度
     * 
     * @param value 待分析的ValueV2对象
     * @return 最大递归深度（Null为0）
     */
    static size_t GetMaxDepth(const ValueV2& value);

private:
    // 序列化辅助方法
    static std::string SerializeValue(
        const ValueV2& value, 
        size_t depth);

    static std::string SerializeSchema(
        const SchemaValueV2* schema, 
        size_t depth);

    static std::string SerializeArray(
        const ArrayValueV2* array, 
        size_t depth);

    static std::string SerializeNull();
    static std::string SerializeInt(int64_t value);
    static std::string SerializeDouble(double value);
    static std::string SerializeBool(bool value);
    static std::string SerializeString(const std::string& value);

    // 反序列化辅助方法
    class Parser
    {
    public:
        explicit Parser(const std::string& input);

        ValueV2 Parse();

    private:
        const std::string& input_;
        size_t pos_;
        size_t line_;
        size_t column_;

        // 基础操作
        void SkipWhitespace();
        char Current() const;
        char Peek(size_t offset = 1) const;
        void Advance();
        void ExpectChar(char ch);

        // 类型解析
        ValueV2 ParseValue();
        ValueV2 ParseSchema();
        ValueV2 ParseArray();
        ValueV2 ParseString();
        ValueV2 ParseNumber();
        ValueV2 ParseBool();
        ValueV2 ParseNull();

        // 错误处理
        void Error(const std::string& message);
        bool IsAtEnd() const;
    };

    // 约束
    static constexpr size_t MAX_RECURSION_DEPTH = 100;
    static constexpr size_t FLOAT_PRECISION = 15;
};

} // namespace ABot
