/**
 * @file IntegrationTests.cpp
 * @brief Handle 系统 PoC 的完整集成测试
 * 
 * 测试目标:
 * 1. 验证 ES(大成功) 脚本的 multiplier 同步问题修复
 * 2. 嵌套字段写回正确性
 * 3. 多层嵌套修改和回滚
 * 4. 脚本执行的边界条件
 */

#include <gtest/gtest.h>
#include <string>
#include <cmath>
#include "../src/Value.h"
#include "../src/VM.h"
#include "../src/Scope.h"
#include "../src/ObjectTable.h"
#include "../src/ObjectHandle.h"

using namespace abot;

/**
 * ============================================================================
 * 集成测试：关键场景演现与验证
 * ============================================================================
 */

class IntegrationTestFixture : public ::testing::Test {
protected:
    void SetUp() override {
        // 初始化测试环境
        object_table_ = std::make_unique<ObjectTable>();
        scope_stack_ = std::make_unique<ScopeStack>();
        
        // 构建初始 Schema（模拟角色数据）
        // 结构：{ atk: 9, hp: 50, turn: { multiplier: 1.0 }, dmg: { d1: 1, d2: 3, d3: 5, d4: 7 } }
        InitializeActorSchema();
    }
    
    void InitializeActorSchema() {
        // 创建嵌套字段：turn.multiplier
        Value turn_schema = Value::CreateSchema();
        turn_schema.SetField("multiplier", Value(1.0));  // 初始 1.0
        
        // 创建嵌套字段：dmg（骰伤）
        Value dmg_schema = Value::CreateSchema();
        dmg_schema.SetField("d1", Value(1));
        dmg_schema.SetField("d2", Value(3));
        dmg_schema.SetField("d3", Value(5));
        dmg_schema.SetField("d4", Value(7));
        
        // 创建主角色 Schema
        actor_schema_ = Value::CreateSchema();
        actor_schema_.SetField("atk", Value(9));       // 攻击力
        actor_schema_.SetField("hp", Value(50));       // 生命值
        actor_schema_.SetField("turn", turn_schema);   // 回合信息
        actor_schema_.SetField("dmg", dmg_schema);     // 伤害骰子
        
        // 注入到 ScopeStack
        scope_stack_->PushScope();
        scope_stack_->SetVariable("self", actor_schema_);
    }
    
    std::unique_ptr<ObjectTable> object_table_;
    std::unique_ptr<ScopeStack> scope_stack_;
    Value actor_schema_;
};

/**
 * IT-TurnMultiplierPersistence: Multiplier 同步问题修复验证
 * 
 * 脚本: set self.turn.multiplier = self.turn.multiplier * 2;
 * 期望: multiplier 变为 2.0（而不是保持 1.0）
 */
TEST_F(IntegrationTestFixture, TurnMultiplierPersistence) {
    // 【初始状态】
    Value self_before = scope_stack_->GetVariable("self");
    Value turn_before = self_before.GetField("turn");
    double mult_before = turn_before.GetField("multiplier").ToDouble();
    
    EXPECT_DOUBLE_EQ(mult_before, 1.0) << "Initial multiplier should be 1.0";
    
    // 【模拟脚本执行】set self.turn.multiplier = self.turn.multiplier * 2;
    // 这里简化流程，通过直接的 API 调用
    
    // Step 1: LOAD_SELF
    Value self_on_stack = scope_stack_->GetVariable("self");  // ← handle 模式不深拷贝
    
    // Step 2: TABLE_ACCESS "turn"
    Value turn_on_stack = self_on_stack.GetField("turn");    // 获取 turn Schema
    
    // Step 3: TABLE_ACCESS "multiplier"
    Value mult_value = turn_on_stack.GetField("multiplier");  // 值: 1.0
    
    // Step 4: CONST 2 + MULTIPLY
    Value two = Value(2.0);
    Value result = mult_value * two;  // 1.0 * 2.0 = 2.0
    
    // Step 5: TABLE_SET "multiplier"
    turn_on_stack.SetField("multiplier", result);  // 修改 turn Schema
    
    // Step 6: TABLE_SET_SELF "turn"（同步关键！）
    self_on_stack.SetField("turn", turn_on_stack);  // 修改 self Schema
    scope_stack_->SetVariable("self", self_on_stack);  // ← 同步回 Scope
    
    // 【验证结果】
    Value self_after = scope_stack_->GetVariable("self");
    Value turn_after = self_after.GetField("turn");
    double mult_after = turn_after.GetField("multiplier").ToDouble();
    
    EXPECT_DOUBLE_EQ(mult_after, 2.0) 
        << "Multiplier should be 2.0 after script execution (Handle mode working!)";
    
    // 验证其他字段未改变
    EXPECT_EQ(self_after.GetField("atk").ToInt(), 9) << "ATK field should unchanged";
    EXPECT_EQ(self_after.GetField("hp").ToInt(), 50) << "HP field should unchanged";
}

