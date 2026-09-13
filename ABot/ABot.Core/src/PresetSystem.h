/**
 * @file PresetSystem.h
 * @brief 统一的四层预设系统
 * 
 * 四种预设类型：
 * 1. Function Preset - 基础函数操作
 * 2. ANKE Preset - 加权投掷集合
 * 3. Skill Preset - 技能定义
 * 4. State Preset - 状态效果
 */

#pragma once

#include "ExecutionEnvironment.h"
#include "Bytecode.h"
#include "Value.h"
#include <string>
#include <map>
#include <unordered_map>
#include <memory>
#include <vector>
#include <functional>

namespace abot {

// ============ 前置声明 ============

struct SkillTriggerMessage;  // 来自 SkillTriggerSystem.h

// ============ 预设类型枚举 ============

enum class PresetType {
    FUNCTION = 0,    // 函数预设
    ANKE = 1,        // ANKE 伤害计算系统
    SKILL = 2,       // 技能预设
    STATE = 3        // 状态预设
};


// ============ 顶层：通用预设基类 ============

/**
 * @brief 预设基类 - 所有预设的共同接口
 */
class PresetBase {
public:
    virtual ~PresetBase() = default;
    
    // 基础信息
    virtual PresetType GetType() const = 0;
    virtual std::string GetName() const = 0;
    virtual bool IsBuiltin() const = 0;
    bool IsUserDefined() const { return !IsBuiltin(); }
    
    // 执行
    virtual int Execute(ExecutionEnvironment* env) = 0;
    
    // 序列化
    virtual std::string ToXml() const = 0;
    
    // 🟦 动态字段系统：所有预设类型共享的动态属性表
    std::unordered_map<std::string, Value> extra;  // 用户扩展字段 + 预设字段
};


// ============ 第一层：函数预设(Function Presets) ============

/**
 * 函数预设 - 对应 C 内置函数的包装
 * 
 * 系统提供的函数预设：
 * - dodamage(damage, tag)
 * - akr(anke_name)
 * - random(min, max)
 * - ShiftAttacker()
 * - 等等
 */
class FunctionPreset : public PresetBase {
public:
    using FunctionPtr = std::function<int(ExecutionEnvironment*)>;
    
    FunctionPreset(const std::string& name, FunctionPtr func, bool builtin = false);
    ~FunctionPreset() = default;
    
    PresetType GetType() const override { return PresetType::FUNCTION; }
    std::string GetName() const override { return name_; }
    bool IsBuiltin() const override { return builtin_; }
    
    int Execute(ExecutionEnvironment* env) override;
    
    std::string ToXml() const override;
    
private:
    std::string name_;
    FunctionPtr func_;
    bool builtin_;
};


// ============ 第二层：ANKE 预设(ANKE Presets) ============

/**
 * ANKE 选项 - ANKE 投掷集合中的单个选项
 */
struct AnkeOption {
    std::string name;                       // 选项名称
    int weight;                             // 权重（相对值）
    std::unique_ptr<BytecodeProgram> script;// 关联的脚本字节码
    
    // 临界选项对支持（es/ef）
    bool is_critical_pair;                  // 是否为大成功/大失败配对
    std::unique_ptr<BytecodeProgram> critical_failure_script;  // ef 脚本（当 is_critical_pair=true 时使用）
    
    AnkeOption() : weight(0), is_critical_pair(false) {}
    
    // 普通选项构造函数
    AnkeOption(const std::string& n, int w, std::unique_ptr<BytecodeProgram> s)
        : name(n), weight(w), script(std::move(s)), is_critical_pair(false) {}
    
    // 临界配对选项构造函数（es + ef）
    AnkeOption(const std::string& n, int w, 
               std::unique_ptr<BytecodeProgram> es_script,
               std::unique_ptr<BytecodeProgram> ef_script)
        : name(n), weight(w), script(std::move(es_script)), 
          is_critical_pair(true), critical_failure_script(std::move(ef_script)) {}
    
