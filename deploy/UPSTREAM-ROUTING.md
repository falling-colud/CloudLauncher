# Routing CurseForge traffic via another host

## The failure this fixes

What a user sees:

```
Download failed: CurseForge version list failed: CurseForge is refusing requests from the
launcher server right now. Try again later.
```

What the server log says:

```
CurseForge's CDN is blocking this server, so the request never reached the API. It is not the
API key: no key, valid or not, changes it. ... (CloudFront request i-CiwSeCw5PM...)
```

`api.curseforge.com` sits behind CloudFront. Its WAF answers blocked callers with an HTML
"Request blocked" page before the API sees the request, so:

- it is not the API key: no key, valid or not, changes it;
- desktop clients on home connections reach the same API fine, because the block is on the
  server's address (datacenter egress, and this server runs on Oracle Cloud);
- the only fixes are getting that address unblocked, or sending CurseForge traffic from a
  different one.

The server supports the second option: give it one or more alternate routes and it retries a
blocked call over them.

## How the server behaves

Each platform has an ordered list of routes. On a call, the proxy walks the list:

- the platform's own API (`direct`) first, then whatever you configure;
- a route that answers with a CDN block page, or can't be reached at all, is parked for
  `BlockCooldownMinutes` (default 10) and the call is retried on the next route;
- a parked route is tried again after the cooldown, so if CurseForge unblocks the address the
  server recovers on its own, with no redeploy;
- anything the API itself produced (including a 404, or a rejected key) counts as reaching it, and
  clears the park;
- the user sees an error only when every route is refused. It is a plain sentence with no route
  names or hosts; the server log names each route tried, so you can tell "the server is blocked"
  from "the relay is blocked too" there.

With nothing configured there is one route, the direct one.

Whatever the route:

- the proxy forwards only the store calls the launcher makes
  (`CloudLauncher.Server/Net/ProxyAllowlist.cs`); anything else is a 404 and never leaves the
  server, so a relay only ever sees those calls;
- CurseForge calls carry the CurseForge key and nothing else of the server's; Modrinth calls carry
  no credentials at all;
- redirects are never followed: a route answering with one gets it passed back to the launcher
  as it is, so the key never travels to a host you did not configure;
- every call carries the User-Agent `falling-colud/CloudLauncher-Server/<version> (<contact>)`.
  A relay that filters on User-Agent has to allow it.

## Step 1: find a host that isn't blocked

Do this before configuring anything: a host is only useful if CloudFront accepts it. Run this
**on the candidate host**:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -H "x-api-key: YOUR_CF_KEY" https://api.curseforge.com/v1/games
```

`200` means usable. `403` means that host is blocked too, so pick another. Candidates that usually
work, roughly from most to least likely to pass: a home or office connection with a static IP, a
residential-grade VPS, a small cloud VM at a different provider, a Cloudflare Worker.

## Step 2: pick a route type

### Option A: outbound proxy (preferred)

The server sends through a proxy, which opens a `CONNECT` tunnel to CurseForge. TLS stays
end-to-end, so **the proxy host never sees the API key**. That is why it beats a relay.

`tinyproxy` on the unblocked host, `/etc/tinyproxy/tinyproxy.conf`:

```
Port 3128
Listen 0.0.0.0
Timeout 600
DisableViaHeader Yes

# Only the launcher server may use this proxy; anything wider is an open proxy.
Allow <launcher-server-ip>

# CONNECT to HTTPS must be permitted explicitly.
ConnectPort 443
```

`BasicAuth user somepassword` adds credentials on top; keep the `Allow` line either way.
(`squid` works too if you already run one; it needs the same `CONNECT :443` ACL.)

Then on the launcher server:

```json
"Upstream": {
  "CurseForge": [
    { "Name": "home-proxy", "HttpProxy": "http://198.51.100.7:3128" }
  ]
}
```

Credentials go inline (`http://user:pass@host:3128`) or as `ProxyUser` / `ProxyPassword`.

### Option B: HTTPS relay

