/**
 * @file BuiltinPresets.cpp
 * @brief 系统内置预设初始化
 * 
 * 注册所有系统提供的函数预设、ANKE预设、技能定义和状态定义
 */

#include "PresetSystem.h"
#include "ExecutionEnvironment.h"
#include "Character.h"
#include "Lexer.h"
#include "Parser.h"
#include "Bytecode.h"
#include "C_API.h"
#include "RoundManager.h"
#include "TypeSystem.h"
#include <cmath>
#include <cstdlib>
#include <ctime>
#include <fstream>
#include <iostream>

// 前置声明：全局RoundManager指针（定义在命名空间外部）
extern abot::RoundManager* g_current_round_manager;

// 全局日志文件
static std::ofstream g_anke_debug_log;

// 初始化函数
static void InitAnkeDebugLog() {
    if (!g_anke_debug_log.is_open()) {
        g_anke_debug_log.open("C:\\ANKE_DEBUG.log", std::ios::app);
    }
}

namespace abot {

// 日志辅助函数 - 输出到RoundManager的日志系统
static void LogDodamage(const std::string& message) {
    if (g_current_round_manager) {
        //g_current_round_manager->AppendSkillTriggerLog("[dodamage] " + message + "\n");
    } else {
       //fprintf(stderr, "[dodamage] %s\n", message.c_str());
    }
}

// ANKE诊断日志辅助函数 - 输出到文件
static void LogAnkeDebug(const std::string& message) {
    InitAnkeDebugLog();
    if (g_anke_debug_log.is_open()) {
        g_anke_debug_log << "[ANKE-DEBUG] " << message << "\n";
        g_anke_debug_log.flush();
    }
    
    // 也尝试输出到RoundManager (如果可用)
    /*if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog("[ANKE-DEBUG] " + message + "\n");
    }*/
}

// ANKE错误日志辅助函数 - 输出到文件
static void LogAnkeError(const std::string& message) {
    InitAnkeDebugLog();
    if (g_anke_debug_log.is_open()) {
        g_anke_debug_log << "[ANKE-ERROR] " << message << "\n";
        g_anke_debug_log.flush();
    }
    
    // 也尝试输出到RoundManager (如果可用)
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog("[ANKE-ERROR] " + message + "\n");
    }
}

// Base64 解码函数 - 从 C_API.cpp 引用相同规范
static std::string DecodeBase64(const std::string& encoded) {
    const std::string base64_chars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    
    std::string decoded;
    decoded.reserve(encoded.size() / 4 * 3);
    
    int in_len = encoded.size();
    int i = 0, j = 0, in_ = 0;
    unsigned char char_array_4[4], char_array_3[3];
    
    while (in_len-- && (encoded[in_] != '=') && isalnum(encoded[in_]) || encoded[in_] == '+' || encoded[in_] == '/') {
        char_array_4[i++] = encoded[in_]; in_++;
        if (i == 4) {
            for (i = 0; i <4; i++)
                char_array_4[i] = base64_chars.find(char_array_4[i]);

            char_array_3[0] = (char_array_4[0] << 2) + ((char_array_4[1] & 0x30) >> 4);
            char_array_3[1] = (((char_array_4[1] & 0xf) << 4) + ((char_array_4[2] & 0x3c) >> 2));
            char_array_3[2] = (((char_array_4[2] & 0x3) << 6) + char_array_4[3]);

            for(i = 0; i < 3; i++)
                decoded.push_back(char_array_3[i]);
            i = 0;
        }
    }

    if (i) {
        for(j = i; j <4; j++)
            char_array_4[j] = 0;

        for (j = 0; j <4; j++)
            char_array_4[j] = base64_chars.find(char_array_4[j]);

        char_array_3[0] = (char_array_4[0] << 2) + ((char_array_4[1] & 0x30) >> 4);
        char_array_3[1] = (((char_array_4[1] & 0xf) << 4) + ((char_array_4[2] & 0x3c) >> 2));
        char_array_3[2] = (((char_array_4[2] & 0x3) << 6) + char_array_4[3]);

        for (j = 0; (j < i - 1); j++) {
            decoded.push_back(char_array_3[j]);
        }
    }
    
    return decoded;
}

// ============ Base64 编码/解码辅助函数 ============

/**
 * @brief Base64 编码函数
 */
