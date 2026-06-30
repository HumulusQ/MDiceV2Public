using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SystemConsole = System.Console;

namespace MDiceV2.Console
{
    class Program
    {
        private static Process? coreProcess;
        private static bool isShuttingDown = false;
        private static readonly object shutdownLock = new object();

        [STAThread]
        static void Main(string[] args)
        {
            int exitCode = 0;
            
            try
            {
                // 获取当前进程ID
                var consoleProcessId = Process.GetCurrentProcess().Id;
                SystemConsole.WriteLine($"[MDiceV2.Console] Console Process ID: {consoleProcessId}");
                SystemConsole.WriteLine($"[MDiceV2.Console] Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");

                // 注册进程退出事件
                AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
                {
                    SystemConsole.WriteLine("[MDiceV2.Console] ProcessExit event triggered");
                    CleanupCore();
                };

                // 注册 Ctrl+C 事件处理
                SystemConsole.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    SystemConsole.WriteLine("\n[MDiceV2.Console] Ctrl+C received - initiating graceful shutdown...");
                    CleanupCore();
                    Environment.Exit(0);
                };

                // 查找 Core 可执行文件
                var basePath = AppDomain.CurrentDomain.BaseDirectory;
                var coreSubDir = Path.Combine(basePath, "Core");
                var coreExePath = Path.Combine(coreSubDir, "MDiceV2.Core.Dice");

                SystemConsole.WriteLine($"[MDiceV2.Console] Looking for Core at: {coreExePath}");

                if (!File.Exists(coreExePath))
                {
                    SystemConsole.WriteLine($"[MDiceV2.Console] ERROR: Core executable not found!");
                    SystemConsole.WriteLine($"[MDiceV2.Console] Searched at: {coreExePath}");
                    exitCode = 1;
                    throw new FileNotFoundException($"Core executable not found at {coreExePath}");
                }

                SystemConsole.WriteLine($"[MDiceV2.Console] ✓ Core executable found");
                SystemConsole.WriteLine($"[MDiceV2.Console] Starting Core in headless mode...");
                SystemConsole.WriteLine("[MDiceV2.Console] ==========================================\n");

                // 启动 Core 进程
                var processInfo = new ProcessStartInfo(coreExePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = basePath,
                    // 传入 --headless 和 --parent-pid 参数
                    Arguments = $"--headless --parent-pid={consoleProcessId}",
                    // 重定向 Core 的标准输出和错误输出到父控制台
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                coreProcess = Process.Start(processInfo);

                if (coreProcess == null)
                {
                    SystemConsole.WriteLine("[MDiceV2.Console] ERROR: Failed to start Core process");
                    exitCode = 1;
                    throw new InvalidOperationException("Failed to start Core process");
                }

                SystemConsole.WriteLine($"[MDiceV2.Console] ✓ Core process started (PID: {coreProcess.Id})");
                SystemConsole.WriteLine("[MDiceV2.Console] ==========================================\n");
                SystemConsole.Out.Flush();

                // 启动异步任务来读取 Core 进程的输出流
                var stdoutTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        while (!coreProcess.StandardOutput.EndOfStream)
                        {
                            var line = coreProcess.StandardOutput.ReadLine();
                            if (line != null)
                            {
                                SystemConsole.WriteLine(line);
                                SystemConsole.Out.Flush();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SystemConsole.WriteLine($"[MDiceV2.Console] Error reading StdOut: {ex.Message}");
                    }
                });

                var stderrTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        while (!coreProcess.StandardError.EndOfStream)
                        {
                            var line = coreProcess.StandardError.ReadLine();
                            if (line != null)
                            {
                                SystemConsole.WriteLine(line);
                                SystemConsole.Out.Flush();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SystemConsole.WriteLine($"[MDiceV2.Console] Error reading StdErr: {ex.Message}");
                    }
                });

                // 等待 Core 进程退出
                coreProcess.WaitForExit();
                
                // 继续读取可能还在缓冲区中的输出
                if (!System.Threading.Tasks.Task.WaitAll(new[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(5)))
                {
                    SystemConsole.WriteLine("[MDiceV2.Console] WARNING: Output relay tasks did not complete within timeout");
                }

                SystemConsole.WriteLine($"\n[MDiceV2.Console] Core process exited with code: {coreProcess.ExitCode}");
                exitCode = coreProcess.ExitCode;
            }
            catch (Exception ex)
            {
                SystemConsole.WriteLine($"[MDiceV2.Console] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                SystemConsole.WriteLine($"[MDiceV2.Console] Stack Trace: {ex.StackTrace}");
                exitCode = 1;
            }
            finally
            {
                SystemConsole.WriteLine("[MDiceV2.Console] ==========================================");
                CleanupCore();
                SystemConsole.WriteLine($"[MDiceV2.Console] Console exit code: {exitCode}");
                SystemConsole.Out.Flush();
            }

            Environment.Exit(exitCode);
        }

        /// <summary>
        /// 清理 Core 进程 - 确保进程及其子进程被完全终止
        /// </summary>
        private static void CleanupCore()
        {
            lock (shutdownLock)
            {
                if (isShuttingDown)
                    return;

                isShuttingDown = true;
            }

            if (coreProcess == null)
                return;

            try
            {
                if (!coreProcess.HasExited)
                {
                    SystemConsole.WriteLine($"[MDiceV2.Console] Stopping Core process (PID: {coreProcess.Id})...");
                    SystemConsole.Out.Flush();

                    // 首先尝试正常关闭
                    if (!coreProcess.CloseMainWindow())
                    {
                        SystemConsole.WriteLine("[MDiceV2.Console] CloseMainWindow failed, using Kill...");
                    }

                    // 等待进程优雅关闭
                    if (!coreProcess.WaitForExit(5000))
                    {
                        SystemConsole.WriteLine("[MDiceV2.Console] Core did not exit gracefully, force killing...");
                        coreProcess.Kill(entireProcessTree: true);
                        coreProcess.WaitForExit(2000);
                    }

                    SystemConsole.WriteLine($"[MDiceV2.Console] ✓ Core process stopped");
                }
            }
            catch (Exception ex)
            {
                SystemConsole.WriteLine($"[MDiceV2.Console] ⚠ Error during Core cleanup: {ex.Message}");
            }
            finally
            {
                coreProcess?.Dispose();
                coreProcess = null;
            }
        }
    }
}
