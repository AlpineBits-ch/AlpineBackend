# Global message search — feasibility report

**Question asked:** can Echo serve a *global* search — one query box, every guild and DM the calling
user can see — with Discord's filter vocabulary (`has:file`, `from:`, `mentions:`, `in:`, `before:`,
`pinned:`), efficiently?

**Short answer: yes, and it is cheaper than it looks.** The hard parts are already built and are not
the parts you would expect. The full-text index exists, is maintained on create/edit/delete, and
lives in Postgres. The per-user ACL scope — normally *the* expensive piece of a multi-tenant search —
is already computed and cached in Redis by `GuildPermissionService`, and can be turned into a flat
channel-id list without a single extra database query in the steady state.

No new infrastructure is required for v1. Meilisearch/OpenSearch is a scale decision for later, not
an enabling one.

Estimated effort: **~2 weeks to a usable global search with the full filter set, ~4 weeks to
Discord-comparable** (backfill, snippets, ranking), one developer. Details in [§7](#7-effort).

There is one structural ceiling: **MLS-encrypted messages can never be server-searched.** See
[§6](#6-the-e2ee-ceiling).

---

## 1. What exists today

| Piece | File | State |
|---|---|---|
| Index table | `Messaging.Infrastructure/Persistence/MessageSearchEntry.cs` | Postgres, generated `tsvector` + GIN |
| Index maintenance | `Messaging.Application/Commands/CreateMessageCommand.cs:85`, `Handler/Messages/MessageUpdatedHandler.cs`, `MessageDeletedHandler.cs` | create/edit/delete all wired |
| Query endpoint | `Messaging.Application/Endpoints/SearchEndpoint.cs` | single channel **or** single conversation, no filters |
| Guild ACL | `Guild.Application/Services/GuildPermissionService.cs:334` | full per-guild channel permission set, Redis, 15 min TTL |
| DM ACL | `Messaging.Application/Services/ConversationPermissionService.cs` | full conversation-id set, Redis, 10 min TTL |
| Message bodies | `ScyllaMessageRepository` (Scylla) / `EfCoreMessageRepository` (self-host Postgres) | partition key is `context_id` |

The index row today is `(message_id, channel_id, conversation_id, author_id, content, created_at,
search_vector)`, with btree indexes on `channel_id` and `conversation_id` and a GIN index on
`search_vector`.

Note what this means: **Postgres already holds a plaintext copy of every non-encrypted message.**
Global search does not change the data-exposure posture of the instance at all. It only changes how
much of it one request can reach — which is a rate-limiting problem, not an architecture problem.

---

## 2. The four real problems

Everything else is schema work. These are the ones that decide the design.

### 2.1 Scoping the query to what the user may see

Discord's search is `WHERE channel IN (everything you can read)`. Naively that means resolving
ViewChannel across every channel of every guild the user is in, per keystroke.

Echo already has the answer cached. `ComputePermissionsForUserAsync(userId, guildId)` returns the
*entire guild's* channel list with resolved permissions in one Redis read (`GuildPermissionService.cs:340`).
So the visible-channel set for a user is:

```
GuildMembers WHERE user_id = ?          → 1 Postgres query in Guild
  + 1 Redis GET per guild               → deserialize, filter to ViewChannel
  + owned guilds (owner short-circuit)
```

That is correct but not free: for a user in 50 guilds it is 50 Redis reads of a JSON blob that can
be tens of KB each for a large guild. Running that per search request is the one thing that would
make this feel slow.

**The fix is a second-order cache.** Resolve once, store the flattened result — a sorted, packed list
of visible channel ids — under `search:scope:{userId}` with a ~60 s TTL. Steady state becomes *one*
Redis read per search. Invalidate it from the same places that already call
`InvalidateUserPermissionsCacheAsync` (role change, overwrite change, join/leave/ban), so a revoked
permission stops appearing in results within a minute at worst — the same staleness envelope
`ChannelAudienceService` already accepts for realtime fan-out, and much tighter than the 15 minutes
the underlying permission cache allows.

This needs one new bus contract in `Guild.Contracts` — `ListVisibleChannelsForUserRequest` /
`Response` — handled the same way `ListManageableGuildsForUserHandler` is. It is maybe 60 lines.

**Size check.** A heavy user in 100 guilds averaging 40 visible channels is ~4 000 ids. Postgres
handles `channel_id = ANY($1::text[])` with 4 000 elements fine (it hashes the array for the bitmap
scan). It is not elegant, but it is measurably fast and it is *exactly correct*, which the
alternatives below are not.

**Rejected alternative:** filter by `guild_id = ANY(...)` (~100 elements) and post-filter the top-K
against the channel set in memory. Smaller query, but it is silently wrong — hits from private
channels the user cannot see consume result slots, so a user can get an under-filled or empty page
while matching messages exist. If you ever need it for a pathological account, over-fetch by a large
factor and page defensively; do not make it the default.

**On `guild_id`:** Messaging does not know it, and it does not need to. `in:guild` is expressible as
"the subset of the scope list belonging to that guild", and the scope resolver knows the mapping.
That avoids denormalizing `guild_id` across a service boundary entirely — no new event, no mirror
table, no drift. Only add `guild_id` to the index if guild-scoped *analytics* ever become a
requirement.

### 2.2 Ranking is the scaling landmine, not matching

The current endpoint does:

```csharp
.OrderByDescending(e => e.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("english", query)))
.Take(limit)
```

`SearchEndpoint.cs:52`. Within one channel that is bounded and fine. **Globally it is not.** `ts_rank`
cannot be served from the GIN index, so Postgres must materialize and score *every* matching row in
scope before it can take the top 25. A common word across a 4 000-channel scope is a multi-million-row
sort.

Discord's own default is recency, with relevance as a toggle — and that is the right call here for
the same reason:

- **Default: `ORDER BY created_at DESC, message_id DESC`** with a keyset cursor. Cheap, pageable, and
  it matches what people actually want ("what did we say about X last week").
- **Relevance: opt-in**, and bounded — score only a capped candidate window (e.g. the most recent
  10 000 matches in scope), not the whole corpus. Say so in the API contract rather than pretending
  it is a global ranking.

Message ids are ULIDs (`Ids/Identifier.cs`) and sort by mint time, so `(created_at, message_id)` is a
clean total order for the cursor — with the caveat the file already documents: **ids minted before
the ULID change do not sort**, so the cursor must compare `created_at` first and use the id only as a
tiebreaker, never on its own.

### 2.3 Result hydration is currently N round-trips

```csharp
var messages = await Task.WhenAll(matchIds.Select(repo.GetMessageAsync));
```

`SearchEndpoint.cs:57`. Each `GetMessageAsync` is `WHERE message_id = ?` — a **secondary-index**
lookup in Scylla, not a partition-key read. 25 results is 25 scattered SI lookups, and SI reads in
Scylla fan out across the cluster.

Two improvements, take both:

1. The index row already knows `context_id` (= `channel_id ?? conversation_id`) and `created_at` —
   which is the actual primary key `(context_id, created_at, message_id)`. Group the hits by
   partition and read them by primary key. Turns 25 SI lookups into a handful of partition reads.
2. Better still: **render the result list from the index row itself.** It already stores `content`,
   `author_id` and `created_at`. Add the handful of display fields a result card needs
   (attachment count, reply-to, author display overrides) and global search becomes *one Postgres
   query with no Scylla traffic at all*. Hydrate the full message only on jump-to-message, where the
   client already has the id and context and the existing `around` cursor endpoint does the work.

Option 2 is what makes this "very efficient" in the sense asked for. It costs duplication of a few
display columns and the discipline of updating them on edit — which `MessageUpdatedHandler` already
does for `content`.

### 2.4 The index has gaps that predate this feature

Three are worth fixing regardless of whether global search ships:

- **Federated messages are never indexed.** `MessagingMaterializationHandlers` writes through
  `IMessageRepository` directly and republishes the domain event — it never passes through
  `CreateMessageCommandHandler`, which is the only place `MessageSearchEntries.Add` is called. Every
  message that arrived from a remote instance is invisible to search today. (Every *other* creation
  path — webhooks, bots, guild system messages, publish/crosspost — goes through the command and is
  fine.)
- **No history before 2026-07-30.** That is when `AddMessageSearchIndex` created the table, and there
  is no backfill job anywhere in the repo. Everything older is unsearchable. Backfilling means a
  full token-range scan of the Scylla `messages` table — routine, but it is a job someone has to
  write, and it should be resumable.
- **Pin state is not mirrored.** `pinned:true` needs `is_pinned` on the index row, and the
  pin/unpin path (`MessagingEndpoints.cs:413`) does not touch the search table.

---

## 3. Filter vocabulary — what each operator costs

All of it is expressible in Postgres. `websearch_to_tsquery` already handles quoted phrases,
`-exclusion` and `or`, so the text half of Discord's syntax works today.

| Operator | Implementation | New column |
|---|---|---|
| `in:#channel` | intersect scope list with that channel | — |
| `in:guild` | intersect scope list with that guild's channels | — |
| `from:@user` | `author_id = ?` | index on `author_id` |
| `mentions:@user` | `mentions @> ARRAY[?]` | `mentions text[]` + GIN |
| `has:file` | `has_attachment` | bool |
| `has:image` / `video` / `sound` | attachment `ContentType` prefix at index time | 3 bools or one small bitmask |
| `has:embed` | `EmbedsJson` non-empty at index time | bool |
| `has:link` | Markdig already parses content in `Message.cs`; extract at index time | bool |
| `pinned:true` | `is_pinned`, mirrored from the pin path | bool |
| `before:` / `after:` / `during:` | `created_at` range | — |
| `-term`, `"phrase"`, `or` | `websearch_to_tsquery` | — |
| attachment filename match | fold filenames into the tsvector with `setweight` | — |

**`from:`/`mentions:` take user *ids*, not names.** Discord's client resolves the picker to an id
before sending, and Echo should do the same — the client already has the mention autocomplete data.
Do not build username resolution into the search endpoint.

**Filename indexing needs raw SQL.** EF's `HasGeneratedTsVectorColumn` produces a single-source
`to_tsvector` column; weighting content above filenames needs a hand-written generated column in the
migration. Small, but it is the one place the EF model stops being enough.

**Index shape.** The combination that makes the scope filter and the text match one index operation
is `btree_gin`:

```sql
CREATE EXTENSION IF NOT EXISTS btree_gin;
CREATE INDEX ix_search_scope_vector ON message_search_entries USING GIN (channel_id, search_vector);
```

Postgres 16 (`compose.yaml:46`) ships btree_gin as contrib; it is available in the official image.

---

## 4. Do we need a search engine?

Not for v1. Possibly for a large instance later.

| | Postgres FTS (extend what exists) | Meilisearch / Typesense | OpenSearch |
|---|---|---|---|
| New infra in `deploy/compose.yaml` | none | one small container | JVM, 2 GB+ floor |
| Self-hoster burden | none | modest | real |
| ACL filtering on a 4 000-id set | `= ANY` + btree_gin | native, designed for this | native |
| Typo tolerance | no | yes | plugin |
| Highlighting / snippets | `ts_headline` (works, costs a re-parse) | native | native |
| Non-English tokenization | **poor** — see below | good | good |
| Ceiling | tens of millions of rows comfortably | hundreds of millions | ~unbounded |

**The genuine Postgres weakness is language.** `HasGeneratedTsVectorColumn(..., "english", ...)` in
`MicroserviceContext.cs:83` hardcodes English stemming for every message on the instance. German
compound words, and CJK entirely (no whitespace to tokenize on), search badly to not at all. If the
user base is meaningfully non-English, this is the argument for a real engine — not throughput.

**Recommendation:** ship on Postgres, but put the query behind an `ISearchIndex` abstraction from day
one so a Meilisearch backend can be added without touching the endpoint. Self-hosters keep the
zero-dependency path; a large instance flips a config value.

**Sizing, for the record.** At ~350 bytes/row the table is ~1.7 GB per 5 M messages, GIN roughly a
third of that on top. 100 M messages ≈ 35 GB + 15 GB index — fine on one Postgres with enough RAM to
keep the GIN warm, past that you want monthly `RANGE` partitioning on `created_at`, which also makes
retention a partition drop and lets `before:`/`after:` prune.

---

## 5. Proposed shape

```
GET /api/v1/messaging/search
  ?query=...                 # websearch_to_tsquery syntax
  &guildId=...               # optional, repeatable — in:
  &channelId=...             # optional, repeatable — in:
  &authorId=...              # from:
  &mentions=...              # mentions:
  &has=file,link,embed,image # has:
  &pinned=true
  &after=...&before=...      # ISO timestamps
  &sort=recency|relevance    # default recency
  &cursor=...&limit=...      # keyset, cap 50
```

Response: result cards served from the index row + `totalApproximate` + `nextCursor` +
`excludedEncryptedContexts` (see below). Omitting every scope parameter means "everything I can see" —
the global case.

Request path in steady state: **one Redis read (scope) + one Postgres query.** No bus hop, no Scylla
read. That is the efficiency answer.

---

## 6. The E2EE ceiling

Messages in MLS-encrypted channels and conversations are never indexed — `CreateMessageCommand.cs:85`
gates on `EncryptionState.Plain`, and correctly so: the server holds ciphertext and nothing else. No
amount of server-side work changes this. The existing frontend guide already documents it.

For "find everything" to be literally true, encrypted contexts need a **client-side index** — the
client decrypts anyway, so it can maintain a local FTS index (SQLite FTS5 on mobile/desktop, or an
IndexedDB-backed one on web) and merge those hits into the server's results. That is a client
project, not this repo, and it is a substantial one on its own.

What this repo should do to make that possible: have the endpoint return
`excludedEncryptedContexts` — the ids of in-scope encrypted channels/conversations the query could
not look inside — so the client knows exactly which contexts to search locally and can present one
merged, honest result list rather than silently missing half the answer.

---

## 7. Effort

One developer, backend only.

| Phase | Work | Estimate |
|---|---|---|
| **0 — Prerequisites** | `ListVisibleChannelsForUserRequest` + handler; `search:scope:{userId}` Redis cache + invalidation hooks; hydration by primary key | 3–4 days |
| **1 — Global search** | New index columns + btree_gin migration; index-time extraction (link/embed/attachment kinds/mentions); pin mirroring; new endpoint with the full filter set, keyset paging, recency/relevance sorts; rate limiting | 1.5–2 weeks |
| **2 — Completeness** | Index federated messages (the `MessagingMaterializationHandlers` gap); resumable Scylla backfill for pre-2026-07-30 history; filename indexing; `ts_headline` snippets | ~1 week |
| **3 — Scale (defer)** | `ISearchIndex` abstraction + Meilisearch backend + dual-write + reindex | 2–3 weeks |
| **4 — Encrypted (client)** | Local FTS index, merge with server results | not this repo |

Phases 0–1 give a working global search with every filter listed in §3. Phase 2 is what makes it
*trustworthy* — without the backfill and the federation fix, "search everything" quietly means
"search everything since 30 July that did not arrive over federation".

## 8. Risks

- **Rate limiting is mandatory, not optional.** A global query is orders of magnitude more expensive
  than a channel-scoped one, and the scope array makes it cheap to *ask* for. `Echo/RateLimiter`
  already exists; this endpoint needs its own bucket.
- **Scope-cache staleness is a permission leak with a clock on it.** 60 s TTL, invalidated on the
  existing permission-invalidation events. Do not raise the TTL to make benchmarks look better.
- **`ts_rank` on an unbounded candidate set** will look fine in dev and fall over on the first busy
  instance. Bound it in the first commit, not after.
- **The scope array grows with the user.** Someone in 500 guilds is a different query than someone in
  5. Measure the tail, and have the over-fetch fallback ready before you need it.
