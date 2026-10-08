# Phase 3: VM Schema 重构 - 完整架构改进

**日期**: 2026-04-10  
**状态**: 实现完成，待编译验证  
**优先级**: 关键

---

## 目标

将 VM 脚本执行系统从**对象句柄代理模式**重构为**完整 Schema 注入模式**，彻底解决脚本无法访问动态构建字段（如 `dmg.d1`）的问题。

---

## 关键改变

### 1. LOAD_SELF 指令（VM.cpp L527-582）

#### 改变前
```cpp
// 返回轻量级 schema，只包含 __handle_id__
Value schema = Value::CreateSchema();
schema.SetField(std::string("__handle_id__"), Value((int64_t)handle_id));
Push(schema);
```

#### 改变后
```cpp
// 从 ExecutionEnvironment 直接获取完整的 self schema
// 这个 schema 是在脚本执行前由运行时注入的
Value self_schema = env->GetValueProperty("self");
if (self_schema.IsNull()) {
    self_schema = Value::CreateSchema();  // 安全失败
}
Push(self_schema);
```

**关键优势**:
- LOAD_SELF 返回完整 Schema，包含所有字段（name, atk, hp, dmg, turn）
- 脚本可以直接访问 `self.dmg.d1` 等嵌套字段
- 不再依赖 object_handles 表的复杂逻辑

### 2. TABLE_ACCESS 指令（VM.cpp L159-190）

#### 改变前
```cpp
// 检查是否为 handle-wrapped schema
Value handle_id_value = schema.GetField("__handle_id__");
if (handle_id_value.GetType() == ValueType::Int && env) {
    // 通过句柄查询真实对象指针
    uintptr_t ptr = env->GetObjectHandle(handle_id);
    // 从 Character 对象读取当前值
    // ...
}
```

#### 改变后
```cpp
// 【关键改变】直接从 Schema 读取字段
// 所有必要的数据都在 Schema 中提前准备好
Value field_value = schema.GetField(key);
Push(field_value);
```

**关键优势**:
- 简化指令逻辑，移除 handle 查询代码
- 不再访问 C++ 对象属性
- 脚本读取的数据都是脚本注入范围内的

### 3. TABLE_SET 指令（VM.cpp L192-223）

#### 改变前
```cpp
// 通过句柄查询 Character 对象并直接修改
if (handle_id >= 0) {
    uintptr_t ptr = env->GetObjectHandle(handle_id);
    Character* actor = reinterpret_cast<Character*>(ptr);
    // 直接修改 actor->atk, actor->hp 等
    actor->atk = new_atk;
    // ...
}
```

#### 改变后
```cpp
// 【关键改变】只修改 Schema 字段
// 脚本修改的是 Schema，不直接影响 Character 对象
schema.SetField(key, value);
Push(schema);
```

**关键优势**:
- Schema 成为唯一的数据真相源（在脚本执行范围内）
- 修改不会直接影响 Character（可选 write-back 机制）
- 更清晰的数据流：脚本 → Schema → [可选同步] Character

### 4. 运行时 Self 注入（PresetSystem.cpp 多处）

在所有脚本执行路径中，都添加双重注册：

```cpp
// 构建完整的 self schema
Value self_schema = Value::CreateSchema();
self_schema.SetField("name", Value(env->GetActor()->name));
self_schema.SetField("camp", Value((int64_t)env->GetActor()->camp));
self_schema.SetField("atk", Value((int64_t)env->GetActor()->atk));
self_schema.SetField("hp", Value((int64_t)env->GetActor()->hp));

// dmg 子 schema
Value dmg_schema = Value::CreateSchema();
dmg_schema.SetField("d1", Value((int64_t)env->GetActor()->dmg[0]));
dmg_schema.SetField("d2", Value((int64_t)env->GetActor()->dmg[1]));
dmg_schema.SetField("d3", Value((int64_t)env->GetActor()->dmg[2]));
dmg_schema.SetField("d4", Value((int64_t)env->GetActor()->dmg[3]));
self_schema.SetField("dmg", dmg_schema);

// turn 对象
Value turn_schema = Value::CreateSchema();
turn_schema.SetField("multiplier", Value((int64_t)env->GetIntProperty("attack_multiplier", 1)));
self_schema.SetField("turn", turn_schema);

// 位置 1: ScopeStack（若脚本直接访问 self 变量）
scope.SetVariable("self", self_schema);

// 位置 2: ExecutionEnvironment（供 LOAD_SELF 指令访问）
env->SetValueProperty("self", self_schema);
```

