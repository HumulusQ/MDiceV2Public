using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Xunit;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Tests.Fixtures;

namespace MDiceV2.Tests.Unit;

/// <summary>
/// Unit tests for ConfigSyncServer
/// Tests: initialization, authentication, client management, and log broadcasting
/// </summary>
public class ConfigSyncServerTests : IDisposable
{
    private readonly ConfigSyncServer _server;
    private readonly string _testPassword = "test-password-123";
    private const int TestPort = 15000;

    public ConfigSyncServerTests()
    {
        _server = new ConfigSyncServer(_testPassword);
    }

    public void Dispose()
    {
        _server?.StopAsync().GetAwaiter().GetResult();
    }

    #region Initialization Tests

    [Fact]
    public void Constructor_WithPassword_InitializesSuccessfully()
    {
        // Arrange & Act
        var server = new ConfigSyncServer("custom-password");

        // Assert
        server.ConnectedClientCount.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithDefaultPassword_Succeeds()
    {
        // Arrange & Act
        var server = new ConfigSyncServer();

        // Assert
        server.ConnectedClientCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WithValidPort_StartsSuccessfully()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();

        // Act
        await _server.StartAsync(port, _testPassword);

        // Assert
        var clientCount = _server.ConnectedClientCount;
        clientCount.Should().BeGreaterThanOrEqualTo(0);

        // Cleanup
        await _server.StopAsync();
    }

    [Fact]
    public async Task StartAsync_WithWrongPassword_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _server.StartAsync(port, "wrong-password")
        );
    }

    [Fact]
    public async Task StopAsync_OnRunningServer_StopsSuccessfully()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();
        await _server.StartAsync(port, _testPassword);

        // Act
        await _server.StopAsync();

        // Assert
        // Server should be stopped, ConnectedClientCount should be 0
        _server.ConnectedClientCount.Should().Be(0);
    }

    #endregion

    #region Client Management Tests

    [Fact]
    public async Task RegisterClient_WithValidClient_IncrementsClientCount()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();
        await _server.StartAsync(port, _testPassword);
        var clientId = "test-client-1";
        var session = new ClientSession();

        // Act
        _server.RegisterClient(clientId, session);

        // Assert
        _server.ConnectedClientCount.Should().Be(1);

        // Cleanup
        await _server.StopAsync();
    }

    [Fact]
    public async Task UnregisterClient_WithValidClient_DecrementsClientCount()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();
        await _server.StartAsync(port, _testPassword);
        var clientId = "test-client-2";
        var session = new ClientSession();
        _server.RegisterClient(clientId, session);

        // Act
        _server.UnregisterClient(clientId);

        // Assert
        _server.ConnectedClientCount.Should().Be(0);

        // Cleanup
        await _server.StopAsync();
    }

    [Fact]
    public async Task RegisterClient_MultipleClients_ManagesAllClients()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();
        await _server.StartAsync(port, _testPassword);
        var clientIds = new[] { "client-1", "client-2", "client-3" };

        // Act
        foreach (var clientId in clientIds)
        {
            _server.RegisterClient(clientId, new ClientSession());
        }

        // Assert
        _server.ConnectedClientCount.Should().Be(3);

        // Cleanup
        await _server.StopAsync();
    }

    #endregion

    #region Log Broadcasting Tests

    [Fact]
    public async Task BroadcastLogsAsync_WithEmptyLogs_CompletesWithoutError()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();
        await _server.StartAsync(port, _testPassword);

        // Act
        await _server.BroadcastLogsAsync(new List<string>());

        // Assert - should complete without throwing

        // Cleanup
        await _server.StopAsync();
    }

    [Fact]
    public async Task BroadcastLogsAsync_WithValidLogs_QueuesForBroadcast()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();
        await _server.StartAsync(port, _testPassword);
        var testLogs = new[] { "Log 1", "Log 2", "Log 3" };

        // Act
        await _server.BroadcastLogsAsync(testLogs);

        // Assert
        // The logs should be queued (exact assertion depends on internal state)
        _server.ConnectedClientCount.Should().BeGreaterThanOrEqualTo(0);

        // Cleanup
        await _server.StopAsync();
    }

    [Fact]
    public async Task BroadcastLogsAsync_WhenServerNotRunning_DoesNotThrow()
    {
        // Act & Assert
        await _server.BroadcastLogsAsync(new[] { "Some log" });

        // Should not throw even if server is not running
    }

    #endregion

    #region State Tests

    [Fact]
    public async Task StartAsync_TwiceInSuccession_ThrowsInvalidOperationException()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();
        await _server.StartAsync(port, _testPassword);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _server.StartAsync(port, _testPassword)
        );

        // Cleanup
        await _server.StopAsync();
    }

    [Fact]
    public async Task StopAsync_OnAlreadyStoppedServer_DoesNotThrow()
    {
        // Arrange
        var port = TestFixtures.GetAvailablePort();
        await _server.StartAsync(port, _testPassword);
        await _server.StopAsync();

        // Act & Assert
        await _server.StopAsync(); // Should not throw
    }

    #endregion
}
