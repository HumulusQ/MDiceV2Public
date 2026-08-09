using System;
using System.IO;
using ABot;

class DiagnosticProgram
{
    static void Main(string[] args)
    {
        string logPath = Path.Combine(Path.GetTempPath(), "diagnostic_test.log");
        
        try
        {
            // 清理旧的诊断日志
            try { File.Delete("C:\\Windows\\Temp\\abot_cpp_debug.log"); } catch { }
            File.WriteAllText(logPath, "=== ABot 诊断测试开始 ===\n");
            
            Log(logPath, "步骤1: 创建 ABotInterpreter 实例");
            var interpreter = new ABotInterpreter();
            Log(logPath, "✓ ABotInterpreter 创建成功");
            
            Log(logPath, "\n步骤2: 第一次调用 IsReady()");
            bool firstCheck = interpreter.IsReady();
            Log(logPath, $"✓ 第一次结果: {firstCheck}");
            
            Log(logPath, "\n步骤3: 延迟 1 秒");
            System.Threading.Thread.Sleep(1000);
            
            Log(logPath, "步骤4: 第二次调用 IsReady()");
            bool secondCheck = interpreter.IsReady();
            Log(logPath, $"✓ 第二次结果: {secondCheck}");
            
            Log(logPath, "\n=== 诊断完成 ===");
            Log(logPath, $"结论: IsReady() 返回 {(firstCheck && secondCheck ? "✓ 始终为 true" : "✗ 返回 false")}");
            
            // 读取 C++ 诊断日志
            Log(logPath, "\n--- C++ 诊断日志 ---");
            string cppLogPath = "C:\\Windows\\Temp\\abot_cpp_debug.log";
            if (File.Exists(cppLogPath))
            {
                string[] cppLogs = File.ReadAllLines(cppLogPath);
                foreach (string line in cppLogs)
                {
                    Log(logPath, line);
                }
            }
            else
            {
                Log(logPath, "C++ 诊断日志未找到");
            }
            
            interpreter.Dispose();
            
            // 输出到控制台
            Console.WriteLine(File.ReadAllText(logPath));
        }
        catch (Exception ex)
        {
            Log(logPath, $"✗ 异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            Console.WriteLine(File.ReadAllText(logPath));
        }
    }
    
    static void Log(string filePath, string message)
    {
        try
        {
            File.AppendAllText(filePath, message + "\n", System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
