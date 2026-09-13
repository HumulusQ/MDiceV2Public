using System;
using System.Collections.Generic;

namespace AIMod.Trpg;

/// <summary>
/// 运行时世界状态
/// 存储当前世界的结构化状态，避免 AI 从记忆推导导致状态漂移
/// </summary>
public class RuntimeWorldState
{
    /// <summary>
    /// 当前场景 ID
    /// </summary>
    public string CurrentSceneId { get; set; } = "scene_default";

    /// <summary>
    /// 当前位置描述
    /// </summary>
    public string CurrentLocation { get; set; } = "未知位置";

    /// <summary>
    /// 在场角色列表
    /// </summary>
    public List<string> PresentCharacters { get; set; } = new List<string>();

    /// <summary>
    /// 场景标志（场景内的状态变量）
    /// 例如：{"门锁状态": "已解锁", "灯光": "开启"}
    /// </summary>
    public Dictionary<string, object> SceneFlags { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// 活跃事件列表
    /// 例如：["战斗中", "调查中", "对话中"]
    /// </summary>
    public List<string> ActiveEvents { get; set; } = new List<string>();

    /// <summary>
    /// 当前目标列表
    /// 例如：["探索房间B", "调查实验", "保持警惕"]
    /// </summary>
    public List<string> ActiveObjectives { get; set; } = new List<string>();

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 更新场景标志
    /// </summary>
    public void SetSceneFlag(string key, object value)
    {
        SceneFlags[key] = value;
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 获取场景标志
    /// </summary>
    public T? GetSceneFlag<T>(string key, T? defaultValue = default)
    {
        if (SceneFlags.TryGetValue(key, out var value))
        {
            if (value is T typedValue)
                return typedValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// 添加活跃事件
    /// </summary>
    public void AddActiveEvent(string eventName)
    {
        if (!ActiveEvents.Contains(eventName))
        {
            ActiveEvents.Add(eventName);
            LastUpdated = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 移除活跃事件
    /// </summary>
    public void RemoveActiveEvent(string eventName)
    {
        ActiveEvents.Remove(eventName);
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 添加当前目标
    /// </summary>
    public void AddObjective(string objective)
    {
        if (!ActiveObjectives.Contains(objective))
        {
            ActiveObjectives.Add(objective);
            LastUpdated = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 移除当前目标
    /// </summary>
    public void RemoveObjective(string objective)
    {
        ActiveObjectives.Remove(objective);
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 清空所有目标
    /// </summary>
    public void ClearObjectives()
    {
        ActiveObjectives.Clear();
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// 生成结构化状态字符串（用于 Prompt）
    /// </summary>
    public string ToPromptString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"场景: {CurrentSceneId}");
        sb.AppendLine($"位置: {CurrentLocation}");
        sb.AppendLine($"在场: {string.Join(", ", PresentCharacters)}");
        if (SceneFlags.Count > 0)
            foreach (var flag in SceneFlags)
                sb.AppendLine($"{flag.Key}: {flag.Value}");
        if (ActiveEvents.Count > 0)
            sb.AppendLine($"活跃事件: {string.Join(", ", ActiveEvents)}");
        if (ActiveObjectives.Count > 0)
        {
            sb.AppendLine("目标:");
            foreach (var obj in ActiveObjectives)
                sb.AppendLine($"- {obj}");
        }
        return sb.ToString();
    }
}
