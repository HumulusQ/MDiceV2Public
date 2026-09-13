using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace MDiceV2.Launcher;

/// <summary>
/// 运行时模块初始化 - 在 Program.cs 中的任何程序集加载前配置依赖解析
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
            // 注册程序集加载解析器 - 主要针对.NET框架和第三方库
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
