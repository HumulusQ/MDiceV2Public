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
                Console.WriteLine("[App] >>> ========== LoadAllMods START ==========");
                Log.Normal("[App] ========== LoadAllMods START ==========");
                
                string projectPath = Directory.GetCurrentDirectory();
                string modsPath = Path.Combine(projectPath, "mods");
                
                // 获取更多诊断信息
                var executingAsm = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var executingDir = Path.GetDirectoryName(executingAsm);
                var appContextBase = AppContext.BaseDirectory;

                Console.WriteLine($"[App] >>> ========== DIAGNOSTIC INFO ==========");
                Console.WriteLine($"[App] >>> Current directory: {projectPath}");
                Console.WriteLine($"[App] >>> Executing Assembly: {executingAsm}");
                Console.WriteLine($"[App] >>> Executing Directory: {executingDir}");
                Console.WriteLine($"[App] >>> AppContext.BaseDirectory: {appContextBase}");
                Console.WriteLine($"[App] >>> Mods path: {modsPath}");
                Console.WriteLine($"[App] >>> Mods directory exists: {Directory.Exists(modsPath)}");
                
                if (Directory.Exists(modsPath))
                {
                    var modFolders = Directory.GetDirectories(modsPath);
                    Console.WriteLine($"[App] >>> Found {modFolders.Length} mod folders in mods directory");
                    foreach (var folder in modFolders)
                    {
                        Console.WriteLine($"[App] >>>   - {Path.GetFileName(folder)}");
                    }
                }
                else
                {
                    Console.WriteLine($"[App] >>> WARNING: Mods directory does not exist!");
                }
                Console.WriteLine($"[App] >>> ========== END DIAGNOSTIC ==========");
                
                Log.Normal($"[App] Mods path: {modsPath}");

                // Create mods directory if it doesn't exist
                Directory.CreateDirectory(modsPath);

                // Get or create the singleton MessageDistribution instance
                var messageDistribution = MessageDistribution.GetInstance();
                
                var modContext = new ModContextImpl(messageDistribution, "core");

                // Create loader with the temporary context
                var loader = new ModPluginLoader(modsPath, modContext);
                Console.WriteLine("[App] >>> ModPluginLoader created");
                Log.Normal("[App] ModPluginLoader created");

                var loadedMods = loader.LoadAllMods();
                Console.WriteLine($"[App] >>> Loaded {loadedMods.Count} mods");
                Log.Normal($"[App] Loaded {loadedMods.Count} mods");

                // Create ModEventBridge and register mods
                var modEventBridge = new ModEventBridge(modContext);
                Log.Normal("[App] ModEventBridge created");

                // Store ModEventBridge reference for program exit handling
                _modEventBridge = modEventBridge;

                // Initialize each mod (call OnLoad method) and register to bridge
                foreach (var (plugin, metadata) in loadedMods)
                {
                    try
                    {
                        Console.WriteLine($"[App] >>> ========== OnLoad START for {metadata.Id} ==========");
                        Log.Normal($"[App] Initializing mod: {metadata.Id}");
                        Console.WriteLine($"[App] >>> About to call plugin.OnLoad() for {metadata.Id}");
                        plugin.OnLoad();
                        Console.WriteLine($"[App] >>> plugin.OnLoad() completed for {metadata.Id}");
                        
                        // Register mod to ModEventBridge
                        Console.WriteLine($"[App] >>> About to register mod to ModEventBridge: {metadata.Id}");
                        modEventBridge.RegisterMod(plugin, metadata, isEnabled: true);
                        Console.WriteLine($"[App] >>> Mod registered to ModEventBridge: {metadata.Id}");
                        
                        // Call OnEnable for enabled mods
                        Console.WriteLine($"[App] >>> About to call plugin.OnEnable() for {metadata.Id}");
                        plugin.OnEnable();
                        Console.WriteLine($"[App] >>> plugin.OnEnable() completed for {metadata.Id}");
                        
                        Console.WriteLine($"[App] >>> ========== OnLoad END for {metadata.Id} ==========");
                        Log.Normal($"[App] ✓ Mod {metadata.Id} initialized successfully");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[App] >>> EXCEPTION during mod initialization for {metadata.Id}: {ex.Message}");
                        Console.WriteLine($"[App] >>> StackTrace: {ex.StackTrace}");
                        Log.Error($"[App] ✗ Error initializing mod {metadata.Id}: {ex.Message}\n{ex.StackTrace}");
                    }
                }

                // Initialize all mods in ModEventBridge
                modEventBridge.InitializeAllMods();
                Log.Normal("[App] ModEventBridge.InitializeAllMods() called");
                
                // Set ModEventBridge to MessageProcessor for message handling
#pragma warning disable CS0618
                MessageProcessor.GetInstance().SetModEventBridge(modEventBridge);
#pragma warning restore CS0618
                Log.Normal("[App] ✓ ModEventBridge set to MessageProcessor");

                Log.Normal("[App] ✓✓✓ All mods loaded and initialized successfully");
                Log.Normal("[App] ========== LoadAllMods END ==========");
            }
            catch (Exception ex)
            {
                Log.Error($"[App] ✗ Error loading mods: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}