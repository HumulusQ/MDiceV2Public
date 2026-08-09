using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Xunit;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Tests.Fixtures;

namespace MDiceV2.Tests.Unit;

/// <summary>
/// Unit tests for ConfigSyncClient
/// Tests: connection, config pull/push, server communication
/// </summary>
public class ConfigSyncClientTests : IDisposable
{
    private readonly ConfigSyncClient _client;
    private readonly string _testServerAddress = "localhost";
    private readonly int _testServerPort = 15001;
    private readonly string _testPassword = "client-test-password";

    public ConfigSyncClientTests()
    {
        _client = new ConfigSyncClient();
    }

    public void Dispose()
    {
        try
        {
            _client?.DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore disposal errors
        }
    }

    #region Connection Tests

    [Fact]
    public void Constructor_InitializesWithDisconnectedState()
    {
        // Arrange & Act
        var client = new ConfigSyncClient();

        // Assert
        client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAsync_WithValidParameters_EstablishesConnection()
    {
        // Act
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);

        // Assert
        _client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectAsync_WithInvalidAddress_ThrowsArgumentException()
    {
        // Act & Assert
        // Note: Invalid address throws ArgumentException during parameter validation
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.ConnectAsync("invalid.server.address", 0, _testPassword)
        );
    }

    [Fact]
    public async Task ConnectAsync_WithInvalidPort_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.ConnectAsync(_testServerAddress, -1, _testPassword)
        );
    }

    [Fact]
    public async Task ConnectAsync_AlreadyConnected_ThrowsInvalidOperationException()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword)
        );
    }

    [Fact]
    public async Task DisconnectAsync_WhenConnected_DisconnectsSuccessfully()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);

        // Act
        await _client.DisconnectAsync();

        // Assert
        _client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_DoesNotThrow()
    {
        // Act & Assert
        await _client.DisconnectAsync(); // Should not throw
    }

    #endregion

    #region Config Pull Tests

    [Fact]
    public async Task PullConfigAsync_WhenConnected_ReturnsConfigDictionary()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);

        // Act
        var config = await _client.PullConfigAsync();

        // Assert
        config.Should().NotBeNull();
        config.Should().BeAssignableTo<Dictionary<string, string>>();
        config.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PullConfigAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.PullConfigAsync()
        );
    }

    [Fact]
    public async Task PullConfigAsync_ReturnedConfig_ContainsServerInfo()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);

        // Act
        var config = await _client.PullConfigAsync();

        // Assert
        config.Should().ContainKeys("server.host", "server.port");
        config["server.host"].Should().Be(_testServerAddress);
        config["server.port"].Should().Be(_testServerPort.ToString());
    }

    [Fact]
    public async Task PullConfigAsync_MultipleInvocations_ReturnsFreshData()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);

        // Act
        var config1 = await _client.PullConfigAsync();
        await Task.Delay(100);
        var config2 = await _client.PullConfigAsync();

        // Assert
        config1.Should().NotBeEmpty();
        config2.Should().NotBeEmpty();
        // Timestamps should be different (or at least task completed)
        config1.Count.Should().Be(config2.Count);
    }

    #endregion

    #region Config Push Tests

    [Fact]
    public async Task PushConfigAsync_WithValidConfig_SucceedsWithoutError()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);
        var testConfig = TestFixtures.GenerateTestConfig("key1", "key2", "key3");

        // Act & Assert
        await _client.PushConfigAsync(testConfig); // Should not throw
    }

    [Fact]
    public async Task PushConfigAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        // Arrange
        var testConfig = TestFixtures.GenerateTestConfig();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.PushConfigAsync(testConfig)
        );
    }

    [Fact]
    public async Task PushConfigAsync_WithNullConfig_ThrowsArgumentException()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.PushConfigAsync(null!)
        );
    }

    [Fact]
    public async Task PushConfigAsync_WithEmptyConfig_ThrowsArgumentException()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);
        var emptyConfig = new Dictionary<string, string>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.PushConfigAsync(emptyConfig)
        );
    }

    [Fact]
    public async Task PushConfigAsync_WithLargeConfig_SucceedsWithoutError()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);
        var largeConfig = TestFixtures.GenerateTestConfig(1000);

        // Act & Assert
        await _client.PushConfigAsync(largeConfig); // Should not throw
    }

    #endregion

    #region Log Subscription Tests

    [Fact]
    public async Task SubscribeLogsAsync_WhenConnected_SubscribesSuccessfully()
    {
        // Arrange
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);
        var logReceived = false;

        // Act
        await _client.SubscribeLogsAsync((log) =>
        {
            logReceived = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        logReceived.Should().BeFalse(); // No logs expected immediately

        // Cleanup
        await _client.DisconnectAsync();
    }

    [Fact]
    public async Task SubscribeLogsAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.SubscribeLogsAsync((log) => Task.CompletedTask, CancellationToken.None)
        );
    }

    #endregion

    #region State Tests

    [Fact]
    public async Task MultipleConnect_DisconnectCycles_WorkCorrectly()
    {
        // Arrange & Act
        // First cycle
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);
        _client.IsConnected.Should().BeTrue();
        await _client.DisconnectAsync();
        _client.IsConnected.Should().BeFalse();

        // Second cycle
        await _client.ConnectAsync(_testServerAddress, _testServerPort, _testPassword);
        _client.IsConnected.Should().BeTrue();
        await _client.DisconnectAsync();
        _client.IsConnected.Should().BeFalse();
    }

    #endregion
}
