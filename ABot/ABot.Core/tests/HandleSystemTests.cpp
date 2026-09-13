/**
 * @file HandleSystemTests.cpp
 * @brief Handle 系统 PoC 的单元和集成测试
 * 
 * 测试目标:
 * 1. 验证当前的深拷贝问题（UT-DeepCopyIssue）
 * 2. 验证 Handle 方案是否解决问题（待实现）
 * 3. 验证 Handle 模式与 Legacy 模式的行为一致性（IT-HandleVsLegacy）
 */

#include <gtest/gtest.h>
#include <memory>
#include "../src/Value.h"
#include "../src/VM.h"
#include "../src/Scope.h"

using namespace abot;

/**
 * ============================================================================
 * 单元测试：验证当前的深拷贝语义
 * ============================================================================
 */

class ValueDeepCopyTests : public ::testing::Test {
protected:
    void SetUp() override {
        // 初始化测试 SchemaValue
        initial_schema_ = Value::CreateSchema();
        initial_schema_.SetField("atk", Value(10));
        initial_schema_.SetField("hp", Value(50));
    }
    
    Value initial_schema_;
};

/**
 * UT-DeepCopyBehavior: 验证 Value 复制确实进行深拷贝
 */
TEST_F(ValueDeepCopyTests, DeepCopyBehavior) {
    // 准备: 创建一个 Schema Value
    Value v1 = initial_schema_;
    
    // 操作: 拷贝该 Value（模拟 Pop/Push）
    Value v2 = v1;
    
    // 修改 v2 中的字段
    v2.SetField("atk", Value(100));
    
    // 验证: v1 的字段应该不变（深拷贝的证明）
    EXPECT_EQ(v1.GetField("atk").ToInt(), 10)
        << "Deep copy: v1 should not be affected by v2 modification";
    EXPECT_EQ(v2.GetField("atk").ToInt(), 100)
        << "v2 should have the new value";
}

/**
 * UT-NestedDeepCopy: 验证嵌套 Schema 的深拷贝
 */
TEST_F(ValueDeepCopyTests, NestedDeepCopy) {
    // 准备: 创建嵌套 Schema { atk: 10, dmg: { d1: 1, d2: 3 } }
    Value dmg = Value::CreateSchema();
    dmg.SetField("d1", Value(1));
    dmg.SetField("d2", Value(3));
    
    Value parent = Value::CreateSchema();
    parent.SetField("atk", Value(10));
    parent.SetField("dmg", dmg);
    
    // 操作: 拷贝 parent（深拷贝应递归）
    Value parent_copy = parent;
    
    // 修改 copy 的嵌套字段
    Value dmg_copy = parent_copy.GetField("dmg");
    dmg_copy.SetField("d1", Value(99));
    parent_copy.SetField("dmg", dmg_copy);
    
    // 验证: 原始 parent 不应改变
    Value original_dmg = parent.GetField("dmg");
    EXPECT_EQ(original_dmg.GetField("d1").ToInt(), 1)
        << "Deep copy: nested field in original should not change";
    
    Value copied_dmg = parent_copy.GetField("dmg");
    EXPECT_EQ(copied_dmg.GetField("d1").ToInt(), 99)
        << "Copied nested field should have new value";
}

/**
 * UT-StackOperationDeepCopy: 模拟 VM 栈操作，验证深拷贝问题
 * 
 * 这个测试演示了问题：
 * 在 ScopeStack 中存储的 Schema 与 VM 栈上的 Schema 是不同的对象，
 * 所以对栈的修改不会影响 ScopeStack。
 */
TEST_F(ValueDeepCopyTests, StackOperationDeepCopyProblem) {
    // 模拟 ScopeStack
    std::map<std::string, Value> scope_map;
    scope_map["self"] = initial_schema_;  // self → { atk:10, hp:50 }
    
    // 模拟 VM 栈
    std::vector<Value> vm_stack;
    
    // 模拟 LOAD_SELF: Pop from ScopeStack → Push to VM Stack
    vm_stack.push_back(scope_map["self"]);  // ← 这里触发深拷贝！
    
    // 此时:
    // - scope_map["self"] 指向原始 SchemaValue A
    // - vm_stack[0] 指向副本 SchemaValue A' (深拷贝)
    
    Value from_vm_stack = vm_stack.back();
    from_vm_stack.SetField("atk", Value(999));  // 修改副本
    vm_stack.back() = from_vm_stack;
    
    // 问题：此时 scope_map 中的值未改变
    EXPECT_EQ(scope_map["self"].GetField("atk").ToInt(), 10)
        << "scopeStack should NOT see the modification (it's a deep copy)";
    
    EXPECT_EQ(vm_stack.back().GetField("atk").ToInt(), 999)
        << "VM stack should have the modification";
    
    // 这正是 turn.multiplier 问题的根源！
    // Script 修改 = 修改 vm_stack 上的副本
    // from_schema() 回调 = 从 scope_map（原始）读取 → 看不到修改
}

