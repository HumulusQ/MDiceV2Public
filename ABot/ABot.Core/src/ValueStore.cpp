#include "ValueStore.h"
#include <fstream>
#include <filesystem>
#include <sstream>
#include <chrono>
#include <algorithm>
#include <iomanip>

namespace fs = std::filesystem;

namespace ABot {

// ====================================================================
// ValueStore 构造和析构
// ====================================================================

ValueStore::ValueStore(const std::string& basePath)
    : base_path_(basePath)
{
    // 确保基础目录存在
    EnsureDirectoryExists(base_path_);
}

ValueStore::~ValueStore()
{
    // 析构时执行自动保存
    if (auto_save_enabled_) {
        FlushAutoSave();
    }
}

// ====================================================================
// 基本操作
// ====================================================================

void ValueStore::Save(const ValueV2& value, 
                      const std::string& filename, 
                      bool backup)
{
    try {
        std::string fullPath = GetFullPath(filename);

        // 确保目录存在
        fs::path filePath(fullPath);
        EnsureDirectoryExists(filePath.parent_path().string());

        // 如果启用备份且文件已存在，则创建备份
        if (backup && fs::exists(fullPath)) {
            std::string backupName = GenerateBackupFilename(filename);
            fs::copy_file(fullPath, GetFullPath(backupName), 
                         fs::copy_options::overwrite_existing);
        }

        // 序列化并写入文件
        std::string dslContent = ValueV2Serializer::Serialize(value);
        WriteFileContent(fullPath, dslContent);
    } catch (const SerializationError& e) {
        throw StoreException(std::string("Serialization failed: ") + e.what());
    } catch (const std::exception& e) {
        throw StoreException(std::string("Save failed: ") + e.what());
    }
}

ValueV2 ValueStore::Load(const std::string& filename)
{
    try {
        std::string fullPath = GetFullPath(filename);

        if (!fs::exists(fullPath)) {
            throw StoreException("File not found: " + filename);
        }

        // 读取文件内容
        std::string dslContent = ReadFileContent(fullPath);

        // 反序列化
        return ValueV2Serializer::Deserialize(dslContent);
    } catch (const SerializationError& e) {
        throw StoreException(std::string("Deserialization failed: ") + e.what());
    } catch (const StoreException& e) {
        throw;
    } catch (const std::exception& e) {
        throw StoreException(std::string("Load failed: ") + e.what());
    }
}

bool ValueStore::Exists(const std::string& filename) const
{
    std::string fullPath = GetFullPath(filename);
    return fs::exists(fullPath);
}

bool ValueStore::Delete(const std::string& filename)
{
    try {
        std::string fullPath = GetFullPath(filename);
        if (fs::exists(fullPath)) {
            fs::remove(fullPath);
            return true;
        }
        return false;
    } catch (const std::exception&) {
        return false;
    }
}

// ====================================================================
// 自动保存机制
// ====================================================================

void ValueStore::SetAutoSave(bool enabled)
{
    auto_save_enabled_ = enabled;
    if (!enabled) {
        FlushAutoSave();
    }
}

void ValueStore::SetAutoSaveInterval(unsigned int intervalMs)
{
    auto_save_interval_ms_ = intervalMs;
}

void ValueStore::RegisterAutoSave(const std::string& key, 
                                 const ValueV2& value, 
                                 const std::string& filename)
{
    AutoSaveEntry entry;
    entry.key = key;
    entry.filename = filename;
    auto_save_map_[key] = entry;
}

void ValueStore::UnregisterAutoSave(const std::string& key)
{
    auto_save_map_.erase(key);
}

void ValueStore::FlushAutoSave()
{
    // 执行所有待自动保存的操作
    // 注意：这是框架实现，实际保存需要存储对象引用
    // 目前只清空映射
    auto_save_map_.clear();
}

// ====================================================================
// 版本管理
// ====================================================================

FileMetadata ValueStore::GetMetadata(const std::string& filename) const
{
    FileMetadata metadata;
    std::string fullPath = GetFullPath(filename);

    if (!fs::exists(fullPath)) {
        throw StoreException("File not found: " + filename);
    }

    metadata.filename = filename;
    metadata.format_version = "1.0";  // 默认版本
    metadata.file_size = fs::file_size(fullPath);

    // 获取修改时间
    auto lastWriteTime = fs::last_write_time(fullPath);
    auto sctp = std::chrono::time_point_cast<std::chrono::system_clock::duration>(
        lastWriteTime - fs::file_time_type::clock::now() + std::chrono::system_clock::now()
    );
    auto time_t = std::chrono::system_clock::to_time_t(sctp);
    std::ostringstream oss;
    oss << std::put_time(std::gmtime(&time_t), "%FT%TZ");
    metadata.modified_time = oss.str();

    return metadata;
}

std::vector<FileMetadata> ValueStore::GetVersionHistory(const std::string& filename) const
{
    std::vector<FileMetadata> history;
    // 框架实现：目前不支持完整的版本历史
    // 只返回当前文件的元数据
    try {
        history.push_back(GetMetadata(filename));
    } catch (...) {
        // 文件不存在
    }
    return history;
}

ValueV2 ValueStore::RestoreVersion(const std::string& filename, 
                                  size_t version)
{
    // 框架实现：目前只支持版本0（当前版本）
    if (version != 0) {
        throw StoreException("Version not supported");
    }
    return Load(filename);
}

bool ValueStore::IsVersionCompatible(const std::string& version)
{
    // 版本1.0是基础版本
    return version == "1.0" || version == "1.1";
}

ValueV2 ValueStore::MigrateVersion(const ValueV2& value, 
                                  const std::string& fromVersion, 
                                  const std::string& toVersion)
{
    // 框架实现：目前两个版本相同
    // 未来可在这里实现版本转换逻辑
    if (fromVersion == toVersion) {
        return value;
    }

    if (!IsVersionCompatible(fromVersion) || !IsVersionCompatible(toVersion)) {
        throw StoreException("Incompatible version");
    }

    // 暂时直接返回原值
    return value;
}

// ====================================================================
// 批量操作
// ====================================================================

std::vector<std::string> ValueStore::ListFiles(
    const std::string& directory, 
    bool recursive) const
{
    std::vector<std::string> files;
    std::string searchPath = GetFullPath(directory);

    try {
        if (!fs::exists(searchPath)) {
            return files;
        }

        if (recursive) {
            for (const auto& entry : fs::recursive_directory_iterator(searchPath)) {
                if (entry.is_regular_file() && entry.path().extension() == ".dsl") {
                    std::string relativePath = fs::relative(entry.path(), base_path_).string();
                    files.push_back(relativePath);
                }
            }
        } else {
            for (const auto& entry : fs::directory_iterator(searchPath)) {
                if (entry.is_regular_file() && entry.path().extension() == ".dsl") {
                    std::string relativePath = fs::relative(entry.path(), base_path_).string();
                    files.push_back(relativePath);
                }
            }
        }
    } catch (const std::exception&) {
        // 忽略错误
    }

    return files;
}

std::map<std::string, ValueV2> ValueStore::LoadDirectory(
    const std::string& directory)
{
    std::map<std::string, ValueV2> result;
    auto files = ListFiles(directory, false);

    for (const auto& file : files) {
        try {
            std::string fullPath = directory.empty() ? file : directory + "/" + file;
            result[file] = Load(fullPath);
        } catch (const std::exception&) {
            // 忽略加载失败的文件
        }
    }

    return result;
}

void ValueStore::SaveDirectory(
    const std::map<std::string, ValueV2>& values)
{
    for (const auto& pair : values) {
        try {
            Save(pair.second, pair.first, true);
        } catch (const std::exception&) {
            // 记录错误但继续处理其他文件
        }
    }
}

// ====================================================================
// 备份和恢复
// ====================================================================

std::string ValueStore::CreateBackup(const std::string& filename)
{
    std::string backupName = GenerateBackupFilename(filename);
    std::string fullPath = GetFullPath(filename);
    std::string backupPath = GetFullPath(backupName);

    try {
        EnsureDirectoryExists(fs::path(backupPath).parent_path().string());
        fs::copy_file(fullPath, backupPath, fs::copy_options::overwrite_existing);
        return backupName;
    } catch (const std::exception& e) {
        throw StoreException(std::string("Backup failed: ") + e.what());
    }
}

std::string ValueStore::CreateDirectoryBackup(
    const std::string& sourceDir, 
    const std::string& backupDir)
{
    std::string timestamp = std::to_string(std::chrono::system_clock::now().time_since_epoch().count());
    std::string backupPath = backupDir + "_" + timestamp;

    try {
        fs::create_directories(GetFullPath(backupPath));
        
        auto sourceFiles = ListFiles(sourceDir, true);
        for (const auto& file : sourceFiles) {
            std::string srcFull = GetFullPath(sourceDir + "/" + file);
            std::string dstFull = GetFullPath(backupPath + "/" + file);
            
            EnsureDirectoryExists(fs::path(dstFull).parent_path().string());
            fs::copy_file(srcFull, dstFull, fs::copy_options::overwrite_existing);
        }

        return backupPath;
    } catch (const std::exception& e) {
        throw StoreException(std::string("Directory backup failed: ") + e.what());
    }
}

void ValueStore::RestoreBackup(const std::string& backupFile, 
                              const std::string& targetFile)
{
    try {
        std::string backupPath = GetFullPath(backupFile);
        std::string targetPath = GetFullPath(targetFile);

        if (!fs::exists(backupPath)) {
            throw StoreException("Backup file not found");
        }

        EnsureDirectoryExists(fs::path(targetPath).parent_path().string());
        fs::copy_file(backupPath, targetPath, fs::copy_options::overwrite_existing);
    } catch (const StoreException& e) {
        throw;
    } catch (const std::exception& e) {
        throw StoreException(std::string("Restore failed: ") + e.what());
    }
}

// ====================================================================
// 配置和状态
// ====================================================================

ValueStore::StorageInfo ValueStore::GetStorageInfo() const
{
    StorageInfo info;

    try {
        for (const auto& entry : fs::recursive_directory_iterator(base_path_)) {
            if (entry.is_regular_file() && entry.path().extension() == ".dsl") {
                info.total_files++;
                size_t size = fs::file_size(entry.path());
                info.total_size += size;

                if (size > info.largest_file) {
                    info.largest_file = size;
                    info.largest_file_name = fs::relative(entry.path(), base_path_).string();
                }
            }
        }
    } catch (const std::exception&) {
        // 忽略错误
    }

    return info;
}

// ====================================================================
// 私有辅助方法
// ====================================================================

std::string ValueStore::GetFullPath(const std::string& filename) const
{
    fs::path fullPath = base_path_;
    fullPath /= filename;
    return fullPath.string();
}

void ValueStore::EnsureDirectoryExists(const std::string& directory)
{
    try {
        fs::create_directories(directory);
    } catch (const std::exception& e) {
        throw StoreException(std::string("Failed to create directory: ") + e.what());
    }
}

std::string ValueStore::GenerateBackupFilename(const std::string& filename) const
{
    fs::path filePath(filename);
    std::string stem = filePath.stem().string();
    std::string ext = filePath.extension().string();
    std::string dir = filePath.parent_path().string();

    auto now = std::chrono::system_clock::now();
    auto time = std::chrono::system_clock::to_time_t(now);
    std::ostringstream oss;
    oss << std::put_time(std::localtime(&time), "%Y%m%d_%H%M%S");

    std::string backupName = stem + "_" + oss.str() + ".backup" + ext;
    
    if (!dir.empty()) {
        backupName = dir + "/" + backupName;
    }

    return backupName;
}

std::string ValueStore::ReadFileContent(const std::string& path) const
{
    std::ifstream file(path);
    if (!file.is_open()) {
        throw StoreException("Cannot open file: " + path);
    }

    std::ostringstream oss;
    oss << file.rdbuf();
    return oss.str();
}

void ValueStore::WriteFileContent(const std::string& path, 
                                 const std::string& content)
{
    std::ofstream file(path);
    if (!file.is_open()) {
        throw StoreException("Cannot create file: " + path);
    }

    file << content;
    if (!file.good()) {
        throw StoreException("Failed to write file: " + path);
    }
}

} // namespace ABot
