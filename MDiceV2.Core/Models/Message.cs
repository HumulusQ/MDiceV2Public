using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MDiceV2.Models;

/// <summary>
/// 聊天消息模型
/// 表示一条聊天消息，包含文本内容、发送者信息和时间戳
/// 支持普通消息和合并转发消息
/// </summary>
public partial class Message : ObservableObject
{
    /// <summary>
    /// 消息文本内容（对于普通消息，或者合并消息的标题）
    /// </summary>
    [ObservableProperty]
    private string text = string.Empty;

    /// <summary>
    /// 是否为用户发送的消息
    /// true表示用户发送，false表示系统/其他用户发送
    /// </summary>
    [ObservableProperty]
    private bool isFromUser;

    /// <summary>
    /// 消息发送时间戳
    /// </summary>
    [ObservableProperty]
    private DateTime timestamp;

    /// <summary>
    /// 是否为合并转发消息
    /// true表示这是一个包含多个内容项的合并气泡
    /// </summary>
    [ObservableProperty]
    private bool isForwardMessage;

    /// <summary>
    /// 合并消息的内容列表
    /// 仅当 IsForwardMessage=true 时有效
    /// 每个项目代表合并气泡内的一个内容条目
    /// </summary>
    [ObservableProperty]
    private List<string> forwardContent = new();
}