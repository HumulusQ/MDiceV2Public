using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ETBattleRelay;

if (args.Length >= 2 && args[0] == "--serve" && int.TryParse(args[1], out var servePort))
{
    var seconds = args.Length >= 3 && int.TryParse(args[2], out var parsedSeconds) ? Math.Clamp(parsedSeconds, 1, 300) : 30;
    var serveOptions = Options();
    serveOptions.Enabled = true;
    serveOptions.HttpPrefix = $"http://127.0.0.1:{servePort}/et-battle/";
    serveOptions.IceServers = [];
    using var serveServer = new RelayServer(serveOptions);
    serveServer.Start();
    Console.WriteLine($"ETBATTLE_RELAY_READY {servePort}");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    await serveServer.StopAsync();
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("room code and password", RoomCodeAndPassword),
    ("eight peer cap", EightPeerCap),
    ("direct host-star signalling", DirectSignalling),
    ("opaque relay routing", RelayRouting),
    ("disconnect resume and timeout", Recovery),
    ("rate limiter", RateLimiter),
    ("local websocket lifecycle", () => LocalWebSocketLifecycle().GetAwaiter().GetResult()),
    ("mod chat isolation", ModIsolation)
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"ETBattleRelay tests: {tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static RelayOptions Options() => new()
{
    Enabled = false,
    MaxPeers = 8,
    RecoverySeconds = 300,
    IceServers = [new IceServerOptions { Urls = ["stun:unit.test:3478"] }]
};

static void RoomCodeAndPassword()
{
    var hub = new RelayHub(Options());
    var host = new FakeConnection("host");
    hub.Handle(host, Request("create_room", new { nickname = "Host", mode = "relay", password = "correct horse", max_peers = 8 }));
    var created = host.Last("room_created");
    var code = created.GetProperty("room_code").GetString()!;
    Assert(code.Length == 8 && code.All(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c)), "room code format");
    Assert(created.GetProperty("ice_servers")[0].GetProperty("urls")[0].GetString() == "stun:unit.test:3478", "ICE config");
    var wrong = new FakeConnection("wrong");
    hub.Handle(wrong, Request("join_room", new { nickname = "Guest", room_code = code, password = "wrong" }));
    Assert(wrong.Last("error").GetProperty("code").GetString() == "invalid_password", "password rejection");
    var guest = new FakeConnection("guest");
    hub.Handle(guest, Request("join_room", new { nickname = "Guest", room_code = code, password = "correct horse" }));
    Assert(guest.Last("room_joined").GetProperty("players").GetArrayLength() == 2, "password join");
}

static void EightPeerCap()
{
    var hub = new RelayHub(Options());
    var host = new FakeConnection("host");
    hub.Handle(host, Request("create_room", new { nickname = "H", mode = "relay", max_peers = 8 }));
    var code = host.Last("room_created").GetProperty("room_code").GetString()!;
    for (var i = 1; i <= 7; i++)
    {
        var guest = new FakeConnection($"g{i}");
        hub.Handle(guest, Request("join_room", new { nickname = $"G{i}", room_code = code }));
        guest.Last("room_joined");
    }
    var overflow = new FakeConnection("overflow");
    hub.Handle(overflow, Request("join_room", new { nickname = "G8", room_code = code }));
    Assert(overflow.Last("error").GetProperty("code").GetString() == "room_full", "room cap");
}

static void DirectSignalling()
{
    var hub = new RelayHub(Options());
    var host = new FakeConnection("host");
    var guest = new FakeConnection("guest");
    hub.Handle(host, Request("create_room", new { nickname = "H", mode = "direct" }));
    var created = host.Last("room_created");
    var code = created.GetProperty("room_code").GetString()!;
    var hostId = created.GetProperty("peer_id").GetString()!;
    hub.Handle(guest, Request("join_room", new { nickname = "G", room_code = code }));
    host.Clear();
    hub.Handle(guest, Request("signal", new { room_code = code, target_peer_id = hostId, signal = new { type = "offer", sdp = "opaque-sdp" } }));
    Assert(host.Last("signal").GetProperty("signal").GetProperty("sdp").GetString() == "opaque-sdp", "signal passthrough");
    hub.Handle(guest, Request("relay", new { room_code = code, data = "YWJj" }));
    Assert(guest.Last("error").GetProperty("code").GetString() == "wrong_room_mode", "direct rejects relay");
}

