using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Microsoft.Extensions.Logging;

namespace MDiceV2.Core.Infrastructure.Configurers;

/// <summary>
/// 基本配置处理器，处理 basic.* 配置项
/// </summary>
public class BasicConfigurer : IConfigurable
{
    private readonly ILogger<BasicConfigurer> _logger;

    // 支持的配置键列表
    private static readonly List<string> SupportedKeys = new()
    {
        "basic.master",
        "basic.mastergroup",
        "basic.url",
        "basic.approvefriendjoinrequest",
        "basic.approvegroupjoinrequest",
        "basic.sendgroupjoinreport",
        "basic.sendfriendjoinreport"
    };

    public BasicConfigurer(ILogger<BasicConfigurer> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> GetConfigKeys() => SupportedKeys.AsReadOnly();

    public string? GetConfigValue(string key)
    {
        if (MessageProcessor.Instance?.basicConfigData is not { } config)
            return null;

        return key.ToLowerInvariant() switch
        {
            "basic.master" => config.Master,
            "basic.mastergroup" => config.MasterGroup,
            "basic.url" => config.Url,
            "basic.approvefriendjoinrequest" => config.ApproveFriendJoinRequest.ToString(),
            "basic.approvegroupjoinrequest" => config.ApproveGroupJoinRequest.ToString(),
            "basic.sendgroupjoinreport" => config.SendGroupJoinReport.ToString(),
            "basic.sendfriendjoinreport" => config.SendFriendJoinReport.ToString(),
            _ => null
        };
    }

    public ConfigValidationResult ValidateConfig(string key, string value)
    {
        var lowerKey = key.ToLowerInvariant();
        _logger.LogDebug("📝 [BasicConfigurer] 验证配置 - Key: {Key} (normalized: {LowerKey}), Value: {Value}", 
            key, lowerKey, value);

        // 验证 URL 格式
        if (lowerKey == "basic.url")
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _logger.LogError("❌ [BasicConfigurer] URL验证失败: URL 不能为空");
                return ConfigValidationResult.Invalid("URL 不能为空");
            }
            
            if (!Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                _logger.LogError("❌ [BasicConfigurer] URL验证失败: URL 格式无效 - Value: {Value}", value);
                return ConfigValidationResult.Invalid("URL 格式无效");
            }
        }

        // 验证布尔值配置
        if (lowerKey.EndsWith("request") || lowerKey.EndsWith("report"))
        {
            if (!bool.TryParse(value, out _))
            {
                _logger.LogError("❌ [BasicConfigurer] 布尔值验证失败: '{Key}' 必须为布尔值 (true/false), 接收到: {Value}", 
                    key, value);
                return ConfigValidationResult.Invalid($"'{key}' 必须为布尔值 (true/false)");
            }
            _logger.LogDebug("✅ [BasicConfigurer] 布尔值验证通过: {Key} = {Value}", key, value);
        }

        // 字符串类型配置无需特殊验证
        if (lowerKey == "basic.master" || lowerKey == "basic.mastergroup")
        {
            if (value == null)
            {
                _logger.LogError("❌ [BasicConfigurer] 字符串验证失败: '{Key}' 不能为 null", key);
                return ConfigValidationResult.Invalid($"'{key}' 不能为 null");
            }
            _logger.LogDebug("✅ [BasicConfigurer] 字符串验证通过: {Key}", key);
        }

        _logger.LogDebug("✅ [BasicConfigurer] 完整验证通过: {Key}", key);
        return ConfigValidationResult.Valid();
    }

    public async Task<ConfigApplicationResult> ApplyConfigAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("📝 [BasicConfigurer] 开始应用配置 - Key: {Key}, Value: {Value}", key, value);

        if (MessageProcessor.Instance?.basicConfigData is not { } config)
        {
            _logger.LogError("❌ [BasicConfigurer] MessageProcessor 实例未初始化");
            return ConfigApplicationResult.Fail("MessageProcessor 实例未初始化");
        }

        try
        {
            var lowerKey = key.ToLowerInvariant();
            _logger.LogDebug("📝 [BasicConfigurer] 规范化后的Key: {LowerKey}", lowerKey);

            string internalKey = string.Empty;
            object actualValue = value ?? string.Empty;

            switch (lowerKey)
            {
                case "basic.master":
                    internalKey = "Master";
                    config.Master = value ?? string.Empty;
                    break;

                case "basic.mastergroup":
                    internalKey = "MasterGroup";
                    config.MasterGroup = value ?? string.Empty;
                    break;

                case "basic.url":
                    internalKey = "Url";
                    config.Url = value ?? string.Empty;
                    break;

                case "basic.approvefriendjoinrequest":
                    internalKey = "ApproveFriendJoinRequest";
                    if (bool.TryParse(value, out var approveFriend))
                    {
                        config.ApproveFriendJoinRequest = approveFriend;
                        actualValue = approveFriend;
                    }
                    else
                    {
                        return ConfigApplicationResult.Fail($"无法解析布尔值: {value}");
                    }
                    break;

                case "basic.approvegroupjoinrequest":
                    internalKey = "ApproveGroupJoinRequest";
                    if (bool.TryParse(value, out var approveGroup))
                    {
                        config.ApproveGroupJoinRequest = approveGroup;
                        actualValue = approveGroup;
                    }
                    else
                    {
                        return ConfigApplicationResult.Fail($"无法解析布尔值: {value}");
                    }
                    break;

                case "basic.sendgroupjoinreport":
                    internalKey = "SendGroupJoinReport";
                    if (bool.TryParse(value, out var sendGroupReport))
                    {
                        config.SendGroupJoinReport = sendGroupReport;
                        actualValue = sendGroupReport;
                    }
                    else
                    {
                        return ConfigApplicationResult.Fail($"无法解析布尔值: {value}");
                    }
                    break;

                case "basic.sendfriendjoinreport":
                    internalKey = "SendFriendJoinReport";
                    if (bool.TryParse(value, out var sendFriendReport))
                    {
                        config.SendFriendJoinReport = sendFriendReport;
                        actualValue = sendFriendReport;
                    }
                    else
                    {
                        return ConfigApplicationResult.Fail($"无法解析布尔值: {value}");
                    }
                    break;

                default:
                    _logger.LogError("❌ [BasicConfigurer] 未知的配置键: {Key}", key);
                    return ConfigApplicationResult.Fail($"未知的配置键: {key}");
            }

            // 同步到持久化层
            if (!string.IsNullOrEmpty(internalKey))
            {
                GlobalFeedbackMessages.SetBasicSetting(internalKey, actualValue.ToString() ?? "");
                GlobalFeedbackMessages.SaveBasicSettings();
                _logger.LogDebug("💾 [BasicConfigurer] 已同步到 GlobalFeedbackMessages: {Key} = {Value}", internalKey, actualValue);
            }

            // 触发配置更改事件
            ConfigChanged?.Invoke(key, value ?? "");
            _logger.LogInformation("✓ 基本配置已应用并持久化: {Key} = {Value}", key, value);

            return ConfigApplicationResult.Succeed(value ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✗ 应用基本配置失败: {Key}", key);
            return ConfigApplicationResult.Fail($"应用异常: {ex.Message}");
        }
    }

    public event ConfigChangedEventHandler? ConfigChanged;
}