static std::string EncodeBase64(const std::string& input) {
    const char base64_table[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string result;
    result.reserve((input.size() + 2) / 3 * 4);
    
    int i = 0;
    unsigned char char_array_3[3];
    unsigned char char_array_4[4];

    while (i < (int)input.size()) {
        char_array_3[0] = input[i++];
        char_array_3[1] = (i < (int)input.size()) ? input[i++] : 0;
        char_array_3[2] = (i < (int)input.size()) ? input[i++] : 0;

        char_array_4[0] = (char_array_3[0] & 0xfc) >> 2;
        char_array_4[1] = ((char_array_3[0] & 0x03) << 4) | ((char_array_3[1] & 0xf0) >> 4);
        char_array_4[2] = ((char_array_3[1] & 0x0f) << 2) | ((char_array_3[2] & 0xc0) >> 6);
        char_array_4[3] = char_array_3[2] & 0x3f;

        result += base64_table[char_array_4[0]];
        result += base64_table[char_array_4[1]];
        result += (i - 2 < (int)input.size()) ? base64_table[char_array_4[2]] : '=';
        result += (i - 1 < (int)input.size()) ? base64_table[char_array_4[3]] : '=';
    }
    
    return result;
}

/**
 * @brief 准备脚本编译：检测编码、解码、清理垃圾字符
 * 
 * 这是通用的脚本准备逻辑，用于：
 * 1. 检测脚本是否为BASE64编码
 * 2. 如果是BASE64，自动解码
 * 3. 清理末尾垃圾字符（null、空格等）
 * 4. 返回准备好编译的源代码
 * 
 * 这套逻辑与C_API.cpp中的skilldef解析一致，确保系统范围内的统一性
 * 
 * 【重要】此函数被 C_API.cpp 和 BuiltinPresets.cpp 共同使用，因此不应标记为 static
 * 
 * @param raw_script 从XML选项中提取的原始脚本（可能BASE64或原始文本）
 * @param option_name 选项名称（用于日志）
 * @return 准备好编译的源代码，若失败则返回空字符串
 */
std::string PrepareScriptForCompilation(const std::string& raw_script, const std::string& option_name)
{
    if (raw_script.empty()) {
        LogAnkeError("PrepareScript: raw_script is empty for option '" + option_name + "'");
        return "";
    }
    
    // 【第1步】首先清理末尾垃圾字符（null、空格、换行等）
    // 这样可以避免被这些字符影响BASE64检测
    std::string cleaned = raw_script;
    while (!cleaned.empty() && (cleaned.back() == '\0' || cleaned.back() == ' ' || cleaned.back() == '\n' || cleaned.back() == '\r')) {
        cleaned.pop_back();
    }
    
    if (cleaned.empty()) {
        LogAnkeError("PrepareScript: script is empty after cleanup for option '" + option_name + "'");
        return "";
    }
    
    // 【第2步】检测是否为BASE64编码 - 使用与C_API.cpp相同的逻辑
    // 【改进】首先清理末尾，再检测，避免NULL字符的影响
    bool is_base64 = true;
    int valid_base64_count = 0;
    for (unsigned char c : cleaned) {
        bool is_base64_char = isalnum(c) || c == '+' || c == '/' || c == '=';
        if (is_base64_char) {
            valid_base64_count++;
        } else {
            is_base64 = false;
            break;
        }
    }
    
    LogAnkeDebug("Option[" + option_name + "] is_base64=" + (is_base64 ? std::string("true") : std::string("false")) + 
                 ", length=" + std::to_string(cleaned.length()) + ", valid_base64_chars=" + std::to_string(valid_base64_count));
    
    std::string source_code;
    
    if (is_base64 && cleaned.length() > 10) {
        // 【第3步】解码BASE64 - 这个脚本是编码过的
        LogAnkeDebug("Option[" + option_name + "] Decoding BASE64 (length=" + std::to_string(cleaned.length()) + ")");
        source_code = DecodeBase64(cleaned);
        if (source_code.empty()) {
            LogAnkeError("Option[" + option_name + "] BASE64 decode returned empty!");
            return "";
        }
        LogAnkeDebug("Option[" + option_name + "] Decoded length=" + std::to_string(source_code.length()) + 
                     ", content: " + source_code);
    } else {
        // 【第4步】不是BASE64，直接使用清理后的内容（或长度太短，不值得解码）
        LogAnkeDebug("Option[" + option_name + "] Using cleaned script (not BASE64 or too short)");
        source_code = cleaned;
    }
    
    // 【第5步】最终清理末尾垃圾字符 - 移除null字符、空格等
    // 这与C_API.cpp中的清理逻辑一致
    while (!source_code.empty() && (source_code.back() == '\0' || source_code.back() == ' ' || source_code.back() == '\n' || source_code.back() == '\r')) {
        source_code.pop_back();
    }
    
    LogAnkeDebug("Option[" + option_name + "] After final cleanup, length=" + std::to_string(source_code.length()) + 
                 ", ready for compilation");
    
    return source_code;
}

// ============ 用户定义的 ANKE 脚本 ============
/**
 * @brief NATK (Normal Attack) ANKE 预设脚本
 * 
 * CoC7 critical 判定的两步流程：
 * 
 * 【第一步】投掷 D10 选择具体选项：
 *   - 1-9：普通选项（回避、小伤害×2、大伤害、极大伤害等）
 *   - 10：选中 critical（触发大成功/大失败分支）
 * 
 * 【第二步】如果第一步选中 critical（D10=10），则进行大成功/大失败判定：
 *   - 投掷第二个 D10（称为"D2判定"或"重大判定"）
 *   - 1-5：大成功 (es)，触发 self.turn.multiplier *= 2，然后重新发起 akr("natk")
 *   - 6-10：大失败 (ef)，如果 multiplier=1 则执行 shiftattacker()，否则 multiplier /= 2
 * 
 * 脚本中集成的倍增系统：
 * - es (大成功): 倍增系数 *= 2，然后重新投掷攻击
 * - ef (大失败): 倍增系数 /= 2 或切换攻击手（如果倍增系数为1）
 * 
 * 注意事项：
 * 1. critical 选项的权重为 1（代表整个 D10 中的一个刻度）
 * 2. 第二步的判定也是投掷 D10，只是在 es/ef 分支中进行
 * 3. 避免在脚本中使用不匹配的括号，可能导致解析错误
 * 4. 所有脚本通过 expr(content) 格式载入
 */

// 定义完整的 ANKE NATK 脚本（原始 ABOL 格式，不需预先编码）
// RegisterAnkeFromString() 会自动检测、编码和处理脚本内容
// expr() 支持赋值语句（已验证：WillbeUsefulNextTime 成功运行多个 set 语句）
// 大成功设计：倍增系数 *= 2，然后重新投掷攻击
// 大失败设计：如果倍增系数为1，尝试切换攻击手；否则系数 /= 2 并重新投掷
static const char* NATK_ANKE_SCRIPT = R"SCRIPT(
[
<type value=ankeset>
<anke name=natk, unit=[
  {e=回避, w=1, p=expr(if(self.turn.multiplier == 1){dodamage(self.dmg.d1);})},
  {e=小伤害, w=2, p=expr(dodamage(self.dmg.d1);)},
  {e=中伤害, w=2, p=expr(dodamage(self.dmg.d2);)},
  {e=大伤害, w=2, p=expr(dodamage(self.dmg.d3);)},
  {e=极大伤害, w=2, p=expr(dodamage(self.dmg.d4);)},
  {es=大成功, p=expr(set self.turn.multiplier = self.turn.multiplier * 2; log("  [大成功-系数翻倍]"); akr("natk");)},
  {ef=大失败, p=expr(if(self.turn.multiplier == 1){log("  [大失败-切换到对方反击]"); shiftattacker();} else {log("  [大失败-系数递减]"); set self.turn.multiplier = self.turn.multiplier / 2;})}
]>
]
)SCRIPT";

// ============ 脚本编译辅助函数 ============

/**
 * @brief 从字符串编译脚本为字节码
 * @param script_source 脚本源代码
 * @return 编译后的字节码，或nullptr如果编译失败
 * 
 * 注意：expr() 支持完整的赋值语句和表达式
 * 例如：expr(set self.turn.multiplier = self.turn.multiplier * 2; akr("natk");)
 * 这种支持已在 WillbeUsefulNextTime 技能定义中验证成功
 */
static std::unique_ptr<BytecodeProgram> CompileScript(const std::string& script_source)
{
    // 第1步：词法分析
    Lexer lexer(script_source);
    auto tokens = lexer.ScanTokens();
    
    if (lexer.HasError()) {
        // 【关键】捕获词法分析器错误消息并设置到 RoundManager
        std::string error_msg = lexer.GetErrorMessage();
        LogAnkeError("Lexer error: " + error_msg);
        
        if (g_current_round_manager) {
            g_current_round_manager->SetLastError("[SCRIPT COMPILER ERROR - Lexer] " + error_msg);
        }
        return nullptr;
    }
    
    // 第2步：语法分析
    Parser parser(tokens);
    auto statements = parser.ParseProgram();
    
    if (parser.HasError()) {
        // 【关键】捕获语法分析器错误消息并设置到 RoundManager
        std::string error_msg = parser.GetErrorMessage();
        LogAnkeError("Parser error: " + error_msg);
        
        if (g_current_round_manager) {
            g_current_round_manager->SetLastError("[SCRIPT COMPILER ERROR - Parser] " + error_msg);
        }
        return nullptr;
    }
    
    // 第3步：字节码编译
    BytecodeCompiler compiler;
    auto bytecode = compiler.Compile(statements);
    
    return bytecode;
}

/**
 * @brief 从 ANKE 脚本字符串创建并注册预设
 * 
 * 支持两种格式：
 * 
 * 1. 单个ANKE格式：
 *    <anke name=预设名称, unit=[{选项1}, {选项2}, ...]>
 * 
 * 2. ANKESET格式（包含多个ANKE）：
 *    [<type value=ankeset>
 *     <anke name=预设名1, unit=[...]>
 *     <anke name=预设名2, unit=[...]>
 *     ...
 *    ]
 * 
 * 解析流程：
 * - 如果输入包含 <type value=ankeset>，则识别为ankeset格式
 * - 提取所有 <anke ...> 块，逐个单独解析和注册
 * - 如果输入不包含ankeset标记，按单个anke处理
 */
