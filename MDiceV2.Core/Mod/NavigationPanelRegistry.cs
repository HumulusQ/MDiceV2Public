using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MDiceV2.Interfaces;

namespace MDiceV2.Core.Mod;

/// <summary>
/// 导航面板注册表
/// 管理 Mod 向主窗口注册的导航面板
/// 支持动态注册和查询面板信息
/// </summary>
public class NavigationPanelRegistry : INavigationPanelRegistry
{
    private static NavigationPanelRegistry? _instance;
    private readonly Dictionary<string, INavigationPanelProvider> _registeredPanels = new();
    private readonly List<(string PanelId, Control Panel)> _cachedPanels = new();

    /// <summary>
    /// 获取全局单例实例
    /// </summary>
    public static NavigationPanelRegistry Instance => _instance ??= new NavigationPanelRegistry();

    /// <summary>Raised when a mod navigation panel is added or removed at runtime.</summary>
    public event EventHandler<NavigationPanelChangedEventArgs>? PanelChanged;

    /// <summary>
    /// 注册导航面板提供者
    /// </summary>
    /// <param name="provider">实现 INavigationPanelProvider 接口的对象</param>
    /// <exception cref="InvalidOperationException">面板ID已被注册</exception>
    public void Register(INavigationPanelProvider provider)
    {
        if (provider == null)
            throw new ArgumentNullException(nameof(provider));

        if (string.IsNullOrWhiteSpace(provider.PanelId))
            throw new ArgumentException("面板ID不能为空", nameof(provider.PanelId));

        if (_registeredPanels.ContainsKey(provider.PanelId))
            throw new InvalidOperationException($"面板 '{provider.PanelId}' 已被注册");

        _registeredPanels.Add(provider.PanelId, provider);
        MDiceV2.Models.Log.InfoFormat($"[NavigationPanelRegistry.Register] Successfully registered panel: '{provider.PanelId}' (name: {provider.PanelName}, priority: {provider.Priority})");
        PanelChanged?.Invoke(this, new NavigationPanelChangedEventArgs(provider, true));
    }

    /// <summary>
    /// 注销导航面板
    /// </summary>
    /// <param name="panelId">面板ID</param>
    /// <returns>是否成功注销</returns>
    public bool Unregister(string panelId)
    {
        if (string.IsNullOrWhiteSpace(panelId))
            return false;

        // 移除缓存的面板
        _cachedPanels.RemoveAll(x => x.PanelId == panelId);

        if (!_registeredPanels.Remove(panelId, out var provider))
            return false;

        PanelChanged?.Invoke(this, new NavigationPanelChangedEventArgs(provider, false));
        return true;
    }

    /// <summary>
    /// 获取已注册的所有面板提供者（按优先级排序）
    /// </summary>
    /// <returns>按优先级降序排列的面板提供者列表</returns>
    public IList<INavigationPanelProvider> GetRegisteredPanels()
    {
        return _registeredPanels.Values
            .OrderByDescending(p => p.Priority)
            .ToList();
    }

    /// <summary>
    /// 获取指定ID的面板提供者
    /// </summary>
    /// <param name="panelId">面板ID</param>
    /// <returns>面板提供者，如果不存在返回 null</returns>
    public INavigationPanelProvider? GetPanel(string panelId)
    {
        _registeredPanels.TryGetValue(panelId, out var provider);
        return provider;
    }

    /// <summary>
    /// 创建面板实例（带缓存）
    /// </summary>
    /// <param name="panelId">面板ID</param>
    /// <returns>面板控件实例，如果面板未注册返回 null</returns>
    public Control? CreatePanel(string panelId)
    {
        if (string.IsNullOrWhiteSpace(panelId))
            return null;

        // 检查缓存
        var cached = _cachedPanels.FirstOrDefault(x => x.PanelId == panelId).Panel;
        if (cached != null)
        {
            MDiceV2.Models.Log.InfoFormat($"[NavigationPanelRegistry.CreatePanel] Returning cached panel for '{panelId}'");
            return cached;
        }

        // 创建新面板
        if (!_registeredPanels.TryGetValue(panelId, out var provider))
        {
            MDiceV2.Models.Log.Warn($"[NavigationPanelRegistry.CreatePanel] Panel '{panelId}' not registered");
            return null;
        }

        MDiceV2.Models.Log.InfoFormat($"[NavigationPanelRegistry.CreatePanel] Creating new panel for '{panelId}'");
        var panel = provider.CreatePanel();
        _cachedPanels.Add((panelId, panel));
        MDiceV2.Models.Log.InfoFormat($"[NavigationPanelRegistry.CreatePanel] Successfully created panel for '{panelId}'");
        return panel;
    }

    /// <summary>
    /// 检查面板是否已注册
    /// </summary>
    /// <param name="panelId">面板ID</param>
    /// <returns>是否已注册</returns>
    public bool IsRegistered(string panelId)
    {
        return !string.IsNullOrWhiteSpace(panelId) && _registeredPanels.ContainsKey(panelId);
    }

    /// <summary>
    /// 获取已注册的面板总数
    /// </summary>
    public int Count => _registeredPanels.Count;

    /// <summary>
    /// 清空所有注册的面板
    /// </summary>
    public void Clear()
    {
        _registeredPanels.Clear();
        _cachedPanels.Clear();
    }
}

public sealed class NavigationPanelChangedEventArgs : EventArgs
{
    public NavigationPanelChangedEventArgs(INavigationPanelProvider provider, bool isRegistered)
    {
        Provider = provider;
        IsRegistered = isRegistered;
    }

    public INavigationPanelProvider Provider { get; }
    public bool IsRegistered { get; }
}
