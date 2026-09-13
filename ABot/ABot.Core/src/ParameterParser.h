/**
 * @file ParameterParser.h
 * @brief 参数单元解析器
 * 
 * 解析格式：[参数名 key1=value1 key2=value2 ...]...[/参数名]（ABOL标签格式）
 */

#ifndef ABOT_PARAMETER_PARSER_H
#define ABOT_PARAMETER_PARSER_H

#include <string>
#include <map>
#include <vector>
#include <memory>

namespace abot {

// Forward declarations
struct Character;  // Forward declare as struct to match Character.h definition

/**
 * @brief 参数节点（代表一个参数单元）
 */
struct ParameterNode {
    std::string name;                      // 参数名称
    std::map<std::string, std::string> attributes;  // 键值对
    std::vector<std::shared_ptr<ParameterNode>> children;  // 子节点
    
    /**
     * @brief 获取属性值
     * @param key 属性键
     * @param default_value 默认值
     * @return 属性值或默认值
     */
    std::string GetAttribute(const std::string& key, 
                            const std::string& default_value = "") const;
    
    /**
     * @brief 获取属性值并转换为整数
     */
    int GetAttributeInt(const std::string& key, int default_value = 0) const;
    
    /**
     * @brief 获取属性值并转换为浮点数
     */
    float GetAttributeFloat(const std::string& key, float default_value = 0.0f) const;
};

/**
 * @brief 参数单元解析器
 */
class ParameterParser {
public:
    /**
     * @brief 解析完整的角色卡 XML
     * @param character_xml 角色卡 XML 文本
     * @param character 输出的角色卡数据
     * @return 解析是否成功
     */
    static bool ParseCharacterCard(const std::string& character_xml, Character& character);
    
    /**
     * @brief 解析单个参数单元文本
     */
    static std::shared_ptr<ParameterNode> Parse(const std::string& xml);
    
    /**
     * @brief 获取最后一个错误信息
     */
    static std::string GetLastError() { return last_error_; }

private:
    static std::string last_error_;
    
    /**
     * @brief 跳过空白字符
     */
    static size_t SkipWhitespace(const std::string& xml, size_t pos);
    
    /**
     * @brief 提取标签名称
     */
    static std::string ExtractTagName(const std::string& xml, size_t& pos);
    
    /**
     * @brief 提取属性键值对
     */
    static std::map<std::string, std::string> ExtractAttributes(
        const std::string& xml, size_t& pos);
    
    /**
     * @brief 提取单个属性键值
     */
    static bool ExtractKeyValue(const std::string& xml, size_t& pos,
                               std::string& key, std::string& value);
    
    /**
     * @brief 提取带引号的字符串值
     */
    static std::string ExtractQuotedString(const std::string& xml, size_t& pos);
    
    /**
     * @brief 提取不带引号的值
     */
    static std::string ExtractUnquotedValue(const std::string& xml, size_t& pos);
};

}  // namespace abot

#endif  // ABOT_PARAMETER_PARSER_H