static bool RegisterAnkeFromString(const std::string& ankeset_xml)
{
    LogAnkeDebug("RegisterAnkeFromString called");
    LogAnkeDebug("Input script length: " + std::to_string(ankeset_xml.length()));
    LogAnkeDebug("Input script (first 200 chars):");
    LogAnkeDebug(ankeset_xml.substr(0, std::min(size_t(200), ankeset_xml.length())));
    LogAnkeDebug("===========================");
    
    // ========== 第1步：检测是否为ANKESET格式 ==========
    bool is_ankeset = (ankeset_xml.find("<type value=ankeset>") != std::string::npos);
    
    if (is_ankeset) {
        LogAnkeDebug("ANKESET format detected - extracting multiple <anke> definitions");
        
        // ========== 第2步：从ANKESET中提取所有<anke>块 ==========
        std::vector<std::string> anke_blocks;
        
        size_t pos = 0;
        while (pos < ankeset_xml.length()) {
            size_t anke_start = ankeset_xml.find("<anke ", pos);
            if (anke_start == std::string::npos) {
                break;  // 没有更多的<anke>块了
            }
            
            // 找到这个<anke>块的结束 - 使用括号匹配
            // <anke ...> 以 > 结束
            size_t anke_end = ankeset_xml.find('>', anke_start);
            if (anke_end == std::string::npos) {
                LogAnkeError("Malformed <anke> block - missing closing >");
                return false;
            }
            
            // 提取从<anke开始到>结束的整个块
            std::string anke_block = ankeset_xml.substr(anke_start, anke_end - anke_start + 1);
            anke_blocks.push_back(anke_block);
            
            LogAnkeDebug("Extracted <anke> block #" + std::to_string(anke_blocks.size()) + 
                        " (length=" + std::to_string(anke_block.length()) + ")");
            
            pos = anke_end + 1;
        }
        
        LogAnkeDebug("Total <anke> blocks found: " + std::to_string(anke_blocks.size()));
        
        // ========== 第3步：逐个注册每个ANKE ==========
        if (anke_blocks.empty()) {
            LogAnkeError("ANKESET format detected but no <anke> blocks found");
            return false;
        }
        
        bool all_succeeded = true;
        for (size_t i = 0; i < anke_blocks.size(); i++) {
            LogAnkeDebug("Processing ANKE #" + std::to_string(i + 1) + " of " + std::to_string(anke_blocks.size()));
            
            if (!RegisterAnkeFromString(anke_blocks[i])) {
                LogAnkeError("Failed to register ANKE block #" + std::to_string(i + 1));
                all_succeeded = false;
                // 继续处理其他的anke，不立即返回
            }
        }
        
        return all_succeeded;
    }
    
    // ========== 单个ANKE的解析逻辑 ==========
    // 从这里开始处理单个 <anke name=..., unit=[...]> 块
    
    // 第1步：提取预设名称
    size_t name_start = ankeset_xml.find("name=");
    if (name_start == std::string::npos) {
        LogAnkeError("missing 'name=' attribute");
        return false;
    }
    name_start += 5; // 跳过 "name="
    
    // 查找名称的结束位置
    size_t name_end = ankeset_xml.find_first_of(", ", name_start);
    if (name_end == std::string::npos) {
        LogAnkeError("invalid name format");
        return false;
    }
    
    std::string preset_name = ankeset_xml.substr(name_start, name_end - name_start);
    LogAnkeDebug("Preset name: '" + preset_name + "'");
    
    // 第2步：提取unit数组
    size_t unit_start = ankeset_xml.find("unit=");
    if (unit_start == std::string::npos) {
        LogAnkeError("missing 'unit=' attribute");
        return false;
    }
    unit_start = ankeset_xml.find('[', unit_start);
    size_t unit_end = ankeset_xml.rfind(']');
    if (unit_start == std::string::npos || unit_end == std::string::npos) {
        LogAnkeError("invalid unit array format");
        return false;
    }
    
    std::string unit_str = ankeset_xml.substr(unit_start + 1, unit_end - unit_start - 1);
    LogAnkeDebug("Unit string length: " + std::to_string(unit_str.length()));
    LogAnkeDebug("Unit string (first 300 chars):");
    LogAnkeDebug(unit_str.substr(0, std::min(size_t(300), unit_str.length())));
    
    // 第3步：创建ANKE预设对象
    auto anke_preset = std::make_unique<AnkePreset>(preset_name);
    
    // 第4步：解析所有选项
    struct TempOption {
        std::string type;        // "e", "es", "ef"
        std::string name;
        int weight;
        std::string script;
    };
    std::vector<TempOption> temp_options;
    
    size_t pos = 0;
    int option_index = 0;
    while (pos < unit_str.length()) {
        size_t opt_start = unit_str.find('{', pos);
        if (opt_start == std::string::npos) break;
        
        // 【修復】使用括號和大括號匹配計數而非簡單查找
        // 這樣可以正確處理腳本內有大括號的情況，例如：
        // {ef=..., p=expr(if (...) { ... } akr(...);)}
        int brace_depth = 1;  // 已經在開括號內
        size_t opt_end = opt_start + 1;
        while (opt_end < unit_str.length()) {
            char c = unit_str[opt_end];
            if (c == '{') {
                brace_depth++;
            } else if (c == '}') {
                brace_depth--;
                if (brace_depth == 0) {
                    // 找到匹配的右括號
                    break;
                }
            }
            opt_end++;
        }
        
        if (brace_depth != 0) {
            LogAnkeError("unclosed { in option");
            return false;
        }
        
        std::string option_str = unit_str.substr(opt_start + 1, opt_end - opt_start - 1);
        pos = opt_end + 1;
        
        LogAnkeDebug("Option[" + std::to_string(option_index) + "] string (first 150 chars):");
        LogAnkeDebug(option_str.substr(0, std::min(size_t(150), option_str.length())));
        
        // 判断选项类型
        std::string opt_type;
        std::string option_name;
        int option_weight = 0;
        std::string script_str;
        
        size_t e_pos = option_str.find("e=");
        size_t es_pos = option_str.find("es=");
        size_t ef_pos = option_str.find("ef=");
        
        LogAnkeDebug("Option[" + std::to_string(option_index) + "] positions: e_pos=" + 
            std::to_string(e_pos == std::string::npos ? 9999 : e_pos) + ", es_pos=" + 
            std::to_string(es_pos == std::string::npos ? 9999 : es_pos) + ", ef_pos=" + 
            std::to_string(ef_pos == std::string::npos ? 9999 : ef_pos));
        
        // 优先级：es/ef > e
        // 【关键修复】对于 es/ef 选项，使用固定名称而不是从字符串解析，避免 UTF-8 边界问题
        if (es_pos != std::string::npos && (e_pos == std::string::npos || es_pos < e_pos)) {
            opt_type = "es";
            option_name = "critical_success";  // 固定名称，避免 UTF-8 问题
            option_weight = 1;
        } else if (ef_pos != std::string::npos && (e_pos == std::string::npos || ef_pos < e_pos)) {
            opt_type = "ef";
            option_name = "critical_fail";  // 固定名称，避免 UTF-8 问题
            option_weight = 1;
        } else if (e_pos != std::string::npos) {
            opt_type = "e";
            e_pos += 2;
            size_t name_comma = option_str.find(',', e_pos);
            option_name = option_str.substr(e_pos, (name_comma != std::string::npos ? name_comma - e_pos : 0));
            
            // 提取权重
            size_t w_pos = option_str.find("w=", (name_comma != std::string::npos ? name_comma : e_pos));
            if (w_pos != std::string::npos) {
                w_pos += 2;
                size_t w_comma = option_str.find(',', w_pos);
                std::string weight_str = option_str.substr(w_pos,
                    (w_comma != std::string::npos ? w_comma - w_pos : option_str.find('}', w_pos) - w_pos));
                option_weight = std::atoi(weight_str.c_str());
            }
            
            LogAnkeDebug("Option[" + std::to_string(option_index) + "] type='e', name='" + option_name + "', weight=" + std::to_string(option_weight));
        } else {
            LogAnkeError("RegisterAnkeFromString: invalid option format (option_index=" + std::to_string(option_index) + ")");
            LogAnkeError("Expected 'e=', 'es=', or 'ef=' not found");
            LogAnkeError("Option string: " + option_str);
            option_index++;
            continue;
        }
        
        // 清除名称两端空格
        while (!option_name.empty() && (option_name.front() == ' ' || option_name.front() == '"')) {
            option_name = option_name.substr(1);
        }
        while (!option_name.empty() && (option_name.back() == ' ' || option_name.back() == '"')) {
            option_name.pop_back();
        }
        
        // 提取脚本 - 使用括号匹配而非简单查找
        size_t p_pos = option_str.find("p=expr(&");
        if (p_pos == std::string::npos) {
            p_pos = option_str.find("p=expr(");
            if (p_pos == std::string::npos) {
                LogAnkeError("missing p=expr in option");
                option_index++;
                continue;
            }
            p_pos += 7;  // 跳过 "p=expr("
        } else {
            p_pos += 8;  // 跳过 "p=expr(&"
        }
        
        // 【关键修复】括号匹配计数，而非简单查找
        // 这样可以正确处理嵌套的括号，例如：
        // expr(if (condition) { ... } akr("natk");)
        int paren_depth = 1;  // 已经在第一个 ( 内
        size_t script_end = p_pos;
        
        while (script_end < option_str.length()) {
            char c = option_str[script_end];
            if (c == '(') {
                paren_depth++;
            } else if (c == ')') {
                paren_depth--;
                if (paren_depth == 0) {
                    // 找到匹配的右括号
                    break;
                }
            }
            script_end++;
        }
        
        if (paren_depth != 0) {
            LogAnkeError("unmatched parentheses in expr()");
            option_index++;
            continue;
        }
        
        script_str = option_str.substr(p_pos, script_end - p_pos);
        
        // 【诊断】脚本提取后的末尾检查
        LogAnkeDebug("Option[" + std::to_string(option_index) + "] RAW EXTRACTION (length=" + std::to_string(script_str.length()) + ")");
        LogAnkeDebug("  First 10 chars: '" + (script_str.length() >= 10 ? script_str.substr(0, 10) : script_str) + "'");
        LogAnkeDebug("  Last 10 chars: '" + (script_str.length() >= 10 ? script_str.substr(script_str.length() - 10) : script_str) + "'");
        
        // 【诊断】末尾字符的八进制值
        if (!script_str.empty()) {
            std::string end_info = "  Last 5 bytes (hex): ";
            size_t start = script_str.length() > 5 ? script_str.length() - 5 : 0;
            for (size_t i = start; i < script_str.length(); i++) {
                char buf[8];
                sprintf(buf, "[%02X] ", (unsigned char)script_str[i]);
                end_info += buf;
            }
            LogAnkeDebug(end_info);
        }
        
        // 清除脚本前导空格
        while (!script_str.empty() && script_str.front() == ' ') {
            script_str = script_str.substr(1);
        }
        while (!script_str.empty() && script_str.back() == ' ') {
            script_str.pop_back();
        }
        
        // 【统一脚本处理】
        // 脚本可能是以下之一：
        // 1. 原始源代码 (当使用 expr(dodamage(0);) 格式时)
        // 2. BASE64编码的脚本 (当使用 expr(&BASE64_CONTENT) 格式时)
        // 3. BASE64编码的脚本，但从NATK_ANKE_SCRIPT中自动编码
        //
        // PrepareScriptForCompilation 会在编译时统一处理这些情况
        // 此处我们只需直接存储提取的内容即可
        LogAnkeDebug("Option[" + std::to_string(option_index) + "] Extracted script (length=" + std::to_string(script_str.length()) + ")");
        
        LogAnkeDebug("Option[" + std::to_string(option_index) + "] parsed: type='" + opt_type + "', name='" + option_name + "', weight=" + std::to_string(option_weight) + ", script_len=" + std::to_string(script_str.length()));
        
        temp_options.push_back({opt_type, option_name, option_weight, script_str});
        option_index++;
    }
    
    LogAnkeDebug("Parsed " + std::to_string(temp_options.size()) + " options from script");
    
    // 第5步：处理大成功/大失败配对
    std::vector<bool> processed(temp_options.size(), false);
    
    for (size_t i = 0; i < temp_options.size(); i++) {
        if (processed[i]) continue;
        
        auto& opt = temp_options[i];
        
        if (opt.type == "es") {
            // 查找对应的ef选项
            bool found_ef = false;
            for (size_t j = i + 1; j < temp_options.size(); j++) {
                if (temp_options[j].type == "ef") {
                    // 【通用脚本处理】使用统一的PrepareScriptForCompilation函数
                    std::string es_source = PrepareScriptForCompilation(opt.script, "es");
                    if (es_source.empty()) {
                        LogAnkeError("Failed to prepare es script for compilation");
                        return false;
                    }
                    
                    auto bytecode_es = CompileScript(es_source);
                    if (!bytecode_es) {
                        LogAnkeError("Failed to compile es script");
                        return false;
                    }
                    
                    // 【通用脚本处理】使用统一的PrepareScriptForCompilation函数
                    std::string ef_source = PrepareScriptForCompilation(temp_options[j].script, "ef");
                    if (ef_source.empty()) {
                        LogAnkeError("Failed to prepare ef script for compilation");
                        return false;
                    }
                    
                    auto bytecode_ef = CompileScript(ef_source);
                    if (!bytecode_ef) {
                        LogAnkeError("Failed to compile ef script");
                        return false;
                    }
                    
                    // 创建配对选项
                    // 【重要】权重为 1：代表在第一次 D10 投掷中选中 critical（即 D10=10）的情况
                    // 之后在 AnkePreset::Execute() 中会进行第二步 D10 投掷（称为"D2判定"）来判定大成功/大失败
                    // 完整流程：
                    //   第一步：投掷 D10 选择选项 (1:回避, 2-3:小伤d1, 4-5:小伤d2, 6-7:大伤, 8-9:极大, 10:critical)
                    //   如果 D10=10（选中critical）
                    //   第二步：投掷 D10 进行大成功/大失败判定（1-5:大成功, 6-10:大失败）
                    AnkeOption critical_option("critical", 1, std::move(bytecode_es), std::move(bytecode_ef));
                    anke_preset->AddOption(std::move(critical_option));
                    
                    LogAnkeDebug("Added critical (es/ef) option");
                    
                    processed[i] = true;
                    processed[j] = true;
                    found_ef = true;
                    break;
                }
            }
            
            if (!found_ef) {
                LogAnkeError("es option without matching ef");
                return false;
            }
        } else if (opt.type == "e") {
            // 普通选项 - 【通用脚本处理】使用统一的PrepareScriptForCompilation函数
            std::string option_source = PrepareScriptForCompilation(opt.script, opt.name);
            if (option_source.empty()) {
                LogAnkeError("Failed to prepare script for compilation (option='" + opt.name + "')");
                continue;
            }
            
            auto bytecode = CompileScript(option_source);
            if (!bytecode) {
                LogAnkeError("Failed to compile option script (option='" + opt.name + "')");
                continue;
            }
            
            AnkeOption option(opt.name, opt.weight, std::move(bytecode));
            anke_preset->AddOption(std::move(option));
            
            LogAnkeDebug("Added option '" + opt.name + "' with weight " + std::to_string(opt.weight));
            
            processed[i] = true;
        }
    }
    
    // 第6步：注册预设
    anke_preset->SetBuiltin(true);  // 标记为系统内置预设
    PresetRegistry* registry = PresetRegistry::GetInstance();
    if (!registry) {
        LogAnkeError("PresetRegistry not available");
        return false;
    }
    
    registry->RegisterAnke(preset_name, std::move(anke_preset));
    LogAnkeDebug("Successfully registered '" + preset_name + "' from script");
    
    return true;
}

