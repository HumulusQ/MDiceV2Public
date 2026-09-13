using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// 灵活的配置同步调度器，支持按前缀或精确键注册处理方法
/// 实现了同步逻辑与通信协议（gRPC）的解耦
/// </summary>
public class ConfigSyncDispatcher
{
    private readonly ConcurrentDictionary<string, Func<string, string, Task>> _categoryHandlers = 
        new(StringComparer.OrdinalIgnoreCase);
    
    private readonly ConcurrentDictionary<string, Func<string, string, Task>> _exactKeyHandlers = 
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<ConfigSyncDispatcher> _logger;

    public ConfigSyncDispatcher(ILogger<ConfigSyncDispatcher>? logger = null)
    {
        _logger = logger ?? NullLogger<ConfigSyncDispatcher>.Instance;
    }

    /// <summary>
    /// 注册一个类别的处理器（按 key.split('.').First() 匹配）
    /// </summary>
    public void RegisterCategory(string category, Func<string, string, Task> handler)
    {
        _categoryHandlers[category] = handler;
        _logger.LogInformation(" 已注册类别同步处理器: {Category}", category);
    }

    /// <summary>
    /// 注册一个精确键的处理器
    /// </summary>
    public void RegisterKey(string key, Func<string, string, Task> handler)
    {
        _exactKeyHandlers[key] = handler;
        _logger.LogInformation(" 已注册精确键同步处理器: {Key}", key);
    }

    /// <summary>
    /// 派发并处理配置更新
    /// </summary>
    public async Task DispatchAsync(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;

        _logger.LogInformation($"[ConfigSyncDispatcher] ► 派发配置: key='{key}', valueLength={value?.Length ?? 0}");
        
        // 1. 优先尝试精确匹配
        if (_exactKeyHandlers.TryGetValue(key, out var exactHandler))
        {
            _logger.LogInformation($"[ConfigSyncDispatcher] ├─ 【精确匹配】处理器已找到");
            try
            {
                await exactHandler(key, value);
                _logger.LogInformation($"[ConfigSyncDispatcher] ✓ 精确键处理器执行成功");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ConfigSyncDispatcher] ✗ 精确键处理器执行失败: {key}");
            }
        }
        else
        {
            _logger.LogInformation($"[ConfigSyncDispatcher] ├─ 【精确匹配】无精确处理器，尝试类别匹配");
        }

        // 2. 尝试类别匹配 (prefix.xxx)
        var category = key.Split('.').FirstOrDefault();
        _logger.LogInformation($"[ConfigSyncDispatcher] ├─ 【类别匹配】提取类别: '{category}'");
        
        if (category != null && _categoryHandlers.TryGetValue(category, out var categoryHandler))
        {
            _logger.LogInformation($"[ConfigSyncDispatcher] ├─ 【类别匹配】'{category}' 处理器已找到");
            try
            {
                await categoryHandler(key, value);
                _logger.LogInformation($"[ConfigSyncDispatcher] ✓ 类别处理器执行成功: {category}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ConfigSyncDispatcher] ✗ 类别处理器执行失败: {category} (Key: {key})");
            }
        }
        else
        {
            _logger.LogWarning($"[ConfigSyncDispatcher] ⚠ 未找到匹配的处理器 - key: {key}, category: {category}");
            _logger.LogWarning($"[ConfigSyncDispatcher]   已注册的精确处理器: {{{string.Join(", ", _exactKeyHandlers.Keys)}}}");
            _logger.LogWarning($"[ConfigSyncDispatcher]   已注册的类别处理器: {{{string.Join(", ", _categoryHandlers.Keys)}}}");
        }
    }

    /// <summary>
    /// 批量派发配置更新
    /// </summary>
    public async Task DispatchBatchAsync(IEnumerable<KeyValuePair<string, string>> configs)
    {
        var configList = configs.ToList();
        _logger.LogInformation($"[ConfigSyncDispatcher] ► 开始批量派发配置 - 共 {configList.Count} 项");
        
        int successCount = 0;
        int failureCount = 0;
        
        foreach (var kvp in configList)
        {
            _logger.LogInformation($"[ConfigSyncDispatcher] ├─ 派发配置项 [{successCount + failureCount + 1}/{configList.Count}]: {kvp.Key}");
            try
            {
                await DispatchAsync(kvp.Key, kvp.Value);
                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ConfigSyncDispatcher] ✗ 派发失败: {kvp.Key}");
                failureCount++;
            }
        }
        
        _logger.LogInformation($"[ConfigSyncDispatcher] ✓ 批量派发完成 - 成功: {successCount}，失败: {failureCount}");
    }
}
