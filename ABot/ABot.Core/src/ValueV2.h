/**
 * @file ValueV2.h (建议设计)
 * @brief ABOT 值系统 V2 - 改进设计
 * 
 * 改进亮点：
 * ==========
 * 1. 统一的Value对象模型（TypeInfo* + void*）
 * 2. 完整的引用语义（共享所有权）
 * 3. 动态Schema支持字段增删改查
 * 4. 深度路径访问 (a.b.c.d)
 * 5. 自动序列化支持
 * 6. 运行时反射能力
 * 
 * 与原设计的对比：
 * ===============
 * 原设计（当前）：
 *   ├─ Value = enum ValueType + union data
 *   ├─ 类型由枚举表示
 *   ├─ Schema = std::map<string, Value>
 *   └─ 只有值语义（复制）
 * 
 * 新设计（建议）：
 *   ├─ Value = TypeInfo* + shared_ptr<void>
 *   ├─ 类型由TypeInfo对象表示
 *   ├─ Schema = 动态对象（支持字段增删）
 *   ├─ 引用语义（共享+写时复制）
 *   └─ 完整反射和序列化
 */

#ifndef ABOT_VALUE_V2_H
#define ABOT_VALUE_V2_H

#include "TypeSystem.h"
#include "Value.h"
#include <memory>
#include <string>
#include <map>
#include <vector>

namespace abot {

// 前向声明
class ValueV2;

// ============================================================================
// Path Component 定义 - 用于路径解析
// ============================================================================

/**
 * @brief 路径组件结构体（用于解析 "a.b[0].c" 这样的路径）
 */
struct PathComponent {
    std::string fieldName;                 // 字段名
    int32_t arrayIndex = -1;              // 数组索引，-1 表示不存在
};

// ============================================================================
// SchemaValue V2 - 动态运行时对象
// ============================================================================

/**
 * @brief Schema对象（动态运行时对象）
 * 
 * 特点：
 * - 字段动态增删改查
 * - 支持嵌套对象
 * - 支持深度路径访问
 * - 写时复制（Copy-on-Write）
 */
class SchemaValueV2 {
public:
    SchemaValueV2() : fields_(std::make_shared<std::map<std::string, ValueV2>>()) {}
    
    // ---- 字段操作 ----
    ValueV2 GetField(const std::string& key) const;
    
    void SetField(const std::string& key, const ValueV2& value);
    
    /**
     * @brief 删除字段
     */
    void RemoveField(const std::string& key);
    
    /**
     * @brief 添加字段
     */
    void AddField(const std::string& key, const ValueV2& value);
    
    /**
     * @brief 检查字段是否存在
     */
    bool HasField(const std::string& key) const;
    
    /**
     * @brief 获取所有字段名
     */
    std::vector<std::string> GetKeys() const;
    
    // ---- 深度访问 ----
    /**
     * @brief 按路径获取字段
     * 示例：GetByPath("stats.hp.max") → 返回最终值
     */
    ValueV2 GetByPath(const std::string& path) const;
    
    /**
     * @brief 按路径设置字段
     * 如果中间路径不存在，autoCreate为true时自动创建中间对象
     */
    void SetByPath(const std::string& path, const ValueV2& value, bool autoCreate = true);
    
    // ---- 序列化 ----
    /**
     * @brief 序列化为DSL格式 {a=1, b=2, c={d=3}}
     */
    std::string Serialize() const;
    
    /**
     * @brief 反序列化
     */
    static SchemaValueV2 Deserialize(const std::string& dslText);
    
    // ---- 反射 ----
    /**
     * @brief 获取字段类型信息
     */
    TypeInfo* GetFieldType(const std::string& key) const;
    
    /**
     * @brief 获取所有字段和类型
     */
    std::map<std::string, TypeInfo*> GetFieldTypes() const;
    
    // ---- 迭代 ----
    class Iterator {
    public:
        Iterator(const std::shared_ptr<std::map<std::string, ValueV2>>& fields);
        bool HasNext() const;
        std::pair<std::string, ValueV2> Next();
    private:
        std::map<std::string, ValueV2>::iterator it_;
        std::map<std::string, ValueV2>::iterator end_;
    };
    
    Iterator Iterate() const;
    
    // ---- 内存管理 ----
    /**
     * @brief 获取字段映射的共享指针（用于写时复制）
     */
    std::shared_ptr<std::map<std::string, ValueV2>> GetFieldsPtr() const {
        return fields_;
    }
    
    // ---- 路径解析 ----
    /**
     * @brief 解析路径字符串为组件
     * 示例："player.stats[0].hp" → [{"player", {}}, {"stats", 0}, {"hp", {}}]
     */
    static std::vector<PathComponent> ParsePath(const std::string& path);
    
private:
    // 字段表使用shared_ptr支持引用共享
    std::shared_ptr<std::map<std::string, ValueV2>> fields_;
};

// ============================================================================
// ArrayValue V2 - 动态数组
// ============================================================================

class ArrayValueV2 {
public:
    ArrayValueV2() : elements_(std::make_shared<std::vector<ValueV2>>()) {}
    
