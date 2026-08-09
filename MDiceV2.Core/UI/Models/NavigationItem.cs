using MDiceV2.Interfaces;

namespace MDiceV2.Core.UI.Models;

/// <summary>
/// 导航项数据模型
/// 用于动态生成导航列表项
/// </summary>
public class NavigationItem
{
    /// <summary>
    /// 面板 ID
    /// </summary>
    public string PanelId { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 图标路径
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// 是否为 Mod 面板
    /// 用于 UI 样式区分（设置背景颜色）
    /// </summary>
    public bool IsModPanel { get; set; }

    /// <summary>
    /// 构造函数（从 INavigationPanelProvider 创建）
    /// </summary>
    public NavigationItem(INavigationPanelProvider provider)
    {
        PanelId = provider.PanelId;
        DisplayName = provider.PanelName;
        IconPath = provider.IconSource;
        IsModPanel = provider.IsModPanel;
    }
}
