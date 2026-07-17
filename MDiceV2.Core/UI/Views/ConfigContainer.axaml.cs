using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using MDiceV2.Core.UI.ViewModels;
using MDiceV2.Models;

/// <summary>
/// 配置容器视图类
/// 处理UI交互和布局调整逻辑
/// </summary>
namespace MDiceV2.Core.UI.Views;

/// <summary>
/// ConfigContainer的用户控件实现
/// 管理配置项的显示和交互，包括拖拽限制和动态布局
/// </summary>
public partial class ConfigContainer : UserControl
{
    /// <summary>
    /// 视图模型引用
    /// </summary>
    protected ConfigContainerViewModel? _viewModel;
    private bool _isSectionExpanded = true;
    public bool IsSectionExpanded => _isSectionExpanded;
    public event EventHandler? SectionExpansionChanged;

    /// <summary>
    /// 构造函数
    /// 初始化组件并设置数据上下文变化监听
    /// </summary>
    public ConfigContainer()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// 初始化Avalonia组件
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 初始化帮助信息文本
    /// </summary>
    public void InitializeHelpText(string helpText)
    {
        var helpTextBlock = this.FindControl<TextBlock>("HelpTextBlock");
        if (helpTextBlock != null)
        {
            helpTextBlock.Text = helpText;
        }
    }

