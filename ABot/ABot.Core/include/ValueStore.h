#pragma once

/**
 * @file ValueStore.h
 * @brief Phase 4: 文件存储和加载系统
 * 
 * 功能：
 * - Save(ValueV2, filename) - 保存为DSL文件
 * - Load(filename) - 从DSL文件加载
 * - 自动保存机制
 * - 版本管理和迁移
 * - 目录遍历和批量操作
 * 
 * 示例：
 *   ValueStore store("config/");
 *   store.Save(value, "character.dsl");
 *   ValueV2 loaded = store.Load("character.dsl");
 *   
 *   // 自动保存
 *   store.SetAutoSave(true);
 *   store.SaveInterval(1000);  // 每1000ms自动保存
 */

#include <string>
#include <memory>
#include <map>
#include <vector>
#include "ValueV2.h"
#include "ValueV2Serializer.h"

namespace ABot {

/**
 * @class StoreException
 * @brief 文件存储异常
 */
class StoreException : public std::exception
{
public:
    explicit StoreException(const std::string& message) 
        : message_(message) {}
    
    const char* what() const noexcept override { return message_.c_str(); }

private:
    std::string message_;
};

/**
 * @class FileMetadata
 * @brief 文件元数据
 */
struct FileMetadata
{
    std::string filename;
    std::string format_version;      // "1.0", "2.0" 等
    std::string created_time;        // ISO 8601格式
    std::string modified_time;
    size_t file_size;                // 字节数
    std::string checksum;            // MD5或其他校验和
    std::string description;         // 可选描述
};

/**
 * @class ValueStore
 * @brief ValueV2对象的文件存储系统
 * 
 * 功能：
 * - 保存和加载ValueV2对象为DSL文件
 * - 自动保存机制
 * - 文件版本管理
 * - 数据迁移
 * - 备份和恢复
 */
class ValueStore
{
public:
    /**
     * @brief 构造ValueStore
     * 
     * @param basePath 基础目录路径（必须存在或自动创建）
     */
    explicit ValueStore(const std::string& basePath);

    /**
     * @brief 析构，执行清理（保存未提交的数据等）
     */
    ~ValueStore();

    // ========================================================
    // 基本操作
    // ========================================================

    /**
     * @brief 保存ValueV2到DSL文件
     * 
     * @param value 待保存的ValueV2对象
     * @param filename 相对于basePath的文件名
     * @param backup 是否创建备份（.backup后缀）
     * 
     * @throw StoreException 如果文件操作失败
     * 
     * 路径示例：
     * - "character.dsl" → basePath/character.dsl
     * - "data/hero.dsl" → basePath/data/hero.dsl
     */
    void Save(const ValueV2& value, 
              const std::string& filename, 
              bool backup = true);

    /**
     * @brief 从DSL文件加载ValueV2
     * 
     * @param filename 相对于basePath的文件名
     * @return 加载的ValueV2对象
     * 
     * @throw StoreException 如果文件不存在或格式非法
     */
    ValueV2 Load(const std::string& filename);

    /**
     * @brief 检查文件是否存在
     * 
     * @param filename 相对于basePath的文件名
     * @return true表示文件存在
     */
    bool Exists(const std::string& filename) const;

    /**
     * @brief 删除文件
     * 
     * @param filename 相对于basePath的文件名
     * @return true表示删除成功
     */
    bool Delete(const std::string& filename);

    // ========================================================
    // 自动保存机制
    // ========================================================

    /**
     * @brief 启用自动保存
     * 
     * @param enabled 是否启用
     */
    void SetAutoSave(bool enabled);

    /**
     * @brief 设置自动保存间隔
     * 
     * @param intervalMs 间隔（毫秒）
     */
    void SetAutoSaveInterval(unsigned int intervalMs);

    /**
     * @brief 注册自动保存对象
     * 
     * @param key 对象标识（用于自动保存）
     * @param value ValueV2对象引用
     * @param filename 自动保存的文件名
     */
    void RegisterAutoSave(const std::string& key, 
                         const ValueV2& value, 
                         const std::string& filename);

    /**
     * @brief 取消注册自动保存
     * 
     * @param key 对象标识
     */
    void UnregisterAutoSave(const std::string& key);

