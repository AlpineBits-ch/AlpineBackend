# Self-hosting Venta / Echo

One command brings up the whole federated stack - nine services, their infrastructure, and
a TLS-terminating reverse proxy - and keeps it running across reboots.

| Host | Installer |
| --- | --- |
| Linux | `sudo ./deploy/install.sh` |
| Windows | `.\deploy\Install-VentaStack.ps1` (elevated PowerShell) |

Both write the same `deploy/.env` and drive the same `deploy/compose.yaml`, so an instance
can be moved between the two by copying that one file.

---

## What gets deployed

```
                     :443 / :80
                          │
                    ┌─────▼─────┐   automatic Let's Encrypt certificates
                    │   Caddy   │   (HTTP-01, renewed in the background)
                    └─────┬─────┘
             ┌────────────┴────────────┐
             │                         │
      ┌──────▼──────┐           ┌──────▼──────┐
      │ echo        │           │ minio       │  attachments, avatars, emoji
      │ (YARP + hub │           └─────────────┘
      │  + sagas)   │
      └──────┬──────┘
             │  http://<service>:8080
   ┌─────┬───┴───┬─────────┬────────┬───────┬────────┬──────┐
identity guild messaging social federation bots  import  isle
   │       │      │         │        │       │      │      │
   └───────┴──────┴────┬────┴────────┴───────┴──────┴──────┘
                       │
        PostgreSQL · Redis · RabbitMQ · ScyllaDB
```

**Services.** `identity` (accounts, OpenIddict tokens), `guild` (servers, channels, roles),
`messaging` (messages, attachments, calls), `social` (profiles, friends), `federation`
(cross-instance events), `bots` (Discord-compatible bot API + gateway), `import` (Discord
import), `isle` (game-server integration, optional), and `echo` - the gateway, which is not
just a reverse proxy: it also hosts the realtime SignalR hub and the cross-service sagas
that complete user registration.

**Infrastructure.** PostgreSQL (one database per service, plus the Wolverine
inbox/outbox), Redis (cache, SignalR backplane, DataProtection ring), RabbitMQ (the
Wolverine bus every service shares), ScyllaDB (message store, optional), MinIO (S3
storage, optional).

Each service applies its own EF Core migrations at startup, so there is no separate
migration step - the installer never needs `dotnet ef`.

---

## Prerequisites

* A 64-bit host with Docker (the installers offer to install it) and **4 GB RAM minimum**,
  8 GB if the ScyllaDB message store is enabled.
* For a federating, publicly reachable instance: a DNS **A/AAAA record for your API
  hostname and for the storage hostname**, both pointing at the host, with inbound TCP
  80 and 443 open.
* The gateway also serves five sites, each gated on its own hostname: `docs.`, `admin.`,
  `support.`, `status.` and `auth.` of your API hostname by default. They all point at the
  same host, so a wildcard record covers them, and each one you skip simply has no site.
  **`auth.` is the exception**: it is the OIDC issuer, so a partner site's server fetches
  `https://auth.<domain>/.well-known/openid-configuration` from the outside and sends
  people's browsers there to sign in. Without that record SSO does not work, though the
  chat product still does - the mobile and web clients never touch it.
* On Windows: Docker Desktop in **Linux container** mode (its default). The installer
  refuses to continue in Windows-container mode.

---

## Installing

### Linux

```bash
git clone --recurse-submodules <repo> /opt/venta && cd /opt/venta
sudo ./deploy/install.sh
```

Unattended:

```bash
sudo ./deploy/install.sh --non-interactive \
     --domain chat.example.com \
     --storage-domain cdn.example.com \
     --instance-name "Example Chat" \
     --acme-email admin@example.com
```

`--help` lists every flag: external PostgreSQL, external S3, disabling ScyllaDB, enabling
Isle, choosing prebuilt images versus a source build, and so on.

### Windows

```powershell
git clone --recurse-submodules <repo> C:\venta; cd C:\venta
.\deploy\Install-VentaStack.ps1
```

Unattended:

