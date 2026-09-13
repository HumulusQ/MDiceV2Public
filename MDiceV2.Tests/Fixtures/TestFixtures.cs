using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace MDiceV2.Tests.Fixtures;

/// <summary>
/// Shared test fixtures for unit tests
/// </summary>
public class TestFixtures
{
    /// <summary>
    /// Generate a valid authentication token for testing
    /// </summary>
    public static string GenerateTestToken()
    {
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Generate a test configuration dictionary
    /// </summary>
    public static Dictionary<string, string> GenerateTestConfig(int size = 10)
    {
        var config = new Dictionary<string, string>();
        for (int i = 0; i < size; i++)
        {
            config[$"key_{i}"] = $"value_{i}_{Guid.NewGuid():N}";
        }
        return config;
    }

    /// <summary>
    /// Generate test configuration with specific keys
    /// </summary>
    public static Dictionary<string, string> GenerateTestConfig(params string[] keys)
    {
        var config = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            config[key] = $"value_{key}_{Guid.NewGuid():N}";
        }
        return config;
    }

    /// <summary>
    /// Default timeout for async operations (3 seconds)
    /// </summary>
    public const int DefaultTimeout = 3000;

    /// <summary>
    /// Extended timeout for performance tests (10 seconds)
    /// </summary>
    public const int ExtendedTimeout = 10000;

    /// <summary>
    /// Standard port range for test servers
    /// </summary>
    public static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Collection fixture for ConfigSync tests - ensures test isolation
/// </summary>
[CollectionDefinition("ConfigSync Collection")]
public class ConfigSyncCollection : ICollectionFixture<ConfigSyncFixture>
{
    // This has no code, and never creates an instance of ConfigSyncCollection.
    // It's just a marker used to define the collection.
}

/// <summary>
/// Fixture for ConfigSync server/client setup
/// </summary>
public class ConfigSyncFixture : IAsyncLifetime
{
    private readonly int _port;

    public ConfigSyncFixture()
    {
        _port = TestFixtures.GetAvailablePort();
    }

    public string ServerAddress => $"localhost:{_port}";

    public async Task InitializeAsync()
    {
        // Server initialization would happen here
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Cleanup would happen here
        await Task.CompletedTask;
    }
}

/// <summary>
/// Test output helper for debugging
/// </summary>
public class TestOutputHelper
{
    private readonly ITestOutputHelper? _output;

    public TestOutputHelper(ITestOutputHelper? output = null)
    {
        _output = output;
    }

    public void WriteLine(string message)
    {
        _output?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    public void WriteLine(string format, params object[] args)
    {
        _output?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {string.Format(format, args)}");
    }
}
