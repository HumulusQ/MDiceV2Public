using System.ComponentModel;

namespace MDiceV2.Models;

/// <summary>
/// 消息来源枚举
/// 定义消息来源类型
/// </summary>
public enum MessageSource
{
    /// <summary>
    /// 群聊消息
    /// </summary>
    [Description("群聊")]
    group,

    /// <summary>
    /// 私聊消息
    /// </summary>
    [Description("私聊")]
    privatechat
}