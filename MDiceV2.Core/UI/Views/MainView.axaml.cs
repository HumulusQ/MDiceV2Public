using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MDiceV2.Models;
using MDiceV2.Core.Mod;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MDiceV2.Core.UI.Views;

/// <summary>
/// MainView - 应用程序的主视图控件
/// 实现可折叠的导航侧边栏和动态内容显示
/// </summary>
public partial class MainView : UserControl
{
    private ListBox? _navigationListBox;
    private Bitmap? _workspaceBackground;
    private static readonly string BackgroundDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDiceV2", "Backgrounds");
    private static readonly string BackgroundPreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDiceV2", "workspace-background.txt");
    private static readonly PaneOpenToIconAlignmentConverter IconAlignmentConverter = new();
    private static readonly PaneOpenToIconMarginConverter IconMarginConverter = new();

    /// <summary>
    /// 菜单项图片资源字典
    /// 键格式：MenuItemName_State (State: Normal, Hover, Selected)
    /// 值格式：avares 资源路径
    /// </summary>
    private static readonly Dictionary<string, string> MenuIconResources = new()
    {
        // Main 菜单项
        { "MainMenuItem_Normal", "/MDiceV2.Core/Assets/Sprite/icon.png" },
        { "MainMenuItem_Hover", "/MDiceV2.Core/Assets/Sprite/icon.png" },
        { "MainMenuItem_Selected", "/MDiceV2.Core/Assets/Sprite/icon.png" },
        
        // Log 菜单项
        { "LogMenuItem_Normal", "/MDiceV2.Core/Assets/Sprite/Log.png" },
        { "LogMenuItem_Hover", "/MDiceV2.Core/Assets/Sprite/Log.png" },
        { "LogMenuItem_Selected", "/MDiceV2.Core/Assets/Sprite/Log.png" },
        
        // Chat 菜单项
        { "ChatMenuItem_Normal", "avares://MDiceV2.Core/Assets/Sprite/Chat.png" },
        { "ChatMenuItem_Hover", "avares://MDiceV2.Core/Assets/Sprite/Chat.png" },
        { "ChatMenuItem_Selected", "avares://MDiceV2.Core/Assets/Sprite/Chat_Select.png" },
        
        // Setting 菜单项
        { "SettingMenuItem_Normal", "/MDiceV2.Core/Assets/Sprite/Setting.png" },
        { "SettingMenuItem_Hover", "/MDiceV2.Core/Assets/Sprite/Setting_Hover.png" },
        { "SettingMenuItem_Selected", "/MDiceV2.Core/Assets/Sprite/Setting_Select.png" },
        
        // Mods 菜单项
        { "ModsMenuItem_Normal", "/MDiceV2.Core/Assets/Sprite/Mod.png" },
        { "ModsMenuItem_Hover", "/MDiceV2.Core/Assets/Sprite/Mod.png" },
        { "ModsMenuItem_Selected", "/MDiceV2.Core/Assets/Sprite/Mod.png" }
    };

    /// <summary>
    /// 递归查找控件内第一个指定类型的子控件
    /// </summary>
    public T? FindDescendantOfType<T>(Avalonia.Controls.Control control) where T : Avalonia.Controls.Control
    {
        if (control is T result)
            return result;

        foreach (var child in control.GetLogicalChildren())
        {
            if (child is Avalonia.Controls.Control childControl)
            {
                var found = FindDescendantOfType<T>(childControl);
                if (found != null)
                    return found;
            }
        }

        return null;
    }