    /// <summary>
    /// 数据上下文变化处理
    /// 当视图模型改变时更新引用
    /// </summary>
    /// <param name="sender">事件发送者</param>
    /// <param name="e">事件参数</param>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel = DataContext as ConfigContainerViewModel;
    }

    /// <summary>
    /// Collapses the section body while keeping its shared section toolbar available.
    /// </summary>
    protected void SectionToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        var sectionBody = this.FindControl<Control>("SectionBody");
        var glyph = this.FindControl<TextBlock>("SectionToggleGlyph");
        var toggleButton = this.FindControl<Button>("SectionToggleButton");
        if (sectionBody == null)
            return;

        _isSectionExpanded = !_isSectionExpanded;
        sectionBody.IsVisible = _isSectionExpanded;
        SectionExpansionChanged?.Invoke(this, EventArgs.Empty);

        if (glyph != null)
            glyph.Text = _isSectionExpanded ? "−" : "+";

        if (toggleButton != null)
            ToolTip.SetTip(toggleButton, _isSectionExpanded ? "Collapse section" : "Expand section");
    }


    /// <summary>
    /// 搜索按钮点击处理
    /// 切换搜索面板的可见性和动画，如果Help面板当前打开，会先关闭Help面板
    /// 包含透明度淡入淡出效果
    /// </summary>
    /// <param name="sender">搜索按钮</param>
    /// <param name="e">点击事件参数</param>
    protected async void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            // 检查Help面板是否打开，如果打开则先关闭它
            if (_viewModel.IsHelpPanelVisible)
            {
                await CloseHelpPanel();
            }

            // 切换状态
            bool newState = !_viewModel.IsSearchPanelVisible;

            // 触发动画
            var searchPanel = this.FindControl<Border>("SearchPanel");
            var fillArea = this.FindControl<Border>("FillArea");
            var searchControls = this.FindControl<Grid>("SearchControls");

            if (searchPanel != null)
            {
                if (newState)
                {
                    // 展开：立即设置为可见，动画会自动开始
                    _viewModel.IsSearchPanelVisible = true;
                    fillArea.Height = 54;
                    searchPanel.Height = 56;
                    searchPanel.Opacity = 1.0; // 整个搜索面板淡入
                }
                else
                {
                    // 收回：等待动画完成（0.3秒），然后设置为不可见
                    fillArea.Height = 0; // 填充区折叠到0像素
                    searchPanel.Height = 0;
                    searchPanel.Opacity = 0.0; // 整个搜索面板淡出
                    await Task.Delay(300); // 等待动画完成（0.3秒）
                    _viewModel.IsSearchPanelVisible = false;
                }
            }
        }
    }

    /// <summary>
    /// 关闭Help面板的辅助方法
    /// （该方法原本就属于 ConfigContainer，用于 SearchButton_Click / HelpButton_Click 调用）
    /// 修正变量命名错误，避免 Panel/helpPanel 混用导致的运行时异常。
    /// </summary>
    private async Task CloseHelpPanel()
    {
        // 查找 Help 相关控件
        var helpPanel = this.FindControl<Border>("HelpPanel");
        var helpFillArea = this.FindControl<Border>("HelpFillArea");
        var helpContent = this.FindControl<Grid>("HelpContent");

        if (helpPanel != null && helpFillArea != null && helpContent != null)
        {
            // 收起：同时收起所有元素
            helpContent.Height = 0; // 折叠Help内容区域
            helpFillArea.Height = 0; // 填充区折叠到0像素
            helpPanel.Height = 0;
            helpPanel.Opacity = 0.0; // 整个Help面板淡出
            await Task.Delay(300); // 等待动画完成

            if (_viewModel != null)
            {
                _viewModel.IsHelpPanelVisible = false;
            }
        }
    }

    /// <summary>
    /// Help按钮点击处理
    /// 切换Help面板的可见性和动画，如果搜索面板当前打开，会先关闭搜索面板
    /// </summary>
    /// <param name="sender">Help按钮</param>
    /// <param name="e">点击事件参数</param>
    protected async void HelpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            // 检查搜索面板是否打开，如果打开则先关闭它
            if (_viewModel.IsSearchPanelVisible)
            {
                // 触发动画关闭搜索面板
                var searchPanel = this.FindControl<Border>("SearchPanel");
                var fillArea = this.FindControl<Border>("FillArea");

                if (searchPanel != null && fillArea != null)
                {
                    // 收回：等待动画完成（0.3秒），然后设置为不可见
                    fillArea.Height = 0; // 填充区折叠到0像素
                    searchPanel.Height = 0;
                    searchPanel.Opacity = 0.0; // 整个搜索面板淡出
                    await Task.Delay(300); // 等待动画完成（0.3秒）
                    _viewModel.IsSearchPanelVisible = false;
                }
            }

            // 切换状态
            bool newState = !_viewModel.IsHelpPanelVisible;

            // 触发动画
            var helpPanel = this.FindControl<Border>("HelpPanel");
            var helpFillArea = this.FindControl<Border>("HelpFillArea");
            var helpContent = this.FindControl<Grid>("HelpContent");

            if (helpPanel != null && helpFillArea != null && helpContent != null)
            {
                if (newState)
                {
                    // 展开：立即设置为可见，动画会自动开始
                    _viewModel.IsHelpPanelVisible = true;
                    helpFillArea.Height = 38;
                    helpContent.Height = 120; // 展开Help内容区域
                    helpPanel.Height = 174;
                    helpPanel.Opacity = 1.0; // 整个Help面板淡入
                }
                else
                {
                    // 收起：同时收起所有元素
                    helpContent.Height = 0;
                    helpFillArea.Height = 0;
                    helpPanel.Height = 0;
                    helpPanel.Opacity = 0.0;
                    await Task.Delay(300);
                    _viewModel.IsHelpPanelVisible = false;
                }
            }
        }
    }

    /// <summary>
    /// 网格大小变化处理
    /// 根据容器宽度动态设置列的最小和最大宽度，实现拖拽限制
    /// </summary>
    /// <param name="sender">Grid控件</param>
    /// <param name="e">大小变化事件参数</param>
    protected void Grid_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid && grid.ColumnDefinitions.Count >= 3)
        {
            var totalWidth = grid.Bounds.Width;
            var minWidth = totalWidth * 0.1;
            var maxWidth = totalWidth * 0.9;

            grid.ColumnDefinitions[0].MinWidth = minWidth;
            grid.ColumnDefinitions[0].MaxWidth = maxWidth;
            grid.ColumnDefinitions[2].MinWidth = minWidth;
            grid.ColumnDefinitions[2].MaxWidth = maxWidth;

            // 只有在初次加载时设置比例，之后不再强制修改以避免拖拽冲突
            if (grid.DataContext is ConfigItem item &&
                item.LeftColumnRatio == 0.5 && item.RightColumnRatio == 0.5)
            {
                // 初次加载时的默认比例
                item.LeftColumnRatio = 0.5;
                item.RightColumnRatio = 0.5;
            }
        }
    }
    // 存储当前获得焦点的TextBox引用和其Border引用，防止虚拟化导致的问题
    private TextBox? _focusedTextBox;
    private Border? _focusedBorder;
    private bool _isTextBoxExpanded = false;

    // 文本框获得焦点：扩展自身高度，并同步放大父级卡片 Border 与对应 ListBoxItem，高度变化由 Transitions 平滑动画
    protected void OnItemTextBoxGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
            return;

        // 如果已有其他TextBox被展开，先收起它
        if (_isTextBoxExpanded && _focusedTextBox != null && _focusedTextBox != tb)
        {
            RestoreTextBox(_focusedTextBox, _focusedBorder);
        }

        // 保存当前TextBox和Border引用
        _focusedTextBox = tb;
        _focusedBorder = FindParentBorder(tb);
        _isTextBoxExpanded = true;

        // 扩展文本框高度（内部内容区域）
        tb.Height = 74;

        // 同步扩展父级 Border 高度（ItemTemplate 中的卡片容器）
        if (_focusedBorder != null)
        {
            _focusedBorder.Height = 92;
        }
    }

    // 文本框失去焦点：收回自身高度，并同步恢复父级 Border 与 ListBoxItem 高度
    protected async void OnItemTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
            return;

        // 仅处理当前已展开的TextBox失焦
        if (_focusedTextBox == tb && _isTextBoxExpanded)
        {
            // 获取TextBox的DataContext（应该是ConfigItem）
            if (tb.DataContext is ConfigItem item && tb.Text != item.Value)
            {
                // 推送值变更到远程（如果启用了同步模式）
                await PushConfigUpdateAsync(item.Key, tb.Text ?? "");
            }

            RestoreTextBox(tb, _focusedBorder);
            _focusedTextBox = null;
            _focusedBorder = null;
            _isTextBoxExpanded = false;
        }
    }

    /// <summary>
    /// 推送配置项更新到远程服务器（仅在同步模式启用时）
    /// </summary>
    protected async Task PushConfigUpdateAsync(string key, string value)
    {
        try
        {
            // 尝试获取主窗口，然后获取其DataContext（应该是MainViewModel）
            var window = this.GetVisualRoot() as Window;
            if (window?.DataContext is MDiceV2.Core.UI.ViewModels.MainViewModel mainViewModel)
            {
                // 调用MainViewModel的推送方法
                await mainViewModel.PushConfigUpdateToRemoteAsync(key, value);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigContainer] Error pushing config update: {ex.Message}");
        }
    }

    /// <summary>
    /// 辅助方法：恢复TextBox和Border到原始大小
    /// 提供统一的恢复逻辑，避免代码重复
    /// </summary>
    private void RestoreTextBox(TextBox? tb, Border? border)
    {
        if (tb != null)
        {
            // 恢复文本框紧凑高度
            tb.Height = 34;
        }

        if (border != null)
        {
            // 恢复为原始卡片高度
            border.Height = 54;
        }
    }

    /// <summary>
    /// 从当前控件向上查找 ItemTemplate 中的父级 Border（卡片容器）。
    /// 依赖当前模板结构：TextBox -> Grid(右列) -> Grid(行) -> Border。
    /// </summary>
    private static Border? FindParentBorder(Control control)
    {
        var current = control.Parent;
        while (current is Control c)
        {
            if (c is Border b)
                return b;

            current = c.Parent;
        }
        return null;
    }
    /// <summary>
    /// GridSplitter拖拽增量处理
    /// 更新配置项的列宽度比例，实现持久化
    /// </summary>
    /// <param name="sender">GridSplitter控件</param>
    /// <param name="e">拖拽事件参数</param>
    protected void GridSplitter_DragDelta(object? sender, RoutedEventArgs e)
    {
        if (sender is GridSplitter splitter && splitter.Parent is Grid grid && grid.DataContext is ConfigItem item)
        {
            // 获取当前列的实际宽度
            var leftWidth = grid.ColumnDefinitions[0].Width.Value;
            var rightWidth = grid.ColumnDefinitions[2].Width.Value;
            var totalWidth = leftWidth + rightWidth;

            if (totalWidth > 0)
            {
                // 更新配置项的宽度比例
                item.LeftColumnRatio = leftWidth / totalWidth;
                item.RightColumnRatio = rightWidth / totalWidth;
            }
        }
    }

    /// <summary>
    /// 重置按钮点击：将当前项恢复为默认值
    /// </summary>
    protected void OnResetButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ConfigItem item)
        {
            _viewModel?.ResetToDefault(item.Key);

            var listBoxItem = btn.FindAncestorOfType<ListBoxItem>();

            if (item.Type == ConfigType.LineEdit)
            {
                var textBox = listBoxItem?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(tb => tb.IsVisible);
                if (textBox != null)
                {
                    // 清除之前的展开状态
                    if (_focusedTextBox != textBox)
                    {
                        RestoreTextBox(_focusedTextBox, _focusedBorder);
                        _focusedTextBox = null;
                        _focusedBorder = null;
                        _isTextBoxExpanded = false;
                    }

                    textBox.Focus();
                    textBox.Height = 75;
                    var border = FindParentBorder(textBox);
                    if (border != null)
                    {
                        border.Height = 90;
                        // 更新焦点追踪
                        _focusedTextBox = textBox;
                        _focusedBorder = border;
                        _isTextBoxExpanded = true;
                    }
                }
            }
            else if (item.Type == ConfigType.CheckBox)
            {
                var checkBox = listBoxItem?.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(cb => cb.IsVisible);
                checkBox?.Focus();
            }
        }
    }

}
