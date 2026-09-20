# Routing CurseForge traffic via another host

## The failure this fixes

```
Download failed: CurseForge version list failed: CurseForge's CDN is blocking the launcher
server's address, so the request never reached the API. (CloudFront request i-CiwSeCw5PM...)
```

`api.curseforge.com` sits behind CloudFront. Its WAF answers blocked callers with an HTML
"Request blocked" page **before the API sees the request**, so:

- it is not the API key — no key, valid or not, changes it;
- desktop clients on home connections reach the same API fine, because the block is on the
  *server's* address (datacenter egress, and this server runs on Oracle Cloud);
- the only fixes are getting that address unblocked, or sending CurseForge traffic from a
  different one.

The server now supports the second option directly: give it one or more alternate routes and it
retries a blocked call over them automatically.

## How the server behaves

Each platform has an ordered list of routes. On a call, the proxy walks the list:

- the platform's own API (`direct`) first, then whatever you configure;
- a route that answers with a CDN block page — or that can't be reached at all — is parked for
  `BlockCooldownMinutes` (default 10) and the call is retried on the next route;
- a parked route is retried after the cooldown, so CurseForge unblocking the address heals on its
  own with no redeploy;
- anything the API itself produced (including a 404, or a rejected key) counts as reaching it, and
  clears the park;
- only when every route is refused does the user see an error — and it names each route tried, so
  "we're blocked" and "your relay is blocked too" are distinguishable.

With nothing configured there is exactly one route, the direct one, and behaviour is unchanged.

## Step 1 — find a host that isn't blocked

**Do this before configuring anything.** Whatever host you pick is only useful if CloudFront
accepts *it*. Run this **on the candidate host**:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -H "x-api-key: YOUR_CF_KEY" https://api.curseforge.com/v1/games
```

`200` means usable. `403` means that host is blocked too — pick another. Candidates that usually
work, roughly in order of how likely they are to pass: a home/office connection with a static IP,
a residential-grade VPS, a small cloud VM at a provider other than this one, a Cloudflare Worker.

## Step 2 — pick a route type

### Option A: outbound proxy (preferred)

The server sends through a proxy, which opens a `CONNECT` tunnel to CurseForge. **TLS stays
end-to-end, so the proxy host never sees the API key** — this is why it beats a relay.

`tinyproxy` on the unblocked host, `/etc/tinyproxy/tinyproxy.conf`:

```
Port 3128
Listen 0.0.0.0
Timeout 600
DisableViaHeader Yes

# Only the launcher server may use this proxy — otherwise you are running an open proxy.
Allow <launcher-server-ip>

# CONNECT to HTTPS must be permitted explicitly.
ConnectPort 443
```

`BasicAuth user somepassword` adds credentials on top; keep the `Allow` line regardless.
(`squid` works identically if you already run one — it just needs the same `CONNECT :443` ACL.)

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
nginx somewhere unblocked, but **the relay terminates TLS and therefore sees the API key** — only
point this at a host you own.

```nginx
server {
    listen 443 ssl;
    server_name cf-relay.example.net;
    ssl_certificate     /etc/letsencrypt/live/cf-relay.example.net/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/cf-relay.example.net/privkey.pem;

    # Without this you are running an open, authenticated CurseForge proxy for the internet.
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

A Cloudflare Worker does the same job with no server to run — test it with step 1 first, since
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

## Step 3 — configure and restart

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

Malformed routing config throws at startup rather than surfacing later as a confusing failure, so
check the service came up.

### Other settings

| Key | Default | What it does |
| --- | --- | --- |
| `Upstream:UseDirect` | `true` | Whether to try the platform's own API first. Set `false` once you know this server is permanently blocked, to skip a wasted attempt every cooldown window. A platform with no alternate configured keeps its direct route either way, so turning this off for CurseForge cannot take Modrinth offline with it. |
| `Upstream:BlockCooldownMinutes` | `10` | How long a refused route is parked before being tried again. |
| `Upstream:Modrinth` | *(empty)* | Same route list for Modrinth, which is not currently blocked. |

List more than one route to chain fallbacks; they are tried in order.

## Step 4 — verify

Browse mods in the launcher. On the server:

```bash
grep -i "upstream route" /home/opc/everything/cloud/cloudlauncher/logs/server.log | tail
```

- `is not usable (CDN block page, HTTP 403). Skipping it until ...` — that route is blocked. Seeing
  this for `direct` while mod browsing works is the intended steady state: the block is being
  routed around.
- Errors reaching users mean *every* route was refused; the message names them. Re-run step 1 on
  the relay host.
- `is reachable again` — a parked route recovered.

## Keep the relay closed

Both options let whoever can reach them make CurseForge calls with your API key attached. Keep the
IP allowlist (Option A's `Allow`, Option B's `allow`/`deny`) or the shared token in place; an open
one is an API key leak waiting to happen.

## Why not just call CurseForge from the desktop clients?

Clients aren't blocked, so it would work — but the API key would have to ship to every client, and
the whole point of `/proxy/{platform}` is that it never leaves the server. Routing the server's
traffic keeps that property. It is available as a last resort if no unblocked host can be found;
treat the key as public if you take it.