The server sends to a host you control, which forwards to CurseForge. Simpler if you already have
nginx somewhere unblocked, but **the relay terminates TLS, so it sees the API key**. Only point
this at a host you own.

```nginx
server {
    listen 443 ssl;
    server_name cf-relay.example.net;
    ssl_certificate     /etc/letsencrypt/live/cf-relay.example.net/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/cf-relay.example.net/privkey.pem;

    # Without this, anyone can make CurseForge calls with your key through the relay.
    allow <launcher-server-ip>;
    deny  all;

    location /v1/ {
        proxy_pass              https://api.curseforge.com/v1/;
        proxy_ssl_server_name   on;
        proxy_set_header        Host api.curseforge.com;
        proxy_http_version      1.1;
        proxy_set_header        Connection "";
        proxy_read_timeout      120s;
    }
}
```

```json
"Upstream": {
  "CurseForge": [
    { "Name": "relay", "BaseUrl": "https://cf-relay.example.net/v1" }
  ]
}
```

A Cloudflare Worker does the same job with no server to run. Test it with step 1 first, since
Cloudflare egress is not guaranteed to pass the WAF:

```js
export default {
  async fetch(request, env) {
    if (request.headers.get('x-relay-token') !== env.RELAY_TOKEN)
      return new Response('forbidden', { status: 403 });
    const url = new URL(request.url);
    return fetch(new Request('https://api.curseforge.com' + url.pathname + url.search, request));
  }
};
```

Add the shared secret with `Headers`, so the Worker isn't open to anyone who finds its URL:

```json
{ "Name": "worker", "BaseUrl": "https://cf.example.workers.dev/v1",
  "Headers": { "X-Relay-Token": "a-long-random-string" } }
```

## Step 3: configure and restart

Secrets belong in the systemd `EnvironmentFile`
(`/home/opc/everything/cloud/cloudlauncher/cloudlauncher.env`), not in `appsettings.json`.
Nested keys use double underscores and list entries use their index:

```sh
Upstream__CurseForge__0__Name=home-proxy
Upstream__CurseForge__0__HttpProxy=http://198.51.100.7:3128
Upstream__CurseForge__0__ProxyUser=cloudlauncher
Upstream__CurseForge__0__ProxyPassword=...
```

```bash
sudo systemctl restart cloudlauncher
```

A malformed routing config makes the server fail at startup, so check that the service came up.

### Other settings

| Key | Default | What it does |
| --- | --- | --- |
| `Upstream:UseDirect` | `true` | Try the platform's own API first. Set `false` if this server is blocked for good, to skip a wasted attempt every cooldown window. A platform with no alternate route keeps its direct route either way, so turning this off for CurseForge can't take Modrinth offline. |
| `Upstream:BlockCooldownMinutes` | `10` | How long a refused route is parked before being tried again. |
| `Upstream:Modrinth` | *(empty)* | Same route list for Modrinth, which is not currently blocked. |

List more than one route to chain fallbacks; they are tried in order.

## Step 4: verify

Browse mods in the launcher. On the server:

```bash
grep -i "upstream route" /home/opc/everything/cloud/cloudlauncher/logs/server.log | tail
```

- `is not usable (CDN block page, HTTP 403). Skipping it until ...`: that route is blocked. Seeing
  this for `direct` while mod browsing works is normal: the block is being routed around.
- Errors reaching users mean every route was refused. The server log names the routes
  (`grep "CDN is blocking" .../server.log`); users are only told the store is refusing the
  server. Re-run step 1 on the relay host.
- `is reachable again`: a parked route recovered.

## Keep the relay closed

Both options let whoever can reach them make CurseForge calls with your API key attached. Keep the
IP allowlist (Option A's `Allow`, Option B's `allow`/`deny`) or the shared token in place; an open
relay leaks your API key.

## Why not call CurseForge from the desktop clients?

Clients aren't blocked, so it would work, but the API key would have to ship with every client.
`/proxy/{platform}` exists to keep the key on the server, and routing the server's traffic keeps it
there. Shipping the key is a last resort if no unblocked host can be found; treat the key as public
if you do.
