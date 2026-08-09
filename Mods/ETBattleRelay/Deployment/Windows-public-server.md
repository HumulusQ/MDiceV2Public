# Windows public relay deployment

1. Build MDiceV2 and `ETBattleRelay` for `Release/net10.0-windows`. Install the
   Mod into the headless/console MDiceV2 instance's `data/mods` directory.
2. Copy `etbattle-relay.example.json` to `etbattle-relay.json`, keep the prefix
   on `127.0.0.1`, set `enabled` to `true`, and configure one or more public
   STUN/TURN URLs. Restrict the config ACL to the MDiceV2 service identity.
3. Run the MDiceV2 console build under a dedicated Windows service account.
   Configure service recovery and a graceful stop timeout of at least 10
   seconds so `OnDisable`/`OnUnload` can close sockets. From an elevated prompt,
   reserve only the loopback URL for that account (replace the account name):

   ```powershell
   netsh http add urlacl url=http://127.0.0.1:8787/et-battle/ user=SERVER\MDiceRelay
   ```
4. Install Caddy or IIS with URL Rewrite, bind the public DNS name, and use the
   supplied example to proxy `/et-battle/` to `127.0.0.1:8787`. TLS 1.2 or
   newer must terminate at the proxy. The ET Battle client URL is
   `wss://battle.example.com/et-battle/`.
5. Open inbound TCP 443 only. Do not expose 8787 in Windows Firewall. If a TURN
   server is deployed, open only its documented listener and relay ranges;
   ordinary STUN is not a substitute for TURN on restrictive NATs.
6. Validate once with a direct room and once with a relay room from two devices
   on different networks. Direct-mode failure is intentionally reported to the
   user; create a new relay-mode room rather than silently switching modes.

Operational logs should contain connection/rate/lifecycle information only.
Never add relay-frame bodies, room passwords, resume tokens, TURN credentials,
or decoded `et-battle-room/v1` messages to logs. Monitor memory, active socket
count, room count, 429/close rates at the proxy, and certificate renewal.

For IIS, install the WebSocket Protocol Windows feature and URL Rewrite module,
then place `iis-web.config.example` as the site's `web.config`. For Caddy, adapt
the hostname in `Caddyfile.example`; WebSocket upgrade headers are forwarded
automatically.
