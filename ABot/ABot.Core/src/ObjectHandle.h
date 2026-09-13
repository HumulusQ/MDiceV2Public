/**
 * @file ObjectHandle.h
 * @brief Handle 系统的核心 - 轻量级对象引用
 * 
 * 设计思想：
 * - Handle 是一个简单的 uint64_t ID，不持有指针
 * - 真实对象存储在 ObjectTable 中
 * - 消除深拷贝：Value 中的 schema_handle_ 在复制时保持不变
 * 
 * 优势：
 * 1. 32/64位通用
 * 2. 可序列化
 * 3. 线程安全的 ID（原子递增）
 * 4. 支持验证（检查 ID 是否有效）
 */

#pragma once

#include <cstdint>
#include <functional>
#include <ostream>
#include <string>

namespace abot {

/**
 * @class ObjectHandle
 * @brief 对象引用句柄（仅包含 ID，不持有指针）
 * 
 * 使用场景：
 * - Value::schema_handle_ 存储 SchemaValue 的 handle
 * - Value copy 时，handle 保持不变（不深拷贝！）
 * - 真实 SchemaValue 在 ObjectTable 中，通过 handle 查询
 */
class ObjectHandle {
public:
    /**
     * 构造空 handle（ID = 0）
     */
    ObjectHandle() : id_(0) {}
    
    /**
     * 用指定 ID 构造 handle
     */
    explicit ObjectHandle(uint64_t id) : id_(id) {}
    
    // 移动/拷贝默认行为（非常轻量）
    ObjectHandle(const ObjectHandle& other) = default;
    ObjectHandle& operator=(const ObjectHandle& other) = default;
    ObjectHandle(ObjectHandle&& other) noexcept = default;
    ObjectHandle& operator=(ObjectHandle&& other) noexcept = default;
    
    ~ObjectHandle() = default;
    
    // ============ 查询方法 ============
    
    /**
     * 获取原始 ID
     */
    uint64_t GetID() const { return id_; }
    
    /**
     * 判断是否为空（ID = 0）
     */
    bool IsNull() const { return id_ == 0; }
    
    /**
     * 判断是否有效（ID != 0）
     */
    bool IsValid() const { return id_ != 0; }
    
    // ============ 比较操作 ============
    
    /**
     * 相等比较
     */
    bool operator==(const ObjectHandle& other) const {
        return id_ == other.id_;
    }
    
    /**
     * 不等比较
     */
    bool operator!=(const ObjectHandle& other) const {
        return id_ != other.id_;
    }
    
    /**
     * 用于 std::map/std::set
     */
    bool operator<(const ObjectHandle& other) const {
        return id_ < other.id_;
    }
    
    bool operator<=(const ObjectHandle& other) const {
        return id_ <= other.id_;
    }
    
    bool operator>(const ObjectHandle& other) const {
        return id_ > other.id_;
    }
    
    bool operator>=(const ObjectHandle& other) const {
        return id_ >= other.id_;
    }
    
    // ============ I/O ============
    
    /**
     * 转为字符串（用于调试和日志）
     */
    std::string ToString() const {
        if (IsNull()) return "Handle(null)";
        return "Handle(" + std::to_string(id_) + ")";
    }
    
    /**
     * 输出流支持
     */
    friend std::ostream& operator<<(std::ostream& os, const ObjectHandle& h) {
        os << h.ToString();
        return os;
    }
    
private:
    uint64_t id_;
};

/**
 * @struct ObjectHandleHash
 * @brief std::unordered_map 的哈希函数
 * 
 * 用法:
 *   std::unordered_map<ObjectHandle, Value, ObjectHandleHash> map;
 */
struct ObjectHandleHash {
    std::size_t operator()(const ObjectHandle& h) const noexcept {
        return std::hash<uint64_t>()(h.GetID());
    }
};

}  // namespace abot

