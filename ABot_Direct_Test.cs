using System;
using System.IO;
using ABot;

namespace ABotDirectTest
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("[Test] Starting direct ABot script test...");
            
            // Test script with Chinese characters
            string testScript = @"[
<type value=Character>
<Name value=烈海王>
<Camp value=1>
<Atk value=100>
<Hp value=50, Max=50>
<Dmg d1=1, d2=3, d3=5, d4=7>
]

[
<type value=Character>
<Name value=范马勇次郎>
<Camp value=2>
<Atk value=120>
<Hp value=60, Max=60>
<Dmg d1=2, d2=4, d3=6, d4=8>
]";
            
            try
            {
                Console.WriteLine("[Test] Creating ABotInterpreter...");
                var interpreter = new ABotInterpreter();
                
                Console.WriteLine("[Test] Checking if interpreter is ready...");
                if (!interpreter.IsReady())
                {
                    Console.WriteLine("[Test] ERROR: Interpreter is not ready!");
                    Console.ReadLine();
                    return;
                }
                
                Console.WriteLine("[Test] Interpreter is ready. Executing script...");
                int result = interpreter.ExecuteScript(testScript);
                
                if (result == 0)
                {
                    Console.WriteLine("[Test] SUCCESS: Script executed successfully!");
                }
                else
                {
                    Console.WriteLine($"[Test] ERROR: Script execution failed with code {result}");
                    string? error = interpreter.GetLastError();
                    if (!string.IsNullOrEmpty(error))
                    {
                        Console.WriteLine($"[Test] Error message: {error}");
                    }
                }
                
                // Check log file
                Console.WriteLine("\n[Test] Checking debug log...");
                string logPath = "C:\\Windows\\Temp\\abot_cpp_debug.log";
                if (File.Exists(logPath))
                {
                    Console.WriteLine("[Test] Log file found!");
                    Console.WriteLine("\n=== Last 50 lines of debug log ===\n");
                    var lines = File.ReadAllLines(logPath);
                    int startIndex = Math.Max(0, lines.Length - 50);
                    for (int i = startIndex; i < lines.Length; i++)
                    {
                        Console.WriteLine(lines[i]);
                    }
                }
                else
                {
                    Console.WriteLine("[Test] Log file not found!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Test] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[Test] StackTrace: {ex.StackTrace}");
            }
            
            Console.WriteLine("\n[Test] Press any key to exit...");
            Console.ReadLine();
        }
    }
}