static void RelayRouting()
{
    var hub = new RelayHub(Options());
    var host = new FakeConnection("host");
    var guest = new FakeConnection("guest");
    hub.Handle(host, Request("create_room", new { nickname = "H", mode = "relay" }));
    var code = host.Last("room_created").GetProperty("room_code").GetString()!;
    hub.Handle(guest, Request("join_room", new { nickname = "G", room_code = code }));
    host.Clear(); guest.Clear();
    hub.Handle(guest, Request("relay", new { room_code = code, target_peer_id = "", data = "c2VjcmV0LWJhdHRsZS1wYXlsb2Fk" }));
    Assert(host.Last("relay").GetProperty("data").GetString() == "c2VjcmV0LWJhdHRsZS1wYXlsb2Fk", "guest to host opaque routing");
    Assert(guest.Count == 0, "relay not echoed");
    host.Clear();
    hub.Handle(host, Request("relay", new { room_code = code, target_peer_id = "", data = "aG9zdC1zbmFwc2hvdA==" }));
    Assert(guest.Last("relay").GetProperty("data").GetString() == "aG9zdC1zbmFwc2hvdA==", "host broadcast");
    hub.Handle(host, Request("signal", new { room_code = code, target_peer_id = "nobody", signal = new { } }));
    Assert(host.Last("error").GetProperty("code").GetString() == "wrong_room_mode", "relay rejects signalling");
}

static void Recovery()
{
    var hub = new RelayHub(Options());
    var host = new FakeConnection("host");
    hub.Handle(host, Request("create_room", new { nickname = "H", mode = "relay" }));
    var created = host.Last("room_created");
    var code = created.GetProperty("room_code").GetString()!;
    var token = created.GetProperty("resume_token").GetString()!;
    hub.Disconnect(host);
    var resumed = new FakeConnection("resumed");
    hub.Handle(resumed, Request("resume_room", new { nickname = "H2", room_code = code, resume_token = token }));
    var resumePayload = resumed.Last("room_resumed");
    var rotated = resumePayload.GetProperty("resume_token").GetString()!;
    Assert(rotated != token, "token rotation");
    hub.Disconnect(resumed);
    var replay = new FakeConnection("replay");
    hub.Handle(replay, Request("resume_room", new { nickname = "H", room_code = code, resume_token = token }));
    Assert(replay.Last("error").GetProperty("code").GetString() == "invalid_resume_token", "old token invalidated");
    var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-301);
    var hub2 = new RelayHub(Options());
    var expiringHost = new FakeConnection("expiring");
    hub2.Handle(expiringHost, Request("create_room", new { nickname = "H", mode = "relay" }));
    hub2.Disconnect(expiringHost, expiredAt);
    hub2.Cleanup(DateTimeOffset.UtcNow);
    Assert(hub2.RoomCount == 0, "host timeout closes room");
}

static void RateLimiter()
{
    var limiter = new SlidingWindowLimiter(2, TimeSpan.FromSeconds(1));
    var now = DateTimeOffset.UtcNow;
    Assert(limiter.TryConsume(now: now) && limiter.TryConsume(now: now), "initial allowance");
    Assert(!limiter.TryConsume(now: now), "limit enforced");
    Assert(limiter.TryConsume(now: now.AddSeconds(2)), "window resets");
}

static async Task LocalWebSocketLifecycle()
{
    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    var options = Options();
    options.Enabled = true;
    options.HttpPrefix = $"http://127.0.0.1:{port}/et-battle/";
    options.HeartbeatSeconds = 30;
    options.HeartbeatTimeoutSeconds = 90;
    using var server = new RelayServer(options);
    server.Start();
    Assert(server.IsRunning, "server starts");
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("X-ET-Battle-Protocol", RelayHub.Protocol);
    await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/et-battle/"), CancellationToken.None);
    var request = Encoding.UTF8.GetBytes(Request("create_room", new { nickname = "Integration", mode = "relay" }));
    await socket.SendAsync(request, WebSocketMessageType.Text, true, CancellationToken.None);
    var response = new byte[16 * 1024];
    var result = await socket.ReceiveAsync(response, CancellationToken.None);
    var raw = Encoding.UTF8.GetString(response, 0, result.Count);
    using (var doc = JsonDocument.Parse(raw))
        Assert(doc.RootElement.GetProperty("type").GetString() == "room_created", "WebSocket create response");
    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test_complete", CancellationToken.None);
    await server.StopAsync();
    Assert(!server.IsRunning, "server stops");
}

static void ModIsolation()
{
    var mod = new ETBattleRelayMod();
    Assert(mod.OnGroupMessage(1, 2, ".r 1d100", true) is null, "group message isolation");
    Assert(mod.OnPrivateMessage(2, "secret") is null, "private message isolation");
    mod.OnLoad(); mod.OnDisable(); mod.OnUnload();
}

static string Request(string type, object payload) => JsonSerializer.Serialize(new
{ protocol = RelayHub.Protocol, type, message_id = Guid.NewGuid().ToString("N"), payload });

static void Assert(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

internal sealed class FakeConnection(string id) : IRelayConnection
{
    private readonly List<string> _messages = [];
    public string ConnectionId { get; } = id;
    public int Count => _messages.Count;
    public string? CloseReason { get; private set; }
    public bool TrySend(string message) { _messages.Add(message); return true; }
    public void RequestClose(string reason) => CloseReason = reason;
    public void Clear() => _messages.Clear();
    public JsonElement Last(string type)
    {
        foreach (var raw in _messages.AsEnumerable().Reverse())
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.GetProperty("type").GetString() == type)
                return doc.RootElement.GetProperty("payload").Clone();
        }
        throw new InvalidOperationException($"message type {type} not found");
    }
}