// ============ 内置函数实现 ============

/**
 * @brief 日志输出函数
 * 模式：log(message)
 * 将消息输出到战斗日志（battle info）
 * 
 * 参数说明：
 * - message (string): 要输出的消息文本
 * 
 * 返回值：0（成功）或 -1（失败）
 * 
 * 用例：
 *   log("This is a debug message");
 *   log("Damage applied: " + tostring(damage));
 */
static int builtin_log(ExecutionEnvironment* env)
{
    if (!env) {
        return -1;
    }
    
    int argc = env->GetArgumentCount();
    if (argc < 1) {
        // 没有参数的 log()，输出空行
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("\n");
        }
        return 0;
    }
    
    // 获取第一个参数
    auto arg = env->GetArgument(0);
    if (!arg) {
        return -1;
    }
    
    // 将参数转换为字符串
    std::string message = arg->ToString();
    
    // 输出到战斗日志
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog("[log] " + message + "\n");
    }
    
    // 同时也输出到 stderr（用于调试）
    fprintf(stderr, "[log] %s\n", message.c_str());
    
    return 0;
}

/**
 * @brief 造成伤害函数（支持 turn.multiplier）
 * 模式：dodamage(damage_index)
 * 从ExecutionEnvironment 获取当前actor和target，造成指定伤害
 * 
 * 参数说明：
 * - damage_index (0-3): 引用actor的dmg[i] (d1=最小, d2=较小, d3=较大, d4=最大)
 * 
 * ✨ 新增功能：自动读取 actor->turn.multiplier，应用乘数，然后重置
 */
