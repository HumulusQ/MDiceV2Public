using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MDiceV2.Models;

/// <summary>
/// 日志消息项
/// 表示一条日志消息及其属性
/// </summary>
public partial class LogMessageItem : ObservableObject
{
    /// <summary>
    /// 消息文本
    /// </summary>
    [ObservableProperty]
    private string text = string.Empty;

    /// <summary>
    /// 消息类型
    /// </summary>
    [ObservableProperty]
    private LogMessageType type;

    /// <summary>
    /// 时间戳
    /// </summary>
    [ObservableProperty]
    private DateTime timestamp;
}