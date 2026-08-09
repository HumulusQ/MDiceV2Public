using System;
using MDiceV2.Models;

namespace MDiceV2.Models;

/// <summary>
/// 日志发送器
/// 提供静态方法用于发送不同类型的日志消息
/// 在 UI 版本中通过 GlobalMessageQueue 显示在日志面板
/// 在 Console 版本中直接输出到控制台
/// </summary>
public static class LogSender
{
    /// <summary>
    /// 发送普通日志消息
    /// </summary>
    /// <param name="message">消息内容</param>
    public static void Normal(string message)
    {
#if CONSOLE_MODE
        Console.WriteLine($"[INFO] {message}");
#else
        GlobalMessageQueue.Instance?.EnqueueLogMessage(message, LogMessageType.Normal);
#endif
    }

    /// <summary>
    /// 发送警告日志消息
    /// </summary>
    /// <param name="message">消息内容</param>
    public static void Warn(string message)
    {
#if CONSOLE_MODE
        Console.WriteLine($"[WARN] {message}");
#else
        GlobalMessageQueue.Instance?.EnqueueLogMessage(message, LogMessageType.Warning);
#endif
    }

    /// <summary>
    /// 发送错误日志消息
    /// </summary>
    /// <param name="message">消息内容</param>
    public static void Error(string message)
    {
#if CONSOLE_MODE
        Console.WriteLine($"[ERROR] {message}");
#else
        GlobalMessageQueue.Instance?.EnqueueLogMessage(message, LogMessageType.Important);
#endif
    }

    /// <summary>
    /// 发送格式化的普通日志消息
    /// </summary>
    /// <param name="format">格式字符串</param>
    /// <param name="args">格式参数</param>
    public static void InfoFormat(string format, params object[] args)
    {
        var message = string.Format(format, args);
#if CONSOLE_MODE
        Console.WriteLine($"[INFO] {message}");
#else
        if (GlobalMessageQueue.Instance != null)
        {
            GlobalMessageQueue.Instance.EnqueueLogMessage(message, LogMessageType.Normal);
        }
#endif
    }

    /// <summary>
    /// 发送格式化的普通日志消息（无参数版本）
    /// </summary>
    /// <param name="format">消息内容</param>
    public static void InfoFormat(string format)
    {
#if CONSOLE_MODE
        Console.WriteLine($"[INFO] {format}");
#else
        GlobalMessageQueue.Instance?.EnqueueLogMessage(format, LogMessageType.Normal);
#endif
    }
}

/// <summary>
/// 日志静态类
/// 提供便捷的日志记录方法
/// </summary>
public static class Log
{
    /// <summary>
    /// 发送普通日志消息
    /// </summary>
    /// <param name="message">消息内容</param>
    public static void Normal(string message) =>
        LogSender.Normal(message);

    /// <summary>
    /// 发送警告日志消息
    /// </summary>
    /// <param name="message">消息内容</param>
    public static void Warn(string message) =>
        LogSender.Warn(message);

    /// <summary>
    /// 发送错误日志消息
    /// </summary>
    /// <param name="message">消息内容</param>
    public static void Error(string message) =>
        LogSender.Error(message);

    /// <summary>
    /// 发送格式化的普通日志消息
    /// </summary>
    /// <param name="format">格式字符串</param>
    /// <param name="args">格式参数</param>
    public static void InfoFormat(string format, params object[] args) =>
        LogSender.InfoFormat(format, args);

    /// <summary>
    /// 发送格式化的普通日志消息（无参数版本）
    /// </summary>
    /// <param name="format">消息内容</param>
    public static void InfoFormat(string format) =>
        LogSender.InfoFormat(format);
}