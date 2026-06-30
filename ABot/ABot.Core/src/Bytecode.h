/**
 * @file Bytecode.h
 * @brief ABOT 字节码编译器 - 将AST转换为字节码
 * 
 * 字节码指令集：
 * ==============
 * - 加载/存储：LOAD_INT, LOAD_VAR, STORE_VAR等
 * - 算术：ADD, SUB, MUL, DIV等
 * - 栈操作：PUSH, POP等
 * - 控制流：JMP, JMP_IF_FALSE等
 * - 函数：CALL, RETURN等
 * - 特殊：DICE_ROLL, TABLE_ACCESS等
 */

#ifndef ABOT_BYTECODE_H
#define ABOT_BYTECODE_H

#include "Parser.h"
#include "Value.h"
#include <cstdint>
#include <vector>
#include <memory>

namespace abot {

// 字节码操作码
enum class Opcode : uint8_t {
    // 字面量加载
    LOAD_INT,       // arg: int64_t值
    LOAD_DOUBLE,    // arg: double值
    LOAD_BOOL,      // arg: bool值
    LOAD_STRING,    // arg: string值
    LOAD_NULL,      // no arg
    
    // 变量操作
    LOAD_VAR,       // arg: 变量名
    STORE_VAR,      // arg: 变量名
    
    // 栈操作
    POP,            // no arg
    DUP,            // no arg - 复制栈顶
    
    // 算术操作
    ADD,            // no arg
    SUB,            // no arg
    MUL,            // no arg
    DIV,            // no arg
    MOD,            // no arg
    
    // 逻辑操作
    AND,            // no arg
    OR,             // no arg
    NOT,            // no arg
    
    // 比较操作
    CMP_EQ,         // no arg
    CMP_NE,         // no arg
    CMP_LT,         // no arg
    CMP_LE,         // no arg
    CMP_GT,         // no arg
    CMP_GE,         // no arg
    
    // 控制流
    JMP,            // arg: 目标指令地址
    JMP_IF_FALSE,   // arg: 目标指令地址
    JMP_IF_TRUE,    // arg: 目标指令地址
    
    // 函数调用
    CALL,           // arg: 函数名
    RETURN,         // no arg
    
    // 骰子操作
    DICE_ROLL,      // no arg
    
    // 集合操作
    TABLE_ACCESS,   // arg: 键名
    TABLE_SET,      // arg: 键名 - 修改栈上对象的字段，不同步scope/env
    TABLE_SET_SELF, // arg: 键名 - 修改self的字段，并同步回scope/env
    
    // 参数访问
    LOAD_PARA,      // no arg - 加载技能参数Schema
    LOAD_MESSAGE,   // no arg - 加载触发消息Schema
    LOAD_SELF,      // no arg - 加载作用者(Self)
    LOAD_ENEMY,     // no arg - 加载目标(Enemy)
    LOAD_ALLIES,    // no arg - 加载同方角色列表(Allies)
    
    // 【新增】显式提交修改
    SELF_COMMIT,    // no arg - 将栈顶的self schema写回到scope和env
    
    // 其他
    NOOP,           // no arg - 空操作
    HALT,           // no arg - 停止执行
};

/**
 * @brief 单个字节码指令
 */
struct Instruction {
    Opcode opcode;
    std::string arg_string;     // 字符串参数（变量名、函数名等）
    union {
        int64_t arg_int;
        double arg_double;
        bool arg_bool;
        uint32_t arg_addr;      // 地址参数
    };
    
    Instruction(Opcode op) : opcode(op), arg_int(0) {}
};

/**
 * @brief 字节码程序
 * 包含一系列指令和常量表
 */
class BytecodeProgram {
public:
    std::vector<Instruction> instructions;
    std::vector<Value> constants;
    std::string compilation_diagnostics;  // 编译诊断信息
    
    // 增加指令
    void Emit(Opcode op);
    void Emit(Opcode op, int64_t arg);
    void Emit(Opcode op, double arg);
    void Emit(Opcode op, bool arg);
    void Emit(Opcode op, const std::string& arg);
    void Emit(Opcode op, uint32_t addr);
    
    // 获取当前指令地址
    uint32_t CurrentAddress() const {
        return static_cast<uint32_t>(instructions.size());
    }
    
    // Patch：修改已发出的指令的参数（用于回溯填充地址）
    void Patch(uint32_t addr, uint32_t target_addr);
};

/**
 * @brief 字节码编译器
 * 将AST转换为字节码
 */
class BytecodeCompiler {
public:
    // ============ 构造函数 ============
    BytecodeCompiler();
    ~BytecodeCompiler();

    // ============ 编译方法 ============
    
    /**
     * @brief 编译AST为字节码
     */
    std::unique_ptr<BytecodeProgram> Compile(
        const std::vector<std::unique_ptr<Statement>>& statements
    );

    // ============ 错误处理 ============
    
    bool HasError() const { return has_error_; }
    std::string GetErrorMessage() const { return error_message_; }

private:
    std::unique_ptr<BytecodeProgram> program_;
    bool has_error_;
    std::string error_message_;

    // ============ 编译助手 ============
    
    void Error(const std::string& message);
    
    void CompileStatement(const Statement* stmt);
    void CompileExpression(const Expression* expr);
    
    void CompileIfStatement(const IfStatement* stmt);
    void CompileForStatement(const ForStatement* stmt);
    void CompileAssignmentStatement(const AssignmentStatement* stmt);
    void CompileDeclarationStatement(const DeclarationStatement* stmt);
    
    void CompileBinaryOp(const BinaryOp* expr);
    void CompileUnaryOp(const UnaryOp* expr);
    void CompileLiteral(const Literal* expr);
    void CompileVariable(const Variable* expr);
    void CompileFunctionCall(const FunctionCall* expr);
    void CompileMemberAccess(const MemberAccess* expr);  // ✅ 新增：编译成员访问
    
    // ============ 赋值语句辅助方法 ============
    void CompileExpressionForAssignmentTarget(const Expression* expr);  // 编译赋值目标的对象部分
};

}  // namespace abot

#endif  // ABOT_BYTECODE_H
