using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MDiceV2.Core.Infrastructure;
using Xunit;

namespace MDiceV2.Tests;

public sealed class BotFrameworkRuntimeManagerTests
{
    [Theory]
    [InlineData(123456789, "123456789")]
    [InlineData(0, null)]
    public async Task LoginProbeReturnsAuthenticatedAccount(long userId, string? expected)
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var socketContext = await context.AcceptWebSocketAsync(null);
            using var socket = socketContext.WebSocket;
            var buffer = new byte[4096];
            var request = await socket.ReceiveAsync(buffer, CancellationToken.None);
            var payload = Encoding.UTF8.GetString(buffer, 0, request.Count);
            Assert.Contains("get_login_info", payload);
            Assert.Contains("mdice_login_probe", payload);

            var response = Encoding.UTF8.GetBytes(
                $$"""{"status":"ok","retcode":0,"data":{"user_id":{{userId}},"nickname":"test"},"echo":"mdice_login_probe"}""");
            await socket.SendAsync(response, WebSocketMessageType.Text, true, CancellationToken.None);
        });

        var result = await InvokeLoginProbeAsync($"ws://127.0.0.1:{port}/");
        await server;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void SnowLumaGlobalConfigRemovesSharedMDiceEndpointAndEnablesAutoLoad()
    {
        var runtime = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Directory.CreateDirectory(Path.Combine(runtime, "config")).FullName;
            File.WriteAllText(Path.Combine(configDirectory, "onebot.json"),
                """{"networks":{"wsServers":[{"name":"mdice","port":3001},{"name":"user","port":8080}]}}""");
            File.WriteAllText(Path.Combine(configDirectory, "runtime.json"), """{"hookAutoLoad":false}""");

            InvokePrivateStatic("EnsureSnowLumaOneBotConfig", runtime);

            var oneBot = JsonNode.Parse(File.ReadAllText(Path.Combine(configDirectory, "onebot.json")))!;
            var adapters = oneBot["networks"]!["wsServers"]!.AsArray();
            Assert.Single(adapters);
            Assert.Equal("user", adapters[0]!["name"]!.GetValue<string>());
            var runtimeConfig = JsonNode.Parse(File.ReadAllText(Path.Combine(configDirectory, "runtime.json")))!;
            Assert.True(runtimeConfig["hookAutoLoad"]!.GetValue<bool>());
            AssertUtf8WithoutBom(Path.Combine(configDirectory, "onebot.json"));
            AssertUtf8WithoutBom(Path.Combine(configDirectory, "runtime.json"));
        }
        finally
        {
            Directory.Delete(runtime, true);
        }
    }

    [Fact]
    public void SnowLumaAccountConfigUsesDedicatedEndpointAndPreservesUserAdapters()
    {
        var runtime = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Directory.CreateDirectory(Path.Combine(runtime, "config")).FullName;
            var configPath = Path.Combine(configDirectory, "onebot_123456789.json");
            File.WriteAllText(configPath,
                """{"networks":{"wsServers":[{"name":"user","port":8080},{"name":"mdice","port":3001}]}}""");

            InvokePrivateStatic("ConfigureSnowLumaAccount", runtime, "123456789", 32123);

            var config = JsonNode.Parse(File.ReadAllText(configPath))!;
            var adapters = config["networks"]!["wsServers"]!.AsArray();
            Assert.Equal(2, adapters.Count);
            Assert.Contains(adapters, item => item!["name"]!.GetValue<string>() == "user");
            var managed = Assert.Single(adapters, item => item!["name"]!.GetValue<string>() == "mdice");
            Assert.Equal(32123, managed!["port"]!.GetValue<int>());
            Assert.Equal("127.0.0.1", managed["host"]!.GetValue<string>());
            AssertUtf8WithoutBom(configPath);
        }
        finally
        {
            Directory.Delete(runtime, true);
        }
    }

    [Theory]
    [InlineData(ManagedBotFramework.SnowLuma, "launcher.bat")]
    [InlineData(ManagedBotFramework.NapCat, "launcher-win10.bat")]
    public void RuntimeDiscoverySelectsLauncherScriptInsteadOfCore(
        ManagedBotFramework framework,
        string expectedLauncher)
    {
        var runtime = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(runtime, "Core.Dice"), string.Empty);
            File.WriteAllText(Path.Combine(runtime, expectedLauncher), string.Empty);

            var result = InvokePrivateStatic("FindLauncherInExtractedDirectory", runtime, framework) as string;

            Assert.Equal(Path.Combine(runtime, expectedLauncher), result);
        }
        finally
        {
            Directory.Delete(runtime, true);
        }
    }

    [Theory]
    [InlineData("12345", true)]
    [InlineData("123456789012", true)]
    [InlineData("1234", false)]
    [InlineData("012345", false)]
    [InlineData("12345a", false)]
    public void ManualSnowLumaAccountValidationRejectsInvalidFileNames(string userId, bool expected)
    {
        var result = InvokePrivateStatic("IsValidSnowLumaAccountId", userId);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SnowLumaAgreementVersionMatchesUpstreamContentHashAlgorithm()
    {
        var result = InvokePrivateStatic(
            "ComputeSnowLumaAgreementVersion",
            "EULA text",
            "Privacy text");

        Assert.Equal("1beaa2472cdae053", result);
    }

    [Fact]
    public void ExistingActiveFrameworkStateDefaultsAutoConnectToEnabled()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "active-framework.json"),
                """{"Framework":0,"WebSocketUrl":"ws://127.0.0.1:32101/"}""");

            var state = InvokePrivateStatic("ReadActiveState", root);

            Assert.NotNull(state);
            var autoConnect = state.GetType().GetProperty("AutoConnect")?.GetValue(state);
            Assert.Equal(true, autoConnect);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<string?> InvokeLoginProbeAsync(string url)
    {
        var method = typeof(BotFrameworkRuntimeManager).GetMethod(
            "ProbeLoginAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = method.Invoke(null, new object[] { url, CancellationToken.None }) as Task<string?>;
        Assert.NotNull(task);
        return await task;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static object? InvokePrivateStatic(string methodName, params object[] arguments)
    {
        var method = typeof(BotFrameworkRuntimeManager).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, arguments);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MDiceV2.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertUtf8WithoutBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 &&
                     bytes[0] == 0xEF &&
                     bytes[1] == 0xBB &&
                     bytes[2] == 0xBF);
    }
}