    // ---- 访问 ----
    ValueV2 GetElement(size_t index) const;
    void SetElement(size_t index, const ValueV2& value);
    
    // ---- 修改 ----
    void PushBack(const ValueV2& value);
    void PopBack();
    void Insert(size_t index, const ValueV2& value);
    void Remove(size_t index);
    
    // ---- 查询 ----
    size_t GetSize() const;
    bool IsEmpty() const { return GetSize() == 0; }
    
    // ---- 序列化 ----
    std::string Serialize() const;
    static ArrayValueV2 Deserialize(const std::string& dslText);
    
    // ---- 迭代 ----
    std::vector<ValueV2> GetElements() const;
    
private:
    std::shared_ptr<std::vector<ValueV2>> elements_;
};

// ============================================================================
// Value V2 - 统一的值对象
// ============================================================================

/**
 * @brief ABOT运行时值对象
 * 
 * 结构：
 *   Value = TypeInfo* + shared_ptr<void>
 * 
 * 特点：
 * - 所有类型统一表示
 * - 引用语义（共享所有权）
 * - 完整的类型反射
 * - 安全的类型转换
 */
class ValueV2 {
public:
    // ============ 构造函数 ============
    ValueV2();                                    // Null值
    ValueV2(int64_t i);                           // Int
    ValueV2(double d);                            // Double
    ValueV2(bool b);                              // Bool
    ValueV2(const std::string& s);                // String
    ValueV2(const char* s);                       // C字符串 → String
    
    // 复制和移动
    ValueV2(const ValueV2& other);
    ValueV2(ValueV2&& other) noexcept;
    ValueV2& operator=(const ValueV2& other);
    ValueV2& operator=(ValueV2&& other) noexcept;
    
    ~ValueV2();
    
    // ============ 类型信息 ============
    /**
     * @brief 获取值的类型信息
     */
    TypeInfo* GetTypeInfo() const { return typeInfo_; }
    
    /**
     * @brief 获取值的类型名称
     */
    std::string GetTypeName() const;
    
    /**
     * @brief 检查是否为某个类型
     */
    bool IsType(const std::string& typeName) const;
    
    /**
     * @brief 检查是否为 Null 值
     */
    bool IsNull() const { return IsType("null"); }
    
    // ============ 类型转换 ============
    int64_t ToInt() const;
    double ToDouble() const;
    bool ToBool() const;
    std::string ToString() const;
    
    // ============ Schema操作（如果类型是Schema） ============
    /**
     * @brief 获取Schema字段
     * 前置条件：IsType("schema")
     */
    ValueV2 GetField(const std::string& key) const;
    
    /**
     * @brief 设置Schema字段
     */
    void SetField(const std::string& key, const ValueV2& value);
    
    /**
     * @brief 删除Schema字段
     */
    void RemoveField(const std::string& key);
    
    /**
     * @brief 添加Schema字段
     */
    void AddField(const std::string& key, const ValueV2& value);
    
    /**
     * @brief 按深度路径访问
     * 示例：GetByPath("player.stats.hp.max")
     */
    ValueV2 GetByPath(const std::string& path) const;
    
    /**
     * @brief 按深度路径设置
     */
    void SetByPath(const std::string& path, const ValueV2& value);
    
    // ============ Array操作（如果类型是Array） ============
    ValueV2 GetElement(size_t index) const;
    void SetElement(size_t index, const ValueV2& value);
    void PushBack(const ValueV2& value);
    void PopBack();
    size_t GetSize() const;
    
    // ============ 序列化 ============
    /**
     * @brief 序列化为DSL文本
     */
    std::string Serialize() const;
    
    /**
     * @brief 从DSL文本反序列化
     */
    static ValueV2 Deserialize(const std::string& dslText, TypeInfo* expectedType = nullptr);
    
    // ============ 工厂方法 ============
    static ValueV2 CreateSchema();
    static ValueV2 CreateArray();
    static ValueV2 CreateNull();
    
private:
    TypeInfo* typeInfo_;                         // 指向类型描述
    std::shared_ptr<void> data_;                 // 指向实际数据
    
    // 内部构造
    ValueV2(TypeInfo* typeInfo, std::shared_ptr<void> data)
        : typeInfo_(typeInfo), data_(data) {}
    
    friend class ValueBuilder;
};

// ============================================================================
// 便利函数
// ============================================================================

/**
 * @brief 检查两个值是否为引用同一对象
 * （用于判断是否是同一个Schema/Array实例）
 */
bool AreReferences(const ValueV2& a, const ValueV2& b);

}  // namespace abot

#endif  // ABOT_VALUE_V2_H
