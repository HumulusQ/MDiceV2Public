namespace MDiceV2.Interfaces.Mod;

/// <summary>
/// 表示可以处理配置推送/拉取的组件（UI 或 Mod）
/// </summary>
public interface IConfigurable
{
    /// <summary>
    /// 获取此组件管理的所有配置键，格式为 category.key （如 basic.master, feedback.group_join）
    /// </summary>
    IReadOnlyList<string> GetConfigKeys();

    /// <summary>
    /// 获取指定配置键的当前值
    /// </summary>
    /// <param name="key">配置键（格式：category.key）</param>
    /// <returns>配置值，如果键不存在则返回 null</returns>
    string? GetConfigValue(string key);

    /// <summary>
    /// 验证配置是否有效，在应用之前调用
    /// </summary>
    /// <param name="key">配置键</param>
    /// <param name="value">配置值</param>
    /// <returns>验证结果，包含是否有效和错误消息</returns>
    ConfigValidationResult ValidateConfig(string key, string value);

    /// <summary>
    /// 应用配置到运行时，必须在 ValidateConfig 返回有效后调用
    /// </summary>
    /// <param name="key">配置键</param>
    /// <param name="value">配置值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>应用结果</returns>
    Task<ConfigApplicationResult> ApplyConfigAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// 当配置被应用时触发，通知 UI 更新（如适用）
    /// </summary>
    event ConfigChangedEventHandler? ConfigChanged;
}

/// <summary>
/// 配置验证结果
/// </summary>
public class ConfigValidationResult
{
    public bool IsValid { get; set; } = true;
    public string? ErrorMessage { get; set; }

    public static ConfigValidationResult Valid() => new() { IsValid = true };
    public static ConfigValidationResult Invalid(string message) => new() { IsValid = false, ErrorMessage = message };
}

/// <summary>
/// 配置应用结果
/// </summary>
public class ConfigApplicationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AppliedValue { get; set; }

    public static ConfigApplicationResult Succeed(string appliedValue) =>
        new() { Success = true, AppliedValue = appliedValue };

    public static ConfigApplicationResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// 配置更改事件处理委托
/// </summary>
/// <param name="key">更改的配置键</param>
/// <param name="newValue">新配置值</param>
public delegate void ConfigChangedEventHandler(string key, string newValue);