static int builtin_dodamage(ExecutionEnvironment* env)
{
    // ========== 【诊断：文件 + 战斗日志双轨】 ==========
    // 因为 fprintf(stderr) 无法被察觉，改用文件方案确保可见
    FILE* diag_file = fopen("C:\\dodamage_diagnostic.log", "a");
    if (diag_file) {
        time_t now = time(nullptr);
        fprintf(diag_file, "\n╔════ [dodamage ENTRY DETECTED] ════╗\n");
        fprintf(diag_file, "Timestamp: %lld\n", (long long)now);
        fprintf(diag_file, "env pointer: %p\n", (void*)env);
        fprintf(diag_file, "g_current_round_manager: %s (not nullptr = can log)\n", 
                g_current_round_manager ? "✓ EXISTS" : "✗ NULL - LOGS LOST!");
        fprintf(diag_file, "════════════════════════════════════════\n");
        fflush(diag_file);
        fclose(diag_file);
    }
    
    // 【诊断日志】尝试写入战斗日志
    if (g_current_round_manager) {
        //g_current_round_manager->AppendSkillTriggerLog("[dodamage] ➤ FUNCTION ENTRY ➤ env=" + std::string(env ? "OK" : "NULL") + "\n");
    }
    
    if (!env) {
        if (g_current_round_manager) {
            //g_current_round_manager->AppendSkillTriggerLog("[dodamage] ✗ ERROR: env is null\n");
        }
        // 写入文件记录失败原因
        FILE* f = fopen("C:\\dodamage_diagnostic.log", "a");
        if (f) {
            fprintf(f, "ERROR: env is NULL - returning -1\n");
            fclose(f);
        }
        return -1;
    }
    
    // 获取函数参数个数
    int argc = env->GetArgumentCount();
    
    if (argc < 1) {
        if (g_current_round_manager) {
            //g_current_round_manager->AppendSkillTriggerLog("[dodamage] ✗ ERROR: argc < 1 (argc=" + std::to_string(argc) + ")\n");
        }
        FILE* f = fopen("C:\\dodamage_diagnostic.log", "a");
        if (f) {
            fprintf(f, "ERROR: argc < 1 (argc=%d) - returning -1\n", argc);
            fclose(f);
        }
        return -1;  // 至少需要1个参数
    }
    
    // 获取actor和target
    Character* actor = env->GetActor();
    Character* target = env->GetTarget();
    
    if (!actor || !target) {
        if (g_current_round_manager) {
            //g_current_round_manager->AppendSkillTriggerLog("[dodamage] ✗ ERROR: actor=" + std::string(actor ? "OK" : "NULL") + 
            //                                       ", target=" + std::string(target ? "OK" : "NULL") + "\n");
        }
        FILE* f = fopen("C:\\dodamage_diagnostic.log", "a");
        if (f) {
            fprintf(f, "ERROR: actor=%s, target=%s - returning -1\n", 
                    actor ? "OK" : "NULL", target ? "OK" : "NULL");
            fclose(f);
        }
        return -1;
    }
    
    // 获取第一个参数（伤害值索引或直接伤害值）
    auto dmg_arg = env->GetArgument(0);
    if (!dmg_arg) {
        if (g_current_round_manager) {
            //g_current_round_manager->AppendSkillTriggerLog("[dodamage] ✗ ERROR: dmg_arg is nullptr\n");
        }
        FILE* f = fopen("C:\\dodamage_diagnostic.log", "a");
        if (f) {
            fprintf(f, "ERROR: dmg_arg is nullptr - returning -1\n");
            fclose(f);
        }
        return -1;
    }
    
    int dmg_index = static_cast<int>(dmg_arg->ToInt());
    int base_dmg = dmg_index;
    
    FILE* f = fopen("C:\\dodamage_diagnostic.log", "a");
    if (f) {
        fprintf(f, "dmg_index=%d\n", dmg_index);
        fclose(f);
    }
    
    if (g_current_round_manager) {
        //g_current_round_manager->AppendSkillTriggerLog("[dodamage] 参数: dmg_index=" + std::to_string(dmg_index) + "\n");
    }
    
    // 如果dmg_index在0-3之间，则从actor的dmg数组中获取基础伤害
    if (dmg_index >= 0 && dmg_index < 4) {
        base_dmg = actor->dmg[dmg_index];  // d1=actor->dmg[0], d2=dmg[1], 等等
        if (g_current_round_manager) {
            //g_current_round_manager->AppendSkillTriggerLog("[dodamage] 从dmg[]数组获取基础伤害=" + std::to_string(base_dmg) + "\n");
        }
    }
    
    // ✨ 【修复】获取 turn.multiplier 乘数
    double multiplier = actor->turn.multiplier;  // 默认值
    bool multiplier_from_env = false;
    bool multiplier_from_actor = true;
    uint64_t env_self_handle_id = 0;
    bool env_self_is_handle = false;
    bool env_self_is_schema = false;
    double env_self_mult = -999.0;
    Value self_from_env = env->GetValueProperty("self");
    if (self_from_env.IsHandle() || self_from_env.IsSchema()) {
        env_self_is_handle = self_from_env.IsHandle();
        env_self_is_schema = self_from_env.IsSchema();
        if (env_self_is_handle) {
            env_self_handle_id = self_from_env.GetHandle().GetID();
        }
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog(
                "[BUILTIN_926] self_from_env.HasField('turn'): " + std::to_string(self_from_env.HasField("turn") ? 1 : 0) +
                " (IsHandle=" + std::to_string(env_self_is_handle ? 1 : 0) + ", IsSchema=" + std::to_string(env_self_is_schema ? 1 : 0) + ")\n");
        }
        if (self_from_env.HasField("turn")) {
            Value turn_field = self_from_env.GetField("turn");
            if (turn_field.IsSchema() && turn_field.HasField("multiplier")) {
                Value multiplier_field = turn_field.GetField("multiplier");
                if (multiplier_field.IsDouble()) {
                    multiplier = multiplier_field.GetDouble();
                    multiplier_from_env = true;
                    multiplier_from_actor = false;
                    env_self_mult = multiplier;
                } else if (multiplier_field.IsInt()) {
                    multiplier = (double)multiplier_field.GetInt();
                    multiplier_from_env = true;
                    multiplier_from_actor = false;
                    env_self_mult = multiplier;
                }
            }
        } else {
            if (g_current_round_manager) {
                g_current_round_manager->AppendSkillTriggerLog("[ERROR_926] Missing 'turn' field in self_from_env\n");
            }
        }
    }
    
    {
        const char* source = multiplier_from_env ? "env.self" : "actor";
        char source_buf[512];
        snprintf(source_buf, sizeof(source_buf),
                 "[DODAMAGE] multiplier source=%s env.self.IsHandle=%d env.self.handle_id=%llu env.self.IsSchema=%d env.self.turn.multiplier=%.6f actor.turn.multiplier=%.6f final_multiplier=%.6f",
                 source,
                 env_self_is_handle ? 1 : 0,
                 (unsigned long long)env_self_handle_id,
                 env_self_is_schema ? 1 : 0,
                 env_self_mult,
                 actor->turn.multiplier,
                 multiplier);
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog(std::string(source_buf) + "\n");
        }
    }
    
    // 计算最终伤害值
    int final_damage = static_cast<int>(base_dmg * multiplier);
    
    f = fopen("C:\\dodamage_diagnostic.log", "a");
    if (f) {
        fprintf(f, "base_dmg=%d, multiplier=%.2f, final_damage=%d\n", base_dmg, multiplier, final_damage);
        fclose(f);
    }
    
    if (g_current_round_manager) {
        //g_current_round_manager->AppendSkillTriggerLog("[dodamage] 计算: " + std::to_string(base_dmg) + " * " + std::to_string(multiplier) + " = " + std::to_string(final_damage) + "\n");
    }
    
    // 📊 诊断：记录目标身份
    {
        FILE* fdiag = fopen("C:\\dodamage_diagnostic.log", "a");
        if (fdiag) {
            fprintf(fdiag, "🎯 [TARGET INFO] name=%s, ptr=%p, camp=%d, hp_before=%d\n", 
                    target->name.c_str(), (void*)target, target->camp, target->hp);
            fclose(fdiag);
        }
    }
    
    // 检查是否有伤害回调（用于技能触发系统）
    auto damage_callback = ExecutionEnvironment::GetDamageCallback();
    int actual_damage = 0;
    
    if (damage_callback) {
        // 如果有回调，通过回调应用伤害（会触发被动技能）
        actual_damage = damage_callback((void*)actor, (void*)target, final_damage, "");
    } else {
        // 否则直接应用伤害
        actual_damage = target->TakeDamage(final_damage);
    }
    
    {
        FILE* fdiag = fopen("C:\\dodamage_diagnostic.log", "a");
        if (fdiag) {
            fprintf(fdiag, "actual_damage=%d\n", actual_damage);
            fprintf(fdiag, "Target HP before: %d, after: %d\n", target->hp + actual_damage, target->hp);
            fprintf(fdiag, ">>> dodamage() EXECUTION COMPLETE - SUCCESS <<<\n\n");
            fclose(fdiag);
        }
    }
    
    // 【新增】向 battleinfo 添加伤害显示文本
    if (g_current_round_manager) {
        // 获取目标的最大 HP（从 Character 类获取，假设有 max_hp 属性）
        // 如果没有max_hp，则使用当前hp+受到的伤害作为参考
        int target_max_hp = target->max_hp > 0 ? target->max_hp : (target->hp + actual_damage);
        int target_current_hp = target->hp;
        
        // ✨ 【新增】详细日志：显示完整的伤害计算过程
        std::string damage_display;
        
        // 详细显示计算过程
        std::string calc_detail = "  [伤害计算] 基础=" + std::to_string(base_dmg) + 
                                 " × 系数" + std::to_string(multiplier) + 
                                 " = " + std::to_string(final_damage) + 
                                 " (防御减免后=" + std::to_string(actual_damage) + ")";
        g_current_round_manager->AppendSkillTriggerLog(calc_detail + "\n");
        
        // 构建伤害显示文本
        if (multiplier > 1.0) {
            // 格式：造成 x*n 点伤害，hp xx/yy
            damage_display = "造成 " + std::to_string((int)multiplier) + "*" + std::to_string(base_dmg) + 
                           " (共" + std::to_string(actual_damage) + ")点伤害，" +
                           target->name + " HP " + std::to_string(target_current_hp) + "/" + std::to_string(target_max_hp);
        } else {
            // 格式：造成 n 点伤害，hp xx/yy
            damage_display = "造成 " + std::to_string(actual_damage) + " 点伤害，" +
                           target->name + " HP " + std::to_string(target_current_hp) + "/" + std::to_string(target_max_hp);
        }
        
        g_current_round_manager->AppendSkillTriggerLog(damage_display + "\n");
    }
    
    // 【调试】伤害应用后自动重置
    // LogDodamage("重置倍增系数: " + std::to_string(actor->turn.multiplier) + " -> 1.0");
    actor->turn.multiplier = 1.0;
    
    // 将实际造成的伤害返回给脚本
    env->SetIntProperty("__return__", actual_damage);
    
    LogDodamage("<<< 函数执行完成，返回值: " + std::to_string(actual_damage));
    return actual_damage;
}

