using System;
using System.Collections.Generic;
using System.Linq;

namespace AIMod.Trpg;

/// <summary>
/// 注意力标记缓存：延迟归纳，避免单次行为立即影响长期状态
/// </summary>
public class AttentionBuffer
{
    private readonly Dictionary<(string WorldId, long GroupId, string CharacterId), List<AttentionMarker>> _buffers = new();

    /// <summary>
    /// 添加标记到缓存
    /// </summary>
    public void AddMarker(TrpgScope scope, string characterId, AttentionMarker marker)
    {
        var key = (scope.WorldId, scope.GroupId, characterId);
        lock (_buffers)
        {
            if (!_buffers.ContainsKey(key))
                _buffers[key] = new List<AttentionMarker>();
            _buffers[key].Add(marker);
        }
    }

    /// <summary>
    /// 获取并清空缓存
    /// </summary>
    public List<AttentionMarker> GetAndClear(TrpgScope scope, string characterId)
    {
        var key = (scope.WorldId, scope.GroupId, characterId);
        lock (_buffers)
        {
            if (_buffers.TryGetValue(key, out var markers))
            {
                _buffers.Remove(key);
                return markers;
            }
            return new List<AttentionMarker>();
        }
    }

    /// <summary>
    /// 获取缓存数量
    /// </summary>
    public int GetCount(TrpgScope scope, string characterId)
    {
        var key = (scope.WorldId, scope.GroupId, characterId);
        lock (_buffers)
        {
            return _buffers.TryGetValue(key, out var markers) ? markers.Count : 0;
        }
    }

    /// <summary>
    /// 获取高 importance 标记（> 0.9）
    /// </summary>
    public List<AttentionMarker> GetHighImportanceMarkers(TrpgScope scope, string characterId)
    {
        var key = (scope.WorldId, scope.GroupId, characterId);
        lock (_buffers)
        {
            if (_buffers.TryGetValue(key, out var markers))
                return markers.Where(m => m.Importance >= 0.9).ToList();
            return new List<AttentionMarker>();
        }
    }

    /// <summary>
    /// 清空指定角色的缓存
    /// </summary>
    public void Clear(TrpgScope scope, string characterId)
    {
        var key = (scope.WorldId, scope.GroupId, characterId);
        lock (_buffers)
        {
            _buffers.Remove(key);
        }
    }

    /// <summary>
    /// 清空所有缓存
    /// </summary>
    public void ClearAll()
    {
        lock (_buffers)
        {
            _buffers.Clear();
        }
    }
}
