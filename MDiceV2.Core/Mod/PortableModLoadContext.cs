using System.Reflection;
using System.Runtime.Loader;

namespace MDiceV2.Core.Mod;

internal sealed class PortableModLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedPrefixes =
    {
        "MDiceV2.Interfaces", "MDiceV2.Abstractions", "MDiceV2.Core", "Avalonia", "Semi.Avalonia",
        "ReactiveUI", "Splat", "SkiaSharp", "HarfBuzzSharp", "CommunityToolkit.Mvvm",
        "Grpc", "Google.Protobuf", "System.Data.SQLite", "Polly"
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PortableModLoadContext(string mainAssemblyPath)
        : base($"mmod:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name ?? string.Empty;
        if (SharedPrefixes.Any(prefix => simpleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                assembly => string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
