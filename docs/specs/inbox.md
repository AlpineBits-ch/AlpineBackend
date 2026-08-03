# Inbox (Discord-parity) — implementation plan

Status: **Phases 0-4 implemented 2026-08-03.** Client-facing reference lives in `Guild.Application/docs/inbox-frontend-guide.md`.
Migrations generated, **not applied**.

Original status: plan only. Written 2026-08-03 against `main` (`d6d2f19`).

Reference: `discord_inbox.png`. Two tabs — **Unread** and **Mentions** — in a popout with a
header carrying *mark-all-read* and a mention-count badge. Each Unread group is one channel:
guild icon, `# channel`, breadcrumb `GUILD › CATEGORY › CHANNEL`, an unread badge, per-group
actions (mute / mark-read / collapse), and the unread messages rendered inline underneath.
The onboarding card states the rule the backend has to honour: *"Unread messages from all your
unmuted channels will show up here."*

---

## 1. What already exists (and what it can't do)

| Capability | Where | Usable as-is? |
|---|---|---|
| Per-(member, channel) read cursor | `Guild.Domain/Entity/ReadState.cs` — `LastReadMessageId`, `MentionCount` | Yes, it is the unread cursor |
| Read-cursor ack | `Echo.Realtime/EchoRealtimeHub.cs:134` → `Guild.Application/Bus/Events/Realtime/GuildReadHandler.cs` | Yes |
| Mention counting at write time | `Guild.Application/Bus/Events/Messages/MessageCreatedHandler.cs:74-129` | Yes — this is the fan-out hook |
| Mute / notification level resolution | `Guild.Application/Services/NotificationResolutionService.cs` | Yes, but wrong batch axis (see §4.3) |
| Channel visibility filter | `GuildPermissionService.CanUserPerformActionAsync`, `ChannelAudienceService` | Yes |
| Cursor-anchored message reads | `IMessageRepository.GetMessagePageByCursorAsync` (`After`) | Yes — cheap single-partition read |
| DM read cursor + mute | `Messaging.Domain/Entities/ConversationMember.cs` | Partly (see §7) |
| Denormalized-index precedent | `pinned_messages` table, `ScyllaContext.RunMigrationsAsync:379` | Pattern to copy |

**The three things nothing today can answer:**

1. *"Which of my channels have anything newer than my cursor?"* — `ReadState` stores the cursor
   but nothing stores the channel's head. Answering it today means one Scylla read per channel
   per user, per inbox open.
2. *"Show me every message that mentioned me, newest first, across all guilds."* — messages are
   partitioned by `context_id` (`ScyllaContext.RunMigrationsAsync:183`). There is no global
   secondary index on `mentions`, and adding one to a Cassandra collection column would be a
   cluster-wide scan. **This requires a new denormalized index — it cannot be queried out of the
   existing model at any price.**
3. *"How many unread messages are in this channel?"* — `ReadState.MentionCount` is a mention
   count, not an unread count.

---

## 2. Findings that constrain the design

These came out of reading the code and each one changes what a naive implementation would do.

### 2.1 Ids are only comparable if both were minted after the ULID change

Id generation moved to `Ids/Identifier.cs` (ULID, Crockford base32) — new ids sort
lexicographically by mint time. Ids minted **before** that change were upper-cased base62, which
folded `a-z` onto `A-Z` and destroyed the ordering, and the two encodings produce different leading
characters for the same instant.

**Consequence:** `channel.LastMessageId > readState.LastReadMessageId` is wrong for any account with
pre-existing data, and will stay wrong — there is no backfill. Every "is there something newer" check
in this feature runs on **timestamps**. Ids stay opaque cursors handed to
`GetMessagePageByCursorAsync`, which resolves them via the `message_id` secondary index. Hence
`ReadState.LastReadAt`.

### 2.2 Two O(members) paths in `MessageCreatedHandler` — **fixed, Phase 0 (2026-08-03)**

| | Where it was | When it fired |
|---|---|---|
| Loaded every member row, then handed all ids to two `IN`-list queries | `PublishPushRecipientsAsync` | **every message** |
| One sequential `SELECT` + upsert per mentioned member | the mention loop | every `@everyone` |

The second was the dramatic one — 5,000 sequential Postgres round trips for an `@everyone` in a
5,000-member guild, inside a single Wolverine handler on the message-create path. The first was the
one always paid.

Both are now set-based. The mention badge is one chunked read plus one batched write;
`NotificationResolutionService.NotifiableCandidatesAsync` narrows the push candidate set to the
members who can actually clear `ShouldNotify`, which under a guild default of `OnlyMentions` is just
the ones the message named. `Guild.DefaultMessageNotifications` (Discord's
`default_message_notifications`) is what makes that reduction available.

`AllMessages` guilds remain O(members) on the push path. That is what the setting means and no query
can shrink it — the fix is that it costs one query instead of one per member, and that a large guild
can opt out.

