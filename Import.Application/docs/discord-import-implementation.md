# Discord Community Import

Lets a guild owner recreate an existing Discord server's structure (categories, channels, roles,
permission overwrites) as a new Echo guild, then keeps it **live-synced from Discord** going
forward. Shipped 2026-07-28.

## Scope (v1)

- **Structure only**: categories, channels, roles, role-based permission overwrites.
- **No members, no message history.** The Echo user who starts the import becomes the new
  guild's owner - nobody else is added.
- **Live sync, Discord → Venta only.** After the initial import, the linked Discord server stays
  connected: channel/category/role changes made on Discord auto-apply to the Echo guild.
  Venta → Discord sync is modeled in the data (`GuildLink.SyncDirection` has `VentaToDiscord`/
  `Bidirectional` values) but not implemented.
- **Dropped without a stand-in**: Discord threads/forum posts, member-targeted (not role-targeted)
  channel permission overwrites, Stage/Media channel types (mapped lossily to Voice/Forum).

## Why this shape

Discord's ToS forbids automating a real user account to read server data ("self-botting"). The
only compliant way to read a server's structure is a real Discord Bot Application added to that
server via the standard OAuth2 "add bot" flow. Since members/messages are out of scope, only the
`View Channels` permission is requested - no privileged Discord intents needed at all.

## Outstanding

Two things stand between this and a real, working import:

1. **OAuth callback redirect target is wrong.** `DiscordImportEndpoint.Callback` currently redirects
   the browser to a literal web route (`{InstanceUrl}/imports/{jobId}`). It needs to redirect to
   the `venta://discord-import?jobId=...` deep link instead, matching the existing
   `venta://steam-auth` convention (`SteamConfiguration.ClientReturnUrl`). Not fixed yet.
2. **No real Discord application has been registered** (see One-time setup below) - the OAuth flow,
   REST calls, and Gateway handshake have never been exercised against real discord.com.

## One-time setup (outside this repo)

1. Register a Discord Application + Bot in Discord's [Developer Portal](https://discord.com/developers/applications).
2. Set its OAuth2 redirect URI to `{PublicBaseUrl}{PublicCallbackPath}` - defaults to
   `https://api.venta.gg/api/v1/imports/discord/callback` (must match exactly).
3. Configure these env vars on the Import service deployment (see `DiscordImportConfiguration` in
   `AppEnvironment/Env.cs`):
   - `DISCORD_IMPORT_BOT_TOKEN` - the bot's token (deployment secret, never persisted in a DB row)
   - `DISCORD_IMPORT_CLIENT_ID` - the application's client ID
   - `DISCORD_IMPORT_PUBLIC_BASE_URL` / `DISCORD_IMPORT_PUBLIC_CALLBACK_PATH` - only needed if
     they differ from the `INSTANCE_URL`-derived defaults

**This has not been done yet** - no real Discord application exists, so the OAuth flow and Gateway
handshake have never been exercised against live discord.com.

## Architecture

New `Import.*` microservice, mirroring the existing `Bots.*` 4-project convention:

| Project | Contents |
|---|---|
| `Import.Domain` | `ImportJob` (one-shot job lifecycle), `GuildLink` (the permanent link, Active/Paused/Revoked), `ImportEntityMapping` (persisted Discord id ↔ Echo id, needed once sync is ongoing) |
| `Import.Contracts` | Cross-service request/response contracts |
| `Import.Infrastructure` | EF Core/Postgres persistence for the three entities above |
| `Import.Application` | REST client, mappers, HTTP endpoints, the durable one-shot import command, the live Gateway client, and the reconciliation backstop |

Key pieces in `Import.Application`:
- **`DiscordApiClient`** (`Discord/`) - REST calls to `discord.com/api/v10`, Bot-token auth, Polly retry honoring `Retry-After`/429.
- **`DiscordPermissionMapper`/`DiscordChannelTypeMapper`** (`Mapping/`) - explicit semantic remap tables. Discord's and Echo's permission bitmasks have completely different bit layouts - this is never a numeric cast.
- **`StartDiscordStructureImportCommand` + handler** (`Commands/`) - durable Wolverine command that fetches the Discord guild/roles/channels, builds the bulk creation payload, and calls into Guild.
- **`DiscordGatewayClient`** (`Gateway/`) - a persistent **outbound** `ClientWebSocket` connection to Discord's real Gateway (`wss://gateway.discord.gg`), using the bot token, `GUILDS` intent only. This is the mirror image of `Bots.Application`'s Gateway *server* (which accepts inbound connections from real Discord bots) - here Echo is the client. Reuses `Bots.Contracts.Gateway`'s envelope/op-code/Hello payload shapes since the wire protocol is identical either direction. Best-effort OP 6 Resume; falls back to a fresh IDENTIFY on Invalid Session.
- **`DiscordStructureSyncHandler`** (`Gateway/`) - applies one parsed `CHANNEL_*`/`GUILD_ROLE_*` dispatch to whichever Echo guild is linked, via the granular Guild commands below.
- **`DiscordStructureReconciliationService`** (`Services/`) - hourly `BackgroundService` that re-fetches each active link's structure via REST and diffs it against `ImportEntityMapping`, correcting anything the Gateway connection missed (dropped frame, reconnect gap, Invalid Session).

