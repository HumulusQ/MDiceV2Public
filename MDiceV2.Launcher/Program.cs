using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;

namespace MDiceV2.Launcher
{
    class Program 
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Log to file
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
            var logWriter = new StreamWriter(logPath, false) { AutoFlush = true };

            try
            {
                var basePath = AppDomain.CurrentDomain.BaseDirectory;
                
                logWriter.WriteLine($"[Launcher] Started at {DateTime.Now}");
                logWriter.WriteLine($"[Launcher] BaseDirectory: {basePath}");
                logWriter.WriteLine($"[Launcher] Arguments: {string.Join(", ", args)}");

                // Check if headless mode is requested
                bool isHeadlessMode = args.Any(arg => arg.Contains("--headless"));
                
                string executablePath;
                string startupMode;
                
                if (isHeadlessMode)
                {
                    // Launch Console in headless mode - Console will spawn Core with arguments
                    executablePath = Path.Combine(basePath, "MDiceV2.Console.exe");
                    startupMode = "Console (headless)";
                    logWriter.WriteLine($"[Launcher] Headless mode detected - launching Console in headless mode");
                }
                else
                {
                    // Launch Core directly for UI mode
                    executablePath = Path.Combine(basePath, "Core", "MDiceV2.Core.Dice");
                    startupMode = "Core (UI)";
                    logWriter.WriteLine($"[Launcher] UI mode detected - launching Core directly");
                }

                logWriter.WriteLine($"[Launcher] Looking for {startupMode} at: {executablePath}");

                if (!File.Exists(executablePath))
                {
                    logWriter.WriteLine($"ERROR: {startupMode} not found!");
                    logWriter.Flush();
                    logWriter.Close();
                    Environment.Exit(1);
                }

                logWriter.WriteLine($"[Launcher] {startupMode} found, starting process...");
                var processInfo = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = basePath,  // 确保应用在根目录作为工作目录,以便找到 SQLite.Interop.dll
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // For headless Console mode, pass the arguments through
                    Arguments = isHeadlessMode ? string.Join(" ", args) : ""
                };
                var process = Process.Start(processInfo);
                
                // Capture and log stdout/stderr in background tasks
                if (process != null)
                {
                    _ = Task.Run(() => {
                        while (!process.StandardOutput.EndOfStream)
                        {
                            var line = process.StandardOutput.ReadLine();
                            if (line != null)
                            {
                                logWriter.WriteLine($"[Core.Out] {line}");
                            }
                        }
                    });
                    
                    _ = Task.Run(() => {
                        while (!process.StandardError.EndOfStream)
                        {
                            var line = process.StandardError.ReadLine();
                            if (line != null)
                            {
                                logWriter.WriteLine($"[Core.Err] {line}");
                            }
                        }
                    });
                }

                if (process == null)
                {
                    logWriter.WriteLine("[Launcher] ERROR: Failed to start process");
                    logWriter.Flush();
                    logWriter.Close();
                    Environment.Exit(1);
                }

                logWriter.WriteLine($"[Launcher] Core process started with ProcessId: {process.Id}");
                logWriter.Flush();

                // Wait for Core to complete
                process.WaitForExit();
                logWriter.WriteLine($"[Launcher] Core application exited with code: {process.ExitCode}");
            }
            catch (Exception ex)
            {
                logWriter.WriteLine($"[Launcher] EXCEPTION: {ex}");
            }
            finally
            {
                logWriter.WriteLine($"[Launcher] Launcher exiting at {DateTime.Now}");
                logWriter.Flush();
                logWriter.Close();
            }

            Environment.Exit(0);
        }
    }
}
