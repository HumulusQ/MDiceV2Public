using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace MDiceV2.Console;

/// <summary>
/// 运行时模块初始化 - 在 Program.cs 中的任何 using 语句执行前配置程序集加载
/// </summary>
internal static class ModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var depsDir = Path.Combine(baseDir, "deps");
        
        if (Directory.Exists(depsDir))
        {
            // 注册程序集加载解析器 - 当运行时无法找到程序集时会调用此函数
            AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
            {
                var assemblyPath = Path.Combine(depsDir, $"{assemblyName.Name}.dll");
                if (File.Exists(assemblyPath))
                {
                    try
                    {
                        return context.LoadFromAssemblyPath(assemblyPath);
                    }
                    catch { }
                }
                return null;
            };
        }
    }
}
