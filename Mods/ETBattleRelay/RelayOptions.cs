using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ETBattleRelay;

public sealed class RelayOptions
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("http_prefix")] public string HttpPrefix { get; set; } = "http://127.0.0.1:8787/et-battle/";
    [JsonPropertyName("max_peers")] public int MaxPeers { get; set; } = 8;
    [JsonPropertyName("recovery_seconds")] public int RecoverySeconds { get; set; } = 300;
    [JsonPropertyName("max_frame_bytes")] public int MaxFrameBytes { get; set; } = 512 * 1024;
    [JsonPropertyName("messages_per_second")] public int MessagesPerSecond { get; set; } = 80;
    [JsonPropertyName("bytes_per_second")] public int BytesPerSecond { get; set; } = 2 * 1024 * 1024;
    [JsonPropertyName("join_attempts_per_minute")] public int JoinAttemptsPerMinute { get; set; } = 12;
    [JsonPropertyName("outbound_queue_capacity")] public int OutboundQueueCapacity { get; set; } = 128;
    [JsonPropertyName("heartbeat_seconds")] public int HeartbeatSeconds { get; set; } = 15;
    [JsonPropertyName("heartbeat_timeout_seconds")] public int HeartbeatTimeoutSeconds { get; set; } = 45;
    [JsonPropertyName("ice_servers")] public List<IceServerOptions> IceServers { get; set; } = [];

    public static RelayOptions Load()
    {
        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        var configuredPath = Environment.GetEnvironmentVariable("ETBATTLE_RELAY_CONFIG");
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(assemblyDirectory, "etbattle-relay.json")
            : Path.GetFullPath(configuredPath);
        RelayOptions options = new();
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            options = JsonSerializer.Deserialize<RelayOptions>(stream, JsonDefaults.Options) ?? new RelayOptions();
        }
        if (TryReadBool("ETBATTLE_RELAY_ENABLED", out var enabled))
            options.Enabled = enabled;
        var prefix = Environment.GetEnvironmentVariable("ETBATTLE_RELAY_HTTP_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
            options.HttpPrefix = prefix.Trim();
        var iceServers = Environment.GetEnvironmentVariable("ETBATTLE_RELAY_ICE_SERVERS");
        if (!string.IsNullOrWhiteSpace(iceServers))
        {
            options.IceServers = iceServers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(url => new IceServerOptions { Urls = [url] }).ToList();
        }
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (!Uri.TryCreate(HttpPrefix, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("http_prefix must be an absolute http:// HttpListener prefix.");
        if (!uri.IsLoopback)
            throw new InvalidOperationException("Relay must bind to loopback; publish it through a TLS reverse proxy.");
        if (!HttpPrefix.EndsWith('/'))
            HttpPrefix += "/";
        MaxPeers = Math.Clamp(MaxPeers, 2, 8);
        RecoverySeconds = Math.Clamp(RecoverySeconds, 30, 3600);
        MaxFrameBytes = Math.Clamp(MaxFrameBytes, 16 * 1024, 2 * 1024 * 1024);
        MessagesPerSecond = Math.Clamp(MessagesPerSecond, 5, 500);
        BytesPerSecond = Math.Clamp(BytesPerSecond, 64 * 1024, 16 * 1024 * 1024);
        JoinAttemptsPerMinute = Math.Clamp(JoinAttemptsPerMinute, 2, 120);
        OutboundQueueCapacity = Math.Clamp(OutboundQueueCapacity, 16, 1024);
        HeartbeatSeconds = Math.Clamp(HeartbeatSeconds, 5, 120);
        HeartbeatTimeoutSeconds = Math.Max(HeartbeatSeconds * 2, Math.Clamp(HeartbeatTimeoutSeconds, 10, 300));
        IceServers ??= [];
        foreach (var server in IceServers)
            server.Urls = server.Urls.Where(url => url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
    }

    private static bool TryReadBool(string name, out bool value)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (bool.TryParse(raw, out value)) return true;
        value = raw == "1";
        return raw is "1" or "0";
    }
}

public sealed class IceServerOptions
{
    [JsonPropertyName("urls")] public List<string> Urls { get; set; } = [];
    [JsonPropertyName("username")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Username { get; set; }
    [JsonPropertyName("credential")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Credential { get; set; }
}

internal static class JsonDefaults
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
