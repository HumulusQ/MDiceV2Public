using System;
using System.IO;
using ABot;

class Program
{
    static void Main()
    {
        Console.WriteLine("[ABotTest] Starting comprehensive ABot test...\n");
        
        try
        {
            Console.WriteLine("[ABotTest] Step 1: Creating interpreter...");
            var interpreter = new ABotInterpreter();
            
            if (!interpreter.IsReady())
            {
                Console.WriteLine("[ABotTest] ERROR: Interpreter not ready!");
                return;
            }
            Console.WriteLine("[ABotTest] ✓ Interpreter ready\n");
            
            // 测试 XML 解析
            string character1 = @"[
<type value=Character>
<Name value=TestChar>
<Camp value=1>
<Atk value=100>
<Hp value=50, Max=50>
<Dmg d1=1, d2=3, d3=5, d4=7>
]";
            
            Console.WriteLine("[ABotTest] Step 2: Parsing XML card...");
            int xmlResult = interpreter.ParseCharacter(character1);
            if (xmlResult == 0)
            {
                Console.WriteLine("[ABotTest] ✓ XML card parsed successfully");
            }
            else
            {
                Console.WriteLine($"[ABotTest] ✗ XML parse failed: {xmlResult}");
                string? error = interpreter.GetLastError();
                if (!string.IsNullOrEmpty(error))
                    Console.WriteLine($"  Error: {error}");
            }
            
            // 测试脚本执行
            string testScript = "set x = 10";
            
            Console.WriteLine("\n[ABotTest] Step 3: Executing script...");
            Console.WriteLine($"  Script: '{testScript}'");
            int scriptResult = interpreter.ExecuteScript(testScript);
            if (scriptResult == 0)
            {
                Console.WriteLine("[ABotTest] ✓ Script executed successfully");
            }
            else
            {
                Console.WriteLine($"[ABotTest] ✗ Script execution failed: {scriptResult}");
                string? error = interpreter.GetLastError();
                if (!string.IsNullOrEmpty(error))
                    Console.WriteLine($"  Error: {error}");
            }
            
            // 显示日志
            Console.WriteLine("\n[ABotTest] Checking debug log...");
            string logPath = "C:\\Windows\\Temp\\abot_cpp_debug.log";
            if (File.Exists(logPath))
            {
                Console.WriteLine("[ABotTest] Debug log found!\n");
                Console.WriteLine("=== Relevant Log Entries ===\n");
                var lines = File.ReadAllLines(logPath);
                foreach (var line in lines)
                {
                    if (line.Contains("Bytecode") || line.Contains("Compile") || 
                        line.Contains("Instr") || line.Contains("LOAD") || 
                        line.Contains("STORE") || line.Contains("Parser completed") ||
                        line.Contains("abot_execute_script"))
                    {
                        Console.WriteLine(line);
                    }
                }
            }
            else
            {
                Console.WriteLine("[ABotTest] Log file not found!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABotTest] EXCEPTION: {ex.Message}");
            Console.WriteLine($"[ABotTest] StackTrace: {ex.StackTrace}");
        }
    }
}