/**
 * @brief 调用攻击预设 (ANKE) 函数
 * 模式：akr("preset_name") 或 akr("natk")
 * 从预设注册表查找并执行 ANKE 预设
 * 
 * 【重要】支持递归调用（用于大成功重新投掷）
 * 核心诊断：此函数是判断 es 脚本中 akr("natk") 是否被调用的关键点
 */
static int builtin_akr(ExecutionEnvironment* env)
{
    if (!env) return -1;
    
    // 【诊断关键点1】akr() 函数入口 - 确认被调用
    // FILE* diag_file = fopen("C:\\dodamage_diagnostic.log", "a");
    // if (diag_file) {
    //     fprintf(diag_file, "\n✓✓✓ [akr CALLED] Function entry detected ✓✓✓\n");
    //     fprintf(diag_file, "    Timestamp: %lld\n", (long long)time(nullptr));
    //     fflush(diag_file);
    // }
    
    // 获取函数参数个数
    int argc = env->GetArgumentCount();
    if (argc < 1) {
        // if (diag_file) {
        //     fprintf(diag_file, "    ERROR: argc < 1 (argc=%d) - RETURNING -1\n", argc);
        //     fflush(diag_file);
        // }
        // if (diag_file) fclose(diag_file);
        return -1;
    }
    
    // 获取ANKE预设名称参数
    auto preset_name_arg = env->GetArgument(0);
    if (!preset_name_arg) {
        // if (diag_file) {
        //     fprintf(diag_file, "    ERROR: preset_name_arg is null\n");
        //     fflush(diag_file);
        // }
        // if (diag_file) fclose(diag_file);
        return -1;
    }
    
    std::string preset_name = preset_name_arg->ToString();
    
    // 【诊断关键点2】获取预设名称
    // if (diag_file) {
    //     fprintf(diag_file, "    Preset name: '%s' (length=%lu)\n", preset_name.c_str(), preset_name.length());
    //     fprintf(diag_file, "    Raw bytes: ");
    //     for (size_t i = 0; i < preset_name.length(); i++) {
    //         fprintf(diag_file, "[%d] ", (unsigned char)preset_name[i]);
    //     }
    //     fprintf(diag_file, "\n");
    //     fflush(diag_file);
    // }
    
    // 【诊断】尝试修复末尾引号
    if (!preset_name.empty() && preset_name.back() == '"') {
        // if (diag_file) {
        //     fprintf(diag_file, "    WARNING: Found trailing quote! Removing...\n");
        //     fprintf(diag_file, "    Before: '%s'\n", preset_name.c_str());
        // }
        preset_name.pop_back();
        // if (diag_file) {
        //     fprintf(diag_file, "    After: '%s'\n", preset_name.c_str());
        // }
    }
    
    // 从预设注册表查找ANKE预设
    PresetRegistry* registry = PresetRegistry::GetInstance();
    PresetBase* anke = registry->GetPreset(PresetType::ANKE, preset_name);
    
    // 【诊断关键点3】预设查找
    if (!anke) {
        // if (diag_file) {
        //     fprintf(diag_file, "    ERROR: ANKE preset '%s' NOT FOUND in registry\n", preset_name.c_str());
        //     fprintf(diag_file, "    RETURNING -1 - PRESET NOT FOUND\n");
        //     fflush(diag_file);
        // }
        // if (diag_file) fclose(diag_file);
        
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog(
                "[DEBUG] ✗ ANKE preset '" + preset_name + "' not found in registry\n");
        }
        return -1;
    }
    
    // 【诊断关键点4】执行开始
    // if (diag_file) {
    //     fprintf(diag_file, "    ✓ Found preset '%s' - about to execute\n", preset_name.c_str());
    //     fprintf(diag_file, "    Calling anke->Execute(env)...\n");
    //     fflush(diag_file);
    // }
    
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog(
            "[DEBUG] ▶▶▶ EXECUTING PRESET '" + preset_name + "' (THIS IS THE RECURSIVE CALL)\n");
    }

    // 诊断：打印 recursion env 状态
    if (env) {
        Character* actor = env->GetActor();
        double actor_mult = actor ? actor->turn.multiplier : -999.0;
        Value self_val = env->GetValueProperty("self");
        double self_mult = -999.0;
        if (self_val.IsSchema() || self_val.IsHandle()) {
            if (self_val.HasField("turn")) {
                Value turn_field = self_val.GetField("turn");
                if (turn_field.IsSchema() && turn_field.HasField("multiplier")) {
                    Value mult_field = turn_field.GetField("multiplier");
                    if (mult_field.IsDouble()) {
                        self_mult = mult_field.GetDouble();
                    } else if (mult_field.IsInt()) {
                        self_mult = (double)mult_field.GetInt();
                    }
                }
            }
        }
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog(
                "[DEBUG] RECURSIVE CALL DEBUG: actor.turn.multiplier=" + std::to_string(actor_mult) +
                " env.self.type=" + std::to_string((int)self_val.GetType()) +
                " env.self.handle=" + std::to_string(self_val.IsHandle() ? self_val.GetHandle().GetID() : 0) +
                " env.self.turn.multiplier=" + std::to_string(self_mult) + "\n");
        }
    }
    
    // 【核心】执行ANKE预设
    int result = anke->Execute(env);
    
    // 【诊断关键点5】执行完成
    // if (diag_file) {
    //     fprintf(diag_file, "    Execution completed. Result: %d\n", result);
    //     fprintf(diag_file, "✓✓✓ [akr COMPLETED] Returning %d ✓✓✓\n", result);
    //     fprintf(diag_file, "\n");
    //     fflush(diag_file);
    //     fclose(diag_file);
    // }
    
    if (g_current_round_manager) {
        g_current_round_manager->AppendSkillTriggerLog(
            "[DEBUG] ◀◀◀ PRESET EXECUTION COMPLETE (result=" + std::to_string(result) + ")\n");
    }
    
    return result;
}

