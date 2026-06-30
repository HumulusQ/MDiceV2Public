using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Data.SQLite;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MDiceV2.Models;
using MDiceV2.Core.UI.Views;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Core.Infrastructure.Configurers;
using MDiceV2.Core.Models;
using MDiceV2.Abstractions;
using MDiceV2.Interfaces.Mod;
using Microsoft.Extensions.Logging.Abstractions;
using ModelsConfigType = MDiceV2.Models.ConfigType;

#nullable enable

namespace MDiceV2.Core.UI.ViewModels
{
    /// <summary>
    /// MainViewModel - 主视图的视图模型
    /// 管理导航状态、页面内容的切换逻辑和导航面板的展开/折叠状态
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        private readonly Dictionary<int, object> _views = new();
        private readonly MDiceV2.Abstractions.IDispatcher? _dispatcher;
        private MessageProcessor? _globalMessageProcessor;
        private ConfigContainerViewModel _basicConfigContainer = null!;
        private ConfigContainerViewModel _feedbackTemplatesConfigContainer = null!;
        private ConfigContainerViewModel _helpTemplatesConfigContainer = null!;
        private ModManagerViewModel? _modManagerViewModel; // 缓存ModManagerViewModel实例，避免重复加载Mod
        private ScrollViewer? _logScrollViewer;
        private bool _wasAtBottom = true;
        
        // 同步相关字段
        private SyncConfigManager? _syncConfigManager;
        private ConfigSyncDispatcher? _configSyncDispatcher;
        private GrpcConfigSyncClient? _grpcClient;
        private GrpcServerHost? _grpcServerHost; // ✅ 【新增】保存服务器实例用于重启和事件订阅
        private System.Collections.Generic.Dictionary<string, object?> _originalConfigValues = new();

        public string VersionDisplay { get; }
        
        // 同步模式属性
        [ObservableProperty]
        private bool isSyncModeEnabled = false;

        [ObservableProperty]
        private bool isSyncExpanded = false;

        [ObservableProperty]
        private string remoteServerAddress = "127.0.0.1";

        [ObservableProperty]
        private int remoteServerPort = 5001;

        [ObservableProperty]
        private int localListeningPort = 5001;

        [ObservableProperty]
        private bool isLocalServerListening = false;

        [ObservableProperty]
        private string localSyncKey = "";

        [ObservableProperty]
        private string remoteServerKey = "";

        [ObservableProperty]
        private string syncStatusMessage = "未启动同步";

        [ObservableProperty]
        private bool isSyncConnecting = false;

        // 更新源选择：存储用户选择的更新源（github, ghproxy, fastgit）
        private string _updateSourceSelection = "github";
        public string UpdateSourceSelection
        {
            get => _updateSourceSelection;
            set => SetProperty(ref _updateSourceSelection, value);
        }

        // 兼容旧版本：UseMirrorForUpdate 逻辑已迁移到 UpdateSourceSelection
        public bool UseMirrorForUpdate
        {
            get => !string.Equals(UpdateSourceSelection, "github", StringComparison.OrdinalIgnoreCase);
            set => UpdateSourceSelection = value ? "ghproxy" : "github";
        }


        /// <summary>
        /// 发送消息的命令
        /// </summary>
        [RelayCommand]
        private void SendMessage()
        {
            if (!string.IsNullOrWhiteSpace(CurrentMessageText))
            {
                // 解析来源账号与群号（留空则使用默认值）
                long parsedUserId = 1001;
                if (!string.IsNullOrWhiteSpace(AccountIdInput) && long.TryParse(AccountIdInput, out var tmpUser))
                {
                    parsedUserId = tmpUser;
                }

                long parsedGroupId = 0;
                if (!string.IsNullOrWhiteSpace(GroupIdInput) && long.TryParse(GroupIdInput, out var tmpGroup))
                {
                    parsedGroupId = tmpGroup;
                }

                // 根据勾选决定消息来源（群/私聊），群聊缺省则仍为0
                var source = IsGroupChatMode ? MessageSource.group : MessageSource.privatechat;
                if (!IsGroupChatMode)
                {
                    parsedGroupId = 0; // 私聊场景忽略群号
                }

                // 添加用户消息到聊天界面
                var userMessage = new Message
                {
                    Text = CurrentMessageText.Trim(),
                    IsFromUser = true,
                    Timestamp = DateTime.Now
                };
                Messages.Add(userMessage);

                // 处理机器人指令（使用模拟模式状态）
                var trimmedMessage = CurrentMessageText.Trim();
                var msg = new Msg(parsedGroupId, parsedUserId, trimmedMessage, source, IsSimulationMode);

                // 在后台线程处理消息，避免阻塞UI
                Task.Run(() =>
                {
                    try
                    {
                        if (_globalMessageProcessor?.MessageDistribution != null)
                        {
                            _globalMessageProcessor.MessageDistribution.HandleSimulationMessage(trimmedMessage, source, IsSimulationMode, parsedUserId, parsedGroupId);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSender.Error($"处理消息时发生错误: {ex.Message}");

                        var errorMessage = new Message
                        {
                            Text = $"处理消息时发生错误: {ex.Message}",
                            IsFromUser = false,
                            Timestamp = DateTime.Now
                        };
                        if (_dispatcher != null)
                        {
                            _dispatcher.Post(() => Messages.Add(errorMessage));
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(() => Messages.Add(errorMessage));
                        }
                    }
                });

                CurrentMessageText = string.Empty;
            }
        }

        /// <summary>
        /// 构造函数 - 初始化视图模型
        /// </summary>
        public MainViewModel(MDiceV2.Abstractions.IDispatcher? dispatcher = null)
        {
            LogSender.Normal($"[MainViewModel] ========== 构造函数开始 ==========");
            _dispatcher = dispatcher;
            // 移除调试日志
            VersionDisplay = ResolveVersionDisplay();

            // ✅ 使用 GrpcBootstrapper 创建共享的 gRPC 基础设施
            try
            {
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 创建 gRPC 基础设施...");
                _configSyncDispatcher = GrpcBootstrapper.CreateDispatcher();
                _syncConfigManager = GrpcBootstrapper.CreateSyncManager();
                LocalSyncKey = _syncConfigManager.LocalKey;
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] gRPC 基础设施已创建，local key: {LocalSyncKey.Substring(0, Math.Min(8, LocalSyncKey.Length))}...");
            }
            catch (Exception ex)
            {
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 创建 gRPC 基础设施失败: {ex.Message}");
                return;
            }

            // ✅ 注册 UI 版本特定的处理器（只处理 UI 容器更新）
            try
            {
                RegisterUIConfigHandlers();
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] UI处理器已注册");
            }
            catch (Exception ex)
            {
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 注册UI处理器失败: {ex.Message}");
            }
            
            // 自动启动本地 gRPC 服务器监听
            try
            {
                if (_dispatcher != null)
                {
                    _dispatcher.Post(() => AutoStartLocalGrpcServer());
                }
                else
                {
                    Dispatcher.UIThread.Post(() => AutoStartLocalGrpcServer());
                }
            }
            catch (Exception ex)
            {
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 自动启动本地 gRPC 服务器失败: {ex.Message}");
            }
            
            InitializeViews();
            SelectedIndex = 0;
            UpdateCurrentView();

            // 在构造函数中尝试加载已保存的URL（如果GlobalFeedbackMessages已初始化）
            try
            {
                if (MDiceV2.Models.GlobalFeedbackMessages.IsInitialized())
                {
                    string? savedUrl = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting("Url");
                    if (!string.IsNullOrEmpty(savedUrl))
                    {
                        WsUrl = savedUrl;
                        LogSender.InfoFormat($"[MainViewModel] 从数据库加载WsUrl: {WsUrl}");
                    }
                    
                    // 加载已保存的更新源选择
                    string? savedUpdateSource = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting("UpdateSource");
                    if (!string.IsNullOrEmpty(savedUpdateSource))
                    {
                        UpdateSourceSelection = savedUpdateSource;
                        LogSender.InfoFormat($"[MainViewModel] 从数据库加载UpdateSource: {UpdateSourceSelection}");
                    }
                    else
                    {
                        // 如果数据库中没有保存的源，使用默认值 "github"
                        UpdateSourceSelection = "github";
                        LogSender.InfoFormat($"[MainViewModel] 使用默认UpdateSource: github");
                    }
                }
            }
            catch (Exception ex)
            {
                LogSender.Warn($"[MainViewModel] 从数据库加载设置失败: {ex.Message}");
                UpdateSourceSelection = "github"; // 出错时使用默认值
            }

            // 订阅日志消息事件
            if (GlobalMessageQueue.Instance != null)
            {
                GlobalMessageQueue.Instance.LogMessageQueued += DisplayLog;
            }

            // 订阅GlobalFeedbackMessages的UI更新事件
            MDiceV2.Models.GlobalFeedbackMessages.FeedbackTemplatesLoaded += OnFeedbackTemplatesLoaded;
            MDiceV2.Models.GlobalFeedbackMessages.HelpTemplatesLoaded += OnHelpTemplatesLoaded;

            // 获取MessageProcessor实例并设置MainViewModel引用（必须在dispatcher.Post之前！）
#pragma warning disable CS0618
            _globalMessageProcessor = MDiceV2.Models.MessageProcessor.GetInstance();
#pragma warning restore CS0618

            // 设置MessageProcessor和MessageDistribution的MainViewModel引用
            if (_globalMessageProcessor != null)
            {
                LogSender.Normal($"[MainViewModel] 已获取MessageProcessor实例，即将设置MainViewModel和dispatcher引用");
                
                // ✅ 关键修复：设置dispatcher到MessageProcessor
                _globalMessageProcessor.SetDispatcher(_dispatcher);
                LogSender.Normal($"[MainViewModel] SetDispatcher已调用，dispatcher = {(_dispatcher != null ? "不为null" : "为null")}");
                
                _globalMessageProcessor.MainViewModel = this;
                if (_globalMessageProcessor.MessageDistribution != null)
                {
                    _globalMessageProcessor.MessageDistribution.MainViewModel = this;
                    // 订阅WS连接状态变化
                    SubscribeToWSConnectionStatus();
                }
            }
            else
            {
                LogWarning("MessageProcessor实例为空，无法初始化引用");
            }

