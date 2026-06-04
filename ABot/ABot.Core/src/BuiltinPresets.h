/**
 * @file BuiltinPresets.h
 * @brief 系统内置预设声明
 */

#pragma once

#include <string>

namespace abot {

/**
 * @brief 初始化所有系统内置预设
 * 
 * 应在应用程序启动时调用一次，注册：
 * - 8 个内置函数 (dodamage, akr, random, etc.)
 * - NATK ANKE 预设
 * - 系统技能预设
 * - 系统状态预设
 * 
 * 示例：
 * ```cpp
 * int main() {
 *     abot::InitializeBuiltinPresets();
 *     // 应用程序运行...
 * }
 * ```
 */
void InitializeBuiltinPresets();

/**
 * @brief Base64 解码函数 - 通用编解码工具
 * 
 * 用于解码脚本、技能定义、预设等中的BASE64编码内容
 * 这是系统范围内统一使用的解码函数
 * 
 * @param encoded BASE64编码的字符串
 * @return 解码后的源代码/脚本文本，若失败则返回空字符串
 */
std::string DecodeBase64(const std::string& encoded);

/**
 * @brief Base64 编码函数 - 通用编解码工具
 * 
 * 用于编码脚本、技能定义、预设等内容为BASE64
 * 这是系统范围内统一使用的编码函数
 * 
 * @param input 待编码的源代码/脚本文本
 * @return BASE64编码后的字符串
 */
std::string EncodeBase64(const std::string& input);

/**
 * @brief 准备脚本编译 - 通用脚本准备函数
 * 
 * 这是系统范围内统一的脚本准备逻辑，用于：
 * 1. ANKE预设的脚本编译
 * 2. SkillDef的脚本编译
 * 3. 任何其他需要编译脚本的组件
 * 
 * 处理流程：
 * 1. 清理末尾的null和空白字符
 * 2. 检测是否为BASE64编码
 * 3. 如果是BASE64，自动解码
 * 4. 最终清理，返回准备好编译的源代码
 * 
 * @param raw_script 从XML或其他格式中提取的原始脚本（可能BASE64或纯文本）
 * @param option_name 脚本对应的选项名称（用于日志和诊断）
 * @return 准备好编译的源代码，若失败则返回空字符串
 */
std::string PrepareScriptForCompilation(const std::string& raw_script, const std::string& option_name);

}  // namespace abot
