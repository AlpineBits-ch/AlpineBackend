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
* The gateway also serves six sites, each gated on its own hostname: `docs.`, `admin.`,
  `support.`, `status.`, `auth.` and `wiki.` of your API hostname by default. They all point
  at the same host, so a wildcard record covers them, and each one you skip simply has no site.
  **`auth.` is the exception**: it is the OIDC issuer, so a partner site's server fetches
  `https://auth.<domain>/.well-known/openid-configuration` from the outside and sends
  people's browsers there to sign in. Without that record SSO does not work, though the
  chat product still does - the mobile and web clients never touch it.
* **`wiki.` needs a second record**, `*.wiki.<domain>`: each guild that publishes a wiki gets a
  hostname of its own beneath it. See below for what that costs in certificates.
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

### Certificates for published wikis

A guild that publishes its wiki gets a hostname of its own: `<slug>.wiki.<domain>`. That is one
name per published wiki, appearing whenever a guild owner presses publish, which is not something a
certificate can be requested for in advance.

The bundled Caddy handles it with **on-demand issuance**: the first request for a name triggers an
ordinary HTTP-01 challenge for that one name. What keeps that from being an open certificate mill is
the `ask` endpoint in the generated Caddyfile - Caddy asks the gateway at
`/api/v1/wiki/certificate-check?domain=<name>` first, and the gateway answers `200` only for a slug
some guild has actually published. Anything else is refused, and no certificate is requested.

You need the DNS record regardless: `*.wiki.<domain>`, pointing at this host. A wildcard
*certificate* would be the other way to do it, and it needs the DNS-01 challenge, a Caddy build with
your DNS provider's plugin, and an API credential - which is why the default is not that.

**The path form always works.** `wiki.<domain>/<slug>/<page>` is served on the apex certificate and
redirects permanently to the per-wiki hostname. Keep it: while a per-wiki certificate is missing, the
subdomain fails at TLS, which a browser shows as a security warning rather than as a missing page,
and the path form is the address that still serves.

In `external-proxy` mode the installer prints the same thing for your own proxy, including the ask
URL if it supports on-demand issuance.

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
| `LIVEKIT_API_KEY` / `_SECRET` / `LIVEKIT__NODES__0__*` | voice and video do not connect; the voice endpoints answer 503 `voiceNotConfigured` |
| `FIREBASE_SERVICE_ACCOUNT_JSON_BASE_64` | no Android/iOS push notifications |
| `APNS_KEY_ID` / `_TEAM_ID` / `_AUTH_KEY_BASE_64` | no iOS VoIP/CallKit pushes |
| `DISCORD_IMPORT_BOT_TOKEN` / `_CLIENT_ID` | the Discord import service idles; nothing else is affected |
| `STEAM_WEB_API_KEY` | Steam login still works (the key is only for profile enrichment) |
| `SENTRY_URL` | no error reporting |
| `ISLE_*` | only read when the `isle` profile is enabled |
| `AUTH_CLIENTS` | no site other than Venta's own apps can sign people in through this instance |
| `CORS_ALLOWED_ORIGINS` | only the built-in dev and desktop origins, plus the web client derived from `INSTANCE_URL`, may call the API from a browser - see below |
| `INSTANCE_LINK_HOSTS` | nothing, unless invite links use a hostname that is neither `INSTANCE_URL`'s nor the web client's - then they preview as a scraped page instead of an invite card, see below |
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

### Putting a site of your own on this instance

A browser app you host yourself needs **two** grants, in two different places, enforced by two
different services. Missing either one is a working-looking site that does not work, and neither
failure writes anything to a server log.

| | Where | Refusal looks like |
| --- | --- | --- |
| Sign people in | `AUTH_CLIENTS`, on the identity service | `invalid_client` on the sign-in page, or `invalid_request` about the redirect URI |
| Read API responses in the browser | `CORS_ALLOWED_ORIGINS`, shared by every service | a CORS error in the browser console; the request itself succeeded server-side |

`CORS_ALLOWED_ORIGINS` is additive and takes a comma, semicolon or space separated list. It never
removes the built-ins (`http://localhost:4200`, `http://localhost:1420`, the two packaged-desktop
origins) or the web client derived from `INSTANCE_URL`, so you cannot lock your own apps out with
it. `*` is refused outright: on a policy that carries credentials, ASP.NET reads it as "reflect
whatever origin asked", which is a working credentialed grant to every site on the internet.

The two localhost entries are why this is a **production-only** failure. A site under `ng serve`
is already allowed, so nothing goes wrong until it is deployed to its real hostname.

Worked example, the Isle companion site on the hosted instance. In `deploy/.env`:

```
CORS_ALLOWED_ORIGINS=https://isle.venta.gg
AUTH_CLIENTS='[{"clientId":"isle","displayName":"VentaIsle","redirectUris":["https://isle.venta.gg/auth/callback","http://localhost:4200/auth/callback"],"postLogoutRedirectUris":["https://isle.venta.gg/"],"scopes":["openid","profile","email"],"firstParty":true,"public":true}]'
```

Both redirect URIs are listed because they are matched exactly and the development one is a
different URI, not a special case. `public: true` because the client runs in a browser and can
keep no secret; `firstParty: true` because it is our own site, which skips the consent screen.
Then `ventactl restart identity` for `AUTH_CLIENTS`, or `ventactl up` for the origins, which every
service reads.

Field-by-field reference: [`docs/specs/sso-integration.md`](../docs/specs/sso-integration.md).

### `INSTANCE_LINK_HOSTS` - when an invite link previews as your marketing page

A link to this instance is resolved in-process against the services that own the record, never
fetched: an invite code becomes a card naming the server, and the fetcher that exists to dial
third parties is never pointed at our own API. Both halves of that decide the same way, from the
same list of hostnames.

The list derives two entries with no configuration - `INSTANCE_URL`'s host, and the web client's
(`app.<host>`, or `APP_DOMAIN`). That covers the ordinary case. It does not cover a **third**
hostname that the deployment cannot derive: an apex or vanity domain that redirects into the app,
a hostname you migrated from, or a CDN name in front of it.

The symptom of a missing one is specific and does not look like a configuration problem. The link
is classified as third-party, so it is fetched like any other URL - and what answers on that
hostname is usually a marketing site. The preview comes back as a card for that page, correctly
built, describing the product instead of the server somebody was invited to.

```
INSTANCE_LINK_HOSTS=example.com,www.example.com
```

Comma, space or semicolon separated; bare hosts or full URLs, only the host part is used. Additive
- it never removes the two derived entries. `ventactl up` afterwards, because both the messaging
and the unfurl service read it and the two must agree.

One trade-off worth knowing before you add a host: a link to *any other* page on it now gets no
card at all, rather than a scraped one. An unrecognised path on our own host is still our own host,
so refusing is the only answer that does not hand the fetcher back the job it was taken off.

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