/**
 * ============================================================================
 * 集成测试：Multiplier 同步问题演现
 * ============================================================================
 */

class MultiiplierSyncTests : public ::testing::Test {
protected:
    void SetUp() override {
        // 创建模拟的 turn schema
        Value turn = Value::CreateSchema();
        turn.SetField("multiplier", Value(1.0));
        
        actor_schema_ = Value::CreateSchema();
        actor_schema_.SetField("atk", Value(9));
        actor_schema_.SetField("turn", turn);
    }
    
    Value actor_schema_;
};

/**
 * IT-TurnMultiplierIssue: 演现 turn.multiplier 同步问题
 * 
 * 这个测试复现了 ES (大成功) 脚本的问题：
 * set self.turn.multiplier = self.turn.multiplier * 2;
 * 
 * 脚本执行后，multiplier 应该是 2.0，但实际上仍是 1.0
 */
TEST_F(MultiiplierSyncTests, TurnMultiplierPersistenceIssue) {
    // 模拟 ScopeStack（执行环境初始化）
    std::map<std::string, Value> scope_map;
    scope_map["self"] = actor_schema_;
    
    // 模拟脚本执行：set self.turn.multiplier = self.turn.multiplier * 2;
    // 编译为字节码（简化表示）：
    // [0] LOAD_SELF              → 栈: [self_schema]
    // [1] TABLE_ACCESS 'turn'    → 栈: [turn_schema]
    // [2] TABLE_ACCESS 'mult'    → 栈: [1.0]
    // [3] CONST 2                → 栈: [2, 1.0]
    // [4] MULTIPLY               → 栈: [2.0]
    // [5] TABLE_SET 'mult'       → 栈: [turn_schema']  (modified)
    // [6] TABLE_SET_SELF 'turn'  → 栈: [self_schema'] (modified, but ...)
    
    std::vector<Value> vm_stack;
    
    // [0] LOAD_SELF
    vm_stack.push_back(scope_map["self"]);  // ← 深拷贝触发！
    
    // [1] TABLE_ACCESS 'turn'
    Value self_on_stack = vm_stack.back();
    vm_stack.pop_back();
    Value turn_on_stack = self_on_stack.GetField("turn");
    vm_stack.push_back(turn_on_stack);  // ← 又是深拷贝！
    
    // [2] TABLE_ACCESS 'multiplier'
    turn_on_stack = vm_stack.back();
    vm_stack.pop_back();
    Value mult_value = turn_on_stack.GetField("multiplier");
    vm_stack.push_back(mult_value);
    
    // [3] CONST 2
    vm_stack.push_back(Value(2.0));
    
    // [4] MULTIPLY
    Value operand2 = vm_stack.back(); vm_stack.pop_back();
    Value operand1 = vm_stack.back(); vm_stack.pop_back();
    Value result = operand1 * operand2;
    vm_stack.push_back(result);  // 栈: [2.0]
    
    // [5] TABLE_SET 'multiplier'
    Value new_mult = vm_stack.back(); vm_stack.pop_back();
    turn_on_stack = vm_stack.back(); vm_stack.pop_back();
    turn_on_stack.SetField("multiplier", new_mult);
    vm_stack.push_back(turn_on_stack);  // 栈: [turn_schema with mult=2.0]
    
    // [6] TABLE_SET_SELF 'turn'
    Value modified_turn = vm_stack.back(); vm_stack.pop_back();
    self_on_stack = vm_stack.back(); vm_stack.pop_back();
    self_on_stack.SetField("turn", modified_turn);
    vm_stack.push_back(self_on_stack);
    
    // 问题：此时 scope_map["self"] 仍未改变！
    // 因为 scope_map 持有的是**原始**的 self_schema，
    // 而我们修改的是 vm_stack 上面的**副本**
    
    Value final_self = scope_map["self"];
    Value final_turn = final_self.GetField("turn");
    Value final_mult = final_turn.GetField("multiplier");
    
    EXPECT_EQ(final_mult.ToDouble(), 1.0)  // ✗ 应该是 2.0，但深拷贝导致失败
        << "Current Deep-Copy Issue: multiplier should be 2.0 but remains 1.0";
}

/**
 * IT-CorrectBehaviorExpectation: 未来的正确行为期望
 * 
 * 一旦 Handle 系统实现，这个测试应该通过
 */
TEST_F(MultiiplierSyncTests, DISABLED_CorrectHandleBehavior) {
    // 这个测试目前被禁用，因为 Handle 系统还未实现
    // 一旦实现，应该改为 Handle 路径，此时所有修改都作用于同一个对象
    
    GTEST_SKIP() << "Handle system not yet implemented";
}