    // 不支持拷贝，使用 move 语义
    AnkeOption(const AnkeOption&) = delete;
    AnkeOption(AnkeOption&&) = default;
    AnkeOption& operator=(const AnkeOption&) = delete;
    AnkeOption& operator=(AnkeOption&&) = default;
};

/**
 * ANKE 预设 - 加权随机投掷集合
 */
class AnkePreset : public PresetBase {
public:
    explicit AnkePreset(const std::string& name);
    ~AnkePreset() = default;
    
    PresetType GetType() const override { return PresetType::ANKE; }
    std::string GetName() const override { return name_; }
    bool IsBuiltin() const override { return builtin_; }
    
    // ANKE 特有方法
    void AddOption(AnkeOption option);
    void SetBuiltin(bool builtin) { builtin_ = builtin; }
    
    /**
     * 执行：加权随机选择一个选项，执行其脚本
     */
    int Execute(ExecutionEnvironment* env) override;
    
    /**
     * 获取选中的选项（用于调试/显示）
     */
    const AnkeOption* GetSelectedOption() const { return selected_; }
    
    const std::vector<AnkeOption>& GetOptions() const { return options_; }
    
    /**
     * 获取最后一次掷骰的详细信息（用于战斗日志）
     */
    int GetLastRandomValue() const { return last_random_val_; }
    int GetLastSelectedIndex() const { return last_selected_index_; }
    int GetLastExecutionResult() const { return last_execution_result_; }
    int GetTotalWeight() const { return total_weight_; }
    
    std::string ToXml() const override;
    
private:
    std::string name_;
    std::vector<AnkeOption> options_;
    const AnkeOption* selected_;           // 最后一次选择的选项
    int total_weight_;
    bool builtin_;
    
    // 记录最后一次执行的详细信息（用于日志）
    int last_random_val_;      // 最后一次掷骰结果
    int last_selected_index_;  // 最后一次选择的选项下标
    int last_execution_result_; // 最后一次 VM 执行的结果代码
};


// ============ 第三层：技能预设(Skill Presets) ============

/**
 * 技能定义 - 根据 ABOT.md 规范
 * 
 * 技能参数由用户从 skillpara 中完全定义
 * 此结构作为灵活容器，不预置任何系统技能
 */
struct SkillDefinition {
    std::string id;                        // 技能唯一标识符
    std::string type;                      // ActSkill, onHitDealtSkill 等
    
    // 新增：消息参数签名（定义该技能接受的消息参数）
    // 注：由系统在初始化时根据 type 自动填充
    // std::unique_ptr<SkillMessageSignature> message_signature;
    
    // 现有字段
    std::map<std::string, std::string> parameters;  // 灵活参数映射
    std::unique_ptr<BytecodeProgram> def;  // 技能定义脚本字节码
    std::string original_expression;       // 原始表达式（用于诊断）
    
    SkillDefinition() {}
    
    /**
     * @brief 验证消息参数是否与该技能定义匹配
     * @param msg 要验证的消息
     * @return 是否有效
     */
    bool ValidateMessage(const SkillTriggerMessage& msg) const;
};

class SkillPreset : public PresetBase {
public:
    explicit SkillPreset(SkillDefinition&& def);
    ~SkillPreset() = default;
    
    PresetType GetType() const override { return PresetType::SKILL; }
    std::string GetName() const override { return def_.id; }
    bool IsBuiltin() const override { return builtin_; }
    
    /**
     * 执行技能（由具体实现处理脚本执行）
     */
    int Execute(ExecutionEnvironment* env) override;
    
    const SkillDefinition& GetDefinition() const { return def_; }
    void SetBuiltin(bool builtin) { builtin_ = builtin; }
    
    // 获取技能参数
    std::string GetParameter(const std::string& key, const std::string& default_val = "") const;
    bool HasParameter(const std::string& key) const;
    
