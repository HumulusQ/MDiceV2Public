using System.ComponentModel;

namespace MDiceV2.Models;

/// <summary>
/// 日志消息类型枚举
/// 定义不同级别的日志消息类型
/// </summary>
public enum LogMessageType
{
    /// <summary>
    /// 普通信息
    /// </summary>
    [Description("普通")]
    Normal,

    /// <summary>
    /// 警告信息
    /// </summary>
    [Description("警告")]
    Warning,

    /// <summary>
    /// 重要信息
    /// </summary>
    [Description("重要")]
    Important,

    /// <summary>
    /// 错误信息
    /// </summary>
    [Description("错误")]
    Error,

    /// <summary>
    /// 骰子反馈
    /// </summary>
    [Description("骰子反馈")]
    DiceRoll,

    /// <summary>
    /// 系统消息
    /// </summary>
    [Description("系统消息")]
    System
}