    /**
     * @brief 立即执行所有待自动保存的对象
     */
    void FlushAutoSave();

    // ========================================================
    // 版本管理和迁移
    // ========================================================

    /**
     * @brief 获取文件元数据
     * 
     * @param filename 文件名
     * @return 元数据信息
     */
    FileMetadata GetMetadata(const std::string& filename) const;

    /**
     * @brief 获取所有版本（如果支持版本控制）
     * 
     * @param filename 文件名
     * @return 版本列表
     */
    std::vector<FileMetadata> GetVersionHistory(const std::string& filename) const;

    /**
     * @brief 恢复到特定版本
     * 
     * @param filename 文件名
     * @param version 版本号
     * @return 恢复的ValueV2对象
     */
    ValueV2 RestoreVersion(const std::string& filename, 
                          size_t version);

    /**
     * @brief 检查格式版本是否兼容
     * 
     * @param version 格式版本字符串：
     * @return true表示兼容且无需迁移
     */
    static bool IsVersionCompatible(const std::string& version);

    /**
     * @brief 迁移数据到新格式版本
     * 
     * @param value 待迁移的值
     * @param fromVersion 源格式版本
     * @param toVersion 目标格式版本
     * @return 迁移后的ValueV2
     */
    static ValueV2 MigrateVersion(const ValueV2& value, 
                                  const std::string& fromVersion, 
                                  const std::string& toVersion);

    // ========================================================
    // 批量操作
    // ========================================================

    /**
     * @brief 列出目录下的所有.dsl文件
     * 
     * @param directory 相对于basePath的目录（"" 表示根）
     * @param recursive 是否递归
     * @return 文件名列表
     */
    std::vector<std::string> ListFiles(
        const std::string& directory = "", 
        bool recursive = false) const;

    /**
     * @brief 批量加载目录下的所有文件
     * 
     * @param directory 相对于basePath的目录
     * @return 文件名→ValueV2的映射
     */
    std::map<std::string, ValueV2> LoadDirectory(
        const std::string& directory);

    /**
     * @brief 批量保存多个对象
     * 
     * @param values 文件名→ValueV2的映射
     */
    void SaveDirectory(
        const std::map<std::string, ValueV2>& values);

    // ========================================================
    // 备份和恢复
    // ========================================================

    /**
     * @brief 创建单个文件的备份
     * 
     * @param filename 文件名
     * @return 备份文件名
     */
    std::string CreateBackup(const std::string& filename);

    /**
     * @brief 创建整个目录的备份
     * 
     * @param sourceDir 源目录
     * @param backupDir 备份目录
     * @return 备份目录名
     */
    std::string CreateDirectoryBackup(
        const std::string& sourceDir, 
        const std::string& backupDir);

    /**
     * @brief 恢复备份
     * 
     * @param backupFile 备份文件
     * @param targetFile 目标文件
     */
    void RestoreBackup(const std::string& backupFile, 
                      const std::string& targetFile);

    // ========================================================
    // 配置和状态
    // ========================================================

    /**
     * @brief 获取基础路径
     * 
     * @return 存储的基础路径
     */
    std::string GetBasePath() const { return base_path_; }

    /**
     * @brief 获取当前容量信息
     * 
     * @return 总文件数、总大小等
     */
    struct StorageInfo {
        size_t total_files = 0;
        size_t total_size = 0;  // 字节
        size_t largest_file = 0;
        std::string largest_file_name;
    };

    StorageInfo GetStorageInfo() const;

private:
    std::string base_path_;
    bool auto_save_enabled_ = false;
    unsigned int auto_save_interval_ms_ = 5000;  // 默认5秒

    // 待自动保存的对象
    struct AutoSaveEntry {
        std::string key;
        std::string filename;
        // 注意：这里不能直接存储ValueV2引用，需要其他机制
    };
    std::map<std::string, AutoSaveEntry> auto_save_map_;

    // 内部辅助方法
    std::string GetFullPath(const std::string& filename) const;
    void EnsureDirectoryExists(const std::string& directory);
    std::string GenerateBackupFilename(const std::string& filename) const;
    std::string ReadFileContent(const std::string& path) const;
    void WriteFileContent(const std::string& path, const std::string& content);
};

} // namespace ABot
