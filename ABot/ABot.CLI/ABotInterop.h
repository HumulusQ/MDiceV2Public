/**
 * @file ABotInterop.h
 * @brief C++/CLI 互操作层
 * 
 * 这个头文件定义了C++/CLI托管类，提供了从C#到C++ Core的桥接
 * 它包装了C_API函数，处理字符串编码转换等细节
 */

#pragma once

#using <mscorlib.dll>
#using <System.dll>

using namespace System;
using namespace System::Runtime::InteropServices;

namespace ABot {
namespace CLI {

/**
 * @brief 从托管字符串转换为本地UTF-8字符串
 */
ref class StringHelper {
public:
    static const char* ManagedToNative(String^ managed);
    static String^ NativeToManaged(const char* native);
    static void FreeNativeString(const char* ptr);
};

/**
 * @brief ABOT解释器的C++/CLI包装
 * 
 * 这个类提供了安全的托管接口到C++ Core ABotContext
 * 所有异常都被转换为托管异常
 */
public ref class ABotInterpreter {
public:
    /**
     * @brief 构造函数 - 创建新的ABOT上下文
     */
    ABotInterpreter();
    
    /**
     * @brief 析构函数 - 释放ABOT上下文
     */
    ~ABotInterpreter();
    
    /**
     * @brief 解析角色XML定义
     * @param characterXml 角色XML字符串
     * @return 成功返回true，失败返回false（检查GetLastError）
     */
    bool ParseCharacter(String^ characterXml);
    
    /**
     * @brief 注册技能集XML
     * @param skillsetXml 技能集XML字符串
     * @return 成功返回true，失败返回false
     */
    bool RegisterSkillset(String^ skillsetXml);
    
    /**
     * @brief 注册状态集XML
     * @param statesetXml 状态集XML字符串
     * @return 成功返回true，失败返回false
     */
    bool RegisterStateset(String^ statesetXml);
    
    /**
     * @brief 注册安科集XML
     * @param ankesetXml 安科集XML字符串
     * @return 成功返回true，失败返回false
     */
    bool RegisterANKESet(String^ ankesetXml);
    
    /**
     * @brief 执行战斗逻辑
     * @return 成功返回true，失败返回false
     */
    bool ExecuteBattle();
    
    /**
     * @brief 执行脚本代码
     * @param script 要执行的脚本代码
     * @return 成功返回true，失败返回false
     */
    bool ExecuteScript(String^ script);
    
    /**
     * @brief 获取最后一条错误消息
     * @return 错误消息字符串
     */
    String^ GetLastError();
    
    /**
     * @brief 清除错误状态
     */
    void ClearError();
    
    /**
     * @brief 获取解释器是否已就绪
     * @return 如果已加载程序，返回true
     */
    bool IsReady();
    
    /**
     * @brief 获取ABOT版本
     * @return 版本字符串
     */
    static String^ GetVersion();

private:
    /**
     * @brief 非托管ABOT_HANDLE
     */
    IntPtr handle;
};

}  // namespace CLI
}  // namespace ABot