```powershell
.\deploy\Install-VentaStack.ps1 -NonInteractive `
    -Domain chat.example.com -StorageDomain cdn.example.com `
    -InstanceName "Example Chat" -AcmeEmail admin@example.com
```

### Re-running

Safe and idempotent. Existing secrets in `deploy/.env` are kept - in particular the
**Ed25519 federation keypair**, which identifies this instance to every peer it has
already shaken hands with. Pass `--reconfigure` / `-Reconfigure` to answer the questions
again (secrets still survive), and `--uninstall` / `-Uninstall` to stop the stack and
remove the boot hook while keeping all data.

---

## TLS modes

| Mode | When to use | What the installer does |
| --- | --- | --- |
| `letsencrypt` (default) | public instance, nothing else on :80/:443 | runs Caddy, issues and renews certificates automatically, opens the firewall |
| `external-proxy` | you already run nginx/Traefik/HAProxy | binds the gateway to `127.0.0.1:8080` and MinIO to `127.0.0.1:9000` and prints what to forward |
| `local` | LAN or development | publishes the gateway on `:8080` and MinIO on `:9000`, no TLS |

Federation requires a publicly reachable HTTPS endpoint - remote instances fetch
`/.well-known/federation` and post signed events to `/api/v1/federation/events`.

### Why `INSTANCE_URL` must resolve inside the containers

`INSTANCE_URL` is the OpenIddict issuer, the JWT `Authority` every service validates
against (each one fetches `{INSTANCE_URL}/.well-known/openid-configuration` and the JWKS
at startup), *and* the `host` field federation peers verify signatures against. It is
therefore a public URL that the containers themselves must be able to reach.

The installers handle this per mode: in `letsencrypt` mode Caddy carries a Docker network
alias for the public hostname, so containers resolve it to Caddy directly instead of
depending on the router hairpinning NAT; in `external-proxy` mode the hostname is mapped
back to the Docker host via `extra_hosts`; in `local` mode `INSTANCE_URL` uses the LAN IP
rather than `localhost`, which inside a container would mean the container itself.

---

## Day-to-day operation

Linux (`/usr/local/bin/ventactl`) and Windows (`deploy\ventactl.ps1`) take the same verbs:

```
ventactl status              container status
ventactl logs [service]      follow logs
ventactl restart [service]   restart everything or one service
ventactl update              pull (or rebuild) current images and restart
ventactl backup [dir]        copy .env and dump PostgreSQL
ventactl federation-doc      print this instance's public federation document
ventactl stop | up | down
```

**Auto-start.** Linux installs and enables `venta-stack.service`; Windows registers a
`VentaStack` scheduled task that runs at boot and waits for the Docker engine before
starting. Containers also carry `restart: unless-stopped`, so they come back with the
Docker daemon on their own.

**Upgrades.** `ventactl update`. With `IMAGE_SOURCE=registry` it pulls the current images;
with `build` it pulls the repo and rebuilds. Migrations apply themselves on the next
start, and `postgres-init` creates any database a newly added service needs.

---

## Federating with another instance

1. Confirm your own document is reachable: `ventactl federation-doc`, or
   `curl https://chat.example.com/.well-known/federation`. It publishes your instance
   name, protocol version, capabilities and Ed25519 public key.
2. Start the handshake (any account with administrative rights):

   ```bash
   curl -X POST https://chat.example.com/api/v1/admin/federation/initiate \
        -H "Authorization: Bearer <admin-token>" \
        -H 'Content-Type: application/json' \
        -d '{"host":"https://peer.example.com"}'
   ```

3. The peer's admin approves it from their side (`/api/v1/admin/federation/instances`,
   then `/api/v1/admin/federation/<id>/approve`), and you approve theirs. Only instances
   in `Active` state exchange events; `deny` and `defederate` are available on the same
   route.

Federated identifiers are `<localId>:<domain>`, so keep `INSTANCE_URL` stable. Changing it
after peering - or regenerating the federation keypair - invalidates every existing
relationship. The protocol itself is documented in
`Federation.Application/docs/federation-protocol.md`.

