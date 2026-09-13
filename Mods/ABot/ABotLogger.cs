using System;
using System.IO;

namespace ABot;

/// <summary>
/// ABot 文件日志记录器
/// 将所有日志写入到文件，用于调试代理加载等无法看到控制台的情况
/// </summary>
public static class ABotLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Directory.GetCurrentDirectory(), 
        "Mods", "ABot", "Data"
    );

    private static readonly string LogFilePath = Path.Combine(LogDirectory, "abot.log");

    /// <summary>
    /// 初始化日志系统
    /// </summary>
    public static void Initialize()
    {
        try
        {
            // 确保日志目录存在
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }

            // 清空现有日志（每次启动时重新开始）
            if (File.Exists(LogFilePath))
            {
                try
                {
                    File.Delete(LogFilePath);
                }
                catch
                {
                    // 如果无法删除，就追加
                }
            }

            WriteRaw($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ========== ABot Logger Initialized ==========");
            WriteRaw($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Log file: {LogFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABotLogger] Failed to initialize: {ex.Message}");
        }
    }

    /// <summary>
    /// 写入日志信息
    /// </summary>
    public static void Info(string message)
    {
        WriteLog("INFO", message);
    }

    /// <summary>
    /// 写入警告日志
    /// </summary>
    public static void Warn(string message)
    {
        WriteLog("WARN", message);
    }

    /// <summary>
    /// 写入错误日志
    /// </summary>
    public static void Error(string message)
    {
        WriteLog("ERROR", message);
    }

    /// <summary>
    /// 写入调试日志
    /// </summary>
    public static void Debug(string message)
    {
        WriteLog("DEBUG", message);
    }

    /// <summary>
    /// 内部日志写入方法
    /// </summary>
    private static void WriteLog(string level, string message)
    {
        try
        {
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            WriteRaw(logLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABotLogger] Failed to write log: {ex.Message}");
        }
    }

    /// <summary>
    /// 原始日志写入（内部使用）
    /// </summary>
    private static void WriteRaw(string logLine)
    {
        try
        {
            // 确保日志目录存在
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }

            // 追加写入日志文件
            File.AppendAllText(LogFilePath, logLine + Environment.NewLine);

            // 同时输出到控制台（如果可用）
            Console.WriteLine(logLine);
        }
        catch (Exception ex)
        {
            // 如果文件写入失败，至少输出到控制台
            try
            {
                Console.WriteLine($"[ABotLogger] Failed to write to {LogFilePath}: {ex.Message}");
            }
            catch
            {
                // 如果连控制台都输出不了，就没办法了
            }
        }
    }
}