Also fixed in the same pass: the author was being counted as mentioned by their own `@everyone`, and
`@here` unioned the whole presence set unfiltered — so an `@here` in a private channel bumped the
badge of every online member of the guild, including those without `ViewChannel`.

### 2.3 `Channel.LastActivityAt` / `MessageCount` exist but are thread-only

`TouchThreadActivityAsync` (same file, line 213) early-returns for anything that isn't
`ChannelType.Thread`. The fields (`Guild.Domain/Aggregates/Channel.cs:71,76`) are exactly what
the Unread tab needs — they just need to be maintained for every channel type.

### 2.3b Fan out only when the recipient set is not reconstructable

The rule that decides the whole write path:

> Materialise per-user rows **iff** the recipient predicate cannot be evaluated later from durable
> state. Otherwise write one row and evaluate at read time.

| Mention | Recipients | Reconstructable? | Write |
|---|---|---|---|
| `@user` | on the message | yes — but the message is not *findable*, Scylla is partitioned by context | fan out, bounded by the message |
| `@everyone` / `@here` | guild members, bounded below by `GuildMember.JoinedAt` | **yes**, durably | one broadcast row |
| `@role` | role holders, bounded below by `RoleMember.CreatedAt`, above by `ExpiresAt` | **yes**, durably | one broadcast row |

Direct mentions are indexed for *findability*, not because the predicate is unknowable, and the list
is bounded by one message — `MessagingEndpoints.MaxMentionsPerMessage` caps it at 100, the limit
Discord documents on `allowed_mentions`. Broadcast mentions never reach the index at all.

**Evidence.** Discord's Message object carries `mentions` (user objects), `mention_roles`
(*"array of role object ids"* — never expanded) and `mention_everyone` (a single boolean). There is
**no `mention_here` field**; `mention_everyone` covers both. If broadcast recipients were
materialised server-side, none of those fields would need to exist.

### 2.4 The bus events carry no message timestamp

Neither `Messaging.Domain/Events/Message/MessageCreated.cs` nor
`Guild.Contracts/Bus/Events/MessageCreatedForChannel.cs` has a `CreatedAt`. Guild currently
stamps `DateTimeOffset.UtcNow` in `TouchThreadActivityAsync`, which drifts from the stored
`Message.CreatedAt` by the broker latency. Both events need an additive `CreatedAt` field so
Guild's denormalized head-of-channel matches what Scylla actually stored — otherwise cursor
reads and the unread predicate disagree at the boundary.

### 2.5 Two storage backends, always

`MessagingInfrastructure` switches between `ScyllaMessageRepository` and
`EfCoreMessageRepository` (self-hosted deployments run Postgres). Any new message-side table
needs **both** implementations behind an interface, plus tests for both — `ScyllaMessageRepositoryTests`
exists specifically because a Scylla-only bug (`RowSet` is single-pass) shipped while the EF
path was green.

### 2.6 Scylla `RowSet` is single-pass

`ScyllaMessageRepository` comments at lines 57-62 and 139-141. Every new fetch in the mention
repository must `.ToList()` immediately. `FakeCassandraMapper` deliberately reproduces the
self-consuming behaviour, so tests catch it.

### 2.7 Latent bugs adjacent to this feature

