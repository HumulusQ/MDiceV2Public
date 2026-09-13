# ET Battle Relay Mod

`ETBattleRelay` implements the existing `MDiceV2.Interfaces.Mod.IModPlugin`
contract. It does not subscribe to, answer, or intercept group/private messages.
Its only job is to provide ET Battle Engine room discovery, WebRTC signalling,
heartbeats, identity recovery, and opaque WebSocket forwarding.

The listener is disabled by default. The Mod binds only to loopback HTTP;
Caddy or IIS must terminate TLS and expose the public `wss://` endpoint.

## Build and install

```powershell
dotnet build .\Mods\ETBattleRelay\ETBattleRelay.csproj -c Release
```

Copy the following into one MDiceV2 Mod directory under `data/mods`:

- `ETBattleRelay.dll`
- `mod.json`
- `etbattle-relay.json` (copy and rename the example configuration)

Do not copy a private `MDiceV2.Interfaces.dll`; the host supplies that API.
Enable the Mod in MDiceV2 only after the loopback listener and reverse proxy are
configured.

## Configuration

The Mod reads `etbattle-relay.json` beside its DLL. An alternate absolute file
may be selected with `ETBATTLE_RELAY_CONFIG`. Environment variables override:

- `ETBATTLE_RELAY_ENABLED=true`
- `ETBATTLE_RELAY_HTTP_PREFIX=http://127.0.0.1:8787/et-battle/`
- `ETBATTLE_RELAY_ICE_SERVERS=stun:host:3478,turn:host:3478`

The HTTP prefix is deliberately restricted to loopback. Keep the default room
limit at 8 and recovery period at 300 seconds unless deployment policy requires
a stricter value. TURN credentials may be placed in JSON with `username` and
`credential`; protect that file with a service-account ACL.

## Security and behavior

- Passwords are reduced to salted PBKDF2-SHA256 verifiers; plaintext is not
  retained or logged.
- Resume tokens are cryptographically random, stored only as SHA-256 verifiers,
  and rotated after every successful resume.
- Direct rooms forward host-star WebRTC signalling only. Relay rooms forward
  opaque Base64 battle frames and never decode the battle protocol.
- A disconnected host suspends the room. No host migration occurs. The room is
  closed after the recovery window or immediately after an explicit host leave.
- Receive rates, byte rates, join attempts, frame size, and bounded outbound
  queues are enforced per connection.

See the ET Battle Engine `docs/network_protocol.md` for the wire contract and
`Deployment/Windows-public-server.md` for public deployment.
