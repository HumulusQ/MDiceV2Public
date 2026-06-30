using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using MDiceV2.Core.Mod;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class AIModSlimPackageTests
{
    private static readonly MethodInfo ValidateAIModPayloadMethod =
        typeof(MessageProcessor).GetMethod("ValidateAIModPayload", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ValidateAIModPayload not found.");

    private static readonly MethodInfo ShouldPreferHostAssemblyMethod =
        typeof(ModPluginLoader).GetMethod("ShouldPreferHostAssembly", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ShouldPreferHostAssembly not found.");

    private static readonly MethodInfo GenerateAIModUpdateBatchFileAsyncMethod =
        typeof(MessageProcessor).GetMethod("GenerateAIModUpdateBatchFileAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GenerateAIModUpdateBatchFileAsync not found.");

    [Fact]
    public void ReleaseOutput_ShouldOnlyContainSlimPluginArtifacts()
    {
        var repoRoot = FindRepoRoot();
        var releaseDir = Path.Combine(repoRoot, "Mods", "AIMod", "bin", "Release", "net10.0-windows");

        Directory.Exists(releaseDir).Should().BeTrue("AIMod Release output should exist after building Release");

        var relativePaths = Directory
            .EnumerateFileSystemEntries(releaseDir, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(releaseDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        relativePaths.Should().Contain("AIMod.dll");
        relativePaths.Should().Contain("mod.json");
        relativePaths.Should().Contain("ai-config.json");
        relativePaths.Should().Contain(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));

        relativePaths.Should().NotContain(path => path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("Avalonia", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("Semi.Avalonia", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("ReactiveUI", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("Splat", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("HarfBuzzSharp", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("System.Data.SQLite", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("Polly", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("Grpc", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("Google.Protobuf", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("protobuf-net", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("MDiceV2.Core.dll", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("MDiceV2.Interfaces.dll", StringComparison.OrdinalIgnoreCase));
        relativePaths.Should().NotContain(path => path.Contains("MDiceV2.Abstractions.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateAIModPayload_ShouldAcceptSlimPackage()
    {
        using var tempDir = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "mod.json"), """
        {
          "id": "com.humulus.aimod",
          "dllFileName": "AIMod.dll",
          "pluginClassName": "AIMod.AIMod"
        }
        """);
        File.WriteAllText(Path.Combine(tempDir.Path, "AIMod.dll"), "stub");
        File.WriteAllText(Path.Combine(tempDir.Path, "AIMod.pdb"), "stub");
        File.WriteAllText(Path.Combine(tempDir.Path, "ai-config.json"), "{}");
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Assets"));

        var invoke = () => ValidateAIModPayloadMethod.Invoke(null, new object[] { tempDir.Path });

        invoke.Should().NotThrow();
    }

    [Theory]
    [InlineData("AIMod.deps.json")]
    [InlineData("AIMod.runtimeconfig.json")]
    [InlineData("Avalonia.Base.dll")]
    [InlineData("System.Data.SQLite.dll")]
    [InlineData("Polly.dll")]
    public void ValidateAIModPayload_ShouldRejectFatRuntimeArtifacts(string extraFile)
    {
        using var tempDir = CreateMinimalAIModPayload();
        File.WriteAllText(Path.Combine(tempDir.Path, extraFile), "stub");

        var act = () => ValidateAIModPayloadMethod.Invoke(null, new object[] { tempDir.Path });

        var exception = Assert.Throws<TargetInvocationException>(act);
        exception.InnerException.Should().NotBeNull();
        exception.InnerException!.Message.Should().Contain("瘦插件包");
    }

    [Fact]
    public void ValidateAIModPayload_ShouldRejectRuntimesDirectory()
    {
        using var tempDir = CreateMinimalAIModPayload();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "runtimes", "win-x64"));

        var act = () => ValidateAIModPayloadMethod.Invoke(null, new object[] { tempDir.Path });

        var exception = Assert.Throws<TargetInvocationException>(act);
        exception.InnerException.Should().NotBeNull();
        exception.InnerException!.Message.Should().Contain("瘦插件包");
    }

    [Theory]
    [InlineData("Avalonia.Base")]
    [InlineData("Semi.Avalonia")]
    [InlineData("ReactiveUI")]
    [InlineData("Splat")]
    [InlineData("SkiaSharp")]
    [InlineData("HarfBuzzSharp")]
    [InlineData("System.Data.SQLite")]
    [InlineData("Polly")]
    [InlineData("MDiceV2.Interfaces")]
    [InlineData("MDiceV2.Abstractions")]
    public void SharedDependencies_ShouldPreferHostAssembly(string assemblyName)
    {
        var result = (bool)ShouldPreferHostAssemblyMethod.Invoke(null, new object[] { assemblyName })!;

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("AIMod")]
    [InlineData("CustomizedReply")]
    [InlineData("SomePluginSpecificDependency")]
    public void PluginSpecificDependencies_ShouldNotBeForcedToHost(string assemblyName)
    {
        var result = (bool)ShouldPreferHostAssemblyMethod.Invoke(null, new object[] { assemblyName })!;

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAIModUpdateBatchFile_ShouldKeepExternalScriptFlowWithoutRuntimeHardFails()
    {
        using var tempDir = CreateMinimalAIModPayload();
        var packagePath = System.IO.Path.Combine(tempDir.Path, "AIModPackTest.zip");
        File.WriteAllText(packagePath, "stub");

        var processor = new MessageProcessor();
        var task = (Task<string>)GenerateAIModUpdateBatchFileAsyncMethod.Invoke(
            processor,
            new object[]
            {
                "AIModPackVTest",
                tempDir.Path,
                packagePath,
                new Action<string>(_ => { })
            })!;

        var scriptPath = await task;
        File.Exists(scriptPath).Should().BeTrue();

        var scriptContent = File.ReadAllText(scriptPath);
        scriptContent.Should().Contain("tasklist /FI \"PID eq %PID%\"");
        scriptContent.Should().Contain("robocopy \"%PAYLOAD%\" \"%TARGET%\" /E");
        scriptContent.Should().Contain("if not exist \"%PAYLOAD%\\mod.json\" goto missing_payload");
        scriptContent.Should().Contain("if not exist \"%PAYLOAD%\\%DLL_FILE%\" goto missing_payload");
        scriptContent.Should().Contain("move \"%TARGET%\" \"%BACKUP%\"");
        scriptContent.Should().Contain("start \"\" \"%EXE_PATH%\"");

        scriptContent.IndexOf("runtimes", StringComparison.OrdinalIgnoreCase).Should().Be(-1);
        scriptContent.IndexOf("Avalonia", StringComparison.OrdinalIgnoreCase).Should().Be(-1);
        scriptContent.IndexOf("Semi.Avalonia", StringComparison.OrdinalIgnoreCase).Should().Be(-1);
        scriptContent.IndexOf("System.Data.SQLite", StringComparison.OrdinalIgnoreCase).Should().Be(-1);
    }

    private static TemporaryDirectory CreateMinimalAIModPayload()
    {
        var tempDir = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "mod.json"), """
        {
          "id": "com.humulus.aimod",
          "dllFileName": "AIMod.dll",
          "pluginClassName": "AIMod.AIMod"
        }
        """);
        File.WriteAllText(Path.Combine(tempDir.Path, "AIMod.dll"), "stub");
        return tempDir;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MDiceV2.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root from test output directory.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIModSlimTests_" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
