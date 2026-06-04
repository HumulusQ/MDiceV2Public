using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Microsoft.Extensions.Logging;

namespace MDiceV2.Core.Infrastructure.Configurers;

/// <summary>
/// 反馈模板配置处理器，处理 feedback.* 配置项
/// </summary>
public class FeedbackTemplateConfigurer : IConfigurable
{
    private readonly ILogger<FeedbackTemplateConfigurer> _logger;

    public FeedbackTemplateConfigurer(ILogger<FeedbackTemplateConfigurer> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> GetConfigKeys()
    {
        // 动态获取所有反馈模板键，并转换为 feedback.* 格式
        return GlobalFeedbackMessages.FeedbackTemplates.Keys
            .Select(k => $"feedback.{k.ToLowerInvariant()}")
            .ToList()
            .AsReadOnly();
    }

    public string? GetConfigValue(string key)
    {
        // 提取原始键名 (去掉 "feedback." 前缀)
        if (!key.StartsWith("feedback.", StringComparison.OrdinalIgnoreCase))
            return null;

        var templateKey = key.Substring(9); // "feedback.".Length = 9
        return GlobalFeedbackMessages.FeedbackTemplates.TryGetValue(templateKey, out var value) ? value : null;
    }

    public ConfigValidationResult ValidateConfig(string key, string value)
    {
        // 提取原始键名
        if (!key.StartsWith("feedback.", StringComparison.OrdinalIgnoreCase))
            return ConfigValidationResult.Invalid($"无效的反馈配置键前缀: {key}");

        var templateKey = key.Substring(9);
        if (string.IsNullOrWhiteSpace(templateKey))
            return ConfigValidationResult.Invalid("模板键不能为空");

        // 允许空值（代表默认值）
        if (value == null)
            return ConfigValidationResult.Invalid("模板值不能为 null");

        // 如果是占位符模板，验证格式不会过于复杂
        // 简单检查：至少要有一些内容
        if (string.IsNullOrWhiteSpace(value))
            return ConfigValidationResult.Invalid($"模板值不能为空白: {key}");

        return ConfigValidationResult.Valid();
    }

    public async Task<ConfigApplicationResult> ApplyConfigAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        try
        {
            // 提取原始键名
            if (!key.StartsWith("feedback.", StringComparison.OrdinalIgnoreCase))
                return ConfigApplicationResult.Fail($"无效的反馈配置键: {key}");

            var templateKey = key.Substring(9);

            // 应用到全局反馈模板字典
            GlobalFeedbackMessages.FeedbackTemplates[templateKey] = value;

            // 触发配置更改事件
            ConfigChanged?.Invoke(key, value);
            _logger.LogInformation("✓ 反馈模板已应用: {Key} = '{Value}'", key, value);

            return ConfigApplicationResult.Succeed(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✗ 应用反馈模板失败: {Key}", key);
            return ConfigApplicationResult.Fail($"应用异常: {ex.Message}");
        }
    }

    public event ConfigChangedEventHandler? ConfigChanged;
}