- `Messaging.Application/Handler/Realtime/UpdateConversationReadHandler.cs` does **not** reset
  `ConversationMember.MentionCount` (Guild's equivalent does, `GuildReadHandler.cs:52`), and it
  calls `SaveChangesAsync()` manually despite Wolverine's auto-commit middleware.
- `ConversationMember.MentionCount` is **never incremented anywhere** — grep confirms the only
  writes are the field declaration. DM mention badges have therefore always read 0.
- Fixing both is in scope for the Mentions tab (DMs appear in Discord's mention filter), and each
  needs a regression test.

### 2.8 Facets and the architecture test

New response DTOs must be `[Facet(...)]` partials that expose **no EF entity**, or
`Domain.Tests/Facets/FacetEntityLeakTests` fails. Nested facets must be scalar-only
(`MinimalAttachmentDto` is the model to copy).

---

## 3. Service placement

**The Inbox HTTP surface lives in `Guild.Application`.**

Guild already owns read state, channels, categories, guilds, mute/notification resolution and
permission resolution — four of the five inputs. `Guild.Application.csproj` already references
`Messaging.Contracts` and `Messaging.Domain`, and `Bots.Application` already does a
Guild→Messaging bus read (`bus.InvokeAsync<GetMessageResponse>(new GetMessageRequest ...)`), so
the direction is precedented and adds no new project reference.

Public paths (gateway rewrites `/api/v1/guild/{**}` → `/api/v1/{**}`, `Echo/Proxy/ProxyConfig.cs:39`):

| Declared in Guild | Public |
|---|---|
| `/api/v1/inbox/unread` | `/api/v1/guild/inbox/unread` |
| `/api/v1/inbox/mentions` | `/api/v1/guild/inbox/mentions` |
| `/api/v1/inbox/read-all` | `/api/v1/guild/inbox/read-all` |

No gateway change is needed. **The mention *index* lives in Messaging** (it is message data, and
it must cover DMs which Guild cannot see); Guild reads it over the bus.

---

## 4. Unread tab

### 4.1 Storage changes (Guild, Postgres — one EF migration)

`Channel` (`Guild.Domain/Aggregates/Channel.cs`) — generalize from threads to all channel types:

| Field | Change |
|---|---|
| `LastActivityAt` | now maintained for every channel type; doc comment updated |
| `LastMessageId` | **new**, `string?` — opaque cursor for `after=` reads, never compared |
| `MessageCount` | now maintained for every channel type |

`ReadState` (`Guild.Domain/Entity/ReadState.cs`):

| Field | Change |
|---|---|
| `LastReadAt` | **new**, `DateTimeOffset?` — the `CreatedAt` of the acked message. The *only* thing compared |
| `MessageCountAtRead` | **new**, `int` — snapshot of `Channel.MessageCount` at ack, for the badge |

Indexes: `ReadState(MemberId)` (drives the whole tab), and `Channel(GuildId, LastActivityAt)`.

**Why `LastReadAt` and not id comparison:** §2.1. **How it is populated:** `GuildReadHandler`
receives only an id. In the overwhelmingly common case the acked id equals `Channel.LastMessageId`,
so it copies `Channel.LastActivityAt` — exact, zero extra I/O. When it doesn't (user acked while
scrolled up), fall back to one `bus.InvokeAsync<GetMessageResponse>` to resolve the real
`CreatedAt`; if that fails, fall back to `UtcNow` and log. Ack is not on the message hot path,
so one conditional round trip is affordable.

### 4.2 Unread predicate

For member `m`, channel `c`, read state `rs`:

```
hasUnread =
    c.LastActivityAt is not null
 && !c.Type.IsHouseholdModule()          // ChannelTypeExtensions — no message history
 && (rs is null ? c.LastActivityAt > m.JoinedAt
                : c.LastActivityAt > rs.LastReadAt)

unreadCount = max(0, c.MessageCount - (rs?.MessageCountAtRead ?? 0))
```

`rs is null` covers "joined a guild and never opened the channel", which a `ReadState`-only query
would silently drop. `unreadCount` is best-effort by construction (`MessageCount` is
bus-derived and drifts under message loss — the existing comment on the field says so); it is
display-only, clamped at 0, and reported alongside the exact `mentionCount`.

### 4.3 Notification resolution needs a second batch axis

`NotificationResolutionService.ResolveForChannelAsync(channelId, memberIds)` batches over
*members for one channel*. The inbox needs the inverse — *one member, many channels*. Add:

```csharp
Task<Dictionary<string, ResolvedNotification>> ResolveForMemberAsync(
    string memberId, IReadOnlyCollection<string> channelIds)
```

One query for the member's `GuildNotificationSetting` rows, one for all `NotificationOverride`
rows across those channels and their categories, then reuse the existing **pure static**
`Resolve(guildSetting, categoryOverride, channelOverride, now)`. Zero duplicated precedence
logic, and `NotificationResolutionServiceTests` keeps covering it unchanged.

A channel resolving to `IsMuted == true` or `Level == Nothing` is excluded from the Unread tab
(matching the screenshot's copy). It is **not** excluded from Mentions — Discord still shows
mentions from muted servers, and `ResolvedNotification.ShouldNotify` already treats mute as a
notification suppressor rather than a visibility one.

### 4.4 Read path

`GET /api/v1/inbox/unread?limit=10&cursor=<opaque>`

1. One query: caller's `GuildMembers` ⋈ `Channels` ⋈ `ReadStates` filtered by the §4.2
   predicate, ordered by `Channel.LastActivityAt DESC`, `limit + 1` rows. **Pure Postgres.**
2. `ResolveForMemberAsync` over that page → drop muted.
3. `GuildPermissionService.CanUserPerformActionAsync(userId, channelId, ViewChannel)` per
   surviving channel — Redis-cached, ~10 cache reads for a default page.
4. One batched bus request to Messaging for the previews:
   `GetChannelMessagePagesRequest { Items: [(channelId, afterMessageId, limit)] }`, served by a
   new handler that fans out over `IMessageRepository.GetMessagePageByCursorAsync` with
   `Direction = After`. Each item is one single-partition Scylla read; ≤10 per page, issued
   concurrently, bounded by `Task.WhenAll` over the page (not over all channels).
   `afterMessageId is null` (never-read channel) falls back to `GetMessagesByChannelIdAsync`.
5. Compose `InboxUnreadGroupDto` with the breadcrumb (guild name/icon, category name, channel
   name), badges, and `MessageDto[]` previews.

**Cost per open:** 1 Postgres query + 2 small Postgres queries + ~10 Redis hits + ≤10
single-partition Scylla reads. It does not scale with the number of guilds the user is in, only
with the page size.

**Preview cap:** at most `MaxPreviewMessages = 5` per group (Discord renders a handful, not the
whole backlog). A group whose unread count exceeds it sets `previewsTruncated = true`.

**Encrypted channels:** previews come back as the same `MessageDto` the history endpoint
returns, ciphertext and `MlsGeneration` included. The client decrypts exactly as it already does
for channel history. The server never attempts to decrypt, and the mention index never stores
content.

### 4.5 Mark-as-read

- `POST /api/v1/inbox/channels/{channelId}/read` — REST twin of the existing
  `guild.UpdateLastRead` hub method, so the check button works without a live hub and so it is
  E2E-testable. Both funnel into one handler; the hub method keeps its current signature.
- `POST /api/v1/inbox/read-all` — sets, for every `ReadState` the caller has,
  `LastReadMessageId = Channel.LastMessageId`, `LastReadAt = Channel.LastActivityAt`,
  `MessageCountAtRead = Channel.MessageCount`, `MentionCount = 0`; and inserts rows for
  currently-unread channels that have none. Executed as a set-based `ExecuteUpdateAsync` plus one
  bulk insert — **never** a per-channel loop. Bounded: a user in 200 guilds × 50 channels is
  10,000 rows, so it is chunked and the endpoint is rate-limited.
- Both publish `inbox.ReadStateChanged` over the hub to the caller's **other** devices so a
  second client's badge clears.

---

## 5. Mentions tab

### 5.1 The new index

New table, written on fan-out, read on one partition. TTL-bounded so it needs no reaper.

```sql
CREATE TABLE IF NOT EXISTS user_mentions (
  user_id         text,
  created_at      timestamp,
  message_id      text,
  context_id      text,
  guild_id        text,          -- null for DMs
  channel_id      text,
  conversation_id text,
  author_id       text,
  kind            text,          -- Direct | Role | Everyone | Here
  PRIMARY KEY (user_id, created_at, message_id)
) WITH CLUSTERING ORDER BY (created_at DESC, message_id ASC);
```

Added in `ScyllaContext.RunMigrationsAsync` beside `pinned_messages`, with a matching
`config.Define(new Map<UserMention>()...)`. **No content column** — the row is a pointer;
content is fetched from `messages` for the page being rendered, which keeps E2EE material out
of a second place and keeps edits/deletes from going stale in the index.

Rows are written with **TTL = 31 days**, one day past the longest lookback the UI offers. Scylla
expires them itself. `Mapper.InsertAsync(poco, insertNulls, ttl, options)` is the overload
needed — note `FakeCassandraMapper` currently throws on it and must implement it.

**EF fallback:** `UserMention` entity + `DbSet` in Messaging's `MicroserviceContext`, index on
`(UserId, CreatedAt DESC)`, plus a swept expiry (Postgres has no TTL) in the existing periodic
job style used by the thread auto-archive sweep.

**Interface:** `Messaging.Domain/Repositories/IMentionIndexRepository.cs` —
`AddAsync(IReadOnlyCollection<UserMention>)`, `GetPageAsync(userId, before?, limit, since)`,
`DeleteAsync(userId, createdAt, messageId)`, `DeleteAllAsync(userId)`. Two implementations,
mirroring `IMessageRepository` exactly.

### 5.2 Guild filter — second table, written from the start

Discord's Mentions tab filters by server, so a second query table is written alongside the global
one:

```sql
CREATE TABLE IF NOT EXISTS user_guild_mentions (
  user_id text, guild_id text, created_at timestamp, message_id text,
  channel_id text, author_id text, kind text,
  PRIMARY KEY ((user_id, guild_id), created_at, message_id)
) WITH CLUSTERING ORDER BY (created_at DESC, message_id ASC);
```

Same 31-day TTL, same fan-out, written only when `guild_id` is non-null — DMs write the global row
only. The filtered read becomes a single-partition scan instead of app-side filtering with a
bounded over-fetch loop that can still return short pages.

**What it costs:** two rows per guild mention instead of one — and since only *direct* mentions
reach this index (§2.3b), that is two rows per named user, capped at 100 per message. The earlier
version of this section worried about 10,000 LSM appends for an `@everyone`; under the broadcast-row
design that case never touches this table at all.

### 5.2b Broadcast mentions — one row, evaluated at read time

`@everyone`, `@here` and `@role` write a single row to Guild's Postgres, not to the mention index:

```
ChannelBroadcastMention { ChannelId, MessageId, CreatedAt, AuthorId, Kind (Everyone|Role), RoleId? }
index (ChannelId, CreatedAt DESC)
```

Postgres rather than Scylla deliberately: the Unread query is already a pure-Postgres join over
`GuildMember ⋈ Channel ⋈ ReadState`, and role/guild membership and `ViewChannel` all live there, so
broadcast evaluation folds into that same query instead of a cross-service call. Volume is tiny —
`@everyone` is permission-gated (`MessagingEndpoints.cs:72-87`) so it is a handful of rows per guild
per day. Pruned at 31 days by a sweep, in the thread-auto-archive job style.

**The predicate.** Exact, and the reason read-time evaluation works at all:

```
mentionsMe(bm, member, readState, resolved) =
     bm.CreatedAt > member.JoinedAt
  && bm.CreatedAt > (readState?.LastReadAt ?? member.JoinedAt)
  && bm.AuthorId != member.UserId
  && ( bm.Kind == Everyone
         ? !resolved.SuppressEveryone
         : holdsRole(member, bm.RoleId, at: bm.CreatedAt) && !resolved.SuppressRoleMentions )
  && canView(member.UserId, bm.ChannelId)

holdsRole(m, roleId, at) =
     rm.CreatedAt < at                                -- held the role when it was sent
  && (rm.ExpiresAt is null || rm.ExpiresAt > at)      -- time-boxed GuestAccess roles
```

`rm.CreatedAt < at` is load-bearing: without it, being given a role would retroactively fill the
inbox with last week's `@role` pings. Discord has that false positive because it evaluates against
*current* roles; we do not.

**Documented false negative:** losing a role, or a role being removed and re-added, drops old
`@role` mentions (`RoleMember.CreatedAt` becomes the re-add time). Discord behaves the same way.

**`@here` does not become a broadcast row.** See §5.2c.

### 5.2c `@here` is unresolved

Presence is a Redis sorted set scored by heartbeat (`GuildHydrateService.cs:76-117`) — volatile and
evicted, so "who was online at 14:32" is unknowable after the fact. Two decisions were taken that do
not compose:

1. **Index:** `@here` collapses into `@everyone` (Discord parity — there is no `mention_here` field,
   so Discord cannot distinguish them either once stored). Zero fan-out.
2. **Audience:** `@here` reaches members whose presence `Status` is `Online` only.

Under (1) a read-time evaluation returns the broadcast to *every* member, which contradicts (2).
**Resolved in favour of (2): `@here` fans out** to the online members who can see the channel.

It is the only reading consistent with both "100% reliable" and "online only", it is exact, and it is
bounded by presence rather than by membership - on a path that is already O(presence) for the
realtime broadcast. `@everyone` and `@role` remain one row each, so the guild-sized case never fans
out.

### 5.3 Fan-out (write path)

`Guild.Application`'s `MessageCreatedHandler` already computes `mentionedMemberIds` as the union
of direct / role / `@everyone` / `@here` (lines 74-106). After Phase 0 turns that into a
set-based upsert, it additionally publishes:

```
IndexMentionsCommand { MessageId, CreatedAt, ChannelId, GuildId, AuthorId,
                       Recipients: [(UserId, MentionKind)], ... }
```

- **Offloaded, not inline.** It goes on the bus so the message-create path returns immediately
  and the fan-out gets Wolverine's retry/error-queue semantics for free.
- **Chunked** at `MaxRecipientsPerCommand = 500`; an `@everyone` in a 5,000-member guild becomes
  10 commands, not one giant message and not 5,000 round trips.
- **Deduplicated** — a user who is directly mentioned *and* holds a mentioned role gets one row,
  with the most specific `kind` winning (`Direct > Role > Here > Everyone`).
- **Author excluded** (already the case for the realtime fan-out).
- **Suppression is honoured at write time**: `SuppressEveryone` / `SuppressRoleMentions` from
  `ResolvedNotification` drop those recipients before indexing, so the mention count the header
  badge shows matches what the user asked to be told about.
- **Bots and webhooks** produce mentions normally (`AuthorIdType` is carried on the message, not
  the index row) — a bot pinging you is a mention.

DM path: Messaging's own `MessageCreatedHandler` (`Handler/Messages/MessageCreatedHandler.cs:31`)
gets the same treatment for `messageCreated.Mentions` within a conversation, and **finally
increments `ConversationMember.MentionCount`** (§2.7).

### 5.4 Read path

`GET /api/v1/inbox/mentions?guildId=&since=7d&includeEveryone=&includeRoles=&includeDms=&before=&limit=25`

1. Guild endpoint → one bus request → Messaging returns the index page (one partition scan).
2. Filter by `kind` per the `includeEveryone` / `includeRoles` flags and by `since`
   (24h / 7d / 30d, default 7d, clamped to the TTL).
3. Resolve the messages themselves — `GetMessageAsync` per id on the page (secondary-index
   lookup, ≤25, concurrent). Rows whose message no longer exists are **skipped and reaped**
   from the index, so a deleted message disappears from Mentions rather than rendering a hole.
4. Re-check `ViewChannel` per distinct channel on the page before returning anything. **This is
   load-bearing:** an index row written while the user could see a private channel must not
   leak that message after their access is revoked. Same lesson as the
   `ChannelAudienceService` fix.
5. `DELETE /api/v1/inbox/mentions/{messageId}` dismisses one (the X in the UI); it deletes the
   index row. Idempotent — dismissing twice is `204`.

### 5.5 Header badge — derived, never incremented

`GET /api/v1/inbox/summary` → `{ unreadChannelCount, mentionCount }`, computed:

```
mentionCount(member, channel) =
    count(user_mentions rows        where created_at > readState.LastReadAt)   -- direct
  + count(ChannelBroadcastMention   where the §5.2b predicate holds)           -- @everyone / @role
```

`readState.MentionCount++` was not idempotent — a Wolverine retry doubled it, a permanent handler
failure lost it forever, and deleting a message left it high. It also *cannot* be right under §2.3b:
broadcast mentions have no per-user write to hang an increment on. Deriving is idempotent by primary
key, self-heals on replay, and drops automatically when a message is deleted.

The direct-mention partition scan is capped at 1,000 rows; past that the count is reported capped.

**Wire compatibility.** `ReadStateDto` is a `[Facet]` nested inside `MemberDto` and `SelfMemberDto`
(`Dtos/Response/MemberDto.cs:30,50`), so dropping the column would silently remove
`readState.mentionCount` from `/me`. The property stays, declared explicitly on the partial and
populated from the derived count. A test pins the serialized shape.

`ConversationMember.MentionCount` — never incremented anywhere today (§2.7) — gets the same
treatment: derived rather than fixed.

---

## 6. Realtime

One new push, to the mentioned users only, from the existing fan-out (the recipient set is
already computed, so this is free):

```
inbox.MentionAdded { MessageId, ChannelId, GuildId, ConversationId, AuthorId, Kind, CreatedAt }
```

and `inbox.ReadStateChanged { ChannelId, LastReadMessageId, MentionCount }` to the acking user's
other devices. No new hub methods — server→client only, via `IHubContext<EchoRealtimeHub>`,
matching how `social.*` pushes were done.

Recipients of `inbox.MentionAdded` are filtered through `ChannelAudienceService.FilterToViewersAsync`
for the same reason the message push is.

---

## 7. DMs

Discord's Mentions tab has an "Include DMs" filter; its Unread tab is channel-only (DM unreads
live in the DM list). Scope accordingly:

- **In scope:** DM mentions in the Mentions tab, gated by `includeDms`; fixing
  `ConversationMember.MentionCount` never being incremented; fixing
  `UpdateConversationReadHandler` never resetting it.
- **Out of scope:** DM conversations in the Unread tab. Stated explicitly so it is a decision,
  not an omission.

---

## 8. Phasing

| Phase | Content | Independently shippable |
|---|---|---|
| **0** ✅ **done 2026-08-03** | Both O(members) paths made set-based (§2.2); `Guild.DefaultMessageNotifications`; mention arrays capped at 100; author excluded from their own mentions; `@here` filtered to online channel viewers; `CreatedAt` on both bus events (§2.4) | Yes — pure perf/correctness, no new surface |
| **1** | `Channel.LastMessageId` + generalized `LastActivityAt`/`MessageCount`; `ReadState.LastReadAt` + `MessageCountAtRead`; `ChannelBroadcastMention` (§5.2b); migration; `ResolveForMemberAsync` | Yes — nothing reads it yet |
| **2** | Unread tab: `InboxEndpoint.GetUnreadAsync`, batched preview request/handler in Messaging, mark-read + read-all | Yes |
| **3** | `user_mentions` table (both backends), `IMentionIndexRepository`, `IndexMentionsCommand` fan-out for direct mentions only, derived counts replacing `ReadState.MentionCount`, DM fixes. **Resolve §5.2c first.** | Yes — index fills before the UI reads it |
| **4** | Mentions tab: merged read over both sources, filter, dismiss; `inbox.MentionAdded` push; `/inbox/summary` | Yes |

**Phase 0 deliberately kept `ReadState.MentionCount` as an incremented counter.** Deleting it there
would have left badges reading zero until Phase 3 lands the derived count. It is made set-based, not
removed.

Phase 3 landing before Phase 4 means the index has real data by the time the tab ships. There is
**no backfill** — mentions predating Phase 3 will not appear, which is correct behaviour for a
30-day rolling window and should be stated in the client docs rather than engineered around.

**Migration discipline:** two EF migrations (one Guild, one Messaging). Per the forum-parity
lesson, generate them **sequentially, never concurrently**, and confirm the model snapshot after
each.

---

## 9. Test plan

Criterion (c) requires each new behaviour to be proven working, proven to fail gracefully, and
covered at its edges. Following existing conventions: NUnit, no mocking framework, hand-rolled
fakes (`FakeMessageBus`, `FakeCassandraMapper`, `TestGuildContext` on EF InMemory).

### 9.1 Proves it works

**`Guild.Tests/Endpoints/InboxEndpointTests.cs`**
- Channel with messages after the cursor → appears, with correct breadcrumb and badges.
- Channel fully read → absent.
- Never-opened channel (no `ReadState`) with messages after `JoinedAt` → appears.
- Ordering is `LastActivityAt DESC` across guilds.
- `unreadCount` = `Channel.MessageCount − MessageCountAtRead`.
- Preview cap: 12 unread → 5 previews + `previewsTruncated == true`.
- `read-all` clears every channel and zeroes every `MentionCount`.
- Per-channel mark-read is idempotent (twice → same state, `204` both times).

**`Guild.Tests/Services/NotificationResolutionServiceTests.cs`** (extend)
- `ResolveForMemberAsync` produces identical results to `ResolveForChannelAsync` for the same
  (member, channel) pairs across guild / category / channel precedence — the two axes must not
  be allowed to drift.

**`Messaging.Tests/Repositories/ScyllaMentionIndexRepositoryTests.cs`** and
**`...EfCoreMentionIndexRepositoryTests.cs`** — the same assertions against both backends:
- Insert then page → newest-first.
- `before` cursor pages backwards without duplicating or skipping the boundary row.
- `since` filters by timestamp.
- Delete removes exactly one row.
- **Single-pass**: the Scylla implementation must not re-enumerate a `FetchAsync` result
  (`FakeCassandraMapper` fails it if it does — this is the §2.6 regression guard).

**`Guild.Tests/Bus/Events/MessageCreatedHandlerTests.cs`** (new file — none exists today)
- Direct mention → one row, `kind = Direct`.
- Role mention → one row per role member.
- `@everyone` → one row per member, author excluded.
- Direct + role for the same user → **one** row, `kind = Direct`.
- `SuppressEveryone` → that member gets no `@everyone` row.
- 1,200 recipients → 3 chunked commands, none exceeding 500.

**`Echo.E2E.Tests/Scenarios/InboxFlowTests.cs`** — real Guild + Messaging processes:
- Two users, a guild, a channel. User B posts `@userA`. User A's `/inbox/unread` shows the
  channel with the message; `/inbox/mentions` shows the mention; `/inbox/summary` reports 1.
- User A marks read → both tabs empty, summary 0.
- Mirrors `ThreadFlowTests`' structure (`E2EUsers.RegisterAndGetTokenAsync`, `E2EAssert`).

### 9.2 Proves it fails gracefully

- **Messaging unreachable / bus timeout** when fetching previews → the Unread tab returns the
  groups with **empty previews and a `previewsUnavailable` flag**, HTTP 200. The tab must not
  500 because a preview fetch failed; the unread *state* is Guild's own data.
- **Message deleted after being indexed** → mention row is skipped, reaped, and the page is
  returned without it. Asserted, not incidental.
- **Channel deleted** → `ReadState` cascades (`MicroserviceContext.cs:143`); assert no orphan
  group is emitted and no null-reference is thrown building the breadcrumb.
- **Guild left** → `GuildMember` cascade removes `ReadState`; the channel vanishes from the tab.
- **Permission revoked after indexing** → mention row exists, `ViewChannel` now false → message
  is **not** returned (§5.4 step 4). This is the security test; it gets its own named test.
- **Corrupt/absent cursor** (`before` names a message that no longer exists) → empty page, not a
  500 — same contract `GetMessagePageByCursorAsync` already documents.
- **`limit=0` / negative / oversized** → clamped, never passed through to Scylla (`LIMIT 0` is a
  hard error there; `MessagingController.NormalizePaging` documents the same trap).
- **Fan-out command fails mid-chunk** → retried by Wolverine; re-running it must be idempotent
  (same primary key → upsert, no duplicate rows). Asserted by running the handler twice.

### 9.3 Edge cases

- Household-module channels (`List`, `Chores`, `Ledger`, `Pantry`, `Decisions`) carry no message
  history → never appear in Unread. `ChannelTypeExtensions.IsHouseholdModule` already exists for
  exactly this.
- Threads and forum posts are `Channel` rows and **do** appear, with the parent in the breadcrumb.
- Archived / locked threads with unread messages: appear (they are still readable).
- Muted channel with a direct mention → absent from Unread, present in Mentions.
- Muted **category** and muted **guild** → same, via the inherited resolution.
- `MutedUntil` in the past → not muted (the `IsMuted(now)` comparison).
- Member timed out (`GuildMember.MutedUntil`) → still sees their inbox; a timeout strips
  participation, not reading.
- Onboarding incomplete (`OnboardingCompletedAt is null`) → channels gated by it are excluded,
  because the `ViewChannel` check already accounts for it.
- Encrypted channel → preview is ciphertext with `MlsGeneration`; asserted to be byte-identical
  to what the history endpoint returns for the same message.
- System messages (`GuildMemberJoin`) with `SystemMessageVariant` → surface unchanged so the
  client can render the localized variant.
- Webhook-authored message → `AuthorDisplayName`/`AuthorAvatarUrl` survive into the preview.
- Self-mention (author `@`s themselves) → no row.
- Message edited to add a mention → **documented as not indexed** (Discord behaves the same way);
  a test asserts the current behaviour so a future change is deliberate.
- Bulk delete of 100 messages → index rows for them are reaped lazily on read (step 3), asserted.
- User in zero guilds → `200` with an empty list, not a `404`.

### 9.4 Regression guards for existing behaviour

- `GuildReadHandler` still zeroes `MentionCount` and still handles the "no read state yet" path.
- `MessageCreatedHandler`'s existing realtime fan-out, `ChannelPushRequested` recipients and
  thread `LastActivityAt`/`AutoArchiveAt` behaviour are unchanged by the Phase 0 rewrite — this
  needs explicit assertions, since Phase 0 rewrites the middle of that handler.
- `FacetEntityLeakTests` passes with the new DTOs.
- `MemberDto` / `SelfMemberDto` nested `ReadStateDto` still serializes after the two new
  `ReadState` fields land (it is a `[Facet]` of the entity, so the fields flow through
  automatically — assert the shape in `MemberProjectionShapeTests`).

---

## 10. Risks

| Risk | Mitigation |
|---|---|
| Fan-out write amplification on `@everyone` in a huge guild | Offloaded to its own queue, chunked at 500, TTL-bounded rows, Scylla LSM writes. Already better than today's per-member Postgres loop. |
| `MessageCount` drift making `unreadCount` wrong | Display-only, clamped at 0, `mentionCount` (exact) shown beside it. Documented on the field, as it already is. |
| `read-all` on a very large account | Set-based + chunked + rate-limited; measured in the E2E test with a seeded 500-channel account. |
| Self-hosted EF path diverging from Scylla | Both implementations written together, same test file structure, both in CI — the §2.5 lesson. |
| Two concurrent EF migrations | Generate sequentially; verify snapshot after each (forum-parity lesson). |
| Private-channel leak through the index | `ViewChannel` re-checked at read time, with a dedicated named test. |

---

## 11. File-level change list

**New**
- `Guild.Application/Endpoints/InboxEndpoint.cs`
- `Guild.Application/Services/InboxService.cs`
- `Guild.Application/Dtos/Response/InboxDtos.cs`
- `Guild.Application/Bus/Events/Messages/IndexMentionsHandler.cs`
- `Guild.Contracts/Bus/Commands/IndexMentionsCommand.cs`
- `Messaging.Contracts/Bus/Request/GetChannelMessagePagesRequest.cs` + `.../Response/...`
- `Messaging.Contracts/Bus/Request/GetUserMentionsRequest.cs` + `.../Response/...`
- `Messaging.Domain/Entities/UserMention.cs`
- `Messaging.Domain/Repositories/IMentionIndexRepository.cs`
- `Messaging.Infrastructure/Persistence/Repositories/{Scylla,EfCore}MentionIndexRepository.cs`
- `Messaging.Application/Handler/Messages/{GetChannelMessagePagesHandler,IndexMentionsHandler,GetUserMentionsHandler}.cs`
- Tests per §9.

**Modified**
- `Guild.Domain/Aggregates/Channel.cs` — `LastMessageId`; comments on `LastActivityAt`/`MessageCount`
- `Guild.Domain/Entity/ReadState.cs` — `LastReadAt`, `MessageCountAtRead`
- `Guild.Application/Bus/Events/Messages/MessageCreatedHandler.cs` — set-based upsert, generalized activity touch, fan-out publish
- `Guild.Application/Bus/Events/Realtime/GuildReadHandler.cs` — populate the two new fields
- `Guild.Application/Services/NotificationResolutionService.cs` — `ResolveForMemberAsync`
- `Guild.Infrastructure/Persistence/MicroserviceContext.cs` + migration
- `Messaging.Domain/Events/Message/MessageCreated.cs` — `CreatedAt`
- `Guild.Contracts/Bus/Events/MessageCreatedForChannel.cs` — `CreatedAt`
- `Messaging.Application/Handler/Messages/MessageCreatedHandler.cs` — DM mention index + `MentionCount`
- `Messaging.Application/Handler/Realtime/UpdateConversationReadHandler.cs` — reset `MentionCount`, drop the manual `SaveChangesAsync`
- `Messaging.Infrastructure/Persistence/ScyllaContext.cs` — `user_mentions` table + mapping
- `Messaging.Infrastructure/Persistence/MicroserviceContext.cs` + migration
- `Messaging.Tests/Helpers/FakeCassandraMapper.cs` — implement the TTL `InsertAsync` overload
- `docs/specs/discord-parity.md` — scorecard rows (also: its "Notification settings — Missing
  entirely" row is stale, that shipped 2026-07-30)

**Unchanged:** `Echo/Proxy/ProxyConfig.cs` (no new gateway route), `EchoRealtimeHub` (no new
client→server methods).
