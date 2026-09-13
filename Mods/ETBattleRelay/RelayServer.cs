using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ETBattleRelay;

public sealed class RelayServer : IDisposable
{
    private readonly RelayOptions _options;
    private readonly RelayHub _hub;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _connectionsGate = new();
    private readonly HashSet<RelayWebSocketConnection> _connections = [];
    private Task? _acceptTask;
    private Task? _maintenanceTask;

    public RelayServer(RelayOptions options)
    {
        _options = options;
        _options.Validate();
        _hub = new RelayHub(options);
        _listener.Prefixes.Add(options.HttpPrefix);
    }

    public bool IsRunning => _listener.IsListening;

    public void Start()
    {
        if (IsRunning) return;
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_shutdown.Token);
        _maintenanceTask = MaintenanceLoopAsync(_shutdown.Token);
    }

    public async Task StopAsync()
    {
        if (!_shutdown.IsCancellationRequested) _shutdown.Cancel();
        if (_listener.IsListening) _listener.Stop();
        RelayWebSocketConnection[] connections;
        lock (_connectionsGate) connections = _connections.ToArray();
        try
        {
            await Task.WhenAll(connections.Select(connection => connection.CloseAsync("server_stopping"))).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException) { }
        var tasks = new[] { _acceptTask, _maintenanceTask }.Where(task => task is not null).Cast<Task>().ToArray();
        if (tasks.Length == 0) return;
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(cancellationToken); }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { break; }
            _ = HandleContextAsync(context, cancellationToken);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = (int)HttpStatusCode.UpgradeRequired;
            context.Response.Close();
            return;
        }
        var requestedProtocol = context.Request.Headers["X-ET-Battle-Protocol"];
        if (!string.IsNullOrEmpty(requestedProtocol) && requestedProtocol != RelayHub.Protocol)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.Close();
            return;
        }
        try
        {
            var webSocketContext = await context.AcceptWebSocketAsync(null);
            var connection = new RelayWebSocketConnection(webSocketContext.WebSocket, _hub, _options, RemoveConnection);
            lock (_connectionsGate) _connections.Add(connection);
            await connection.RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine($"[ETBattleRelay] WebSocket connection closed with {ex.GetType().Name}.");
            try { context.Response.Abort(); } catch { }
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try { while (await timer.WaitForNextTickAsync(cancellationToken)) _hub.Cleanup(); }
        catch (OperationCanceledException) { }
    }

    private void RemoveConnection(RelayWebSocketConnection connection)
    { lock (_connectionsGate) _connections.Remove(connection); }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Close();
        _shutdown.Dispose();
    }
}

internal sealed class RelayWebSocketConnection : IRelayConnection
{
    private readonly WebSocket _socket;
    private readonly RelayHub _hub;
    private readonly RelayOptions _options;
    private readonly Action<RelayWebSocketConnection> _onClosed;
    private readonly Channel<string> _outbound;
    private readonly CancellationTokenSource _localStop = new();
    private readonly SlidingWindowLimiter _messageLimiter;
    private readonly SlidingWindowLimiter _byteLimiter;
    private readonly SlidingWindowLimiter _joinLimiter;
    private long _lastHeartbeatAck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private string? _pendingHeartbeatNonce;
    private int _closed;

    internal RelayWebSocketConnection(WebSocket socket, RelayHub hub, RelayOptions options, Action<RelayWebSocketConnection> onClosed)
    {
        _socket = socket;
        _hub = hub;
        _options = options;
        _onClosed = onClosed;
        _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(options.OutboundQueueCapacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        _messageLimiter = new SlidingWindowLimiter(options.MessagesPerSecond, TimeSpan.FromSeconds(1));
        _byteLimiter = new SlidingWindowLimiter(options.BytesPerSecond, TimeSpan.FromSeconds(1));
        _joinLimiter = new SlidingWindowLimiter(options.JoinAttemptsPerMinute, TimeSpan.FromMinutes(1));
    }

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");

    public bool TrySend(string message)
    {
        if (_closed != 0 || !_outbound.Writer.TryWrite(message))
        {
            RequestClose("outbound_queue_full");
            return false;
        }
        return true;
    }

    public void RequestClose(string reason) => _ = CloseAsync(reason);

    internal async Task RunAsync(CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _localStop.Token);
        var token = linked.Token;
        var send = SendLoopAsync(token);
        var heartbeat = HeartbeatLoopAsync(token);
        try { await ReceiveLoopAsync(token); }
        finally
        {
            await CloseAsync("connection_closed");
            try { await Task.WhenAll(send, heartbeat); } catch (OperationCanceledException) { }
        }
    }

    internal async Task CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _localStop.Cancel();
        _outbound.Writer.TryComplete();
        _hub.Disconnect(this);
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason[..Math.Min(reason.Length, 120)], CancellationToken.None);
        }
        catch { }
        finally { _socket.Dispose(); _onClosed(this); }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(32 * 1024, _options.MaxFrameBytes)];
        using var message = new MemoryStream();
        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) { RequestClose("text_frames_only"); return; }
            if (message.Length + result.Count > _options.MaxFrameBytes) { RequestClose("frame_too_large"); return; }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var length = checked((int)message.Length);
            if (!_messageLimiter.TryConsume() || !_byteLimiter.TryConsume(length)) { RequestClose("rate_limit"); return; }
            var raw = Encoding.UTF8.GetString(message.GetBuffer(), 0, length);
            message.SetLength(0);
            var (type, nonce) = EnvelopeMetadata(raw);
            if (type == "join_room" && !_joinLimiter.TryConsume()) { RequestClose("join_rate_limit"); return; }
            if (type == "heartbeat_ack" && string.Equals(nonce, Volatile.Read(ref _pendingHeartbeatNonce), StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _lastHeartbeatAck, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Interlocked.Exchange(ref _pendingHeartbeatNonce, null);
            }
            _hub.Handle(this, raw);
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken))
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var lastAck = DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastHeartbeatAck));
            if (DateTimeOffset.UtcNow - lastAck > TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds))
            { RequestClose("heartbeat_timeout"); return; }
            var nonce = RelaySecrets.Token()[..16];
            Interlocked.Exchange(ref _pendingHeartbeatNonce, nonce);
            if (!TrySend(_hub.CreateHeartbeat(nonce))) return;
        }
    }

    private static (string Type, string Nonce) EnvelopeMetadata(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return ("", "");
            var type = doc.RootElement.TryGetProperty("type", out var typeValue) ? typeValue.GetString() ?? "" : "";
            var nonce = doc.RootElement.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("nonce", out var nonceValue)
                && nonceValue.ValueKind == JsonValueKind.String ? nonceValue.GetString() ?? "" : "";
            return (type, nonce);
        }
        catch (JsonException) { return ("", ""); }
    }
}
