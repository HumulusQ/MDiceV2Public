/**
 * @file C_API.cpp
 * @brief ABOT C API 的实现
 * 
 * 这个文件将C API映射到C++对象
 * 每个ABOT_HANDLE实际上是指向ABotContext的指针
 */

#include "C_API.h"
#include "Lexer.h"
#include "Parser.h"
#include "Bytecode.h"
#include "VM.h"
#include "Character.h"
#include "ParameterParser.h"
#include "Battle.h"
#include "RoundManager.h"
#include "PresetSystem.h"
#include "BuiltinPresets.h"
#include <cstring>
#include <memory>
#include <cstdlib>
#include <map>
#include <cstdio>
#include <windows.h>
#include <vector>
#include <sstream>
#include <iomanip>
#include <io.h>
#include <fcntl.h>
// DecodeBase64, EncodeBase64, PrepareScriptForCompilation 都在那里定义
// 这样ANKE、SkillDef和其他组件都使用同一套经过验证的编解码逻辑

namespace abot {

/**
 * @brief 对象句柄管理器
 * 用于安全地管理C API中的opaque指针
 */
class HandleManager {
public:
    using CharacterPtr = std::shared_ptr<Character>;
    using ParameterNodePtr = std::shared_ptr<ParameterNode>;
    using BattlePtr = std::shared_ptr<Battle>;
    using RoundManagerPtr = std::shared_ptr<RoundManager>;
    
private:
    std::map<uintptr_t, CharacterPtr> characters;
    std::map<uintptr_t, ParameterNodePtr> parameters;
    std::map<uintptr_t, BattlePtr> battles;
    std::map<uintptr_t, RoundManagerPtr> round_managers;
    uintptr_t next_id = 1;
    
public:
    uintptr_t RegisterCharacter(const CharacterPtr& ch) {
        uintptr_t id = next_id++;
        characters[id] = ch;
        return id;
    }
    
    uintptr_t RegisterParameter(const ParameterNodePtr& param) {
        uintptr_t id = next_id++;
        parameters[id] = param;
        return id;
    }
    
    uintptr_t RegisterBattle(const BattlePtr& battle) {
        uintptr_t id = next_id++;
        battles[id] = battle;
        return id;
    }
    
    uintptr_t RegisterRoundManager(const RoundManagerPtr& rm) {
        uintptr_t id = next_id++;
        round_managers[id] = rm;
        return id;
    }
    
    CharacterPtr GetCharacter(uintptr_t id) {
        auto it = characters.find(id);
        return (it != characters.end()) ? it->second : nullptr;
    }
    
    ParameterNodePtr GetParameter(uintptr_t id) {
        auto it = parameters.find(id);
        return (it != parameters.end()) ? it->second : nullptr;
    }
    
    BattlePtr GetBattle(uintptr_t id) {
        auto it = battles.find(id);
        return (it != battles.end()) ? it->second : nullptr;
    }
    
    RoundManagerPtr GetRoundManager(uintptr_t id) {
        auto it = round_managers.find(id);
        return (it != round_managers.end()) ? it->second : nullptr;
    }
    
    void UnregisterCharacter(uintptr_t id) {
        characters.erase(id);
    }
    
    void UnregisterParameter(uintptr_t id) {
        parameters.erase(id);
    }
    
    void UnregisterBattle(uintptr_t id) {
        battles.erase(id);
    }
    
    void UnregisterRoundManager(uintptr_t id) {
        round_managers.erase(id);
    }
};

/**
 * @brief ABOT解释器上下文
 * 隐藏在C API后面的实际C++对象
 */
class ABotContext {
public:
    ABotContext() : vm(std::make_unique<VM>()), 
                    scope(std::make_unique<ScopeStack>()),
                    error_message("") {
    }
    
    std::unique_ptr<VM> vm;
    std::unique_ptr<ScopeStack> scope;
    std::unique_ptr<BytecodeProgram> program;
    std::unique_ptr<Character> parsed_character;  // 存储解析后的角色数据
    std::shared_ptr<RoundManager> round_manager;  // 回合管理器
    std::string error_message;
    std::string status_buffer;                    // 用于返回状态字符串
    std::string log_buffer;                       // 用于返回日志字符串
    std::string skill_trigger_log_buffer;         // 用于返回技能触发日志字符串
    // handle_manager 现在使用全局的
    
    void SetError(const std::string& msg) {
        error_message = msg;
    }
    
    void ClearError() {
        error_message.clear();
    }
};

}  // namespace abot

// ============ UTF-8日志写入辅助函数 ============
/// <summary>
/// UTF-8安全的日志写入函数
/// 使用宽字符版本的 fopen 和内部编码转换，确保中文字符正确输出
/// </summary>
inline void WriteUtf8Log(const char* filename, const char* utf8_text) {
    if (!filename || !utf8_text) return;
    
    // 使用宽字符版本打开文件（自动处理UTF-8编码问题）
    wchar_t filename_w[MAX_PATH];
    MultiByteToWideChar(CP_ACP, 0, filename, -1, filename_w, MAX_PATH);
    
    FILE* f = nullptr;
    errno_t err = _wfopen_s(&f, filename_w, L"at, ccs=UTF-8");
    
    if (f == nullptr) {
        // 如果打开失败，尝试用ASCII模式
        fopen_s(&f, filename, "at");
    }
    
    if (f) {
        // 转换UTF-8字符串为宽字符，再写入文件
        int required_size = MultiByteToWideChar(CP_UTF8, 0, utf8_text, -1, nullptr, 0);
        if (required_size > 0) {
            wchar_t* wide_text = new wchar_t[required_size];
            MultiByteToWideChar(CP_UTF8, 0, utf8_text, -1, wide_text, required_size);
            fwprintf(f, L"%ls", wide_text);
            delete[] wide_text;
        } else {
            // 转换失败，直接输出（可能乱码但不会崩溃）
            fprintf(f, "%s", utf8_text);
        }
        fflush(f);
        fclose(f);
    }
}

// ============ 全局句柄管理器 ============
static abot::HandleManager g_global_handle_manager;

// ============ 生命周期管理 ============

extern "C" {

ABOT_API ABOT_HANDLE abot_create(void) {
    FILE* log_file = nullptr;
    fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    if (log_file) {
        fprintf(log_file, "\n[abot_create] Called at thread %u\n", GetCurrentThreadId());
    }
    
    try {
        if (log_file) fprintf(log_file, "[abot_create] Creating new ABotContext...\n");
        auto context = new abot::ABotContext();
        
        if (log_file) {
            fprintf(log_file, "[abot_create] ABotContext allocated at %p\n", (void*)context);
        }
        
        // 验证 context 创建成功
        if (context == nullptr) {
            if (log_file) {
                fprintf(log_file, "[abot_create] FAILED: context == nullptr\n");
                fclose(log_file);
            }
            return nullptr;
        }
        
        if (log_file) {
            fprintf(log_file, "[abot_create] Checking vm: %p\n", (void*)(context->vm.get()));
            fprintf(log_file, "[abot_create] Checking scope: %p\n", (void*)(context->scope.get()));
        }
        
        // 验证内部组件初始化
        if (context->vm == nullptr || context->scope == nullptr) {
            if (log_file) {
                fprintf(log_file, "[abot_create] FAILED: vm=%p, scope=%p\n", 
                        (void*)(context->vm.get()),
                        (void*)(context->scope.get()));
                fclose(log_file);
            }
            delete context;
            return nullptr;
        }
        
        if (log_file) {
            fprintf(log_file, "[abot_create] SUCCESS: Returning handle %p\n", (void*)context);
            fclose(log_file);
        }
        return static_cast<ABOT_HANDLE>(context);
    } catch (const std::exception& e) {
        if (log_file) {
            fprintf(log_file, "[abot_create] EXCEPTION: %s\n", e.what());
            fclose(log_file);
        }
        return nullptr;
    } catch (...) {
        if (log_file) {
            fprintf(log_file, "[abot_create] UNKNOWN EXCEPTION\n");
            fclose(log_file);
        }
        return nullptr;
    }
}

ABOT_API void abot_destroy(ABOT_HANDLE handle) {
    if (!handle) return;
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        delete context;
    } catch (...) {
        // 忽略错误
    }
}

// ============ 脚本加载和编译 ============

