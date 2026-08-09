using MDiceV2.Interfaces.Mod;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// 中央配置应用器注册表，将配置项路由到相应的 IConfigurable 处理器
/// </summary>
public class ConfigApplierRegistry
{
    private readonly ConcurrentDictionary<string, IConfigurable> _categoryHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    
    private readonly ILogger<ConfigApplierRegistry> _logger;

    public ConfigApplierRegistry(ILogger<ConfigApplierRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 注册一个配置处理器
    /// </summary>
    /// <param name="configurable">实现 IConfigurable 的处理器</param>
    public void Register(IConfigurable configurable)
    {
        var keys = configurable.GetConfigKeys();
        foreach (var key in keys)
        {
            // 提取 category 前缀（例如 "basic.master" -> "basic"）
            var categoryMatch = key.Split('.').FirstOrDefault();
            if (!string.IsNullOrEmpty(categoryMatch))
            {
                _categoryHandlers.TryAdd(categoryMatch, configurable);
                _logger.LogInformation("✓ 已注册配置处理器: 类别={Category}, 处理器类型={HandlerType}", 
                    categoryMatch, configurable.GetType().Name);
            }
        }
    }

    /// <summary>
    /// 应用单个配置项
    /// </summary>
    public async Task<ConfigApplyResult> ApplyConfigAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("📝 [ApplyConfig] 开始处理配置项 - Key: {Key}, Value: {Value}", key, value);

        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogError("❌ [ApplyConfig] 配置键为空");
            return ConfigApplyResult.Fail(key, "配置键不能为空");
        }

        var category = key.Split('.').FirstOrDefault();
        _logger.LogDebug("📝 [ApplyConfig] 提取的category: {Category} (from key: {Key})", category, key);

        if (string.IsNullOrEmpty(category))
        {
            _logger.LogError("❌ [ApplyConfig] 无效的配置键格式 - Key: {Key} (应为 category.key)", key);
            return ConfigApplyResult.Fail(key, "无效的配置键格式，应为 category.key");
        }

        if (!_categoryHandlers.TryGetValue(category, out var handler))
        {
            var registeredCategories = string.Join(", ", _categoryHandlers.Keys);
            _logger.LogError("❌ [ApplyConfig] 未找到配置处理器 - Category: {Category}, 已注册的类别: [{RegisteredCategories}]", 
                category, registeredCategories);
            return ConfigApplyResult.Fail(key, $"未找到配置处理器: {category}");
        }

        _logger.LogDebug("📝 [ApplyConfig] 找到处理器: {HandlerType}", handler.GetType().Name);

        // 验证配置
        _logger.LogDebug("📝 [ApplyConfig] 开始验证配置 - Key: {Key}", key);
        var validationResult = handler.ValidateConfig(key, value);
        if (!validationResult.IsValid)
        {
            _logger.LogError("❌ [ApplyConfig] 验证失败 - Key: {Key}, 错误原因: {ErrorMessage}", 
                key, validationResult.ErrorMessage ?? "验证失败");
            return ConfigApplyResult.Fail(key, validationResult.ErrorMessage ?? "验证失败");
        }

        _logger.LogDebug("✅ [ApplyConfig] 验证通过 - Key: {Key}", key);

        // 应用配置
        try
        {
            _logger.LogDebug("📝 [ApplyConfig] 开始应用配置 - Key: {Key}", key);
            var applyResult = await handler.ApplyConfigAsync(key, value, cancellationToken);
            
            if (!applyResult.Success)
            {
                _logger.LogError("❌ [ApplyConfig] 处理器应用失败 - Key: {Key}, 错误原因: {ErrorMessage}", 
                    key, applyResult.ErrorMessage ?? "应用失败");
                return ConfigApplyResult.Fail(key, applyResult.ErrorMessage ?? "应用失败");
            }

            _logger.LogInformation("✅ [ApplyConfig] 配置已应用: {Key} = {Value}", key, value);
            return ConfigApplyResult.Succeed(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [ApplyConfig] 应用配置异常: {Key}", key);
            return ConfigApplyResult.Fail(key, $"应用异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 批量应用配置项
    /// </summary>
    public async Task<BatchApplyResult> ApplyBatchAsync(IEnumerable<(string Key, string Value)> configs, 
        CancellationToken cancellationToken = default)
    {
        var results = new List<ConfigApplyResult>();
        var successCount = 0;
        var failCount = 0;

        foreach (var (key, value) in configs)
        {
            var result = await ApplyConfigAsync(key, value, cancellationToken);
            results.Add(result);
            
            if (result.Success)
                successCount++;
            else
                failCount++;
        }

        return new BatchApplyResult
        {
            TotalCount = results.Count,
            SuccessCount = successCount,
            FailCount = failCount,
            Results = results
        };
    }

    /// <summary>
    /// 导出所有已注册的配置项及其当前值
    /// </summary>
    public Dictionary<string, string> ExportAllConfigs()
    {
        var configs = new Dictionary<string, string>();
        var uniqueHandlers = _categoryHandlers.Values.Distinct();

        foreach (var handler in uniqueHandlers)
        {
            var keys = handler.GetConfigKeys();
            foreach (var key in keys)
            {
                var value = handler.GetConfigValue(key);
                if (value != null)
                    configs[key] = value;
            }
        }

        return configs;
    }

    /// <summary>
    /// 检查是否已注册给定类别的处理器
    /// </summary>
    public bool HasCategory(string category) => _categoryHandlers.ContainsKey(category);

    /// <summary>
    /// 获取已注册的所有类别
    /// </summary>
    public IEnumerable<string> GetRegisteredCategories() => _categoryHandlers.Keys.Distinct();
}

/// <summary>
/// 单个配置应用结果
/// </summary>
public class ConfigApplyResult
{
    public string Key { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Value { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;

    public static ConfigApplyResult Succeed(string key, string value) =>
        new() { Key = key, Success = true, Value = value };

    public static ConfigApplyResult Fail(string key, string message) =>
        new() { Key = key, Success = false, ErrorMessage = message };
}

/// <summary>
/// 批量应用结果
/// </summary>
public class BatchApplyResult
{
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public List<ConfigApplyResult> Results { get; set; } = new();

    public bool HasFailures => FailCount > 0;

    public IEnumerable<ConfigApplyResult> GetFailedResults() =>
        Results.Where(r => !r.Success);
}