/**
 * @brief 随机数生成函数
 * 模式：random(min, max)
 * 返回 [min, max] 范围内的随机整数
 * 
 * 返回值通过 __return__ 属性返回给调用者
 */
static int builtin_random(ExecutionEnvironment* env)
{
    if (!env) {
        return -1;
    }
    
    int argc = env->GetArgumentCount();
    if (argc < 2) {
        return -1;
    }
    
    auto min_arg = env->GetArgument(0);
    if (!min_arg) {
        return -1;
    }
    int min_val = static_cast<int>(min_arg->ToInt());
    
    auto max_arg = env->GetArgument(1);
    if (!max_arg) {
        return -1;
    }
    int max_val = static_cast<int>(max_arg->ToInt());
    
    // 验证参数范围
    if (max_val < min_val) {
        return -1;
    }
    
    int result = rand() % (max_val - min_val + 1) + min_val;
    env->SetIntProperty("__return__", result);
    
    return result;
}

/**
 * @brief 暴击函数
 * 模式：crit(base_damage, crit_rate, crit_multiplier)
 * 基于暴击几率判断是否暴击
 */
static int builtin_crit(ExecutionEnvironment* env)
{
    if (!env) return -1;
    
    // TODO: 从函数参数获取：
    // - base_damage
    // - crit_rate (0-100)
    // - crit_multiplier (默认 1.5)
    // 
    // 计算：
    // random_val = rand() % 100
    // if (random_val < crit_rate) {
    //     return base_damage * crit_multiplier
    // }
    // return base_damage
    
    return 0;
}

/**
 * @brief 获取属性值函数
 * 模式：get_property(actor, property_name)
 * 从执行环境获取角色属性
 */
static int builtin_get_property(ExecutionEnvironment* env)
{
    if (!env) return -1;
    
    // TODO: 从函数参数获取 actor, property_name
    // 从执行环境访问 actor->GetProperty(property_name)
    
    return 0;
}

/**
 * @brief 设置属性值函数
 * 模式：set_property(actor, property_name, value)
 * 设置角色属性
 */
static int builtin_set_property(ExecutionEnvironment* env)
{
    if (!env) return -1;
    
    // TODO: 从函数参数获取 actor, property_name, value
    // 调用 actor->SetProperty(property_name, value)
    
    return 0;
}

/**
 * @brief 获取攻击者函数
 * 模式：get_actor()
 * 返回当前执行环境的攻击者
 */
static int builtin_get_actor(ExecutionEnvironment* env)
{
    if (!env) return -1;
    
    // character* actor = env->GetActor();
    // 返回 actor 指针作为 Value
    
    return 0;
}

/**
 * @brief 获取目标函数
 * 模式：get_target()
 * 返回当前执行环境的目标
 */
static int builtin_get_target(ExecutionEnvironment* env)
{
    if (!env) return -1;
    
    // character* target = env->GetTarget();
    // 返回 target 指针作为 Value
    
    return 0;
}

/**
 * @brief 转移攻击者函数 - 用于大失败时调用
 * 模式：shiftattacker()
 * 
 * 功能：
 * - 基于同阵营其他活着单位的仇恨度(aggro)进行加权随机选择
 * - 将攻击权转移到新的攻击者
 * - 触发 OnAttackerShifted 技能
 * - 输出结果到战斗日志
 * 
 * 返回值：0 表示转移成功，-1 表示失败
 * 
 * 使用场景：在大失败时从脚本调用，自动选择接替者
 * 权重机制：新攻击者按照其aggro属性作为权重选择
 */
static int builtin_shiftattacker(ExecutionEnvironment* env)
{
    if (!env) {
        return -1;
    }
    
    // 获取当前执行环境中的参考目标（可选）
    Character* target = env->GetTarget();
    
    // 获取全局 RoundManager 实例
    // 使用 g_current_round_manager 全局线程本地变量
    if (!g_current_round_manager) {
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("[shiftattacker] ✗ RoundManager 不可用\n");
        }
        return -1;  // 没有运行中的RoundManager
    }
    
    // 调用 RoundManager 的 ShiftAttacker 方法
    std::shared_ptr<Character> target_ptr;
    if (target) {
        // 如果有目标，将其作为参考（虽然当前实现中不使用）
        target_ptr = nullptr;  // 可后续扩展
    }
    
    bool success = g_current_round_manager->ShiftAttacker(target_ptr);
    
    if (success) {
        // 成功转移
        if (g_current_round_manager) {
            Character* new_actor = env->GetActor();  // ShiftAttacker() 后，这会是新的攻击者
            if (new_actor) {
                g_current_round_manager->AppendSkillTriggerLog("  [大失败-攻击手切换：" + new_actor->name + "]\n");
            }
        }
        return 0;
    } else {
        // 转移失败（没有其他活着的同阵营成员）
        if (g_current_round_manager) {
            g_current_round_manager->AppendSkillTriggerLog("  [大失败-无法切换(无其他可用成员)]\n");
        }
        return -1;
    }
}

