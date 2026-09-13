using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

#nullable enable
namespace MDiceV2.Models;

/// <summary>
/// 先攻列表条目
/// </summary>
public class InitiativeListEntry
{
    /// <summary>
    /// 人物名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 先攻值
    /// </summary>
    public int InitiativeValue { get; set; }

    /// <summary>
    /// 掷骰表达式（记录用，用于显示）
    /// </summary>
    public string DiceExpression { get; set; } = string.Empty;

    /// <summary>
    /// 详细掷骰结果（如 "1d20 = [12] + 5 = 17"）
    /// </summary>
    public string RollDetail { get; set; } = string.Empty;

    /// <summary>
    /// 添加时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 先攻列表 - 按群ID存储
/// </summary>
public class InitiativeList
{
    /// <summary>
    /// 所属群ID
    /// </summary>
    public long GroupId { get; set; }

    /// <summary>
    /// 先攻列表条目（使用List，按添加顺序）
    /// </summary>
    private List<InitiativeListEntry> _entries = new();

    /// <summary>
    /// 线程安全锁
    /// </summary>
    private readonly object _lockObj = new object();

    /// <summary>
    /// 最后修改时间
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 添加条目到先攻列表
    /// 如果名称已存在，自动添加序数（如 "(2)", "(3)"）
    /// </summary>
    /// <param name="entry">先攻列表条目</param>
    /// <returns>实际使用的人物名称</returns>
    public string AddEntry(InitiativeListEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
            return string.Empty;

        lock (_lockObj)
        {
            string actualName = entry.Name;
            int counter = 2;

            // 检查重名并添加序数
            while (_entries.Any(e => e.Name == actualName))
            {
                actualName = $"{entry.Name}({counter})";
                counter++;
            }

            entry.Name = actualName;
            entry.CreatedAt = DateTime.UtcNow;
            _entries.Add(entry);
            LastModified = DateTime.UtcNow;

            return actualName;
        }
    }

    /// <summary>
    /// 根据名称获取条目
    /// </summary>
    public InitiativeListEntry? GetByName(string name)
    {
        lock (_lockObj)
        {
            return _entries.FirstOrDefault(e => e.Name == name);
        }
    }

    /// <summary>
    /// 根据名称移除条目
    /// </summary>
    public bool RemoveByName(string name)
    {
        lock (_lockObj)
        {
            var entry = _entries.FirstOrDefault(e => e.Name == name);
            if (entry != null)
            {
                _entries.Remove(entry);
                LastModified = DateTime.UtcNow;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 获取按先攻值排序（从高到低）的条目列表
    /// </summary>
    public List<InitiativeListEntry> GetSorted()
    {
        lock (_lockObj)
        {
            return _entries
                .OrderByDescending(e => e.InitiativeValue)
                .ThenBy(e => e.CreatedAt)
                .ToList();
        }
    }

    /// <summary>
    /// 获取所有条目（不排序）
    /// </summary>
    public List<InitiativeListEntry> GetAll()
    {
        lock (_lockObj)
        {
            return new List<InitiativeListEntry>(_entries);
        }
    }

    /// <summary>
    /// 清空列表
    /// </summary>
    public void Clear()
    {
        lock (_lockObj)
        {
            _entries.Clear();
            LastModified = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 获取列表中的条目数
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lockObj)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// 是否为空
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lockObj)
            {
                return _entries.Count == 0;
            }
        }
    }
}

/// <summary>
/// 用于持久化的群先攻数据
/// </summary>
public class GroupInitiativeData
{
    /// <summary>
    /// 群ID
    /// </summary>
    public long GroupId { get; set; }

    /// <summary>
    /// 先攻列表条目集合
    /// </summary>
    public List<InitiativeListEntry> Entries { get; set; } = new();

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后修改时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
