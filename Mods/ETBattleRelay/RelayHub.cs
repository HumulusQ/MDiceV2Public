using System.Security.Cryptography;
using System.Text.Json;

namespace ETBattleRelay;

internal interface IRelayConnection
{
    string ConnectionId { get; }
    bool TrySend(string message);
    void RequestClose(string reason);
}

internal sealed class RelayHub
{
    internal const string Protocol = "et-battle-relay/v1";
    private readonly object _gate = new();
    private readonly Dictionary<string, RelayRoom> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelayPeer> _connections = new(StringComparer.Ordinal);
    private readonly RelayOptions _options;

    internal RelayHub(RelayOptions options) => _options = options;
    internal int RoomCount { get { lock (_gate) return _rooms.Count; } }

    internal void Handle(IRelayConnection connection, string raw)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 24 }); }
        catch (JsonException) { SendError(connection, "invalid_json", "Invalid relay message.", ""); return; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                SendError(connection, "invalid_envelope", "Unsupported relay envelope.", "");
                return;
            }
            var protocol = Text(root, "protocol");
            var type = Text(root, "type");
            var messageId = Text(root, "message_id");
            if (protocol != Protocol || string.IsNullOrWhiteSpace(type)
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                SendError(connection, "invalid_envelope", "Unsupported relay envelope.", messageId);
                return;
            }
            switch (type)
            {
                case "create_room": CreateRoom(connection, payload, messageId); break;
                case "join_room": JoinRoom(connection, payload, messageId); break;
                case "resume_room": ResumeRoom(connection, payload, messageId); break;
                case "leave_room": LeaveRoom(connection, payload, messageId); break;
                case "signal": RouteSignal(connection, payload, messageId); break;
                case "relay": RouteBattle(connection, payload, messageId); break;
                case "heartbeat_ack": break;
                default: SendError(connection, "unknown_type", "Unknown relay message type.", messageId); break;
            }
        }
    }

    internal void Disconnect(IRelayConnection connection, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (!_connections.Remove(connection.ConnectionId, out var peer)) return;
            peer.Connection = null;
            peer.DisconnectedAt = now ?? DateTimeOffset.UtcNow;
            var room = peer.Room;
            if (ReferenceEquals(room.Host, peer))
                Broadcast(room, Envelope("host_suspended", new { room_code = room.Code, recovery_seconds = _options.RecoverySeconds }), peer);
            else
                Broadcast(room, Envelope("peer_left", PlayerPayload(peer)), peer);
        }
    }

    internal void Cleanup(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            var current = now ?? DateTimeOffset.UtcNow;
            var expiry = TimeSpan.FromSeconds(_options.RecoverySeconds);
            foreach (var room in _rooms.Values.ToArray())
            {
                if (!room.Host.Connected && room.Host.DisconnectedAt is { } hostLeft && current - hostLeft >= expiry)
                {
                    CloseRoom(room, "host_recovery_timeout");
                    continue;
                }
                foreach (var peer in room.Peers.Values.Where(peer => !ReferenceEquals(peer, room.Host)
                    && !peer.Connected && peer.DisconnectedAt is { } left && current - left >= expiry).ToArray())
                    room.Peers.Remove(peer.Id);
            }
        }
    }

    internal string CreateHeartbeat(string nonce) => Envelope("heartbeat", new { nonce });

    private void CreateRoom(IRelayConnection connection, JsonElement payload, string messageId)
    {
        lock (_gate)
        {
            if (_connections.ContainsKey(connection.ConnectionId)) { SendError(connection, "already_in_room", "Connection already belongs to a room.", messageId); return; }
            var mode = Text(payload, "mode").ToLowerInvariant();
            if (mode is not ("direct" or "relay")) { SendError(connection, "invalid_mode", "Room mode must be direct or relay.", messageId); return; }
            var nickname = NormalizeNickname(Text(payload, "nickname"));
            if (nickname is null) { SendError(connection, "invalid_nickname", "Nickname must contain 1 to 32 characters.", messageId); return; }
            string code;
            do code = RelaySecrets.RoomCode(); while (_rooms.ContainsKey(code));
            var room = new RelayRoom(code, mode, Math.Clamp(Int(payload, "max_peers", _options.MaxPeers), 2, _options.MaxPeers), PasswordVerifier.Create(Text(payload, "password")));
            var token = RelaySecrets.Token();
            var host = NewPeer(connection, nickname, token, room);
            room.Host = host;
            room.Peers.Add(host.Id, host);
            _rooms.Add(code, room);
            _connections.Add(connection.ConnectionId, host);
            SendRoomState(connection, "room_created", room, host, token, messageId);
        }
    }

    private void JoinRoom(IRelayConnection connection, JsonElement payload, string messageId)
    {
        lock (_gate)
        {
            if (_connections.ContainsKey(connection.ConnectionId)) { SendError(connection, "already_in_room", "Connection already belongs to a room.", messageId); return; }
            var code = NormalizeCode(Text(payload, "room_code"));
            if (!_rooms.TryGetValue(code, out var room)) { SendError(connection, "room_not_found", "Room not found.", messageId); return; }
            if (!room.Host.Connected) { SendError(connection, "host_suspended", "Host is reconnecting; joining is unavailable.", messageId); return; }
            if (room.Peers.Count >= room.MaxPeers) { SendError(connection, "room_full", "Room is full.", messageId); return; }
            if (room.Password is not null && !room.Password.Verify(Text(payload, "password"))) { SendError(connection, "invalid_password", "Incorrect room password.", messageId); return; }
            var nickname = NormalizeNickname(Text(payload, "nickname"));
            if (nickname is null) { SendError(connection, "invalid_nickname", "Nickname must contain 1 to 32 characters.", messageId); return; }
            var token = RelaySecrets.Token();
            var peer = NewPeer(connection, nickname, token, room);
            room.Peers.Add(peer.Id, peer);
            _connections.Add(connection.ConnectionId, peer);
            SendRoomState(connection, "room_joined", room, peer, token, messageId);
            Broadcast(room, Envelope("peer_joined", PlayerPayload(peer)), peer);
        }
    }

    private void ResumeRoom(IRelayConnection connection, JsonElement payload, string messageId)
    {
        lock (_gate)
        {
            if (_connections.ContainsKey(connection.ConnectionId)) { SendError(connection, "already_in_room", "Connection already belongs to a room.", messageId); return; }
            var code = NormalizeCode(Text(payload, "room_code"));
            if (!_rooms.TryGetValue(code, out var room)) { SendError(connection, "room_not_found", "Room not found.", messageId); return; }
            var verifier = RelaySecrets.TokenVerifier(Text(payload, "resume_token"));
            var peer = room.Peers.Values.FirstOrDefault(candidate => CryptographicOperations.FixedTimeEquals(candidate.ResumeVerifier, verifier));
            if (peer is null || peer.Connected) { SendError(connection, "invalid_resume_token", "Resume token is invalid or already active.", messageId); return; }
            if (peer.DisconnectedAt is not { } disconnectedAt || DateTimeOffset.UtcNow - disconnectedAt > TimeSpan.FromSeconds(_options.RecoverySeconds))
            { SendError(connection, "resume_expired", "Resume window has expired.", messageId); return; }
            var nickname = NormalizeNickname(Text(payload, "nickname"));
            if (nickname is not null) peer.Nickname = nickname;
            var rotatedToken = RelaySecrets.Token();
            peer.ResumeVerifier = RelaySecrets.TokenVerifier(rotatedToken);
            peer.Connection = connection;
            peer.DisconnectedAt = null;
            _connections.Add(connection.ConnectionId, peer);
            SendRoomState(connection, "room_resumed", room, peer, rotatedToken, messageId);
            Broadcast(room, Envelope("peer_joined", PlayerPayload(peer)), peer);
        }
    }

    private void LeaveRoom(IRelayConnection connection, JsonElement payload, string messageId)
    {
        lock (_gate)
        {
            if (!TryPeer(connection, payload, messageId, out var peer, out var room)) return;
            _connections.Remove(connection.ConnectionId);
            peer.Connection = null;
            if (ReferenceEquals(peer, room.Host)) CloseRoom(room, "host_left");
            else { room.Peers.Remove(peer.Id); Broadcast(room, Envelope("peer_left", PlayerPayload(peer)), peer); }
        }
    }

    private void RouteSignal(IRelayConnection connection, JsonElement payload, string messageId)
    {
        lock (_gate)
        {
            if (!TryPeer(connection, payload, messageId, out var sender, out var room)) return;
            if (room.Mode != "direct") { SendError(connection, "wrong_room_mode", "WebRTC signalling is only available in direct rooms.", messageId); return; }
            var targetId = Text(payload, "target_peer_id");
            if (!room.Peers.TryGetValue(targetId, out var target) || !target.Connected || (!ReferenceEquals(sender, room.Host) && !ReferenceEquals(target, room.Host)))
            { SendError(connection, "invalid_signal_target", "Direct rooms use a host-star topology.", messageId); return; }
            if (!payload.TryGetProperty("signal", out var signal)) { SendError(connection, "invalid_signal", "Missing signal payload.", messageId); return; }
            target.Connection!.TrySend(Envelope("signal", new { room_code = room.Code, sender_peer_id = sender.Id, signal = signal.Clone() }, messageId));
        }
    }

    private void RouteBattle(IRelayConnection connection, JsonElement payload, string messageId)
    {
        lock (_gate)
        {
            if (!TryPeer(connection, payload, messageId, out var sender, out var room)) return;
            if (room.Mode != "relay") { SendError(connection, "wrong_room_mode", "Battle forwarding is unavailable in direct rooms.", messageId); return; }
            var data = Text(payload, "data");
            if (string.IsNullOrWhiteSpace(data)) { SendError(connection, "invalid_relay_data", "Missing opaque relay data.", messageId); return; }
            var targetId = Text(payload, "target_peer_id");
            var message = Envelope("relay", new { room_code = room.Code, sender_peer_id = sender.Id, data }, messageId);
            if (!string.IsNullOrEmpty(targetId))
            {
                if (!room.Peers.TryGetValue(targetId, out var target) || !target.Connected) { SendError(connection, "peer_unavailable", "Target peer is unavailable.", messageId); return; }
                target.Connection!.TrySend(message);
            }
            else if (ReferenceEquals(sender, room.Host)) Broadcast(room, message, sender);
            else if (room.Host.Connected) room.Host.Connection!.TrySend(message);
            else SendError(connection, "host_suspended", "Host is reconnecting.", messageId);
        }
    }

    private bool TryPeer(IRelayConnection connection, JsonElement payload, string messageId, out RelayPeer peer, out RelayRoom room)
    {
        if (!_connections.TryGetValue(connection.ConnectionId, out peer!))
        { room = null!; SendError(connection, "not_in_room", "Connection does not belong to a room.", messageId); return false; }
        room = peer.Room;
        if (NormalizeCode(Text(payload, "room_code")) != room.Code)
        { SendError(connection, "room_mismatch", "Room code does not match this connection.", messageId); return false; }
        return true;
    }

    private static RelayPeer NewPeer(IRelayConnection connection, string nickname, string token, RelayRoom room) => new()
    { Id = Guid.NewGuid().ToString("N"), Nickname = nickname, Connection = connection, ResumeVerifier = RelaySecrets.TokenVerifier(token), Room = room };

    private void SendRoomState(IRelayConnection connection, string type, RelayRoom room, RelayPeer peer, string token, string messageId) => connection.TrySend(Envelope(type, new
    {
        room_code = room.Code, peer_id = peer.Id, host_peer_id = room.Host.Id, mode = room.Mode, resume_token = token,
        players = room.Peers.Values.Select(PlayerPayload).ToArray(), ice_servers = _options.IceServers
    }, messageId));

    private void CloseRoom(RelayRoom room, string reason)
    {
        _rooms.Remove(room.Code);
        var message = Envelope("room_closed", new { room_code = room.Code, reason });
        foreach (var peer in room.Peers.Values)
        {
            if (peer.Connection is { } connection) { _connections.Remove(connection.ConnectionId); connection.TrySend(message); }
            peer.Connection = null;
        }
        room.Peers.Clear();
    }

    private static void Broadcast(RelayRoom room, string message, RelayPeer except)
    { foreach (var peer in room.Peers.Values) if (!ReferenceEquals(peer, except) && peer.Connection is { } connection) connection.TrySend(message); }
    private static object PlayerPayload(RelayPeer peer) => new { peer_id = peer.Id, nickname = peer.Nickname, connected = peer.Connected };
    private static string? NormalizeNickname(string value) { var nickname = value.Trim(); return nickname.Length is >= 1 and <= 32 ? nickname : null; }
    private static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int Int(JsonElement element, string name, int fallback) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static string Envelope(string type, object payload, string? messageId = null) => JsonSerializer.Serialize(new
    { protocol = Protocol, type, message_id = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId, payload }, JsonDefaults.Options);
    private static void SendError(IRelayConnection connection, string code, string message, string messageId) => connection.TrySend(Envelope("error", new { code, message }, messageId));
}

internal sealed class RelayRoom
{
    internal RelayRoom(string code, string mode, int maxPeers, PasswordVerifier? password)
    { Code = code; Mode = mode; MaxPeers = maxPeers; Password = password; }
    internal string Code { get; }
    internal string Mode { get; }
    internal int MaxPeers { get; }
    internal PasswordVerifier? Password { get; }
    internal RelayPeer Host { get; set; } = null!;
    internal Dictionary<string, RelayPeer> Peers { get; } = new(StringComparer.Ordinal);
}

internal sealed class RelayPeer
{
    internal required string Id { get; init; }
    internal required string Nickname { get; set; }
    internal required byte[] ResumeVerifier { get; set; }
    internal required RelayRoom Room { get; init; }
    internal IRelayConnection? Connection { get; set; }
    internal DateTimeOffset? DisconnectedAt { get; set; }
    internal bool Connected => Connection is not null;
}