---

## Configuration reference

Everything lives in `deploy/.env`; the values map one-for-one onto `AppEnvironment/Env.cs`.
The installer leaves the optional integrations blank and the stack runs without them.

| Setting | Effect when unset |
| --- | --- |
| `MICROSOFT_GRAPH_CLIENT_ID` / `_SECRET` | no outbound e-mail; keep `AUTH_REQUIRE_USER_EMAIL_VERIFICATION=false` or sign-ups cannot verify |
| `CLOUDFLARE_APP_ID` / `_API_TOKEN` | voice and video calls (Cloudflare Calls SFU) do not connect |
| `FIREBASE_SERVICE_ACCOUNT_JSON_BASE_64` | no Android/iOS push notifications |
| `APNS_KEY_ID` / `_TEAM_ID` / `_AUTH_KEY_BASE_64` | no iOS VoIP/CallKit pushes |
| `DISCORD_IMPORT_BOT_TOKEN` / `_CLIENT_ID` | the Discord import service idles; nothing else is affected |
| `STEAM_WEB_API_KEY` | Steam login still works (the key is only for profile enrichment) |
| `SENTRY_URL` | no error reporting |
| `ISLE_*` | only read when the `isle` profile is enabled |
| `GATEWAY_PROXY_SECRET` | forwarded headers are ignored and **every anonymous caller on the internet shares one rate-limit bucket** - see below |
| `GATEWAY_TRUSTED_PROXIES` | nothing, unless you are using it instead of the secret |

After editing, apply with `ventactl up`.

### `GATEWAY_PROXY_SECRET` - how the gateway identifies a caller

The gateway rate-limits per caller. A signed-in caller is identified by the user id in their
token; everyone else is identified by their IP address. But the gateway is never the edge -
Caddy (or your own proxy) terminates TLS and forwards over the container network - so the
address the gateway sees on the socket is the *proxy*, identical for every caller alive. The
real address is in `X-Forwarded-For`, and a header can be forged, so the gateway will only
believe it when something vouches for it.

`GATEWAY_PROXY_SECRET` is that something. The installer generates a 32-byte random value,
writes it to `deploy/.env`, and configures the reverse proxy to send it as the
**`X-Echo-Proxy-Auth`** header on every request to the gateway. The gateway compares it in
constant time and, on a match, trusts the forwarded chain. It then **removes the header**, so
it never reaches any of the eight backend services.

**Why a secret rather than a list of proxy addresses.** There is also
`GATEWAY_TRUSTED_PROXIES`, a comma-separated list of addresses or CIDR ranges whose forwarded
headers are believed. Either mechanism is sufficient - the chain is trusted if the secret
matches **or** the peer is on the list - but the secret is the recommended one, because
container addresses are reassigned whenever the stack restarts and cloud load balancers rotate
theirs. An allowlist that has gone stale does not fail loudly: it simply stops matching, the
gateway quietly falls back to the peer address, and every anonymous caller collapses into one
bucket. The secret is bound to the configuration, not to the network, so nothing about a
restart or a replaced load balancer can invalidate it. Use `GATEWAY_TRUSTED_PROXIES` only if
your proxy really does sit on a fixed range.

**Leaving it unset is safe, but coarse.** The gateway starts, ignores forwarded headers
entirely, partitions on the peer address, and logs a warning at startup naming both variables.
Behind a proxy that means login, registration and password reset for everyone in the world are
charged to a single bucket, so one busy client can 429 everybody else. Existing installations
that predate this variable are in exactly that state until the installer is re-run.

**Never give it a fixed or shared value.** A default secret is published wherever it is written
down, which makes it precisely as useful as no secret. If you set it by hand, generate one
(`openssl rand -hex 32`) and put the same value on the gateway and the proxy. It is trimmed
before comparison, so a trailing newline in `.env` is harmless - but a value that is *only*
whitespace is treated as unset and warned about, never as a blank secret that matches a blank
header.

