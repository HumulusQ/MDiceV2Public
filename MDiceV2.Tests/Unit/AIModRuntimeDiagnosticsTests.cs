using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MDiceV2.Abstractions;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Core.Mod;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Xunit;
using Xunit.Abstractions;

namespace MDiceV2.Tests.Unit;

[Collection(nameof(AIModRuntimeDiagnosticsCollection))]
public class AIModRuntimeDiagnosticsTests : IDisposable
{
    private static readonly MethodInfo EnsureCommandHandlersInitializedMethod =
        typeof(MessageProcessor).GetMethod("EnsureCommandHandlersInitialized", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("EnsureCommandHandlersInitialized not found.");

    private static readonly FieldInfo CommandHandlersField =
        typeof(MessageProcessor).GetField("commandHandlers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("commandHandlers not found.");

    private static readonly FieldInfo ModEventBridgeField =
        typeof(MessageProcessor).GetField("_modEventBridge", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_modEventBridge not found.");

    private readonly ITestOutputHelper _output;

    public AIModRuntimeDiagnosticsTests(ITestOutputHelper output)
    {
        _output = output;
        ResetStaticRuntimeState();
    }

    public void Dispose()
    {
        RuntimeModInitializer.UnloadCurrent();
        ResetStaticRuntimeState();
    }

    [Fact]
    public void HeadlessLoadsModsTest()
    {
        using var modsRoot = CreateIsolatedAimodModsRoot();
        var processor = InitializeHeadlessAimodRuntime(modsRoot.Path, out var result);

        var aimod = result.Mods.Should().ContainSingle(m => m.Id == "com.humulus.aimod").Subject;
        aimod.TypeName.Should().Be("AIMod.AIMod");
        aimod.OnLoadExecuted.Should().BeTrue();
        aimod.Registered.Should().BeTrue();
        aimod.OnEnableExecuted.Should().BeTrue();
        aimod.Enabled.Should().BeTrue();
        aimod.Error.Should().BeNull();

        result.ModEventBridge.GetModStatus("com.humulus.aimod").Should().NotBeNull();
        GetModEventBridge(processor).Should().BeSameAs(result.ModEventBridge);
        _output.WriteLine($"[BridgeIds][HeadlessLoadsModsTest] register={GetObjectId(result.ModEventBridge)} processor={GetObjectId(processor)} processorBridge={GetObjectId(GetModEventBridge(processor))}");
    }

    [Fact]
    public void HeadlessCommandHandlersContainAIModTest()
    {
        using var modsRoot = CreateIsolatedAimodModsRoot();
        var processor = InitializeHeadlessAimodRuntime(modsRoot.Path, out var result);

        InvokeEnsureCommandHandlersInitialized(processor);

        var commandHandlers = GetCommandHandlers(processor);
        commandHandlers.Keys.Should().Contain("ai");
        result.ModEventBridge.GetAllCommandHandlers().Keys.Should().Contain("ai");
        _output.WriteLine($"[CommandKeys][HeadlessCommandHandlersContainAIModTest] {string.Join(",", commandHandlers.Keys.OrderBy(x => x))}");
    }

    [Fact]
    public void LateBridgeInjectionRefreshesModCommandsTest()
    {
        var processor = new MessageProcessor();
        InvokeEnsureCommandHandlersInitialized(processor);
        GetCommandHandlers(processor).Keys.Should().NotContain("latecmd");

        var bridge = CreateBridgeWithFakeCommandProvider("latecmd");
        processor.SetModEventBridge(bridge);

        InvokeEnsureCommandHandlersInitialized(processor);

        GetModEventBridge(processor).Should().BeSameAs(bridge);
        GetCommandHandlers(processor).Keys.Should().Contain("latecmd");
        bridge.GetAllCommandHandlers().Keys.Should().Contain("latecmd");
        _output.WriteLine($"[BridgeIds][LateBridgeInjectionRefreshesModCommandsTest] bridge={GetObjectId(bridge)} processorBridge={GetObjectId(GetModEventBridge(processor))}");
    }

    [Fact]
    public void HeadlessSubcommandProviderReachableTest()
    {
        using var modsRoot = CreateIsolatedAimodModsRoot();
        InitializeHeadlessAimodRuntime(modsRoot.Path, out var result);

        var providers = result.ModEventBridge.GetSubcommandProviders();
        providers.Should().ContainSingle(p => p.GetType().FullName == "AIMod.AIMod");

        var provider = providers.Single(p => p.GetType().FullName == "AIMod.AIMod");
        var msg = new Msg(10001, 20002, ".team addai", MessageSource.group);

        provider.HandleSubcommand("team", "addai", string.Empty, msg)
            .Should().Be("格式：.team addai 角色名");
        provider.HandleSubcommand("team", "listai", string.Empty, msg)
            .Should().Contain("还没有加入任何队伍");
        provider.HandleSubcommand("log", "on", string.Empty, msg)
            .Should().Contain("还没有加入任何队伍");

        _output.WriteLine($"[SubcommandProviders][HeadlessSubcommandProviderReachableTest] count={providers.Count} types={string.Join(",", providers.Select(p => p.GetType().FullName))}");
    }

    private static MessageProcessor InitializeHeadlessAimodRuntime(string modsPath, out RuntimeModInitializationResult result)
    {
        var serviceProvider = ServiceBootstrapper.BuildServices(StartupMode.Console);
        Action validate = () => ServiceBootstrapper.ValidateServices(serviceProvider);
        validate.Should().NotThrow("Console mode intentionally does not register IMessageChannel");

        MessageProcessor.EnsureInitialized();
        var processor = MessageProcessor.Instance;
        processor.Should().NotBeNull();

        result = RuntimeModInitializer.InitializeModsForRuntime(
            "HeadlessTest",
            modsPath,
            processor,
            forceReload: true);

        return processor!;
    }

    private static ModEventBridge CreateBridgeWithFakeCommandProvider(string commandName)
    {
        var distribution = MessageDistribution.GetInstance();
        var modContext = new ModContextImpl(distribution, "fake");
        var bridge = new ModEventBridge(modContext);
        var plugin = new FakeCommandMod(commandName);
        var metadata = new ModMetadata
        {
            Id = "com.test.fakecommand",
            Name = "Fake Command Mod",
            Version = "1.0.0",
            Author = "Tests",
            Description = "Test command provider",
            DllFileName = "FakeCommand.dll",
            PluginClassName = typeof(FakeCommandMod).FullName,
            ApiVersion = "1.0",
            Priority = 100
        };

        bridge.RegisterMod(plugin, metadata, isEnabled: true);
        return bridge;
    }

    private static void InvokeEnsureCommandHandlersInitialized(MessageProcessor processor)
    {
        EnsureCommandHandlersInitializedMethod.Invoke(processor, Array.Empty<object>());
    }

    private static ConcurrentDictionary<string, Action<string, Msg>> GetCommandHandlers(MessageProcessor processor)
    {
        return (ConcurrentDictionary<string, Action<string, Msg>>)CommandHandlersField.GetValue(processor)!;
    }

    private static ModEventBridge? GetModEventBridge(MessageProcessor processor)
    {
        return (ModEventBridge?)ModEventBridgeField.GetValue(processor);
    }

    private static string GetObjectId(object? instance)
    {
        return instance is null ? "null" : RuntimeHelpers.GetHashCode(instance).ToString();
    }

    private static TemporaryDirectory CreateIsolatedAimodModsRoot()
    {
        var repoRoot = FindRepoRoot();
        var releaseDir = Path.Combine(repoRoot, "Mods", "AIMod", "bin", "Release", "net10.0-windows");
        Directory.Exists(releaseDir).Should().BeTrue("AIMod Release output should exist after building Release");

        var tempRoot = new TemporaryDirectory("AIModRuntimeDiagnostics");
        var targetDir = Path.Combine(tempRoot.Path, "AIMod");
        CopyDirectory(releaseDir, targetDir);
        return tempRoot;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MDiceV2.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root from test output directory.");
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(targetDir, fileName), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDir))
        {
            var childName = Path.GetFileName(directory);
            CopyDirectory(directory, Path.Combine(targetDir, childName));
        }
    }

    private static void ResetStaticRuntimeState()
    {
        try
        {
            MessageProcessor.Instance?.Dispose(skipSave: true);
        }
        catch
        {
        }

        RuntimeModInitializer.UnloadCurrent();
        SetStaticField(typeof(MessageProcessor), "<Instance>k__BackingField", null);
        SetStaticField(typeof(MessageDistribution), "<Instance>k__BackingField", null);
        SetStaticField(typeof(MessageDistribution), "_subscribed", false);
        SetStaticField(typeof(NavigationPanelRegistry), "_instance", null);
    }

    private static void SetStaticField(Type type, string fieldName, object? value)
    {
        var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        if (field != null)
            field.SetValue(null, value);
    }

    private sealed class FakeCommandMod : IModPlugin, ICommandProvider
    {
        private readonly string _commandName;

        public FakeCommandMod(string commandName)
        {
            _commandName = commandName;
        }

        public string ModId => "com.test.fakecommand";
        public string ModName => "Fake Command Mod";
        public string Version => "1.0.0";
        public string Author => "Tests";
        public void OnLoad() { }
        public void OnEnable() { }
        public void OnDisable() { }
        public void OnUnload() { }
        public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted) => null;
        public ModMessageResult? OnPrivateMessage(long userId, string content) => null;

        public Dictionary<string, Func<string, object, string?>> GetCommandHandlers()
        {
            return new Dictionary<string, Func<string, object, string?>>
            {
                [_commandName] = (_, _) => "late command handled"
            };
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; }

        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}

[CollectionDefinition(nameof(AIModRuntimeDiagnosticsCollection), DisableParallelization = true)]
public sealed class AIModRuntimeDiagnosticsCollection
{
}