### Guild-side changes (small, additive)

- `CreateGuildParams.SkipDefaultChannels` (bool) - skips seeding the default "Text Channels"/"Voice
  Channels" categories when set. `@everyone` role + owner membership are still always created.
  `SystemChannelId` is deliberately left null for imports (setting it to a channel created in the
  same unit of work would hit the same Guild↔Channel circular-FK issue `GuildEndpoint.CreateGuild`
  already works around with a two-phase save) - the owner can set one later via the existing
  `UpdateGuild(SystemChannelId)` endpoint.
- New `Guild.Contracts` commands: `ImportGuildStructureCommand` (bulk, one-shot - a full
  category/channel/role tree in one call) and four granular ones the live sync uses one event at a
  time: `UpsertChannelFromSyncCommand`, `DeleteChannelFromSyncCommand`,
  `UpsertRoleFromSyncCommand`, `DeleteRoleFromSyncCommand`. Both paths reuse the existing
  `Category.Create`/`Channel.Create`/`Role.Create` domain factories - no parallel creation logic.
- Two new `AuditActionType` values: `GuildImportedFromDiscord`, `GuildSyncedFromDiscord`.

## Frontend integration

All calls go through the public gateway (`https://api.venta.gg` or whatever `INSTANCE_URL` is
configured as) under `/api/v1/imports/**` - the gateway strips the `imports` segment before
forwarding, same convention as `bots-route`/`guild-route`, so the internal route attributes in
`DiscordImportEndpoint.cs` are one segment shorter than the public path.

| Purpose | Method & public URL | Auth | Notes |
|---|---|---|---|
| Start an import | `GET /api/v1/imports/discord/start` | Bearer token | Returns `{ authorizeUrl }` - do a full-page redirect, not a fetch |
| Discord's redirect target | `GET /api/v1/imports/discord/callback` | none | Discord calls this; the frontend never does |
| Poll job status | `GET /api/v1/imports/jobs/{jobId}` | Bearer token | `status`: Pending/FetchingFromDiscord/CreatingGuild/Completed/Failed |
| List link for a guild | `GET /api/v1/imports/links?guildId={echoGuildId}` | Bearer token | Empty array if never imported |
| Pause/resume sync | `PATCH /api/v1/imports/links/{linkId}` | Bearer token | Body: `{ "status": "Active" \| "Paused" }` |
| Unlink (revoke) | `DELETE /api/v1/imports/links/{linkId}` | Bearer token | Bot leaves the Discord server, best-effort |

Flow:
1. "Import from Discord" button → `GET .../discord/start` → redirect the browser to `authorizeUrl`.
2. User approves in Discord's UI → Discord redirects to the callback → Import service redirects
   the browser to the `venta://discord-import?jobId=...` deep link (see Outstanding above - this
   redirect target isn't fixed yet, still points at a web route).
3. That page polls `GET .../jobs/{jobId}` (e.g. every 1-2s) until `Completed` (navigate to
   `echoGuildId`) or `Failed` (show `errorMessage`). There's intentionally no push/SignalR event
   for this - imports finish in seconds since there's no message/member history to move.
4. A guild's settings screen can call `GET .../links?guildId=...` to show link status and offer
   Pause/Resume/Unlink controls.

## Verification status

- Full solution build clean; `Guild.Tests` 194/194, `Import.Tests` 52/52 (new project), `Bots.Tests`
  43/43 unaffected. Zero pending EF model changes on `Import.Infrastructure`/`Guild.Infrastructure`.
- **Not yet deployed or live-tested** - see Outstanding above.
- CI: `Import.Tests` runs automatically as part of the solution-wide `dotnet test` step; a Docker
  build matrix entry (`Import.Application/Dockerfile` → `import-application` image) was added
  alongside the other services in `.github/workflows/docker-build.yml`.

## Deliberate follow-ups, not done

- Venta → Discord sync direction: would subscribe to Guild's existing `ChannelCreatedForBots`/
  `ChannelUpdatedForBots`/`ChannelDeletedForBots` bus events for `Bidirectional`-linked guilds and
  translate them into Discord REST calls. Would also need new `RoleCreatedForBots`/
  `RoleUpdatedForBots`/`RoleDeletedForBots` events, which don't exist yet (today only role
  *membership* changes are covered via `MemberUpdatedForBots`, not role-definition CRUD).
- Member import / message history import were explicitly cut from scope during planning - see
  the approved plan for the full reasoning (attachment re-hosting, rate limits at scale,
  author-identity mapping all get much harder once messages are in scope).