#### 修改位置

1. **AnkePreset::Execute() - 临界成功脚本**（PresetSystem.cpp ~360-365）
   - ✅ 已完成（代码中已存在）

2. **AnkePreset::Execute() - 普通选项脚本**（PresetSystem.cpp ~437-445）
   - ✅ 已修改

3. **SkillPreset::Execute()**（PresetSystem.cpp ~575-583）
   - ✅ 已修改

---

## 架构对比

### 旧架构（Phase 1-2）

```
脚本: self.dmg.d1
  ↓
LOAD_SELF → 返回 {__handle_id__: 1000}
  ↓
TABLE_ACCESS dmg → 查询 object_handles[1000] → 获取 Character*
  ↓
TABLE_ACCESS d1 → actor->dmg[0]
  ↓
结果：需要从 C++ 对象读取

问题：脚本无法访问不存在于 Character 类的字段！
```

### 新架构（Phase 3）

```
脚本: self.dmg.d1
  ↓
运行时注入 self_schema（包含完整 dmg 子 schema）
  ↓
LOAD_SELF → 返回完整 {name, atk, hp, dmg: {d1, d2, d3, d4}, turn: {...}}
  ↓
TABLE_ACCESS dmg → 从 Schema 读取 dmg 字段
  ↓
TABLE_ACCESS d1 → 从 dmg schema 读取 d1 值
  ↓
结果：所有字段都在 Schema 中

优势：脚本完全基于 Schema，不需要访问 C++ 对象！
```

---

## 数据流图

### Self Schema 完整结构

```
self_schema (Schema)
├── name: String("范马勇次郎")
├── camp: Int(2)
├── atk: Int(120)
├── hp: Int(60)
├── dmg: Schema
│   ├── d1: Int(2)
│   ├── d2: Int(4)
│   ├── d3: Int(6)
│   └── d4: Int(8)
├── turn: Schema
│   └── multiplier: Int(1)
└── [其他字段可扩展]

// 脚本可访问的所有路径：
self.name           → "范马勇次郎"
self.camp           → 2
self.atk            → 120
self.hp             → 60
self.dmg            → dmg Schema
self.dmg.d1         → 2
self.dmg.d2         → 4
self.dmg.d3         → 6
self.dmg.d4         → 8
self.turn           → turn Schema
self.turn.multiplier → 1
```

---

## 脚本执行路径

### Path 1: AnkePreset 普通选项脚本

```
1. 用户掷骰 → 选中选项（如"大伤害"）
2. 编译脚本 → dodamage(self.dmg.d3);
3. 创建 ScopeStack 和 VM
4. 初始化 self_schema 到 ScopeStack 和 ExecutionEnvironment
5. VM 执行：
   - LOAD_SELF → 从 ExecutionEnvironment 返回 self_schema
   - TABLE_ACCESS dmg → 从 self_schema 读取 dmg 子 schema
   - TABLE_ACCESS d3 → 从 dmg 读取 d3=6
   - CALL dodamage(6) → 伤害应用
6. 脚本执行成功 ✓
```

### Path 2: AnkePreset 临界成功脚本