/**
 * @brief 获取类型名称函数
 * 模式：gettype(value)
 * 返回值的类型名称字符串
 * 
 * 返回值通过 __return__ 属性返回给调用者
 * 例如：gettype(123) -> "int"
 */
static int builtin_gettype(ExecutionEnvironment* env)
{
    if (!env) {
        return -1;
    }
    
    int argc = env->GetArgumentCount();
    if (argc < 1) {
        return -1;
    }
    
    auto arg = env->GetArgument(0);
    if (!arg) {
        return -1;
    }
    
    // 获取类型名称：Value::GetTypeInfo() 返回 const TypeInfo*
    const TypeInfo* type_info = arg->GetTypeInfo();
    std::string type_name = (type_info != nullptr) ? type_info->name : "unknown";
    
    // 返回类型名称给脚本
    env->SetValueProperty("__return__", Value(type_name));
    
    return 0;
}

/**
 * @brief 显式字符串转换函数
 * 模式：tostring(value)
 * 将值转换为字符串表示
 * 
 * 返回值通过 __return__ 属性返回给调用者
 * 例如：tostring(123) -> "123"
 */
static int builtin_tostring(ExecutionEnvironment* env)
{
    if (!env) {
        return -1;
    }
    
    int argc = env->GetArgumentCount();
    if (argc < 1) {
        return -1;
    }
    
    auto arg = env->GetArgument(0);
    if (!arg) {
        return -1;
    }
    
    // 转换为字符串并返回
    std::string str_value = arg->ToString();
    env->SetValueProperty("__return__", Value(str_value));
    
    return 0;
}

/**
 * @brief 类型信息和反射函数
 * 模式：typeinfo(value)
 * 输出详细的类型信息和值内容（用于调试）
 * 
 * 返回值为 0（成功）或 -1（失败）
 * 副作用：输出详细的类型信息到stdout
 * 
 * 用例：在脚本调试时获取变量的类型信息
 */
static int builtin_typeinfo(ExecutionEnvironment* env)
{
    if (!env) {
        return -1;
    }
    
    int argc = env->GetArgumentCount();
    if (argc < 1) {
        return -1;
    }
    
    auto arg = env->GetArgument(0);
    if (!arg) {
        return -1;
    }
    
    // 获取类型信息
    const TypeInfo* type_info = arg->GetTypeInfo();
    std::string type_name = (type_info != nullptr) ? type_info->name : "unknown";
    std::string str_value = arg->ToString();
    
    // 输出统一的类型信息
    printf("\n[typeinfo] Type: %s\n", type_name.c_str());
    printf("[typeinfo] Value: %s\n", str_value.c_str());
    printf("[typeinfo] TypeInfo: %p\n\n", (void*)type_info);
    
    return 0;
}

// ============ 系统预设初始化 ============

void InitializeBuiltinPresets()
{
    PresetRegistry* registry = PresetRegistry::GetInstance();
    
    // 注册内置函数
    // 这些是辅助函数，为用户定义的技能和状态提供基础功能
    registry->RegisterFunction("log", builtin_log, true);  // 日志输出函数
    registry->RegisterFunction("dodamage", builtin_dodamage, true);
    registry->RegisterFunction("akr", builtin_akr, true);
    registry->RegisterFunction("random", builtin_random, true);
    registry->RegisterFunction("crit", builtin_crit, true);
    registry->RegisterFunction("get_property", builtin_get_property, true);
    registry->RegisterFunction("set_property", builtin_set_property, true);
    registry->RegisterFunction("get_actor", builtin_get_actor, true);
    registry->RegisterFunction("get_target", builtin_get_target, true);
    registry->RegisterFunction("shiftattacker", builtin_shiftattacker, true);
    
    // 注册TypeInfo反射函数
    registry->RegisterFunction("gettype", builtin_gettype, true);
    registry->RegisterFunction("tostring", builtin_tostring, true);
    registry->RegisterFunction("typeinfo", builtin_typeinfo, true);
    
    // ========== 从用户脚本加载 NATK (Normal Attack) ANKE 预设 ==========
    // 使用统一的 ANKE 脚本格式（原始 ABOL，自动编码）
    // 脚本包含完整的 Turn 倍增系统集成和 es/ef 分支
    printf("[NATK] ========== Loading NATK from ANKE Script (Raw ABOL, auto-encoded) ==========\n");
    
    if (!RegisterAnkeFromString(NATK_ANKE_SCRIPT)) {
        printf("[NATK] ========== FAILED to load NATK script ==========\n");
    } else {
        printf("[NATK] ========== Successfully loaded NATK from script ==========\n");
    }
    
    // 注意: 不设置系统技能或系统状态
    // 所有的技能和状态都应该由用户在其角色卡中定义
    // 通过 SkillDef 和 StateDefinition 灵活地传入参数
}

}  // namespace abot


// ============ 脚本编译诊断函数（命名空间外部，C链接） ============

/**
 * @brief 诊断脚本编译状态
 * @param script_source 脚本源代码
 * @param out_error 输出错误消息
 * @param out_error_len 错误消息缓冲区大小
 * @return 编译状态 (0=成功, 1=Lexer错误, 2=Parser错误, 3=Compiler错误, -1=参数错误)
 */
extern "C" ABOT_API int DiagnoseScriptCompilation(const char* script_source, char* out_error, int out_error_len)
{
    if (!script_source || !out_error || out_error_len <= 0) {
        return -1;
    }
    
    try {
        std::string error_msg;
        
        // 第1步：词法分析
        abot::Lexer lexer(script_source);
        auto tokens = lexer.ScanTokens();
        
        if (lexer.HasError()) {
            error_msg = "LEXER_ERROR: " + lexer.GetErrorMessage();
            strncpy(out_error, error_msg.c_str(), out_error_len - 1);
            out_error[out_error_len - 1] = '\0';
            return 1;  // Lexer失败
        }
        
        // 第2步：语法分析
        abot::Parser parser(tokens);
        auto statements = parser.ParseProgram();
        
        if (parser.HasError()) {
            error_msg = "PARSER_ERROR: " + parser.GetErrorMessage();
            strncpy(out_error, error_msg.c_str(), out_error_len - 1);
            out_error[out_error_len - 1] = '\0';
            return 2;  // Parser失败
        }
        
        // 第3步：字节码编译
        abot::BytecodeCompiler compiler;
        auto bytecode = compiler.Compile(statements);
        
        if (!bytecode) {
            error_msg = "COMPILER_ERROR: Bytecode generation failed";
            strncpy(out_error, error_msg.c_str(), out_error_len - 1);
            out_error[out_error_len - 1] = '\0';
            return 3;  // Compiler失败
        }
        
        // 编译成功
        strncpy(out_error, "SUCCESS", out_error_len - 1);
        out_error[out_error_len - 1] = '\0';
        return 0;
    }
    catch (const std::exception& e) {
        strncpy(out_error, e.what(), out_error_len - 1);
        out_error[out_error_len - 1] = '\0';
        return -1;
    }
    catch (...) {
        strncpy(out_error, "Unknown exception", out_error_len - 1);
        out_error[out_error_len - 1] = '\0';
        return -1;
    }
}