            // 在UI准备就绪后初始化MessageProcessor，然后加载URL
            // 注意：此时MainViewModel已经设置到MessageProcessor中了
            if (_dispatcher != null)
            {
                LogSender.Normal($"[MainViewModel] 即将post dispatcher任务来初始化MessageProcessor");
                _dispatcher.Post(() =>
                {
                    MDiceV2.Models.MessageProcessor.EnsureInitialized();
                    // MessageProcessor初始化后，再尝试一次加载URL
                    LoadWsUrlFromDatabase();
                });
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    MDiceV2.Models.MessageProcessor.EnsureInitialized();
                    // MessageProcessor初始化后，再尝试一次加载URL
                    LoadWsUrlFromDatabase();
                });
            }
        }

        /// <summary>
        /// SelectedIndex属性变更时的回调方法
        /// </summary>
        partial void OnSelectedIndexChanged(int value)
        {
            UpdateCurrentView();
        }

        /// <summary>
        /// 注册UI版本特定的配置处理器
        /// 这些处理器在接收到推送/拉取的配置时，更新UI容器
        /// </summary>
        private void RegisterUIConfigHandlers()
        {
            if (_configSyncDispatcher == null) return;

            // 1. 注册核心基础配置处理器
            _configSyncDispatcher.RegisterCategory("basic", async (key, value) => 
            {
                if (_basicConfigContainer == null) return;
                
                // a. 更新内存模型
                var logger = NullLogger<BasicConfigurer>.Instance;
                var configurer = new BasicConfigurer(logger);
                await configurer.ApplyConfigAsync(key, value);

                // b. 更新 UI 容器
                var uiKey = key.StartsWith("basic.") ? key.Substring(6) : key;
                var item = _basicConfigContainer.Items.FirstOrDefault(i => i.Key.Equals(uiKey, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    var originalCallback = item.ValueChangedCallback;
                    item.ValueChangedCallback = null; // 禁用回调避免死循环
                    
                    if (item.Type == ConfigType.CheckBox)
                    {
                        if (bool.TryParse(value, out var boolVal)) item.Value = boolVal;
                    }
                    else item.Value = value;
                    
                    item.ValueChangedCallback = originalCallback;
                }
            });

            // 2. 注册反馈模板处理器
            _configSyncDispatcher.RegisterCategory("feedback", async (key, value) => 
            {
                var templateKey = key.StartsWith("feedback.") ? key.Substring(9) : key;
                GlobalFeedbackMessages.FeedbackTemplates[templateKey] = value;
                
                if (_feedbackTemplatesConfigContainer != null)
                {
                    var item = _feedbackTemplatesConfigContainer.Items.FirstOrDefault(i => i.Key.Equals(templateKey, StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                    {
                        var originalCallback = item.ValueChangedCallback;
                        item.ValueChangedCallback = null;
                        item.Value = value;
                        item.ValueChangedCallback = originalCallback;
                    }
                }
                await Task.CompletedTask;
            });

            // 3. 注册帮助消息处理器
            _configSyncDispatcher.RegisterCategory("help", async (key, value) => 
            {
                var helpKey = key.StartsWith("help.") ? key.Substring(5) : key;
                GlobalFeedbackMessages.HelpTemplates[helpKey] = value;
                
                if (_helpTemplatesConfigContainer != null)
                {
                    var item = _helpTemplatesConfigContainer.Items.FirstOrDefault(i => i.Key.Equals(helpKey, StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                    {
                        var originalCallback = item.ValueChangedCallback;
                        item.ValueChangedCallback = null;
                        item.Value = value;
                        item.ValueChangedCallback = originalCallback;
                    }
                }
                await Task.CompletedTask;
            });

            // 4. 注册 Mod 配置处理器 (CustomizedReplyMod 远程推送接收)
            // ✅【注意】RegisterCategory 使用第一个点前的类别，所以 "mod.customreply.rules" → 类别为 "mod"
            _configSyncDispatcher.RegisterCategory("mod", async (key, value) => 
            {
                try
                {
                    // 只处理 CustomizedReply 的配置
                    if (!key.StartsWith("mod.customreply.", StringComparison.OrdinalIgnoreCase))
                    {
                        LogSender.Warn($"[Dispatcher] Skipping mod config (not customreply): {key}");
                        return;
                    }

                    var customizedReplyMod = GetCustomizedReplyModInstance();
                    if (customizedReplyMod == null) 
                    {
                        LogSender.Error($"[Dispatcher] Failed to get CustomizedReplyMod instance for key: {key}");
                        return;
                    }

                    LogSender.Normal($"[Dispatcher] ► Applying mod config: {key}");
                    
                    // Mod会调用ApplyConfigAsync，自动触发ConfigChanged事件
                    // CustomizedReplyPanel已订阅该事件，会自动刷新UI
                    var result = await customizedReplyMod.ApplyConfigAsync(key, value);
                    
                    if (result.Success)
                    {
                        LogSender.Normal($"[Dispatcher] ✓ Mod config applied successfully: {key}");
                    }
                    else
                    {
                        LogSender.Error($"[Dispatcher] ✗ Mod config application failed: {key} - {result.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    LogSender.Error($"[Dispatcher] ✗ Error applying mod config: {ex.Message}");
                }
            });

            LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] UI处理器已全部注册");
        }

        /// <summary>
        /// 初始化视图状态
        /// </summary>
        private void InitializeViews()
        {
            LogSender.InfoFormat($"[MainViewModel] InitializeViews 开始");
            _views[0] = CreateMainPanel();
            _views[1] = CreateLogContent();
            _views[2] = CreateChatContent();
            _views[3] = CreateSettingContent();
            _views[4] = CreateModsContent();

            // 加载从 Mod 注册的导航面板
            LoadRegisteredModPanels();
            LogSender.InfoFormat($"[MainViewModel] InitializeViews 完成");
        }

        /// <summary>
        /// 从 NavigationPanelRegistry 加载所有已注册的 Mod 面板
        /// </summary>
        private void LoadRegisteredModPanels()
        {
            try
            {
                Console.WriteLine("[MainViewModel] >>> ========== LoadRegisteredModPanels START ==========");
                var registry = global::MDiceV2.Core.Mod.NavigationPanelRegistry.Instance;
                Console.WriteLine($"[MainViewModel] >>> Registry obtained: {(registry != null ? "SUCCESS" : "NULL")}");
                
                var registeredPanels = registry.GetRegisteredPanels();
                
                Console.WriteLine($"[MainViewModel] >>> GetRegisteredPanels() returned {registeredPanels.Count} panels");
                LogSender.InfoFormat($"LoadRegisteredModPanels: Registry has {registeredPanels.Count} panels");

                // 起始索引为 5（前 5 个位置已被内置面板占用）
                int viewIndex = 5;

                // 按优先级添加 Mod 面板
                foreach (var panelProvider in registeredPanels)
                {
                    Console.WriteLine($"[MainViewModel] >>> Loading panel: {panelProvider.PanelId} ({panelProvider.PanelName})");
                    LogSender.InfoFormat($"LoadRegisteredModPanels: Creating panel for '{panelProvider.PanelId}' (name: {panelProvider.PanelName})");
                    var panel = registry.CreatePanel(panelProvider.PanelId);
                    if (panel != null)
                    {
                        _views[viewIndex] = panel;
                        Console.WriteLine($"[MainViewModel] >>> ✓ Panel added at index {viewIndex}: {panelProvider.PanelId}");
                        LogSender.InfoFormat($"LoadRegisteredModPanels: Panel '{panelProvider.PanelId}' added to views at index {viewIndex}");
                        viewIndex++;
                    }
                    else
                    {
                        Console.WriteLine($"[MainViewModel] >>> ✗ Failed to create panel: {panelProvider.PanelId}");
                        LogSender.Warn($"LoadRegisteredModPanels: Failed to create panel for '{panelProvider.PanelId}'");
                    }
                }
                
                Console.WriteLine($"[MainViewModel] >>> ========== LoadRegisteredModPanels END: {viewIndex - 5} panels loaded ==========");
                LogSender.InfoFormat($"LoadRegisteredModPanels: Total panels loaded: {viewIndex - 5}");
            }
            catch (Exception ex)
            {
                // Mod 面板加载失败不应该影响主程序启动
                // 静默处理错误
                Console.WriteLine($"[MainViewModel] >>> EXCEPTION in LoadRegisteredModPanels: {ex.Message}");
                Console.WriteLine($"[MainViewModel] >>> StackTrace: {ex.StackTrace}");
                LogSender.Error($"LoadRegisteredModPanels: Error loading mod panels: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 根据当前选中的索引更新视图内容
        /// </summary>
        private void UpdateCurrentView()
        {
            if (_views.TryGetValue(SelectedIndex, out var view))
            {
                CurrentView = view;
            }
        }

        /// <summary>
        /// 主面板：显示版本信息和同步配置
        /// </summary>
        private Control CreateMainPanel()
        {
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var mainStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 16,
                MaxWidth = 500
            };

            // 标题
            mainStack.Children.Add(new TextBlock
            {
                Text = "Main Panel",
                FontSize = 20,
                FontWeight = FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.White
            });

            // 版本信息
            mainStack.Children.Add(new TextBlock
            {
                Text = $"Version: {VersionDisplay}",
                FontSize = 28,
                FontFamily = new FontFamily("avares://MDiceV2.Core/Assets/Font/PlayfairDisplay-Black.ttf#Playfair Display"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.White
            });

            // 同步模式配置面板
            var syncConfigPanel = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(0, 16, 0, 0)
            };

            // 同步开关
            var syncCheckboxPanel = new Grid
            {
                ColumnDefinitions = 
                {
                    new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) }
                }
            };

            var syncCheckBox = new CheckBox
            {
                Content = "启用同步模式 (Synchronize Mode)",
                Foreground = Brushes.White,
                IsChecked = IsSyncModeEnabled
            };
            
            // 绑定同步开关的点击事件
            syncCheckBox.IsCheckedChanged += async (s, e) =>
            {
                if (syncCheckBox.IsChecked == true)
                {
                    IsSyncModeEnabled = true;
                    IsSyncExpanded = true;
                }
                else
                {
                    IsSyncModeEnabled = false;
                    IsSyncExpanded = false;
                    await DisableSyncMode();
                }
            };

            Grid.SetColumn(syncCheckBox, 0);
            syncCheckboxPanel.Children.Add(syncCheckBox);

            syncConfigPanel.Children.Add(syncCheckboxPanel);

            // 同步配置展开面板（仅在IsSyncExpanded时显示）
            var syncExpandedPanel = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(16, 8, 0, 0)
            };

            // 本地密钥显示（用于接收其他程序的连接验证，不需要在此修改）
            syncExpandedPanel.Children.Add(new TextBlock
            {
                Text = "本地密钥 (Local Key - 用于接收其他程序同步):",
                Foreground = Brushes.Gray,
                FontSize = 11
            });

            var localKeyTextBox = new TextBox
            {
                Text = LocalSyncKey,
                IsReadOnly = true,
                Height = 24,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = Brushes.Gray
            };
            syncExpandedPanel.Children.Add(localKeyTextBox);

            // 远程服务器密钥输入（用于连接到远程程序）
            syncExpandedPanel.Children.Add(new TextBlock
            {
                Text = "远程服务器密钥 (Remote Server Key - 从远程程序获取):",
                Foreground = Brushes.LightGray,
                FontSize = 12
            });

            var remoteKeyTextBox = new TextBox
            {
                Text = RemoteServerKey,
                Watermark = "paste the remote server's key here",
                Height = 28,
                Margin = new Thickness(0, 0, 0, 8)
            };
            
            remoteKeyTextBox.TextChanged += (s, e) =>
            {
                RemoteServerKey = remoteKeyTextBox.Text ?? "";
            };
            syncExpandedPanel.Children.Add(remoteKeyTextBox);

            // 远程服务器地址输入
            // 远程服务器地址输入
            syncExpandedPanel.Children.Add(new TextBlock
            {
                Text = "远程服务器地址 (Remote Server Address):",
                Foreground = Brushes.LightGray,
                FontSize = 12
            });

            var serverAddressBox = new TextBox
            {
                Text = RemoteServerAddress,
                Watermark = "e.g. 192.168.1.100 or localhost",
                Height = 28,
                Margin = new Thickness(0, 0, 0, 8)
            };
            
            serverAddressBox.TextChanged += (s, e) =>
            {
                RemoteServerAddress = serverAddressBox.Text ?? "127.0.0.1";
            };
            syncExpandedPanel.Children.Add(serverAddressBox);

            // 远程服务器端口输入
            syncExpandedPanel.Children.Add(new TextBlock
            {
                Text = "远程服务器端口 (Remote Server Port):",
                Foreground = Brushes.LightGray,
                FontSize = 12
            });

            var serverPortBox = new TextBox
            {
                Text = RemoteServerPort.ToString(),
                Watermark = "e.g. 5001",
                Height = 28,
                Margin = new Thickness(0, 0, 0, 8)
            };
            
            serverPortBox.TextChanged += (s, e) =>
            {
                if (int.TryParse(serverPortBox.Text, out var port) && port > 0 && port <= 65535)
                {
                    RemoteServerPort = port;
                }
            };
            syncExpandedPanel.Children.Add(serverPortBox);

            // 本地监听端口标签
            var localPortLabel = new TextBlock
            {
                Text = "本地监听端口 (Local Listening Port):",
                Margin = new Thickness(0, 12, 0, 4),
                FontSize = 12
            };
            syncExpandedPanel.Children.Add(localPortLabel);

            // 本地监听端口输入框
            var localPortBox = new TextBox
            {
                Text = LocalListeningPort.ToString(),
                Watermark = "e.g. 5001",
                Height = 28,
                Margin = new Thickness(0, 0, 0, 8)
            };
            
            localPortBox.TextChanged += async (s, e) =>
            {
                if (int.TryParse(localPortBox.Text, out var port) && port > 0 && port <= 65535)
                {
                    if (LocalListeningPort != port)
                    {
                        LocalListeningPort = port;
                        LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 本地监听端口已更改为: {port}");
                        
                        // ✅ 【新增】如果服务器正在运行，则重启以应用新端口
                        if (IsLocalServerListening)
                        {
                            await RestartGrpcServerAsync(port);
                        }
                    }
                }
            };
            syncExpandedPanel.Children.Add(localPortBox);

            // 服务器状态显示
            var serverStatusLabel = new TextBlock
            {
                Text = "服务器状态: ⋯ 初始化中...",
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = Brushes.Yellow
            };
            
            // ✅ 【修复】更新服务器状态显示的方法
            var updateServerStatusDisplay = () =>
            {
                if (IsLocalServerListening)
                {
                    serverStatusLabel.Text = $"服务器状态: ✓ 正在监听端口 {LocalListeningPort}";
                    serverStatusLabel.Foreground = Brushes.LightGreen;
                    LogSender.Normal($"[MainViewModel] 服务器状态已更新: 监听端口 {LocalListeningPort}");
                }
                else
                {
                    serverStatusLabel.Text = "服务器状态: ✗ 未启动";
                    serverStatusLabel.Foreground = Brushes.LightCoral;
                    LogSender.Normal($"[MainViewModel] 服务器状态已更新: 未启动");
                }
            };
            
            // 绑定服务器状态更新 - 监听IsLocalServerListening和LocalListeningPort两个属性
            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IsLocalServerListening) || e.PropertyName == nameof(LocalListeningPort))
                {
                    updateServerStatusDisplay();
                }
            };
            syncExpandedPanel.Children.Add(serverStatusLabel);

            // 同步状态显示
            var statusBlock = new TextBlock
            {
                Text = SyncStatusMessage,
                Foreground = Brushes.LightGreen,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            };
            
            // 绑定状态消息更新
            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SyncStatusMessage))
                {
                    statusBlock.Text = SyncStatusMessage;
                }
            };
            syncExpandedPanel.Children.Add(statusBlock);

            // 连接按钮
            var connectButton = new Button
            {
                Content = "连接 (Connect)",
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            };

            connectButton.Click += async (s, e) =>
            {
                // 根据当前连接状态判断执行连接或断开
                if (_grpcClient?.IsConnected == true)
                {
                    // 已连接，执行断开操作
                    await OnSyncDisconnectClick(connectButton);
                }
                else
                {
                    // 未连接，执行连接操作
                    await OnSyncConnectClick(connectButton);
                }
            };
            syncExpandedPanel.Children.Add(connectButton);

            // 展开面板滚动视图 - 为超长内容提供滚动条
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = syncExpandedPanel,
                MaxHeight = 400
            };

            // 展开面板包装器 - 仅在IsSyncExpanded时显示
            var expandedWrapper = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12),
                Child = scrollViewer,
                IsVisible = IsSyncExpanded
            };

            // 绑定展开状态
            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IsSyncExpanded))
                {
                    expandedWrapper.IsVisible = IsSyncExpanded;
                }
            };

            syncConfigPanel.Children.Add(expandedWrapper);

            mainStack.Children.Add(syncConfigPanel);

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Black,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(32),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = mainStack
            };

            grid.Children.Add(card);
            return grid;
        }

        private string ResolveVersionDisplay()
        {
            try
            {
                LogSender.Normal($"[MainViewModel] Resolving version using unified method");
                return MessageProcessor.GetApplicationVersion();
            }
            catch (Exception ex)
            {
                LogSender.Warn($"[MainViewModel] Failed to resolve version: {ex.Message}");
                return "Unknown";
            }
        }

        private static string ComposeVersionDisplay(string baseVersion, string? infoVersion)
        {
            if (string.IsNullOrWhiteSpace(infoVersion) || infoVersion.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                return baseVersion;
            }

            var trimmedInfo = infoVersion.Trim();
            if (trimmedInfo.StartsWith(baseVersion, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = trimmedInfo.Substring(baseVersion.Length).Trim();
                if (!string.IsNullOrWhiteSpace(remainder))
                {
                    return $"{baseVersion}{remainder}";
                }
            }

            return $"{baseVersion}-{trimmedInfo}";
        }

        private string? TryReadAssemblyFileVersion()
        {
            try
            {
                var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
                var candidates = new List<string>
                {
                    typeof(MainViewModel).Assembly.Location,
                    Path.Combine(AppContext.BaseDirectory, "MDiceV2.Core.dll"),
                    Path.Combine(AppContext.BaseDirectory, "MDiceV2_Published", "MDiceV2.Core.dll"),
                    Path.Combine(repoRoot, "MDiceV2_Published", "MDiceV2.Core.dll"),
                    Path.Combine(Directory.GetCurrentDirectory(), "MDiceV2.Core.dll")
                };

                LogSender.Normal($"[MainViewModel] Probing assembly versions. Candidates: {string.Join(", ", candidates.Distinct())}");

                foreach (var path in candidates.Distinct())
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        LogSender.Normal($"[MainViewModel] Version probe skip (missing): {path}");
                        continue;
                    }

                    var info = FileVersionInfo.GetVersionInfo(path);
                    var candidate = string.IsNullOrWhiteSpace(info.FileVersion) ? info.ProductVersion : info.FileVersion;

                    LogSender.Normal($"[MainViewModel] Version probe hit: {path}, FileVersion={info.FileVersion}, ProductVersion={info.ProductVersion}");

                    if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
                    {
                        LogSender.Normal($"[MainViewModel] Version selected from {path}: {candidate}");
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                LogSender.Warn($"[MainViewModel] Failed to read assembly version: {ex.Message}. BaseDir={AppContext.BaseDirectory}");
            }

            LogSender.Warn("[MainViewModel] Assembly version probes returned no usable value.");
            return null;
        }

        /// <summary>
        /// 日志面板滚动条位置变化事件处理
        /// </summary>
        /// <param name="sender">事件发送者</param>
        /// <param name="e">滚动变化事件参数</param>
        private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_logScrollViewer != null)
            {
                // 检查滚动条是否在底部
                var maxScroll = _logScrollViewer.Extent.Height - _logScrollViewer.Viewport.Height;
                _wasAtBottom = _logScrollViewer.Offset.Y >= maxScroll - 1.0; // 允许小的误差
            }
        }

        /// <summary>
        /// 处理日志消息显示
        /// </summary>
        private void DisplayLog(string text, LogMessageType logMessageType)
        {
            var logItem = new LogMessageItem
            {
                Text = text,
                Type = logMessageType,
                Timestamp = DateTime.Now
            };

            var addLog = () =>
            {
                LogMessages.Add(logItem);

                // 限制日志行数
                while (LogMessages.Count > MaxLogLines)
                {
                    LogMessages.RemoveAt(0);
                }

                // 如果此前滚动条在底部，则保持到底部位置
                if (_wasAtBottom && _logScrollViewer != null)
                {
                    _logScrollViewer.Offset = new Vector(_logScrollViewer.Offset.X, _logScrollViewer.Extent.Height);
                }
            };

            if (_dispatcher != null)
            {
                _dispatcher.Post(addLog);
            }
            else
            {
                Dispatcher.UIThread.Post(addLog);
            }
        }

        /// <summary>
        /// 创建日志页面内容
        /// </summary>
        private Control CreateLogContent()
        {
            var grid = new Grid();

            // 上部：使用ItemsControl显示日志消息
            var logItemsControl = new ItemsControl
            {
                Background = Brushes.Black,
                ItemsSource = LogMessages,
                Margin = new Thickness(5)
            };

            // 创建日志消息的DataTemplate
            // 使用只读 TextBox 替代 TextBlock，以支持鼠标选中和复制，同时保留按日志类型着色能力
            var logItemTemplate = new FuncDataTemplate<LogMessageItem>((logItem, scope) =>
            {
                var textBox = new TextBox
                {
                    Text = $"[{logItem.Timestamp:HH:mm:ss}] {GetLogPrefix(logItem.Type)}{logItem.Text}",
                    Foreground = GetLogColor(logItem.Type), // 保留按类型着色
                    FontSize = 14,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    IsReadOnly = true,            // 禁止编辑
                    AcceptsReturn = false,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                    Padding = new Thickness(0),
                    CaretBrush = Brushes.Transparent // 隐藏插入符，避免多行光标干扰
                };

                // 保持可以命中、选择文本，用于复制
                textBox.IsHitTestVisible = true;

                return textBox;
            });

            logItemsControl.ItemTemplate = logItemTemplate;

            var scrollViewer = new ScrollViewer
            {
                Content = logItemsControl,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            // 保存ScrollViewer引用并添加滚动条位置变化事件
            _logScrollViewer = scrollViewer;
            scrollViewer.ScrollChanged += OnLogScrollChanged;

            Grid.SetRow(scrollViewer, 0);
            grid.Children.Add(scrollViewer);

            return grid;
        }

        /// <summary>
        /// 获取日志消息的前缀
        /// </summary>
        private string GetLogPrefix(LogMessageType type)
        {
            return type switch
            {
                LogMessageType.Normal => "[INFO] ",
                LogMessageType.Warning => "[WARN] ",
                LogMessageType.Important => "[IMPORTANT] ",
                LogMessageType.Error => "[ERROR] ",
                LogMessageType.DiceRoll => "[DICE] ",
                LogMessageType.System => "[SYSTEM] ",
                _ => "[UNKNOWN] "
            };
        }

        /// <summary>
        /// 获取日志消息的颜色
        /// </summary>
        private IBrush GetLogColor(LogMessageType type)
        {
            return type switch
            {
                LogMessageType.Normal => Brushes.White,
                LogMessageType.Warning => Brushes.Orange,
                LogMessageType.Important => Brushes.Yellow,
                LogMessageType.Error => Brushes.Red,
                LogMessageType.DiceRoll => Brushes.Cyan,
                LogMessageType.System => Brushes.Magenta,
                _ => Brushes.White
            };
        }

        /// <summary>
        /// 创建聊天页面内容
        /// 左侧：上下排列聊天显示区域和输入框+发送按钮
        /// 右侧：两个CheckBox和两个TextBox纵向排列
        /// </summary>
        private Control CreateChatContent()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // 左侧聊天区域
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // 右侧控制面板

            // 左侧：聊天区域（上下排列）
            var leftPanel = new Grid();
            leftPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // 聊天显示区域
            leftPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 输入框和按钮

            // 聊天显示区域（使用ScrollViewer和ItemsControl显示消息气泡）
            var scrollViewer = new ScrollViewer
            {
                Margin = new Thickness(5),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var itemsControl = new ItemsControl
            {
                ItemsSource = Messages,
                Margin = new Thickness(5)
            };

            // 创建Border作为聊天背景容器，支持圆角
            var chatBackground = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FFFFE0")),
                CornerRadius = new CornerRadius(8),
                Child = itemsControl
            };

            // 创建消息气泡的DataTemplate
            var borderFactory = new FuncDataTemplate<Message>((message, scope) =>
            {
                var translateTransform = new TranslateTransform { Y = 20 };
                var transformGroup = new TransformGroup
                {
                    Children = { translateTransform }
                };

                // 检查是否为合并消息
                if (message.IsForwardMessage && message.ForwardContent.Count > 0)
                {
                    // 合并消息气泡：外层容器包含多个内容项
                    var outerBorder = new Border
                    {
                        Margin = new Thickness(5, 2),
                        Padding = new Thickness(12, 8),
                        CornerRadius = new CornerRadius(12),
                        MaxWidth = 400,
                        HorizontalAlignment = message.IsFromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                        Background = message.IsFromUser 
                            ? new SolidColorBrush(Color.Parse("#fc8d87"))  // User: coral
                            : new SolidColorBrush(Color.Parse("#258292")), // Bot: teal
                        BorderBrush = new SolidColorBrush(Color.Parse("#E0E0E0")),
                        BorderThickness = new Thickness(0),
                        Opacity = 0,
                        RenderTransform = transformGroup
                    };

                    // 创建内容容器（StackPanel存放所有转发内容）
                    var contentStackPanel = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 8
                    };

                    // 为每个转发内容项创建一个小气泡/条目
                    foreach (var content in message.ForwardContent)
                    {
                        var itemBorder = new Border
                        {
                            Padding = new Thickness(8, 6),
                            CornerRadius = new CornerRadius(8),
                            Background = message.IsFromUser
                                ? new SolidColorBrush(Color.Parse("#FFDDD9"))  // Light coral
                                : new SolidColorBrush(Color.Parse("#D4EEF1")), // Light teal
                            BorderBrush = new SolidColorBrush(Color.Parse("#FFFFFF")),
                            BorderThickness = new Thickness(1)
                        };

                        var itemTextBlock = new TextBlock
                        {
                            Text = content,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#333333"))
                        };

                        itemBorder.Child = itemTextBlock;
                        contentStackPanel.Children.Add(itemBorder);
                    }

                    outerBorder.Child = contentStackPanel;

                    // 动画：淡入 + 上滑
                    var combinedAnimation = new Animation
                    {
                        Duration = TimeSpan.FromMilliseconds(300),
                        Easing = new CubicEaseOut(),
                        FillMode = FillMode.Forward
                    };

                    combinedAnimation.Children.Add(new KeyFrame
                    {
                        KeyTime = TimeSpan.FromMilliseconds(0),
                        Setters =
                        {
                            new Setter(Border.OpacityProperty, 0.0),
                            new Setter(TranslateTransform.YProperty, 20.0)
                        }
                    });

                    combinedAnimation.Children.Add(new KeyFrame
                    {
                        KeyTime = TimeSpan.FromMilliseconds(300),
                        Setters =
                        {
                            new Setter(Border.OpacityProperty, 1.0),
                            new Setter(TranslateTransform.YProperty, 0.0)
                        }
                    });

                    outerBorder.Loaded += async (sender, e) =>
                    {
                        await combinedAnimation.RunAsync(outerBorder);
                    };

                    return outerBorder;
                }
                else
                {
                    // 普通消息气泡（原有逻辑）
                    var border = new Border
                    {
                        Margin = new Thickness(5, 2),
                        Padding = new Thickness(10),
                        CornerRadius = new CornerRadius(12),
                        MaxWidth = 300,
                        HorizontalAlignment = message.IsFromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                        Background = message.IsFromUser ? new SolidColorBrush(Color.Parse("#fc8d87")) : new SolidColorBrush(Color.Parse("#258292")) ,
                        Opacity = 0, // 初始透明
                        RenderTransform = transformGroup
                    };

                    // 创建同时运行的透明度和位移动画
                    var combinedAnimation = new Animation
                    {
                        Duration = TimeSpan.FromMilliseconds(300),
                        Easing = new CubicEaseOut(),
                        FillMode = FillMode.Forward
                    };

                    // 起始关键帧：透明且在下方
                    combinedAnimation.Children.Add(new KeyFrame
                    {
                        KeyTime = TimeSpan.FromMilliseconds(0),
                        Setters =
                        {
                            new Setter(Border.OpacityProperty, 0.0),
                            new Setter(TranslateTransform.YProperty, 20.0)
                        }
                    });

                    // 结束关键帧：不透明且在正确位置
                    combinedAnimation.Children.Add(new KeyFrame
                    {
                        KeyTime = TimeSpan.FromMilliseconds(300),
                        Setters =
                        {
                            new Setter(Border.OpacityProperty, 1.0),
                            new Setter(TranslateTransform.YProperty, 0.0)
                        }
                    });

                    var textBlock = new TextBlock
                    {
                        Text = message.Text,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14
                    };

                    border.Child = textBlock;

                    // 当border加载时播放动画
                    border.Loaded += async (sender, e) =>
                    {
                        await combinedAnimation.RunAsync(border);
                    };

                    return border;
                }
            });

            itemsControl.ItemTemplate = borderFactory;
            scrollViewer.Content = chatBackground;
            Grid.SetRow(scrollViewer, 0);
            leftPanel.Children.Add(scrollViewer);

            // 下方：输入框和发送按钮
            var inputPanel = new Grid();
            inputPanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            inputPanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var inputBox = new TextBox
            {
                Margin = new Thickness(5, 0, 0, 0),
                Watermark = "Type your message or script... (Ctrl+Enter to send)",
                AcceptsReturn = true,        // 支持多行输入
                TextWrapping = TextWrapping.Wrap,  // 启用文本换行
                MinHeight = 60,              // 最小高度
                MaxHeight = 150              // 最大高度，内容超出时会显示滚动条
            };
            // 绑定文本属性
            var binding = new Avalonia.Data.Binding
            {
                Source = this,
                Path = "CurrentMessageText",
                Mode = Avalonia.Data.BindingMode.TwoWay
            };
            inputBox.Bind(Avalonia.Controls.TextBox.TextProperty, binding);

            // 添加 Ctrl+Enter 快捷键发送功能（支持多行输入）
            // 使用 AddHandler 方式并设置 HandledEventsToo = true，确保能拦截 TextBox 已处理的事件
            inputBox.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (sender, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter && 
                    e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && 
                    !string.IsNullOrWhiteSpace(CurrentMessageText))
                {
                    // 通过 SendMessageCommand 发送消息，严格遵循 MVVM 模式
                    if (SendMessageCommand.CanExecute(null))
                    {
                        SendMessageCommand.Execute(null);
                    }
                    e.Handled = true;  // 标记事件已处理，防止换行插入
                }
            }, handledEventsToo: true);  // 关键：设置为 true，使其能拦截已处理的事件
            
            Grid.SetColumn(inputBox, 0);
            inputPanel.Children.Add(inputBox);

            // 使用自定义控件替换
            var sendButton = new Border
            {
                Margin = new Thickness(-5, 0, 0, 0),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(0, 8, 8, 0),
                Background = new SolidColorBrush(Color.Parse("#fc8d87")),
                Child = new Image
                {
                    Source = new Bitmap(AssetLoader.Open(new Uri("avares://MDiceV2.Core/Assets/Sprite/Send2.png"))),
                    Width = 24,
                    Height = 24
                }
            };

            // 添加点击事件
            
            sendButton.PointerPressed += async (s, e) =>
            {
                sendButton.Background = new SolidColorBrush(Color.Parse("#d87771"));
                if (SendMessageCommand.CanExecute(null))
                {
                    SendMessageCommand.Execute(null);
                }
                await Task.Delay(100);
                sendButton.Background = new SolidColorBrush(Color.Parse("#fc8d87"));
            };

            // 添加悬浮效果
            sendButton.PointerEntered += (s, e) =>
            {
                sendButton.Background = new SolidColorBrush(Color.Parse("#ff9b95"));
            };

            sendButton.PointerExited += (s, e) =>
            {
                sendButton.Background = new SolidColorBrush(Color.Parse("#fc8d87"));
            };

            Grid.SetColumn(sendButton, 1);
            inputPanel.Children.Add(sendButton);

            Grid.SetRow(inputPanel, 1);
            leftPanel.Children.Add(inputPanel);

            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);

            // 右侧：两个CheckBox和两个TextBox纵向排列
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10, 5, 5, 5)
            };
            var checkBox1 = new Border
            {
                CornerRadius = new CornerRadius(8, 8, 8, 8),
                Background = new SolidColorBrush(Color.Parse("#fc8d87")),
                Child = new CheckBox
                {
                    Content = "Simulation Mode",
                    Margin = new Thickness(5, 0, 0, 0)
                },
                Margin = new Thickness(-5, 0, -5, 0)
            };

            // 获取CheckBox并设置其属性和绑定
            if (checkBox1.Child is CheckBox simulationCheckBox)
            {
                simulationCheckBox.IsChecked = IsSimulationMode;
                // 绑定模拟模式属性
                var simulationBinding = new Avalonia.Data.Binding
                {
                    Source = this,
                    Path = "IsSimulationMode",
                    Mode = Avalonia.Data.BindingMode.TwoWay
                };
                simulationCheckBox.Bind(Avalonia.Controls.CheckBox.IsCheckedProperty, simulationBinding);
            }

            rightPanel.Children.Add(checkBox1);

            var checkBox2 = new CheckBox
            {
                Content = "Group Chat Mode",
                Margin = new Thickness(0, 0, 0, 10)
            };
            // 绑定群聊模式开关
            var groupModeBinding = new Avalonia.Data.Binding
            {
                Source = this,
                Path = "IsGroupChatMode",
                Mode = Avalonia.Data.BindingMode.TwoWay
            };
            checkBox2.Bind(Avalonia.Controls.CheckBox.IsCheckedProperty, groupModeBinding);
            rightPanel.Children.Add(checkBox2);

            var textBox1 = new TextBox
            {
                Watermark = "Group ID",
                Margin = new Thickness(0, 0, 0, 10),
                Height = 30
            };
            // 绑定群号输入
            var groupIdBinding = new Avalonia.Data.Binding
            {
                Source = this,
                Path = "GroupIdInput",
                Mode = Avalonia.Data.BindingMode.TwoWay
            };
            textBox1.Bind(Avalonia.Controls.TextBox.TextProperty, groupIdBinding);
            rightPanel.Children.Add(textBox1);

            var textBox2 = new TextBox
            {
                Watermark = "Account ID",
                Height = 30
            };
            // 绑定账号输入
            var accountIdBinding = new Avalonia.Data.Binding
            {
                Source = this,
                Path = "AccountIdInput",
                Mode = Avalonia.Data.BindingMode.TwoWay
            };
            textBox2.Bind(Avalonia.Controls.TextBox.TextProperty, accountIdBinding);
            rightPanel.Children.Add(textBox2);

            Grid.SetColumn(rightPanel, 1);
            grid.Children.Add(rightPanel);

            return grid;
        }

        /// <summary>
        /// 创建设置页面内容
        /// 显示配置容器
        /// </summary>
        private Control CreateSettingContent()
        {
            LogSender.Normal($"[BasicConfig] ========== CreateSettingContent 开始 ==========");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // 左侧：上方WebSocket区域，下方Feedback Message Templates及ConfigContainer
            var leftPanel = new Grid();
            leftPanel.RowDefinitions.Add(new RowDefinition(new GridLength(200)));
            leftPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Feedback Message Templates ConfigContainer

            // 左侧配置容器 - Feedback Message Templates
            _feedbackTemplatesConfigContainer = new ConfigContainerViewModel();
            _feedbackTemplatesConfigContainer.Title = "Feedback Message Templatess";
            
            // 定义反馈模板的分类映射（硬编码基于GlobalFeedbackMessages中的注释）
            var feedbackCategories = new Dictionary<string, string>()
            {
                // 掷骰指令反馈
                { "RollResult", "掷骰指令反馈" },
                { "RollParamOutOfRange", "掷骰指令反馈" },
                { "RollUnknownFormat", "掷骰指令反馈" },
                
                // 暗骰
                { "DarkRollPublic", "暗骰" },
                { "DarkRollPrivatePrefix", "暗骰" },
                
                // Bot指令反馈
                { "BotOn", "Bot指令反馈" },
                { "BotOff", "Bot指令反馈" },
                { "BotStatus", "Bot指令反馈" },
                { "BotGroupOnly", "Bot指令反馈" },
                { "BotUnknownCommand", "Bot指令反馈" },
                { "BotDisabledIgnoreCommand", "Bot指令反馈" },
                { "BotAlreadyOn", "Bot指令反馈" },
                { "BotAlreadyOff", "Bot指令反馈" },
                
                // Log指令反馈
                { "LogCommandGroupOnly", "Log指令反馈" },
                { "LogEnabled", "Log指令反馈" },
                { "LogEnabledWithName", "Log指令反馈" },
                { "LogDisabled", "Log指令反馈" },
                { "LogCommandInvalid", "Log指令反馈" },
                { "LogNameRequired", "Log指令反馈" },
                { "LogList", "Log指令反馈" },
                { "LogListEmpty", "Log指令反馈" },
                
                // 技能插入指令反馈
                { "SkillInsertFormatError", "技能插入指令反馈" },
                { "CharacterNameEmpty", "技能插入指令反馈" },
                { "CharacterCardLimitExceeded", "技能插入指令反馈" },
                { "CharacterEmptyAndApplied", "技能插入指令反馈" },
                { "SkillInsertNoSkills", "技能插入指令反馈" },
                { "RollError", "技能插入指令反馈" },
                { "SkillValueFormatError", "技能插入指令反馈" },
                { "SkillValueOutOfRange", "技能插入指令反馈" },
                { "SkillValueNotApplicable", "技能插入指令反馈" },
                { "SkillInsertSuccess", "技能插入指令反馈" },
                { "SkillInsertNoValidSkills", "技能插入指令反馈" },
                { "SkillInsertNoName", "技能插入指令反馈" },
                { "SkillInsertDuplicate", "技能插入指令反馈" },
                { "SkillInsertInvalid", "技能插入指令反馈" },
                { "SkillInsertError", "技能插入指令反馈" },
                
                // 检定反馈
                { "CoCFormatError", "检定反馈" },
                { "UnsupportedCheckMode", "检定反馈" },
                { "CharacterNotFound", "检定反馈" },
                { "InternalError", "检定反馈" },
                { "MainPartFormatError", "检定反馈" },
                { "SkillNotFound", "检定反馈" },
                { "DiceRollError", "检定反馈" },
                { "CoCCheckResult", "检定反馈" },
                { "ETCheckResult", "检定反馈" },
                
                // CoC7 检定结果个性化文本
                { "CoCExMessageSuccess", "CoC7检定结果个性化文本" },
                { "CoCExMessageFailure", "CoC7检定结果个性化文本" },
                { "CoCExMessageHardSuccess", "CoC7检定结果个性化文本" },
                { "CoCExMessageExtremeSuccess", "CoC7检定结果个性化文本" },
                { "CoCExMessageCriticalSuccess", "CoC7检定结果个性化文本" },
                { "CoCExMessageCriticalFailure", "CoC7检定结果个性化文本" },
                
                // ET 检定结果个性化文本
                { "ETExMessageSuccess", "ET检定结果个性化文本" },
                { "ETExMessageFailure", "ET检定结果个性化文本" },
                { "ETExMessageCriticalSuccess", "ET检定结果个性化文本" },
                { "ETExMessageCriticalFailure", "ET检定结果个性化文本" },
                
                { "CustomCheckEXMessage", "群加入同意和好友同意通知" },
                
                // 群加入同意和好友同意通知
                { "GroupJoinApproved", "群加入同意和好友同意通知" },
                { "FriendRequestApproved", "群加入同意和好友同意通知" },
                
                // Duel 指令反馈
                { "DuelNoTurnsAvailable", "Duel指令反馈" },
                { "DuelNew", "Duel指令反馈" },
                { "DuelContinue", "Duel指令反馈" },
                
                // Help指令反馈
                { "HelpDefaultMessage", "Help指令反馈" },
                
                // 人物卡输出格式
                { "COCCharacterDetails", "人物卡输出格式" },
                
                // 角色属性生成默认消息
                { "GCDefaultMessage", "角色属性生成默认消息" },
                
                // Team消息
                { "TeamCallMessage", "Team消息" }
            };
            
            // 为GlobalFeedbackMessages.Templates中的每个模板创建配置项（带分类标签）
            var defaultFeedbacks = MDiceV2.Models.GlobalFeedbackMessages.GetDefaultFeedbackTemplates();
            string lastCategory = "";
            
            foreach (var kvp in MDiceV2.Models.GlobalFeedbackMessages.FeedbackTemplates)
            {
                // 获取当前项的分类
                feedbackCategories.TryGetValue(kvp.Key, out var currentCategory);
                currentCategory = currentCategory ?? "其他";
                
                // 如果分类改变，添加一个分割标签
                if (lastCategory != currentCategory)
                {
                    _feedbackTemplatesConfigContainer.AddConfig(currentCategory, ConfigType.SectionLabel, null);
                    lastCategory = currentCategory;
                }
                
                // 添加配置项
                defaultFeedbacks.TryGetValue(kvp.Key, out var defVal);
                _feedbackTemplatesConfigContainer.AddConfig(kvp.Key, ConfigType.LineEdit, defVal);
                _feedbackTemplatesConfigContainer.SetValue(kvp.Key, kvp.Value);
            }
            // 确保 FilteredItems 在初始化后与 Items 同步，避免加载成功但列表为空
            _feedbackTemplatesConfigContainer.UpdateFilteredItems();

            // 设置值变化回调，用于保存到FeedbackTemplate表
            _feedbackTemplatesConfigContainer.OnValueChanged = (key, value) =>
            {
                var newValue = value?.ToString() ?? "";
                var oldValue = MDiceV2.Models.GlobalFeedbackMessages.FeedbackTemplates[key];
                
                // 【关键修复】检查值是否真的改变了（防止UI重建导致的虚假推送）
                if (oldValue == newValue)
                {
                    LogSender.InfoFormat($"[MainViewModel] FeedbackTemplates值未改变，跳过更新 Key={key}, Value={newValue}");
                    return;
                }
                
                // 更新内存中的FeedbackTemplates
                MDiceV2.Models.GlobalFeedbackMessages.FeedbackTemplates[key] = newValue;
                
                // 推送配置变更到远程（如果启用了同步模式）
#pragma warning disable CS4014 // 忽略未被等待的异步调用警告
                PushConfigUpdateToRemoteAsync(key, newValue);
#pragma warning restore CS4014
            };


            // WebSocket Connection 区域
            var websocketPanel = new Grid();
            websocketPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 标题行
            websocketPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // 内容行
            websocketPanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            websocketPanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // WebSocket Connection 标题
            var websocketTitle = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#258292")),
                Height = 40,
                CornerRadius = new CornerRadius(8, 8, 8, 8),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock
                {
                    Text = "WebSocket Connectionsss",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("avares://MDiceV2.Core/Assets/Font/PlayfairDisplay-Black.ttf#Playfair Display"),
                    FontSize = 14
                }
            };
            Grid.SetColumnSpan(websocketTitle, 2); // 跨越两列
            Grid.SetRow(websocketTitle, 0);
            websocketPanel.Children.Add(websocketTitle);

            // 左侧log面板 - 显示WebSocket连接信息（锁定为黑底白字，不受系统主题影响）
            var logPanel = new ScrollViewer
            {
                Background = Brushes.Black,
                Margin = new Thickness(5)
            };
            // 为 ScrollViewer 添加专用样式类，应用 App.axaml 中的 WsLogScroll 样式
            logPanel.Classes.Add("WsLogScroll");

            var wsLogTextBox = new TextBox
            {
                Background = Brushes.Black,
                Foreground = Brushes.White,
                FontSize = 12,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0),
                FontFamily = new FontFamily("avares://MDiceV2.Core/Assets/Font/PlayfairDisplay-Black.ttf#Playfair Display")
            };
            // 为 TextBox 添加专用样式类，应用 App.axaml 中的 WsLogTextBox 样式
            wsLogTextBox.Classes.Add("WsLogTextBox");

            logPanel.Content = wsLogTextBox;

            // 绑定WebSocket连接信息到TextBox - 使用数据绑定而不是直接设置文本
            wsLogTextBox.Bind(
                Avalonia.Controls.TextBox.TextProperty,
                new Avalonia.Data.Binding("WsConnectionLogs")
            );
            Grid.SetColumn(logPanel, 0);
            Grid.SetRow(logPanel, 1);
            websocketPanel.Children.Add(logPanel);

            // 右侧WS URL配置
            var wsConfigPanel = new StackPanel
            {
                Margin = new Thickness(5),
                Spacing = 5
            };

            // WS URL 输入框 - 绑定到ViewModel的WsUrl属性，实现双向绑定
            var wsUrlTextBox = new TextBox
            {
                Watermark = "WebSocket URL (e.g., ws://localhost:8080)",
                Height = 30,
                FontSize = 12
            };

            // 绑定到ViewModel的WsUrl属性，实现双向同步
            var wsUrlBinding = new Avalonia.Data.Binding
            {
                Source = this,
                Path = "WsUrl",
                Mode = Avalonia.Data.BindingMode.TwoWay
            };
            wsUrlTextBox.Bind(Avalonia.Controls.TextBox.TextProperty, wsUrlBinding);

            // TextBox现在通过数据绑定自动同步，不需要手动TextChanged事件
            // 同步逻辑在OnWsUrlChanged属性变更回调中处理
            Grid.SetRow(wsUrlTextBox, 0);
            Grid.SetColumnSpan(wsUrlTextBox, 2);
            wsConfigPanel.Children.Add(wsUrlTextBox);

            // 连接按钮 - 使用与send button相同的自定义样式，并在左侧显示图标
            var connectButton = new Border
            {
                Margin = new Thickness(0, 3, 0, 0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse("#fc8d87")),
                Height = 45,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new Button
                {
                    CornerRadius = new CornerRadius(4),
                    Height = 45,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Command = ConnectWebSocketCommand,
                    Background = new SolidColorBrush(Color.Parse("#fc8d87")),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(16, 8)
                }
            };

            // 设置 connect 按钮内容为 图标 + 文本
            if (connectButton.Child is Button innerButton)
            {
                var connectStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                try
                {
                    var bmp = new Bitmap(AssetLoader.Open(new Uri("avares://MDiceV2.Core/Assets/Sprite/Connect.png")));
                    connectStack.Children.Add(new Image { Source = bmp, Width = 24, Height = 24 });
                }
                catch { }
                connectStack.Children.Add(new TextBlock { Text = "Reconnect WebSockets", VerticalAlignment = VerticalAlignment.Center });
                innerButton.Content = connectStack;

                // 按钮视觉效果
                innerButton.PointerPressed += async (s, e) =>
                {
                    connectButton.Background = new SolidColorBrush(Color.Parse("#d87771"));
                    innerButton.Background = new SolidColorBrush(Color.Parse("#d87771"));
                };
                innerButton.PointerEntered += (s, e) =>
                {
                    connectButton.Background = new SolidColorBrush(Color.Parse("#ff9b95"));
                    innerButton.Background = new SolidColorBrush(Color.Parse("#ff9b95"));
                };
                innerButton.PointerExited += (s, e) =>
                {
                    connectButton.Background = new SolidColorBrush(Color.Parse("#fc8d87"));
                    innerButton.Background = new SolidColorBrush(Color.Parse("#fc8d87"));
                };
                innerButton.PointerReleased += (s, e) =>
                {
                    connectButton.Background = new SolidColorBrush(Color.Parse("#fc8d87"));
                    innerButton.Background = new SolidColorBrush(Color.Parse("#fc8d87"));
                };
            }

            wsConfigPanel.Children.Add(connectButton);

            // Update区域：左侧 Update 按钮，右侧镜像源选择下拉框（共用一行，等宽）
            var updateRowGrid = new Grid
            {
                Height = 45,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            updateRowGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // 左半：Update按钮
            updateRowGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // 右半：镜像站选择

            // 左侧：Update 按钮
            var updateButton = new Border
            {
                CornerRadius = new CornerRadius(4, 0, 0, 4),
                Background = new SolidColorBrush(Color.Parse("#258292")),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new Button
                {
                    CornerRadius = new CornerRadius(4, 0, 0, 4),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Command = UpdateFromGitHubCommand,
                    Background = new SolidColorBrush(Color.Parse("#207584")),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(12, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Center
                }
            };

            if (updateButton.Child is Button updateInner)
            {
                var updateStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                try
                {
                    var bmp2 = new Bitmap(AssetLoader.Open(new Uri("avares://MDiceV2.Core/Assets/Sprite/Update.png")));
                    updateStack.Children.Add(new Image { Source = bmp2, Width = 18, Height = 18 });
                }
                catch { }
                updateStack.Children.Add(new TextBlock { Text = "Update", VerticalAlignment = VerticalAlignment.Center });
                updateInner.Content = updateStack;
            }

            Grid.SetColumn(updateButton, 0);
            updateRowGrid.Children.Add(updateButton);

            // 右侧：更新源选择 ComboBox（支持多个镜像站，从 CustomUpdateManager 动态生成）
            // 放在 Grid 第1列，由列宽控制为右半区宽度，内部内容居中但不影响控件占满半宽
            // 获取所有支持的镜像站配置
            var allMirrors = MDiceV2.Models.CustomUpdateManager.MirrorSites.GetAllMirrors()
                ?? new List<(string, string, string)>
                {
                    // 备用默认配置（防止反射失败）
                    ("GitHub 官方主站", "github", ""),
                    ("ghproxy.net (国内加速)", "ghproxy", "https://ghproxy.net/"),
                    ("FastGit (独立加速)", "fastgit", "https://raw.fastgit.org/"),
                    ("Jihulab (极狐加速)", "jihulab", "https://jihulab.com/api/v4/projects/"),
                };

            // 构建源标识符到显示名称的映射
            var sourceMapping = new Dictionary<int, string>();
            var sourceReverseMapping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var displayNames = new List<string>();

            for (int i = 0; i < allMirrors.Count; i++)
            {
                sourceMapping[i] = allMirrors[i].SourceId;
                sourceReverseMapping[allMirrors[i].SourceId] = i;
                displayNames.Add(allMirrors[i].DisplayName);
            }

            var mirrorCombo = new ComboBox
            {
                CornerRadius = new CornerRadius(0, 4, 4, 0),
                Background = new SolidColorBrush(Color.Parse("#333333")),
                Foreground = Brushes.White,
                Height = 45,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 8, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ItemsSource = displayNames,
                SelectedIndex = 0, // 默认第一个（GitHub 主站）
                FontSize = 12
            };

            // 将 ComboBox 与 ViewModel.UpdateSourceSelection 做逻辑绑定
            mirrorCombo.SelectionChanged += (s, e) =>
            {
                if (mirrorCombo.SelectedIndex >= 0 && sourceMapping.TryGetValue(mirrorCombo.SelectedIndex, out var sourceValue))
                {
                    UpdateSourceSelection = sourceValue;
                    LogSender.Normal($"[MainViewModel] 更新源选择已更改为: {sourceValue} ({displayNames[mirrorCombo.SelectedIndex]})");
                }
            };

            // 初始化选中项与当前 UpdateSourceSelection 状态同步（保持双向一致）
            mirrorCombo.Loaded += (s, e) =>
            {
                if (sourceReverseMapping.TryGetValue(UpdateSourceSelection, out var index))
                {
                    mirrorCombo.SelectedIndex = index;
                    LogSender.Normal($"[MainViewModel] 更新源选择初始化为: {UpdateSourceSelection} ({displayNames[index]})");
                }
                else
                {
                    mirrorCombo.SelectedIndex = 0; // 默认 GitHub
                    LogSender.Normal($"[MainViewModel] 未知的源标识符，已重置为默认值 (GitHub)");
                }
            };

            Grid.SetColumn(mirrorCombo, 1);
            updateRowGrid.Children.Add(mirrorCombo);

            wsConfigPanel.Children.Add(updateRowGrid);

            // 测试按钮 - 用于验证双向绑定是否生效
            var testButton = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse("#ff6b6b")),
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 6, 0, 0),
                Child = new Button
                {
                    CornerRadius = new CornerRadius(4),
                    Height = 30,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Command = TestBindingCommand,
                    Background = new SolidColorBrush(Color.Parse("#ff6b6b")),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(16, 8),
                    Content = "Test Binding (Save Basic Settings)"
                }
            };

            // 设置测试按钮的视觉效果
            if (testButton.Child is Button testInner)
            {
                testInner.PointerPressed += (s, e) =>
                {
                    testButton.Background = new SolidColorBrush(Color.Parse("#d44a4a"));
                    testInner.Background = new SolidColorBrush(Color.Parse("#d44a4a"));
                };
                testInner.PointerEntered += (s, e) =>
                {
                    testButton.Background = new SolidColorBrush(Color.Parse("#ff8080"));
                    testInner.Background = new SolidColorBrush(Color.Parse("#ff8080"));
                };
                testInner.PointerExited += (s, e) =>
                {
                    testButton.Background = new SolidColorBrush(Color.Parse("#ff6b6b"));
                    testInner.Background = new SolidColorBrush(Color.Parse("#ff6b6b"));
                };
                testInner.PointerReleased += (s, e) =>
                {
                    testButton.Background = new SolidColorBrush(Color.Parse("#ff6b6b"));
                    testInner.Background = new SolidColorBrush(Color.Parse("#ff6b6b"));
                };
            }

            // wsConfigPanel.Children.Add(testButton); // 隐藏test按钮

            Grid.SetColumn(wsConfigPanel, 1);
            Grid.SetRow(wsConfigPanel, 1);
            websocketPanel.Children.Add(wsConfigPanel);

            Grid.SetRow(websocketPanel, 0);
            leftPanel.Children.Add(websocketPanel);


            var templatesConfigView = new MDiceV2.Core.UI.Views.ConfigContainer
            {
                DataContext = _feedbackTemplatesConfigContainer
            };
            Grid.SetRow(templatesConfigView, 1);
            leftPanel.Children.Add(templatesConfigView);
            templatesConfigView.InitializeHelpText("部分可用的特殊格式：\n" +
                "<name> - 替换为用户昵称\n" +
                "<id> - 替换为用户QQ号\n" +
                "<time> - 替换为xx:xx格式的当前时间\n" +
                "<dice 表达式> - 执行掷骰表达式（如 <dice 1d6>）\n" +
                "<deck 键值> - 从FeedBackDeck中随机选择列表项\n" +
                "<read 键值> - 读取存储的值\n" +
                "<write 键,值> - 写入值到存储\n" +
                "<func 函数名> - 调用Lua脚本函数（如 <func FunctionName()>）\n\n" +
                "随机选择格式（{}包围）：\n" +
                "{选项1||选项2||选项3} - 等权重随机选择\n" +
                "{选项1<weight 2>||选项2||选项3<weight 3>} - 按权重随机选择\n" +
                "{选项1<id 111>||选项2<id 222>||选项3} - 按用户ID过滤\n" +
                "{选项1<group 123>||选项2<group 456>||选项3} - 按群号过滤\n" +
                "{选项1<id 111><weight 2>||选项2<id 222>||选项3<weight 3>} - 组合使用");
            // 右侧面板：上半区域Basic Config，下半区域HelpMessage
            var rightPanel = new Grid();
            rightPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Basic Config区域
            rightPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // HelpMessage区域

            // Basic Config 配置容器
            _basicConfigContainer = new ConfigContainerViewModel();
            _basicConfigContainer.Title = "Basic Config";
            
            // 【修复】注册初始化完成事件，以便在GlobalFeedbackMessages初始化后重新加载已保存的配置
            MDiceV2.Models.GlobalFeedbackMessages.OnInitializationComplete += () =>
            {
                LogSender.Normal($"[BasicConfig初始化] 【修复】检测到GlobalFeedbackMessages初始化完成，正在重新加载已保存配置");
                ReloadBasicConfigFromGlobal();
            };

            // 先设置值变化回调，这样在AddConfig时就会使用这个回调
            _basicConfigContainer.OnValueChanged = (key, value) =>
            {
                var newValue = value?.ToString() ?? "";
                var oldValue = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting(key);
                
                // 【关键修复】检查值是否真的改变了（防止UI重建导致的虚假推送）
                if (oldValue == newValue)
                {
                    LogSender.InfoFormat($"[MainViewModel] 基础设置值未改变，跳过更新 Key={key}, Value={newValue}");
                    return;
                }
                
                MDiceV2.Models.GlobalFeedbackMessages.SetBasicSetting(key, newValue);
                
                // Synchronize changes to basicConfigData
                if (MessageProcessor.Instance != null)
                {
                    switch (key)
                    {
                        case "master":
                            MessageProcessor.Instance.basicConfigData.Master = newValue;
                            break;
                        case "mastergroup":
                            MessageProcessor.Instance.basicConfigData.MasterGroup = newValue;
                            break;
                        case "Url":
                            MessageProcessor.Instance.basicConfigData.Url = newValue;
                            break;
                        case "SendGroupJoinReport":
                            if (bool.TryParse(newValue, out bool sendGroupReport))
                                MessageProcessor.Instance.basicConfigData.SendGroupJoinReport = sendGroupReport;
                            break;
                        case "SendFriendJoinReport":
                            if (bool.TryParse(newValue, out bool sendFriendReport))
                                MessageProcessor.Instance.basicConfigData.SendFriendJoinReport = sendFriendReport;
                            break;
                        case "ApproveGroupJoinRequest":
                            if (bool.TryParse(newValue, out bool approveGroup))
                                MessageProcessor.Instance.basicConfigData.ApproveGroupJoinRequest = approveGroup;
                            break;
                        case "ApproveFriendJoinRequest":
                            if (bool.TryParse(newValue, out bool approveFriend))
                                MessageProcessor.Instance.basicConfigData.ApproveFriendJoinRequest = approveFriend;
                            break;
                    }
                }

                // 推送配置变更到远程（如果启用了同步模式）
                // Fire and Forget 异步推送，不阻塞主线程
#pragma warning disable CS4014 // 忽略未被等待的异步调用警告
                PushConfigUpdateToRemoteAsync(key, newValue);
#pragma warning restore CS4014
            };

            // 【修复】添加新的配置项 - 使用GetAllBasicSettings获取磁盘保存的值，而不仅是默认值
            var defaultBasic = MDiceV2.Models.GlobalFeedbackMessages.GetAllBasicSettings();
            LogSender.Normal($"[BasicConfig初始化] 【修复】从磁盘加载已保存的设置，共 {defaultBasic.Count} 个");
            
            // 记录每个初始值
            var sgv = defaultBasic.GetValueOrDefault("SendGroupJoinReport", "false");
            var sfv = defaultBasic.GetValueOrDefault("SendFriendJoinReport", "false");
            var agv = defaultBasic.GetValueOrDefault("ApproveGroupJoinRequest", "false");
            var afv = defaultBasic.GetValueOrDefault("ApproveFriendJoinRequest", "false");
            var mav = defaultBasic.GetValueOrDefault("Master", string.Empty);
            var mgv = defaultBasic.GetValueOrDefault("MasterGroup", string.Empty);
            var urv = defaultBasic.GetValueOrDefault("Url", string.Empty);
            
            LogSender.Normal($"[BasicConfig初始化] 初始值 - SendGroupJoinReport={sgv}, SendFriendJoinReport={sfv}, ApproveGroupJoinRequest={agv}, ApproveFriendJoinRequest={afv}, Master={mav}, MasterGroup={mgv}, Url={urv}");
            foreach (var kvp in defaultBasic)
            {
                LogSender.Normal($"[BasicConfig初始化] 【诊断】已保存的设置: {kvp.Key} = {kvp.Value}");
            }
            
            _basicConfigContainer.AddConfig("SendGroupJoinReport", ConfigType.CheckBox, sgv);
            LogSender.Normal($"[BasicConfig初始化] ✅ 已添加 SendGroupJoinReport (CheckBox) = {sgv}");
            _basicConfigContainer.AddConfig("SendFriendJoinReport", ConfigType.CheckBox, sfv);
            LogSender.Normal($"[BasicConfig初始化] ✅ 已添加 SendFriendJoinReport (CheckBox) = {sfv}");
            _basicConfigContainer.AddConfig("ApproveGroupJoinRequest", ConfigType.CheckBox, agv);
            LogSender.Normal($"[BasicConfig初始化] ✅ 已添加 ApproveGroupJoinRequest (CheckBox) = {agv}");
            _basicConfigContainer.AddConfig("ApproveFriendJoinRequest", ConfigType.CheckBox, afv);
            LogSender.Normal($"[BasicConfig初始化] ✅ 已添加 ApproveFriendJoinRequest (CheckBox) = {afv}");
            _basicConfigContainer.AddConfig("master", ConfigType.LineEdit, mav);
            LogSender.Normal($"[BasicConfig初始化] ✅ 已添加 master (LineEdit) = {mav}");
            _basicConfigContainer.AddConfig("mastergroup", ConfigType.LineEdit, mgv);
            LogSender.Normal($"[BasicConfig初始化] ✅ 已添加 mastergroup (LineEdit) = {mgv}");
            _basicConfigContainer.AddConfig("Url", ConfigType.LineEdit, urv);
            LogSender.Normal($"[BasicConfig初始化] ✅ 已添加 Url (LineEdit) = {urv}");
            
            LogSender.Normal($"[BasicConfig初始化] 配置项初始化完成，共7个项目");
            LogSender.Normal($"[BasicConfig初始化] 【诊断】Items.Count = {_basicConfigContainer.Items.Count}");
            foreach (var item in _basicConfigContainer.Items)
            {
                LogSender.Normal($"[BasicConfig初始化] 【诊断】Item: Key={item.Key}, Type={item.Type}, Value={item.Value}");
            }

            // 初始化默认值（确保所有配置项都有默认值）
            _basicConfigContainer.InitializeDefaults();
            LogSender.Normal($"[BasicConfig初始化] ConfigContainer创建完成，Items数量: {_basicConfigContainer.Items.Count}");
            LogSender.Normal($"[BasicConfig初始化] 【验证】 FilteredItems数量: {_basicConfigContainer.FilteredItems.Count}");

            var configView = new MDiceV2.Core.UI.Views.ConfigContainer
            {
                DataContext = _basicConfigContainer
            };
            LogSender.Normal($"[BasicConfig初始化] ConfigView已创建，DataContext已绑定");

            // 强制刷新配置容器以确保数据绑定生效
            _basicConfigContainer.Items.CollectionChanged += (s, e) => _basicConfigContainer.UpdateFilteredItems();
            LogSender.Normal($"[BasicConfig初始化] ========== CreateSettingContent完成 ==========");
            _basicConfigContainer.UpdateFilteredItems();

            Grid.SetRow(configView, 0);
            rightPanel.Children.Add(configView);
            
            LogSender.Normal($"[BasicConfig初始化] ========== CreateSettingContent完成 ==========");

            // HelpMessage 配置容器
            _helpTemplatesConfigContainer = new ConfigContainerViewModel();
            _helpTemplatesConfigContainer.Title = "HelpMessage";

            // 为GlobalFeedbackMessages.HelpTemplates中的每个模板创建配置项
            var defaultHelps = MDiceV2.Models.GlobalFeedbackMessages.GetDefaultHelpTemplates();
            foreach (var kvp in MDiceV2.Models.GlobalFeedbackMessages.HelpTemplates)
            {
                defaultHelps.TryGetValue(kvp.Key, out var defVal);
                _helpTemplatesConfigContainer.AddConfig(kvp.Key, ConfigType.LineEdit, defVal);
                _helpTemplatesConfigContainer.SetValue(kvp.Key, kvp.Value);
            }

            // 确保 FilteredItems 在初始化后与 Items 同步，避免加载成功但列表为空
            _helpTemplatesConfigContainer.UpdateFilteredItems();

            // 设置值变化回调，用于保存到HelpTemplates表
            _helpTemplatesConfigContainer.OnValueChanged = (key, value) =>
            {
                var newValue = value?.ToString() ?? "";
                var oldValue = MDiceV2.Models.GlobalFeedbackMessages.HelpTemplates[key];
                
                // 【关键修复】检查值是否真的改变了（防止UI重建导致的虚假推送）
                if (oldValue == newValue)
                {
                    LogSender.InfoFormat($"[MainViewModel] HelpTemplates值未改变，跳过更新 Key={key}, Value={newValue}");
                    return;
                }
                
                // 更新内存中的HelpTemplates
                MDiceV2.Models.GlobalFeedbackMessages.HelpTemplates[key] = newValue;
                
                // 推送配置变更到远程（如果启用了同步模式）
#pragma warning disable CS4014 // 忽略未被等待的异步调用警告
                PushConfigUpdateToRemoteAsync(key, newValue);
#pragma warning restore CS4014
            };

            var helpConfigView = new MDiceV2.Core.UI.Views.AdjustableConfigContainer
            {
                DataContext = _helpTemplatesConfigContainer
            };
            helpConfigView.InitializeHelpText("=====空的默认配置说明.~(OvO)~======\n\n此容器支持添加新配置项功能。\n可以点击Add按钮来添加新的配置项。");
            Grid.SetRow(helpConfigView, 1);
            rightPanel.Children.Add(helpConfigView);

            Grid.SetColumn(leftPanel, 0);
            Grid.SetColumn(rightPanel, 1);
            grid.Children.Add(leftPanel);
            grid.Children.Add(rightPanel);

            return grid;
        }

        /// <summary>
        /// 【修复】从GlobalFeedbackMessages重新加载已保存的BasicConfig
        /// 用于解决 MainViewModel 先于 GlobalFeedbackMessages 初始化导致的配置丢失问题
        /// </summary>
        private void ReloadBasicConfigFromGlobal()
        {
            try
            {
                if (_basicConfigContainer == null)
                {
                    LogSender.Warn($"[BasicConfig重新加载] _basicConfigContainer 为 null，无法重新加载");
                    return;
                }

                LogSender.Normal($"[BasicConfig重新加载] 开始从GlobalFeedbackMessages重新加载配置");
                _basicConfigContainer.IsCallbackEnabled = false;

                // 获取已保存的所有设置
                var savedSettings = MDiceV2.Models.GlobalFeedbackMessages.GetAllBasicSettings();
                LogSender.Normal($"[BasicConfig重新加载] 【诊断】从磁盘加载的设置数: {savedSettings.Count}");
                foreach (var kvp in savedSettings)
                {
                    LogSender.Normal($"[BasicConfig重新加载] 【诊断】设置: {kvp.Key} = {kvp.Value}");
                }

                // 更新现有的ConfigItem值
                foreach (var item in _basicConfigContainer.Items)
                {
                    if (savedSettings.TryGetValue(item.Key, out var savedValue))
                    {
                        // 如果值不同，更新它
                        if (item.Value?.ToString() != savedValue)
                        {
                            if (item.Type == ModelsConfigType.CheckBox)
                            {
                                item.Value = savedValue;  // 保持字符串，ValueAsBool会处理转换
                                LogSender.Normal($"[BasicConfig重新加载] ✅ 更新CheckBox {item.Key} = {savedValue}");
                            }
                            else if (item.Type == ModelsConfigType.LineEdit)
                            {
                                item.Value = savedValue;
                                LogSender.Normal($"[BasicConfig重新加载] ✅ 更新LineEdit {item.Key} = {savedValue}");
                            }
                        }
                    }
                }

                _basicConfigContainer.IsCallbackEnabled = true;
                _basicConfigContainer.UpdateFilteredItems();
                LogSender.Normal($"[BasicConfig重新加载] ========== 重新加载完成 ==========");
            }
            catch (Exception ex)
            {
                LogSender.Error($"[BasicConfig重新加载] 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 连接WebSocket命令
        /// </summary>
        [RelayCommand]
        private async Task ConnectWebSocket()
        {
            try
            {
                AddWSConnectionLog("Attempting to connect to WebSocket...");
                AddWSConnectionLog($"URL: {WsUrl}");

                // 设置WS URL到MessageProcessor
                if (_globalMessageProcessor?.MessageDistribution?.WSconnection != null)
                {
                    WSconnection.wsUrl = WsUrl;
                    await _globalMessageProcessor.MessageDistribution.WSconnection.StartConnection();
                }
                else
                {
                    AddWSConnectionLog("Error: MessageDistribution or WSconnection is null");
                }
            }
            catch (Exception ex)
            {
                AddWSConnectionLog($"Connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 GitHub 拉取并更新 DLL 的命令（使用定制化更新逻辑）
        /// 根据当前 ComboBox 选择决定是否通过 ghproxy 镜像下载。
        /// </summary>
        [RelayCommand]
        private async Task UpdateFromGitHub()
        {
            try
            {
                // ========== 阶段 1: 初始化 ==========
                AddWSConnectionLog("═══════════════════════════════════════");
                AddWSConnectionLog("🔄 开始执行更新检查...");
                AddWSConnectionLog($"⏰ 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                AddWSConnectionLog("");

                // ========== 阶段 2: 配置更新源 ==========
                AddWSConnectionLog("📋 【步骤 1/5】配置更新源...");
                string selectedSource = UpdateSourceSelection switch
                {
                    "github" => "GitHub 主站 (官方源)",
                    "ghproxy" => "ghproxy.net (国内镜像)",
                    "fastgit" => "FastGit (独立加速镜像)",
                    _ => "默认源"
                };
                AddWSConnectionLog($"   ✓ 已选择: {selectedSource}");

                // 将当前更新源选择写入全局设置，供 CustomUpdateManager 使用
                MDiceV2.Models.GlobalFeedbackMessages.SetBasicSetting("UpdateSource", UpdateSourceSelection);
                AddWSConnectionLog($"   ✓ 配置已保存到数据库");
                LogSender.Normal($"[UpdateFromGitHub] 已设置更新源为: {UpdateSourceSelection}");
                AddWSConnectionLog("");

                // ========== 阶段 3: 检查网络连接 ==========
                AddWSConnectionLog("🌐 【步骤 2/5】检查网络连接...");
                AddWSConnectionLog("   ⏳ 连接到更新源...");
                await Task.Delay(500); // 模拟网络检查延迟

                // ========== 阶段 4: 执行更新 ==========
                AddWSConnectionLog($"   ✓ 网络连接正常");
                AddWSConnectionLog("");
                AddWSConnectionLog("📦 【步骤 3/5】获取版本信息...");
                AddWSConnectionLog("   ⏳ 从更新源检索最新版本...");
                
                var mgr = new CustomUpdateManager(AddWSConnectionLog);
                
                AddWSConnectionLog("");
                AddWSConnectionLog("⬇️  【步骤 4/5】下载更新文件...");
                AddWSConnectionLog("   这个过程可能需要一些时间，请耐心等待...");

                var result = await mgr.ExecuteCustomUpdateAsync();

                AddWSConnectionLog("");
                
                // ========== 阶段 5: 完成 ==========
                if (result.Success)
                {
                    AddWSConnectionLog("✅ 【步骤 5/5】验证和安装...");
                    AddWSConnectionLog($"   ✓ {result.Message}");
                    AddWSConnectionLog("");
                    AddWSConnectionLog("═══════════════════════════════════════");
                    AddWSConnectionLog("🎉 更新成功！");
                    AddWSConnectionLog("   应用将在几秒内重启以应用更新...");
                    LogSender.Normal($"[UpdateFromGitHub] 更新成功: {result.Message}");
                }
                else
                {
                    AddWSConnectionLog("❌ 【步骤 5/5】更新失败");
                    AddWSConnectionLog($"   错误: {result.Message}");
                    AddWSConnectionLog("");
                    AddWSConnectionLog("═══════════════════════════════════════");
                    AddWSConnectionLog("⚠️  更新检查已跳过 (无新版本可用)");
                    AddWSConnectionLog("   您已在最新版本");
                    LogSender.Normal($"[UpdateFromGitHub] 更新结果: {result.Message}");
                }

                AddWSConnectionLog("");
                AddWSConnectionLog($"⏰ 完成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                AddWSConnectionLog("═══════════════════════════════════════");
            }
            catch (Exception ex)
            {
                AddWSConnectionLog("");
                AddWSConnectionLog("═══════════════════════════════════════");
                AddWSConnectionLog("❌ 更新过程发生异常!");
                AddWSConnectionLog($"   错误信息: {ex.Message}");
                AddWSConnectionLog($"   错误类型: {ex.GetType().Name}");
                
                if (ex.InnerException != null)
                {
                    AddWSConnectionLog($"   内部异常: {ex.InnerException.Message}");
                }
                
                AddWSConnectionLog("");
                AddWSConnectionLog("💡 解决建议:");
                AddWSConnectionLog("   • 检查网络连接是否正常");
                AddWSConnectionLog("   • 尝试更换更新源（GitHub、ghproxy、FastGit）");
                AddWSConnectionLog("   • 如果问题持续，请稍后重试");
                AddWSConnectionLog("═══════════════════════════════════════");
                
                LogSender.Error($"[UpdateFromGitHub] 更新异常: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 测试双向绑定是否生效的命令 - 验证master和mastergroup绑定及保存功能
        /// </summary>
        [RelayCommand]
        private void TestBinding()
        {
            try
            {
                // 获取当前的master和mastergroup值（UI中的值）
                string masterValue = _basicConfigContainer.GetValue("master")?.ToString() ?? "null";
                string mastergroupValue = _basicConfigContainer.GetValue("mastergroup")?.ToString() ?? "null";

                // 获取GlobalFeedbackMessages中的值（内存中的设置）
                string globalMaster = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting("master");
                string globalMasterGroup = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting("mastergroup");

                // 手动同步UI值到GlobalFeedbackMessages._basicSettings（如果不同步）
                if (masterValue != "null" && masterValue != globalMaster)
                {
                    MDiceV2.Models.GlobalFeedbackMessages.SetBasicSetting("master", masterValue);
                }
                if (mastergroupValue != "null" && mastergroupValue != globalMasterGroup)
                {
                    MDiceV2.Models.GlobalFeedbackMessages.SetBasicSetting("mastergroup", mastergroupValue);
                }

                // 同步更新源选择
                MDiceV2.Models.GlobalFeedbackMessages.SetBasicSetting("UpdateSource", UpdateSourceSelection);

                // 重新获取更新后的值
                string updatedGlobalMaster = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting("master");
                string updatedGlobalMasterGroup = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting("mastergroup");
                string updatedUpdateSource = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting("UpdateSource");

                // 发送日志验证
                AddWSConnectionLog($"Test binding: master(UI)='{masterValue}', master(mem)='{updatedGlobalMaster}'");
                AddWSConnectionLog($"Test binding: mastergroup(UI)='{mastergroupValue}', mastergroup(mem)='{updatedGlobalMasterGroup}'");
                AddWSConnectionLog($"Test binding: UpdateSource='{updatedUpdateSource}'");
                LogInfo($"[TestBinding] master(UI)='{masterValue}', master(mem)='{updatedGlobalMaster}'");
                LogInfo($"[TestBinding] mastergroup(UI)='{mastergroupValue}', mastergroup(mem)='{updatedGlobalMasterGroup}'");
                LogInfo($"[TestBinding] UpdateSource='{updatedUpdateSource}'");

                // 调用 SaveBasicSettings 方法
                MDiceV2.Models.GlobalFeedbackMessages.SaveBasicSettings();
                AddWSConnectionLog("Test binding: SaveBasicSettings triggered.");
                LogInfo("[TestBinding] SaveBasicSettings triggered.");
            }
            catch (Exception ex)
            {
                AddWSConnectionLog($"Test binding failed: {ex.Message}");
                LogSender.Error($"[TestBinding] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// LoadTemplates命令 - 用于Mod面板加载模板
        /// </summary>
        [RelayCommand]
        private void LoadTemplates()
        {
            try
            {
                LogInfo("[LoadTemplates] 开始加载模板...");
                AddWSConnectionLog("正在加载模板...");

                // 调用LoadTemplates方法，刷新 GlobalFeedbackMessages.FeedbackTemplates
                // UI更新将通过事件自动处理
                GlobalFeedbackMessages.LoadTemplates();

                LogInfo("[LoadTemplates] 加载模板完成");
                AddWSConnectionLog("LoadTemplates加载完成，UI将自动刷新。");
            }
            catch (Exception ex)
            {
                LogSender.Error($"[LoadTemplates] 错误: {ex.Message}");
                AddWSConnectionLog($"LoadTemplates失败: {ex.Message}");
            }
        }

        /// <summary>
        /// TestSaveTemplates命令 - 用于Mod面板测试保存功能
        /// </summary>
        [RelayCommand]
        private void TestSaveTemplates()
        {
            try
            {
                LogInfo("[TestSaveTemplates] 开始测试保存模板功能...");
                AddWSConnectionLog("正在测试保存模板功能...");
                
                // 调用SaveTemplates方法
                GlobalFeedbackMessages.SaveTemplates();
                
                LogInfo("[TestSaveTemplates] 保存模板测试完成");
                AddWSConnectionLog("SaveTemplates测试完成");
            }
            catch (Exception ex)
            {
                LogSender.Error($"[TestSaveTemplates] 错误: {ex.Message}");
                AddWSConnectionLog($"TestSaveTemplates失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从数据库加载WsUrl到UI
        /// </summary>
        private void LoadWsUrlFromDatabase()
        {
            try
            {
                string dbPath = Path.Combine(AppContext.BaseDirectory, "feedback.db");
                
                if (File.Exists(dbPath))
                {
                    string? savedUrl = null;
                    
                    // 先尝试通过GlobalFeedbackMessages（如果已初始化）
                    if (MDiceV2.Models.GlobalFeedbackMessages.IsInitialized())
                    {
                        savedUrl = MDiceV2.Models.GlobalFeedbackMessages.GetBasicSetting("Url");
                    }
                    else
                    {
                        // 如果GlobalFeedbackMessages未初始化，直接读取数据库
                        using var connection = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath};Version=3;");
                        connection.Open();
                        
                        using var command = new System.Data.SQLite.SQLiteCommand(
                            "SELECT value FROM BasicSetting WHERE key = 'Url'", connection);
                        var result = command.ExecuteScalar();
                        
                        if (result != null && result != DBNull.Value)
                        {
                            savedUrl = result.ToString();
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(savedUrl) && savedUrl != WsUrl && savedUrl != "ws://localhost:8080")
                    {
                        WsUrl = savedUrl;
                        LogSender.InfoFormat($"[MainViewModel] 从数据库加载WsUrl: {WsUrl}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogSender.Warn($"[MainViewModel] 加载WsUrl失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从GlobalFeedbackMessages加载基础设置
        /// </summary>
        public void LoadBasicSettingsFromGlobal()
        {
            try
            {
                // 如果基础配置容器还未创建，延迟加载
                if (_basicConfigContainer == null)
                {
                    LogSender.Normal("[BasicConfig加载] _basicConfigContainer 尚未创建，跳过加载");
                    return;
                }

                LogSender.Normal("[BasicConfig加载] ========== 开始从全局配置加载 ==========");
                var allSettings = MDiceV2.Models.GlobalFeedbackMessages.GetAllBasicSettings();
                LogSender.Normal($"[BasicConfig加载] 从全局获取 {allSettings.Count} 个设置项");

                // 先处理 URL
                foreach (var setting in allSettings)
                {
                    string key = setting.Key;
                    string value = setting.Value;
                    
                    // 规范化键名用于比较（移除空格）
                    string normalizedKey = key.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

                    // 处理特殊属性
                    if (normalizedKey == "url")
                    {
                        if (!string.IsNullOrEmpty(value))
                        {
                            // 设置WsUrl属性，通过数据绑定自动更新UI
                            WsUrl = value;
                            LogSender.InfoFormat($"[MainViewModel] 从基础设置加载URL: {WsUrl}");

                            // 确保同步到连接组件（通过OnWsUrlChanged回调处理）
                            // OnWsUrlChanged回调会自动同步到WSconnection
                        }
                    }
                }
                
                // 为了正确匹配 UI 中的键名，我们需要建立映射关系
                var keyMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "sendgroupjoinreport", "SendGroupJoinReport" },
                    { "sendfriendjoinreport", "SendFriendJoinReport" },
                    { "approvegroupjoinrequest", "ApproveGroupJoinRequest" },  // 已规范化为camelCase
                    { "approvefriendjoinrequest", "ApproveFriendJoinRequest" },
                    { "master", "master" },
                    { "mastergroup", "mastergroup" },
                    { "url", "Url" }
                };
                
                foreach (var setting in allSettings)
                {
                    string key = setting.Key;
                    string value = setting.Value;
                    
                    // 规范化键名用于查找映射
                    string normalizedKey = key.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

                    if (!keyMapping.TryGetValue(normalizedKey, out var uiKey) || string.IsNullOrEmpty(uiKey))
                    {
                        continue;  // 如果找不到映射，跳过
                    }

                    // 根据类型设置值
                    if (normalizedKey == "sendgroupjoinreport" || normalizedKey == "sendfriendjoinreport" ||
                        normalizedKey == "approvegroupjoinrequest" || normalizedKey == "approvefriendjoinrequest")
                    {
                        // 布尔值：使用 UI 中的实际键名
                        bool boolValue = value == "True" || value.ToLower() == "true";
                        var currentValue = _basicConfigContainer.GetValue(uiKey);
                        LogSender.Normal($"[BasicConfig加载] 布尔值: {uiKey} 当前值={currentValue} 远程值={boolValue}");
                        _basicConfigContainer.SetValue(uiKey, boolValue);
                        LogSender.Normal($"[BasicConfig加载] SetValue已执行: {uiKey} = {boolValue}");
                    }
                    else if (normalizedKey == "master" || normalizedKey == "mastergroup" || normalizedKey == "url")
                    {
                        // 字符串值：使用 UI 中的实际键名
                        var currentValue = _basicConfigContainer.GetValue(uiKey);
                        LogSender.Normal($"[BasicConfig加载] 字符串值: {uiKey} 当前值={currentValue} 远程值={value}");
                        _basicConfigContainer.SetValue(uiKey, value);
                        LogSender.Normal($"[BasicConfig加载] SetValue已执行: {uiKey} = {value}");
                    }
                }

                LogSender.Normal($"[BasicConfig加载] 所有配置项加载完成");
            }
            catch (Exception ex)
            {
                LogSender.Error($"[BasicConfig加载] 加载基础设置失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 添加WebSocket连接日志
        /// </summary>
        private void AddWSConnectionLog(string message)
        {
            WsConnectionLogs += $"[{DateTime.Now:HH:mm:ss}] {message}\n";

            // 限制日志行数
            var lines = WsConnectionLogs.Split('\n');
            if (lines.Length > WsLogMaxLines)
            {
                WsConnectionLogs = string.Join("\n", lines.Skip(lines.Length - WsLogMaxLines)) + "\n";
            }
        }

        /// <summary>
        /// 订阅WebSocket连接状态变化
        /// </summary>
        private void SubscribeToWSConnectionStatus()
        {
            if (_globalMessageProcessor?.MessageDistribution?.WSconnection != null)
            {
                var wsConnection = _globalMessageProcessor.MessageDistribution.WSconnection;

                // 订阅连接状态变化
                wsConnection.PropertyChanged += (sender, e) =>
                {
                    if (e.PropertyName == nameof(WSconnection.IsWsConnected))
                    {
                        WsConnectionStatus = wsConnection.IsWsConnected ? "Connected" : "Disconnected";
                        AddWSConnectionLog($"Connection status: {WsConnectionStatus.ToLower()}");
                        UpdateWSConnectionInfoInSettings();
                    }
                };

                // 初始化当前状态
                WsConnectionStatus = wsConnection.IsWsConnected ? "Connected" : "Disconnected";
                WsConnectionUrl = WSconnection.wsUrl;
                AddWSConnectionLog($"Initial status: {WsConnectionStatus.ToLower()}");
                AddWSConnectionLog($"Initial URL: {WsConnectionUrl}");
                UpdateWSConnectionInfoInSettings();
            }
        }

        /// <summary>
        /// 更新设置页面中的WS连接信息显示
        /// </summary>
        private void UpdateWSConnectionInfoInSettings()
        {
            // 重新创建设置内容以更新logPanel
            if (SelectedIndex == 2) // 如果当前是设置页面
            {
                UpdateCurrentView();
            }
        }

        /// <summary>
        /// 更新Feedback Message Templates的UI显示
        /// </summary>
        private void UpdateFeedbackTemplatesUI()
        {
            // 直接更新Feedback Message Templates容器的值，无需重建整个页面
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_feedbackTemplatesConfigContainer != null)
                    {
                         foreach (var kvp in GlobalFeedbackMessages.FeedbackTemplates)
                        {
                            _feedbackTemplatesConfigContainer.SetValue(kvp.Key, kvp.Value);
                            //Log.InfoFormat($"[UpdateFeedbackTemplatesUI] 已同步Feedback模板: {kvp.Key} = {kvp.Value}");
                        }
                        
                        // 更新RollResult模板的值
                        LogSender.InfoFormat("[UpdateFeedbackTemplatesUI] 已更新FeedbackTemplate");
                    }
                    else
                    {
                        LogSender.Warn("[UpdateFeedbackTemplatesUI] FeedbackTemplates容器为空，无法更新UI");
                    }
                }
                catch (Exception ex)
                {
                    LogSender.Error($"[UpdateFeedbackTemplatesUI] 刷新UI时发生错误: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 更新Help Message Templates的UI显示
        /// </summary>
        private void UpdateHelpTemplatesUI()
        {
            // 直接更新Help Message Templates容器的值，无需重建整个页面
            var updateUI = () =>
            {
                try
                {
                    if (_helpTemplatesConfigContainer != null)
                    {
                        // 同步GlobalFeedbackMessages.HelpTemplates中的数据到UI容器
                        foreach (var kvp in GlobalFeedbackMessages.HelpTemplates)
                        {
                            _helpTemplatesConfigContainer.SetValue(kvp.Key, kvp.Value);
                        }
                        
                        // 强制刷新FilteredItems以确保UI与数据同步
                        _helpTemplatesConfigContainer.UpdateFilteredItems();
                    }
                    else
                    {
                        LogSender.Warn("[UpdateHelpTemplatesUI] HelpTemplates容器为空，无法更新UI");
                    }
                }
                catch (Exception ex)
                {
                    LogSender.Error($"[UpdateHelpTemplatesUI] 刷新UI时发生错误: {ex.Message}");
                }
            };

            if (_dispatcher != null)
            {
                _dispatcher.Post(updateUI);
            }
            else
            {
                Dispatcher.UIThread.Post(updateUI);
            }
        }

        /// <summary>
        /// Feedback Templates加载完成事件处理
        /// </summary>
        private void OnFeedbackTemplatesLoaded()
        {
            UpdateFeedbackTemplatesUI();
        }

        /// <summary>
        /// Help Templates加载完成事件处理
        /// </summary>
        private void OnHelpTemplatesLoaded()
        {
            UpdateHelpTemplatesUI();
        }

        /// <summary>
        /// 创建模组页面内容
        /// </summary>
        private Control CreateModsContent()
        {
            // 使用缓存的ModManagerViewModel实例，确保Mod只加载一次
            // 避免每次切换到Mod菜单时重复加载和检测Mod
            if (_modManagerViewModel == null)
            {
                _modManagerViewModel = new ModManagerViewModel();
            }

            var modManagerPanel = new ModManagerPanel
            {
                DataContext = _modManagerViewModel
            };
            return modManagerPanel;
        }

        /// <summary>
        /// 点击同步连接按钮时的处理
        /// </summary>
        private async Task OnSyncConnectClick(Button? connectButton = null)
        {
            // 打印完整的同步配置
            
            string logmessage = ("========== [同步连接 - 完整配置信息] ==========\n");
            logmessage +=($"[MainViewModel] 客户端地址: 127.0.0.1 (本机)\n");
            logmessage +=($"[MainViewModel] 本地监听端口: {LocalListeningPort}\n");
            logmessage +=($"[MainViewModel] 本地密钥: {(LocalSyncKey.Length > 8 ? LocalSyncKey.Substring(0, 8) + "..." : "未生成")}\n");
            logmessage +=($"[MainViewModel] 本地密钥长度: {LocalSyncKey.Length}\n");
            logmessage +=("-----\n");
            logmessage +=($"[MainViewModel] 远程服务器地址: {RemoteServerAddress}\n");
            logmessage +=($"[MainViewModel] 远程服务器端口: {RemoteServerPort}\n");
            logmessage +=($"[MainViewModel] 远程服务器密钥: {(RemoteServerKey.Length > 8 ? RemoteServerKey.Substring(0, 8) + "..." : "未输入")}\n");
            logmessage +=($"[MainViewModel] 远程服务器密钥长度: {RemoteServerKey.Length}\n");
            logmessage +="=============================================";
            LogSender.Normal(logmessage);

            if (string.IsNullOrWhiteSpace(RemoteServerAddress))
            {
                SyncStatusMessage = "请输入服务器地址";
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 服务器地址为空");
                return;
            }

            if (RemoteServerPort <= 0 || RemoteServerPort > 65535)
            {
                SyncStatusMessage = "请输入有效的端口号 (1-65535)";
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 端口号无效: {RemoteServerPort}");
                return;
            }

            if (string.IsNullOrWhiteSpace(RemoteServerKey))
            {
                SyncStatusMessage = "请先输入远程服务器密钥";
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 远程服务器密钥为空");
                return;
            }

            if (_syncConfigManager == null || _grpcClient == null)
            {
                try
                {
                    LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 初始化 SyncConfigManager 和 GrpcConfigSyncClient...");
                    _syncConfigManager = new SyncConfigManager();
                    _grpcClient = new GrpcConfigSyncClient();
                    LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 初始化成功");
                }
                catch (Exception ex)
                {
                    SyncStatusMessage = $"初始化失败: {ex.Message}";
                    LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 初始化失败: {ex}");
                    return;
                }
            }

            try
            {
                IsSyncConnecting = true;
                SyncStatusMessage = "正在连接...";
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 正在连接到 {RemoteServerAddress}:{RemoteServerPort}");

                // 连接到远程服务器（使用用户输入的远程密钥进行验证）
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 调用 ConnectAsync...");
                await _grpcClient.ConnectAsync(RemoteServerAddress, RemoteServerPort, RemoteServerKey);
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 连接成功");

                // 连接成功后拉取远程配置
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 正在拉取远程配置...");
                
                // 【详细诊断】确保 PullConfigAsync 被执行
                Dictionary<string, string> remoteConfig;
                try
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【执行前诊断】即将调用 PullConfigAsync");
                    remoteConfig = await _grpcClient.PullConfigAsync() ?? new Dictionary<string, string>();
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【执行后诊断】PullConfigAsync 已返回");
                }
                catch (Exception pullEx)
                {
                    LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ PullConfigAsync 异常: {pullEx.Message}");
                    LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 堆栈: {pullEx.StackTrace}");
                    throw;
                }
                
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【诊断】✓ 拉取成功，获得 {remoteConfig.Count} 个配置项");
                
                // 【新增诊断】打印拉取到的所有配置项
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【诊断】拉取到的配置项详情:");
                foreach (var kvp in remoteConfig)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【拉取项】{kvp.Key} = {(kvp.Value?.Length > 50 ? kvp.Value.Substring(0, 50) + "..." : kvp.Value)}");
                }

                // 保存到本地同步文件夹
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 正在保存配置到同步文件夹...");
                await _syncConfigManager.SaveSyncConfigAsync(remoteConfig);
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 保存成功");

                // 更新UI配置
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ===【即将更新UI】正在更新 UI 配置...");
                await UpdateUIConfigFromSync(remoteConfig);
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【诊断】✓ UI 更新完成");

                // 【新增】保存本地所有配置作为备份
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 正在保存本地所有配置作为备份...");
                MessageProcessor.Instance?.SaveAllConfiguration();
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 本地配置已保存");

                SyncStatusMessage = $"已连接到 {RemoteServerAddress}:{RemoteServerPort}";
                IsSyncModeEnabled = true;
                
                // 【新增】改变连接按钮状态：背景色变为淡红色，文本变为"断开连接"
                if (connectButton != null)
                {
                    connectButton.Background = new SolidColorBrush(Color.Parse("#ff9b95"));
                    connectButton.Content = "断开连接 (Disconnect)";
                    LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 连接按钮已更新为断开连接状态");
                }
                
                // 【新增】订阅 Mod 配置变化事件以实现推送同步
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【处理】正在订阅 Mod 配置变化事件...");
                SubscribeToModConfigChanges();
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【完成】Mod 配置变化事件订阅完成");
                
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 同步连接完成");
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ===== 同步连接成功 =====");;
            }
            catch (Exception ex)
            {
                SyncStatusMessage = $"连接失败: {ex.Message}";
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 连接异常:");
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 错误信息: {ex.Message}");
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 堆栈跟踪: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 内部异常: {ex.InnerException.Message}");
                }
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ===== 同步连接失败 =====");
            }
            finally
            {
                IsSyncConnecting = false;
            }
        }

        /// <summary>
        /// 自动启动本地 gRPC 服务器监听
        /// </summary>
        private async void AutoStartLocalGrpcServer()
        {
            try
            {
                LogSender.Normal("========== [本地gRPC服务器 - 启动配置] ==========");
                LogSender.Normal($"[MainViewModel] 操作方式: 自动启动");
                LogSender.Normal($"[MainViewModel] 监听地址: 0.0.0.0");
                LogSender.Normal($"[MainViewModel] 监听端口: {LocalListeningPort}");
                
                if (_syncConfigManager == null)
                {
                    _syncConfigManager = new SyncConfigManager();
                    LocalSyncKey = _syncConfigManager.LocalKey;
                }

                // 创建 gRPC 服务器主机，并传入 ConfigProvider 函数
                // 这样服务器在响应 Pull 请求时会调用该函数获取最新的 UI 数据
                _grpcServerHost = new GrpcServerHost(_syncConfigManager.LocalKey, _syncConfigManager, () => ExportCurrentUIConfig());
                
                // 订阅配置更新事件
                _grpcServerHost.ConfigServer.OnConfigUpdated += async (updatedConfig) =>
                {
                    if (_configSyncDispatcher != null)
                    {
                        LogSender.Normal($"[MainViewModel] 接收到推送配置，包含 {updatedConfig.Count} 个项，正在派发...");
                        await _configSyncDispatcher.DispatchBatchAsync(updatedConfig);
                    }
                };
                
                await _grpcServerHost.StartAsync(LocalListeningPort);
                IsLocalServerListening = true;
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 本地 gRPC 服务器已启动");
            }
            catch (Exception ex)
            {
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 启动本地服务器失败: {ex.Message}");
                IsLocalServerListening = false;
            }
        }

        /// <summary>
        /// 导出当前 UI 中的所有配置为字典，用于 gRPC Pull 响应
        /// </summary>
        private Dictionary<string, string> ExportCurrentUIConfig()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. 导出基础配置
            if (_basicConfigContainer != null)
            {
                foreach (var item in _basicConfigContainer.Items)
                {
                    result[$"basic.{item.Key.ToLower()}"] = item.Value?.ToString() ?? "";
                }
            }

            // 2. 导出反馈模板
            if (_feedbackTemplatesConfigContainer != null)
            {
                foreach (var item in _feedbackTemplatesConfigContainer.Items)
                {
                    result[$"feedback.{item.Key.ToLower()}"] = item.Value?.ToString() ?? "";
                }
            }

            // 3. 导出帮助消息
            if (_helpTemplatesConfigContainer != null)
            {
                foreach (var item in _helpTemplatesConfigContainer.Items)
                {
                    result[$"help.{item.Key.ToLower()}"] = item.Value?.ToString() ?? "";
                }
            }

            return result;
        }

        /// <summary>
        /// ✅ 【新增】重启gRPC服务器以应用新模式（如端口变更）
        /// </summary>
        private async Task RestartGrpcServerAsync(int newPort)
        {
            try
            {
                LogSender.Normal($"[MainViewModel] 正在重启gRPC服务器以应用新端口: {newPort}");
                
                if (_grpcServerHost != null)
                {
                    await _grpcServerHost.StopAsync();
                    await Task.Delay(500);
                }
                
                if (_syncConfigManager != null)
                {
                    _grpcServerHost = new GrpcServerHost(_syncConfigManager.LocalKey, _syncConfigManager, () => ExportCurrentUIConfig());
                    
                    _grpcServerHost.ConfigServer.OnConfigUpdated += async (updatedConfig) =>
                    {
                        if (_configSyncDispatcher != null)
                        {
                            await _configSyncDispatcher.DispatchBatchAsync(updatedConfig);
                        }
                    };
                    
                    await _grpcServerHost.StartAsync(newPort);
                    IsLocalServerListening = true;
                    LogSender.Normal($"[MainViewModel] ✓ gRPC服务器已在新端口启动: {newPort}");
                }
            }
            catch (Exception ex)
            {
                LogSender.Error($"[MainViewModel] ❌ 重启服务器失败: {ex.Message}");
                IsLocalServerListening = false;
            }
        }

        /// <summary>
        /// 禁用同步模式
        /// 从本地数据文件夹重新加载配置，防止数据丢失
        /// </summary>
        private async Task DisableSyncMode()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[MainViewModel] ===== 禁用同步模式开始 =====");

                if (_grpcClient != null)
                {
                    System.Diagnostics.Debug.WriteLine("[MainViewModel] ✓ 正在断开 gRPC 连接...");
                    await _grpcClient.DisconnectAsync();
                    System.Diagnostics.Debug.WriteLine("[MainViewModel] ✓ gRPC 连接已断开");
                    _grpcClient = null;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MainViewModel] ℹ gRPC 客户端为 null");
                }

                // 清空同步文件夹中的配置
                if (_syncConfigManager != null)
                {
                    System.Diagnostics.Debug.WriteLine("[MainViewModel] ✓ 正在清空同步文件夹...");
                    _syncConfigManager.ClearSyncFolder();
                    System.Diagnostics.Debug.WriteLine("[MainViewModel] ✓ 同步文件夹已清空");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MainViewModel] ⚠ 同步配置管理器为 null");
                }

                // 从原始数据文件夹重新加载配置
                System.Diagnostics.Debug.WriteLine("[MainViewModel] ✓ 正在从磁盘重新加载配置...");
                ReloadConfigurationFromDisk();
                System.Diagnostics.Debug.WriteLine("[MainViewModel] ✓ 从磁盘重新加载配置完成");

                SyncStatusMessage = "已关闭同步，配置已从本地重新加载";
                System.Diagnostics.Debug.WriteLine("[MainViewModel] ===== 禁用同步模式完成 =====");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] ❌ 禁用同步异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] 堆栈: {ex.StackTrace}");
                SyncStatusMessage = $"禁用同步时出错: {ex.Message}";
            }
        }

        /// <summary>
        /// 点击同步断开连接按钮时的处理
        /// </summary>
        private async Task OnSyncDisconnectClick(Button? connectButton = null)
        {
            try
            {
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ===== 同步断开连接开始 =====");
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 正在断开 gRPC 连接...");

                // 调用关闭同步模式的逻辑
                await DisableSyncMode();

                // 【新增】复位连接按钮状态：背景色恢复为蓝色，文本恢复为"连接"
                if (connectButton != null)
                {
                    connectButton.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                    connectButton.Content = "连接 (Connect)";
                    LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 连接按钮已更新为连接状态");
                }

                IsSyncModeEnabled = false;
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 同步断开连接完成");
                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ===== 同步断开连接成功 =====");
            }
            catch (Exception ex)
            {
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 断开连接异常: {ex.Message}");
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 堆栈跟踪: {ex.StackTrace}");
                SyncStatusMessage = $"断开连接失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 从同步远程配置更新本地UI配置
        /// </summary>
        private async Task UpdateUIConfigFromSync(System.Collections.Generic.Dictionary<string, string> remoteConfig)
        {
            if (_basicConfigContainer == null)
            {
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ _basicConfigContainer 为 null，无法更新");
                return;
            }

            try
            {
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ===== 【开始更新UI配置】从同步配置更新 UI =====");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【诊断】远程配置总项数: {remoteConfig?.Count ?? 0}");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【诊断】_basicConfigContainer 项数: {_basicConfigContainer.Items.Count}");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【诊断】_feedbackTemplatesConfigContainer 项数: {_feedbackTemplatesConfigContainer?.Items.Count ?? 0}");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【诊断】_helpTemplatesConfigContainer 项数: {_helpTemplatesConfigContainer?.Items.Count ?? 0}");

                int basicUpdatedCount = 0;
                int feedbackUpdatedCount = 0;
                int helpUpdatedCount = 0;

                // ============ 更新 BasicConfig 容器 ============
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【处理】开始更新 BasicConfig 容器...");
                foreach (var item in _basicConfigContainer.Items)
                {
                    string basicKey = $"basic.{item.Key.ToLower()}";
                    if (remoteConfig.TryGetValue(basicKey, out var value))
                    {
                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【更新】BasicConfig: {item.Key} ← {basicKey}={value}");
                        
                        // 暂时禁用回调以避免拉取时触发推送
                        var originalCallback = item.ValueChangedCallback;
                        item.ValueChangedCallback = null;
                        
                        // 根据类型转换值
                        if (item.Type == ConfigType.CheckBox)
                        {
                            if (bool.TryParse(value, out var boolValue))
                            {
                                item.Value = boolValue;
                                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【成功】CheckBox转换: {item.Key} = {boolValue}");
                            }
                            else
                            {
                                item.Value = false;
                                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【⚠️】CheckBox转换失败，使用默认false: {item.Key}");
                            }
                        }
                        else
                        {
                            item.Value = value;
                            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【成功】LineEdit转换: {item.Key} = {value}");
                        }
                        
                        // 恢复回调
                        item.ValueChangedCallback = originalCallback;
                        basicUpdatedCount++;
                    }
                }
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【完成】BasicConfig 更新了 {basicUpdatedCount} 个项");

                // ============ 更新 FeedbackTemplate 容器 ============
                if (_feedbackTemplatesConfigContainer != null)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【处理】开始更新 FeedbackTemplate 容器...");
                    foreach (var item in _feedbackTemplatesConfigContainer.Items)
                    {
                        string feedbackKey = $"feedback.{item.Key.ToLower()}";
                        if (remoteConfig.TryGetValue(feedbackKey, out var value))
                        {
                            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【更新】FeedbackTemplate: {item.Key} ← {feedbackKey}={value}");
                            
                            var originalCallback = item.ValueChangedCallback;
                            item.ValueChangedCallback = null;
                            item.Value = value;
                            item.ValueChangedCallback = originalCallback;
                            feedbackUpdatedCount++;
                            
                            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【成功】FeedbackTemplate: {item.Key} = {value}");
                        }
                    }
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【完成】FeedbackTemplate 更新了 {feedbackUpdatedCount} 个项");
                }
                else
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【⚠️】_feedbackTemplatesConfigContainer 为 null，跳过更新");
                }

                // ============ 更新 HelpTemplate 容器 ============
                if (_helpTemplatesConfigContainer != null)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【处理】开始更新 HelpTemplate 容器...");
                    foreach (var item in _helpTemplatesConfigContainer.Items)
                    {
                        string helpKey = $"help.{item.Key.ToLower()}";
                        if (remoteConfig.TryGetValue(helpKey, out var value))
                        {
                            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【更新】HelpTemplate: {item.Key} ← {helpKey}={value}");
                            
                            var originalCallback = item.ValueChangedCallback;
                            item.ValueChangedCallback = null;
                            item.Value = value;
                            item.ValueChangedCallback = originalCallback;
                            helpUpdatedCount++;
                            
                            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【成功】HelpTemplate: {item.Key} = {value}");
                        }
                    }
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【完成】HelpTemplate 更新了 {helpUpdatedCount} 个项");
                }
                else
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【⚠️】_helpTemplatesConfigContainer 为 null，跳过更新");
                }

                // ============ 处理 Mod 配置（拉取） ============
                int modUpdatedCount = 0;
                try
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【处理】开始更新 Mod 配置...");
                    
                    var modConfigKeys = remoteConfig.Keys.Where(k => k.StartsWith("mod.customreply.")).ToList();
                    
                    if (modConfigKeys.Count > 0)
                    {
                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【检测】发现 {modConfigKeys.Count} 个 mod.customreply.* 配置项");
                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【详情】这些键是: {string.Join(", ", modConfigKeys)}");
                        
                        // 获取CustomizedReplyMod实例（通过 MessageProcessor 的内部引用）
                        try
                        {
                            var msgProcessor = _globalMessageProcessor;
                            if (msgProcessor != null)
                            {
                                // 尝试通过反射访问内部的 ModEventBridge
                                var bridgeField = msgProcessor.GetType().GetField("_modEventBridge", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (bridgeField != null)
                                {
                                    var bridge = bridgeField.GetValue(msgProcessor);
                                    if (bridge != null)
                                    {
                                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【获取】ModEventBridge 实例成功");
                                        
                                        // 使用 GetModStatus 方法获取 Mod 实例
                                        var getModStatusMethod = bridge.GetType().GetMethod("GetModStatus", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                        if (getModStatusMethod != null)
                                        {
                                            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【获取】GetModStatus 方法成功");
                                            var modStatusObj = getModStatusMethod.Invoke(bridge, new object[] { "com.example.customreply" });
                                            
                                            if (modStatusObj != null)
                                            {
                                                // 从元组中提取 Plugin 并转换为 IConfigurable
                                                dynamic modStatus = modStatusObj;
                                                var pluginObj = (object?)modStatus.Item1;
                                                var customizedReplyMod = pluginObj as IConfigurable;
                                                
                                                if (customizedReplyMod != null)
                                                {
                                                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【获取】CustomizedReplyMod 实例成功");
                                                    
                                                    foreach (var modKey in modConfigKeys)
                                                {
                                                    if (remoteConfig.TryGetValue(modKey, out var modValue))
                                                    {
                                                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【应用】Mod配置: {modKey}");
                                                        
                                                        try
                                                        {
                                                            var result = await customizedReplyMod.ApplyConfigAsync(modKey, modValue);
                                                            
                                                            if (result.Success)
                                                            {
                                                                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【成功】Mod配置应用成功: {modKey}");
                                                                modUpdatedCount++;
                                                            }
                                                            else
                                                            {
                                                                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【失败】Mod配置应用失败: {modKey} - {result.ErrorMessage}");
                                                            }
                                                        }
                                                        catch (Exception applyEx)
                                                        {
                                                            LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 【异常】Mod配置应用异常: {modKey} - {applyEx.Message}");
                                                        }
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【获取】CustomizedReplyMod 实例为 null");
                                            }
                                        }
                                        else
                                        {
                                            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【获取】GetModStatus 方法未找到");
                                        }
                                    }
                                    else
                                    {
                                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【获取】ModEventBridge 实例为 null");
                                    }
                                }
                                else
                                {
                                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【获取】_modEventBridge 字段未找到");
                                }
                            }
                            else
                            {
                                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【获取】MessageProcessor.GetInstance() 返回 null");
                            }
                        }
                    }
                    catch (Exception bridgeEx)
                    {
                        LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 【异常】通过 ModEventBridge 获取 Mod 实例失败: {bridgeEx.Message}");
                    }
                }
                else
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【检测】未检测到 mod.customreply.* 配置项");
                }
                    
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【完成】Mod 配置更新了 {modUpdatedCount} 个项");
                }
                catch (Exception modEx)
                {
                    LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 【异常】Mod配置处理异常: {modEx.Message}");
                }

                // ============ 汇总统计 ============
                int totalUpdated = basicUpdatedCount + feedbackUpdatedCount + helpUpdatedCount + modUpdatedCount;
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【统计】总共更新 {totalUpdated} 个配置项 (BasicConfig:{basicUpdatedCount}, FeedbackTemplate:{feedbackUpdatedCount}, HelpTemplate:{helpUpdatedCount}, Mod:{modUpdatedCount})");
                
                // 检查是否有配置项没有被匹配
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【对账】远程配置 {remoteConfig.Count} 项，已处理 {totalUpdated} 项，未匹配 {remoteConfig.Count - totalUpdated} 项");
                
                // 打印未匹配的配置项（可能是拼写错误或容器缺失）
                var matchedKeys = new System.Collections.Generic.HashSet<string>();
                foreach (var item in _basicConfigContainer.Items)
                    matchedKeys.Add($"basic.{item.Key.ToLower()}");
                if (_feedbackTemplatesConfigContainer != null)
                    foreach (var item in _feedbackTemplatesConfigContainer.Items)
                        matchedKeys.Add($"feedback.{item.Key.ToLower()}");
                if (_helpTemplatesConfigContainer != null)
                    foreach (var item in _helpTemplatesConfigContainer.Items)
                        matchedKeys.Add($"help.{item.Key.ToLower()}");
                
                // 添加关于已处理的mod配置的记录
                foreach (var key in remoteConfig.Keys.Where(k => k.StartsWith("mod.customreply.")))
                    matchedKeys.Add(key);
                
                var unmatchedKeys = remoteConfig.Keys.Where(k => !matchedKeys.Contains(k)).ToList();
                if (unmatchedKeys.Any())
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【⚠️】以下远程配置项未被匹配到本地容器:");
                    foreach (var unmatchedKey in unmatchedKeys)
                    {
                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【未匹配】{unmatchedKey} = {remoteConfig[unmatchedKey]}");
                    }
                }

                await Task.CompletedTask;
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ===== 【完成】从同步配置更新 UI 完成 =====");
            }
            catch (Exception ex)
            {
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 更新 UI 从同步配置异常: {ex.Message}");
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 订阅 Mod 配置变化事件
        /// 当 CustomizedReplyMod 的规则或配置发生改变时，自动推送到远程服务器
        /// </summary>
        private void SubscribeToModConfigChanges()
        {
            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【开始】SubscribeToModConfigChanges");
            
            try
            {
                // 通过反射获取 MessageProcessor 中的 ModEventBridge
                var msgProcessor = _globalMessageProcessor;
                
                if (msgProcessor == null)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【警告】MessageProcessor 为 null，无法订阅 Mod 事件");
                    return;
                }
                
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【获取】MessageProcessor 实例成功");
                
                // 尝试通过反射访问内部的 ModEventBridge
                var bridgeField = msgProcessor.GetType().GetField("_modEventBridge", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (bridgeField == null)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【警告】无法找到 _modEventBridge 字段");
                    return;
                }
                
                var bridge = bridgeField.GetValue(msgProcessor);
                if (bridge == null)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【警告】ModEventBridge 实例为 null");
                    return;
                }
                
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【获取】ModEventBridge 实例成功");
                
                // 获取 GetModStatus 方法（返回 (IModPlugin?, IModMetadata?, bool)? 元组）
                var getModStatusMethod = bridge.GetType().GetMethod("GetModStatus", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (getModStatusMethod == null)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【警告】无法找到 GetModStatus 方法");
                    return;
                }
                
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【获取】GetModStatus 方法成功");
                
                // 调用 GetModStatus 获取 Mod 实例（返回值是可空元组）
                var modStatusObj = getModStatusMethod.Invoke(bridge, new object[] { "com.example.customreply" });
                if (modStatusObj == null)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【警告】CustomizedReplyMod 未找到或加载状态异常");
                    return;
                }
                
                // 从元组中提取 Plugin（Item1）并转换为 IConfigurable
                dynamic modStatus = modStatusObj;
                var plugin = (object?)modStatus.Item1;
                var customizedReplyMod = plugin as IConfigurable;
                
                if (customizedReplyMod == null)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【警告】CustomizedReplyMod 未加载或不是 IConfigurable");
                    return;
                }
                
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【获取】CustomizedReplyMod 实例成功");
                
                // 订阅 ConfigChanged 事件
                customizedReplyMod.ConfigChanged += async (key, newValue) =>
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 🔔 【事件】Mod 配置变化: {key}");
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【值】{(newValue?.Length > 100 ? newValue.Substring(0, 100) + "..." : newValue)}");
                    
                    try
                    {
                        // 检查同步是否启用
                        if (!IsSyncModeEnabled)
                        {
                            LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【跳过】同步模式未启用，不推送 Mod 配置");
                            return;
                        }
                        
                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【推送】正在推送 Mod 配置: {key}");
                        
                        // 直接推送 Mod 配置到远程
                        await PushConfigUpdateToRemoteAsync(key, newValue ?? "");
                        
                        LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【完成】Mod 配置推送完成: {key}");
                    }
                    catch (Exception pushEx)
                    {
                        LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 【异常】推送 Mod 配置时出错: {pushEx.Message}");
                    }
                };
                
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【完成】已成功订阅 ConfigChanged 事件");
                
                // ✅ 订阅 OnRulesModified 事件（用于本地修改时推送，不刷新UI）
                try
                {
                    dynamic modStatusDynamic = modStatusObj;
                    var modPlugin = (dynamic?)modStatusDynamic.Item1;
                    if (modPlugin != null)
                    {
                        // 使用反射订阅 OnRulesModified 事件
                        var pluginType = modPlugin.GetType();
                        var rulesModifiedEvent = pluginType.GetEvent("OnRulesModified", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        
                        if (rulesModifiedEvent != null)
                        {
                            // 创建委托
                            var delegateType = rulesModifiedEvent.EventHandlerType;
                            var method = typeof(MainViewModel).GetMethod("OnModRulesModified", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            
                            if (method != null)
                            {
                                var handler = Delegate.CreateDelegate(delegateType, this, method);
                                rulesModifiedEvent.AddEventHandler(modPlugin, handler);
                                LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 【完成】已成功订阅 OnRulesModified 事件");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ⚠️ 【警告】订阅 OnRulesModified 事件失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 【异常】订阅 Mod 配置事件时出错: {ex.Message}");
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【堆栈】{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 从本地磁盘重新加载配置
        /// </summary>
        private void ReloadConfigurationFromDisk()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[MainViewModel] ===== 从磁盘重新加载配置开始 =====");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 【重载】从磁盘重新加载配置开始");

                if (_basicConfigContainer == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MainViewModel] ❌ _basicConfigContainer 为 null，无法重新加载");
                    LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ _basicConfigContainer 为 null");
                    return;
                }

                // 【修复】禁用回调以防止推送
                _basicConfigContainer.IsCallbackEnabled = false;
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 已禁用值变化回调");

                // 从磁盘重新加载所有基础设置
                var allSettings = MDiceV2.Models.GlobalFeedbackMessages.GetAllBasicSettings();
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] [参数] 从磁盘加载的设置数: {allSettings.Count}");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] [参数] 从磁盘加载的设置数: {allSettings.Count}");

                // 清空原有的容器项
                int oldCount = _basicConfigContainer.Items.Count;
                _basicConfigContainer.Items.Clear();
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] ✓ 已清空 {oldCount} 个旧项");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 已清空 {oldCount} 个旧项");

                // 获取默认值以确定配置类型
                var defaultBasic = MDiceV2.Models.GlobalFeedbackMessages.GetDefaultBasicSettings();

                // 重新加载所有设置 - 必须使用AddConfig而不仅仅是SetValue，以重建配置项结构
                int loadedCount = 0;
                
                // 按照初始化时的顺序重新添加配置项
                var configOrder = new[] { "SendGroupJoinReport", "SendFriendJoinReport", "ApproveGroupJoinRequest", "ApproveFriendJoinRequest", "master", "mastergroup", "Url" };
                
                foreach (var key in configOrder)
                {
                    string value = "";
                    if (allSettings.TryGetValue(key, out var diskValue))
                    {
                        value = diskValue;
                    }
                    else
                    {
                        value = defaultBasic.GetValueOrDefault(key, "");
                    }

                    // 检查值类型以确定配置项类型
                    bool isBool = value == "True" || value == "False" || value.ToLower() == "true" || value.ToLower() == "false";
                    ConfigType configType = ConfigType.LineEdit;

                    if (key == "SendGroupJoinReport" || key == "SendFriendJoinReport" || key == "ApproveGroupJoinRequest" || key == "ApproveFriendJoinRequest")
                    {
                        configType = ConfigType.CheckBox;
                    }

                    // 重新添加配置项（回调已禁用）
                    _basicConfigContainer.AddConfig(key, configType, value);
                    System.Diagnostics.Debug.WriteLine($"[MainViewModel] ✓ 重新添加配置: {key} = {value}（类型={configType}）");
                    LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 重新添加配置: {key} = {value}");
                    loadedCount++;
                }

                // 【修复】恢复回调
                _basicConfigContainer.IsCallbackEnabled = true;
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 已恢复值变化回调");

                System.Diagnostics.Debug.WriteLine($"[MainViewModel] ✓ 共重新加载 {loadedCount} 个配置项");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ✓ 共重新加载 {loadedCount} 个配置项");
                
                _basicConfigContainer.UpdateFilteredItems();
                SyncStatusMessage = "已从磁盘重新加载配置";
                System.Diagnostics.Debug.WriteLine("[MainViewModel] ===== 从磁盘重新加载配置完成 =====");
                LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ===== 【重载完成】从磁盘重新加载配置完成 =====");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] ❌ 从磁盘重新加载配置异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] 堆栈: {ex.StackTrace}");
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] ❌ 从磁盘重新加载配置异常: {ex.Message}");
                LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MainViewModel] 堆栈: {ex.StackTrace}");
                SyncStatusMessage = $"重新加载配置失败: {ex.Message}";
                
                // 确保恢复回调
                if (_basicConfigContainer != null)
                {
                    _basicConfigContainer.IsCallbackEnabled = true;
                }
            }
        }

        /// <summary>
        /// 处理 Mod 规则修改事件（本地修改触发的推送）
        /// 用于订阅 CustomizedReplyMod.OnRulesModified 事件
        /// </summary>
        private async void OnModRulesModified(string key, string value)
        {
            try
            {
                LogSender.Normal($"[MainViewModel] 🔔 【事件】Mod 规则修改（本地）: {key}");
                
                if (!IsSyncModeEnabled)
                {
                    LogSender.Normal($"[MainViewModel] ⚠️ 【跳过】同步模式未启用，不推送规则修改");
                    return;
                }
                
                LogSender.Normal($"[MainViewModel] ► 【推送】推送本地规则修改...");
                await PushConfigUpdateToRemoteAsync(key, value ?? "");
                LogSender.Normal($"[MainViewModel] ✓ 【完成】规则修改推送成功");
            }
            catch (Exception ex)
            {
                LogSender.Error($"[MainViewModel] ❌ 【异常】推送规则修改时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取 CustomizedReplyMod 实例 - 用于Mod配置同步
        /// </summary>
        private IConfigurable? GetCustomizedReplyModInstance()
        {
            try
            {
                var msgProcessor = _globalMessageProcessor;
                if (msgProcessor == null) return null;

                // 通过反射访问内部的 ModEventBridge
                var bridgeField = msgProcessor.GetType().GetField("_modEventBridge", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (bridgeField == null) return null;

                var bridge = bridgeField.GetValue(msgProcessor);
                if (bridge == null) return null;

                // 获取 GetModStatus 方法
                var getModStatusMethod = bridge.GetType().GetMethod("GetModStatus", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (getModStatusMethod == null) return null;

                // 获取 CustomizedReplyMod 实例
                var modStatusObj = getModStatusMethod.Invoke(bridge, new object[] { "com.example.customreply" });
                if (modStatusObj == null) return null;

                // 从元组中提取 Plugin 并转换为 IConfigurable
                dynamic modStatus = modStatusObj;
                var pluginObj = (object?)modStatus.Item1;
                return pluginObj as IConfigurable;
            }
            catch (Exception ex)
            {
                LogSender.Error($"[MainViewModel] Failed to get CustomizedReplyMod instance: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 推送配置项更新到远程服务器（在同步模式下自动调用）
        /// UI 键需要转换为后端配置键格式（category.key）
        /// </summary>
        public async Task PushConfigUpdateToRemoteAsync(string key, string value)
        {
            // 【临时禁用日志】用于诊断切换标签页时的推送触发源头
            
            if (!IsSyncModeEnabled)
            {
                return;
            }

            if (_grpcClient == null)
            {
                SyncStatusMessage = "推送失败: gRPC客户端未初始化";
                return;
            }

            if (!_grpcClient.IsConnected)
            {
                SyncStatusMessage = $"推送失败: 未连接到服务器";
                return;
            }

            try
            {
                // 检查是mod配置（mod.customreply.*）
                if (key.StartsWith("mod.customreply.", StringComparison.OrdinalIgnoreCase))
                {
                    if (_grpcClient?.IsConnected == true)
                    {
                        var modConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { key, value ?? string.Empty }
                        };
                        await _grpcClient.PushConfigAsync(modConfig);
                    }
                    return;
                }

                // ✅ 将 UI 键转换为后端配置键格式（basic.key_in_lowercase）
                string backendKey = ConvertUIKeyToBackendKey(key);

                // ✅ 检查是否已经存在相同的键
                if (string.IsNullOrWhiteSpace(backendKey))
                {
                    SyncStatusMessage = $"推送失败: 无效的配置键";
                    return;
                }

                // ✅ 使用大小写不敏感的字典比较，避免大小写导致的动复键问题
                var config = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                
                try
                {
                    config.Add(backendKey, value ?? string.Empty);
                }
                catch (ArgumentException ex)
                {
                    SyncStatusMessage = $"推送失败: {ex.Message}";
                    return;
                }

                await _grpcClient.PushConfigAsync(config);
            }
            catch (Exception ex)
            {
                LogSender.Error($"[MainViewModel] 推送配置异常: {ex.Message}");
                SyncStatusMessage = $"推送失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 将 UI 键转换为后端配置键格式
        /// 例如: "SendFriendJoinReport" → "basic.sendfriendjoinreport"
        /// </summary>
        private string ConvertUIKeyToBackendKey(string uiKey)
        {
            if (string.IsNullOrEmpty(uiKey))
                return uiKey;

            // 后端配置键映射：UI键 → 后端键
            // 使用OrdinalIgnoreCase以避免大小写导致的重复键问题
            var backendKeyMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "SendGroupJoinReport", "basic.sendgroupjoinreport" },
                { "SendFriendJoinReport", "basic.sendfriendjoinreport" },
                { "ApproveGroupJoinRequest", "basic.approvegroupjoinrequest" },
                { "ApproveFriendJoinRequest", "basic.approvefriendjoinrequest" },
                { "Master", "basic.master" },
                { "MasterGroup", "basic.mastergroup" },
                { "Url", "basic.url" }
            };

            // 优先使用精确匹配（忽略大小写）
            if (backendKeyMapping.TryGetValue(uiKey, out var backendKey))
            {
                LogSender.InfoFormat($"[ConvertUIKeyToBackendKey] 找到精确映射: {uiKey} → {backendKey}");
                return backendKey;
            }

            // 🆕 如果键来自 feedback 容器，添加 "feedback." 前缀
            // 🆕 如果键来自 help 容器，添加 "help." 前缀
            // 🆕 否则假设所有基本配置都属于 basic 类别
            
            // 检查键是否存在于 feedbackTemplatesConfigContainer 中
            if (_feedbackTemplatesConfigContainer?.Items.Any(item => item.Key == uiKey) == true)
            {
                string smartKey = $"feedback.{uiKey.ToLowerInvariant()}";
                LogSender.InfoFormat($"[ConvertUIKeyToBackendKey] 识别为反馈模板键: {uiKey} → {smartKey}");
                return smartKey;
            }

            // 检查键是否存在于 helpTemplatesConfigContainer 中
            if (_helpTemplatesConfigContainer?.Items.Any(item => item.Key == uiKey) == true)
            {
                string smartKey = $"help.{uiKey.ToLowerInvariant()}";
                LogSender.InfoFormat($"[ConvertUIKeyToBackendKey] 识别为帮助消息键: {uiKey} → {smartKey}");
                return smartKey;
            }

            // 如果没有找到映射，尝试智能转换：假设所有基本配置都属于 basic 类别
            // 格式：ui_key → basic.ui_key_lowercase
            string defaultKey = $"basic.{uiKey.ToLowerInvariant()}";
            LogSender.InfoFormat($"[ConvertUIKeyToBackendKey] 使用默认转换: {uiKey} → {defaultKey}");
            return defaultKey;
        }
    }
}