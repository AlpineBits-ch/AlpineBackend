# venta.gg

![Build](https://github.com/AlpineBits-ch/AlpineBackend/actions/workflows/docker-build.yml/badge.svg)
![Coverage](https://github.com/AlpineBits-ch/AlpineBackend/blob/main/.github/badges/coverage.svg)

> A fast, self-hostable chat and community platform.

> **Early Release** - This project is in early development. Expect breaking changes, incomplete features, and missing documentation.

---

## Features

- Real-time messaging
- Community and channel management
- Self-hostable - full control over your data
- Designed for performance and scalability

---

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | .NET / C# |
| Database | PostgreSQL |
| Scalable Storage | ScyllaDB |
| Cache / Broker | Redis |
| Orchestration | Kubernetes (K8s) |

> The frontend (Angular + Tauri) lives in a separate repository.

---

## Self-Hosting

One installer brings up the whole stack - every service, its infrastructure, and a
TLS-terminating reverse proxy - and keeps it running across reboots:

```bash
sudo ./deploy/install.sh                # Linux
```

```powershell
.\deploy\Install-VentaStack.ps1         # Windows (elevated PowerShell)
```

It generates all secrets (including the Ed25519 federation identity and the token-signing
certificate), writes `deploy/.env`, configures Caddy with automatic Let's Encrypt
certificates, registers a boot hook, and starts everything. Run it again at any time to
upgrade - existing secrets are preserved. Options exist for an external PostgreSQL, an
external S3 bucket, running behind your own reverse proxy, or a plain-HTTP LAN install.

Afterwards, `ventactl status | logs | update | backup` manages the instance.

Once `https://<your-domain>/health` and `https://<your-domain>/.well-known/federation`
both return 200, you're fully set up.

See [`deploy/README.md`](deploy/README.md) for the full guide: TLS modes, federating with
another instance, configuration reference, backups and troubleshooting.

---

## Environment Variables

All variables have sensible defaults for local development. For production deployments, override the ones marked **Required**.

### Infrastructure

| Variable | Default | Description |
|---|---|---|
| `DATABASE_HOSTNAME` | `localhost` | PostgreSQL host |
| `DATABASE_PORT` | `5433` | PostgreSQL port |
| `DATABASE_NAME` | `postgres` | PostgreSQL database name |
| `DATABASE_USERNAME` | `postgres` | PostgreSQL username |
| `DATABASE_PASSWORD` | `postgres` | PostgreSQL password |
| `REDIS_HOST` | `localhost` | Redis host |
| `REDIS_PORT` | `6379` | Redis port |
| `REDIS_PASSWORD` | `devpassword` | Redis password |
| `RABBITMQ_HOST` | `localhost` | RabbitMQ host |
| `RABBITMQ_PORT` | `5672` | RabbitMQ AMQP port |
| `RABBITMQ_USERNAME` | `admin` | RabbitMQ username |
| `RABBITMQ_PASSWORD` | `admin` | RabbitMQ password |

### Auth / Identity

| Variable | Default | Description |
|---|---|---|
| `INSTANCE_URL` | `https://api.venta.gg` | Public base URL of this instance |
| `AUTH_REQUIRE_USER_EMAIL_VERIFICATION` | `true` | Require email verification before login |
| `IDENTITY_KEY_PASSWORD` | `devpassword` | Password protecting the identity signing key |
| `IDENTITY_SIGNING_CERT` | _(empty)_ | PKCS12 certificate in Base64 for production JWT signing |

### Messaging

| Variable | Default | Description |
|---|---|---|
| `USE_SCYLLA_DB` | `true` | `true` uses ScyllaDB; `false` falls back to PostgreSQL via EF Core |
| `SCYLLA_HOST` | `localhost` | ScyllaDB host |
| `SCYLLA_PORT` | `9042` | ScyllaDB CQL port |
| `SCYLLA_USERNAME` | `scylla` | ScyllaDB username |
| `SCYLLA_PASSWORD` | `scylla` | ScyllaDB password |

### Federation

| Variable | Default | Description |
|---|---|---|
| `INSTANCE_NAME` | `Venta.gg` | Display name of this federation instance |
| `FEDERATION_PRIVATE_KEY_BASE_64` | _(auto-generated)_ | Ed25519 private key in Base64 |
| `FEDERATION_PUBLIC_KEY_BASE_64` | _(auto-generated)_ | Ed25519 public key in Base64 |

### Echo Gateway

| Variable | Default | Description |
|---|---|---|
| `Services__Identity` | k8s DNS | Internal URL of the Identity service |
| `Services__Guild` | k8s DNS | Internal URL of the Guild service |
| `Services__Messaging` | k8s DNS | Internal URL of the Messaging service |
| `Services__Social` | k8s DNS | Internal URL of the Social service |
| `Services__Federation` | k8s DNS | Internal URL of the Federation service |

### Optional / Cloud

| Variable | Default | Description |
|---|---|---|
| `LIVEKIT_API_KEY` | _(empty)_ | LiveKit API key. Unset disables voice entirely |
| `LIVEKIT_API_SECRET` | _(empty)_ | HS256 signing key. A root credential - it mints admin tokens and cannot be revoked |
| `LIVEKIT__NODES__0__REGION` | _(empty)_ | Region tag of the first SFU node, e.g. `fsn1` |
| `LIVEKIT__NODES__0__SIGNALINGURL` | _(empty)_ | Public `wss://` URL, handed to clients |
| `LIVEKIT__NODES__0__APIURL` | _(empty)_ | Control-plane URL, backend only. Must not be reachable from the internet |
| `GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64` | _(empty)_ | Google Cloud service account JSON in Base64 (file storage) |
| `FIREBASE_SEVRICE_ACCOUNT_JSON_BASE_64` | _(empty)_ | Firebase service account JSON in Base64 (push notifications) |
| `SENTRY_URL` | _(empty)_ | Sentry DSN for error reporting |
| `MICROSOFT_GRAPH_CLIENT_ID` | _(empty)_ | Azure AD app client ID |
| `MICROSOFT_GRAPH_CLIENT_SECRET` | _(empty)_ | Azure AD app client secret |

---

## Roadmap

See the full public roadmap at **[venta.gg/#/roadmap](https://venta.gg/#/roadmap)**.

---

## Contributing

Contributions are welcome! Since this is an early-stage project, please open an issue first to discuss what you would like to change or add before submitting a pull request.

Before your pull request can be merged, you will be asked to sign our **[Contributor License Agreement (CLA)](CLA.md)**. This is handled automatically by a bot on your first PR - just follow the instructions it posts in the comment thread.

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Commit your changes (`git commit -m 'Add my feature'`)
4. Push to the branch (`git push origin feature/my-feature`)
5. Open a pull request - the CLA bot will guide you through signing

---

## Tests

Unit tests are written using **NUnit** and can be run locally with the standard .NET test runner:

```bash
dotnet test
```

---

## License

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**. See [LICENSE](LICENSE) for details.