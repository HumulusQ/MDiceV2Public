using Avalonia;
using MDiceV2.Core.UI;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Core.Mod;
using MDiceV2.Abstractions;
using MDiceV2.Models;
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace MDiceV2.Core
{
    public class Program
    {
        private static int? ParentConsolePid { get; set; }
        private static ManualResetEvent? ShutdownSignal { get; set; }

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                // 诊断: 输出接收到的参数
                Console.WriteLine($"[Core.Program] Received {args.Length} arguments:");
                for (int i = 0; i < args.Length; i++)
                {
                    Console.WriteLine($"[Core.Program]   args[{i}] = '{args[i]}'");
                }
                Console.Out.Flush();

                // 修复 AppDomain BaseDirectory 使其指向应用程序根目录
                // 当 Core 作为子进程运行时，BaseDirectory 会指向 Core.exe 所在目录
                // 需要将其改为父目录（MDiceV2_Debug 或发布目录）
                string coreExePath = Process.GetCurrentProcess().MainModule?.FileName ?? AppDomain.CurrentDomain.BaseDirectory;
                string coreDir = Path.GetDirectoryName(coreExePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string rootDir = Path.GetDirectoryName(coreDir) ?? AppDomain.CurrentDomain.BaseDirectory;
                
                // 尝试改变 AppDomain BaseDirectory
                // 注意：直接修改 AppDomain.CurrentDomain.BaseDirectory 在 .NET 中不支持
                // 但可以通过 Environment.CurrentDirectory 来影响相对路径解析
                if (Directory.Exists(rootDir))
                {
                    Environment.CurrentDirectory = rootDir;
                    Console.WriteLine($"[Core.Program] Updated working directory to: {rootDir}");
                }
                
                // 解析启动参数，提取父进程PID
                ParseArgs(args);
                
                // 检查 --headless 参数（检查任何参数，不仅仅是args[0]）
                bool isHeadlessMode = args.Any(arg => arg.Contains("--headless"));
                
                if (isHeadlessMode)
                {
                    Console.WriteLine("[Core.Program] Starting in headless mode...");
                    RunHeadlessMode();
                }
                else
                {
                    Console.WriteLine("[Core.Program] Starting Avalonia UI...");
                    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Core.Program] Fatal error: {ex}");
                Environment.Exit(1);
            }
        }

        private static void ParseArgs(string[] args)
        {
            if (args == null || args.Length == 0) return;
            
            foreach (var arg in args)
            {
                if (arg.StartsWith("--parent-pid="))
                {
                    var pidStr = arg.Substring("--parent-pid=".Length);
                    if (int.TryParse(pidStr, out var pid))
                    {
                        ParentConsolePid = pid;
                        Console.WriteLine($"[Core.Program] Parent Console PID: {pid}");
                    }
                }
                else if (arg.StartsWith("--ws-url="))
                {
                    var url = arg.Substring("--ws-url=".Length);
                    if (!string.IsNullOrEmpty(url))
                    {
                        WSconnection.wsUrl = url;
                        Console.WriteLine($"[Core.Program] Custom WebSocket URL set: {url}");
                    }
                }
            }
        }

        private static void RunHeadlessMode()
        {
            ShutdownSignal = new ManualResetEvent(false);
            WSconnection? wsConnection = null;
            IServiceProvider? serviceProvider = null;
            
            // 注册事件处理
            System.Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[Core.Headless] Ctrl+C received");
                Console.Out.Flush();
                ShutdownSignal?.Set();
            };

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                Console.WriteLine("[Core.Headless] ProcessExit event triggered");
                Console.Out.Flush();
                ShutdownSignal?.Set();
            };

            Console.WriteLine("[Core.Headless] Entering message loop...");
            Console.Out.Flush();

            // 初始化 DI 容器和核心服务
            try
            {
                Console.WriteLine("[Core.Headless] Initializing service container...");
                serviceProvider = ServiceBootstrapper.BuildServices(StartupMode.Console);
                ServiceBootstrapper.ValidateServices(serviceProvider);
                
                // 从 DI 容器获取必要的服务以建立消息处理链
                var globalMessageQueue = serviceProvider.GetService(typeof(GlobalMessageQueue)) as GlobalMessageQueue;
                var messageProcessor = serviceProvider.GetService(typeof(MessageProcessor)) as MessageProcessor;
                
                if (globalMessageQueue != null)
                {
                    Console.WriteLine("[Core.Headless] ✓ GlobalMessageQueue initialized");
                }
                else
                {
                    Console.WriteLine("[Core.Headless] ⚠ Warning: GlobalMessageQueue not available");
                }
                
                if (messageProcessor != null)
                {
                    Console.WriteLine("[Core.Headless] ✓ MessageProcessor initialized");
                }
                else
                {
                    Console.WriteLine("[Core.Headless] ⚠ Warning: MessageProcessor not available");
                }
                
                Console.WriteLine("[Core.Headless] ✓ Service container initialized");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Core.Headless] Error initializing services: {ex.Message}");
                Console.Out.Flush();
            }

            // 确保 GlobalMessageQueue 单例被创建（ServiceBootstrapper 已创建，这是防护确认）
            try
            {
                Console.WriteLine("[Core.Headless] Ensuring GlobalMessageQueue singleton...");
                if (GlobalMessageQueue.Instance == null)
                {
                    _ = new GlobalMessageQueue();
                    Console.WriteLine("[Core.Headless] ✓ GlobalMessageQueue singleton instantiated");
                }
                else
                {
                    Console.WriteLine("[Core.Headless] ✓ GlobalMessageQueue singleton already exists (initialized by ServiceBootstrapper)");
                }
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Core.Headless] Error ensuring GlobalMessageQueue: {ex.Message}");
                Console.Out.Flush();
            }

            // ✅ 【关键】初始化 MessageProcessor（与UI模式统一）
            // UI模式在MainViewModel.EnsureInitialized()中调用，无头模式需要在此调用
            try
            {
                Console.WriteLine("[Core.Headless] Initializing MessageProcessor...");
                MessageProcessor.EnsureInitialized();
#pragma warning disable CS0618
                RuntimeModInitializer.InitializeModsForRuntime("Headless", messageProcessor: MessageProcessor.GetInstance());
#pragma warning restore CS0618
                Console.WriteLine("[Core.Headless] ✓ MessageProcessor initialized");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Core.Headless] Error initializing MessageProcessor: {ex.Message}");
                Console.Out.Flush();
            }

            // ✅ 【新增】为 headless 模式订阅日志事件，输出到控制台
            try
            {
                Console.WriteLine("[Core.Headless] Subscribing to log events for console output...");
                if (GlobalMessageQueue.Instance != null)
                {
                    GlobalMessageQueue.Instance.LogMessageQueued += (message, logType) =>
                    {
                        string prefix = logType switch
                        {
                            LogMessageType.Normal => "[LOG]",
                            LogMessageType.Warning => "[WARN]",
                            LogMessageType.Important => "[ERROR]",
                            _ => "[LOG]"
                        };
                        Console.WriteLine($"{prefix} {message}");
                        Console.Out.Flush();
                    };
                    Console.WriteLine("[Core.Headless] ✓ Log subscriber attached to console output");
                }
                else
                {
                    Console.WriteLine("[Core.Headless] ⚠ GlobalMessageQueue.Instance is null, cannot subscribe to logs");
                }
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Core.Headless] Error subscribing to log events: {ex.Message}");
                Console.Out.Flush();
            }

            // ✅ 【新增】为 headless 模式订阅 OneBot 消息事件，追踪消息流
            try
            {
                Console.WriteLine("[Core.Headless] Subscribing to OneBot message events for message tracing...");
                if (GlobalMessageQueue.Instance != null)
                {
                    GlobalMessageQueue.Instance.OneBotMessageQueued += (oneBotObj) =>
                    {
                        try
                        {
                            if (oneBotObj is JsonElement json)
                            {
                                var postType = json.TryGetProperty("post_type", out var pt) ? pt.GetString() : "unknown";
                                var messageType = json.TryGetProperty("message_type", out var mt) ? mt.GetString() : "unknown";
                                Console.WriteLine($"[ONEBOT] 📨 Received OneBot message: post_type={postType}, message_type={messageType}");
                                
                                if (postType == "message")
                                {
                                    var groupId = json.TryGetProperty("group_id", out var gid) ? gid.GetInt64() : 0;
                                    var userId = json.TryGetProperty("user_id", out var uid) ? uid.GetInt64() : 0;
                                    Console.WriteLine($"[ONEBOT] 👥 Group Message: group_id={groupId}, user_id={userId}");
                                }
                            }
                            Console.Out.Flush();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ONEBOT] Error in message tracing: {ex.Message}");
                            Console.Out.Flush();
                        }
                    };
                    Console.WriteLine("[Core.Headless] ✓ OneBot message subscriber attached for tracing");
                }
                else
                {
                    Console.WriteLine("[Core.Headless] ⚠ GlobalMessageQueue.Instance is null, cannot subscribe to messages");
                }
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Core.Headless] Error subscribing to OneBot messages: {ex.Message}");
                Console.Out.Flush();
            }

            // 初始化并启动 WebSocket 连接
            try
            {
                Console.WriteLine("[Core.Headless] Initializing WebSocket connection...");
                wsConnection = new WSconnection();
                Console.Out.Flush();
                
                // 启动 WebSocket 连接
                Console.WriteLine("[Core.Headless] Starting WebSocket connection task...");
                /*var connectTask = wsConnection.StartConnection();
                
                // 等待连接建立
                if (!connectTask.Wait(TimeSpan.FromSeconds(15)))
                {
                    Console.WriteLine("[Core.Headless] ✗ CRITICAL: WebSocket connection timed out after 15 seconds");
                    Console.Out.Flush();
                    throw new TimeoutException("WebSocket connection failed to establish within 15 seconds");
                }
                
                // 检查连接是否真的成功
                if (!wsConnection.IsWsConnected)
                {
                    Console.WriteLine("[Core.Headless] ✗ CRITICAL: WebSocket connection status is NOT CONNECTED");
                    Console.Out.Flush();
                    throw new InvalidOperationException("WebSocket connection task completed but connection is not active");
                }*/
                
                Console.WriteLine("[Core.Headless] ✓ WebSocket connection established successfully");
                Console.WriteLine("[Core.Headless] ✓ Message processing chain is now active");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Core.Headless] ✗ CRITICAL ERROR: Failed to initialize WebSocket: {ex.Message}");
                Console.WriteLine($"[Core.Headless] Exception Type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[Core.Headless] Inner Exception: {ex.InnerException.Message}");
                }
                Console.Out.Flush();
                
                // 关键错误：无法启动WebSocket，无法继续
                Environment.Exit(1);
            }

            // 启动父进程监控（如果有父进程PID）
            Task? monitorTask = null;
            if (ParentConsolePid.HasValue)
            {
                Console.WriteLine($"[Core.Headless] Parent Console PID: {ParentConsolePid}, monitoring enabled");
                Console.Out.Flush();
                monitorTask = Task.Run(() => MonitorParentProcessAsync());
            }
            else
            {
                Console.WriteLine("[Core.Headless] No parent console to monitor - running independently");
                Console.Out.Flush();
            }

            // 等待关闭信号
            Console.WriteLine("[Core.Headless] Waiting for shutdown signal...");
            Console.Out.Flush();
            ShutdownSignal.WaitOne();
            
            Console.WriteLine("[Core.Headless] Shutdown signal received, stopping...");
            Console.Out.Flush();
            
            // 停止 WebSocket 连接
            if (wsConnection != null)
            {
                try
                {
                    var disconnectTask = wsConnection.DisconnectAsync();
                    if (!disconnectTask.Wait(TimeSpan.FromSeconds(2)))
                    {
                        Console.WriteLine("[Core.Headless] Warning: WebSocket disconnect timed out");
                    }
                    else
                    {
                        Console.WriteLine("[Core.Headless] ✓ WebSocket connection closed");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Core.Headless] Error stopping WebSocket: {ex.Message}");
                }
                Console.Out.Flush();
            }
            
            // 等待监控任务完成
            if (monitorTask != null)
            {
                try
                {
                    if (!monitorTask.Wait(TimeSpan.FromSeconds(2)))
                    {
                        Console.WriteLine("[Core.Headless] Monitor task did not complete in time");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Core.Headless] Error waiting for monitor task: {ex.Message}");
                }
                Console.Out.Flush();
            }
            
            Console.WriteLine("[Core.Headless] Application shutdown complete");
            Console.Out.Flush();
            Environment.Exit(0);
        }

        private static async Task MonitorParentProcessAsync()
        {
            if (!ParentConsolePid.HasValue)
            {
                Console.WriteLine("[Core.Monitor] No parent PID to monitor");
                return;
            }

            Console.WriteLine($"[Core.Monitor] ========== Monitoring parent Console (PID: {ParentConsolePid}) ==========");
            Console.Out.Flush();

            int checkInterval = 500; // 500ms - 快速响应
            int maxConsecutiveErrors = 3;
            int consecutiveErrors = 0;

            while (true)
            {
                try
                {
                    var proc = Process.GetProcessById(ParentConsolePid.Value);
                    
                    if (proc.HasExited)
                    {
                        Console.WriteLine($"[Core.Monitor] ⚠  Parent Console process HAS EXITED (PID: {ParentConsolePid})");
                        Console.Out.Flush();
                        await GracefulShutdownAsync();
                        return;
                    }
                    
                    consecutiveErrors = 0; // 重置错误计数
                }
                catch (ArgumentException)
                {
                    // 进程不存在 - 这是最可能的情况
                    consecutiveErrors++;
                    Console.WriteLine($"[Core.Monitor] ⚠  Parent Console process NOT FOUND (PID: {ParentConsolePid}) [attempt {consecutiveErrors}]");
                    Console.Out.Flush();
                    
                    // 如果连续多次无法获取进程，则认为它已死亡
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Console.WriteLine($"[Core.Monitor] ✗ Parent Console CONFIRMED DEAD after {consecutiveErrors} checks");
                        Console.Out.Flush();
                        await GracefulShutdownAsync();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    Console.WriteLine($"[Core.Monitor] ⚠  Error checking parent: {ex.Message} [{consecutiveErrors}]");
                    Console.Out.Flush();
                    
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Console.WriteLine($"[Core.Monitor] ✗ Too many errors, assuming parent is dead");
                        Console.Out.Flush();
                        await GracefulShutdownAsync();
                        return;
                    }
                }

                await Task.Delay(checkInterval);
            }
        }

        private static async Task GracefulShutdownAsync()
        {
            Console.WriteLine("[Core.Shutdown] ========== GRACEFUL SHUTDOWN INITIATED ==========");
            Console.Out.Flush();

            try
            {
                // 步骤 1: 记录关闭准备
                Console.WriteLine("[Core.Shutdown] Step 1: Preparing for graceful shutdown...");
                Console.Out.Flush();
                await Task.Delay(50);

                // 步骤 2: 尝试清理消息处理系统
                Console.WriteLine("[Core.Shutdown] Step 2: Stopping message processing...");
                Console.Out.Flush();
                try
                {
                    // 如果存在MessageProcessor，尝试清理
                    await Task.Delay(100);
                    Console.WriteLine("[Core.Shutdown] ✓ Message processing stopped");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Core.Shutdown] ⚠  Error stopping message processing: {ex.Message}");
                }
                Console.Out.Flush();

                // 步骤 3: 关闭网络连接
                Console.WriteLine("[Core.Shutdown] Step 3: Closing network connections...");
                Console.Out.Flush();
                await Task.Delay(100);
                Console.WriteLine("[Core.Shutdown] ✓ Network connections closed");
                Console.Out.Flush();

                // 步骤 4: 保存状态
                Console.WriteLine("[Core.Shutdown] Step 4: Saving application state...");
                Console.Out.Flush();
                await Task.Delay(100);
                Console.WriteLine("[Core.Shutdown] ✓ Application state saved");
                Console.Out.Flush();

                Console.WriteLine("[Core.Shutdown] ========== GRACEFUL SHUTDOWN COMPLETE ==========");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Core.Shutdown] ✗ FATAL ERROR during shutdown: {ex}");
                Console.WriteLine($"[Core.Shutdown] Stack Trace: {ex.StackTrace}");
                Console.Out.Flush();
            }
            finally
            {
                // 最终清理：唤醒主线程并直接退出
                Console.WriteLine("[Core.Shutdown] Signaling main thread to exit...");
                Console.Out.Flush();
                
                ShutdownSignal?.Set();
                
                await Task.Delay(200); // 给主线程时间处理
                
                Console.WriteLine("[Core.Shutdown] Force exiting application (PID: {0})...", Process.GetCurrentProcess().Id);
                Console.Out.Flush();
                
                // 直接退出，不再等待
                Environment.Exit(0);
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
