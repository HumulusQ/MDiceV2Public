using System.IO.Compression;
using System.Text;
using FluentAssertions;
using MDiceV2.Core.Mod;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class PortableModManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MDiceV2PortableTests", Guid.NewGuid().ToString("N"));
    private readonly MessageDistribution _distribution;
    private readonly MessageProcessor _processor;
    private readonly ModContextImpl _context;
    private readonly ModEventBridge _bridge;
    private readonly PortableModManager _manager;

    public PortableModManagerTests()
    {
        Directory.CreateDirectory(_root);
        _distribution = MessageDistribution.GetInstance();
        _processor = new MessageProcessor();
        _context = new ModContextImpl(_distribution, "portable-tests");
        _bridge = new ModEventBridge(_context);
        _manager = new PortableModManager(_root, _bridge, _context, _distribution, _processor);
    }

    [Fact]
    public void ManagementCommand_PreservesOriginalArgumentCasing()
    {
        var plugin = new ManagementTestMod();
        _bridge.RegisterMod(plugin, Metadata(), isEnabled: true);

        var response = _manager.ExecuteManagementCommand("testmod", "echo", "ApiKey-AbC123", new object());

        response.Should().Be("ApiKey-AbC123");
        _manager.GetMenu("testmod").Should().Contain("echo").And.Contain("management test");
    }

    [Fact]
    public async Task StandardMod_CanBeDisabledAndEnabledThroughSharedLifecycleService()
    {
        var plugin = new ManagementTestMod();
        _bridge.RegisterMod(plugin, Metadata(), isEnabled: true);

        (await _manager.SetEnabledAsync("testmod", false)).Success.Should().BeTrue();
        plugin.DisableCount.Should().Be(1);
        _bridge.GetModStatus(plugin.ModId)!.Value.IsEnabled.Should().BeFalse();

        (await _manager.SetEnabledAsync("testmod", true)).Success.Should().BeTrue();
        plugin.EnableCount.Should().Be(1);
        _bridge.GetModStatus(plugin.ModId)!.Value.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task PackageWithTraversalEntry_IsRejectedBeforeExtraction()
    {
        var package = Path.Combine(_root, "traversal.mmod");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mod.json", ValidManifest());
            WriteEntry(archive, "../outside.dll", "bad");
        }

        var result = await _manager.InstallPackageAsync(package);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("目录穿越");
        File.Exists(Path.Combine(_root, "outside.dll")).Should().BeFalse();
    }

    [Fact]
    public async Task PackageWithoutRegisteredCommandName_IsRejected()
    {
        var package = Path.Combine(_root, "missing-name.mmod");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mod.json", ValidManifest().Replace("\"commandName\":\"portabletest\",", string.Empty));
            WriteEntry(archive, "PortableTest.dll", "not-loaded");
        }

        var result = await _manager.InstallPackageAsync(package);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("commandName");
    }

    [Fact]
    public void Unregister_RemovesModFromMessageAndCommandRoutingBeforeUnload()
    {
        var plugin = new ManagementTestMod();
        _bridge.RegisterMod(plugin, Metadata(), isEnabled: true);

        _bridge.UnregisterMod(plugin.ModId).Should().BeTrue();

        plugin.DisableCount.Should().Be(1);
        plugin.UnloadCount.Should().Be(1);
        _bridge.GetModStatus(plugin.ModId).Should().BeNull();
        _bridge.InvokeGroupMessage(1, 2, "hello", false).Should().BeNull();
    }

    [Fact]
    public async Task ValidPortablePackage_InstallsTogglesAndReloadsInProcess()
    {
        var package = CreateFixturePackage("fixture-v1.mmod", "1.0.0");

        var installed = await _manager.InstallPackageAsync(package);
        installed.Success.Should().BeTrue(installed.Message);
        _manager.FindByCommandName("fixture")!.IsEnabled.Should().BeTrue();
        _manager.ExecuteManagementCommand("fixture", "echo", "CaseSensitive-Key", new object())
            .Should().Be("CaseSensitive-Key");
        _manager.ExecuteManagementCommand("fixture", "data-dir", "", new object())
            .Should().Be(Path.Combine(_root, ".portable", "fixture", "data"));

        (await _manager.SetEnabledAsync("fixture", false)).Success.Should().BeTrue();
        _manager.FindByCommandName("fixture")!.IsEnabled.Should().BeFalse();
        (await _manager.SetEnabledAsync("fixture", true)).Success.Should().BeTrue();
        (await _manager.ReloadAsync("fixture")).Success.Should().BeTrue();
    }

    [Fact]
    public async Task BrokenUpdate_LeavesPreviouslyRunningVersionActive()
    {
        var first = await _manager.InstallPackageAsync(CreateFixturePackage("fixture-good.mmod", "1.0.0"));
        first.Success.Should().BeTrue(first.Message);

        var broken = Path.Combine(_root, "fixture-broken.mmod");
        using (var archive = ZipFile.Open(broken, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mod.json", FixtureManifest("2.0.0"));
            WriteEntry(archive, "PortableFixture.dll", "not a managed assembly");
        }

        var update = await _manager.InstallPackageAsync(broken);

        update.Success.Should().BeFalse();
        update.Message.Should().Contain("旧版本仍在运行");
        _manager.FindByCommandName("fixture")!.Version.Should().Be("1.0.0");
        _manager.ExecuteManagementCommand("fixture", "echo", "still-running", new object())
            .Should().Be("still-running");
    }

    private static ModMetadata Metadata() => new()
    {
        Id = "com.test.management", CommandName = "testmod", PackageType = "standard",
        Name = "Management Test", Version = "1.0.0", Author = "Tests",
        Description = "management test", DllFileName = "test.dll",
        PluginClassName = typeof(ManagementTestMod).FullName, Priority = 10,
        ModType = "dll", SupportHotReload = false, ApiVersion = "1.0"
    };

    private static string ValidManifest() =>
        "{\"id\":\"com.test.portable\",\"commandName\":\"portabletest\",\"name\":\"Portable Test\",\"version\":\"1.0.0\",\"author\":\"Tests\",\"description\":\"test\",\"dllFileName\":\"PortableTest.dll\",\"pluginClassName\":\"Tests.PortableTest\",\"priority\":100,\"modType\":\"dll\",\"supportHotReload\":true,\"apiVersion\":\"1.0\",\"packageType\":\"portable\"}";

    private string CreateFixturePackage(string fileName, string version)
    {
        var package = Path.Combine(_root, fileName);
        using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
        WriteEntry(archive, "mod.json", FixtureManifest(version));
        var dllEntry = archive.CreateEntry("PortableFixture.dll", CompressionLevel.NoCompression);
        using var source = File.OpenRead(typeof(PortableFixtureMod).Assembly.Location);
        using var target = dllEntry.Open();
        source.CopyTo(target);
        return package;
    }

    private static string FixtureManifest(string version) =>
        $"{{\"id\":\"com.test.fixture\",\"commandName\":\"fixture\",\"name\":\"Fixture\",\"version\":\"{version}\",\"author\":\"Tests\",\"description\":\"load fixture\",\"dllFileName\":\"PortableFixture.dll\",\"pluginClassName\":\"{typeof(PortableFixtureMod).FullName}\",\"priority\":100,\"modType\":\"dll\",\"supportHotReload\":true,\"apiVersion\":\"1.0\",\"packageType\":\"portable\"}}";

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _bridge.UnloadAllMods();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class ManagementTestMod : IModPlugin, IModManagementCommandProvider
    {
        public string ModId => "com.test.management";
        public string ModName => "Management Test";
        public string Version => "1.0.0";
        public string Author => "Tests";
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public int UnloadCount { get; private set; }
        public void OnLoad() { }
        public void OnEnable() => EnableCount++;
        public void OnDisable() => DisableCount++;
        public void OnUnload() => UnloadCount++;
        public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted) => null;
        public ModMessageResult? OnPrivateMessage(long userId, string content) => null;
        public IReadOnlyCollection<ModManagementCommand> GetModManagementCommands() =>
            [new ModManagementCommand("echo", "echo original text", (args, _) => args)];
    }
}

public sealed class PortableFixtureMod : IModPlugin, IModManagementCommandProvider, IPortableModContextReceiver
{
    private string _dataDirectory = "not-injected";
    public PortableFixtureMod(IModContext context) { }
    public void SetPortableModContext(PortableModContext context) => _dataDirectory = context.DataDirectory;
    public string ModId => "com.test.fixture";
    public string ModName => "Fixture";
    public string Version => "1.0.0";
    public string Author => "Tests";
    public void OnLoad() { }
    public void OnEnable() { }
    public void OnDisable() { }
    public void OnUnload() { }
    public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted) => null;
    public ModMessageResult? OnPrivateMessage(long userId, string content) => null;
    public IReadOnlyCollection<ModManagementCommand> GetModManagementCommands() =>
        [
            new ModManagementCommand("echo", "echo original text", (args, _) => args),
            new ModManagementCommand("data-dir", "show injected directory", (_, _) => _dataDirectory)
        ];
}
