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

The quickest way to run a local instance is with Docker Compose:

```bash
./deploy/setup.sh
```

The setup script will setup all required local resources and start the services. You can later adjust the env variables further - if you wish to do so.


The API gateway will be available at `http://localhost:8080/api/v1/configuration`.

You can find the federation document at `http://localhost:8080/.well-known/federation` when both endpoints return a 200, you're fully set up!.

As a side note: The gateway expects a terminated TLS connection, if you host it behind a proxy: GG, you're done. 
If you don't, you need to setup a reverse proxy that handles SSL. Maybe Traefik, Caddy, or even a simple reverse proxy like Nginx.

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
| `CLOUDFLARE_APP_ID` | `mock_app_id` | Cloudflare Calls app ID for voice channels |
| `CLOUDFLARE_API_TOKEN` | `mock_token` | Cloudflare API token for voice channels |
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