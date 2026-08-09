/**
 * @file VM.h
 * @brief ABOT 虚拟机 - 执行字节码
 * 
 * 虚拟机架构：
 * ============
 * - 操作数栈：存储中间计算结果
 * - 调用栈：存储函数调用信息
 * - 指令指针：当前执行位置
 * - 作用域栈：管理变量作用域
 */

#ifndef ABOT_VM_H
#define ABOT_VM_H

#include "Bytecode.h"
#include "Scope.h"
#include <stack>
#include <map>
#include <memory>

namespace abot {

// 前向声明
class ABotContext;

/**
 * @brief 虚拟机调用帧
 * 代表一次函数调用
 */
struct CallFrame {
    Instruction* return_address;
    ScopeStack* scope;
};

/**
 * @brief ABOT虚拟机
 * 执行编译后的字节码
 */
class VM {
public:
    // ============ 构造函数 ============
    VM();
    ~VM();

    // ============ 执行方法 ============
    
    /**
     * @brief 执行字节码程序
     */
    bool Execute(const BytecodeProgram* program, ScopeStack* scope);

    /**
     * @brief 执行单条指令
     */
    bool ExecuteInstruction(const Instruction& instr);

    // ============ 栈操作 ============
    
    Value Pop();
    void Push(const Value& value);
    Value Peek() const;

    // ============ 错误处理 ============
    
    bool HasError() const { return has_error_; }
    std::string GetErrorMessage() const { return error_message_; }

    // ============ 状态检查 ============
    
    bool IsRunning() const { return is_running_; }
    uint32_t GetInstructionPointer() const { return ip_; }

private:
    const BytecodeProgram* program_;
    ScopeStack* scope_;
    
    std::stack<Value> value_stack_;
    std::stack<CallFrame> call_stack_;
    
    uint32_t ip_;  // 指令指针
    bool is_running_;
    bool halted_;
    bool has_error_;
    std::string error_message_;

    // ============ 执行助手 ============
    
    void Error(const std::string& message);
    void Halt();

    // 指令处理方法
    void HandleLoadInt(int64_t value);
    void HandleLoadDouble(double value);
    void HandleLoadBool(bool value);
    void HandleLoadString(const std::string& value);
    void HandleLoadVar(const std::string& name);
    void HandleStoreVar(const std::string& name);
    
    void HandleAdd();
    void HandleSub();
    void HandleMul();
    void HandleDiv();
    void HandleMod();
    
    void HandleJmp(uint32_t address);
    void HandleJmpIfFalse(uint32_t address);
    void HandleJmpIfTrue(uint32_t address);
    
    void HandleReturn();
    void HandleHalt();
};

}  // namespace abot

#endif  // ABOT_VM_H
