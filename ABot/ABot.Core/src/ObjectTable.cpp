/**
 * @file ObjectTable.cpp
 * @brief ObjectTable 的实现
 */

#include "ObjectTable.h"
#include "SchemaValue.h"
#include "RoundManager.h"

extern abot::RoundManager* g_current_round_manager;

namespace abot {

ObjectTable::ObjectTable() : next_id_(1) {}

ObjectTable::~ObjectTable() {
    Clear();
}

ObjectTable::ObjectTable(ObjectTable&& other) noexcept
    : objects_(std::move(other.objects_)),
      refcount_(std::move(other.refcount_)),
      next_id_(other.next_id_.load()) {
}

ObjectTable& ObjectTable::operator=(ObjectTable&& other) noexcept {
    if (this != &other) {
        std::lock_guard<std::mutex> lock(mtx_);
        Clear();
        objects_ = std::move(other.objects_);
        refcount_ = std::move(other.refcount_);
        next_id_.store(other.next_id_.load());
    }
    return *this;
}

uint64_t ObjectTable::AllocateID() {
    // 原子递增，保证每个 ID 唯一
    // 使用 fetch_add(1, ...) 获得当前值并递增
    uint64_t id = next_id_.fetch_add(1, std::memory_order_relaxed);
    return id;
}

ObjectHandle ObjectTable::Create(const SchemaValue& initial) {
    std::lock_guard<std::mutex> lock(mtx_);
    
    uint64_t id = AllocateID();
    // 深拷贝输入 SchemaValue
    objects_[id] = std::make_unique<SchemaValue>(initial);
    refcount_[id] = 1;  // 初始引用计数为 1
    
    // 🟥 【任务4】记录对象创建
    if (g_current_round_manager) {
        char buf[256];
        snprintf(buf, sizeof(buf),
            "[DIAG][OBJ] Create: handle=%llu fields=%zu",
            (unsigned long long)id,
            initial.GetAllFields().size());
        g_current_round_manager->AppendSkillTriggerLog(buf);
    }
    
    return ObjectHandle(id);
}

ObjectHandle ObjectTable::CreateEmpty() {
    std::lock_guard<std::mutex> lock(mtx_);
    
    uint64_t id = AllocateID();
    objects_[id] = std::make_unique<SchemaValue>();
    refcount_[id] = 1;
    
    return ObjectHandle(id);
}

SchemaValue& ObjectTable::Get(const ObjectHandle& handle) {
    std::lock_guard<std::mutex> lock(mtx_);
    
    if (handle.IsNull()) {
        throw std::runtime_error("Cannot get from null handle");
    }
    
    auto it = objects_.find(handle.GetID());
    if (it == objects_.end()) {
        throw std::out_of_range("Object handle not found: " + std::to_string(handle.GetID()));
    }
    
    // 🟥 【任务4】记录对象访问和字段数量
    if (g_current_round_manager && it->second) {
        char buf[256];
        snprintf(buf, sizeof(buf),
            "[DIAG][OBJ] Get: handle=%llu fields=%zu",
            (unsigned long long)handle.GetID(),
            it->second->GetAllFields().size());
        g_current_round_manager->AppendSkillTriggerLog(buf);
    }
    
    return *it->second;
}

const SchemaValue& ObjectTable::Get(const ObjectHandle& handle) const {
    std::lock_guard<std::mutex> lock(mtx_);
    
    if (handle.IsNull()) {
        throw std::runtime_error("Cannot get from null handle");
    }
    
    auto it = objects_.find(handle.GetID());
    if (it == objects_.end()) {
        throw std::out_of_range("Object handle not found: " + std::to_string(handle.GetID()));
    }
    
    return *it->second;
}

ObjectHandle ObjectTable::Clone(const ObjectHandle& handle) {
    std::lock_guard<std::mutex> lock(mtx_);
    
    auto it = objects_.find(handle.GetID());
    if (it == objects_.end()) {
        throw std::out_of_range("Cannot clone non-existent handle: " + std::to_string(handle.GetID()));
    }
    
    // 深拷贝现有对象
    uint64_t new_id = AllocateID();
    objects_[new_id] = std::make_unique<SchemaValue>(*it->second);
    refcount_[new_id] = 1;
    
    return ObjectHandle(new_id);
}

void ObjectTable::AddReference(const ObjectHandle& handle, int count) {
    std::lock_guard<std::mutex> lock(mtx_);
    
    if (handle.IsNull()) return;
    
    auto it = refcount_.find(handle.GetID());
    if (it != refcount_.end()) {
        it->second += count;
    }
}

void ObjectTable::Release(const ObjectHandle& handle, int count) {
    std::lock_guard<std::mutex> lock(mtx_);
    
    if (handle.IsNull()) return;
    
    auto ref_it = refcount_.find(handle.GetID());
    if (ref_it == refcount_.end()) return;
    
    ref_it->second -= count;
    
    // 若引用计数 <= 0，删除对象
    if (ref_it->second <= 0) {
        objects_.erase(handle.GetID());
        refcount_.erase(handle.GetID());
    }
}

int ObjectTable::GetRefCount(const ObjectHandle& handle) const {
    std::lock_guard<std::mutex> lock(mtx_);
    
    if (handle.IsNull()) return 0;
    
    auto it = refcount_.find(handle.GetID());
    if (it == refcount_.end()) return 0;
    
    return it->second;
}

void ObjectTable::Clear() {
    // 注意：不需要二次获取锁，因为只在构造/析构时调用
    // 或从移动操作中调用（此时 mtx_ 已被锁）
    objects_.clear();
    refcount_.clear();
    next_id_.store(1, std::memory_order_relaxed);
}

size_t ObjectTable::GetObjectCount() const {
    std::lock_guard<std::mutex> lock(mtx_);
    return objects_.size();
}

}  // namespace abot