    /// <summary>
    /// <summary>
    /// 从 avares 资源路径加载 Bitmap
    /// 使用 AssetLoader 确保正确处理路径
    /// </summary>
    private Avalonia.Media.Imaging.Bitmap? LoadBitmapFromAvares(string? avarePath)
    {
        if (string.IsNullOrEmpty(avarePath))
            return null;

        try
        {
            // 规范化路径格式
            string normalizedPath = avarePath;
            
            // 如果路径不以 avares:// 开头，添加它
            if (!normalizedPath.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                // 移除开头的 / 或 \
                while (normalizedPath.Length > 0 && (normalizedPath[0] == '/' || normalizedPath[0] == '\\'))
                    normalizedPath = normalizedPath.Substring(1);
                
                // 添加 avares:// 前缀
                normalizedPath = "avares://" + normalizedPath;
            }
            
            // 将反斜杠替换为正斜杠（avares 协议要求使用正斜杠）
            normalizedPath = normalizedPath.Replace("\\", "/");
            
            // 使用 AssetLoader 来加载资源
            var assets = AssetLoader.Open(new Uri(normalizedPath));
            return new Avalonia.Media.Imaging.Bitmap(assets);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load bitmap from {avarePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从菜单项名称和状态获取图标路径
    /// </summary>
    private string GetMenuIconPath(string? menuItemName, IconState state)
    {
        if (string.IsNullOrEmpty(menuItemName))
            return "avares://MDiceV2.Core/Assets/Sprite/icon.png";

        string stateStr = state switch
        {
            IconState.Normal => "Normal",
            IconState.Hover => "Hover",
            IconState.Selected => "Selected",
            _ => "Normal"
        };

        string key = $"{menuItemName}_{stateStr}";
        return MenuIconResources.TryGetValue(key, out var path) 
            ? path 
            : "avares://MDiceV2.Core/Assets/Sprite/icon.png";
    }

    /// <summary>
    /// 图标状态枚举
    /// </summary>
    private enum IconState
    {
        Normal = 0,    // 常态
        Hover = 1,     // 悬停
        Selected = 2   // 选中
    }
    /// <summary>
    /// 构造函数 - 初始化视图
    /// </summary>
    public MainView()
    {
        try
        {
            InitializeComponent();
            LoadWorkspaceBackground();
            DataContext = new ViewModels.MainViewModel();

            // 获取导航列表框
            _navigationListBox = this.FindControl<ListBox>("NavigationListBox");

            // 初始化默认状态
            if (DataContext is ViewModels.MainViewModel viewModel)
            {
                viewModel.IsPaneOpen = false; // 初始状态为折叠
            }

            // 订阅选中项变化事件，用于更新菜单项图标状态
            if (_navigationListBox != null)
            {
                _navigationListBox.SelectionChanged += (s, e) =>
                {
                    UpdateMenuItemIconState();
                    UpdateWorkspaceTitle();
                };
            }

            // 添加五个内置菜单项
            AddBuiltinNavigationItems();
            
            // 加载 Mod 导航项
            AddModNavigationItems();
            NavigationPanelRegistry.Instance.PanelChanged += OnNavigationPanelChanged;

            // The ListBox reports -1 while its items are being added. Select the
            // initial page only after all built-in and mod entries exist so the
            // two-way binding keeps a valid workspace index.
            if (_navigationListBox?.Items?.Count > 0 && _navigationListBox.SelectedIndex < 0)
                _navigationListBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Error($"MainView constructor error: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 添加五个内置菜单项（Main, Log, Chat, Setting, Mods）
    /// 从 MenuIconResources 字典中获取图标路径，避免重复声明
    /// </summary>
    private void AddBuiltinNavigationItems()
    {
        if (_navigationListBox == null)
        {
            Log.Warn("AddBuiltinNavigationItems: NavigationListBox not found");
            return;
        }

        try
        {
            var builtinItems = new[]
            {
                ("Main", "MainMenuItem"),
                ("Log", "LogMenuItem"),
                ("Chat", "ChatMenuItem"),
                ("Setting", "SettingMenuItem"),
                ("Mods", "ModsMenuItem")
            };

            foreach (var (displayName, itemName) in builtinItems)
            {
                try
                {
                    // 从 MenuIconResources 获取 Normal 状态下的图标路径
                    string normalStateKey = $"{itemName}_Normal";
                    if (MenuIconResources.TryGetValue(normalStateKey, out var iconPath))
                    {
                        var item = CreateNavigationItem(displayName, iconPath, itemName, isBuiltin: true);
                        _navigationListBox.Items?.Add(item);
                        Log.InfoFormat($"AddBuiltinNavigationItems: Added '{displayName}' menu item");
                    }
                    else
                    {
                        Log.Warn($"AddBuiltinNavigationItems: Icon path not found for '{normalStateKey}'");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"AddBuiltinNavigationItems: Failed to add '{displayName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"AddBuiltinNavigationItems: Error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 创建导航菜单项（内置项或 Mod 项）
    /// </summary>
    private ListBoxItem CreateNavigationItem(string displayName, string? iconPath, string itemName, bool isBuiltin = false)
    {
        try
        {
            var item = new ListBoxItem
            {
                Height = 52,
                Margin = new Avalonia.Thickness(0),
                CornerRadius = new Avalonia.CornerRadius(2),
                BorderThickness = new Avalonia.Thickness(0),
                Name = itemName,
                Background = Brushes.Transparent
            };

            // Keep the icon and caption as one centered unit in both rail states.
            var contentPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 12
            };
            
            // 规范化图标路径
            string normalizedIconPath = string.IsNullOrEmpty(iconPath) 
                ? "avares://MDiceV2.Core/Assets/Sprite/icon.png"
                : iconPath;
            
            // 添加固定左侧图标
            var iconBitmap = LoadBitmapFromAvares(normalizedIconPath);
            
            var icon = new Avalonia.Controls.Image
            {
                Source = iconBitmap,
                Width = 24,
                Height = 24,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Name = $"{itemName}Icon"
            };
            
            // 添加右侧文字（展开时显示）
            var text = new TextBlock
            {
                Text = displayName,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                FontSize = 11,
                LetterSpacing = 1,
                Margin = new Avalonia.Thickness(0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            
            // 绑定文字的显示/隐藏到 IsPaneOpen
            text.Bind(TextBlock.IsVisibleProperty, new Avalonia.Data.Binding 
            { 
                Path = "IsPaneOpen"
            });
            
            contentPanel.Children.Add(icon);
            contentPanel.Children.Add(text);
            
            item.Content = contentPanel;
            
            // 添加事件处理器
            item.PointerEntered += MenuItem_PointerEntered;
            item.PointerExited += MenuItem_PointerExited;

            return item;
        }
        catch (Exception ex)
        {
            Log.Error($"CreateNavigationItem error for '{itemName}': {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 从注册表中加载 Mod 导航项并添加到列表中
    /// 使用淡黄色背景区分 Mod 面板
    /// </summary>
    private void AddModNavigationItems()
    {
        if (_navigationListBox == null)
        {
            Log.Warn("AddModNavigationItems: NavigationListBox not found");
            Console.WriteLine("[MainView] >>> ERROR: NavigationListBox not found");
            return;
        }

        try
        {
            Console.WriteLine("[MainView] >>> ========== AddModNavigationItems START ==========");
            var registry = NavigationPanelRegistry.Instance;
            Console.WriteLine($"[MainView] >>> Registry obtained: {(registry != null ? "SUCCESS" : "NULL")}");
            
            var registeredPanels = registry.GetRegisteredPanels();
            
            Log.InfoFormat($"AddModNavigationItems: Found {registeredPanels.Count} registered panels");
            Console.WriteLine($"[MainView] >>> GetRegisteredPanels() returned {registeredPanels.Count} panels");

            foreach (var panelProvider in registeredPanels)
            {
                Console.WriteLine($"[MainView] >>> Adding navigation item: {panelProvider.PanelName} (ID: {panelProvider.PanelId})");
                Log.InfoFormat($"AddModNavigationItems: Adding panel '{panelProvider.PanelName}' (ID: {panelProvider.PanelId})");
                var item = CreateModNavigationItem(panelProvider.PanelName, panelProvider.IconSource, panelProvider.PanelId);
                _navigationListBox.Items?.Add(item);
                Console.WriteLine($"[MainView] >>> ✓ Navigation item added: {panelProvider.PanelName}");
                Log.InfoFormat($"AddModNavigationItems: Successfully added panel '{panelProvider.PanelName}'");
            }
            
            Console.WriteLine($"[MainView] >>> Total items in ListBox: {_navigationListBox.Items?.Count}");
            Console.WriteLine("[MainView] >>> ========== AddModNavigationItems END ==========");
            Log.InfoFormat($"AddModNavigationItems: Total items in ListBox after adding: {_navigationListBox.Items?.Count}");
        }
        catch (Exception ex)
        {
            // Mod 导航项加载失败不应该影响主程序显示
            Console.WriteLine($"[MainView] >>> EXCEPTION in AddModNavigationItems: {ex.Message}");
            Console.WriteLine($"[MainView] >>> StackTrace: {ex.StackTrace}");
            Log.Error($"AddModNavigationItems: Error loading mod items: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 创建 Mod 导航项（使用淡黄色背景）
    /// </summary>
    private ListBoxItem CreateModNavigationItem(string displayName, string? iconPath, string panelId)
    {
        // 使用通用方法创建，传入 isBuiltin=false 以应用 Mod 的淡黄色背景
        var item = CreateNavigationItem(displayName, iconPath, displayName.Replace(" ", ""), isBuiltin: false);
        item.Tag = panelId;
        return item;
    }

    private void OnNavigationPanelChanged(object? sender, NavigationPanelChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_navigationListBox == null)
                return;

            // Keep the user on the Mod Manager while its enabled-panel list is
            // being refreshed; index 4 is the built-in Mods page.
            _navigationListBox.SelectedIndex = 4;
            if (DataContext is ViewModels.MainViewModel viewModel)
            {
                viewModel.SelectedIndex = 4;
                viewModel.RefreshModPanelFactories();
            }

            if (e.IsRegistered)
                AddLiveModNavigationItem(e.Provider);
            else
                await RemoveLiveModNavigationItemAsync(e.Provider.PanelId);
        }, DispatcherPriority.Normal);
    }

    private void AddLiveModNavigationItem(MDiceV2.Interfaces.INavigationPanelProvider provider)
    {
        var items = _navigationListBox?.Items;
        if (items == null || items.OfType<ListBoxItem>().Any(item => string.Equals(item.Tag as string, provider.PanelId, StringComparison.Ordinal)))
            return;

        var item = CreateModNavigationItem(provider.PanelName, provider.IconSource, provider.PanelId);
        item.Opacity = 0;
        item.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(180)
            }
        };

        var panelOrder = NavigationPanelRegistry.Instance.GetRegisteredPanels().ToList();
        var position = panelOrder.FindIndex(panel => string.Equals(panel.PanelId, provider.PanelId, StringComparison.Ordinal));
        items.Insert(Math.Max(5, 5 + position), item);
        Dispatcher.UIThread.Post(() => item.Opacity = 1, DispatcherPriority.Render);
    }

    private async Task RemoveLiveModNavigationItemAsync(string panelId)
    {
        var items = _navigationListBox?.Items;
        var item = items?.OfType<ListBoxItem>().FirstOrDefault(candidate => string.Equals(candidate.Tag as string, panelId, StringComparison.Ordinal));
        if (item == null || items == null)
            return;

        item.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(180)
            }
        };
        item.IsHitTestVisible = false;
        item.Opacity = 0;
        await Task.Delay(180);
        items.Remove(item);
    }

    /// <summary>
    /// 面板展开/折叠按钮点击事件处理
    /// 切换导航面板的展开/折叠状态
    /// </summary>
    /// <param name="sender">事件发送者（按钮）</param>
    /// <param name="e">路由事件参数</param>
    /// <summary>
    /// 图标状态定义 - 统一管理所有状态的参数
    /// </summary>
    private class IconStateConfig
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int MarginLeft { get; set; }
        public IconState State { get; set; }

        public IconStateConfig(int width, int height, int marginLeft, IconState state)
        {
            Width = width;
            Height = height;
            MarginLeft = marginLeft;
            State = state;
        }
    }

    /// <summary>
    /// 获取指定状态的配置
    /// </summary>
    private IconStateConfig GetIconStateConfig(IconState state)
    {
        return state switch
        {
            IconState.Normal => new IconStateConfig(24, 24, 12, IconState.Normal),
            IconState.Hover => new IconStateConfig(26, 26, 11, IconState.Hover),
            IconState.Selected => new IconStateConfig(26, 26, 11, IconState.Selected),
            _ => new IconStateConfig(24, 24, 12, IconState.Normal)
        };
    }

    private void LoadWorkspaceBackground()
    {
        try
        {
            var savedPath = File.Exists(BackgroundPreferencePath) ? File.ReadAllText(BackgroundPreferencePath).Trim() : null;
            if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
            {
                // Migrate preferences from earlier releases that only referenced
                // the user's source file instead of retaining an application copy.
                var managedPath = StoreUserBackground(savedPath);
                SetWorkspaceBackground(managedPath);
            }
            else
            {
                SetWorkspaceBackground(null);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Unable to load workspace background: {ex.Message}");
            SetWorkspaceBackground(null);
        }
    }

    private void SetWorkspaceBackground(string? filePath)
    {
        var backgroundImage = this.FindControl<Avalonia.Controls.Image>("WorkspaceBackgroundImage");
        if (backgroundImage == null)
            return;

        try
        {
            _workspaceBackground?.Dispose();
            _workspaceBackground = string.IsNullOrWhiteSpace(filePath)
                ? new Bitmap(AssetLoader.Open(new Uri("avares://MDiceV2.Core/Assets/Sprite/Background.png")))
                : new Bitmap(filePath);
            backgroundImage.Source = _workspaceBackground;
        }
        catch (Exception ex)
        {
            Log.Warn($"Unable to apply workspace background: {ex.Message}");
        }
    }

    private static string StoreUserBackground(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp" and not ".bmp")
            throw new InvalidOperationException("Unsupported background image format.");

        Directory.CreateDirectory(BackgroundDataDirectory);
        var destinationPath = Path.Combine(BackgroundDataDirectory, "workspace-background" + extension);
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, destinationPath, overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(BackgroundPreferencePath)!);
        File.WriteAllText(BackgroundPreferencePath, destinationPath);
        return destinationPath;
    }

    private async void ChooseBackgroundButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose workspace background",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } }
            }
        });

        var selectedFile = files.FirstOrDefault();
        if (selectedFile == null)
            return;

        try
        {
            var managedPath = StoreUserBackground(selectedFile.Path.LocalPath);
            SetWorkspaceBackground(managedPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Unable to save workspace background: {ex.Message}");
        }
    }

    private void UpdateWorkspaceTitle()
    {
        var title = this.FindControl<TextBlock>("WorkspaceTitle");
        if (title != null && _navigationListBox?.SelectedItem is ListBoxItem item)
        {
            var label = item.Content switch
            {
                Grid grid => grid.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
                StackPanel panel => panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
                _ => null
            };
            title.Text = label?.ToUpperInvariant() ?? "WORKSPACE";
        }
    }

    /// <summary>
    /// 应用图标状态配置
    /// </summary>
    private void ApplyIconState(Avalonia.Controls.Image? image, string? itemName, IconStateConfig config)
    {
        if (image == null || itemName == null)
            return;

        image.Width = config.Width;
        image.Height = config.Height;
        
        string iconPath = GetMenuIconPath(itemName, config.State);
        var bitmap = LoadBitmapFromAvares(iconPath);
        if (bitmap != null)
            image.Source = bitmap;
    }

    private sealed class PaneOpenToIconAlignmentConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object Convert(object? value, global::System.Type targetType, object? parameter, global::System.Globalization.CultureInfo culture)
            => value is true ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Center;

        public object ConvertBack(object? value, global::System.Type targetType, object? parameter, global::System.Globalization.CultureInfo culture)
            => throw new global::System.NotSupportedException();
    }

    private sealed class PaneOpenToIconMarginConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object Convert(object? value, global::System.Type targetType, object? parameter, global::System.Globalization.CultureInfo culture)
            => value is true ? new Avalonia.Thickness(12, 0, 0, 0) : new Avalonia.Thickness(0);

        public object ConvertBack(object? value, global::System.Type targetType, object? parameter, global::System.Globalization.CultureInfo culture)
            => throw new global::System.NotSupportedException();
    }

    private void PanelExpandButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 切换面板展开状态
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.IsPaneOpen = !viewModel.IsPaneOpen;
        }
    }

    /// <summary>
    /// 鼠标指针进入导航区域事件处理（保留但不再自动展开）
    /// </summary>
    /// <param name="sender">事件发送者</param>
    /// <param name="e">指针事件参数</param>
    private void NavigationListBox_PointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        // 现在面板展开完全依赖于按钮点击，不再自动展开
    }

    /// <summary>
    /// 鼠标指针离开导航区域事件处理（保留但不再自动折叠）
    /// </summary>
    /// <param name="sender">事件发送者</param>
    /// <param name="e">指针事件参数</param>
    private void NavigationListBox_PointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        // 现在面板折叠完全依赖于按钮点击，不再自动折叠
    }

    /// <summary>
    /// 菜单项鼠标进入事件处理 - 改变图标大小和资源
    /// 选中状态优先级最高：即使鼠标悬停也保持选中状态
    /// 未选中时悬停：使用Hover状态配置
    /// </summary>
    private void MenuItem_PointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            var image = FindDescendantOfType<Avalonia.Controls.Image>(item);
            if (image != null)
            {
                // 检查是否被选中 - 选中状态优先级最高
                bool isSelected = _navigationListBox?.SelectedItem == item;
                
                if (!isSelected)
                {
                    // 未选中状态：使用Hover状态配置
                    var config = GetIconStateConfig(IconState.Hover);
                    ApplyIconState(image, item.Name, config);
                }
                // 如果已选中，不改变状态
            }
        }
    }

    /// <summary>
    /// 菜单项鼠标离开事件处理 - 恢复图标大小和资源
    /// 根据选中状态应用对应的状态配置
    /// </summary>
    private void MenuItem_PointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            var image = FindDescendantOfType<Avalonia.Controls.Image>(item);
            if (image != null)
            {
                // 检查是否被选中
                bool isSelected = _navigationListBox?.SelectedItem == item;
                
                // 根据选中状态应用对应的配置
                var targetState = isSelected ? IconState.Selected : IconState.Normal;
                var config = GetIconStateConfig(targetState);
                ApplyIconState(image, item.Name, config);
            }
        }
    }

    /// <summary>
    /// 导航列表框项目选中变化事件处理
    /// 当选中项改变时更新所有图标的大小和资源
    /// 使用统一的状态配置管理器
    /// </summary>
    private void UpdateMenuItemIconState()
    {
        if (_navigationListBox?.SelectedItem is ListBoxItem selectedItem)
        {
            // 更新所有菜单项的图标状态
            foreach (var item in _navigationListBox.Items?.Cast<ListBoxItem>() ?? Enumerable.Empty<ListBoxItem>())
            {
                var image = FindDescendantOfType<Avalonia.Controls.Image>(item);
                if (image != null)
                {
                    // 确定目标状态：选中或未选中
                    var targetState = (item == selectedItem) ? IconState.Selected : IconState.Normal;
                    var config = GetIconStateConfig(targetState);
                    ApplyIconState(image, item.Name, config);
                }
            }
        }
    }
}