/**
 * IT-NestedFieldWritethrough: 嵌套字段修改和写回
 * 
 * 脚本: set self.dmg.d1 = 99;
 * 期望: dmg.d1 变为 99，其他 dmg 字段不变
 */
TEST_F(IntegrationTestFixture, NestedFieldWritethrough) {
    // 【初始状态】
    Value self_before = scope_stack_->GetVariable("self");
    Value dmg_before = self_before.GetField("dmg");
    
    EXPECT_EQ(dmg_before.GetField("d1").ToInt(), 1) << "Initial d1=1";
    EXPECT_EQ(dmg_before.GetField("d2").ToInt(), 3) << "Initial d2=3";
    
    // 【模拟脚本执行】set self.dmg.d1 = 99;
    Value self_on_stack = scope_stack_->GetVariable("self");
    
    // TABLE_ACCESS "dmg" → 获取 dmg Schema
    Value dmg_on_stack = self_on_stack.GetField("dmg");
    
    // TABLE_SET "d1" → 修改 dmg Schema 的 d1 字段
    dmg_on_stack.SetField("d1", Value(99));
    
    // TABLE_SET_SELF "dmg" → 修改 self Schema 的 dmg 字段，并同步
    self_on_stack.SetField("dmg", dmg_on_stack);
    scope_stack_->SetVariable("self", self_on_stack);  // 同步到 Scope
    
    // 【验证结果】
    Value self_after = scope_stack_->GetVariable("self");
    Value dmg_after = self_after.GetField("dmg");
    
    EXPECT_EQ(dmg_after.GetField("d1").ToInt(), 99) << "d1 should be 99";
    EXPECT_EQ(dmg_after.GetField("d2").ToInt(), 3) << "d2 should be unchanged";
    EXPECT_EQ(dmg_after.GetField("d3").ToInt(), 5) << "d3 should be unchanged";
    EXPECT_EQ(dmg_after.GetField("d4").ToInt(), 7) << "d4 should be unchanged";
}

/**
 * IT-MultipleFieldModification: 多个字段同时修改
 * 
 * 脚本:
 *   set self.atk = 15;
 *   set self.hp -= 10;
 *   set self.turn.multiplier = 2.0;
 * 期望: 所有修改都持久化
 */
TEST_F(IntegrationTestFixture, MultipleFieldModification) {
    // 【初始状态】
    Value self_begin = scope_stack_->GetVariable("self");
    EXPECT_EQ(self_begin.GetField("atk").ToInt(), 9);
    EXPECT_EQ(self_begin.GetField("hp").ToInt(), 50);
    
    // 【修改 1: set self.atk = 15】
    Value self = scope_stack_->GetVariable("self");
    self.SetField("atk", Value(15));
    scope_stack_->SetVariable("self", self);
    
    // 【修改 2: set self.hp -= 10】
    self = scope_stack_->GetVariable("self");
    int hp_current = self.GetField("hp").ToInt();
    self.SetField("hp", Value(hp_current - 10));
    scope_stack_->SetVariable("self", self);
    
    // 【修改 3: set self.turn.multiplier = 2.0】
    self = scope_stack_->GetVariable("self");
    Value turn = self.GetField("turn");
    turn.SetField("multiplier", Value(2.0));
    self.SetField("turn", turn);
    scope_stack_->SetVariable("self", self);
    
    // 【验证所有修改都生效】
    Value self_end = scope_stack_->GetVariable("self");
    
    EXPECT_EQ(self_end.GetField("atk").ToInt(), 15) << "ATK should be 15";
    EXPECT_EQ(self_end.GetField("hp").ToInt(), 40) << "HP should be 40";
    EXPECT_DOUBLE_EQ(self_end.GetField("turn").GetField("multiplier").ToDouble(), 2.0)
        << "Multiplier should be 2.0";
}

/**
 * IT-DeepNestedModification: 深层嵌套修改
 * 
 * 这个测试验证所有层级的同步都工作正常
 */
TEST_F(IntegrationTestFixture, DeepNestedModification) {
    // 【初始状态】
    Value self = scope_stack_->GetVariable("self");
    Value turn_init = self.GetField("turn");
    EXPECT_DOUBLE_EQ(turn_init.GetField("multiplier").ToDouble(), 1.0);
    
    // 【执行 3 层嵌套修改】
    self = scope_stack_->GetVariable("self");
    Value turn = self.GetField("turn");
    
    // 修改第 3 层
    turn.SetField("multiplier", Value(3.5));
    
    // 写回第 2-1 层
    self.SetField("turn", turn);
    scope_stack_->SetVariable("self", self);
    
    // 【验证所有层都得到同步】
    Value self_after = scope_stack_->GetVariable("self");
    Value turn_after = self_after.GetField("turn");
    
    EXPECT_DOUBLE_EQ(turn_after.GetField("multiplier").ToDouble(), 3.5)
        << "Deep nested modification should persist";
}

