using System;
using System.Diagnostics;
using System.Text;

namespace MDiceV2.Models;

/// <summary>
/// 全局调试监控器 - 用于记录指令的性能信息
/// 特点：
/// - 关闭时零性能开销（直接返回）
/// - 打开时将日志收集到内存缓冲区
/// - 支持记录来自启动者的多条指令性能信息
/// - 当日志接近2000字符上限时自动关闭，或可手动通过 #pfm stop 结束
/// - 返回后自动关闭开关并重置状态
/// </summary>
public static class DebugMonitor
{
    private static bool _isEnabled = false;
    private static StringBuilder? _debugBuffer;
    private static readonly object _lockObject = new();
    private static Guid _sessionId = Guid.Empty;
    private static long _initiatorUserId = -1;  // 启动调试的用户ID
    private const int MAX_OUTPUT_LENGTH = 4000;  // 最大输出长度
    private static int _messageCount = 0;  // 记录消息计数（支持多条消息）

    /// <summary>
    /// 启动调试模式 - 开始收集来自启动者的多条指令性能信息
    /// </summary>
    public static void StartDebugSession(long userId)
    {
        lock (_lockObject)
        {
            _isEnabled = true;
            _initiatorUserId = userId;
            _debugBuffer = new StringBuilder();
            _sessionId = Guid.NewGuid();
            _messageCount = 0;
            AppendLog($"[DEBUG SESSION START] SessionId: {_sessionId}, Initiator: {userId}");
        }
    }

    /// <summary>
    /// 检查当前消息发送者是否为启动者
    /// </summary>
    public static bool IsInitiator(long userId)
    {
        return _isEnabled && _initiatorUserId == userId;
    }

    /// <summary>
    /// 完成一次消息处理后记录计数
    /// 如果接近长度限制，自动关闭并返回收集的性能信息
    /// 否则继续记录，返回null表示继续收集
    /// </summary>
    public static string? CompleteAndAutoClose()
    {
        lock (_lockObject)
        {
            if (!_isEnabled || _debugBuffer == null)
                return null;

            _messageCount++;
            AppendLog($"[MESSAGE COMPLETED] Message #{_messageCount}");
            
            // 检查是否接近长度限制（80%）
            if (GetBufferLengthUnsafe() > MAX_OUTPUT_LENGTH * 0.8)
            {
                AppendLog($"[DEBUG SESSION AUTO-CLOSED] SessionId: {_sessionId} - 日志长度接近限制");
                var result = _debugBuffer.ToString();
                
                // 限制输出长度
                if (result.Length > MAX_OUTPUT_LENGTH)
                {
                    result = result.Substring(0, MAX_OUTPUT_LENGTH) + "\n...（日志已截断，超过2000字符上限）";
                }
                
                _isEnabled = false;
                _debugBuffer = null;
                _sessionId = Guid.Empty;
                _initiatorUserId = -1;
                _messageCount = 0;

                return result;
            }
            
            // 尚未接近限制，继续记录
            return null;
        }
    }

    /// <summary>
    /// 手动停止调试模式并返回收集的所有信息（仅限启动者调用）
    /// 返回所有已记录的多条消息信息
    /// </summary>
    public static string? StopDebugSession(long userId)
    {
        lock (_lockObject)
        {
            if (!_isEnabled || _debugBuffer == null || !IsInitiator(userId))
                return null;

            AppendLog($"[DEBUG SESSION END - MANUAL STOP] SessionId: {_sessionId}, Total Messages: {_messageCount}");
            var result = _debugBuffer.ToString();
            
            // 限制输出长度
            if (result.Length > MAX_OUTPUT_LENGTH)
            {
                result = result.Substring(0, MAX_OUTPUT_LENGTH) + "\n...（日志已截断，超过2000字符上限）";
            }
            
            _isEnabled = false;
            _debugBuffer = null;
            _sessionId = Guid.Empty;
            _initiatorUserId = -1;
            _messageCount = 0;

            return result;
        }
    }

    /// <summary>
    /// 检查调试模式是否启用
    /// 关闭时此方法返回 false，调用方应立即返回
    /// </summary>
    public static bool IsEnabled => _isEnabled;

    /// <summary>
    /// 记录阶段性能信息
    /// </summary>
    public static void MarkStage(string commandId, int stageNum, string description, long elapsedMs)
    {
        if (!_isEnabled) return;

        lock (_lockObject)
        {
            if (_debugBuffer != null)
            {
                _debugBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] [PERF] [{commandId}] Stage {stageNum}: {description} | Elapsed: {elapsedMs}ms");
            }
        }
    }

    /// <summary>
    /// 记录检查点信息
    /// </summary>
    public static void CheckpointInStage(string commandId, int stageNum, string checkpoint, long elapsedMs)
    {
        if (!_isEnabled) return;

        lock (_lockObject)
        {
            if (_debugBuffer != null)
            {
                _debugBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] [PERF] [{commandId}] Stage {stageNum}.{checkpoint} | Elapsed: {elapsedMs}ms");
            }
        }
    }

    /// <summary>
    /// 记录完成信息
    /// </summary>
    public static void Complete(string commandId, long totalMs)
    {
        if (!_isEnabled) return;

        lock (_lockObject)
        {
            if (_debugBuffer != null)
            {
                _debugBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] [PERF] [{commandId}] COMPLETE | Total: {totalMs}ms");
            }
        }
    }

    /// <summary>
    /// 返回当前缓冲区长度（用于检查是否接近限制）
    /// </summary>
    public static int GetBufferLength()
    {
        lock (_lockObject)
        {
            return GetBufferLengthUnsafe();
        }
    }

    /// <summary>
    /// 内部方法：获取缓冲区长度（不加锁，仅在已加锁的代码中调用）
    /// </summary>
    private static int GetBufferLengthUnsafe()
    {
        return _debugBuffer?.Length ?? 0;
    }

    /// <summary>
    /// 检查是否接近长度限制
    /// </summary>
    public static bool IsApproachingLimit()
    {
        return GetBufferLength() > MAX_OUTPUT_LENGTH * 0.8; // 当接近80%时返回true
    }

    /// <summary>
    /// 记录自定义信息
    /// </summary>
    public static void Log(string message)
    {
        if (!_isEnabled) return;

        lock (_lockObject)
        {
            if (_debugBuffer != null)
            {
                _debugBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            }
        }
    }

    /// <summary>
    /// 内部附加日志
    /// </summary>
    private static void AppendLog(string message)
    {
        if (_debugBuffer != null)
        {
            _debugBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }
}

/// <summary>
/// 性能监测类 - 与 DebugMonitor 集成
/// </summary>
public class PerformanceMonitor
{
    private readonly Stopwatch _totalStopwatch;
    private readonly string _commandId;

    public PerformanceMonitor(string commandId)
    {
        _commandId = commandId;
        _totalStopwatch = Stopwatch.StartNew();
    }

    public void MarkStage(int stageNum, string description)
    {
        var elapsed = _totalStopwatch.ElapsedMilliseconds;
        DebugMonitor.MarkStage(_commandId, stageNum, description, elapsed);
    }

    public void CheckpointInStage(int stageNum, string checkpoint)
    {
        var elapsed = _totalStopwatch.ElapsedMilliseconds;
        DebugMonitor.CheckpointInStage(_commandId, stageNum, checkpoint, elapsed);
    }

    public void Complete()
    {
        _totalStopwatch.Stop();
        DebugMonitor.Complete(_commandId, _totalStopwatch.ElapsedMilliseconds);
    }

    public long GetElapsedMs() => _totalStopwatch.ElapsedMilliseconds;
}
