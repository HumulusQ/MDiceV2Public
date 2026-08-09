using Avalonia.Controls;

namespace MDiceV2.Interfaces;

/// <summary>
/// 导航面板提供者接口
/// Mod 可以实现此接口来向主窗口注册自定义导航面板
/// </summary>
public interface INavigationPanelProvider
{
    /// <summary>
    /// 导航面板的唯一标识符
    /// 用于区分不同的 Mod 面板
    /// </summary>
    string PanelId { get; }

    /// <summary>
    /// 导航面板的显示名称
    /// 显示在导航栏中
    /// </summary>
    string PanelName { get; }

    /// <summary>
    /// 导航面板的优先级
    /// 数值越大，显示位置越靠前
    /// 默认优先级为 100
    /// </summary>
    int Priority => 100;

    /// <summary>
    /// 导航面板的图标路源
    /// Avalonia 资源路径格式，例如：
    /// avares://AssemblyName/Assets/icon.png
    /// </summary>
    string? IconSource => null;

    /// <summary>
    /// 是否为 Mod 注册的面板
    /// 用于 UI 样式区分（Mod 面板使用淡黄色背景）
    /// </summary>
    bool IsModPanel => true;

    /// <summary>
    /// 创建导航面板的控件
    /// 返回 UserControl 或其他 Control 类型的实例
    /// 此方法会在导航项被选中时调用
    /// </summary>
    Control CreatePanel();
}
