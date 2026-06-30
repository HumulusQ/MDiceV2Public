using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using MDiceV2.Core.UI.Views;
using MDiceV2.Models;
using MDiceV2.Core.Mod;
using System;
using System.IO;

namespace MDiceV2.Core.UI
{
    public class App : Application
    {
        /// <summary>
        /// ModEventBridge 的静态引用，用于程序退出时卸载所有 Mod
        /// </summary>
        private static ModEventBridge? _modEventBridge = null;
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Console.WriteLine("[App] OnFrameworkInitializationCompleted: ensuring GlobalMessageQueue and creating MainWindow");
                try
                {
                    // Ensure the GlobalMessageQueue singleton exists （ServiceBootstrapper 已创建，这是防护）
                    if (GlobalMessageQueue.Instance == null)
                    {
                        _ = new GlobalMessageQueue();
                        Console.WriteLine("[App] GlobalMessageQueue instance created");
                    }
                    else
                    {
                        Console.WriteLine("[App] GlobalMessageQueue instance already exists (initialized by ServiceBootstrapper)");
                    }

                    // Load all Mods before creating the main window
                    Console.WriteLine("[App] Loading mods...");
                    LoadAllMods();

                    var mw = new MainWindow();
                    desktop.MainWindow = mw;
                    // Ensure window is activated when shown
                    mw.Opened += (s, e) =>
                    {
                        Console.WriteLine("[App] MainWindow Opened event fired");
                        try { mw.Activate(); } catch { }
                    };

                    // Register application exit handler to ensure data is saved and resources are cleaned up
                    desktop.Exit += (s, e) =>
                    {
                        Log.Normal("[App] ========== Desktop Exit event fired ==========");
                        Log.Normal("[App] Starting cleanup: unloading mods and disposing MessageProcessor...");
                        try
                        {
                            // Unload all mods first (so they can save their state)
                            if (_modEventBridge != null)
                            {
                                Log.Normal("[App] Calling ModEventBridge.UnloadAllMods()...");
                                _modEventBridge.UnloadAllMods();
                                Log.Normal("[App] ✓ All mods unloaded successfully");
                            }
                            else
                            {
                                Log.Warn("[App] ModEventBridge is null, skipping mod unload");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[App] Error while unloading mods: {ex}");
                        }

                        try
                        {
                            Log.Normal("[App] Disposing MessageProcessor...");
                            MessageProcessor.Instance?.Dispose();
                            Log.Normal("[App] ✓ MessageProcessor disposed successfully");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[App] Error while disposing MessageProcessor: {ex}");
                        }

                        Log.Normal("[App] ========== Desktop Exit cleanup completed ==========");
                    };

                    // Also subscribe to process exit as a fallback when running without the desktop lifetime
                    AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                    {
                        Log.Normal("[App] ========== ProcessExit fired (fallback cleanup) ==========");
                        try
                        {
                            // Unload all mods first (so they can save their state)
                            if (_modEventBridge != null)
                            {
                                Log.Normal("[App] ProcessExit: Calling ModEventBridge.UnloadAllMods()...");
                                _modEventBridge.UnloadAllMods();
                                Log.Normal("[App] ✓ All mods unloaded on ProcessExit");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[App] Error during ProcessExit mod unload: {ex}");
                        }

                        try
                        {
                            Log.Normal("[App] ProcessExit: Disposing MessageProcessor...");
                            MessageProcessor.Instance?.Dispose();
                            Log.Normal("[App] ✓ MessageProcessor disposed on ProcessExit");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[App] Error during ProcessExit MessageProcessor disposal: {ex}");
                        }

                        Log.Normal("[App] ========== ProcessExit cleanup completed ==========");
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[App] Exception creating MainWindow: {ex}");
                    throw;
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// Load all mods from the mods directory and initialize them
        /// </summary>
        private void LoadAllMods()
        {
            try
            {
                var result = RuntimeModInitializer.InitializeModsForRuntime("UI");
                _modEventBridge = result.ModEventBridge;
                Log.Normal($"[App] ✓ Mod runtime initialized bridgeId={result.ModEventBridge.GetHashCode()} loaded={result.Mods.Count}");
            }
            catch (Exception ex)
            {
                Log.Error($"[App] ✗ Error loading mods: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
