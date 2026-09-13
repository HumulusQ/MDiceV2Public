using Avalonia.Controls;
using Avalonia.Input;
using MDiceV2.Models;

namespace MDiceV2.Core.UI.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            Console.WriteLine("[MainWindow] ctor - InitializeComponent start");
            InitializeComponent();
            Console.WriteLine("[MainWindow] ctor - InitializeComponent end");

            this.Opened += (s, e) =>
            {
                Console.WriteLine("[MainWindow] Opened event - window should be visible now");
                try { this.Activate(); } catch { }
            };

            // Log and ensure data is saved when window is closing or closed
            this.Closing += (s, e) =>
            {
                Console.WriteLine("[MainWindow] Closing event fired - calling MessageProcessor.Dispose()");
                try
                {
                    // 检查同步模式是否启用
                    bool isSyncEnabled = false;
                    if (this.DataContext is MDiceV2.Core.UI.ViewModels.MainViewModel mainViewModel)
                    {
                        isSyncEnabled = mainViewModel.IsSyncModeEnabled;
                        if (isSyncEnabled)
                        {
                            Console.WriteLine("[MainWindow] Sync mode is enabled, will skip config save on dispose");
                        }
                        else
                        {
                            Console.WriteLine("[MainWindow] Sync mode is disabled, will save config on dispose");
                        }
                    }

                    // 传递 skipSave 参数：如果同步模式启用则跳过保存
                    MessageProcessor.Instance?.Dispose(skipSave: isSyncEnabled);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainWindow] Error disposing MessageProcessor during Closing: {ex}");
                }
            };

            this.Closed += (s, e) =>
            {
                Console.WriteLine("[MainWindow] Closed event fired - final cleanup (MessageProcessor.Dispose())");
                try
                {
                    MessageProcessor.Instance?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainWindow] Error disposing MessageProcessor during Closed: {ex}");
                }
            };
        }

        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void MinimizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Close();
        }
    }
}
