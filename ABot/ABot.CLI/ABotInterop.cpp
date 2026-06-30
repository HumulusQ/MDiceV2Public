/**
 * @file ABotInterop.cpp
 * @brief C++/CLI 互操作层实现
 * 
 * 实现了托管包装类，处理C++和托管代码之间的互操作
 */

#include "ABotInterop.h"
#include "../ABot.Core/src/C_API.h"
#include <cstring>
#include <cstdlib>

namespace ABot {
namespace CLI {

// ============ StringHelper 实现 ============

const char* StringHelper::ManagedToNative(String^ managed) {
    if (managed == nullptr)
        return nullptr;
    
    // 将托管字符串转换为UTF-8
    IntPtr ptr = Marshal::StringToHGlobalAnsi(managed);
    const char* native = static_cast<const char*>(ptr.ToPointer());
    return native;
}

String^ StringHelper::NativeToManaged(const char* native) {
    if (native == nullptr)
        return nullptr;
    
    // 将UTF-8字符串转换为托管字符串
    return Marshal::PtrToStringAnsi(IntPtr(const_cast<char*>(native)));
}

void StringHelper::FreeNativeString(const char* ptr) {
    if (ptr == nullptr)
        return;
    
    Marshal::FreeHGlobal(IntPtr(const_cast<char*>(ptr)));
}

// ============ ABotInterpreter 实现 ============

/**
 * @brief ABotInterpreter 构造函数
 * 
 * 创建一个新的 ABOT 解释器实例，在 C++ Core 中创建相应的上下文对象
 * 
 * @throws InvalidOperationException 如果 ABOT 上下文创建失败
 */
ABotInterpreter::ABotInterpreter() {
    // 创建新的ABOT上下文
    ABOT_HANDLE native_handle = abot_create();
    
    if (!native_handle) {
        throw gcnew InvalidOperationException("Failed to create ABOT context - abot_create() returned nullptr");
    }
    
    handle = IntPtr(native_handle);
}

/**
 * @brief ABotInterpreter 析构函数
 * 
 * 销毁 ABOT 上下文对象，释放所有关联的资源
 * 这是非托管资源清理的关键方法
 */
ABotInterpreter::~ABotInterpreter() {
    // 销毁ABOT上下文
    if (handle != IntPtr::Zero) {
        ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
        abot_destroy(native_handle);
        handle = IntPtr::Zero;
    }
}

/**
 * @brief 解析角色参数单元
 * 
 * 将包含角色属性、技能、状态等信息的参数单元解析并加载到解释器中
 * 格式为 ABOT 自定义的参数单元语法（非标准 XML）
 * 格式示例：<character name=hero, attributes={hp=100, mp=50}, def=expr(...)>
 * 
 * @param characterXml ABOT 格式的角色定义参数单元字符串
 * @return 如果成功解析返回 true，否则返回 false（使用 GetLastError() 获取错误信息）
 * 
 * @throws ObjectDisposedException 如果解释器已被销毁
 * @throws ArgumentNullException 如果 characterXml 为空
 */
bool ABotInterpreter::ParseCharacter(String^ characterXml) {
    if (handle == IntPtr::Zero) {
        throw gcnew ObjectDisposedException("ABotInterpreter");
    }
    
    if (characterXml == nullptr) {
        throw gcnew ArgumentNullException("characterXml");
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    const char* native_xml = StringHelper::ManagedToNative(characterXml);
    
    try {
        ABOT_ERROR error = abot_parse_character(native_handle, native_xml);
        
        if (error != ABOT_OK) {
            return false;
        }
        
        return true;
    } finally {
        StringHelper::FreeNativeString(native_xml);
    }
}

/**
 * @brief 注册技能集参数单元
 * 
 * 将技能集的参数单元解析并注册到解释器中，使角色可以使用这些技能
 * 格式为 ABOT 自定义的参数单元语法（非标准 XML）
 * 格式示例：<skillset id=sword_skills, skills={slash={power=10, cost=5}}, def=expr(...)>
 * 
 * @param skillsetXml ABOT 格式的技能集定义参数单元字符串
 * @return 如果成功注册返回 true，否则返回 false
 * 
 * @throws ObjectDisposedException 如果解释器已被销毁
 * @throws ArgumentNullException 如果 skillsetXml 为空
 */
bool ABotInterpreter::RegisterSkillset(String^ skillsetXml) {
    if (handle == IntPtr::Zero) {
        throw gcnew ObjectDisposedException("ABotInterpreter");
    }
    
    if (skillsetXml == nullptr) {
        throw gcnew ArgumentNullException("skillsetXml");
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    const char* native_xml = StringHelper::ManagedToNative(skillsetXml);
    
    try {
        ABOT_ERROR error = abot_register_skillset(native_handle, native_xml);
        
        if (error != ABOT_OK) {
            return false;
        }
        
        return true;
    } finally {
        StringHelper::FreeNativeString(native_xml);
    }
}

/**
 * @brief 注册状态集参数单元
 * 
 * 将状态集的参数单元解析并注册到解释器中
 * 状态集定义了角色可能拥有的各种状态（如中毒、燃烧等）
 * 格式为 ABOT 自定义的参数单元语法（非标准 XML）
 * 格式示例：<stateset id=conditions, states={poison={effect=-1hp/turn, duration=3}}, def=expr(...)>
 * 
 * @param statesetXml ABOT 格式的状态集定义参数单元字符串
 * @return 如果成功注册返回 true，否则返回 false
 * 
 * @throws ObjectDisposedException 如果解释器已被销毁
 * @throws ArgumentNullException 如果 statesetXml 为空
 */
bool ABotInterpreter::RegisterStateset(String^ statesetXml) {
    if (handle == IntPtr::Zero) {
        throw gcnew ObjectDisposedException("ABotInterpreter");
    }
    
    if (statesetXml == nullptr) {
        throw gcnew ArgumentNullException("statesetXml");
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    const char* native_xml = StringHelper::ManagedToNative(statesetXml);
    
    try {
        ABOT_ERROR error = abot_register_stateset(native_handle, native_xml);
        
        if (error != ABOT_OK) {
            return false;
        }
        
        return true;
    } finally {
        StringHelper::FreeNativeString(native_xml);
    }
}

/**
 * @brief 注册安科集参数单元
 * 
 * 将安科集（ANKE - Additive Number Kinetic Engine）的参数单元注册到解释器中
 * 安科集定义了数值计算和伤害计算的规则
 * 格式为 ABOT 自定义的参数单元语法（非标准 XML）
 * 格式示例：<ankeset id=damage_calc, formulas={physical_dmg=expr(ATK-DEF), magic_dmg=expr(INT*2-RES)}, def=expr(...)>
 * 
 * @param ankesetXml ABOT 格式的安科集定义参数单元字符串
 * @return 如果成功注册返回 true，否则返回 false
 * 
 * @throws ObjectDisposedException 如果解释器已被销毁
 * @throws ArgumentNullException 如果 ankesetXml 为空
 */
bool ABotInterpreter::RegisterANKESet(String^ ankesetXml) {
    if (handle == IntPtr::Zero) {
        throw gcnew ObjectDisposedException("ABotInterpreter");
    }
    
    if (ankesetXml == nullptr) {
        throw gcnew ArgumentNullException("ankesetXml");
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    const char* native_xml = StringHelper::ManagedToNative(ankesetXml);
    
    try {
        ABOT_ERROR error = abot_register_ankeset(native_handle, native_xml);
        
        if (error != ABOT_OK) {
            return false;
        }
        
        return true;
    } finally {
        StringHelper::FreeNativeString(native_xml);
    }
}

/**
 * @brief 执行战斗模拟
 * 
 * 执行完整的战斗循环，包括所有回合的计算和结果
 * 必须先调用 ParseCharacter 加载角色数据才能执行此操作
 * 
 * @return 如果战斗成功执行返回 true，否则返回 false
 * 
 * @throws ObjectDisposedException 如果解释器已被销毁
 */
bool ABotInterpreter::ExecuteBattle() {
    if (handle == IntPtr::Zero) {
        throw gcnew ObjectDisposedException("ABotInterpreter");
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    ABOT_ERROR error = abot_execute_battle(native_handle);
    
    if (error != ABOT_OK) {
        return false;
    }
    
    return true;
}

/**
 * @brief 执行 ABOT 脚本代码
 * 
 * 执行一段 ABOT 脚本代码，包括词法分析、语法分析、编译和执行
 * 这是解释器的核心功能，支持变量赋值、表达式计算、控制流等
 * 
 * @param script 要执行的 ABOT 脚本代码字符串
 * @return 如果脚本成功执行返回 true，否则返回 false（检查 GetLastError 获取错误信息）
 * 
 * @throws ObjectDisposedException 如果解释器已被销毁
 * @throws ArgumentNullException 如果 script 为空
 */
bool ABotInterpreter::ExecuteScript(String^ script) {
    if (handle == IntPtr::Zero) {
        throw gcnew ObjectDisposedException("ABotInterpreter");
    }
    
    if (script == nullptr) {
        throw gcnew ArgumentNullException("script");
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    const char* native_script = StringHelper::ManagedToNative(script);
    
    try {
        ABOT_ERROR error = abot_execute_script(native_handle, native_script);
        
        if (error != ABOT_OK) {
            return false;
        }
        
        return true;
    } finally {
        StringHelper::FreeNativeString(native_script);
    }
}

/**
 * @brief 获取最后发生的错误消息
 * 
 * 返回最后一次操作中发生的错误描述
 * 如果没有错误发生，返回空字符串
 * 
 * @return 错误消息字符串，如果没有错误则返回空字符串
 */
String^ ABotInterpreter::GetLastError() {
    if (handle == IntPtr::Zero) {
        return "Interpreter disposed";
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    const char* native_error = abot_get_last_error(native_handle);
    
    return StringHelper::NativeToManaged(native_error);
}

/**
 * @brief 清空错误状态
 * 
 * 清除之前记录的错误消息，使 GetLastError() 返回空字符串
 * 通常在开始新操作之前调用
 * 
 * @throws ObjectDisposedException 如果解释器已被销毁
 */
void ABotInterpreter::ClearError() {
    if (handle == IntPtr::Zero) {
        throw gcnew ObjectDisposedException("ABotInterpreter");
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    abot_clear_error(native_handle);
}

/**
 * @brief 检查解释器是否已就绪
 * 
 * 检查是否已加载程序并可以执行
 * 通常在调用 ExecuteBattle 或 ExecuteScript 前检查
 * 
 * @return 如果解释器已然绪（已加载程序）返回 true，否则返回 false
 */
bool ABotInterpreter::IsReady() {
    if (handle == IntPtr::Zero) {
        return false;
    }
    
    ABOT_HANDLE native_handle = static_cast<ABOT_HANDLE>(handle.ToPointer());
    int result = abot_is_ready(native_handle);
    
    return result != 0;
}

/**
 * @brief 获取 ABOT 解释器的版本号
 * 
 * 返回 ABOT 解释器的版本号字符串
 * 这是一个静态方法，可以在不创建实例的情况下调用
 * 
 * @return 版本号字符串，格式通常为 "X.Y.Z-stage"（如 "0.1.0-alpha"）
 */
String^ ABotInterpreter::GetVersion() {
    const char* native_version = abot_get_version();
    return StringHelper::NativeToManaged(native_version);
}

}  // namespace CLI
}  // namespace ABot