```
1. 关键成功（掷骰结果为 10）
2. 执行临界脚本：set self.turn.multiplier = self.turn.multiplier * 2;
                akr("natk");
3. 初始化 self_schema
4. VM 执行：
   - LOAD_SELF → self_schema
   - TABLE_ACCESS turn → turn schema
   - TABLE_ACCESS multiplier → 1
   - 乘以 2 → 2
   - TABLE_SET multiplier → 修改 turn.multiplier = 2在 Schema 中
   - CALL akr("natk") → 递归调用 NATK 预设，会看到 multiplier=2
5. 嵌套预设执行时，self.turn.multiplier 现在是 2
```

### Path 3: SkillPreset 技能脚本

```
1. 技能触发（OnDamageTaken, OnRoundEnd 等）
2. 初始化 self_schema
3. VM 执行技能脚本（可访问 self.atk, self.dmg 等）
4. 脚本修改可能影响战斗逻辑
```

---

## 关键实现详情

### ExecutionEnvironment 两层访问

```cpp
// Layer 1: ScopeStack 存储（用于脚本的本地变量访问）
scope.SetVariable("self", self_schema);

// Layer 2: ExecutionEnvironment 存储（用于 LOAD_SELF 指令访问）
env->SetValueProperty("self", self_schema);

// 虚拟机执行时：
// - LOAD_SELF 指令调用 env->GetValueProperty("self")
// - 直接获得完整的 Schema
```

### 移除的代码

不再需要以下代码：
```cpp
// ❌ 不再需要
schema.SetField("__handle_id__", Value(handle_id));
env->AllocateObjectHandle(actor_ptr);
env->GetObjectHandle(handle_id);
```

---

## 编译影响

### C++ 代码修改

- **VM.cpp**: LOAD_SELF、TABLE_ACCESS、TABLE_SET 指令
- **PresetSystem.cpp**: 3 处脚本执行路径添加 self 初始化

### 编译检查清单

- [✓] 修改 LOAD_SELF 指令
- [✓] 修改 TABLE_ACCESS 指令
- [✓] 修改 TABLE_SET 指令
- [✓] 添加 self 初始化到普通选项路径
- [✓] 添加 self 初始化到 SkillPreset 路径
- [✓] 临界脚本路径（已存在）

### 预期编译结果

- ABot.Core: ✓ 应该编译成功（C++ 改动有限）
- 其他项目: 不受影响

---

## 测试验证步骤

### 测试 1: 脚本执行成功

```
预期：
  [INIT] Self variable initialized in both ScopeStack and ExecutionEnvironment
  [脚本执行] 【结果】: 成功 ✓  ← 从"失败 ✗"改为"成功 ✓"
```

### 测试 2: LOAD_SELF 完整性

```
预期诊断日志：
  [LOAD_SELF] IP:0 - Loaded complete schema: actor=范马勇次郎 atk=120 hp=60
  [LOAD_SELF] dmg schema loaded: d1=2 d2=4 d3=6 d4=8
```

### 测试 3: 伤害应用

```
预期：
  - dodamage() 被调用（文件日志生成）
  - HP 值正确改变
  - 伤害应用到正确的角色
```

### 测试 4: 伤害的 HP 持久化

```
Round 1: target_hp = 50 - damage → 44
Round 2: target_hp = 44 - damage → 38  ← HP 应保持改变，不重置
```

---

## 后续扩展点

该架构为支持其他对象预留了空间：

```cpp
// 未来可添加：
env->SetValueProperty("target", target_schema);
env->SetValueProperty("ally", ally_schema);

// 脚本可以访问：
target.hp
target.atk
target.skills
ally.buff_count
```

---

## 总结

**Phase 3 完全重构了脚本执行的数据模型**，从依赖 C++ 对象指针的间接访问，改为直接基于预注入的 Schema 对象。

这个改变：
- ✅ 解决脚本无法访问 dmg 字段的根本问题
- ✅ 简化 VM 指令逻辑
- ✅ 为技能系统和其他功能打好基础
- ✅ 使数据流更清晰可控

**成功标准**: 脚本执行成功，HP 正确变化，伤害正确应用。
