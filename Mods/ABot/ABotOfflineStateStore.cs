using System;
using System.Collections.Generic;

namespace ABot;

/// <summary>
/// 离线用户状态存储
/// 
/// 用途：
/// ====
/// 1. 保存被 LRU 缓存驱逐的用户的游戏状态
/// 2. 作为内存层，指挥状态最终持久化到数据库
/// 3. 支持状态恢复（当用户重新上线时）
/// 
/// 生命周期：
/// ========
/// 阶段 2：LRU 驱逐一个用户 → 调用 SaveOfflineState()
/// 阶段 3：状态保存在内存中 → 这个类
/// 阶段 5：状态持久化到 SQLite → 实现 PersistToDatabase()
/// 
/// 内存管理：
/// ========
/// - 最多保存最近 100 个离线用户的状态（根据需要调整）
/// - 当超过限制时，删除最旧的（按 CreatedAt）
/// - 内存占用估算：100 users × 50 KB/user ≈ 5 MB
/// </summary>
public class ABotOfflineStateStore
{
    /// <summary>
    /// 离线状态存储的大小限制（最多保存多少个用户的离线状态）
    /// </summary>
    private const int MAX_OFFLINE_STATES = 100;

    /// <summary>
    /// 离线用户状态字典
    /// 键：用户 ID，值：该用户的最新快照
    /// </summary>
    private Dictionary<long, ABotStateSnapshot> _offlineStates = new();

    /// <summary>
    /// 离线状态的访问时间追踪（用于 LRU 清理）
    /// 键：用户 ID，值：最后保存时间
    /// </summary>
    private Dictionary<long, DateTime> _accessTimes = new();

    // ============ 方法 ============

    /// <summary>
    /// 保存离线用户的状态快照
    /// 如果用户已有离线状态，则覆盖
    /// 如果达到容量上限，删除最旧的离线状态
    /// </summary>
    public void SaveOfflineState(ABotStateSnapshot snapshot)
    {
        if (!snapshot.IsValid)
        {
            Console.WriteLine($"[ABot OfflineStore] WARNING: Attempted to save invalid snapshot for user {snapshot.UserId}");
            return;
        }

        // 如果已达容量，删除最旧的
        if (_offlineStates.Count >= MAX_OFFLINE_STATES && !_offlineStates.ContainsKey(snapshot.UserId))
        {
            long oldestUserId = -1;
            DateTime oldestTime = DateTime.MaxValue;

            foreach (var (userId, accessTime) in _accessTimes)
            {
                if (accessTime < oldestTime)
                {
                    oldestTime = accessTime;
                    oldestUserId = userId;
                }
            }

            if (oldestUserId != -1)
            {
                _offlineStates.Remove(oldestUserId);
                _accessTimes.Remove(oldestUserId);
                Console.WriteLine($"[ABot OfflineStore] Evicted offline state for user {oldestUserId} (store full, size: {MAX_OFFLINE_STATES})");
            }
        }

        // 保存新快照
        _offlineStates[snapshot.UserId] = snapshot;
        _accessTimes[snapshot.UserId] = snapshot.CreatedAt;
        Console.WriteLine($"[ABot OfflineStore] Saved offline state for user {snapshot.UserId} ({snapshot.EstimatedSizeBytes / 1024} KB)");
    }

    /// <summary>
    /// 获取用户的离线状态快照
    /// 如果用户没有离线状态，返回 null
    /// </summary>
    public ABotStateSnapshot? GetOfflineState(long userId)
    {
        if (_offlineStates.TryGetValue(userId, out var snapshot))
        {
            Console.WriteLine($"[ABot OfflineStore] Retrieved offline state for user {userId}");
            return snapshot;
        }

        return null;
    }

    /// <summary>
    /// 检查用户是否有离线状态
    /// </summary>
    public bool HasOfflineState(long userId) => _offlineStates.ContainsKey(userId);

    /// <summary>
    /// 删除用户的离线状态（例如用户删除账户时）
    /// </summary>
    public void RemoveOfflineState(long userId)
    {
        if (_offlineStates.Remove(userId))
        {
            _accessTimes.Remove(userId);
            Console.WriteLine($"[ABot OfflineStore] Removed offline state for user {userId}");
        }
    }

    /// <summary>
    /// 获取当前离线状态计数
    /// </summary>
    public int OfflineStateCount => _offlineStates.Count;

    /// <summary>
    /// 获取所有离线用户 ID
    /// </summary>
    public IEnumerable<long> GetAllOfflineUserIds() => _offlineStates.Keys;

    /// <summary>
    /// 估算整个离线状态存储的内存占用
    /// </summary>
    public int EstimateTotalMemoryBytes()
    {
        int total = 0;
        foreach (var snapshot in _offlineStates.Values)
        {
            total += snapshot.EstimatedSizeBytes;
        }
        return total;
    }

    /// <summary>
    /// 清空所有离线状态（仅用于测试或调试）
    /// </summary>
    public void ClearAll()
    {
        int count = _offlineStates.Count;
        _offlineStates.Clear();
        _accessTimes.Clear();
        Console.WriteLine($"[ABot OfflineStore] Cleared all offline states ({count} users)");
    }

    /// <summary>
    /// 获取离线状态存储的摘要信息
    /// </summary>
    public override string ToString()
    {
        return $"ABotOfflineStateStore(States={OfflineStateCount}/{MAX_OFFLINE_STATES}, " +
               $"Memory≈{EstimateTotalMemoryBytes() / 1024} KB)";
    }
}