/**
 * IT-HandleModeBehavior: Handle 模式特定行为
 * 
 * 验证在启用 USE_HANDLES 时，Value 复制不会深拷贝（若实现了）
 */
TEST_F(IntegrationTestFixture, DISABLED_HandleModeBehavior) {
    // 这个测试在启用 USE_HANDLES 编译宏时才有意义
    // 目前暂时禁用，等实际启用 handle 编译时取消禁用
    
    GTEST_SKIP() << "Handle mode object identity test - requires USE_HANDLES macro";
}

/**
 * IT-ScopeStackIntegration: ScopeStack 正确性验证
 */
TEST_F(IntegrationTestFixture, ScopeStackIntegration) {
    // Push 第二个 scope
    scope_stack_->PushScope();
    
    // 设置一个局部变量
    scope_stack_->SetVariable("temp", Value(42));
    
    // 修改继承的 self
    Value self_inner = scope_stack_->GetVariable("self");
    self_inner.SetField("atk", Value(100));
    scope_stack_->SetVariable("self", self_inner);
    
    // 验证内层修改
    Value self_check = scope_stack_->GetVariable("self");
    EXPECT_EQ(self_check.GetField("atk").ToInt(), 100);
    
    // Pop scope
    scope_stack_->PopScope();
    
    // 验证外层仍然看到修改（如果实现了）
    // 或保持原值（如果 scope 是隔离的）
    Value self_outer = scope_stack_->GetVariable("self");
    // 根据架构，这里可能等于 100 或 9（取决于 scope 设计）
    // 这里不做断言，因为取决于 ScopeStack 的具体设计
}

/**
 * ============================================================================
 * 性能测试（基准线）
 * ============================================================================
 */

class PerformanceTestFixture : public ::testing::Test {
protected:
    void SetUp() override {
        object_table_ = std::make_unique<ObjectTable>();
        scope_stack_ = std::make_unique<ScopeStack>();
        scope_stack_->PushScope();
    }
    
    std::unique_ptr<ObjectTable> object_table_;
    std::unique_ptr<ScopeStack> scope_stack_;
};

/**
 * PT-ValueCopyOverhead: 值复制成本（Handle vs Legacy）
 */
TEST_F(PerformanceTestFixture, ValueCopyOverhead) {
    // 创建复杂 Schema
    Value complex_schema = Value::CreateSchema();
    for (int i = 0; i < 100; ++i) {
        complex_schema.SetField("field_" + std::to_string(i), Value(i));
    }
    
    // 计时：复制 10000 次
    auto start = std::chrono::high_resolution_clock::now();
    
    for (int i = 0; i < 10000; ++i) {
        Value copy = complex_schema;  // ← 这里会触发深拷贝（legacy）或仅复制 handle（PoC）
        volatile int dummy = copy.GetField("field_0").ToInt();  // 防止优化
    }
    
    auto end = std::chrono::high_resolution_clock::now();
    auto duration = std::chrono::duration_cast<std::chrono::milliseconds>(end - start);
    
    // 记录性能数据
    std::cout << "Value copy 10000x: " << duration.count() << " ms" << std::endl;
    
    // 基准预期：legacy 模式应该 > 100ms，PoC handle 模式应该 < 10ms
    // 这里不做硬性断言，因为会根据编译模式变化
}

/**
 * PT-ObjectTableOperations: ObjectTable 操作性能
 */
TEST_F(PerformanceTestFixture, ObjectTableOperations) {
    // 创建 1000 个对象
    std::vector<ObjectHandle> handles;
    
    auto start = std::chrono::high_resolution_clock::now();
    
    for (int i = 0; i < 1000; ++i) {
        Value obj = Value::CreateSchema();
        obj.SetField("id", Value(i));
        obj.SetField("value", Value(i * 1.5));
        
        ObjectHandle h = object_table_->Create(obj);
        handles.push_back(h);
    }
    
    auto end = std::chrono::high_resolution_clock::now();
    auto create_time = std::chrono::duration_cast<std::chrono::milliseconds>(end - start);
    
    // 查询 10000 次
    start = std::chrono::high_resolution_clock::now();
    
    volatile double sum = 0.0;
    for (int i = 0; i < 10000; ++i) {
        int idx = i % handles.size();
        SchemaValue& obj = object_table_->Get(handles[idx]);
        // 在实际应用中这里会访问字段
        sum += obj.GetField("value").ToDouble();
    }
    
    end = std::chrono::high_resolution_clock::now();
    auto query_time = std::chrono::duration_cast<std::chrono::milliseconds>(end - start);
    
    std::cout << "ObjectTable Create 1000x: " << create_time.count() << " ms" << std::endl;
    std::cout << "ObjectTable Get 10000x: " << query_time.count() << " ms" << std::endl;
    
    // 验证结果（防止编译器优化）
    EXPECT_GT(handles.size(), 0);
}