ABOT_API ABOT_ERROR abot_parse_character(ABOT_HANDLE handle, const char* character_xml) {
    if (!handle || !character_xml) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        // 验证XML字符串不为空
        if (std::strlen(character_xml) == 0) {
            context->SetError("Empty XML string");
            return ABOT_ERROR_INVALID_XML;
        }
        
        // 创建新Character并解析
        auto character = std::make_unique<abot::Character>();
        std::string xml_str(character_xml);
        
        if (!abot::ParameterParser::ParseCharacterCard(xml_str, *character)) {
            context->SetError(abot::ParameterParser::GetLastError());
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        // 存储解析后的角色数据
        context->parsed_character = std::move(character);
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Parse error: ") + e.what());
        return ABOT_ERROR_PARSE_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_register_skillset(ABOT_HANDLE handle, const char* skillset_xml) {
    // 最早的诊断输出
    FILE* early_diag = nullptr;
    fopen_s(&early_diag, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
    if (early_diag) {
        fprintf(early_diag, "[FUNCTION START] abot_register_skillset() CALLED at address %p\n", handle);
        fprintf(early_diag, "[FUNCTION START] skillset_xml parameter present: %s\n", skillset_xml ? "YES" : "NO");
        fflush(early_diag);
        fclose(early_diag);
    }
    
    FILE* skillset_log = nullptr;
    fopen_s(&skillset_log, "C:\\Windows\\Temp\\abot_skillset_register.log", "at");
    if (skillset_log) {
        fprintf(skillset_log, "\n========== abot_register_skillset CALLED ==========\n");
        fflush(skillset_log);
    }
    
    fprintf(stderr, "[SKILLSET REGISTER] ============ abot_register_skillset() CALLED ============\n");
    fflush(stderr);
    
    if (!handle || !skillset_xml) {
        fprintf(stderr, "[SKILLSET REGISTER] ERROR: handle=%p, skillset_xml=%p\n", handle, skillset_xml);
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->ClearError();
        
        std::string xml_str(skillset_xml);
        fprintf(stderr, "[SKILLSET REGISTER] Input XML length: %zu\n", xml_str.length());
        
        // 诊断：输出完整的XML内容
        fprintf(stderr, "[SKILLSET REGISTER] ========== COMPLETE XML INPUT ==========\n");
        fprintf(stderr, "%s\n", xml_str.c_str());
        fprintf(stderr, "[SKILLSET REGISTER] ========== END COMPLETE XML ==========\n");
        
        if (skillset_log) {
            fprintf(skillset_log, "[SKILLSET REGISTER] Input XML length: %zu\n", xml_str.length());
            fprintf(skillset_log, "[SKILLSET REGISTER] Complete XML:\n%s\n", xml_str.c_str());
            fflush(skillset_log);
        }
        
        // 查找 <skilldef 标签
        size_t skilldef_pos = xml_str.find("<skilldef");
        if (skilldef_pos == std::string::npos) {
            context->SetError("No skilldef found in skillset");
            fprintf(stderr, "[SKILLSET REGISTER] ERROR: No skilldef found\n");
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        size_t start_pos = xml_str.find(">", skilldef_pos);
        if (start_pos == std::string::npos) {
            context->SetError("Malformed skilldef tag");
            fprintf(stderr, "[SKILLSET REGISTER] ERROR: Malformed skilldef tag\n");
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        // 提取属性部分: id=..., para={...}, def=...
        std::string tag_content = xml_str.substr(skilldef_pos + 9, start_pos - skilldef_pos - 9);
        fprintf(stderr, "[SKILLSET REGISTER] Tag content: '%s'\n", tag_content.c_str());
        
        // 诊断：输出 tag_content 的详细信息
        fprintf(stderr, "[SKILLSET REGISTER] ========== TAG CONTENT DETAILS ==========\n");
        fprintf(stderr, "[SKILLSET REGISTER] Tag length: %zu\n", tag_content.length());
        fprintf(stderr, "[SKILLSET REGISTER] First 50 chars: '%.50s'\n", tag_content.c_str());
        fprintf(stderr, "[SKILLSET REGISTER] Full tag content ASCII:\n%s\n", tag_content.c_str());
        fprintf(stderr, "[SKILLSET REGISTER] HEX dump of tag_content:\n");
        for (size_t i = 0; i < tag_content.length(); i += 32) {
            fprintf(stderr, "[SKILLSET REGISTER] [%04zu] ", i);
            for (size_t j = i; j < i + 32 && j < tag_content.length(); j++) {
                fprintf(stderr, "%02X ", (unsigned char)tag_content[j]);
            }
            fprintf(stderr, "\n");
        }
        fprintf(stderr, "[SKILLSET REGISTER] ========== END TAG CONTENT ==========\n");
        
        // 提取 id (处理可能的空白和逗号)
        auto extract_attribute = [&](const std::string& attr_name) -> std::string {
            fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Searching for: '%s'\n", attr_name.c_str());
            if (skillset_log) fprintf(skillset_log, "[extract_attribute] Searching for: '%s'\n", attr_name.c_str());
            
            size_t attr_pos = tag_content.find(attr_name);
            if (attr_pos == std::string::npos) {
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] '%s' not found in tag_content\n", attr_name.c_str());
                if (skillset_log) fprintf(skillset_log, "[extract_attribute] '%s' not found in tag_content\n", attr_name.c_str());
                return "";
            }
            
            fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Found '%s' at position %zu\n", attr_name.c_str(), attr_pos);
            if (skillset_log) fprintf(skillset_log, "[extract_attribute] Found '%s' at position %zu\n", attr_name.c_str(), attr_pos);
            
            // 继续查找 '='
            attr_pos += attr_name.length();
            fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] After '%s', position=%zu, next char='%c' (0x%02X)\n", 
                    attr_name.c_str(), attr_pos, 
                    attr_pos < tag_content.length() ? tag_content[attr_pos] : '?',
                    attr_pos < tag_content.length() ? (unsigned char)tag_content[attr_pos] : 0);
            if (skillset_log) fprintf(skillset_log, "[extract_attribute] After '%s', position=%zu, next char='%c' (0x%02X)\n", 
                    attr_name.c_str(), attr_pos, 
                    attr_pos < tag_content.length() ? tag_content[attr_pos] : '?',
                    attr_pos < tag_content.length() ? (unsigned char)tag_content[attr_pos] : 0);
            
            // 跳过空白和'='来找到等号
            while (attr_pos < tag_content.length() && (tag_content[attr_pos] == ' ' || tag_content[attr_pos] == '=')) {
                if (tag_content[attr_pos] == '=') {
                    attr_pos++;
                    break;
                }
                attr_pos++;
            }
            
            fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] After '=', position=%zu\n", attr_pos);
            if (skillset_log) fprintf(skillset_log, "[extract_attribute] After '=', position=%zu\n", attr_pos);
            
            // 跳过空白
            while (attr_pos < tag_content.length() && tag_content[attr_pos] == ' ') {
                attr_pos++;
            }
            
            fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] After whitespace, position=%zu, next char='%c' (0x%02X)\n", 
                    attr_pos,
                    attr_pos < tag_content.length() ? tag_content[attr_pos] : '?',
                    attr_pos < tag_content.length() ? (unsigned char)tag_content[attr_pos] : 0);
            if (skillset_log) fprintf(skillset_log, "[extract_attribute] After whitespace, position=%zu, next char='%c' (0x%02X)\n", 
                    attr_pos,
                    attr_pos < tag_content.length() ? tag_content[attr_pos] : '?',
                    attr_pos < tag_content.length() ? (unsigned char)tag_content[attr_pos] : 0);
            
            if (attr_pos >= tag_content.length()) {
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Reached end of tag_content\n");
                if (skillset_log) fprintf(skillset_log, "[extract_attribute] Reached end of tag_content\n");
                return "";
            }
            
            // 查找值的边界，优先检查expr(
            if (attr_pos + 5 <= tag_content.length() && tag_content.substr(attr_pos, 5) == "expr(") {
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Found expr( at position %zu\n", attr_pos);
                if (skillset_log) fprintf(skillset_log, "[extract_attribute] Found expr( at position %zu\n", attr_pos);
                
                // expr(...) 格式
                size_t paren_count = 0;
                size_t end = attr_pos + 5;
                paren_count = 1;
                while (end < tag_content.length() && paren_count > 0) {
                    if (tag_content[end] == '(') paren_count++;
                    else if (tag_content[end] == ')') paren_count--;
                    end++;
                }
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] expr(...) ends at position %zu\n", end);
                if (skillset_log) fprintf(skillset_log, "[extract_attribute] expr(...) ends at position %zu (paren_count finished)\n", end);
                
                std::string result = tag_content.substr(attr_pos, end - attr_pos);
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Returning expr value, length=%zu, first 50 chars: '%.50s'\n", result.length(), result.c_str());
                if (skillset_log) {
                    fprintf(skillset_log, "[extract_attribute] RETURNING expr value\n");
                    fprintf(skillset_log, "[extract_attribute]   Length: %zu\n", result.length());
                    fprintf(skillset_log, "[extract_attribute]   First 100 chars: ");
                    fwrite(result.c_str(), 1, (result.length() > 100 ? 100 : result.length()), skillset_log);
                    fprintf(skillset_log, "\n");
                    fflush(skillset_log);
                }
                return result;
            } else if (tag_content[attr_pos] == '{') {
                // {...} 格式
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Found { at position %zu\n", attr_pos);
                if (skillset_log) fprintf(skillset_log, "[extract_attribute] Found { at position %zu (brace mode)\n", attr_pos);
                
                size_t end = tag_content.find("},", attr_pos);
                if (end == std::string::npos) {
                    // 也检查中文逗号
                    std::string chinese_comma = "\xef\xbc\x8c";  // UTF-8 的中文逗号 ，
                    end = tag_content.find(chinese_comma + "}", attr_pos);
                    if (end == std::string::npos) {
                        end = tag_content.find("}", attr_pos);
                    }
                }
                if (end != std::string::npos) {
                    std::string result = tag_content.substr(attr_pos, end - attr_pos + 1);
                    if (skillset_log) fprintf(skillset_log, "[extract_attribute] RETURNING brace value, length=%zu\n", result.length());
                    return result;
                }
            } else {
                // 简单的标识符（英文逗号、中文逗号或尾部结束）
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Simple identifier mode at position %zu, char='%c' (0x%02X)\n", attr_pos, tag_content[attr_pos], (unsigned char)tag_content[attr_pos]);
                if (skillset_log) fprintf(skillset_log, "[extract_attribute] Simple identifier mode at position %zu, char='%c' (0x%02X)\n", attr_pos, tag_content[attr_pos], (unsigned char)tag_content[attr_pos]);
                
                size_t end = attr_pos;
                while (end < tag_content.length()) {
                    char c = tag_content[end];
                    // 检查英文逗号或空白或尾部 '>'
                    if (c == ',' || c == ' ' || c == '>') {
                        if (skillset_log) fprintf(skillset_log, "[extract_attribute] Simple mode: stopped at position %zu, char='%c' (0x%02X)\n", end, c, (unsigned char)c);
                        break;
                    }
                    // 检查中文逗号（UTF-8）
                    if (end + 2 < tag_content.length() && 
                        (unsigned char)tag_content[end] == 0xef &&
                        (unsigned char)tag_content[end+1] == 0xbc &&
                        (unsigned char)tag_content[end+2] == 0x8c) {
                        if (skillset_log) fprintf(skillset_log, "[extract_attribute] Simple mode: stopped at Chinese comma\n");
                        break;
                    }
                    end++;
                }
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Simple value ends at %zu\n", end);
                if (skillset_log) fprintf(skillset_log, "[extract_attribute] Simple value: start=%zu, end=%zu, length=%zu\n", attr_pos, end, end - attr_pos);
                
                std::string result = tag_content.substr(attr_pos, end - attr_pos);
                fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] Returning simple value, length=%zu: '%s'\n", result.length(), result.c_str());
                if (skillset_log) {
                    fprintf(skillset_log, "[extract_attribute] RETURNING simple value\n");
                    fprintf(skillset_log, "[extract_attribute]   Length: %zu\n", result.length());
                    fprintf(skillset_log, "[extract_attribute]   Content: '%s'\n", result.c_str());
                    fflush(skillset_log);
                }
                return result;
            }
            
            fprintf(stderr, "[SKILLSET REGISTER] [extract_attribute] No match found, returning empty\n");
            if (skillset_log) fprintf(skillset_log, "[extract_attribute] No match found, returning empty\n");
            return "";
        };
        
        std::string skill_id = extract_attribute("id");
        if (skill_id.empty()) {
            // 诊断：记录提取失败
            FILE* diag = nullptr;
            fopen_s(&diag, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
            if (diag) {
                fprintf(diag, "[EXTRACT ERROR] skill_id extraction failed, returning early\n");
                fflush(diag);
                fclose(diag);
            }
            
            context->SetError("skilldef missing id attribute");
            fprintf(stderr, "[SKILLSET REGISTER] ERROR: skill_id is empty\n");
            if (skillset_log) {
                fprintf(skillset_log, "[ERROR] skill_id is empty!\n");
                fflush(skillset_log);
            }
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        // 移除可能的某些字符（如引号、空白）
        // 从 skill_id 的开头移除前导空白
        size_t start = skill_id.find_first_not_of(" \t\r\n\"'");
        size_t end = skill_id.find_last_not_of(" \t\r\n\"'");
        if (start != std::string::npos) {
            skill_id = skill_id.substr(start, end - start + 1);
        }
        
        fprintf(stderr, "[SKILLSET REGISTER] Extracted skill_id: '%s' (length=%zu)\n", skill_id.c_str(), skill_id.length());
        
        // 诊断：记录成功提取的 skill_id
        FILE* diag_id = nullptr;
        fopen_s(&diag_id, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
        if (diag_id) {
            fprintf(diag_id, "[SKILL_ID_EXTRACTED] Extracted skill_id: '%s' (length=%zu)\n", skill_id.c_str(), skill_id.length());
            fflush(diag_id);
            fclose(diag_id);
        }
        
        std::string def_value = extract_attribute("def");
        if (skillset_log) fprintf(skillset_log, "[AFTER_EXTRACT] def_value.length() = %zu\n", def_value.length());
        if (skillset_log) fprintf(skillset_log, "[AFTER_EXTRACT] def_value first 50 chars: '%.50s'\n", def_value.c_str());
        if (skillset_log) fflush(skillset_log);
        
        if (def_value.empty()) {
            // 诊断：记录 def 提取失败
            FILE* diag_def = nullptr;
            fopen_s(&diag_def, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
            if (diag_def) {
                fprintf(diag_def, "[DEF_EXTRACT ERROR] def_value is empty for skill '%s', returning early\n", skill_id.c_str());
                fflush(diag_def);
                fclose(diag_def);
            }
            
            context->SetError("skilldef missing def attribute");
            fprintf(stderr, "[SKILLSET REGISTER] ERROR: def_value is empty\n");
            if (skillset_log) {
                fprintf(skillset_log, "[ERROR] def_value is empty!\n");
                fflush(skillset_log);
            }
            return ABOT_ERROR_PARSE_ERROR;
        }
        if (skillset_log) fprintf(skillset_log, "[DEF_VALUE_EXTRACTED] Length: %zu\n", def_value.length());
        if (skillset_log) fprintf(skillset_log, "[DEF_VALUE_EXTRACTED] Content first 100: ");
        if (skillset_log) {
            int writeLen = (def_value.length() > 100) ? 100 : def_value.length();
            fwrite(def_value.c_str(), 1, writeLen, skillset_log);
            fprintf(skillset_log, "\n");
        }
        
        // === 修复：移除前导空格 ===
        size_t first_non_space = def_value.find_first_not_of(" \t\r\n");
        if (first_non_space != std::string::npos && first_non_space > 0) {
            if (skillset_log) fprintf(skillset_log, "[CLEANUP_LEADING] Removing %zu leading spaces\n", first_non_space);
            def_value = def_value.substr(first_non_space);
            if (skillset_log) fprintf(skillset_log, "[CLEANUP_LEADING] After cleanup, length=%zu\n", def_value.length());
        }
        
        // === 关键修复：净化 def_value，移除可能的多余字符（如卡片结尾的 '>') ===
        // 如果 def_value 以 'expr(' 开头但末尾有多余字符，需要修正
        size_t last_paren = def_value.rfind(')');
        if (last_paren != std::string::npos && last_paren < def_value.length() - 1) {
            // 有多余字符在最后的 ')' 之后
            if (skillset_log) {
                fprintf(skillset_log, "[CLEANUP_TAIL] WARNING: extra characters after ')'\n");
                fprintf(skillset_log, "[CLEANUP_TAIL]   Original length: %zu\n", def_value.length());
                fprintf(skillset_log, "[CLEANUP_TAIL]   Last ')' at position: %zu\n", last_paren);
            }
            
            // 截断到最后的 ')'
            def_value = def_value.substr(0, last_paren + 1);
            if (skillset_log) fprintf(skillset_log, "[CLEANUP_TAIL]   After cleanup, length=%zu\n", def_value.length());
        }
        
        if (skillset_log) fprintf(skillset_log, "[BEFORE_EXPR_CHECK] def_value.length()=%zu, first 5 chars: '%.5s'\n", def_value.length(), def_value.c_str());
        if (skillset_log) fflush(skillset_log);
        
        // 【第1步】从 expr(...) 中提取内部内容
        // 这是必要的，因为 extract_attribute 返回的是完整的 expr(...) 字符串
        std::string expr_content = def_value;
        if (def_value.find("expr(") == 0) {  // 以 expr( 开头
            size_t start = 5; // "expr(" 的长度
            size_t end = def_value.rfind(')');  // 找最后一个 )
            if (end != std::string::npos && end > start) {
                expr_content = def_value.substr(start, end - start);
                if (skillset_log) {
                    fprintf(skillset_log, "[EXPR_EXTRACT] Extracted from expr(...), length=%zu, first 30 chars: '%.30s'\n", 
                            expr_content.length(), expr_content.c_str());
                    fflush(skillset_log);
                }
            }
        }
        
        // 【第2步】使用统一的脚本处理函数
        // 这与ANKE预设的脚本处理使用相同的逻辑，确保整个系统的一致性
        std::string expression = abot::PrepareScriptForCompilation(expr_content, skill_id);
        
        if (skillset_log) fprintf(skillset_log, "[FINAL_EXPRESSION] Final expression length: %zu\n", expression.length());
        int maxLen = 100;
        if (maxLen > expression.length()) maxLen = expression.length();
        fprintf(stderr, "[SKILLSET REGISTER] Final expression (first 100 chars): ");
        fwrite(expression.c_str(), 1, maxLen, stderr);
        fprintf(stderr, "\n");
        
        // 诊断：输出完整的expression（最多前1000个字符）
        fprintf(stderr, "[SKILLSET REGISTER] === FULL EXPRESSION TEXT (first 1000 chars) ===\n");
        int fullLen = 1000;
        if (fullLen > expression.length()) fullLen = expression.length();
        fwrite(expression.c_str(), 1, fullLen, stderr);
        fprintf(stderr, "\n");
        if (expression.length() > 1000) {
            fprintf(stderr, "[SKILLSET REGISTER] ... (expression continues, total length %zu)\n", expression.length());
        }
        fprintf(stderr, "[SKILLSET REGISTER] === END EXPRESSION ===\n");
        
        // 分析expression的字符及所有内容
        fprintf(stderr, "[SKILLSET REGISTER] ========== EXPRESSION ANALYSIS ==========\n");
        fprintf(stderr, "[SKILLSET REGISTER] Total length: %zu\n", expression.length());
        fprintf(stderr, "[SKILLSET REGISTER] First char: '%c' (0x%02X)\n", 
            expression.empty() ? '?' : expression[0],
            expression.empty() ? 0 : (unsigned char)expression[0]);
        fprintf(stderr, "[SKILLSET REGISTER] Last char: '%c' (0x%02X)\n",
            expression.empty() ? '?' : expression[expression.length()-1],
            expression.empty() ? 0 : (unsigned char)expression[expression.length()-1]);
        
        // 输出完整expression用于日志检查
        fprintf(stderr, "[SKILLSET DEBUG] Expression to compile:\n");
        fprintf(stderr, "[SKILLSET DEBUG]   Length: %zu\n", expression.length());
        if (expression.length() > 0) {
            fprintf(stderr, "[SKILLSET DEBUG]   Content: %s\n", expression.c_str());
        }
        fflush(stderr);
        
        // 写入诊断文件
        if (skillset_log) {
            fprintf(skillset_log, "[SKILLSET DEBUG] Expression to compile:\n");
            fprintf(skillset_log, "[SKILLSET DEBUG]   Skill ID: '%s'\n", skill_id.c_str());
            fprintf(skillset_log, "[SKILLSET DEBUG]   Length: %zu\n", expression.length());
            fprintf(skillset_log, "[SKILLSET DEBUG]   Content: '%s'\n", expression.c_str());
            fflush(skillset_log);
        }
        
        // 编译表达式为字节码
        if (skillset_log) {
            fprintf(skillset_log, "\n[=== COMPILATION PHASE START ===]\n");
            fflush(skillset_log);
        }
        
        // 诊断：记录编译即将开始
        FILE* diag_compile = nullptr;
        fopen_s(&diag_compile, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
        if (diag_compile) {
            fprintf(diag_compile, "[COMPILE_START] About to compile expression for skill '%s', expression length: %zu\n", skill_id.c_str(), expression.length());
            fflush(diag_compile);
            fclose(diag_compile);
        }
        
        abot::Lexer lexer(expression);
        auto tokens = lexer.ScanTokens();
        if (lexer.HasError()) {
            std::string lexerError = lexer.GetErrorMessage();
            if (lexerError.empty()) {
                lexerError = "Unknown lexer error";
            }
            
            // 诊断：记录 Lexer 错误
            FILE* diag_lexer = nullptr;
            fopen_s(&diag_lexer, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
            if (diag_lexer) {
                fprintf(diag_lexer, "[COMPILE_ERROR] LEXER ERROR for skill '%s'\n", skill_id.c_str());
                fprintf(diag_lexer, "[COMPILE_ERROR] Error message: %s\n", lexerError.c_str());
                fprintf(diag_lexer, "[COMPILE_ERROR] Expression length: %zu\n", expression.length());
                fprintf(diag_lexer, "[COMPILE_ERROR] Expression (first 100 chars): %.100s\n", expression.c_str());
                fprintf(diag_lexer, "[COMPILE_ERROR] ===== RETURNING ABOT_ERROR_PARSE_ERROR =====\n");
                fflush(diag_lexer);
                fclose(diag_lexer);
            }
            
            // 构建详细的错误信息
            std::string fullError = "Lexer error: " + lexerError;
            fullError += "\n  Expression length: " + std::to_string(expression.length());
            int truncLen = 100;
            if (truncLen > expression.length()) truncLen = expression.length();
            fullError += "\n  Expression: " + expression.substr(0, truncLen);
            
            context->SetError(fullError);
            fprintf(stderr, "[SKILLSET REGISTER] ========== LEXER ERROR ==========\n");
            fprintf(stderr, "[SKILLSET REGISTER] Expression: ");
            fwrite(expression.c_str(), 1, expression.length(), stderr);
            fprintf(stderr, "\n");
            fprintf(stderr, "[SKILLSET REGISTER] Expression length: %zu\n", expression.length());
            fprintf(stderr, "[SKILLSET REGISTER] Lexer Error: %s\n", lexerError.c_str());
            fprintf(stderr, "[SKILLSET REGISTER] ===================================\n");
            return ABOT_ERROR_PARSE_ERROR;
        }
        fprintf(stderr, "[SKILLSET REGISTER] Lexer success, tokens: %zu\n", tokens.size());
        
        if (skillset_log) {
            fprintf(skillset_log, "[COMPILE] Lexer successful, tokens: %zu\n", tokens.size());
            fflush(skillset_log);
        }
        
        // 诊断：输出token细节（全部tokens，不限制）
        fprintf(stderr, "[SKILLSET REGISTER] ========== ALL SCANNED TOKENS ==========\n");
        for (size_t i = 0; i < tokens.size(); i++) {
            fprintf(stderr, "[SKILLSET REGISTER] Token[%zu]: TokenType=%d", i, (int)tokens[i].type);
            if (!tokens[i].lexeme.empty()) {
                fprintf(stderr, " lexeme='%s'", tokens[i].lexeme.c_str());
            }
            fprintf(stderr, "\n");
        }
        fprintf(stderr, "[SKILLSET REGISTER] ========== END ALL TOKENS ==========\n");
        
        if (skillset_log) {
            fprintf(skillset_log, "[DEBUG_TOKENS] Total tokens: %zu\n", tokens.size());
            for (size_t i = 0; i < tokens.size() && i < 10; i++) {
                fprintf(skillset_log, "[DEBUG_TOKENS] Token[%zu]: type=%d lexeme='%s'\n", i, (int)tokens[i].type, tokens[i].lexeme.c_str());
            }
            if (tokens.size() > 20) {
                fprintf(skillset_log, "[DEBUG_TOKENS] ... (showing first 10 of %zu tokens)\n", tokens.size());
                for (size_t i = tokens.size() - 5; i < tokens.size(); i++) {
                    fprintf(skillset_log, "[DEBUG_TOKENS] Token[%zu]: type=%d lexeme='%s'\n", i, (int)tokens[i].type, tokens[i].lexeme.c_str());
                }
            }
            fflush(skillset_log);
        }
        
        if (skillset_log) {
            fprintf(skillset_log, "[STAGE_PARSER_INIT] About to create Parser...\n");
            fflush(skillset_log);
        }
        
        abot::Parser parser(tokens);
        
        if (skillset_log) {
            fprintf(skillset_log, "[STAGE_PARSER_INIT] Parser created successfully\n");
            fflush(skillset_log);
        }
        
        auto statements = parser.ParseProgram();
        
        if (skillset_log) {
            fprintf(skillset_log, "[STAGE_PARSER_CALL] ParseProgram() completed, statements: %zu\n", statements.size());
            fflush(skillset_log);
        }
        if (parser.HasError()) {
            std::string parserError = parser.GetErrorMessage();
            if (parserError.empty()) {
                parserError = "Unknown parser error";
            }
            
            std::string fullError = "Parser error: " + parserError + 
                "\n  Tokens parsed: " + std::to_string(tokens.size()) +
                "\n  Statements created: " + std::to_string(statements.size());
            
            context->SetError(fullError);
            fprintf(stderr, "[SKILLSET REGISTER] Parser error: %s\n", parserError.c_str());
            return ABOT_ERROR_PARSE_ERROR;
        }
        fprintf(stderr, "[SKILLSET REGISTER] Parser success, statements: %zu\n", statements.size());
        
        if (skillset_log) {
            fprintf(skillset_log, "[COMPILE] Parser successful, statements: %zu\n", statements.size());
            fflush(skillset_log);
        }
        
        // 诊断：输出所有parsed statements的类型
        fprintf(stderr, "[SKILLSET REGISTER] ========== PARSED STATEMENTS TYPES ==========\n");
        for (size_t i = 0; i < statements.size(); i++) {
            const auto& stmt = statements[i];
            if (auto assign = dynamic_cast<const abot::AssignmentStatement*>(stmt.get())) {
                fprintf(stderr, "[SKILLSET REGISTER] [%zu] AssignmentStatement: op='%s'\n", i, assign->op.c_str());
                if (auto member = dynamic_cast<const abot::MemberAccess*>(assign->target.get())) {
                    fprintf(stderr, "[SKILLSET REGISTER]      target: MemberAccess(obj=?, member='%s')\n", member->member.c_str());
                } else if (auto var = dynamic_cast<const abot::Variable*>(assign->target.get())) {
                    fprintf(stderr, "[SKILLSET REGISTER]      target: Variable('%s')\n", var->name.c_str());
                }
            } else if (auto ifstmt = dynamic_cast<const abot::IfStatement*>(stmt.get())) {
                fprintf(stderr, "[SKILLSET REGISTER] [%zu] IfStatement\n", i);
            } else if (auto iffor = dynamic_cast<const abot::ForStatement*>(stmt.get())) {
                fprintf(stderr, "[SKILLSET REGISTER] [%zu] ForStatement\n", i);
            } else if (auto decl = dynamic_cast<const abot::DeclarationStatement*>(stmt.get())) {
                fprintf(stderr, "[SKILLSET REGISTER] [%zu] DeclarationStatement\n", i);
            } else if (auto expr = dynamic_cast<const abot::ExpressionStatement*>(stmt.get())) {
                fprintf(stderr, "[SKILLSET REGISTER] [%zu] ExpressionStatement\n", i);
            } else {
                fprintf(stderr, "[SKILLSET REGISTER] [%zu] Unknown statement type\n", i);
            }
        }
        fprintf(stderr, "[SKILLSET REGISTER] ========== END PARSED STATEMENTS ==========\n");
        
        fprintf(stderr, "\n🔴🔴🔴 [SKILLSET REGISTER] ========== COMPILING SKILL: '%s' ==========\n", skill_id.c_str());
        fprintf(stderr, "[SKILLSET REGISTER] Statements to compile: %zu\n", statements.size());
        
        if (skillset_log) {
            fprintf(skillset_log, "[STAGE_COMPILER_INIT] About to create BytecodeCompiler...\n");
            fflush(skillset_log);
        }
        
        abot::BytecodeCompiler compiler;
        
        if (skillset_log) {
            fprintf(skillset_log, "[STAGE_COMPILER_INIT] BytecodeCompiler created successfully\n");
            fflush(skillset_log);
        }
        
        auto bytecode = compiler.Compile(statements);
        
        if (skillset_log) {
            fprintf(skillset_log, "[STAGE_COMPILER_COMPILE] Compile() call completed, bytecode=%p\n", bytecode.get());
            fflush(skillset_log);
        }
        
        fprintf(stderr, "[SKILLSET REGISTER] ========== DONE COMPILING SKILL: '%s' ==========🔴🔴🔴\n", skill_id.c_str());
        
        if (skillset_log) {
            fprintf(skillset_log, "[COMPILE] Bytecode compilation completed\n");
            fflush(skillset_log);
        }
        
        // 输出编译后的bytecode指令详情
        if (bytecode) {
            fprintf(stderr, "[SKILLSET REGISTER] ========== BYTECODE INSTRUCTIONS ==========\n");
            fprintf(stderr, "[SKILLSET REGISTER] Total instructions: %zu\n", bytecode->instructions.size());
            for (size_t i = 0; i < bytecode->instructions.size(); i++) {
                const auto& instr = bytecode->instructions[i];
                fprintf(stderr, "[SKILLSET REGISTER] [%zu] opcode=%d", i, (int)instr.opcode);
                
                // 根据opcode类型输出操作数
                if (!instr.arg_string.empty()) {
                    fprintf(stderr, " arg_string='%s'", instr.arg_string.c_str());
                }
                if (instr.arg_int != 0) {
                    fprintf(stderr, " arg_int=%lld", instr.arg_int);
                }
                if (instr.arg_double != 0.0) {
                    fprintf(stderr, " arg_double=%f", instr.arg_double);
                }
                fprintf(stderr, "\n");
            }
            fprintf(stderr, "[SKILLSET REGISTER] ========== END BYTECODE ==========\n\n");
        } else {
            fprintf(stderr, "[SKILLSET REGISTER] ERROR: bytecode is nullptr!\n\n");
        }
        
        if (compiler.HasError()) {
            std::string compilerError = compiler.GetErrorMessage();
            if (compilerError.empty()) {
                compilerError = "Unknown compiler error";
            }
            
            // 诊断：记录编译错误
            FILE* diag_compiler = nullptr;
            fopen_s(&diag_compiler, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
            if (diag_compiler) {
                fprintf(diag_compiler, "[COMPILE_ERROR] COMPILER ERROR for skill '%s'\n", skill_id.c_str());
                fprintf(diag_compiler, "[COMPILE_ERROR] Error message: %s\n", compilerError.c_str());
                fprintf(diag_compiler, "[COMPILE_ERROR] Statements compiled: %zu\n", statements.size());
                fprintf(diag_compiler, "[COMPILE_ERROR] ===== RETURNING ABOT_ERROR_PARSE_ERROR =====\n");
                fflush(diag_compiler);
                fclose(diag_compiler);
            }
            
            std::string fullError = "Compiler error: " + compilerError + 
                "\n  Statements compiled: " + std::to_string(statements.size());
            
            context->SetError(fullError);
            fprintf(stderr, "[SKILLSET REGISTER] Compiler error: %s\n", compilerError.c_str());
            return ABOT_ERROR_PARSE_ERROR;
        }
        fprintf(stderr, "[SKILLSET REGISTER] Compiler success, bytecode created\n");
        
        // 诊断：记录编译成功
        FILE* diag_success = nullptr;
        fopen_s(&diag_success, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
        if (diag_success) {
            fprintf(diag_success, "[COMPILE_SUCCESS] Bytecode created for '%s'\n", skill_id.c_str());
            fflush(diag_success);
            fclose(diag_success);
        }
        
        // 创建 SkillDefinition 并注册
        abot::SkillDefinition skill_def;
        skill_def.id = skill_id;
        skill_def.type = ""; // 类型由角色卡中的 skill 元素指定
        skill_def.def = std::move(bytecode);
        skill_def.original_expression = expression;  // 保存原始表达式用于诊断
        
        // 诊断：即将获取 PresetRegistry
        FILE* diag_before_reg = nullptr;
        fopen_s(&diag_before_reg, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
        if (diag_before_reg) {
            fprintf(diag_before_reg, "[BEFORE_REGISTRY] About to call PresetRegistry::GetInstance() for skill '%s'\n", skill_id.c_str());
            fflush(diag_before_reg);
            fclose(diag_before_reg);
        }
        
        if (skillset_log) {
            fprintf(skillset_log, "[REG_STEP] Getting PresetRegistry instance...\n");
            fflush(skillset_log);
        }
        
        auto registry = abot::PresetRegistry::GetInstance();
        if (!registry) {
            context->SetError("PresetRegistry not available");
            fprintf(stderr, "[SKILLSET REGISTER] ERROR: PresetRegistry not available\n");
            if (skillset_log) {
                fprintf(skillset_log, "[REG_ERROR] PresetRegistry::GetInstance() returned nullptr!\n");
                fflush(skillset_log);
            }
            return ABOT_ERROR_UNKNOWN;
        }
        
        // 诊断：直接写入诊断文件（避免缓冲问题）
        FILE* diag_file = nullptr;
        fopen_s(&diag_file, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
        if (diag_file) {
            fprintf(diag_file, "[REGISTRATION TIME] ========== REGISTRATION BEGINS ==========\n");
            fprintf(diag_file, "[REGISTRATION TIME] Skill ID: %s\n", skill_id.c_str());
            fprintf(diag_file, "[REGISTRATION TIME] PresetRegistry::GetInstance() returned address: %p\n", (void*)registry);
            fprintf(diag_file, "[REGISTRATION TIME] Registry pointer is %s\n", registry ? "NOT NULL" : "NULL!!!");
            fprintf(diag_file, "[REGISTRATION TIME] About to call RegisterSkill()...\n");
            fflush(diag_file);
            fclose(diag_file);
        }
        
        registry->RegisterSkill(std::move(skill_def), false);
        
        // 记录注册后的状态
        diag_file = nullptr;
        fopen_s(&diag_file, "C:\\Windows\\Temp\\abot_registry_diagnostic.txt", "at");
        if (diag_file) {
            fprintf(diag_file, "[REGISTRATION TIME] RegisterSkill() returned for skill '%s'\n", skill_id.c_str());
            fprintf(diag_file, "[REGISTRATION TIME] Verifying with GetSkill()...\n");
            fflush(diag_file);
        }
        
        // 验证注册是否成功
        abot::SkillPreset* verify = registry->GetSkill(skill_id);
        if (verify) {
            fprintf(stderr, "[SKILLSET REGISTER] VERIFIED: Skill '%s' is now in registry\n", skill_id.c_str());
            if (skillset_log) {
                fprintf(skillset_log, "[REG_VERIFY] Skill '%s' verified in registry\n", skill_id.c_str());
                fflush(skillset_log);
            }
        } else {
            fprintf(stderr, "[SKILLSET REGISTER] WARNING: Skill '%s' was registered but GetSkill() returned nullptr!\n", skill_id.c_str());
            if (skillset_log) {
                fprintf(skillset_log, "[REG_VERIFY_FAIL] Skill '%s' registered but GetSkill() returned nullptr!\n", skill_id.c_str());
                fflush(skillset_log);
            }
        }
        
        // 列出所有已注册的技能
        auto all_skills = registry->ListPresets(abot::PresetType::SKILL);
        fprintf(stderr, "[SKILLSET REGISTER] Total skills in registry: %zu\n", all_skills.size());
        if (skillset_log) {
            fprintf(skillset_log, "[REG_SUMMARY] Total skills in registry: %zu\n", all_skills.size());
        }
        for (const auto& skill_name : all_skills) {
            fprintf(stderr, "[SKILLSET REGISTER]   - Skill: '%s'\n", skill_name.c_str());
            if (skillset_log) {
                fprintf(skillset_log, "[REG_LIST]   - Skill: '%s'\n", skill_name.c_str());
            }
        }
        if (skillset_log) {
            fflush(skillset_log);
        }
        
        if (skillset_log) {
            fprintf(skillset_log, "[=== COMPILATION PHASE END - SUCCESS ===]\n\n");
            fflush(skillset_log);
        }
        
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Skillset error: ") + e.what());
        fprintf(stderr, "[SKILLSET REGISTER] EXCEPTION: %s\n", e.what());
        if (skillset_log) {
            fprintf(skillset_log, "[EXCEPTION] %s\n", e.what());
            fflush(skillset_log);
        }
        return ABOT_ERROR_PARSE_ERROR;
    } catch (...) {
        fprintf(stderr, "[SKILLSET REGISTER] UNKNOWN EXCEPTION\n");
        if (skillset_log) {
            fprintf(skillset_log, "[EXCEPTION] UNKNOWN EXCEPTION\n");
            fflush(skillset_log);
        }
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_register_stateset(ABOT_HANDLE handle, const char* stateset_xml) {
    if (!handle || !stateset_xml) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        // TODO: 实现状态集注册
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Stateset error: ") + e.what());
        return ABOT_ERROR_PARSE_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_register_ankeset(ABOT_HANDLE handle, const char* ankeset_xml) {
    if (!handle || !ankeset_xml) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        // 解析ANKE格式：
        // <anke name=预设名称, unit=[{e=选项1, w=权重1, p=expr(&脚本1)}, ...]>
        std::string xml_str(ankeset_xml);
        
        // 第1步：提取预设名称
        size_t name_start = xml_str.find("name=");
        if (name_start == std::string::npos) {
            context->SetError("Ankeset format error: missing 'name=' attribute");
            return ABOT_ERROR_PARSE_ERROR;
        }
        name_start += 5; // 跳过 "name="
        
        // 查找名称的结束位置（逗号或空格）
        size_t name_end = xml_str.find_first_of(", ", name_start);
        if (name_end == std::string::npos) {
            context->SetError("Ankeset format error: invalid name format");
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        std::string preset_name = xml_str.substr(name_start, name_end - name_start);
        // 移除名称两端的引号（如果有）
        if (!preset_name.empty() && preset_name.front() == '"') {
            preset_name = preset_name.substr(1);
        }
        if (!preset_name.empty() && preset_name.back() == '"') {
            preset_name.pop_back();
        }
        
        // 第2步：提取unit数组
        size_t unit_start = xml_str.find("unit=");
        if (unit_start == std::string::npos) {
            context->SetError("Ankeset format error: missing 'unit=' attribute");
            return ABOT_ERROR_PARSE_ERROR;
        }
        unit_start = xml_str.find('[', unit_start);
        size_t unit_end = xml_str.rfind(']');
        if (unit_start == std::string::npos || unit_end == std::string::npos) {
            context->SetError("Ankeset format error: invalid unit array format");
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        std::string unit_str = xml_str.substr(unit_start + 1, unit_end - unit_start - 1);
        
        // 第3步：创建ANKE预设对象
        auto anke_preset = std::make_unique<abot::AnkePreset>(preset_name);
        
        // 第4步：第一遍扫描 - 提取所有选项到临时结构
        // 结构：{type, name, weight, script_string}
        struct TempOption {
            std::string type;        // "e", "es", "ef"
            std::string name;
            int weight;
            std::string script;
        };
        std::vector<TempOption> temp_options;
        
        size_t pos = 0;
        while (pos < unit_str.length()) {
            size_t opt_start = unit_str.find('{', pos);
            if (opt_start == std::string::npos) break;
            
            size_t opt_end = unit_str.find('}', opt_start);
            if (opt_end == std::string::npos) {
                context->SetError("Ankeset format error: unclosed { in option");
                return ABOT_ERROR_PARSE_ERROR;
            }
            
            std::string option_str = unit_str.substr(opt_start + 1, opt_end - opt_start - 1);
            pos = opt_end + 1;
            
            // 判断选项类型
            std::string opt_type;
            std::string option_name;
            int option_weight = 0;
            std::string script_str;
            
            size_t e_pos = option_str.find("e=");
            size_t es_pos = option_str.find("es=");
            size_t ef_pos = option_str.find("ef=");
            
            // 优先级：es/ef > e
            if (es_pos != std::string::npos && (e_pos == std::string::npos || es_pos < e_pos)) {
                opt_type = "es";
                es_pos += 3;
                size_t name_comma = option_str.find(',', es_pos);
                option_name = option_str.substr(es_pos, (name_comma != std::string::npos ? name_comma - es_pos : 0));
                option_weight = 1;
            } else if (ef_pos != std::string::npos && (e_pos == std::string::npos || ef_pos < e_pos)) {
                opt_type = "ef";
                ef_pos += 3;
                size_t name_comma = option_str.find(',', ef_pos);
                option_name = option_str.substr(ef_pos, (name_comma != std::string::npos ? name_comma - ef_pos : 0));
                option_weight = 1;
            } else if (e_pos != std::string::npos) {
                opt_type = "e";
                e_pos += 2;
                size_t name_comma = option_str.find(',', e_pos);
                option_name = option_str.substr(e_pos, (name_comma != std::string::npos ? name_comma - e_pos : 0));
                
                // 提取权重
                size_t w_pos = option_str.find("w=", name_comma);
                if (w_pos != std::string::npos) {
                    w_pos += 2;
                    size_t w_comma = option_str.find(',', w_pos);
                    std::string weight_str = option_str.substr(w_pos,
                        (w_comma != std::string::npos ? w_comma - w_pos : option_str.find('}', w_pos) - w_pos));
                    option_weight = std::atoi(weight_str.c_str());
                }
            } else {
                context->SetError("Ankeset format error: invalid option format (missing e=/es=/ef=)");
                return ABOT_ERROR_PARSE_ERROR;
            }
            
            // 清除名称两端空格和引号
            while (!option_name.empty() && (option_name.front() == ' ' || option_name.front() == '"')) {
                option_name = option_name.substr(1);
            }
            while (!option_name.empty() && (option_name.back() == ' ' || option_name.back() == '"')) {
                option_name.pop_back();
            }
            
            // 提取脚本
            size_t p_pos = option_str.find("p=expr(&");
            if (p_pos == std::string::npos) {
                p_pos = option_str.find("p=expr(");
                if (p_pos == std::string::npos) {
                    context->SetError("Ankeset format error: missing p=expr in option");
                    return ABOT_ERROR_PARSE_ERROR;
                }
                p_pos += 7;
            } else {
                p_pos += 8;
            }
            
            size_t script_end = option_str.find(')', p_pos);
            if (script_end == std::string::npos) {
                context->SetError("Ankeset format error: unclosed expr(");
                return ABOT_ERROR_PARSE_ERROR;
            }
            
            script_str = option_str.substr(p_pos, script_end - p_pos);
            while (!script_str.empty() && (script_str.front() == ' ' || script_str.front() == '&')) {
                script_str = script_str.substr(1);
            }
            while (!script_str.empty() && script_str.back() == ' ') {
                script_str.pop_back();
            }
            
            temp_options.push_back({opt_type, option_name, option_weight, script_str});
        }
        
        // 第5步：第二遍扫描 - 处理大成功/大失败配对
        std::vector<bool> processed(temp_options.size(), false);
        
        for (size_t i = 0; i < temp_options.size(); i++) {
            if (processed[i]) continue;
            
            auto& opt = temp_options[i];
            
            // 检查是否为es选项
            if (opt.type == "es") {
                // 查找对应的ef选项
                bool found_ef = false;
                for (size_t j = i + 1; j < temp_options.size(); j++) {
                    if (temp_options[j].type == "ef") {
                        // 编译es脚本
                        abot::Lexer lexer_es(opt.script);
                        auto tokens_es = lexer_es.ScanTokens();
                        if (lexer_es.HasError()) {
                            context->SetError(std::string("Ankeset 大成功脚本 lexer error: ") + lexer_es.GetErrorMessage());
                            return ABOT_ERROR_PARSE_ERROR;
                        }
                        abot::Parser parser_es(tokens_es);
                        auto statements_es = parser_es.ParseProgram();
                        if (parser_es.HasError()) {
                            context->SetError(std::string("Ankeset 大成功脚本 parser error: ") + parser_es.GetErrorMessage());
                            return ABOT_ERROR_PARSE_ERROR;
                        }
                        abot::BytecodeCompiler compiler_es;
                        auto bytecode_es = compiler_es.Compile(statements_es);
                        if (!bytecode_es) {
                            context->SetError("Ankeset 大成功脚本编译失败");
                            return ABOT_ERROR_PARSE_ERROR;
                        }
                        
                        // 编译ef脚本
                        abot::Lexer lexer_ef(temp_options[j].script);
                        auto tokens_ef = lexer_ef.ScanTokens();
                        if (lexer_ef.HasError()) {
                            context->SetError(std::string("Ankeset 大失败脚本 lexer error: ") + lexer_ef.GetErrorMessage());
                            return ABOT_ERROR_PARSE_ERROR;
                        }
                        abot::Parser parser_ef(tokens_ef);
                        auto statements_ef = parser_ef.ParseProgram();
                        if (parser_ef.HasError()) {
                            context->SetError(std::string("Ankeset 大失败脚本 parser error: ") + parser_ef.GetErrorMessage());
                            return ABOT_ERROR_PARSE_ERROR;
                        }
                        abot::BytecodeCompiler compiler_ef;
                        auto bytecode_ef = compiler_ef.Compile(statements_ef);
                        if (!bytecode_ef) {
                            context->SetError("Ankeset 大失败脚本编译失败");
                            return ABOT_ERROR_PARSE_ERROR;
                        }
                        
                        // 创建配对选项（"critical" 提示该选项是es/ef配对）
                        abot::AnkeOption critical_option("critical", 1, std::move(bytecode_es), std::move(bytecode_ef));
                        anke_preset->AddOption(std::move(critical_option));
                        
                        processed[i] = true;
                        processed[j] = true;
                        found_ef = true;
                        break;
                    }
                }
                
                if (!found_ef) {
                    context->SetError("Ankeset format error: 大成功(es=)没有对应的大失败(ef=)");
                    return ABOT_ERROR_PARSE_ERROR;
                }
            } else if (opt.type == "e") {
                // 普通选项 - 单独编译
                abot::Lexer lexer(opt.script);
                auto tokens = lexer.ScanTokens();
                if (lexer.HasError()) {
                    context->SetError(std::string("Ankeset script lexer error: ") + lexer.GetErrorMessage());
                    return ABOT_ERROR_PARSE_ERROR;
                }
                
                abot::Parser parser(tokens);
                auto statements = parser.ParseProgram();
                if (parser.HasError()) {
                    context->SetError(std::string("Ankeset script parser error: ") + parser.GetErrorMessage());
                    return ABOT_ERROR_PARSE_ERROR;
                }
                
                abot::BytecodeCompiler compiler;
                auto bytecode = compiler.Compile(statements);
                if (!bytecode) {
                    context->SetError("Ankeset script bytecode compilation failed");
                    return ABOT_ERROR_PARSE_ERROR;
                }
                
                abot::AnkeOption option(opt.name, opt.weight, std::move(bytecode));
                anke_preset->AddOption(std::move(option));
                processed[i] = true;
            }
        }
        
        // 第7步：注册预设
        anke_preset->SetBuiltin(false);  // 用户定义的预设
        abot::PresetRegistry* registry = abot::PresetRegistry::GetInstance();
        if (!registry) {
            context->SetError("PresetRegistry not available");
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        registry->RegisterAnke(preset_name, std::move(anke_preset));
        context->ClearError();
        return ABOT_OK;
        
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Ankeset error: ") + e.what());
        return ABOT_ERROR_PARSE_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

// ============ 战斗执行 ============

ABOT_API ABOT_ERROR abot_execute_battle(ABOT_HANDLE handle) {
    if (!handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->program) {
            context->SetError("Program not loaded");
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        // TODO: 执行战斗逻辑
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Execution error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_execute_script(ABOT_HANDLE handle, const char* script) {
    // 诊断日志
    FILE* log_file = nullptr;
    fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    
    if (!handle || !script) {
        if (log_file) {
            fprintf(log_file, "[abot_execute_script] ERROR: handle=%p, script=%p\n", handle, script);
            fclose(log_file);
        }
        return ABOT_ERROR_NULL_PTR;
    }
    
    if (log_file) {
        fprintf(log_file, "[abot_execute_script] Called with script length=%zu\n", strlen(script));
        
        // 记录前100个字节为十六进制
        fprintf(log_file, "[abot_execute_script] Hex dump (first 100 bytes):\n");
        for (int i = 0; i < 100 && script[i] != '\0'; i++) {
            fprintf(log_file, "%02X ", (unsigned char)script[i]);
            if ((i+1) % 16 == 0) fprintf(log_file, "\n");
        }
        fprintf(log_file, "\n");
        
        fprintf(log_file, "[abot_execute_script] Text (first 100 bytes): %.100s\n", script);
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        // 【诊断】执行前：从 ExecutionEnvironment 读取 turn.multiplier
        auto* env = abot::ExecutionEnvironment::Current();
        if (env) {
            abot::Value self_val = env->GetValueProperty("self");
            if (self_val.IsSchema() && self_val.HasField("turn")) {
                abot::Value turn_val = self_val.GetField("turn");
                if (turn_val.IsSchema() && turn_val.HasField("multiplier")) {
                    abot::Value mult_val = turn_val.GetField("multiplier");
                    double mult = -999.0;
                    if (mult_val.IsDouble()) {
                        mult = mult_val.GetDouble();
                    } else if (mult_val.IsInt()) {
                        mult = (double)mult_val.GetInt();
                    }
                    if (g_current_round_manager) {
                        g_current_round_manager->AppendSkillTriggerLog(
                            "[C_API_PRE] turn.multiplier = " + std::to_string(mult) + "\n");
                    }
                    if (log_file) fprintf(log_file, "[abot_execute_script] PRE-SCRIPT: turn.multiplier = %.6f\n", mult);
                }
            } else {
                if (g_current_round_manager) {
                    g_current_round_manager->AppendSkillTriggerLog(
                        "[C_API_PRE] self missing 'turn' or not schema\n");
                }
                if (log_file) fprintf(log_file, "[abot_execute_script] PRE-SCRIPT: self missing 'turn' or not schema\n");
            }
        }
        
        // 词法分析
        if (log_file) fprintf(log_file, "[abot_execute_script] Starting Lexer...\n");
        abot::Lexer lexer(script);
        auto tokens = lexer.ScanTokens();
        
        if (lexer.HasError()) {
            std::string error = "Lexer error: " + lexer.GetErrorMessage();
            context->SetError(error);
            if (log_file) {
                fprintf(log_file, "[abot_execute_script] Lexer failed: %s\n", error.c_str());
                fclose(log_file);
            }
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        if (log_file) fprintf(log_file, "[abot_execute_script] Lexer succeeded, %zu tokens. Starting Parser...\n", tokens.size());
        
        // 语法分析
        abot::Parser parser(tokens);
        auto statements = parser.ParseProgram();
        
        if (log_file) fprintf(log_file, "[abot_execute_script] Parser completed\n");
        
        if (parser.HasError()) {
            if (log_file) fprintf(log_file, "[abot_execute_script] Parser error: %s\n", parser.GetErrorMessage().c_str());
            context->SetError("Parser error: " + parser.GetErrorMessage());
            return ABOT_ERROR_PARSE_ERROR;
        }
        
        // 字节码编译
        if (log_file) fprintf(log_file, "[abot_execute_script] %zu statements to compile...\n", statements.size());
        abot::BytecodeCompiler compiler;
        auto program = compiler.Compile(statements);
        
        if (log_file) fprintf(log_file, "[abot_execute_script] Compilation completed\n");
        
        if (compiler.HasError()) {
            if (log_file) fprintf(log_file, "[abot_execute_script] Compiler error: %s\n", compiler.GetErrorMessage().c_str());
            context->SetError("Compiler error: " + compiler.GetErrorMessage());
            return ABOT_ERROR_COMPILE_ERROR;
        }
        
        // 调试：显示生成的字节码
        if (log_file && program) {
            fprintf(log_file, "[abot_execute_script] BYTECODE DUMP: %zu instructions generated\n", program->instructions.size());
            for (size_t i = 0; i < program->instructions.size(); i++) {
                const auto& instr = program->instructions[i];
                fprintf(log_file, "[abot_execute_script] Instr[%zu]: opcode=%d\n", i, (int)instr.opcode);
            }
        }
        
        context->program = std::move(program);
        
        // 执行
        if (log_file) fprintf(log_file, "[abot_execute_script] Starting VM execution...\n");
        if (log_file) fprintf(log_file, "[abot_execute_script] Program ptr=%p, program=%p\n", context->program.get(), context->program.get());
        if (log_file && context->program) {
            fprintf(log_file, "[abot_execute_script] VM about to execute %zu instructions\n", context->program->instructions.size());
        }
        if (!context->vm->Execute(context->program.get(), context->scope.get())) {
            if (log_file) fprintf(log_file, "[abot_execute_script] VM execution failed\n");
            context->SetError("VM error: " + context->vm->GetErrorMessage());
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        // 【诊断】执行后：从 ExecutionEnvironment 读取修改后的 turn.multiplier
        env = abot::ExecutionEnvironment::Current();
        if (env) {
            abot::Value self_val = env->GetValueProperty("self");
            if (self_val.IsSchema() && self_val.HasField("turn")) {
                abot::Value turn_val = self_val.GetField("turn");
                if (turn_val.IsSchema() && turn_val.HasField("multiplier")) {
                    abot::Value mult_val = turn_val.GetField("multiplier");
                    double mult = -999.0;
                    if (mult_val.IsDouble()) {
                        mult = mult_val.GetDouble();
                    } else if (mult_val.IsInt()) {
                        mult = (double)mult_val.GetInt();
                    }
                    if (g_current_round_manager) {
                        g_current_round_manager->AppendSkillTriggerLog(
                            "[C_API_POST] turn.multiplier = " + std::to_string(mult) + "\n");
                    }
                    if (log_file) fprintf(log_file, "[abot_execute_script] POST-SCRIPT: turn.multiplier = %.6f (from env self)\n", mult);
                }
            } else {
                if (g_current_round_manager) {
                    g_current_round_manager->AppendSkillTriggerLog(
                        "[C_API_POST] self missing 'turn' or not schema\n");
                }
                if (log_file) fprintf(log_file, "[abot_execute_script] POST-SCRIPT: self missing 'turn' or not schema\n");
            }
            
            // 也从诊断日志中获取详细信息
            std::string diag_log = env->GetDiagnosticLog();
            if (!diag_log.empty() && log_file) {
                fprintf(log_file, "[abot_execute_script] ===== VM DIAGNOSTIC LOG START =====\n");
                fprintf(log_file, "%s", diag_log.c_str());
                fprintf(log_file, "[abot_execute_script] ===== VM DIAGNOSTIC LOG END =====\n");
            }
        }
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Script error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

// ============ 错误处理 ============

ABOT_API const char* abot_get_last_error(ABOT_HANDLE handle) {
    if (!handle) {
        return "Invalid handle";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        return context->error_message.c_str();
    } catch (...) {
        return "Unknown error";
    }
}

ABOT_API void abot_clear_error(ABOT_HANDLE handle) {
    if (!handle) return;
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->ClearError();
    } catch (...) {
        // 忽略错误
    }
}

// ============ 角色调试信息 ============

ABOT_API const char* abot_get_character_debug_info(ABOT_HANDLE handle) {
    if (!handle) {
        return "Invalid handle";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->parsed_character) {
            return "No character parsed";
        }
        
        // 获取完整的调试信息
        static std::string debug_output;
        debug_output = context->parsed_character->GetCompleteDebug();
        return debug_output.c_str();
    } catch (const std::exception& e) {
        static std::string error_msg;
        error_msg = std::string("Error getting character debug info: ") + e.what();
        return error_msg.c_str();
    }
}

ABOT_API const char* abot_get_character_basic_info(ABOT_HANDLE handle) {
    if (!handle) {
        return "Invalid handle";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->parsed_character) {
            return "No character parsed";
        }
        
        static std::string debug_output;
        debug_output = context->parsed_character->GetBasicInfoDebug();
        return debug_output.c_str();
    } catch (const std::exception& e) {
        static std::string error_msg;
        error_msg = std::string("Error getting character basic info: ") + e.what();
        return error_msg.c_str();
    }
}

ABOT_API const char* abot_get_character_skills_info(ABOT_HANDLE handle) {
    if (!handle) {
        return "Invalid handle";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->parsed_character) {
            return "No character parsed";
        }
        
        static std::string debug_output;
        debug_output = context->parsed_character->GetSkillsDebug();
        return debug_output.c_str();
    } catch (const std::exception& e) {
        static std::string error_msg;
        error_msg = std::string("Error getting character skills info: ") + e.what();
        return error_msg.c_str();
    }
}

ABOT_API const char* abot_get_character_states_info(ABOT_HANDLE handle) {
    if (!handle) {
        return "Invalid handle";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->parsed_character) {
            return "No character parsed";
        }
        
        static std::string debug_output;
        debug_output = context->parsed_character->GetStatesDebug();
        return debug_output.c_str();
    } catch (const std::exception& e) {
        static std::string error_msg;
        error_msg = std::string("Error getting character states info: ") + e.what();
        return error_msg.c_str();
    }
}

// ============ 状态查询 ============

const char* abot_get_version(void) {
    return "0.1.0-alpha";
}

ABOT_API int abot_is_ready(ABOT_HANDLE handle) {
    // Open diagnostic log file
    FILE* log_file = nullptr;
    fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    if (log_file != nullptr) {
        fprintf(log_file, "[%lu] abot_is_ready() called with handle=%p\n", GetCurrentThreadId(), handle);
    }
    
    // 显式检查 handle 是否为 nullptr
    if (handle == nullptr) {
        if (log_file) {
            fprintf(log_file, "[%lu] RETURNING 0: handle is nullptr\n", GetCurrentThreadId());
            fclose(log_file);
        }
        return 0;
    }
    
    try {
        // 验证指针值是否可以访问
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (log_file) {
            fprintf(log_file, "[%lu] context pointer cast successful: %p\n", GetCurrentThreadId(), context);
        }
        
        // 验证 context 指针本身不为空
        if (context == nullptr) {
            if (log_file) {
                fprintf(log_file, "[%lu] RETURNING 0: context is nullptr after cast\n", GetCurrentThreadId());
                fclose(log_file);
            }
            return 0;
        }
        
        // 尝试访问 context 的成员以验证对象有效性
        // 检查 vm 是否初始化
        if (context->vm == nullptr) {
            if (log_file) {
                fprintf(log_file, "[%lu] RETURNING 0: context->vm is nullptr\n", GetCurrentThreadId());
                fprintf(log_file, "[%lu] context address: %p\n", GetCurrentThreadId(), (void*)context);
                fprintf(log_file, "[%lu] context->vm address: %p\n", GetCurrentThreadId(), (void*)(context->vm.get()));
                fclose(log_file);
            }
            return 0;
        }
        
        // 检查 scope 是否初始化
        if (context->scope == nullptr) {
            if (log_file) {
                fprintf(log_file, "[%lu] RETURNING 0: context->scope is nullptr\n", GetCurrentThreadId());
                fclose(log_file);
            }
            return 0;
        }
        
        // 所有检查都通过，上下文已准备就绪
        if (log_file) {
            fprintf(log_file, "[%lu] RETURNING 1: abot_is_ready() = true\n", GetCurrentThreadId());
            fclose(log_file);
        }
        return 1;
    } catch (const std::exception& e) {
        // 捕获并忽略异常
        if (log_file) {
            fprintf(log_file, "[%lu] RETURNING 0: Exception: %s\n", GetCurrentThreadId(), e.what());
            fclose(log_file);
        }
        return 0;
    } catch (...) {
        // 捕获所有其他异常
        if (log_file) {
            fprintf(log_file, "[%lu] RETURNING 0: Unknown exception\n", GetCurrentThreadId());
            fclose(log_file);
        }
        return 0;
    }
}

// ============ 参数解析 ============

ABOT_API ABOT_HANDLE abot_parse_parameter(ABOT_HANDLE handle, const char* parameter_xml) {
    if (!handle || !parameter_xml) {
        return nullptr;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (std::strlen(parameter_xml) == 0) {
            context->SetError("Empty parameter XML string");
            return nullptr;
        }
        
        auto param = abot::ParameterParser::Parse(parameter_xml);
        if (!param) {
            context->SetError("Failed to parse parameter: " + abot::ParameterParser::GetLastError());
            return nullptr;
        }
        
        uintptr_t handle_id = g_global_handle_manager.RegisterParameter(param);
        context->ClearError();
        return reinterpret_cast<ABOT_HANDLE>(handle_id);
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Parameter parse error: ") + e.what());
        return nullptr;
    } catch (...) {
        return nullptr;
    }
}

ABOT_API void abot_parameter_destroy(ABOT_HANDLE param_handle) {
    if (!param_handle) return;
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(param_handle);
        g_global_handle_manager.UnregisterParameter(id);
    } catch (...) {
        // 忽略错误
    }
}

ABOT_API const char* abot_parameter_get_name(ABOT_HANDLE param_handle) {
    if (!param_handle) {
        return "";
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(param_handle);
        auto param = g_global_handle_manager.GetParameter(id);
        return param ? param->name.c_str() : "";
    } catch (...) {
        return "";
    }
}

ABOT_API const char* abot_parameter_get_attribute(ABOT_HANDLE param_handle, const char* key) {
    if (!param_handle || !key) {
        return "";
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(param_handle);
        auto param = g_global_handle_manager.GetParameter(id);
        if (!param) return "";
        return param->GetAttribute(key).c_str();
    } catch (...) {
        return "";
    }
}

ABOT_API int abot_parameter_get_attribute_int(ABOT_HANDLE param_handle, const char* key) {
    if (!param_handle || !key) {
        return 0;
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(param_handle);
        auto param = g_global_handle_manager.GetParameter(id);
        if (!param) return 0;
        return param->GetAttributeInt(key);
    } catch (...) {
        return 0;
    }
}

// ============ 角色管理 ============

ABOT_API ABOT_HANDLE abot_character_create(ABOT_HANDLE handle, ABOT_HANDLE param_handle) {
    if (!handle || !param_handle) {
        return nullptr;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        uintptr_t param_id = reinterpret_cast<uintptr_t>(param_handle);
        
        auto param = g_global_handle_manager.GetParameter(param_id);
        if (!param) {
            context->SetError("Invalid parameter handle");
            return nullptr;
        }
        
        auto character = std::make_shared<abot::Character>();
        
        // 从参数单元中提取属性
        character->name = param->GetAttribute("name");
        character->camp = param->GetAttributeInt("camp");
        character->atk = param->GetAttributeInt("atk");
        character->max_hp = param->GetAttributeInt("hp");
        character->hp = character->max_hp;
        
        // 处理防甲（现已改为向量）
        int dfs_value = param->GetAttributeInt("dfs");
        if (dfs_value > 0) {
            character->defenses.push_back({dfs_value, ""});
        }
        
        character->aggro = param->GetAttributeInt("aggro");
        
        // 处理伤害减免（现已改为向量）
        float dr_value = param->GetAttributeFloat("dr");
        if (dr_value > 0.0f) {
            character->damage_reductions.push_back({dr_value, ""});
        }
        
        character->hp_restore = param->GetAttributeInt("hp_restore");
        
        // 解析伤害数组
        character->dmg[0] = param->GetAttributeInt("dmg_min");
        character->dmg[1] = param->GetAttributeInt("dmg_low");
        character->dmg[2] = param->GetAttributeInt("dmg_high");
        character->dmg[3] = param->GetAttributeInt("dmg_max");
        
        character->is_alive = true;
        
        // 诊断：记录 Character 初始化时的值
        if (g_current_round_manager) {
            char buf[512];
            snprintf(buf, sizeof(buf),
                "[CHAR_INIT] name=%s camp=%d atk=%d dmg=[%d,%d,%d,%d] hp=%d aggro=%d",
                character->name.c_str(),
                character->camp,
                character->atk,
                character->dmg[0], character->dmg[1], character->dmg[2], character->dmg[3],
                character->hp,
                character->aggro);
            g_current_round_manager->AppendSkillTriggerLog(buf);
        }
        
        uintptr_t char_id = g_global_handle_manager.RegisterCharacter(character);
        context->ClearError();
        return reinterpret_cast<ABOT_HANDLE>(char_id);
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Character create error: ") + e.what());
        return nullptr;
    } catch (...) {
        return nullptr;
    }
}

ABOT_API void abot_character_destroy(ABOT_HANDLE char_handle) {
    if (!char_handle) return;
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        g_global_handle_manager.UnregisterCharacter(id);
    } catch (...) {
        // 忽略错误
    }
}

ABOT_API const char* abot_character_get_name(ABOT_HANDLE char_handle) {
    if (!char_handle) {
        return "";
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        auto ch = g_global_handle_manager.GetCharacter(id);
        return ch ? ch->name.c_str() : "";
    } catch (...) {
        return "";
    }
}

ABOT_API int abot_character_get_camp(ABOT_HANDLE char_handle) {
    if (!char_handle) {
        return -1;
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        auto ch = g_global_handle_manager.GetCharacter(id);
        return ch ? ch->camp : -1;
    } catch (...) {
        return -1;
    }
}

ABOT_API int abot_character_get_hp(ABOT_HANDLE char_handle) {
    if (!char_handle) {
        return 0;
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        auto ch = g_global_handle_manager.GetCharacter(id);
        return ch ? ch->hp : 0;
    } catch (...) {
        return 0;
    }
}

ABOT_API int abot_character_get_max_hp(ABOT_HANDLE char_handle) {
    if (!char_handle) {
        return 0;
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        auto ch = g_global_handle_manager.GetCharacter(id);
        return ch ? ch->max_hp : 0;
    } catch (...) {
        return 0;
    }
}

ABOT_API int abot_character_get_atk(ABOT_HANDLE char_handle) {
    if (!char_handle) {
        return 0;
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        auto ch = g_global_handle_manager.GetCharacter(id);
        return ch ? ch->atk : 0;
    } catch (...) {
        return 0;
    }
}

ABOT_API ABOT_ERROR abot_character_take_damage(ABOT_HANDLE char_handle, int damage) {
    if (!char_handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        auto ch = g_global_handle_manager.GetCharacter(id);
        if (!ch) return ABOT_ERROR_NULL_PTR;
        
        ch->TakeDamage(damage);
        return ABOT_OK;
    } catch (const std::exception& e) {
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_character_heal(ABOT_HANDLE char_handle, int healing) {
    if (!char_handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        auto ch = g_global_handle_manager.GetCharacter(id);
        if (!ch) return ABOT_ERROR_NULL_PTR;
        
        ch->Heal(healing);
        return ABOT_OK;
    } catch (const std::exception& e) {
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API int abot_character_is_alive(ABOT_HANDLE char_handle) {
    if (!char_handle) {
        return 0;
    }
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(char_handle);
        auto ch = g_global_handle_manager.GetCharacter(id);
        return (ch && ch->IsAlive()) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

// ============ 战斗管理 ============

ABOT_API ABOT_HANDLE abot_battle_create(ABOT_HANDLE handle) {
    if (!handle) {
        return nullptr;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        auto battle = std::make_shared<abot::Battle>();
        uintptr_t battle_id = g_global_handle_manager.RegisterBattle(battle);
        context->ClearError();
        return reinterpret_cast<ABOT_HANDLE>(battle_id);
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Battle create error: ") + e.what());
        return nullptr;
    } catch (...) {
        return nullptr;
    }
}

ABOT_API void abot_battle_destroy(ABOT_HANDLE battle_handle) {
    if (!battle_handle) return;
    
    try {
        uintptr_t id = reinterpret_cast<uintptr_t>(battle_handle);
        g_global_handle_manager.UnregisterBattle(id);
    } catch (...) {
        // 忽略错误
    }
}

ABOT_API ABOT_ERROR abot_battle_initialize(ABOT_HANDLE battle_handle, ABOT_HANDLE* characters, int count) {
    if (!battle_handle || !characters || count <= 0) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        uintptr_t battle_id = reinterpret_cast<uintptr_t>(battle_handle);
        auto battle = g_global_handle_manager.GetBattle(battle_id);
        if (!battle) return ABOT_ERROR_NULL_PTR;
        
        // 从 handle 数组中获取 Character 对象
        std::vector<std::shared_ptr<abot::Character>> char_vector;
        for (int i = 0; i < count; ++i) {
            uintptr_t char_id = reinterpret_cast<uintptr_t>(characters[i]);
            auto ch = g_global_handle_manager.GetCharacter(char_id);
            if (!ch) {
                return ABOT_ERROR_NULL_PTR;
            }
            char_vector.push_back(ch);
        }
        
        // 初始化战斗
        if (!battle->Initialize(char_vector)) {
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        return ABOT_OK;
    } catch (const std::exception& e) {
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_battle_start(ABOT_HANDLE battle_handle) {
    if (!battle_handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        uintptr_t battle_id = reinterpret_cast<uintptr_t>(battle_handle);
        auto battle = g_global_handle_manager.GetBattle(battle_id);
        if (!battle) return ABOT_ERROR_NULL_PTR;
        
        if (!battle->Start()) {
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        return ABOT_OK;
    } catch (const std::exception& e) {
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_battle_execute_round(ABOT_HANDLE battle_handle) {
    if (!battle_handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        uintptr_t battle_id = reinterpret_cast<uintptr_t>(battle_handle);
        auto battle = g_global_handle_manager.GetBattle(battle_id);
        if (!battle) return ABOT_ERROR_NULL_PTR;
        
        if (!battle->ExecuteRound()) {
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        return ABOT_OK;
    } catch (const std::exception& e) {
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API int abot_battle_is_finished(ABOT_HANDLE battle_handle) {
    if (!battle_handle) {
        return 0;
    }
    
    try {
        uintptr_t battle_id = reinterpret_cast<uintptr_t>(battle_handle);
        auto battle = g_global_handle_manager.GetBattle(battle_id);
        return (battle && battle->IsFinished()) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

ABOT_API int abot_battle_get_victory_camp(ABOT_HANDLE battle_handle) {
    if (!battle_handle) {
        return -1;
    }
    
    try {
        uintptr_t battle_id = reinterpret_cast<uintptr_t>(battle_handle);
        auto battle = g_global_handle_manager.GetBattle(battle_id);
        if (!battle || !battle->IsFinished()) return -1;
        return battle->GetVictoryCamp();
    } catch (...) {
        return -1;
    }
}

ABOT_API int abot_battle_get_current_round(ABOT_HANDLE battle_handle) {
    if (!battle_handle) {
        return -1;
    }
    
    try {
        uintptr_t battle_id = reinterpret_cast<uintptr_t>(battle_handle);
        auto battle = g_global_handle_manager.GetBattle(battle_id);
        return battle ? battle->GetCurrentRound() : -1;
    } catch (...) {
        return -1;
    }
}

// ============ 回合管理器 API ============

ABOT_API ABOT_ERROR abot_round_manager_add_character(ABOT_HANDLE handle) {
    if (!handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->parsed_character) {
            context->SetError("No character parsed. Parse a character first.");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        // If RoundManager doesn't exist yet, create it
        if (!context->round_manager) {
            context->round_manager = std::make_shared<abot::RoundManager>();
        }
        
        // Create shared_ptr from unique_ptr by moving and making a copy-friendly version
        auto character_copy = std::make_shared<abot::Character>(*context->parsed_character);
        
        // Add character to round manager
        if (!context->round_manager->AddCharacter(character_copy)) {
            context->SetError("Failed to add character to round manager");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Add character error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_round_manager_clear_all_characters(ABOT_HANDLE handle) {
    if (!handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        // Clear the round manager or create a new one
        if (context->round_manager) {
            context->round_manager->ClearAllCharacters();
        }
        // If no round manager exists yet, one will be created on demand
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Clear characters error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_round_manager_init(ABOT_HANDLE handle) {
    if (!handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        // If RoundManager doesn't exist, create it
        if (!context->round_manager) {
            context->round_manager = std::make_shared<abot::RoundManager>();
        }
        
        // Initialize the round manager
        if (!context->round_manager->Initialize()) {
            context->SetError("Failed to initialize round manager: " + context->round_manager->GetLastError());
            context->round_manager = nullptr;
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Round manager initialization error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_round_manager_advance(ABOT_HANDLE handle) {
    if (!handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        // 【诊断】在最开始输出日志
        fprintf(stderr, "[C_API_ADVANCE] abot_round_manager_advance called\n");
        fflush(stderr);
        
        if (!context->round_manager) {
            context->SetError("Round manager not initialized");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        // 【诊断】在调用 ExecuteNextRound 前输出
        fprintf(stderr, "[C_API_ADVANCE] About to call ExecuteNextRound()\n");
        fflush(stderr);
        
        if (!context->round_manager->ExecuteNextRound()) {
            context->SetError("Failed to advance round: " + context->round_manager->GetLastError());
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        fprintf(stderr, "[C_API_ADVANCE] Exception caught: %s\n", e.what());
        fflush(stderr);
        
        // 【关键】异常发生时，同步技能触发日志到 context
        // 这样 C# 端即使异常也能读取到已产生的诊断日志
        if (context->round_manager) {
            std::string current_log = context->round_manager->GetSkillTriggerLog();
            context->skill_trigger_log_buffer = current_log + "\n[C_API_CATCH_BLOCK] Exception: " + e.what();
            fprintf(stderr, "[C_API_ADVANCE] Synced skill_trigger_log_buffer with length: %zu\n", context->skill_trigger_log_buffer.length());
            fflush(stderr);
        }
        
        context->SetError(std::string("Round advancement error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_round_manager_advance_multiple(ABOT_HANDLE handle, int count) {
    if (!handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            context->SetError("Round manager not initialized");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        int executed = context->round_manager->ExecuteRounds(count);
        if (executed == 0 && count > 0) {
            context->SetError("Failed to advance rounds");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Multiple round advancement error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API ABOT_ERROR abot_round_manager_skip(ABOT_HANDLE handle) {
    if (!handle) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            context->SetError("Round manager not initialized");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        if (!context->round_manager->SkipCurrentRound()) {
            context->SetError("Failed to skip round: " + context->round_manager->GetLastError());
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Round skip error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

ABOT_API void abot_round_manager_pause(ABOT_HANDLE handle) {
    if (!handle) return;
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (context->round_manager) {
            context->round_manager->Pause();
        }
    } catch (...) {
        // Ignore exceptions in pause
    }
}

ABOT_API void abot_round_manager_resume(ABOT_HANDLE handle) {
    if (!handle) return;
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (context->round_manager) {
            context->round_manager->Resume();
        }
    } catch (...) {
        // Ignore exceptions in resume
    }
}

ABOT_API int abot_round_manager_is_running(ABOT_HANDLE handle) {
    if (!handle) {
        return 0;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            return 0;
        }
        
        return context->round_manager->IsRunning() ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

ABOT_API int abot_round_manager_is_finished(ABOT_HANDLE handle) {
    if (!handle) {
        return 1;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            return 1;
        }
        
        return context->round_manager->IsFinished() ? 1 : 0;
    } catch (...) {
        return 1;
    }
}

ABOT_API int abot_round_manager_get_current_round(ABOT_HANDLE handle) {
    if (!handle) {
        return -1;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            return -1;
        }
        
        return context->round_manager->GetCurrentRound();
    } catch (...) {
        return -1;
    }
}

/// <summary>
/// ✅ 新增防御性检查函数：验证 RoundManager 是否真的准备好了
/// 用于 LoadState() 后进行健康检查，防止"假成功"问题
/// </summary>
ABOT_API int abot_round_manager_is_ready(ABOT_HANDLE handle) {
    if (!handle) {
        return 0;  // 句柄无效，不 ready
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        // 检查项 1: RoundManager 是否存在
        if (!context->round_manager) {
            return 0;  // RoundManager 未创建
        }
        
        // 检查项 2: RoundManager 是否有字符（至少需要一个参战角色）
        auto characters = context->round_manager->GetAllCharacters();
        if (characters.empty()) {
            return 0;  // 没有参战角色，无法执行回合
        }
        
        // 检查项 3: RoundManager 是否已初始化（通过 IsRunning 判断）
        // IsRunning 返回 true 表示战斗已经初始化并在进行中
        if (!context->round_manager->IsRunning()) {
            return 0;  // RoundManager 未初始化或已停止
        }
        
        // ✅ 所有检查都通过
        return 1;  // RoundManager 完全准备好了！
        
    } catch (...) {
        return 0;  // 异常发生，不 ready
    }
}

ABOT_API const char* abot_round_manager_get_status(ABOT_HANDLE handle) {
    if (!handle) {
        return "";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            context->status_buffer = "Round manager not initialized";
        } else {
            context->status_buffer = context->round_manager->GetStatusSummary();
        }
        
        return context->status_buffer.c_str();
    } catch (...) {
        return "";
    }
}

/**
 * @brief 过滤诊断日志
 * 
 * 硬编码过滤掉所有诊断类日志行（以 [DIAG][CONTAINER][HARDLOG][VM_][TABLE_ACCESS][LOAD_SELF][STORE_VAR][BUILTIN] 等开头）
 * 
 * 启用/禁用过滤：修改下方的 ENABLE_DIAGNOSTIC_LOG_FILTER 常量
 *   - true: 启用过滤（生产环境推荐）
 *   - false: 禁用过滤，显示所有日志（调试环境）
 */
static std::string FilterDiagnosticLogs(const std::string& raw_log) {
    // ========== 过滤开关 ==========
    // 修改这里来启用/禁用诊断日志过滤
    const bool ENABLE_DIAGNOSTIC_LOG_FILTER = true;
    // ================================
    
    if (!ENABLE_DIAGNOSTIC_LOG_FILTER) {
        return raw_log;  // 直接返回，不过滤
    }
    
    std::istringstream iss(raw_log);
    std::ostringstream oss;
    std::string line;
    
    while (std::getline(iss, line)) {
        // 检查行是否以诊断前缀开头（包括所有变种）
        if (line.find("[DIAG") == 0 ||           // [DIAG]、[SELF_DIAG] 等
            line.find("[DEBUG") == 0 ||            // [DEBUG]
            line.find("[CONTAINER") == 0 ||     // [CONTAINER]、[CONTAINER] [...]
            line.find("[HARDLOG") == 0 ||       // [HARDLOG]
            line.find("[VM_") == 0 ||           // [VM_IP_*]、[VM_EXECUTE_*] 等
            line.find("[TABLE_") == 0 ||  // [TABLE_ACCESS-*]
            line.find("[LOAD_SELF") == 0 ||     // [LOAD_SELF-*]
            line.find("[STORE_VAR") == 0 ||     // [STORE_VAR]
            line.find("[DAMAGE") == 0 ||        // [DAMAGE]
            line.find("[TRIGGER") == 0 ||        // [TRIGGER]
            line.find("[SKILL") == 0 ||         // [Skill]
            line.find("[指令执行前") == 0 ||    // [指令执行前]
            line.find("[DECLARE") == 0 ||       // [DECLARE]
            line.find("[ROUND") == 0 ||         // [ROUND]
            line.find("[BUILTIN_") == 0 ||      // [BUILTIN_926] 等
            line.find("[EXEC_") == 0 ||         // [EXEC_ROUND_*]
            line.find("[HANDLE断点") == 0 ||    // [HANDLE断点*]
            line.find("[from_schema") == 0 ||   // [from_schema]
            line.find("[SCOPE_GET") == 0 ||     // [SCOPE_GET]
            line.find("[📋") == 0) {            // [📋 全指令追踪] 等（中文符号）
            // 过滤掉此行
            continue;
        }
        
        // 保留此行
        oss << line << "\n";
    }
    
    return oss.str();
}

ABOT_API const char* abot_round_manager_get_log(ABOT_HANDLE handle) {
    if (!handle) {
        return "";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            context->log_buffer = "Round manager not initialized";
        } else {
            std::string raw_log = context->round_manager->GetBattleLog();
            context->log_buffer = FilterDiagnosticLogs(raw_log);
        }
        
        return context->log_buffer.c_str();
    } catch (...) {
        return "";
    }
}

ABOT_API const char* abot_round_manager_get_skill_trigger_log(ABOT_HANDLE handle) {
    if (!handle) {
        return "";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            context->skill_trigger_log_buffer = "Round manager not initialized";
        } else {
            std::string raw_log = context->round_manager->GetSkillTriggerLog();
            context->skill_trigger_log_buffer = FilterDiagnosticLogs(raw_log);
        }
        
        return context->skill_trigger_log_buffer.c_str();
    } catch (...) {
        return "";
    }
}

ABOT_API ABOT_ERROR abot_round_manager_execute_command(ABOT_HANDLE handle, const char* command, const char* parameters) {
    if (!handle || !command) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            context->SetError("Round manager not initialized");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        std::string cmd(command);
        std::string params = parameters ? std::string(parameters) : "";
        
        if (!context->round_manager->ExecuteCommand(cmd, params)) {
            context->SetError("Command execution failed: " + context->round_manager->GetLastError());
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        context->ClearError();
        return ABOT_OK;
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Command execution error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

// ============ 状态导出/导入（多用户隔离支持）============

/// <summary>
/// 将解释器状态导出为JSON格式
/// 包含：圆形管理器状态、角色信息等
/// </summary>
ABOT_API const char* abot_export_state_json(ABOT_HANDLE handle) {
    if (!handle) {
        return "";
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        // 构造 JSON 格式的状态信息
        static std::string json_state;
        std::string round_manager_status = context->round_manager ? 
            context->round_manager->GetStatusSummary() : 
            "round_manager not initialized";
        
        json_state = "{\"exported\":true,\"round_manager_status\":\"" + round_manager_status + "\"}";
        
        return json_state.c_str();
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Export state error: ") + e.what());
        return "";
    } catch (...) {
        return "";
    }
}

/// <summary>
/// 从 JSON 格式导入状态到解释器
/// 
/// JSON 格式（来自 C# SaveState）：
/// {
///   "userId": ...,
///   "createdAt": "...",
///   "characterBasicInfo": "...",
///   "characterSkillsInfo": "...",
///   "characterStatesInfo": "...",
///   "round_manager": "...",       // 关键字段：RoundManager 状态
///   "roundManagerLog": "...",
///   "skillTriggerLog": "...",
///   "lastError": "...",
///   "aBotVersion": "..."
/// }
/// </summary>
ABOT_API ABOT_ERROR abot_import_state_json(ABOT_HANDLE handle, const char* json_state_bytes) {
    FILE* import_log = nullptr;
    
    // 优先级：应用目录 > 临时目录 > 只输出到 stdout
    const char* log_paths[] = {
        "abot.log",                                    // 应用目录
        "mods/ABot/abot.log",                          // MOD 目录
        "C:\\Windows\\Temp\\abot_import_state.log"    // 备选临时目录
    };
    
    for (const char* path : log_paths) {
        fopen_s(&import_log, path, "at");
        if (import_log) break;
    }
    
    if (!handle || !json_state_bytes) {
        if (import_log) {
            fprintf(import_log, "[IMPORT] ERROR: handle=%p, json_state_bytes=%p\n", handle, json_state_bytes);
            fflush(import_log);
            fclose(import_log);
        }
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        std::string json_str(json_state_bytes);
        
        if (import_log) {
            fprintf(import_log, "\n========== abot_import_state_json CALLED (新格式导入) ==========\n");
            fprintf(import_log, "[IMPORT] JSON length: %zu\n", json_str.length());
            fprintf(import_log, "[IMPORT] JSON (first 500 chars): %.500s\n", json_str.c_str());
        }
        
        // 【关键检查】检查是否为新格式（包含 "characters" 数组）
        size_t characters_pos = json_str.find("\"characters\":");
        
        if (characters_pos == std::string::npos) {
            // ❌ 没有 characters 字段 - 不支持旧格式
            if (import_log) {
                fprintf(import_log, "[IMPORT] ❌ ERROR: JSON missing 'characters' array field\n");
                fprintf(import_log, "[IMPORT] The snapshot is in old format and cannot be restored\n");
                fprintf(import_log, "[IMPORT] Required: New format with 'characters' JSON array\n");
                fclose(import_log);
            }
            context->SetError("Snapshot format not supported - requires 'characters' array field");
            return ABOT_ERROR_INVALID_XML;
        }
        
        if (import_log) {
            fprintf(import_log, "[IMPORT] ✅ Found 'characters' array at position %zu\n", characters_pos);
        }
        
        // 【第1步】确保 RoundManager 存在且为空 - 新建或清空后再导入角色
        if (!context->round_manager) {
            if (import_log) {
                fprintf(import_log, "[IMPORT] Creating new RoundManager...\n");
            }
            context->round_manager = std::make_shared<abot::RoundManager>();
        } else {
            // 【关键修复】如果 RoundManager 已存在，必须清空现有的角色
            // 防止旧数据与新导入的数据混杂（导致重复人物）
            if (import_log) {
                fprintf(import_log, "[IMPORT] RoundManager already exists - clearing old characters...\n");
            }
            // 获取所有现有角色并清除
            auto existing_chars = context->round_manager->GetAllCharacters();
            if (import_log) {
                fprintf(import_log, "[IMPORT] Found %zu existing characters to remove\n", existing_chars.size());
            }
            
            // 重新创建 RoundManager 以彻底清空状态
            context->round_manager = std::make_shared<abot::RoundManager>();
            
            if (import_log) {
                fprintf(import_log, "[IMPORT] ✅ RoundManager reset and ready for new import\n");
            }
        }
        
        // 【第2步】从 JSON 中提取 characters 数组
        // 格式：[{角色1}, {角色2}, ...]
        // 注意：需要计算括号配对，因为角色 JSON 中可能有嵌套数组 (dmg: [...])
        
        size_t array_start = json_str.find("[", characters_pos);
        if (array_start == std::string::npos) {
            if (import_log) {
                fprintf(import_log, "[IMPORT] ❌ ERROR: Could not find '[' for characters array\n");
                fclose(import_log);
            }
            context->SetError("Invalid JSON format - characters field is not an array");
            return ABOT_ERROR_INVALID_XML;
        }
        
        // 【修复】计算括号配对来定位数组末尾
        int bracket_count = 1;  // 已经找到一个 [
        size_t array_end = array_start + 1;
        while (array_end < json_str.length() && bracket_count > 0) {
            if (json_str[array_end] == '[' && (array_end == 0 || json_str[array_end - 1] != '\\')) {
                bracket_count++;
            } else if (json_str[array_end] == ']' && (array_end == 0 || json_str[array_end - 1] != '\\')) {
                bracket_count--;
            }
            if (bracket_count > 0) array_end++;
        }
        
        if (bracket_count != 0) {
            if (import_log) {
                fprintf(import_log, "[IMPORT] ❌ ERROR: Could not find matching ']' for characters array\n");
                fclose(import_log);
            }
            context->SetError("Invalid JSON format - unclosed characters array");
            return ABOT_ERROR_INVALID_XML;
        }
        
        std::string characters_array_str = json_str.substr(array_start + 1, array_end - array_start - 1);
        
        if (import_log) {
            fprintf(import_log, "[IMPORT] Extracted characters array (length: %zu)\n", characters_array_str.length());
            fprintf(import_log, "[IMPORT] Content (first 300 chars): %.300s\n", characters_array_str.c_str());
        }
        
        // 【第3步】逐个导入角色
        // 需要分解 JSON 数组中的每个对象
        
        if (import_log) {
            fprintf(import_log, "[IMPORT] ========== Starting character deserialization ==========\n");
        }
        
        size_t pos = 0;
        int char_count = 0;
        
        while (pos < characters_array_str.length()) {
            // 查找下一个 {
            size_t obj_start = characters_array_str.find("{", pos);
            if (obj_start == std::string::npos) break;
            
            // 找到匹配的 }
            int brace_count = 1;
            size_t obj_end = obj_start + 1;
            while (obj_end < characters_array_str.length() && brace_count > 0) {
                if (characters_array_str[obj_end] == '{' && characters_array_str[obj_end - 1] != '\\') {
                    brace_count++;
                } else if (characters_array_str[obj_end] == '}' && characters_array_str[obj_end - 1] != '\\') {
                    brace_count--;
                }
                if (brace_count > 0) obj_end++;
            }
            
            if (obj_end < characters_array_str.length()) {
                std::string char_json = characters_array_str.substr(obj_start, obj_end - obj_start + 1);
                
                if (import_log) {
                    fprintf(import_log, "[IMPORT] ========== Character #%d ==========\n", char_count + 1);
                    fprintf(import_log, "[IMPORT] JSON: %.200s\n", char_json.c_str());
                }
                
                // 【关键】调用反序列化，此时 RoundManager 已经存在
                ABOT_ERROR deser_result = abot_deserialize_character_json(handle, char_json.c_str());
                
                if (deser_result == ABOT_OK) {
                    char_count++;
                    if (import_log) {
                        fprintf(import_log, "[IMPORT] ✅ Character #%d deserialized successfully\n", char_count);
                    }
                } else {
                    if (import_log) {
                        fprintf(import_log, "[IMPORT] ❌ Character #%d deserialization failed (code %d)\n", char_count + 1, deser_result);
                    }
                    // 继续导入其他角色，不要中止
                }
                
                pos = obj_end + 1;
            } else {
                break;
            }
        }
        
        if (import_log) {
            fprintf(import_log, "[IMPORT] ========== Total characters deserialized: %d ==========\n", char_count);
        }
        
        // 【第4步】初始化 RoundManager
        if (import_log) {
            fprintf(import_log, "[IMPORT] Initializing RoundManager...\n");
        }
        
        if (!context->round_manager->Initialize()) {
            if (import_log) {
                fprintf(import_log, "[IMPORT] WARNING: Initialize() failed: %s\n", 
                        context->round_manager->GetLastError().c_str());
                fprintf(import_log, "[IMPORT] Calling ForceStart()...\n");
            }
            context->round_manager->ForceStart();
        } else {
            if (import_log) {
                fprintf(import_log, "[IMPORT] ✅ RoundManager initialized successfully\n");
            }
        }
        
        // 【第5步】最终验证
        if (import_log) {
            fprintf(import_log, "[IMPORT] ========== FINAL VALIDATION ==========\n");
        }
        
        auto characters = context->round_manager->GetAllCharacters();
        
        if (import_log) {
            fprintf(import_log, "[IMPORT] Character count in RoundManager: %zu\n", characters.size());
            for (size_t i = 0; i < characters.size(); i++) {
                if (characters[i]) {
                    fprintf(import_log, "[IMPORT]   [%zu] %s (Camp: %d, HP: %d/%d)\n",
                            i, 
                            characters[i]->name.c_str(),
                            characters[i]->camp,
                            characters[i]->hp,
                            characters[i]->max_hp);
                }
            }
        }
        
        if (characters.empty()) {
            if (import_log) {
                fprintf(import_log, "[IMPORT] ❌ VALIDATION FAILED: No characters in RoundManager after import\n");
                fclose(import_log);
            }
            context->SetError("No characters in RoundManager after import - snapshot may be corrupted");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        // 【第6步】检查 RoundManager 是否在运行
        if (!context->round_manager->IsRunning()) {
            if (import_log) {
                fprintf(import_log, "[IMPORT] ❌ VALIDATION FAILED: RoundManager not running\n");
                fprintf(import_log, "[IMPORT] Status: %s\n", context->round_manager->GetLastError().c_str());
                fclose(import_log);
            }
            context->SetError("RoundManager state incomplete after import - not running");
            return ABOT_ERROR_RUNTIME_ERROR;
        }
        
        // ✅ 全部通过验证
        context->ClearError();
        
        if (import_log) {
            fprintf(import_log, "[IMPORT] ✅ ALL VALIDATION CHECKS PASSED\n");
            fprintf(import_log, "[IMPORT] State import completed successfully with %zu character(s)\n", characters.size());
            fprintf(import_log, "[IMPORT] ========== IMPORT COMPLETED SUCCESSFULLY ==========\n\n");
            fflush(import_log);
            fclose(import_log);
        }
        
        return ABOT_OK;
        
    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        std::string error_msg = std::string("Import state error: ") + e.what();
        context->SetError(error_msg);
        
        if (import_log) {
            fprintf(import_log, "[IMPORT] EXCEPTION: %s\n", error_msg.c_str());
            fclose(import_log);
        }
        
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        if (import_log) {
            fprintf(import_log, "[IMPORT] UNKNOWN EXCEPTION\n");
            fclose(import_log);
        }
        return ABOT_ERROR_UNKNOWN;
    }
}

/// <summary>
/// 将状态导出为二进制格式
/// （暂未实现，留作扩展）
/// </summary>
ABOT_API const char* abot_export_state_binary(ABOT_HANDLE handle, int* out_size) {
    if (!handle || !out_size) {
        return nullptr;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        *out_size = 0;  // 暂未实现
        
        context->SetError("Binary export not yet implemented");
        return nullptr;
    } catch (...) {
        return nullptr;
    }
}

/// <summary>
/// 从二进制格式导入状态
/// （暂未实现，留作扩展）
/// </summary>
ABOT_API ABOT_ERROR abot_import_state_binary(ABOT_HANDLE handle, const char* binary_data, int binary_size) {
    if (!handle || !binary_data || binary_size <= 0) {
        return ABOT_ERROR_NULL_PTR;
    }
    
    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError("Binary import not yet implemented");
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

/// <summary>
/// 将当前已解析的角色序列化为 JSON 格式
/// 返回 JSON 字符串，C# 端可调用此函数获取结构化的角色数据
/// </summary>
ABOT_API const char* abot_serialize_character_json(ABOT_HANDLE handle) {
    if (!handle) {
        return "{\"error\":\"Invalid handle\"}";
    }

    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        if (!context->parsed_character) {
            return "{\"error\":\"No character parsed\"}";
        }

        auto& ch = context->parsed_character;
        std::ostringstream oss;
        
        oss << "{"
            << "\"name\":\"" << ch->name << "\","
            << "\"camp\":" << ch->camp << ","
            << "\"atk\":" << ch->atk << ","
            << "\"hp\":" << ch->hp << ","
            << "\"max_hp\":" << ch->max_hp << ","
            << "\"hp_restore\":" << ch->hp_restore << ","
            << "\"temp_hp\":" << ch->temp_hp << ","
            << "\"aggro\":" << ch->aggro << ","
            << "\"is_alive\":" << (ch->is_alive ? "true" : "false") << ","
            << "\"dmg\":[" << ch->dmg[0] << "," << ch->dmg[1] << "," << ch->dmg[2] << "," << ch->dmg[3] << "],";

        // 序列化技能
        oss << "\"skills\":[";
        for (size_t i = 0; i < ch->skills.size(); i++) {
            const auto& skill = ch->skills[i];
            if (i > 0) oss << ",";
            oss << "{\"name\":\"" << skill.name << "\","
                << "\"id\":\"" << skill.id << "\","
                << "\"type\":\"" << skill.type << "\","
                << "\"cd\":" << skill.cd << ","
                << "\"rate\":" << skill.rate << ","
                << "\"disabled\":" << (skill.disabled ? "true" : "false") << "}";
        }
        oss << "],";

        // 序列化标签
        oss << "\"tags\":[";
        for (size_t i = 0; i < ch->tags.size(); i++) {
            if (i > 0) oss << ",";
            oss << "\"" << ch->tags[i] << "\"";
        }
        oss << "],";

        // 序列化伤害减免
        oss << "\"damage_reductions\":[";
        for (size_t i = 0; i < ch->damage_reductions.size(); i++) {
            const auto& dr = ch->damage_reductions[i];
            if (i > 0) oss << ",";
            oss << "{\"value\":" << std::fixed << std::setprecision(4) << dr.value 
                << ",\"tag\":\"" << dr.tag << "\"}";
        }
        oss << "],";

        // 序列化护甲
        oss << "\"defenses\":[";
        for (size_t i = 0; i < ch->defenses.size(); i++) {
            const auto& def = ch->defenses[i];
            if (i > 0) oss << ",";
            oss << "{\"value\":" << def.value << ",\"tag\":\"" << def.tag << "\"}";
        }
        oss << "]"  // 最后一个字段不需要逗号
            << "}";

        // 保存到静态变量以便返回指针
        static std::string last_json;
        last_json = oss.str();
        return last_json.c_str();

    } catch (const std::exception& e) {
        static std::string error;
        error = std::string("{\"error\":\"") + e.what() + "\"}";
        return error.c_str();
    }
}

/// <summary>
/// 【JSON转义】将字符串进行JSON转义，用于安全输出到JSON
/// 处理转义字符：" \ / \b \f \n \r \t
/// UTF-8 多字节字符（如中文）保持原样
/// </summary>
static std::string JsonEscape(const std::string& input) {
    std::string result;
    for (unsigned char c : input) {
        switch (c) {
            case '"':
                result += "\\\"";
                break;
            case '\\':
                result += "\\\\";
                break;
            case '/':
                result += "\\/";
                break;
            case '\b':
                result += "\\b";
                break;
            case '\f':
                result += "\\f";
                break;
            case '\n':
                result += "\\n";
                break;
            case '\r':
                result += "\\r";
                break;
            case '\t':
                result += "\\t";
                break;
            default:
                // 包括 UTF-8 多字节字符 (>= 0x80) 直接保持
                result += c;
                break;
        }
    }
    return result;
}

/// <summary>
/// 序列化 RoundManager 中的所有角色为 JSON 数组
/// 用于多角色战斗状态的完整保存
/// 返回格式：[{角色1}, {角色2}, ...]
/// </summary>
ABOT_API const char* abot_serialize_all_characters_json(ABOT_HANDLE handle) {
    if (!handle) {
        static std::string error = "[]";
        return error.c_str();
    }

    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            // RoundManager 不存在，返回空数组
            static std::string empty_array = "[]";
            return empty_array.c_str();
        }

        auto characters = context->round_manager->GetAllCharacters();
        
        std::ostringstream oss;
        oss << "[";
        
        for (size_t i = 0; i < characters.size(); i++) {
            if (i > 0) oss << ",";
            
            const auto& ch = characters[i];
            if (!ch) continue;
            
            oss << "{"
                << "\"name\":\"" << JsonEscape(ch->name) << "\","
                << "\"camp\":" << ch->camp << ","
                << "\"atk\":" << ch->atk << ","
                << "\"hp\":" << ch->hp << ","
                << "\"max_hp\":" << ch->max_hp << ","
                << "\"hp_restore\":" << ch->hp_restore << ","
                << "\"temp_hp\":" << ch->temp_hp << ","
                << "\"aggro\":" << ch->aggro << ","
                << "\"is_alive\":" << (ch->is_alive ? "true" : "false") << ","
                << "\"dmg\":[" << ch->dmg[0] << "," << ch->dmg[1] << "," << ch->dmg[2] << "," << ch->dmg[3] << "],";

            // 序列化技能
            oss << "\"skills\":[";
            for (size_t j = 0; j < ch->skills.size(); j++) {
                const auto& skill = ch->skills[j];
                if (j > 0) oss << ",";
                oss << "{\"name\":\"" << JsonEscape(skill.name) << "\","
                    << "\"id\":\"" << JsonEscape(skill.id) << "\","
                    << "\"type\":\"" << JsonEscape(skill.type) << "\","
                    << "\"cd\":" << skill.cd << ","
                    << "\"rate\":" << skill.rate << ","
                    << "\"disabled\":" << (skill.disabled ? "true" : "false") << "}";
            }
            oss << "],";

            // 序列化标签
            oss << "\"tags\":[";
            for (size_t j = 0; j < ch->tags.size(); j++) {
                if (j > 0) oss << ",";
                oss << "\"" << JsonEscape(ch->tags[j]) << "\"";
            }
            oss << "],";

            // 序列化伤害减免
            oss << "\"damage_reductions\":[";
            for (size_t j = 0; j < ch->damage_reductions.size(); j++) {
                const auto& dr = ch->damage_reductions[j];
                if (j > 0) oss << ",";
                oss << "{\"value\":" << std::fixed << std::setprecision(4) << dr.value 
                    << ",\"tag\":\"" << JsonEscape(dr.tag) << "\"}";
            }
            oss << "],";

            // 序列化护甲
            oss << "\"defenses\":[";
            for (size_t j = 0; j < ch->defenses.size(); j++) {
                const auto& def = ch->defenses[j];
                if (j > 0) oss << ",";
                oss << "{\"value\":" << def.value << ",\"tag\":\"" << JsonEscape(def.tag) << "\"}";
            }
            oss << "]"  // 最后一个字段不需要逗号
                << "}";
        }
        
        oss << "]";

        // 保存到静态变量以便返回指针
        static std::string last_json;
        last_json = oss.str();
        return last_json.c_str();

    } catch (const std::exception& e) {
        static std::string error;
        error = std::string("[]");
        return error.c_str();
    }
}

/// <summary>
/// 【辅助】从 JSON 中安全地提取字符串字段值
/// 处理转义引号、前后空白、UTF-8 BOM等边界情况
/// 返回修剪后的字符串
/// </summary>
static std::string ExtractJsonStringField(const std::string& json_str, const std::string& field_name) {
    // 查找字段定义，兼容 "field": "value" 和 "field":"value" 两种格式
    std::string field_pattern = "\"" + field_name + "\"";
    size_t pos = json_str.find(field_pattern);
    
    if (pos == std::string::npos) {
        return "";
    }
    
    // 跳过字段名和冒号，找到开始引号
    size_t colon_pos = pos + field_pattern.length();
    size_t quote_start = json_str.find("\"", colon_pos);
    
    if (quote_start == std::string::npos) {
        return "";
    }
    
    size_t start = quote_start + 1;
    size_t end = start;
    
    // 【关键】安全地处理转义的引号，找到实际的结束引号
    while (end < json_str.length()) {
        if (json_str[end] == '\"') {
            // 计算前面的反斜杠数量
            int backslash_count = 0;
            size_t check_pos = end;
            while (check_pos > start && json_str[check_pos - 1] == '\\') {
                backslash_count++;
                check_pos--;
            }
            
            // 如果反斜杠数是偶数（包括0），则这个" 是真正的结束引号
            if (backslash_count % 2 == 0) {
                break;
            }
        }
        end++;
    }
    
    if (end >= json_str.length()) {
        return "";
    }
    
    std::string result = json_str.substr(start, end - start);
    
    // 【关键修复】更加激进的前后空白修剪
    // 修剪前导空白 - 包括所有 whitespace 和特殊 Unicode 字符
    while (!result.empty()) {
        unsigned char first = result[0];
        if (first <= 32 || first == 0xEF) {  // ASCII space, tab, newline或UTF-8 BOM的第一个字节
            result.erase(0, 1);
        } else {
            break;
        }
    }
    
    // 修剪末尾空白
    while (!result.empty()) {
        unsigned char last = result.back();
        if (last <= 32) {  // ASCII space, tab, newline等
            result.pop_back();
        } else {
            break;
        }
    }
    
    // 【特殊处理】如果检测到 UTF-8 BOM 在开始位置，去掉
    if (result.length() >= 3 && 
        (unsigned char)result[0] == 0xEF && 
        (unsigned char)result[1] == 0xBB && 
        (unsigned char)result[2] == 0xBF) {
        result = result.substr(3);
    }
    
    return result;
}

/// <summary>
/// 从 JSON 反序列化并创建一个新的角色
/// 解析给定的 JSON，创建 Character 对象并添加到 RoundManager
/// 【关键】应该从已 Base64 编码的数据解析，所以输入应该是纯 UTF-8 JSON
/// </summary>
ABOT_API ABOT_ERROR abot_deserialize_character_json(ABOT_HANDLE handle, const char* character_json) {
    if (!handle || !character_json) {
        return ABOT_ERROR_NULL_PTR;
    }

    try {
        auto context = static_cast<abot::ABotContext*>(handle);
        
        if (!context->round_manager) {
            context->SetError("RoundManager not initialized");
            return ABOT_ERROR_RUNTIME_ERROR;
        }

        std::string json_str(character_json);
        auto new_char = std::make_shared<abot::Character>();

        // 【安全解析】使用新的辅助函数获取 name 字段
        // 这个函数处理：转义引号、前后空白、UTF-8 BOM、格式变化等
        new_char->name = ExtractJsonStringField(json_str, "name");
        
        // 再次激进修剪（双重保险）
        while (!new_char->name.empty() && std::isspace((unsigned char)new_char->name.back())) {
            new_char->name.pop_back();
        }
        while (!new_char->name.empty() && std::isspace((unsigned char)new_char->name.front())) {
            new_char->name.erase(0, 1);
        }

        // 提取整数字段的辅助 lambda
        auto extract_int = [&json_str](const std::string& field_name, int& out_value) {
            size_t pos = json_str.find("\"" + field_name + "\":");
            if (pos != std::string::npos) {
                size_t start = pos + field_name.length() + 3;  // skip ":
                size_t end = json_str.find(",", start);
                if (end == std::string::npos) {
                    end = json_str.find("}", start);
                }
                if (end != std::string::npos) {
                    std::string num_str = json_str.substr(start, end - start);
                    out_value = std::atoi(num_str.c_str());
                }
            }
        };

        extract_int("camp", new_char->camp);
        extract_int("atk", new_char->atk);
        extract_int("hp", new_char->hp);
        extract_int("max_hp", new_char->max_hp);
        extract_int("hp_restore", new_char->hp_restore);
        extract_int("temp_hp", new_char->temp_hp);
        extract_int("aggro", new_char->aggro);

        // 提取 is_alive
        if (json_str.find("\"is_alive\":true") != std::string::npos) {
            new_char->is_alive = true;
        } else if (json_str.find("\"is_alive\":false") != std::string::npos) {
            new_char->is_alive = false;
        }

        // 提取 dmg 数组 [d1, d2, d3, d4]
        size_t dmg_pos = json_str.find("\"dmg\":[");
        if (dmg_pos != std::string::npos) {
            std::istringstream dmg_stream;
            size_t start = dmg_pos + 8;  // skip "dmg":[
            size_t end = json_str.find("]", start);
            if (end != std::string::npos) {
                std::string dmg_str = json_str.substr(start, end - start);
                dmg_stream.str(dmg_str);
                dmg_stream >> new_char->dmg[0];
                dmg_stream.ignore();  // skip comma
                dmg_stream >> new_char->dmg[1];
                dmg_stream.ignore();
                dmg_stream >> new_char->dmg[2];
                dmg_stream.ignore();
                dmg_stream >> new_char->dmg[3];
            }
        }

        // 将新角色添加到 RoundManager
        if (!context->round_manager->AddCharacter(new_char)) {
            context->SetError("Failed to add deserialized character to RoundManager");
            return ABOT_ERROR_RUNTIME_ERROR;
        }

        return ABOT_OK;

    } catch (const std::exception& e) {
        auto context = static_cast<abot::ABotContext*>(handle);
        context->SetError(std::string("Character deserialization error: ") + e.what());
        return ABOT_ERROR_RUNTIME_ERROR;
    } catch (...) {
        return ABOT_ERROR_UNKNOWN;
    }
}

}  // extern "C"

