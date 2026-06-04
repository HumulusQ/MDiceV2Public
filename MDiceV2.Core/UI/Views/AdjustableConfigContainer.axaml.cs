using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MDiceV2.Core.UI.ViewModels;
using MDiceV2.Models;

/// <summary>
/// 可调整配置容器视图类
/// 继承自ConfigContainer，增加添加新配置项的功能
/// </summary>
namespace MDiceV2.Core.UI.Views;

/// <summary>
/// AdjustableConfigContainer的用户控件实现
/// 在原有ConfigContainer基础上增加添加新配置项的面板和功能
/// </summary>
public partial class AdjustableConfigContainer : ConfigContainer
{
    private ListBox? _configListBox;

    /// <summary>
    /// 构造函数
    /// 初始化组件并设置数据上下文变化监听
    /// </summary>
    public AdjustableConfigContainer()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// 数据上下文变化事件处理
    /// 当 ViewModel 改变时，重新连接 ListBox 的事件
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // 父类已在其 OnDataContextChanged 中更新了 _viewModel
        // 这里我们需要找到 ListBox 并连接编辑模式逻辑
        _configListBox = this.FindControl<ListBox>("ConfigListBox");
        if (_configListBox != null)
        {
            // 注册 SelectionChanged 事件以启用编辑模式
            _configListBox.SelectionChanged += OnConfigListBoxSelectionChanged;
        }
    }

    /// <summary>
    /// ListBox 选择变化事件处理
    /// 当用户选中配置项时，启用编辑模式（禁用虚拟化）
    /// 当没有选中任何项时，禁用编辑模式（启用虚拟化）
    /// </summary>
    private void OnConfigListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || _configListBox == null)
            return;

        // 如果选中了任何项，启用编辑模式
        if (_configListBox.SelectedItem != null)
        {
            EnableEditMode();
        }
        else
        {
            // 如果没有选中任何项，禁用编辑模式
            DisableEditMode();
        }
    }

    /// <summary>
    /// 启用编辑模式
    /// 切换 ListBox 使用非虚拟化面板以获得流畅的动画
    /// </summary>
    private void EnableEditMode()
    {
        if (_viewModel == null)
            return;

        _viewModel.IsEditMode = true;

        // 将 ListBox 的 ItemsPanel 切换为非虚拟化面板
        if (_configListBox != null && this.Resources["NonVirtualizingPanelTemplate"] is object nonVirtualizingTemplate)
        {
            _configListBox.ItemsPanel = (ITemplate<Panel?>)nonVirtualizingTemplate;
        }
    }

    /// <summary>
    /// 禁用编辑模式
    /// 切换 ListBox 使用虚拟化面板以节约内存
    /// </summary>
    private void DisableEditMode()
    {
        if (_viewModel == null)
            return;

        _viewModel.IsEditMode = false;

        // 将 ListBox 的 ItemsPanel 切换为虚拟化面板
        if (_configListBox != null && this.Resources["VirtualizingPanelTemplate"] is object virtualizingTemplate)
        {
            _configListBox.ItemsPanel = (ITemplate<Panel?>)virtualizingTemplate;
        }
    }

    /// <summary>
    /// 添加面板按钮点击处理
    /// 切换Add面板的可见性和动画
    /// 当Add面板展开时，会先关闭Search和Help面板（互斥逻辑）
    /// </summary>
    private async void AddPanelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            // 切换状态
            bool newState = !_viewModel.IsAddPanelVisible;

            // 触发动画
            var addPanel = this.FindControl<Border>("AddPanel");
            var addFillArea = this.FindControl<Border>("AddFillArea");

            if (addPanel != null && addFillArea != null)
            {
                if (newState)
                {
                    // 展开前，先关闭其他面板（互斥逻辑）
                    if (_viewModel.IsSearchPanelVisible)
                    {
                        await CloseSearchPanel();
                    }
                    if (_viewModel.IsHelpPanelVisible)
                    {
                        await CloseHelpPanel();
                    }

                    // 展开：立即设置为可见，动画会自动开始
                    _viewModel.IsAddPanelVisible = true;
                    addFillArea.Height = 40; // 扩大到40像素以便观察动画
                    addPanel.Opacity = 1.0; // 整个Add面板淡入
                }
                else
                {
                    // 收回：等待动画完成（0.3秒），然后设置为不可见
                    addFillArea.Height = 0; // 填充区折叠到0像素
                    addPanel.Opacity = 0.0; // 整个Add面板淡出
                    await Task.Delay(300); // 等待动画完成（0.3秒）
                    _viewModel.IsAddPanelVisible = false;
                }
            }
        }
    }

    /// <summary>
    /// 搜索按钮点击处理
    /// 重写父类方法以添加互斥逻辑
    /// 当Search面板展开时，会先关闭Add和Help面板
    /// </summary>
    protected new async void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            // 检查其他面板是否打开，如果打开则先关闭它们
            if (_viewModel.IsAddPanelVisible)
            {
                await CloseAddPanel();
            }
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
                    fillArea.Height = 40; // 扩大到40像素以便观察动画
                    searchPanel.Opacity = 1.0; // 整个搜索面板淡入
                }
                else
                {
                    // 收回：等待动画完成（0.3秒），然后设置为不可见
                    fillArea.Height = 0; // 填充区折叠到0像素
                    searchPanel.Opacity = 0.0; // 整个搜索面板淡出
                    await Task.Delay(300); // 等待动画完成（0.3秒）
                    _viewModel.IsSearchPanelVisible = false;
                }
            }
        }
    }

    /// <summary>
    /// Help按钮点击处理
    /// 重写父类方法以添加互斥逻辑
    /// 当Help面板展开时，会先关闭Search和Add面板
    /// </summary>
    protected new async void HelpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            // 检查其他面板是否打开，如果打开则先关闭它们
            if (_viewModel.IsSearchPanelVisible)
            {
                await CloseSearchPanel();
            }
            if (_viewModel.IsAddPanelVisible)
            {
                await CloseAddPanel();
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
                    helpFillArea.Height = 40; // 扩大到40像素以便观察动画
                    helpContent.Height = 120; // 展开Help内容区域
                    helpPanel.Opacity = 1.0; // 整个Help面板淡入
                }
                else
                {
                    // 收起：同时收起所有元素
                    helpContent.Height = 0;
                    helpFillArea.Height = 0;
                    helpPanel.Opacity = 0.0;
                    await Task.Delay(300);
                    _viewModel.IsHelpPanelVisible = false;
                }
            }
        }
    }

    /// <summary>
    /// 关闭Search面板的辅助方法
    /// </summary>
    private async Task CloseSearchPanel()
    {
        var searchPanel = this.FindControl<Border>("SearchPanel");
        var fillArea = this.FindControl<Border>("FillArea");

        if (searchPanel != null && fillArea != null)
        {
            fillArea.Height = 0;
            searchPanel.Opacity = 0.0;
            await Task.Delay(300);
            if (_viewModel != null)
            {
                _viewModel.IsSearchPanelVisible = false;
            }
        }
    }

    /// <summary>
    /// 关闭Add面板的辅助方法
    /// </summary>
    private async Task CloseAddPanel()
    {
        var addPanel = this.FindControl<Border>("AddPanel");
        var addFillArea = this.FindControl<Border>("AddFillArea");

        if (addPanel != null && addFillArea != null)
        {
            addFillArea.Height = 0;
            addPanel.Opacity = 0.0;
            await Task.Delay(300);
            if (_viewModel != null)
            {
                _viewModel.IsAddPanelVisible = false;
            }
        }
    }

    /// <summary>
    /// 关闭Help面板的辅助方法
    /// </summary>
    private async Task CloseHelpPanel()
    {
        var helpPanel = this.FindControl<Border>("HelpPanel");
        var helpFillArea = this.FindControl<Border>("HelpFillArea");
        var helpContent = this.FindControl<Grid>("HelpContent");

        if (helpPanel != null && helpFillArea != null && helpContent != null)
        {
            helpContent.Height = 0;
            helpFillArea.Height = 0;
            helpPanel.Opacity = 0.0;
            await Task.Delay(300);
            if (_viewModel != null)
            {
                _viewModel.IsHelpPanelVisible = false;
            }
        }
    }

    /// <summary>
    /// 添加项按钮点击处理
    /// 从Add面板的输入框获取key，添加到配置容器中
    /// </summary>
    /// <param name="sender">添加项按钮</param>
    /// <param name="e">点击事件参数</param>
    private async void AddItemButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            var addItemInput = this.FindControl<TextBox>("NewKeyTextBox");
            var addValueInput = this.FindControl<TextBox>("NewValueTextBox");

            if (addItemInput != null)
            {
                string newKey = addItemInput.Text?.Trim() ?? "";

                if (!string.IsNullOrEmpty(newKey))
                {
                    // 添加新配置项为 LineEdit 类型，默认值为输入框中的值
                    object? defaultValue = null;
                    if (addValueInput != null && !string.IsNullOrEmpty(addValueInput.Text))
                    {
                        defaultValue = addValueInput.Text;
                    }
                    
                    _viewModel.AddConfig(newKey, ConfigType.LineEdit, defaultValue);

                    // 清空输入框
                    addItemInput.Text = "";
                    if (addValueInput != null)
                        addValueInput.Text = "";

                    // 触发值变化回调（如果有的话）
                    _viewModel.OnValueChanged?.Invoke(newKey, defaultValue);

                    // 推送新添加的配置项到远程（如果启用了同步模式）
                    if (defaultValue != null)
                    {
                        await PushConfigUpdateAsync(newKey, defaultValue.ToString() ?? "");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 重置按钮点击处理
    /// </summary>
    private new void OnResetButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ConfigItem item)
        {
            if (item.DefaultValue != null)
            {
                item.Value = item.DefaultValue;
            }
        }
    }
}
