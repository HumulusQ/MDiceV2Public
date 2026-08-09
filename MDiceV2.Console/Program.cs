using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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
                if (args.Length > 0 && string.Equals(args[0], "test", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.Exit(RunMessageTestCli(args));
                    return;
                }

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

        private sealed class MessageTestConsoleOptions
        {
            public string? Message { get; set; }
            public string? OneBotJson { get; set; }
            public long GroupId { get; set; } = 10000;
            public long UserId { get; set; } = 10001;
            public bool IsPrivate { get; set; }
            public bool Stdin { get; set; }
            public string? FilePath { get; set; }
            public bool OneBotJsonMode { get; set; }
            public int TimeoutMs { get; set; } = 5000;
            public bool Trace { get; set; }
            public bool Json { get; set; }
            public bool AtBot { get; set; }
        }

        private static int RunMessageTestCli(string[] args)
        {
            try
            {
                var options = ParseMessageTestOptions(args);
                var coreExePath = ResolveCoreExePath();

                if (!File.Exists(coreExePath))
                {
                    SystemConsole.Error.WriteLine($"[MDiceV2.Console:test] Core executable not found: {coreExePath}");
                    return 1;
                }

                var processInfo = new ProcessStartInfo(coreExePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                processInfo.ArgumentList.Add("--headless");
                processInfo.ArgumentList.Add("--message-test");
                processInfo.ArgumentList.Add($"--test-group={options.GroupId}");
                processInfo.ArgumentList.Add($"--test-user={options.UserId}");
                processInfo.ArgumentList.Add($"--test-timeout-ms={options.TimeoutMs}");

                if (options.IsPrivate) processInfo.ArgumentList.Add("--test-private");
                if (options.AtBot) processInfo.ArgumentList.Add("--test-at-bot");
                if (options.Trace) processInfo.ArgumentList.Add("--test-trace");

                if (options.OneBotJsonMode)
                {
                    processInfo.ArgumentList.Add($"--test-onebot-json-b64={EncodeBase64(options.OneBotJson ?? string.Empty)}");
                }
                else
                {
                    processInfo.ArgumentList.Add($"--test-message-b64={EncodeBase64(options.Message ?? string.Empty)}");
                }

                using var process = Process.Start(processInfo);
                if (process == null)
                {
                    SystemConsole.Error.WriteLine("[MDiceV2.Console:test] Failed to start Core process.");
                    return 1;
                }

                string? resultJson = null;
                var stderrTask = System.Threading.Tasks.Task.Run(() =>
                {
                    while (!process.StandardError.EndOfStream)
                    {
                        var line = process.StandardError.ReadLine();
                        if (line != null && options.Trace)
                        {
                            SystemConsole.Error.WriteLine(line);
                        }
                    }
                });

                while (!process.StandardOutput.EndOfStream)
                {
                    var line = process.StandardOutput.ReadLine();
                    if (line == null)
                    {
                        continue;
                    }

                    if (line.StartsWith("__MDICEV2_TEST_RESULT__", StringComparison.Ordinal))
                    {
                        resultJson = line["__MDICEV2_TEST_RESULT__".Length..];
                    }
                    else if (options.Trace)
                    {
                        SystemConsole.Error.WriteLine(line);
                    }
                }

                process.WaitForExit();
                stderrTask.Wait(TimeSpan.FromSeconds(1));

                if (string.IsNullOrWhiteSpace(resultJson))
                {
                    SystemConsole.Error.WriteLine("[MDiceV2.Console:test] Core exited without a message-test result.");
                    return process.ExitCode == 0 ? 1 : process.ExitCode;
                }

                if (options.Json)
                {
                    SystemConsole.WriteLine(resultJson);
                }
                else
                {
                    PrintHumanReadableTestResult(resultJson);
                }

                return process.ExitCode;
            }
            catch (Exception ex)
            {
                SystemConsole.Error.WriteLine($"[MDiceV2.Console:test] {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static MessageTestConsoleOptions ParseMessageTestOptions(string[] args)
        {
            var options = new MessageTestConsoleOptions();
            var messageParts = new List<string>();
            var afterDoubleDash = false;
            string? oneBotJsonInline = null;

            for (int i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                if (afterDoubleDash)
                {
                    messageParts.Add(arg);
                    continue;
                }

                if (arg == "--")
                {
                    afterDoubleDash = true;
                    continue;
                }

                switch (arg)
                {
                    case "--group":
                        options.GroupId = ParseLongOption(args, ref i, "--group");
                        break;
                    case var value when value.StartsWith("--group=", StringComparison.Ordinal):
                        options.GroupId = ParseLongValue(value["--group=".Length..], "--group");
                        break;
                    case "--user":
                        options.UserId = ParseLongOption(args, ref i, "--user");
                        break;
                    case var value when value.StartsWith("--user=", StringComparison.Ordinal):
                        options.UserId = ParseLongValue(value["--user=".Length..], "--user");
                        break;
                    case "--private":
                        options.IsPrivate = true;
                        break;
                    case "--stdin":
                        options.Stdin = true;
                        break;
                    case "--file":
                        options.FilePath = ParseStringOption(args, ref i, "--file");
                        break;
                    case "--onebot-json":
                        options.OneBotJsonMode = true;
                        if (i + 1 < args.Length && args[i + 1] != "--" && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            oneBotJsonInline = args[++i];
                        }
                        break;
                    case "--timeout":
                        options.TimeoutMs = ParseTimeoutMs(ParseStringOption(args, ref i, "--timeout"));
                        break;
                    case var value when value.StartsWith("--timeout=", StringComparison.Ordinal):
                        options.TimeoutMs = ParseTimeoutMs(value["--timeout=".Length..]);
                        break;
                    case "--trace":
                        options.Trace = true;
                        break;
                    case "--json":
                        options.Json = true;
                        break;
                    case "--at-bot":
                        options.AtBot = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown test option: {arg}");
                }
            }

            var source = ResolveMessageSource(options, oneBotJsonInline, messageParts);
            if (options.OneBotJsonMode)
            {
                options.OneBotJson = source;
            }
            else
            {
                options.Message = source;
            }

            return options;
        }

        private static string ResolveMessageSource(MessageTestConsoleOptions options, string? inlineOneBotJson, List<string> messageParts)
        {
            if (options.Stdin)
            {
                return StripLeadingBom(SystemConsole.In.ReadToEnd());
            }

            if (!string.IsNullOrWhiteSpace(options.FilePath))
            {
                return StripLeadingBom(File.ReadAllText(options.FilePath, Encoding.UTF8));
            }

            if (options.OneBotJsonMode && !string.IsNullOrEmpty(inlineOneBotJson))
            {
                return File.Exists(inlineOneBotJson)
                    ? StripLeadingBom(File.ReadAllText(inlineOneBotJson, Encoding.UTF8))
                    : inlineOneBotJson;
            }

            if (messageParts.Count > 0)
            {
                return messageParts.Count == 1 ? messageParts[0] : string.Join(" ", messageParts);
            }

            throw new ArgumentException("No test message provided. Use: MDiceV2.Console.exe test -- \"任意格式消息\"");
        }

        private static long ParseLongOption(string[] args, ref int index, string optionName)
        {
            var value = ParseStringOption(args, ref index, optionName);
            if (!long.TryParse(value, out var result))
            {
                throw new ArgumentException($"{optionName} requires an integer value.");
            }

            return result;
        }

        private static long ParseLongValue(string value, string optionName)
        {
            if (!long.TryParse(value, out var result))
            {
                throw new ArgumentException($"{optionName} requires an integer value.");
            }

            return result;
        }

        private static string ParseStringOption(string[] args, ref int index, string optionName)
        {
            if (index + 1 >= args.Length || args[index + 1] == "--")
            {
                throw new ArgumentException($"{optionName} requires a value.");
            }

            return args[++index];
        }

        private static int ParseTimeoutMs(string value)
        {
            if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value[..^2], out var ms))
            {
                return Math.Clamp(ms, 1, 300000);
            }

            if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value[..^1], out var seconds))
            {
                return Math.Clamp((int)(seconds * 1000), 1, 300000);
            }

            if (int.TryParse(value, out var plainMs))
            {
                return Math.Clamp(plainMs, 1, 300000);
            }

            throw new ArgumentException("--timeout requires milliseconds, or a value like 5s / 500ms.");
        }

        private static string StripLeadingBom(string value)
        {
            return value.Length > 0 && value[0] == '\uFEFF' ? value[1..] : value;
        }

        private static void PrintHumanReadableTestResult(string resultJson)
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            var success = root.TryGetProperty("success", out var successElement) && successElement.GetBoolean();

            if (success && root.TryGetProperty("replies", out var repliesElement))
            {
                foreach (var reply in repliesElement.EnumerateArray())
                {
                    SystemConsole.WriteLine(reply.GetString() ?? string.Empty);
                }
                return;
            }

            if (root.TryGetProperty("timedOut", out var timedOutElement) && timedOutElement.GetBoolean())
            {
                SystemConsole.Error.WriteLine("[MDiceV2.Console:test] Timed out waiting for a reply.");
                return;
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                SystemConsole.Error.WriteLine($"[MDiceV2.Console:test] {errorElement.GetString()}");
                return;
            }

            SystemConsole.Error.WriteLine("[MDiceV2.Console:test] No reply captured.");
        }

        private static string EncodeBase64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string ResolveCoreExePath()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var packagedPath = Path.Combine(basePath, "Core", "MDiceV2.Core.Dice");
            if (IsRunnableCorePath(packagedPath))
            {
                return packagedPath;
            }

            var devPath = Path.GetFullPath(Path.Combine(
                basePath,
                "..", "..", "..", "..", "..",
                "MDiceV2.Core", "bin", "Debug", "net10.0-windows", "win-x64", "MDiceV2.Core.Dice"));

            return IsRunnableCorePath(devPath) ? devPath : packagedPath;
        }

        private static bool IsRunnableCorePath(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(path);
            return !string.IsNullOrWhiteSpace(directory) &&
                   File.Exists(Path.Combine(directory, "hostpolicy.dll"));
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
