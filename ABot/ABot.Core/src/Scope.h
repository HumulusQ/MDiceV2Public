/**
 * @file Scope.h
 * @brief ABOT 作用域系统 - 支持6层作用域
 * 
 * 作用域层级（从内到外）：
 * ======================
 * 1. 局域作用域(Local)     - 函数内部变量
 * 2. 作用域作用域(Scope)   - 代码块内变量
 * 3. 技能作用域(Skill)     - 技能内变量
 * 4. 角色作用域(Character) - 角色属性
 * 5. 回合作用域(Turn)      - 回合内变量
 * 6. 场地作用域(Field)     - 整个战场变量
 * 
 * 变量查询顺序：从内向外搜索，找到第一个就返回
 */

#ifndef ABOT_SCOPE_H
#define ABOT_SCOPE_H

#include "Value.h"
#include <string>
#include <map>
#include <memory>

namespace abot {

// 作用域类型
enum class ScopeType : int {
    Local,      // 局域作用域
    Scope,      // 作用域
    Skill,      // 技能作用域
    Character,  // 角色作用域
    Turn,       // 回合作用域
    Field,      // 场地作用域（最外层）
};

/**
 * @brief 单个作用域对象
 * 包含该层级的所有变量定义
 */
class Scope {
public:
    // ============ 构造函数 ============
    Scope(ScopeType type, Scope* parent = nullptr);
    ~Scope();

    // 禁止拷贝
    Scope(const Scope&) = delete;
    Scope& operator=(const Scope&) = delete;

    // ============ 变量管理 ============
    
    /**
     * @brief 设置变量
     * 如果变量已存在则覆盖，否则创建新变量
     */
    void SetVariable(const std::string& name, const Value& value);

    /**
     * @brief 获取变量
     * 先在当前作用域查找，如果不存在则向上查找父作用域
     * @return 找到的值，或返回Null如果不存在
     */
    Value GetVariable(const std::string& name) const;

    /**
     * @brief 检查变量是否存在
     * 在当前作用域查找（不向上查找）
     */
    bool HasVariable(const std::string& name) const;

    /**
     * @brief 在当前作用域删除变量
     */
    void DeleteVariable(const std::string& name);

    // ============ 作用域导航 ============
    
    ScopeType GetType() const { return type_; }
    Scope* GetParent() const { return parent_; }
    
    /**
     * @brief 向上查找特定类型的作用域
     * 例如：找最近的角色作用域
     */
    Scope* FindScopeOfType(ScopeType type);

    // ============ 调试 ============
    void PrintVariables() const;

private:
    ScopeType type_;
    Scope* parent_;
    std::map<std::string, Value> variables_;
};

/**
 * @brief 作用域栈管理器
 * 管理从Field到Local的整个作用域链
 */
class ScopeStack {
public:
    // ============ 构造函数 ============
    ScopeStack();
    ~ScopeStack();

    // ============ 作用域操作 ============
    
    /**
     * @brief 进入新作用域
     * 创建一个新的Scope节点，成为当前作用域
     */
    void EnterScope(ScopeType type);

    /**
     * @brief 退出当前作用域
     * 返回父作用域
     */
    void ExitScope();

    /**
     * @brief 获取当前作用域
     */
    Scope* GetCurrentScope() const { return current_; }

    /**
     * @brief 获取根作用域（Field级别）
     */
    Scope* GetRootScope() const { return root_; }

    /**
     * @brief 获取特定类型的作用域
     */
    Scope* GetScopeOfType(ScopeType type);

    // ============ 变量快捷操作 ============
    
    void SetVariable(const std::string& name, const Value& value) {
        if (current_) current_->SetVariable(name, value);
    }

    Value GetVariable(const std::string& name) const {
        if (current_) return current_->GetVariable(name);
        return Value();
    }

    bool HasVariable(const std::string& name) const {
        if (current_) return current_->HasVariable(name);
        return false;
    }

    // ============ 特殊变量 - self/enemy/allies ============
    
    /**
     * @brief 设置当前角色引用
     * 用于expr()中的 self 访问
     */
    void SetSelfReference(const Value& self);

    /**
     * @brief 获取当前角色引用
     */
    Value GetSelfReference() const { return self_reference_; }

    /**
     * @brief 设置敌方集合
     * 用于expr()中的 enemy 访问
     */
    void SetEnemyList(const Value& enemies);

    /**
     * @brief 获取敌方集合
     */
    Value GetEnemyList() const { return enemy_list_; }

    /**
     * @brief 设置友方集合
     * 用于expr()中的 allies 访问
     */
    void SetAlliesList(const Value& allies);

    /**
     * @brief 获取友方集合
     */
    Value GetAlliesList() const { return allies_list_; }

private:
    Scope* root_;     // 根作用域（Field）
    Scope* current_;  // 当前作用域
    
    // 特殊变量
    Value self_reference_;
    Value enemy_list_;
    Value allies_list_;
};

}  // namespace abot

#endif  // ABOT_SCOPE_H
