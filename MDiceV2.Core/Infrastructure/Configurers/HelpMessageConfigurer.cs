using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Microsoft.Extensions.Logging;

namespace MDiceV2.Core.Infrastructure.Configurers;

/// <summary>
/// 帮助消息配置处理器，处理 help.* 配置项
/// </summary>
public class HelpMessageConfigurer : IConfigurable
{
    private readonly ILogger<HelpMessageConfigurer> _logger;

    public HelpMessageConfigurer(ILogger<HelpMessageConfigurer> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> GetConfigKeys()
    {
        // 动态获取所有帮助模板键，并转换为 help.* 格式
        return GlobalFeedbackMessages.HelpTemplates.Keys
            .Select(k => $"help.{k.ToLowerInvariant()}")
            .ToList()
            .AsReadOnly();
    }

    public string? GetConfigValue(string key)
    {
        // 提取原始键名 (去掉 "help." 前缀)
        if (!key.StartsWith("help.", StringComparison.OrdinalIgnoreCase))
            return null;

        var helpKey = key.Substring(5); // "help.".Length = 5
        return GlobalFeedbackMessages.HelpTemplates.TryGetValue(helpKey, out var value) ? value : null;
    }

    public ConfigValidationResult ValidateConfig(string key, string value)
    {
        // 提取原始键名
        if (!key.StartsWith("help.", StringComparison.OrdinalIgnoreCase))
            return ConfigValidationResult.Invalid($"无效的帮助配置键前缀: {key}");

        var helpKey = key.Substring(5);
        if (string.IsNullOrWhiteSpace(helpKey))
            return ConfigValidationResult.Invalid("帮助键不能为空");

        // 允许空值（代表默认值）
        if (value == null)
            return ConfigValidationResult.Invalid("帮助消息值不能为 null");

        // 帮助消息应该有实际内容
        if (string.IsNullOrWhiteSpace(value))
            return ConfigValidationResult.Invalid($"帮助消息值不能为空白: {key}");

        return ConfigValidationResult.Valid();
    }

    public async Task<ConfigApplicationResult> ApplyConfigAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        try
        {
            // 提取原始键名
            if (!key.StartsWith("help.", StringComparison.OrdinalIgnoreCase))
                return ConfigApplicationResult.Fail($"无效的帮助配置键: {key}");

            var helpKey = key.Substring(5);

            // 应用到全局帮助模板字典
            GlobalFeedbackMessages.HelpTemplates[helpKey] = value;

            // 触发配置更改事件
            ConfigChanged?.Invoke(key, value);
            _logger.LogInformation("✓ 帮助消息已应用: {Key} = '{Value}'", key, value);

            return ConfigApplicationResult.Succeed(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✗ 应用帮助消息失败: {Key}", key);
            return ConfigApplicationResult.Fail($"应用异常: {ex.Message}");
        }
    }

    public event ConfigChangedEventHandler? ConfigChanged;
}
