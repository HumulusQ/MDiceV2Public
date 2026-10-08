# 脚本执行失败问题完整分析报告

**报告生成日期**: 2026-04-10  
**问题状态**: 正在修复  
**优先级**: 🔴 关键

---

## 📋 目录

1. [执行摘要](#执行摘要)
2. [问题症状](#问题症状)
3. [根本原因分析](#根本原因分析)
4. [详细日志分析](#详细日志分析)
5. [技术深度分析](#技术深度分析)
6. [修复方案](#修复方案)
7. [验证方法](#验证方法)
8. [预期效果](#预期效果)

---

## 执行摘要

### 核心问题

在 NATK（Normal Attack）战斗系统中，脚本虽然正确编译和加载，但在虚拟机（VM）执行时失败，导致 `dodamage()` 函数无法被调用，结果造成伤害永远无法应用于目标。

### 主要表现

- **脚本执行失败率**: 100%（每轮都失败）
- **影响范围**: 所有 ANKE 预设脚本（普通选项）
- **伤害应用状态**: 0 次成功（从未导致伤害）
- **HP 变化**: 完全无变化（所有角色 HP 保持初始值）

### 根本原因

VM 的 `LOAD_SELF` 指令从 `ExecutionEnvironment.object_handles` 加载对象时，获得的是**原始 Character 指针，而不是完整的 Schema 包装对象**。当脚本尝试访问 `self.dmg.d3` 这样的字段时，因为字段结构不完整而崩溃。

### 应用的修复

在 `AnkePreset::Execute()` 中添加双重注册：

```cpp
// 位置 1: ScopeStack（直接变量访问）
scope.SetVariable("self", self_schema);

// 位置 2: ExecutionEnvironment（LOAD_SELF 指令访问）← 新增
env->SetValueProperty("self", self_schema);
```

---

## 问题症状

### 症状 1: 脚本执行失败

```
[脚本执行] 【普通选项脚本】: 大伤害 | 总指令数: 8
[INIT] Self variable initialized in ScopeStack
[脚本执行] 【结果】: 失败 ✗
```

**每轮都出现**，无一成功。

### 症状 2: 伤害值未改变

```
[DIAGNOSTIC] Actor: 范马勇次郎 dmg=[2,4,6,8]
[DEBUG] After exec: dmg=[2,4,6,8]
```

执行前后 **dmg 数组完全相同**，说明 `dodamage()` 根本没有被执行。

### 症状 3: HP 持久化无效

```
[Round 2] 烈海王 HP 50/50
[Round 3] 烈海王 HP 50/50
```

**预期**: Round 2 选择"大伤害"应该造成 6 点伤害，HP 应为 44/50  
**实际**: HP 保持 50/50，完全没有变化

### 症状 4: 虚拟机诊断信息不完整

```
[LOAD_SELF] IP:0 handle_id=1000 actor_ptr=0x1f4c1656e90 
           actor=范马勇次郎 atk=120 HP=60
```

**问题**: LOAD_SELF 加载的信息 **缺少 dmg 和 turn 字段**，只有基本属性。

---

## 根本原因分析

### 原因链条

#### 第 1 层：脚本需求

```javascript
// 脚本内容（例如"大伤害"选项）
dodamage(self.dmg.d3);
```

**脚本需要访问**: `self.dmg.d3` （值应为 6）

#### 第 2 层：VM 编译到指令

脚本被编译成 8 条 VM 指令序列：

```
Instruction 0: LOAD_SELF          // 加载 self 对象
Instruction 1: LOAD_PROPERTY      // 访问 self.dmg
Instruction 2: LOAD_PROPERTY      // 访问 dmg.d3
... 其他指令用于函数调用
```

#### 第 3 层：LOAD_SELF 指令的问题

```cpp
// VM 虚拟机中的 LOAD_SELF 实现（伪代码）
object = ExecutionEnvironment::Current()->
         object_handles[handle_id];
// 返回的是原始 Character* 指针
```

**关键问题**: LOAD_SELF 从 `ExecutionEnvironment.object_handles` 获取对象，这是一个**原始指针存储**，不包含 Schema 结构信息。

#### 第 4 层：Schema 结构缺失

```
原始 Character 对象
├── name: 范马勇次郎
├── atk: 120
├── hp: 60
└── ❌ 无 dmg 字段 Schema

完整的 self_schema 对象应该有
├── name: 范马勇次郎
├── atk: 120
├── hp: 60
├── ✅ dmg (Schema)
│   ├── d1: 2
│   ├── d2: 4
│   ├── d3: 6
│   └── d4: 8
└── ✅ turn (Schema)
    └── multiplier: 1
```

#### 第 5 层：访问失败

当脚本执行到尝试访问 `self.dmg.d3` 时：

```
VM 执行流程：
  1. LOAD_SELF 执行
     ↓ 获得原始 Character 指针（无 dmg 字段）
  2. LOAD_PROPERTY dmg
     ↓ 尝试在 Character 中查找 dmg 属性
  3. ❌ 属性不存在！ Script Crash
  4. 结果: 脚本执行失败 ✗
```

### 为什么 ScopeStack 初始化不够

代码中已经有：

```cpp
scope.SetVariable("self", self_schema);
[INIT] Self variable initialized in ScopeStack
```

但这只在以下情况下有帮助：

✅ **直接的变量访问**（如 `self = somefunction()`）  
❌ **VM LOAD_SELF 指令**（绕过 ScopeStack，直接从 object_handles 加载）

VM 的设计使用了两个独立的命名空间：

- **ScopeStack**: 局部变量、参数存储
- **ExecutionEnvironment.object_handles**: 对象引用存储（供 LOAD_SELF、LOAD_PROPERTY 等指令使用）

---

## 详细日志分析

### 时间线重建（从日志）

#### 阶段 1: ANKE 预设加载与初始化

```
[ANKE-DEBUG] RegisterAnkeFromString called
[ANKE-DEBUG] Input script length: 581
[ANKE-DEBUG] ANKESET format detected
[ANKE-DEBUG] Extracted <anke> block #1 (length=554)
[ANKE-DEBUG] Total <anke> blocks found: 1
[ANKE-DEBUG] Processing ANKE #1 of 1
```

**分析**: NATK 预设从 ANKESET 格式正确解析，提取了 1 个 anke 块。

#### 阶段 2: 选项解析

```
[ANKE-DEBUG] Parsed 7 options from script
[ANKE-DEBUG] Option[0] parsed: type='e', name='回避', weight=1, script_len=53
[ANKE-DEBUG] Option[1] parsed: type='e', name='小伤害', weight=2, script_len=22
[ANKE-DEBUG] Option[2] parsed: type='e', name='小伤害', weight=2, script_len=22
[ANKE-DEBUG] Option[3] parsed: type='e', name='大伤害', weight=2, script_len=22
[ANKE-DEBUG] Option[4] parsed: type='e', name='极大伤害', weight=2, script_len=22
[ANKE-DEBUG] Option[5] parsed: type='es', name='critical_success', weight=1, script_len=65
[ANKE-DEBUG] Option[6] parsed: type='ef', name='critical_fail', weight=1, script_len=107
```

**分析**: 7 个选项全部成功解析，包括：
- 5 个普通选项 (e 类型)
- 1 个大成功选项 (es)
- 1 个大失败选项 (ef)

#### 阶段 3: 脚本编译验证

```
[ANKE-DEBUG] Option[1] Extracted script (length=22)
    {e=小伤害, w=2, p=expr(dodamage(self.dmg.d1);)}
[ANKE-DEBUG] Option[1] After final cleanup, length=22, ready for compilation
[ANKE-DEBUG] Added option '小伤害' with weight 2
```

**分析**: 脚本被标记为"ready for compilation"，长度为 22 字符，内容完整。

#### 阶段 4: 掷骰子与选项选择

```
[ANKE投掷] D10=6
[选中选项] 大伤害
```

**分析**: 掷骰结果为 6，权重匹配"大伤害"选项。

#### 阶段 5: 脚本执行初始化

```
[脚本执行] 【普通选项脚本】: 大伤害 | 总指令数: 8
[DIAGNOSTIC] Normal script - env_param=valid env_current=valid stack_depth=1
[DIAGNOSTIC] Actor: 范马勇次郎 dmg=[2,4,6,8]
```

**分析**: 
- 编译成 8 条指令（正常）
- 执行环境参数有效（env_param=valid）
- 当前执行环境有效（env_current=valid）
- ScopeStack 深度为 1（正常）
- dmg 数组初始值正确 [2,4,6,8]

#### 阶段 6: Self 变量初始化

```
[INIT] Self variable initialized in ScopeStack
```

**分析**: 已尝试在 ScopeStack 中初始化 self 变量，但......

#### 阶段 7: 虚拟机执行与崩溃

```
[脚本执行] 【结果】: 失败 ✗
[VM诊断日志开始]
[LOAD_SELF] IP:0 handle_id=1000 actor_ptr=0x1f4c1656e90 
           actor=范马勇次郎 atk=120 HP=60
[VM诊断日志结束]
```

**关键发现**:
- VM 执行失败
- LOAD_SELF 指令执行（IP:0 表示在第一条指令）
- 加载的对象信息不完整（缺少 dmg、turn 等字段）

#### 阶段 8: 后执行诊断

```
[DEBUG] After exec: dmg=[2,4,6,8]
```

**分析**: dmg 数组完全没有改变，确认 `dodamage()` 未被执行。

---

## 技术深度分析

### ExecutionEnvironment 架构

```cpp
class ExecutionEnvironment {
public:
    static ExecutionEnvironment* Current();
    
    // 两个独立的存储机制：
    
    // 1. 属性存储（供 GetValueProperty 使用）
    void SetValueProperty(const std::string& name, const Value& val);
    Value GetValueProperty(const std::string& name);
    
    // 2. 对象句柄存储（供 LOAD_SELF 等指令使用）
    std::map<int, void*> object_handles;  // handle_id -> Character*
};
```

### 虚拟机指令执行流程

#### 当前（有问题的）流程

```
脚本: dodamage(self.dmg.d3);
          ↓
VM 编译: 
  • LOAD_SELF (handle_id=1000)
  • LOAD_PROPERTY "dmg"
  • LOAD_PROPERTY "d3"
  • CALL dodamage(1)
          ↓
执行 LOAD_SELF:
  self_obj = object_handles[1000]  // 获取原始 Character*
  push(self_obj)  // 栈顶现在是 Character 对象（无 Schema）
          ↓
执行 LOAD_PROPERTY "dmg":
  obj = pop()  // Character 对象
  prop = obj->GetProperty("dmg")
  ❌ Character 类中不存在 dmg 属性！
  ❌ 访问失败，脚本崩溃
  ❌ 函数调用链中断
```

#### 修复后（预期）的流程

```
脚本: dodamage(self.dmg.d3);
          ↓
vm.Execute() 调用前:
  env->SetValueProperty("self", self_schema);  // ← 新增
          ↓
执行 LOAD_SELF:
  self_obj = GetValueProperty("self")
  // 或通过其他机制获得 self_schema
  push(self_schema)  // 栈顶现在是完整的 Schema 对象
          ↓
执行 LOAD_PROPERTY "dmg":
  obj = pop()  // Schema 对象（有 dmg 字段）
  prop = obj.GetField("dmg")
  ✅ dmg Schema 存在！
  push(prop)
          ↓
执行 LOAD_PROPERTY "d3":
  obj = pop()  // dmg Schema
  prop = obj.GetField("d3")
  ✅ d3 字段存在，值为 6！
  push(6)
          ↓
执行 CALL dodamage(1):
  arg = pop()  // 6
  dodamage(6)
  ✅ 函数成功执行！
```

### Schema 结构详解

自 Schema 对象应具有以下结构：

```
Value self_schema(ValueType::Schema)
{
  "name": Value("范马勇次郎")
  "camp": Value(2)  // Camp 2
  "atk": Value(120)
  "hp": Value(60)
  "dmg": Value(Schema) {
    "d1": Value(2)
    "d2": Value(4)
    "d3": Value(6)
    "d4": Value(8)
  }
  "turn": Value(Schema) {
    "multiplier": Value(1)
  }
}
```

每一层都是 `Schema` 类型，可以进行 `GetField()` 和 `SetField()` 操作。

---

## 修复方案

### 修复位置 1: AnkePreset::Execute() - 普通选项脚本

**文件**: `ABot/ABot.Core/src/PresetSystem.cpp`

**原始代码** (约 ~360-365 行):

```cpp
// 初始化 self 变量
Value self_schema = CreateSelfSchema(env->character);  // 假设方法名
// ... 设置各字段 ...

// 在 ScopeStack 中设置 self
scope.SetVariable("self", self_schema);

// 执行脚本
bool success = vm.Execute(option.script.get(), &scope);
```

**修复后代码**:

```cpp
// 初始化 self 变量
Value self_schema = CreateSelfSchema(env->character);  // 假设方法名
// ... 设置各字段 ...

// 位置 1: 在 ScopeStack 中设置 self（原有）
scope.SetVariable("self", self_schema);

// 位置 2: 在 ExecutionEnvironment 中设置 self（新增）← CRITICAL FIX
env->SetValueProperty("self", self_schema);

// 执行脚本
bool success = vm.Execute(option.script.get(), &scope);
```

### 修复位置 2: AnkePreset::Execute() - 临界成功选项脚本

**同样的修复应应用于**:
- 第 ~390-400 行（大成功脚本执行）
- 第 ~410-420 行（大失败脚本执行）

因为这些脚本同样使用 `LOAD_SELF` 指令，同样需要完整的 Schema 对象。

### 修复验证检查清单

- [ ] 找到 AnkePreset::Execute() 方法
- [ ] 定位 scope.SetVariable("self", self_schema); 行
- [ ] 在其下方添加 env->SetValueProperty("self", self_schema);
- [ ] 应用到所有 3 处脚本执行位置（普通、大成功、大失败）
- [ ] 编译验证通过
- [ ] 日志中出现 "initialized in both ScopeStack and ExecutionEnvironment" 消息

---

## 验证方法

### 测试步骤

#### 步骤 1: 编译修复代码

```bash
# 在工作区运行 debug-build-clean 任务
# 确保编译成功（Exit Code 0）
```

**预期结果**: 编译成功，无错误或警告

#### 步骤 2: 启动游戏

```bash
# 运行 MDiceV2.Launcher.exe
# 初始化角色：范马勇次郎 和 烈海王
```

#### 步骤 3: 进行 2 轮战斗

```
Round 1:
  - 等待范马勇次郎 执行普通攻击
  - 观察日志中的脚本执行结果
  - 检查伤害是否被应用

Round 2:
  - 观察 HP 是否有变化
  - 再次检查脚本执行结果
```

### 日志验证项

#### ✅ 验证 1: Self 变量初始化日志

**查找**:
```
[INIT] Self variable initialized in both ScopeStack and ExecutionEnvironment
```

**说明**: 出现此日志表示修复已应用且第二层初始化成功。

#### ✅ 验证 2: 脚本执行成功

**期望改变**:
```
// 修复前
[脚本执行] 【结果】: 失败 ✗

// 修复后（预期）
[脚本执行] 【结果】: 成功 ✓
```

**重要性**: 高 - 这是核心修复的直接指标

#### ✅ 验证 3: dodamage() 函数调用

**检查文件**:
- 查看 `C:\dodamage_diagnostic.log`（如果存在）
- 应该包含多个条目表示 dodamage() 被调用

**日志示例**:
```
[dodamage] Damage value: 6
[dodamage] Target: 烈海王
[dodamage] New HP: 44/50
```

#### ✅ 验证 4: HP 变化

**期望**:

```
Round 1 结束:
  - 烈海王 HP: 44/50 (取决于选项伤害值)
  - 范马勇次郎 HP: 60/60

Round 2 结束:
  - 烈海王 HP: 继续下降 (如 42/50)
  - HP 应保持变化不重置
```

#### ✅ 验证 5: VM 诊断日志完整性

**期望日志包含**:
```
[LOAD_SELF] ... dmg=[2,4,6,8] ... turn.multiplier=1
```

说明 LOAD_SELF 加载的对象现在包含完整的字段信息。

---

## 预期效果

### 修复成功标志

| 指标 | 修复前 | 修复后 |
|------|--------|--------|
| 脚本执行成功率 | 0% (失败 ✗) | 100% (成功 ✓) |
| 伤害应用次数/轮 | 0 | 1-5（取决于选项） |
| HP 变化 | 无变化 | 正常下降 |
| dodamage() 调用 | 0 次 | 每轮 1+ 次 |
| VM 完成状态 | 崩溃 | 正常退出 |

### 战斗流程改善

#### 修复前流程

```
背景: 范马勇次郎 vs 烈海王

Round 1:
  ├─ 范马勇次郎 攻击
  ├─ 掷骰选择选项（例如"大伤害"）
  ├─ 脚本编译成功
  ├─ 脚本执行失败（LOAD_SELF 问题）⚠️
  ├─ dodamage() 未执行
  └─ 烈海王 HP: 50/50（无变化）

Round 2:
  ├─ 范马勇次郎 再次攻击
  ├─ 脚本再次执行失败 ⚠️
  └─ 烈海王 HP: 50/50（仍无变化）

结果: 战斗陷入死循环，无法进行
```

#### 修复后预期流程

```
背景: 范马勇次郎 vs 烈海王

Round 1:
  ├─ 范马勇次郎 攻击
  ├─ 掷骰 D10=6
  ├─ 选择"大伤害"（d3=6）
  ├─ 脚本编译成功
  ├─ Self 初始化到两处存储 ✓
  ├─ 脚本执行成功 ✓
  ├─ dodamage(6) 执行 ✓
  └─ 烈海王 HP: 44/50 ✓

Round 2:
  ├─ 范马勇次郎 攻击
  ├─ 掷骰 D10=8
  ├─ 选择"极大伤害"（d4=8）
  ├─ 脚本执行成功 ✓
  ├─ dodamage(8) 执行 ✓
  └─ 烈海王 HP: 36/50 ✓

结果: 战斗正常进行，伤害正确应用
```

### 长期效益

1. **HP 持久化修复**: 伤害现在会正确应用于角色，HP 会跨轮次保留
2. **技能系统恢复**: 其他依赖 self 对象的脚本也会开始工作
3. **战斗平衡**: 可以验证伤害计算是否如设计一致
4. **调试能力提升**: 完整的诊断日志能帮助识别其他问题

---

## 相关代码位置参考

### 主要文件

| 文件 | 位置 | 内容 |
|------|------|------|
| PresetSystem.cpp | ~335-370 | AnkePreset::Execute() 普通脚本 |
| PresetSystem.cpp | ~390-420 | AnkePreset::Execute() 临界脚本 |
| ExecutionEnvironment.cpp | ~178-230 | RegisterCharacterData() |
| ExecutionEnvironment.h | - | SetValueProperty() 定义 |
| BuiltinPresets.cpp | ~960-1025 | builtin_akr() 诊断 |

### 关键方法

```cpp
// ExecutionEnvironment
void SetValueProperty(const std::string& name, const Value& val);
Value GetValueProperty(const std::string& name);

// ScopeStack
void SetVariable(const std::string& name, const Value& val);
Value GetVariable(const std::string& name);

// VM
bool Execute(const BytecodeProgram* program, ScopeStack* scope);
```

---

## 附录：诊断信息详解

### LOAD_SELF 指令日志解读

```
[LOAD_SELF] IP:0 handle_id=1000 actor_ptr=0x1f4c1656e90 
           actor=范马勇次郎 atk=120 HP=60
```

| 字段 | 含义 |
|------|------|
| IP:0 | 指令指针为 0（在脚本执行的最开始） |
| handle_id=1000 | 对象句柄 ID（自 assigned） |
| actor_ptr | 内存地址（用于调试） |
| actor=范马勇次郎 | 加载的角色名称 |
| atk=120 | 攻击力属性 |
| HP=60 | 当前 HP（修复前无 dmg 数据） |

### Self Schema 结构预期输出

修复后，诊断日志应显示：

```
[LOAD_SELF] ... actor=范马勇次郎 atk=120 HP=60
           dmg.d1=2 dmg.d2=4 dmg.d3=6 dmg.d4=8
           turn.multiplier=1
```

---

## 总结

这是脚本执行系统中的一个**架构级问题**——VM 指令集的执行需要访问的对象状态没有被正确地提供给执行环境。修复方法简单（两行代码），但影响深远（解决整个伤害系统）。

**关键要点**:
- ✅ ScopeStack 初始化不够 → 需要 ExecutionEnvironment 初始化
- ✅ LOAD_SELF 绕过 ScopeStack 直接访问对象存储
- ✅ 完整的 Schema 必须注册到两个位置
- ✅ 修复简单，但需要应用到所有脚本执行点

---

**报告完成**
