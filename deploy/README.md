# Self-hosting Venta / Echo

One command brings up the whole federated stack — nine services, their infrastructure, and
a TLS-terminating reverse proxy — and keeps it running across reboots.

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
import), `isle` (game-server integration, optional), and `echo` — the gateway, which is not
just a reverse proxy: it also hosts the realtime SignalR hub and the cross-service sagas
that complete user registration.

**Infrastructure.** PostgreSQL (one database per service, plus the Wolverine
inbox/outbox), Redis (cache, SignalR backplane, DataProtection ring), RabbitMQ (the
Wolverine bus every service shares), ScyllaDB (message store, optional), MinIO (S3
storage, optional).

Each service applies its own EF Core migrations at startup, so there is no separate
migration step — the installer never needs `dotnet ef`.

---

## Prerequisites

* A 64-bit host with Docker (the installers offer to install it) and **4 GB RAM minimum**,
  8 GB if the ScyllaDB message store is enabled.
* For a federating, publicly reachable instance: a DNS **A/AAAA record for your API
  hostname and for the storage hostname**, both pointing at the host, with inbound TCP
  80 and 443 open.
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

Safe and idempotent. Existing secrets in `deploy/.env` are kept — in particular the
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

Federation requires a publicly reachable HTTPS endpoint — remote instances fetch
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
after peering — or regenerating the federation keypair — invalidates every existing
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

After editing, apply with `ventactl up`.

### Storage URLs

Attachment URLs are path-style — `{STORAGE_PUBLIC_URL}/{bucket}/{key}` — so whatever
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
RabbitMQ or (for `messaging`) ScyllaDB not being reachable yet — Scylla can take two
minutes to open its CQL port on first boot, and `messaging` retries until it does.

**Certificates are not issued.** Both hostnames must resolve to this host and port 80 must
be reachable from the internet: Caddy uses the HTTP-01 challenge. `ventactl logs caddy`
shows the ACME exchange.

**Logins stop working after a restart.** `IDENTITY_SIGNING_CERT` is empty, so Identity fell
back to a development certificate that is regenerated on every start. Re-run the installer.

**`401` on every request in a fresh install.** The services could not reach
`{INSTANCE_URL}/.well-known/openid-configuration` — see the section above on why that URL
must resolve from inside the containers.

**Health checks never turn green.** The probe is `deploy/healthcheck.sh`, mounted into each
service container. The .NET runtime images have shipped without `curl` and `wget` since
.NET 8, so it falls back to a raw request over bash's `/dev/tcp`.
