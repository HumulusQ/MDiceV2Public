/**
 * @file Bytecode.cpp
 * @brief ABOT 字节码编译器的实现
 */

#include "Bytecode.h"
#include <sstream>
#include <cstdio>

namespace abot {

// ============ BytecodeProgram实现 ============

void BytecodeProgram::Emit(Opcode op) {
    instructions.emplace_back(op);
}

void BytecodeProgram::Emit(Opcode op, int64_t arg) {
    Instruction instr(op);
    instr.arg_int = arg;
    instructions.push_back(instr);
}

void BytecodeProgram::Emit(Opcode op, double arg) {
    Instruction instr(op);
    instr.arg_double = arg;
    instructions.push_back(instr);
}

void BytecodeProgram::Emit(Opcode op, bool arg) {
    Instruction instr(op);
    instr.arg_bool = arg;
    instructions.push_back(instr);
}

void BytecodeProgram::Emit(Opcode op, const std::string& arg) {
    Instruction instr(op);
    instr.arg_string = arg;
    instructions.push_back(instr);
    
    // 【致命诊断】针对STORE_VAR和LOAD_VAR的强制日志
    if (op == Opcode::STORE_VAR || op == Opcode::LOAD_VAR) {
        const char* op_name = (op == Opcode::STORE_VAR) ? "STORE_VAR" : "LOAD_VAR";
        
        // ⚠️【关键】如果arg为空，这是一个严重的编译错误信号
        if (arg.empty()) {
            fprintf(stderr, "\n░░░ [🔴 FATAL EMISSION] IP:%zu | %s | ARG IS EMPTY ░░░\n",
                    instructions.size() - 1, op_name);
            fprintf(stderr, "    This means Emit() was called with empty string parameter!\n");
            fprintf(stderr, "    Current instruction count: %zu\n", instructions.size());
            fprintf(stderr, "░░░ END FATAL ░░░\n\n");
            fflush(stderr);
            
            // 标记这个Instruction为错误状态
            instr.arg_string = "[EMPTY_VAR_NAME_ERROR]";
        }
        
        // 常规诊断日志
        fprintf(stderr, "[BYTECODE_EMIT] IP:%zu | %s | arg='%s' | len=%zu | empty=%d\n",
                instructions.size() - 1,
                op_name,
                arg.c_str(),
                arg.length(),
                arg.empty() ? 1 : 0);
        fflush(stderr);
        
        // 输出到stdout（用于控制台捕捉）
        printf("[编译诊断] IP:%zu | %s | var='%s' | len=%zu\n",
                instructions.size() - 1,
                op_name,
                arg.c_str(),
                arg.length());
        fflush(stdout);
    }
}

void BytecodeProgram::Emit(Opcode op, uint32_t addr) {
    Instruction instr(op);
    instr.arg_addr = addr;
    instructions.push_back(instr);
}

void BytecodeProgram::Patch(uint32_t addr, uint32_t target_addr) {
    if (addr < instructions.size()) {
        instructions[addr].arg_addr = target_addr;
    }
}

// ============ BytecodeCompiler实现 ============

BytecodeCompiler::BytecodeCompiler()
    : program_(std::make_unique<BytecodeProgram>()), 
      has_error_(false) {
}

BytecodeCompiler::~BytecodeCompiler() {
}

std::unique_ptr<BytecodeProgram> BytecodeCompiler::Compile(
    const std::vector<std::unique_ptr<Statement>>& statements) {
    
    // 【强制诊断】记录AST信息
    fprintf(stderr, "\n╔════════════════════════════════════════════════════════════════╗\n");
    fprintf(stderr, "║ [BytecodeCompiler::Compile] 编译开始\n");
    fprintf(stderr, "║ 总语句数: %zu\n", statements.size());
    fprintf(stderr, "╚════════════════════════════════════════════════════════════════╝\n\n");
    fflush(stderr);
    
    FILE* log_file = nullptr;
    // fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    // if (log_file) fprintf(log_file, "[BytecodeCompiler::Compile] START - %zu statements\n", statements.size());
    
    for (size_t i = 0; i < statements.size(); i++) {
        fprintf(stderr, "[编译语句] %zu/%zu | 开始编译...\n", i+1, statements.size());
        fflush(stderr);
        
        // if (log_file) fprintf(log_file, "[BytecodeCompiler::Compile] Compiling statement %zu\n", i);
        // diag_stream << "[BytecodeCompiler::Compile] Compiling statement " << (i+1) << "/" << statements.size() << "\n";
        CompileStatement(statements[i].get());
        if (has_error_) {
            // diag_stream << "[BytecodeCompiler::Compile] ERROR during compilation: " << error_message_ << "\n";
            // fprintf(stderr, "[BytecodeCompiler::Compile] ERROR: %s\n", error_message_.c_str());
            // if (log_file) {
            //     fprintf(log_file, "[BytecodeCompiler::Compile] COMPILE ERROR: %s\n", error_message_.c_str());
            //     fclose(log_file);
            // }
            return nullptr;
        }
    }
    
    // 【修复v3】确保字节码顺序正确：TABLE_SET → SELF_COMMIT → RETURN → HALT
    // 如果最后的指令是POP，则删除它（防止多余的栈操作）
    if (!program_->instructions.empty()) {
        auto& last_instr = program_->instructions.back();
        if (last_instr.opcode == Opcode::POP) {
            // ★ 删除最后的POP：TABLE_SET已经正确处理了栈，SELF_COMMIT会从scope读
            // if (log_file) fprintf(log_file, "[BytecodeCompiler::Compile] Removing trailing POP instruction\n");
            program_->instructions.pop_back();
        }
    }
    
    // 【新增】在末尾添加SELF_COMMIT指令
    // SELF_COMMIT从scope读取最终修改后的self schema并写回env
    program_->Emit(Opcode::SELF_COMMIT);
    
    // 添加HALT指令作为终止
    program_->Emit(Opcode::HALT);
    
    // diag_stream << "[BytecodeCompiler::Compile] ========== COMPILATION FINISHED ==========\n";
    // diag_stream << "[BytecodeCompiler::Compile] Total instructions: " << program_->instructions.size() << "\n";
    // diag_stream << "[BytecodeCompiler::Compile] ========== ALL INSTRUCTIONS ==========\n";
    
    // 打印所有生成的指令
    // for (size_t i = 0; i < program_->instructions.size(); i++) {
    //     const auto& instr = program_->instructions[i];
    //     diag_stream << "[BytecodeCompiler::Compile] [" << i << "] opcode=" << (int)instr.opcode;
    //     if (!instr.arg_string.empty()) {
    //         diag_stream << " arg_string='" << instr.arg_string << "'";
    //     }
    //     if (instr.arg_int != 0) {
    //         diag_stream << " arg_int=" << instr.arg_int;
    //     }
    //     if (instr.arg_double != 0.0) {
    //         diag_stream << " arg_double=" << instr.arg_double;
    //     }
    //     diag_stream << "\n";
    // }
    // diag_stream << "[BytecodeCompiler::Compile] ========== END INSTRUCTIONS ==========\n";
    
    // 保存诊断信息到程序对象
    // program_->compilation_diagnostics = diag_stream.str();
    
    // if (log_file) {
    //     fprintf(log_file, "[BytecodeCompiler::Compile] FINISHED - %zu instructions generated\n", program_->instructions.size());
    //     // 打印所有生成的指令
    //     for (size_t i = 0; i < program_->instructions.size(); i++) {
    //         fprintf(log_file, "[BytecodeCompiler::Compile]  Instr %zu: opcode=%d\n", i, (int)program_->instructions[i].opcode);
    //     }
    //     fclose(log_file);
    // }
    
    return std::move(program_);
}

void BytecodeCompiler::Error(const std::string& message) {
    has_error_ = true;
    error_message_ = message;
}

void BytecodeCompiler::CompileStatement(const Statement* stmt) {
    if (!stmt) return;
    
    // 使用typeid和dynamic_cast来确定类型
    // 这是一个简化的实现
    if (auto if_stmt = dynamic_cast<const IfStatement*>(stmt)) {
        CompileIfStatement(if_stmt);
    } else if (auto for_stmt = dynamic_cast<const ForStatement*>(stmt)) {
        CompileForStatement(for_stmt);
    } else if (auto assign = dynamic_cast<const AssignmentStatement*>(stmt)) {
        CompileAssignmentStatement(assign);
    } else if (auto decl = dynamic_cast<const DeclarationStatement*>(stmt)) {
        CompileDeclarationStatement(decl);
    } else if (auto expr_stmt = dynamic_cast<const ExpressionStatement*>(stmt)) {
        if (expr_stmt->expression) {
            CompileExpression(expr_stmt->expression.get());
        }
    } else {
        Error("Unknown statement type");
    }
}

void BytecodeCompiler::CompileExpression(const Expression* expr) {
    if (!expr) return;
    
    if (auto binary = dynamic_cast<const BinaryOp*>(expr)) {
        CompileBinaryOp(binary);
    } else if (auto unary = dynamic_cast<const UnaryOp*>(expr)) {
        CompileUnaryOp(unary);
    } else if (auto literal = dynamic_cast<const Literal*>(expr)) {
        CompileLiteral(literal);
    } else if (auto var = dynamic_cast<const Variable*>(expr)) {
        CompileVariable(var);
    } else if (auto call = dynamic_cast<const FunctionCall*>(expr)) {
        CompileFunctionCall(call);
    } else if (auto member = dynamic_cast<const MemberAccess*>(expr)) {  // ✅ 新增：处理 MemberAccess
        CompileMemberAccess(member);
    } else {
        Error("Unknown expression type");
    }
}

void BytecodeCompiler::CompileIfStatement(const IfStatement* stmt) {
    // 编译条件
    if (stmt->condition) {
        CompileExpression(stmt->condition.get());
    }
    
    // JMP_IF_FALSE到else分支（或结束）
    uint32_t if_jump = program_->CurrentAddress();
    program_->Emit(Opcode::JMP_IF_FALSE, static_cast<uint32_t>(0));  // 先填0，待回溯填充
    
    // 编译then体
    for (const auto& s : stmt->then_body) {
        if (s) {
            CompileStatement(s.get());
        }
    }
    
    // 如果有elif或else，需要跳过它们
    std::vector<uint32_t> skip_jumps;  // if/elif结尾的跳转地址
    
    if (!stmt->elif_bodies.empty() || !stmt->else_body.empty()) {
        // if体结尾需要跳过elif/else
        skip_jumps.push_back(program_->CurrentAddress());
        program_->Emit(Opcode::JMP, static_cast<uint32_t>(0));  // 待回溯填充
    }
    
    // 回溯填充if_jump的目标
    program_->Patch(if_jump, program_->CurrentAddress());
    
    // 编译elif分支
    for (size_t i = 0; i < stmt->elif_bodies.size(); i++) {
        // 编译elif条件
        if (i < stmt->elif_conditions.size() && stmt->elif_conditions[i]) {
            CompileExpression(stmt->elif_conditions[i].get());
        }
        
        // JMP_IF_FALSE到下一个elif或else
        uint32_t elif_jump = program_->CurrentAddress();
        program_->Emit(Opcode::JMP_IF_FALSE, static_cast<uint32_t>(0));  // 待回溯填充
        
        // 编译elif体
        for (const auto& s : stmt->elif_bodies[i]) {
            if (s) {
                CompileStatement(s.get());
            }
        }
        
        // elif体结尾需要跳过后续elif/else
        skip_jumps.push_back(program_->CurrentAddress());
        program_->Emit(Opcode::JMP, static_cast<uint32_t>(0));
        
        // 回溯填充elif_jump的目标
        program_->Patch(elif_jump, program_->CurrentAddress());
    }
    
    // 编译else分支
    for (const auto& s : stmt->else_body) {
        if (s) {
            CompileStatement(s.get());
        }
    }
    
    // 回溯填充所有skip_jumps的目标到当前位置
    uint32_t end_addr = program_->CurrentAddress();
    for (auto addr : skip_jumps) {
        program_->Patch(addr, end_addr);
    }
}

void BytecodeCompiler::CompileForStatement(const ForStatement* stmt) {
    // 编译可迭代对象表达式
    // 在实际执行时，这应该返回一个容器/数组
    if (stmt->iterable) {
        CompileExpression(stmt->iterable.get());
    }
    
    // 记录循环开始位置
    // 注意：完整的for循环编译需要运行时支持迭代
    // 这里先生成循环体的代码，运行时再处理迭代逻辑
    
    uint32_t loop_start = program_->CurrentAddress();
    
    // 在实际VM中，需要特殊处理：
    // 1. 获取集合的下一个元素
    // 2. 设置迭代器变量
    // 3. 检查是否有更多元素
    // 4. 如果没有，跳过循环体
    
    // 简化实现：编译循环体
    for (const auto& s : stmt->body) {
        if (s) {
            CompileStatement(s.get());
        }
    }
    
    // 在完整实现中，这里应该有：
    // program_->Emit(Opcode::JMP, loop_start);  // 跳回循环开始
    // 需要IN opcode来完整处理for循环
}

void BytecodeCompiler::CompileAssignmentStatement(const AssignmentStatement* stmt) {
    FILE* log_file = nullptr;
    fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    if (log_file) fprintf(log_file, "[CompileAssignmentStatement] START - operator='%s'\n", stmt->op.c_str());
    
    if (!stmt || !stmt->target) {
        Error("Invalid assignment statement: null target");
        return;
    }

    // 检查目标表达式的类型
    if (auto var = dynamic_cast<const Variable*>(stmt->target.get())) {
        // ===== 情况1：简单变量赋值（set x = 10 或 set x += 5）=====
        if (log_file) fprintf(log_file, "[CompileAssignmentStatement] Simple variable: %s\n", var->name.c_str());
        
        if (stmt->op == "=") {
            // 纯赋值：编译值 → STORE_VAR
            if (stmt->value) {
                CompileExpression(stmt->value.get());
            }
            program_->Emit(Opcode::STORE_VAR, var->name);
        } else {
            // 复合赋值：LOAD_VAR → 编译值 → 操作 → STORE_VAR
            program_->Emit(Opcode::LOAD_VAR, var->name);
            if (stmt->value) {
                CompileExpression(stmt->value.get());
            }
            
            if (stmt->op == "+=") {
                program_->Emit(Opcode::ADD);
            } else if (stmt->op == "-=") {
                program_->Emit(Opcode::SUB);
            } else if (stmt->op == "*=") {
                program_->Emit(Opcode::MUL);
            } else if (stmt->op == "/=") {
                program_->Emit(Opcode::DIV);
            } else if (stmt->op == "%=") {
                program_->Emit(Opcode::MOD);
            } else {
                Error("Unknown operator: " + stmt->op);
                return;
            }
            
            program_->Emit(Opcode::STORE_VAR, var->name);
        }
        
    } else if (auto member = dynamic_cast<const MemberAccess*>(stmt->target.get())) {
        // ===== 情况2：成员访问赋值（set self.atk = 10）=====
        if (log_file) fprintf(log_file, "[CompileAssignmentStatement] MemberAccess detected\n");
        
        if (stmt->op == "=") {
            // ★【纯赋值：使用临时变量管理嵌套赋值】
            // 对于复杂情况（如值表达式包含self访问），使用临时变量避免栈混乱
            
            // 收集访问链深度
            std::vector<std::string> access_chain;
            const Expression* current = member->object.get();
            
            while (auto mem = dynamic_cast<const MemberAccess*>(current)) {
                access_chain.push_back(mem->member);
                current = mem->object.get();
            }
            std::reverse(access_chain.begin(), access_chain.end());
            
            bool is_self_based = false;
            if (auto var = dynamic_cast<const Variable*>(current)) {
                if (var->name == "self") {
                    is_self_based = true;
                }
            }
            
            if (is_self_based && access_chain.size() == 1) {
                // 【两层self赋值】set self.obj.field = value
                // 例如 set self.turn.multiplier = value
                
                fprintf(stderr, "[CompileAssignmentStatement] ⭐ PURE ASSIGNMENT PATH: Two-layer self assignment\n");
                fprintf(stderr, "                             access_chain[0]='%s', member->member='%s'\n", 
                        access_chain[0].c_str(), member->member.c_str());
                
                // Step 1: 获取目标对象
                program_->Emit(Opcode::LOAD_SELF);
                program_->Emit(Opcode::TABLE_ACCESS, access_chain[0]);  // self.obj
                fprintf(stderr, "[CompileAssignmentStatement] 📝 PURE STEP 1: STORE_VAR('__tmp_parent_obj__')\n");
                std::string parent_var_name = "__tmp_parent_obj__";
                program_->Emit(Opcode::STORE_VAR, parent_var_name);  // 保存obj到临时变量
                // 栈 = []
                
                // Step 2: 编译值表达式
                if (stmt->value) {
                    CompileExpression(stmt->value.get());
                }
                // 栈 = [value]  (MUL的结果)
                
                // ★【关键修复】保存value到临时变量，然后重新加载obj
                fprintf(stderr, "[CompileAssignmentStatement] 📝 PURE STEP 2: STORE_VAR('__tmp_value__') - 保存MUL结果\n");
                std::string save_value_name = "__tmp_value__";
                program_->Emit(Opcode::STORE_VAR, save_value_name);  // 保存value到临时变量
                // 栈 = []
                
                // Step 3: 恢复对象、修改字段、同步回self
                fprintf(stderr, "[CompileAssignmentStatement] 📂 PURE STEP 3: LOAD_VAR('__tmp_parent_obj__') - 恢复turn对象\n");
                std::string load_parent_name = "__tmp_parent_obj__";
                program_->Emit(Opcode::LOAD_VAR, load_parent_name);  // 加载obj
                // 栈 = [obj]
                
                fprintf(stderr, "[CompileAssignmentStatement] 📂 PURE STEP 4: LOAD_VAR('__tmp_value__') - 恢复计算结果\n");
                std::string load_value_name = "__tmp_value__";
                program_->Emit(Opcode::LOAD_VAR, load_value_name);  // 恢复value
                // 栈 = [obj, value] → TABLE_SET期望这个顺序
                
                program_->Emit(Opcode::TABLE_SET, member->member);  // obj.field = value
                // 栈 = [obj_modified]
                
                fprintf(stderr, "[CompileAssignmentStatement] 📝 PURE STEP 5: STORE_VAR('__tmp_modified_obj__')\n");
                std::string modified_obj_name = "__tmp_modified_obj__";
                program_->Emit(Opcode::STORE_VAR, modified_obj_name);  // 保存修改的obj
                // 栈 = []
                
                program_->Emit(Opcode::LOAD_SELF);  // 加载self
                fprintf(stderr, "[CompileAssignmentStatement] 📂 PURE STEP 6: LOAD_VAR('__tmp_modified_obj__')\n");
                std::string load_modified_name = "__tmp_modified_obj__";
                program_->Emit(Opcode::LOAD_VAR, load_modified_name);  // 加载修改的obj
                // 栈 = [self, obj_modified] → TABLE_SET_SELF期望这个顺序
                
                // 🔥 关键修复：使用TABLE_SET_SELF而不是TABLE_SET
                // TABLE_SET_SELF会同步修改到Scope和ExecutionEnvironment
                fprintf(stderr, "[CompileAssignmentStatement] ⭐ PURE STEP 7: TABLE_SET_SELF('%s') - 同步修改回self\n",
                        access_chain[0].c_str());
                program_->Emit(Opcode::TABLE_SET_SELF, access_chain[0]);  // self.obj = obj_modified (with sync)
                // 栈 = [self_modified]
                
                if (log_file) fprintf(log_file, "[CompileAssignmentStatement] Two-level self assignment with temp vars\n");
            } else {
                // 【其他情况】直接处理
                CompileExpressionForAssignmentTarget(member->object.get());
                
                if (stmt->value) {
                    CompileExpression(stmt->value.get());
                }
                
                program_->Emit(Opcode::TABLE_SET, member->member);
                
                if (log_file) fprintf(log_file, "[CompileAssignmentStatement] Simple/non-self assignment\n");
            }
        } else {
            // ✨ 复合赋值到字段：set self.atk += 10
            // 优化路径：针对 set self.field op= value 的特殊处理
            
            // 🔍 检测是否是"set self.field"模式（对象为Variable且名字为"self"）
            bool is_self_field = false;
            if (auto obj_var = dynamic_cast<const Variable*>(member->object.get())) {
                if (obj_var->name == "self") {
                    is_self_field = true;
                }
            }
            
            if (is_self_field && log_file) {
                fprintf(log_file, "[CompileAssignmentStatement] 🚀 FAST PATH DETECTED: set self.%s %s value (generating ~7 instructions)\n", 
                    member->member.c_str(), stmt->op.c_str());
            }
            
            if (is_self_field) {
                // ✅ 优化路径：直接栈操作，不使用临时变量
                // 目标：生成指令序列 LOAD_SELF -> TABLE_ACCESS -> value -> OP -> TABLE_SET
                
                // 1. LOAD_SELF (1指令)
                program_->Emit(Opcode::LOAD_SELF);
                // 栈：[schema]
                
                // 2. TABLE_ACCESS 获取当前字段值 (1指令)
                program_->Emit(Opcode::TABLE_ACCESS, member->member);
                // 栈：[current_value]
                
                // 3. 编译右侧值表达式 (N指令)
                if (stmt->value) {
                    CompileExpression(stmt->value.get());
                }
                // 栈：[current_value, rhs_value]
                
                // 4. 执行二元操作 (1指令)
                if (stmt->op == "+=") {
                    program_->Emit(Opcode::ADD);
                } else if (stmt->op == "-=") {
                    program_->Emit(Opcode::SUB);
                } else if (stmt->op == "*=") {
                    program_->Emit(Opcode::MUL);
                } else if (stmt->op == "/=") {
                    program_->Emit(Opcode::DIV);
                } else if (stmt->op == "%=") {
                    program_->Emit(Opcode::MOD);
                } else {
                    Error("Unknown operator: " + stmt->op);
                    return;
                }
                // 栈：[result]
                
                // 5. 立即保存 result 到临时变量 (1指令)
                //    这样可以清空栈，重新准备 [schema, result] 的顺序
                std::string tmp_result_store = "__tmp_result__";
                program_->Emit(Opcode::STORE_VAR, tmp_result_store);
                // 栈：[] (保存了 result)
                
                // 6. 重新加载 SELF 以获得 schema 引用 (1指令)
                program_->Emit(Opcode::LOAD_SELF);
                // 栈：[schema]
                
                // 7. 加载保存的 result 值 (1指令)
                std::string tmp_result_load = "__tmp_result__";
                program_->Emit(Opcode::LOAD_VAR, tmp_result_load);
                // 栈：[schema, result] ✓ 正确顺序：schema在下，result在上
                
                // 8. ★【关键】用 TABLE_SET_SELF 写回字段值 (1指令)
                //    TABLE_SET_SELF 不仅修改self，还同步回scope/env
                program_->Emit(Opcode::TABLE_SET_SELF, member->member);
                // 栈：[] (TABLE_SET_SELF弹出两个元素并写入完毕)
                
                if (log_file) {
                    fprintf(log_file, "[CompileAssignmentStatement] ✅ Fast path complete: LOAD_SELF -> TABLE_ACCESS -> [value] -> OP -> LOAD_SELF -> STORE_VAR -> LOAD_VAR -> TABLE_SET_SELF (~7 instructions)\n");
                }
                
            } else {
                // ❌ 通用路径：嵌套成员访问（例如 self.dmg.d1 += 1）
                
                if (log_file) fprintf(log_file, "[CompileAssignmentStatement] ⚠️  SLOW PATH: Nested member access compound assignment\n");
                
                // ★【最终方案的关键】
                // 对于 set self.dmg.d1 += 1，member = MemberAccess(Variable("self")/MemberAccess(...), "d1")
                // member->object = self.dmg
                // 
                // 策略：收集访问链（self.dmg -> dmg），然后逐层生成指令和回写
                
                // Step 1: 生成访问链来获取当前值
                // CompileExpressionForAssignmentTarget(member->object.get()) 会生成:
                //   LOAD_SELF → TABLE_ACCESS "dmg"  ← 得到 dmg 对象
                // 然后下面会再做:
                //   TABLE_ACCESS "d1"  ← 得到 d1 值
                CompileExpressionForAssignmentTarget(member->object.get());
                // 栈：[parent_object]  (dmg对象)
                
                program_->Emit(Opcode::TABLE_ACCESS, member->member);
                // 栈：[current_value]  (d1的当前值)
                
                // Step 2: 编译右侧值
                if (stmt->value) {
                    CompileExpression(stmt->value.get());
                }
                // 栈：[current_value, rhs_value]
                
                // Step 3: 执行操作
                if (stmt->op == "+=") {
                    program_->Emit(Opcode::ADD);
                } else if (stmt->op == "-=") {
                    program_->Emit(Opcode::SUB);
                } else if (stmt->op == "*=") {
                    program_->Emit(Opcode::MUL);
                } else if (stmt->op == "/=") {
                    program_->Emit(Opcode::DIV);
                } else if (stmt->op == "%=") {
                    program_->Emit(Opcode::MOD);
                } else {
                    Error("Unknown operator: " + stmt->op);
                    return;
                }
                // 栈：[result]
                
                // Step 4: 保存结果
                fprintf(stderr, "[CompileAssignmentStatement] 💾 STEP 4: 准备STORE_VAR('__tmp_nested__')\n");
                std::string tmp_nested_store = "__tmp_nested__";
                program_->Emit(Opcode::STORE_VAR, tmp_nested_store);
                // 栈：[]
                
                // Step 5: 重新生成访问链到父对象，准备 TABLE_SET
                //（这会生成LOAD_SELF, TABLE_ACCESS等，最后结果是[parent_obj]）
                fprintf(stderr, "[CompileAssignmentStatement] 🔄 STEP 5: CompileExpressionForAssignmentTarget for parent object\n");
                CompileExpressionForAssignmentTarget(member->object.get());
                // 栈：[parent_object]  (dmg对象)
                
                // Step 6: 加载结果值
                //（这会推new_value到栈顶，所以栈变成[parent_object, new_value]）
                fprintf(stderr, "[CompileAssignmentStatement] 📂 STEP 6: 准备LOAD_VAR('__tmp_nested__')\n");
                std::string tmp_nested_load = "__tmp_nested__";
                program_->Emit(Opcode::LOAD_VAR, tmp_nested_load);
                // 栈应为：[parent_object, new_value] 
                // 当TABLE_SET pop时：Pop1得new_value(value), Pop2得parent_object(obj)
                
                // Step 7: TABLE_SET 修改嵌套对象
                program_->Emit(Opcode::TABLE_SET, member->member);
                // 栈：[modified_parent_object]  (modified_dmg)
                // ★ 现在栈上是被修改过的dmg对象
                
                // Step 8: ★【关键】如果parent_object本身是MemberAccess，需要把modified_parent_object写回到它的父对象
                // 例如 self.dmg.d1 中，member->object 就是 self.dmg (MemberAccess)
                // 我们需要把 modified_dmg 写回到 self
                if (auto parent_member = dynamic_cast<const MemberAccess*>(member->object.get())) {
                    if (log_file) fprintf(log_file, "[CompileAssignmentStatement] [NESTED] Detected nested parent, generating parent TABLE_SET\n");
                    
                    fprintf(stderr, "[CompileAssignmentStatement] 🔲 NESTED DETECTED: member->object is MemberAccess\n");
                    fprintf(stderr, "                             parent_member->member = '%s'\n", parent_member->member.c_str());
                    
                    // 此时栈：[modified_parent_object]
                    // 需要变成：[grandparent_object, modified_parent_object]
                    // 然后 TABLE_SET 把 modified_parent_object 写回到 grandparent_object
                    
                    // 保存 modified_parent_object
                    fprintf(stderr, "[CompileAssignmentStatement] 📦 NESTED STEP 1: STORE_VAR('__tmp_modified_parent__')\n");
                    std::string tmp_modified_parent_store = "__tmp_modified_parent__";
                    program_->Emit(Opcode::STORE_VAR, tmp_modified_parent_store);
                    // 栈：[]
                    
                    // 生成对祖父对象的访问
                    fprintf(stderr, "[CompileAssignmentStatement] 🔄 NESTED STEP 2: CompileExpressionForAssignmentTarget for grandparent\n");
                    CompileExpressionForAssignmentTarget(parent_member->object.get());
                    // 栈：[grandparent_object]  (self对象)
                    
                    // 加载修改后的父对象
                    fprintf(stderr, "[CompileAssignmentStatement] 📂 NESTED STEP 3: LOAD_VAR('__tmp_modified_parent__')\n");
                    std::string tmp_modified_parent_load = "__tmp_modified_parent__";
                    program_->Emit(Opcode::LOAD_VAR, tmp_modified_parent_load);
                    // 栈：[grandparent_object, modified_parent_object]  (self, modified_dmg)
                    
                    // ★【关键改进】判断祖父对象是否是self，决定用TABLE_SET还是TABLE_SET_SELF
                    bool grandparent_is_self = false;
                    if (auto grandparent_var = dynamic_cast<const Variable*>(parent_member->object.get())) {
                        if (grandparent_var->name == "self") {
                            grandparent_is_self = true;
                        }
                    }
                    
                    if (grandparent_is_self) {
                        // 祖父对象就是self，用TABLE_SET_SELF（会同步scope/env）
                        program_->Emit(Opcode::TABLE_SET_SELF, parent_member->member);
                        if (log_file) fprintf(log_file, "[CompileAssignmentStatement] [NESTED] Using TABLE_SET_SELF for self.%s\n", parent_member->member.c_str());
                    } else {
                        // 祖父对象不是self，用普通TABLE_SET
                        program_->Emit(Opcode::TABLE_SET, parent_member->member);
                        if (log_file) fprintf(log_file, "[CompileAssignmentStatement] [NESTED] Using TABLE_SET for %s\n", parent_member->member.c_str());
                    }
                    // 栈：[modified_grandparent_object]  (modified_self)
                    // ★ 现在栈上是被完整修改过的self对象或子对象，可以传给后续操作
                }
                
                if (log_file) {
                    fprintf(log_file, "[CompileAssignmentStatement] Emitted nested compound assignment with parent writebacks\n");
                }
            }
        }
        
    } else {
        Error("Assignment target must be Variable or MemberAccess");
        return;
    }
    
    if (log_file) {
        fprintf(log_file, "[CompileAssignmentStatement] FINISHED - %zu instructions\n", program_->instructions.size());
        fclose(log_file);
    }
}


/**
 * TABLE_SET字节码生成说明：
 * 
 * 对于语句: set self.atk += 10
 * 编译后的字节码序列为:
 * 
 * 1. LOAD_VAR "self"        - 加载self对象到栈
 * 2. LOAD_VAR "self"        - (复合赋值时)加载self对象获取atk
 * 3. TABLE_ACCESS "atk"     - 获取self.atk的值
 * 4. LOAD_INT 10            - 加载10到栈
 * 5. ADD                    - 执行加法
 * 6. TABLE_SET "atk"        - 设置self.atk为新值
 * 
 * 栈状态演变:
 * 初始: []
 * LOAD_VAR "self" -> [self]
 * (对于复合赋值 += :)
 *   LOAD_VAR "self" -> [self, self]
 *   TABLE_ACCESS "atk" -> [self, atk_value]
 *   LOAD_INT 10 -> [self, atk_value, 10]
 *   ADD -> [self, atk_value+10]
 * TABLE_SET "atk" -> setfield(self, "atk", atk_value+10)
 */

void BytecodeCompiler::CompileDeclarationStatement(const DeclarationStatement* stmt) {
    if (stmt->value) {
        CompileExpression(stmt->value.get());
    }
    program_->Emit(Opcode::STORE_VAR, stmt->name);
}

void BytecodeCompiler::CompileBinaryOp(const BinaryOp* expr) {
    // 编译左操作数
    if (expr->left) {
        CompileExpression(expr->left.get());
    }
    
    // 编译右操作数
    if (expr->right) {
        CompileExpression(expr->right.get());
    }
    
    // 根据操作符生成指令
    if (expr->op == "+") {
        program_->Emit(Opcode::ADD);
    } else if (expr->op == "-") {
        program_->Emit(Opcode::SUB);
    } else if (expr->op == "*") {
        program_->Emit(Opcode::MUL);
    } else if (expr->op == "/") {
        program_->Emit(Opcode::DIV);
    } else if (expr->op == "%") {
        program_->Emit(Opcode::MOD);
    } else if (expr->op == "==") {
        program_->Emit(Opcode::CMP_EQ);
    } else if (expr->op == "!=") {
        program_->Emit(Opcode::CMP_NE);
    } else if (expr->op == "<") {
        program_->Emit(Opcode::CMP_LT);
    } else if (expr->op == "<=") {
        program_->Emit(Opcode::CMP_LE);
    } else if (expr->op == ">") {
        program_->Emit(Opcode::CMP_GT);
    } else if (expr->op == ">=") {
        program_->Emit(Opcode::CMP_GE);
    } else if (expr->op == "&&") {
        program_->Emit(Opcode::AND);
    } else if (expr->op == "||") {
        program_->Emit(Opcode::OR);
    } else {
        Error("Unknown binary operator: " + expr->op);
    }
}

void BytecodeCompiler::CompileUnaryOp(const UnaryOp* expr) {
    if (expr->operand) {
        CompileExpression(expr->operand.get());
    }
    
    if (expr->op == "!") {
        program_->Emit(Opcode::NOT);
    } else if (expr->op == "-") {
        program_->Emit(Opcode::LOAD_INT, static_cast<int64_t>(-1));
        program_->Emit(Opcode::MUL);
    } else {
        Error("Unknown unary operator: " + expr->op);
    }
}

void BytecodeCompiler::CompileLiteral(const Literal* expr) {
    FILE* log_file = nullptr;
    fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    
    const Value& v = expr->value;
    
    if (v.IsInt()) {
        int64_t intVal = v.GetInt();
        program_->Emit(Opcode::LOAD_INT, intVal);
        if (log_file) fprintf(log_file, "[CompileLiteral] LOAD_INT %lld, total instr=%zu\n", intVal, program_->instructions.size());
    } else if (v.IsDouble()) {
        program_->Emit(Opcode::LOAD_DOUBLE, v.GetDouble());
        if (log_file) fprintf(log_file, "[CompileLiteral] LOAD_DOUBLE\n");
    } else if (v.IsBool()) {
        program_->Emit(Opcode::LOAD_BOOL, v.GetBool());
        if (log_file) fprintf(log_file, "[CompileLiteral] LOAD_BOOL\n");
    } else if (v.IsString()) {
        program_->Emit(Opcode::LOAD_STRING, v.GetString());
        if (log_file) fprintf(log_file, "[CompileLiteral] LOAD_STRING\n");
    } else if (v.IsNull()) {
        program_->Emit(Opcode::LOAD_NULL);
        if (log_file) fprintf(log_file, "[CompileLiteral] LOAD_NULL\n");
    } else {
        Error("Unsupported literal type");
        if (log_file) fprintf(log_file, "[CompileLiteral] ERROR: Unsupported type\n");
    }
    
    if (log_file) fclose(log_file);
}

void BytecodeCompiler::CompileVariable(const Variable* expr) {
    // 检查特殊变量（支持小写和大写）
    FILE* log_file = nullptr;
    fopen_s(&log_file, "C:\\Windows\\Temp\\abot_cpp_debug.log", "at");
    if (log_file) fprintf(log_file, "[CompileVariable] START - expr->name='%s' (checking for special vars)\n", expr->name.c_str());
    
    if (expr->name == "para") {
        program_->Emit(Opcode::LOAD_PARA);
        if (log_file) fprintf(log_file, "[CompileVariable] Emitted LOAD_PARA\n");
    } else if (expr->name == "message") {
        program_->Emit(Opcode::LOAD_MESSAGE);
        if (log_file) fprintf(log_file, "[CompileVariable] Emitted LOAD_MESSAGE\n");
    } else if (expr->name == "Self" || expr->name == "self") {  // ✅ 支持小写 self
        program_->Emit(Opcode::LOAD_SELF);
        if (log_file) fprintf(log_file, "[CompileVariable] Emitted LOAD_SELF for '%s'\n", expr->name.c_str());
    } else if (expr->name == "Enemy" || expr->name == "enemy") {  // ✅ 支持小写 enemy
        program_->Emit(Opcode::LOAD_ENEMY);
        if (log_file) fprintf(log_file, "[CompileVariable] Emitted LOAD_ENEMY for '%s'\n", expr->name.c_str());
    } else if (expr->name == "Aliases" || expr->name == "aliases") {  // ✅ 支持小写 aliases
        program_->Emit(Opcode::LOAD_ALLIES);
        if (log_file) fprintf(log_file, "[CompileVariable] Emitted LOAD_ALLIES for '%s'\n", expr->name.c_str());
    } else {
        program_->Emit(Opcode::LOAD_VAR, expr->name);
        if (log_file) fprintf(log_file, "[CompileVariable] Emitted LOAD_VAR '%s'\n", expr->name.c_str());
    }
    
    if (log_file) fclose(log_file);
}

void BytecodeCompiler::CompileFunctionCall(const FunctionCall* expr) {
    // 编译参数：将每个参数表达式编译，结果存储到环境的临时属性
    // arg0, arg1, arg2, ... 和 argc（参数个数）
    
    for (size_t i = 0; i < expr->arguments.size(); i++) {
        if (expr->arguments[i]) {
            CompileExpression(expr->arguments[i].get());
        }
    }
    
    // 发出参数个数指令（作为整数加载）
    program_->Emit(Opcode::LOAD_INT, static_cast<int64_t>(expr->arguments.size()));
    
    // CRITICAL FIX: 明确构造 std::string，避免临时引用问题
    std::string argc_name = "__argc__";
    program_->Emit(Opcode::STORE_VAR, argc_name);
    
    // 从栈中弹出所有参数并存储到环境的 arg0, arg1, ... 中
    for (int i = static_cast<int>(expr->arguments.size()) - 1; i >= 0; i--) {
        std::string arg_name = "__arg" + std::to_string(i) + "__";
        program_->Emit(Opcode::STORE_VAR, arg_name);
    }
    
    // 发出函数调用指令
    program_->Emit(Opcode::CALL, expr->name);
}

void BytecodeCompiler::CompileMemberAccess(const MemberAccess* expr) {
    // ✅ 编译的逻辑：
    // 1. 首先编译对象表达式（这会将对象压入栈）
    // 2. 然后发出 TABLE_ACCESS 指令来获取成员值
    
    if (expr->object) {
        CompileExpression(expr->object.get());
    }
    
    // 发出TABLE_ACCESS指令，参数是成员名称
    program_->Emit(Opcode::TABLE_ACCESS, expr->member);
}

/**
 * 辅助方法：编译赋值目标的对象部分
 * 例如：对于 set self.Dmg.d1 = x，这编译 self.Dmg 部分
 *       对于 set self.atk = x，这编译 self 部分
 * 
 * 对于 MemberAccess 链，递归编译对象表达式，最后一层会被 TABLE_SET 使用
 */
void BytecodeCompiler::CompileExpressionForAssignmentTarget(const Expression* expr) {
    if (!expr) {
        return;
    }
    
    // ★【最终方案】完整的访问链生成
    // 递归生成访问链中的每一个 TABLE_ACCESS 指令
    // 
    // 例如对于 self.dmg.d1：
    //   CompileExpressionForAssignmentTarget(self.dmg)
    //   → CompileExpressionForAssignmentTarget(self)
    //     → CompileExpression(self) = LOAD_SELF → [self]
    //   → Emit TABLE_ACCESS "dmg" → [dmg]
    //
    // 调用者会再做 TABLE_ACCESS "d1" 来获取最终的字段值
    
    if (auto member = dynamic_cast<const MemberAccess*>(expr)) {
        // 递归处理链中的前一层
        CompileExpressionForAssignmentTarget(member->object.get());
        // ★【关键修复】从 TABLE_ACCESS 生成
        // 这样能完整表示访问链的每一层
        program_->Emit(Opcode::TABLE_ACCESS, member->member);
        
    } else {
        // 基础表达式（Variable 或 Identifier）
        CompileExpression(expr);
    }
}

}  // namespace abot