**Configuring your own proxy.** `letsencrypt` mode needs nothing: the generated
`deploy/generated/Caddyfile` already carries
`header_up X-Echo-Proxy-Auth "…"` on both gateway sites. In `external-proxy` mode the installer
prints the line to add; for reference:

```nginx
# nginx - inside the location block that proxy_passes to the gateway
proxy_set_header X-Echo-Proxy-Auth "<GATEWAY_PROXY_SECRET from deploy/.env>";
```

Use a *set*, not an append: it must overwrite any copy the client sent. (The gateway refuses a
multi-valued header anyway, but the failure mode of that is a client forcing itself onto the
shared bucket, which is worth avoiding.) The repository's top-level `nginx.conf`, used for
local/dev fronting rather than by these installers, does **not** set the header - add the line
above to its `location /` block if you rate-limit behind it.

### Rate limits

The gateway allows **50 requests per second per signed-in user**, with a 100-request burst
reserve, across all proxied routes combined - the same shape as Discord's global limit, so a
client library written against Discord already backs off correctly. Anonymous (IP-partitioned)
callers get **20 per second with a 40-request reserve**: signing in, registering and refreshing
a token do not fan out the way a logged-in client's first paint does, and an address is the
cheapest partition for an attacker to multiply. Webhook execution is partitioned per webhook id
and gets the authenticated budget.

Exceeding it returns `429` with `Retry-After`, `X-RateLimit-*` headers and a Discord-shaped
JSON body. Note that this limit is **newly enforced**: it was configured but never installed in
earlier builds, so callers that were previously unlimited will now see 429s.

There is deliberately **no tighter bucket on credential routes** (`/connect/token`, password
reset, registration) - brute-force protection for those is not implemented.

### Storage URLs

Attachment URLs are path-style - `{STORAGE_PUBLIC_URL}/{bucket}/{key}` - so whatever
serves the storage hostname must expose the bucket at the root path. The bundled Caddy
site does exactly that. Pointing `STORAGE_PUBLIC_URL` at a CDN in front of MinIO works as
long as the path shape is preserved.

---

## Backups

State lives in named Docker volumes (`venta_postgres_data`, `venta_redis_data`,
`venta_rabbitmq_data`, `venta_scylla_data`, `venta_minio_data`, `venta_caddy_data`).

```bash
ventactl backup /var/backups/venta     # .env + pg_dumpall
docker run --rm -v venta_minio_data:/data -v "$PWD:/out" alpine \
    tar czf /out/minio-$(date +%F).tar.gz -C /data .
```

Back up `deploy/.env` and `deploy/generated/` together with the data: they hold the
federation private key and the token-signing certificate. Losing the certificate logs
every user out; losing the federation key breaks every peering.

---

## Troubleshooting

**A service restarts in a loop.** `ventactl logs <service>`. Almost always PostgreSQL,
RabbitMQ or (for `messaging`) ScyllaDB not being reachable yet - Scylla can take two
minutes to open its CQL port on first boot, and `messaging` retries until it does.

**Certificates are not issued.** Both hostnames must resolve to this host and port 80 must
be reachable from the internet: Caddy uses the HTTP-01 challenge. `ventactl logs caddy`
shows the ACME exchange.

**`identity` will not start, logging that `IDENTITY_SIGNING_CERT` is not set.** It is empty,
and Identity refuses to run in Production without a persistent signing certificate. It used
to fall back to a development one that was regenerated on every start, which showed up as
"everyone was logged out again" hours later instead of as a startup error. Re-run the
installer, which generates a self-signed bundle when the variable is unset.

**`401` on every request in a fresh install.** The services could not reach
`{INSTANCE_URL}/.well-known/openid-configuration` - see the section above on why that URL
must resolve from inside the containers.

**Health checks never turn green.** The probe is `deploy/healthcheck.sh`, mounted into each
service container. The .NET runtime images have shipped without `curl` and `wget` since
.NET 8, so it falls back to a raw request over bash's `/dev/tcp`.