    std::string ToXml() const override;
    
private:
    SkillDefinition def_;
    bool builtin_;
};


// ============ 第四层：状态预设(State Presets) ============

/**
 * 状态效果单元
 */
/**
 * 状态定义 - 根据 ABOT.md 规范
 * 
 * 状态参数由用户完全定义，此结构作为灵活容器
 */
struct StateDefinition {
    std::string id;                                // 状态唯一标识符
    std::string type;                              // buff, debuff, neutral 等
    int default_duration;                          // 默认持续时间（回合数）
    std::map<std::string, std::string> parameters; // 用户定义的参数（如：dmg, heal 等）
    std::unique_ptr<BytecodeProgram> on_apply;    // 应用时执行
    std::unique_ptr<BytecodeProgram> on_tick;     // 每回合执行
    std::unique_ptr<BytecodeProgram> on_remove;   // 移除时执行
    
    StateDefinition() : default_duration(1) {}
    StateDefinition(const StateDefinition&) = delete;  // 不支持复制，使用 move
    StateDefinition(StateDefinition&&) = default;      // 支持移动
    StateDefinition& operator=(const StateDefinition&) = delete;  // 不支持赋值
    StateDefinition& operator=(StateDefinition&&) = default;      // 支持移动赋值
};


class StatePreset : public PresetBase {
public:
    explicit StatePreset(StateDefinition&& def);
    ~StatePreset() = default;
    
    PresetType GetType() const override { return PresetType::STATE; }
    std::string GetName() const override { return def_.id; }
    bool IsBuiltin() const override { return builtin_; }
    
    /**
     * 执行：应用状态效果到目标
     */
    int Execute(ExecutionEnvironment* env) override;
    
    const StateDefinition& GetDefinition() const { return def_; }
    void SetBuiltin(bool builtin) { builtin_ = builtin; }
    
    // 获取状态参数
    std::string GetParameter(const std::string& key, const std::string& default_val = "") const;
    bool HasParameter(const std::string& key) const;
    
    std::string ToXml() const override;
    
private:
    StateDefinition def_;
    bool builtin_;
};


// ============ 顶层管理器：统一预设注册表 ============

/**
 * @brief 全局预设管理系统
 * 
 * 职责：
 * 1. 注册系统预设
 * 2. 接受用户定义的预设
 * 3. 查询和执行预设
 * 4. 冲突处理（用户预设覆写系统预设）
 */
class PresetRegistry {
public:
    PresetRegistry() = default;
    ~PresetRegistry() = default;
    
    // ============ 注册接口 ============
    
    // 注册函数预设
    void RegisterFunction(const std::string& name,
                         FunctionPreset::FunctionPtr func,
                         bool is_builtin = false);
    
    // 注册 ANKE 预设
    void RegisterAnke(const std::string& name,
                     std::unique_ptr<AnkePreset> anke);
    
    // 注册技能预设
    void RegisterSkill(SkillDefinition&& def,
                      bool is_builtin = false);
    
    // 注册状态预设
    void RegisterState(StateDefinition&& def,
                      bool is_builtin = false);
    
    // ============ 查询接口 ============
    
    PresetBase* GetPreset(PresetType type, const std::string& name);
    
    FunctionPreset* GetFunction(const std::string& name);
    AnkePreset* GetAnke(const std::string& name);
    SkillPreset* GetSkill(const std::string& name);
    StatePreset* GetState(const std::string& name);
    
    bool HasPreset(PresetType type, const std::string& name) const;
    
    // ============ 执行接口 ============
    
    /**
     * 执行指定的预设
     */
    int ExecutePreset(PresetType type, const std::string& name,
                     ExecutionEnvironment* env);
    
    // ============ 调试/诊断 ============
    
    /**
     * 列出所有注册的预设（按类型）
     */
    std::vector<std::string> ListPresets(PresetType type) const;
    
    /**
     * 获取预设信息（用于显示）
     */
    std::string GetPresetInfo(PresetType type, const std::string& name) const;
    
    // ============ 全局访问 ============
    
    static PresetRegistry* GetInstance();
    
private:
    std::map<std::string, std::unique_ptr<FunctionPreset>> functions_;
    std::map<std::string, std::unique_ptr<AnkePreset>> ankes_;
    std::map<std::string, std::unique_ptr<SkillPreset>> skills_;
    std::map<std::string, std::unique_ptr<StatePreset>> states_;
};

}  // namespace abot
