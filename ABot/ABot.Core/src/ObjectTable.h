/**
 * @file ObjectTable.h
 * @brief SchemaValue 对象管理表 - Handle 系统的核心存储
 * 
 * 核心职责：
 * 1. 存储 SchemaValue 对象（通过 handle ID 索引）
 * 2. 分配/释放 handle
 * 3. 支持引用计数（未来支持垃圾回收）
 * 4. 支持克隆（用于隔离或 Clone 方法）
 * 
 * 设计特点：
 * - 线程安全（std::mutex 保护）
 * - 单调递增的 ID 分配（原子操作）
 * - 引用计数跟踪（预留未来使用）
 * - 支持事务/快照（预留接口）
 */

#pragma once

#include <unordered_map>
#include <mutex>
#include <atomic>
#include <memory>

#include "ObjectHandle.h"

namespace abot {

// forward declaration
class SchemaValue;

// forward declaration
class Value;

/**
 * @class ObjectTable
 * @brief SchemaValue 的集中管理容器
 * 
 * 核心操作：
 * 1. Create(SchemaValue) → ObjectHandle
 *    - 分配新 ID
 *    - 深拷贝输入 SchemaValue
 *    - 返回 handle
 * 
 * 2. Get(ObjectHandle) → SchemaValue&
 *    - 查询 handle 对应的 SchemaValue
 *    - 返回非 const 引用（允许修改）
 *    - 若 handle 无效，抛出异常或返回 nullptr（TBD）
 * 
 * 3. Clone(ObjectHandle) → ObjectHandle
 *    - 深拷贝现有对象
 *    - 分配新 handle
 *    - 用于隔离（避免修改原始对象）
 * 
 * 4. Release(ObjectHandle)
 *    - 递减引用计数
 *    - 若计数 = 0，删除对象
 *    - 用于生命周期管理
 * 
 * 5. Clear()
 *    - 清空所有对象（重置）
 */
class ObjectTable {
public:
    /**
     * 默认构造函数
     */
    ObjectTable();
    
    /**
     * 析构函数（清理所有 SchemaValue）
     */
    ~ObjectTable();
    
    // 禁止拷贝（ObjectTable 是全局单例）
    ObjectTable(const ObjectTable&) = delete;
    ObjectTable& operator=(const ObjectTable&) = delete;
    
    // 允许移动
    ObjectTable(ObjectTable&& other) noexcept;
    ObjectTable& operator=(ObjectTable&& other) noexcept;
    
    // ============ 核心操作 ============
    
    /**
     * 创建新对象
     * 
     * @param initial 初始 SchemaValue（会被深拷贝）
     * @return 分配的 ObjectHandle
     * 
     * @post 返回的 handle 总是有效的（ID > 0）
     */
    ObjectHandle Create(const SchemaValue& initial);
    
    /**
     * 创建空对象
     * 
     * @return 分配的 ObjectHandle
     */
    ObjectHandle CreateEmpty();
    
    /**
     * 获取对象引用（非 const）
     * 
     * @param handle 对象 handle
     * @return SchemaValue 的引用
     * @throw std::out_of_range 如果 handle 无效
     * 
     * 注意：返回的引用仅在 handle 有效期内有效
     *      若对象被删除（Release 后引用计数 = 0），引用变为悬空指针
     */
    SchemaValue& Get(const ObjectHandle& handle);
    
    /**
     * 获取对象引用（const）
     */
    const SchemaValue& Get(const ObjectHandle& handle) const;
    
    /**
     * 克隆对象
     * 
     * @param handle 源对象 handle
     * @return 新对象的 handle（完全独立副本）
     * @throw std::out_of_range 如果 handle 无效
     * 
     * 用途：
     * - 隔离修改（修改副本不影响原始）
     * - 事务回滚（保存克隆作为快照）
     * - 深拷贝操作（用于 Value::Clone()）
     */
    ObjectHandle Clone(const ObjectHandle& handle);
    
    /**
     * 添加对象的引用计数
     * 
     * @param handle 对象 handle
     * @param count 增加数量（默认 1）
     * 
     * 用途：当另一个 Value 或数据结构引用该对象时调用
     */
    void AddReference(const ObjectHandle& handle, int count = 1);
    
    /**
     * 删除对象的引用计数
     * 
     * @param handle 对象 handle
     * @param count 减少数量（默认 1）
     * 
     * 行为：
     * - 若计数 > 0，递减
     * - 若计数 = 0，删除对象并释放内存
     * - 若 handle 无效，此操作无效
     */
    void Release(const ObjectHandle& handle, int count = 1);
    
    /**
     * 获取对象的当前引用计数
     * 
     * @param handle 对象 handle
     * @return 引用计数（若 handle 无效返回 0）
     */
    int GetRefCount(const ObjectHandle& handle) const;
    
    /**
     * 清空所有对象
     * 
     * 用途：重置或清理环境
     */
    void Clear();
    
    /**
     * 获取当前表中的对象数量
     */
    size_t GetObjectCount() const;
    
    /**
     * 获取下一个可用的 handle ID（用于诊断）
     */
    uint64_t GetNextHandleID() const {
        return next_id_.load();
    }
    
    // ============ 事务支持（未来功能）============
    
    /**
     * 保存快照（为了支持回滚）
     */
    // void BeginTransaction();
    
    /**
     * 提交事务
     */
    // void Commit();
    
    /**
     * 回滚至最后一个检查点
     */
    // void Rollback();
    
private:
    // ============ 私有成员 ============
    
    /**
     * 对象存储：ID → SchemaValue
     */
    std::unordered_map<uint64_t, std::unique_ptr<SchemaValue>> objects_;
    
    /**
     * 引用计数：ID → count
     */
    std::unordered_map<uint64_t, int> refcount_;
    
    /**
     * 互斥锁（线程安全）
     */
    mutable std::mutex mtx_;
    
    /**
     * 下一个可用的 ID（原子递增）
     */
    std::atomic<uint64_t> next_id_;
    
    // ============ 私有方法 ============
    
    /**
     * 分配新 ID（线程安全）
     */
    uint64_t AllocateID();
};

}  // namespace abot

