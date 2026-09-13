using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using MDiceV2.Models;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// 同步配置管理器
/// 管理本地同步文件夹、密钥生成、配置加载/保存
/// </summary>
public class SyncConfigManager
{
    private readonly string _dataFolder;
    private readonly string _syncFolder;
    private readonly string _keyFile;
    private string? _localKey;

    public string LocalKey
    {
        get
        {
            if (_localKey == null)
            {
                _localKey = LoadOrGenerateKey();
            }
            return _localKey;
        }
    }

    public SyncConfigManager(string? customDataFolder = null)
    {
        _dataFolder = customDataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MDiceV2", "data");
        
        _syncFolder = Path.Combine(_dataFolder, "synchronize");
        _keyFile = Path.Combine(_dataFolder, ".sync_key");

        EnsureDirectoriesExist();
    }

    /// <summary>
    /// 确保必要的文件夹存在
    /// </summary>
    private void EnsureDirectoriesExist()
    {
        if (!Directory.Exists(_dataFolder))
            Directory.CreateDirectory(_dataFolder);
        
        if (!Directory.Exists(_syncFolder))
            Directory.CreateDirectory(_syncFolder);
    }

    /// <summary>
    /// 加载或生成本地密钥
    /// 密钥在第一次启动时生成，之后保存到文件中
    /// </summary>
    private string LoadOrGenerateKey()
    {
        Log($"开始加载或生成本地密钥");
        Log($"数据文件夹: {_dataFolder}");
        Log($"密钥文件: {_keyFile}");

        try
        {
            if (File.Exists(_keyFile))
            {
                Log($"✓ 密钥文件已存在，正在读取...");
                var key = File.ReadAllText(_keyFile).Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    Log($"✓ 成功加载本地密钥: {(key.Length > 8 ? key.Substring(0, 8) + "..." : "***")}");
                    return key;
                }
            }

            // 生成新的密钥（128位随机值的Base64编码）
            Log("密钥文件不存在，正在生成新密钥...");
            var keyBytes = new byte[16];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(keyBytes);
            }
            var generatedKey = Convert.ToBase64String(keyBytes);

            // 保存到文件
            Log($"正在保存密钥到: {_keyFile}");
            File.WriteAllText(_keyFile, generatedKey);
            Log($"✓ 成功生成并保存新密钥: {(generatedKey.Length > 8 ? generatedKey.Substring(0, 8) + "..." : "***")}");
            return generatedKey;
        }
        catch (Exception ex)
        {
            LogError($"加载/生成密钥失败: {ex.Message}");
            LogError($"堆栈: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 为指定的服务器地址和密钥生成认证哈希
    /// 使用 HMAC-SHA256
    /// </summary>
    public string GenerateAuthHash(string localKey, long timestamp)
    {
        try
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(localKey)))
            {
                var data = Encoding.UTF8.GetBytes(timestamp.ToString());
                var hash = hmac.ComputeHash(data);
                return Convert.ToBase64String(hash);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncConfigManager] Error generating auth hash: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 将配置保存到同步文件夹
    /// 配置以JSON格式存储
    /// </summary>
    public async Task SaveSyncConfigAsync(Dictionary<string, string> config)
    {
        Log($"正在保存同步配置...");
        Log($"配置项数: {config.Count}");

        try
        {
            var configFile = Path.Combine(_syncFolder, "config.json");
            Log($"配置文件路径: {configFile}");

            var metadata = new
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                version = "1.0",
                items_count = config.Count
            };

            var data = new { metadata, config };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(configFile, json);
            Log($"✓ 成功保存同步配置，包含 {config.Count} 个配置项");
        }
        catch (Exception ex)
        {
            LogError($"保存配置失败: {ex.Message}");
            LogError($"堆栈: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 从同步文件夹加载配置
    /// </summary>
    public async Task<Dictionary<string, string>> LoadSyncConfigAsync()
    {
        Log($"正在加载同步配置...");

        try
        {
            var configFile = Path.Combine(_syncFolder, "config.json");
            Log($"配置文件路径: {configFile}");

            if (!File.Exists(configFile))
            {
                Log($"❌ 同步配置文件不存在");
                return new Dictionary<string, string>();
            }

                Log($"✓ 文件存在，正在读取...");
            var json = await File.ReadAllTextAsync(configFile);
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var config = new Dictionary<string, string>();

                if (root.TryGetProperty("config", out var configElement))
                {
                    foreach (var prop in configElement.EnumerateObject())
                    {
                        config[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[SyncConfigManager] Loaded sync config with {config.Count} items");
                return config;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncConfigManager] Error loading sync config: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 保存单个配置项到同步文件夹
    /// 用于实时同步时保存最新的配置
    /// </summary>
    public async Task UpdateSyncConfigItemAsync(string key, string value)
    {
        try
        {
            Log($"更新配置项: {key} = {value}");
            
            var config = await LoadSyncConfigAsync();
            Log($"✓ 配置加载成功, 当前项数: {config.Count}");
            
            config[key] = value;
            Log($"✓ 配置项已更新, 新项数: {config.Count}");
            
            await SaveSyncConfigAsync(config);
            Log($"✓ 配置项更新保存完成");
        }
        catch (Exception ex)
        {
            LogError($"更新配置项异常: {ex.Message}");
            LogError($"堆栈: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 清空同步配置文件夹
    /// </summary>
    public void ClearSyncFolder()
    {
        try
        {
            Log($"开始清空同步文件夹: {_syncFolder}");
            
            if (Directory.Exists(_syncFolder))
            {
                var dir = new DirectoryInfo(_syncFolder);
                var files = dir.GetFiles();
                Log($"发现文件数: {files.Length}");
                
                foreach (var file in files)
                {
                    file.Delete();
                    Log($"✓ 删除文件: {file.Name}");
                }
                
                Log($"✓ 同步文件夹已清空");
            }
            else
            {
                Log($"ℹ 同步文件夹不存在");
            }
        }
        catch (Exception ex)
        {
            LogError($"清空同步文件夹异常: {ex.Message}");
            LogError($"堆栈: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 验证远程服务器返回的密钥签名
    /// </summary>
    public bool VerifyServerSignature(string serverKey, string signature, long timestamp)
    {
        try
        {
            Log($"开始验证服务器签名");
            Log($"[参数] 服务器密钥: {serverKey.Substring(0, Math.Min(8, serverKey.Length))}...");
            Log($"[参数] 时间戳: {timestamp}");
            Log($"[参数] 签名: {signature.Substring(0, Math.Min(16, signature.Length))}...");
            
            var expectedHash = GenerateAuthHash(serverKey, timestamp);
            Log($"生成的哈希: {expectedHash.Substring(0, Math.Min(16, expectedHash.Length))}...");
            
            var isValid = expectedHash == signature;
            if (isValid)
            {
                Log($"✓ 服务器签名验证成功");
            }
            else
            {
                Log($"❌ 服务器签名验证失败");
            }
            
            return isValid;
        }
        catch (Exception ex)
        {
            LogError($"验证签名异常: {ex.Message}");
            LogError($"堆栈: {ex.StackTrace}");
            return false;
        }
    }

    private void Log(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [SyncManager] {message}";
        LogSender.Normal(formatted);
    }

    private void LogError(string message)
    {
        var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [SyncManager] ERROR: {message}";
        LogSender.Error(formatted);
    }
}
