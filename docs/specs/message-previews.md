# Message previews (link embeds) — design & implementation plan

**Goal:** Discord-equivalent link previews. A user posts `https://…`; the server fetches the page,
builds a preview card, and it appears for *everyone* in the channel a moment later. The author (or a
moderator) can remove the preview and it disappears for everyone. Rich providers — YouTube, Vimeo,
Twitch, Spotify — produce an in-app player rather than a static card.

**Short answer: most of this already exists.** `Message.EmbedsJson` is already a Discord-shaped
embed array persisted across both backing stores, and the edit pipeline already broadcasts embed
changes to web clients, guild clients and bots. What is missing is (a) the thing that *produces*
embeds from a URL, (b) ~15 fields on the embed shape, and (c) a suppression flag.

There is one structural ceiling: **MLS-encrypted contexts can never be server-unfurled** — see [§9](#9-the-e2ee-ceiling).

---

## 1. How Discord actually does it

Worth stating precisely, because the async delivery is the part people get wrong.

| Stage | Discord's behaviour |
|---|---|
| Detection | Regex over message content at send time. Links inside `` ` `` code spans/fences and links wrapped in `<angle brackets>` are skipped. Deduped by URL. Capped (~5 per message). |
| Send | `MESSAGE_CREATE` fires **immediately with `embeds: []`**. The message is never held waiting on a fetch. |
| Unfurl | A dedicated **Unfurler** service does the outbound HTTP: 5s timeout, ≤5 redirects, `User-Agent: Discordbot/…`, no JS execution. Metadata precedence is oEmbed / OG tags → Twitter Card tags → bare HTML (`<title>`, `<meta name=description>`, `<link rel=icon>`). |
| Media | When the unfurled result names an image or video, the Unfurler calls `/_metadata` on a **Media Proxy** instance to get real dimensions and a re-hosted copy. That is where `proxy_url` → `media.discordapp.net` comes from, alongside `width`/`height` and a `placeholder` thumbhash. |
| Delivery | A **`MESSAGE_UPDATE`** carrying the populated `embeds` array. Explicitly documented: "If an embed for a website is uncached, Discord will fire `MESSAGE_CREATE` with an empty embeds array, and will fire a `MESSAGE_UPDATE` containing the new embeds array." |
| Cache | Keyed on the URL, TTL on the order of minutes. A second person posting the same link gets the embed synchronously. |
| Removal | The ✕ on the card sets the **`SUPPRESS_EMBEDS` message flag (`1 << 2`)** via `PATCH …/messages/{id}`. It is message state, not per-viewer state, so it vanishes for everyone. Author or `MANAGE_MESSAGES`. Sender-side, `<https://…>` prevents the unfurl ever happening. |
| Players | Provider embeds are `type: "video"` (or `gifv`) with a `video.url` pointing at the provider's *iframe player* (`youtube.com/embed/{id}`), a `thumbnail` from the provider, and `provider: {name, url}`. The client shows the thumbnail and swaps in the iframe on click. |

The two shapes that matter for us:

```jsonc
// link / article — the ordinary card
{ "type": "link", "url": "…", "title": "…", "description": "…", "color": 3447003,
  "provider": { "name": "Example", "url": "https://example.com" },
  "author":   { "name": "…", "url": "…", "icon_url": "…", "proxy_icon_url": "…" },
  "thumbnail":{ "url": "…", "proxy_url": "…", "width": 1200, "height": 630,
                "content_type": "image/png", "placeholder": "…", "placeholder_version": 1 } }

// video — the in-app player
{ "type": "video", "url": "https://www.youtube.com/watch?v=…", "title": "…", "description": "…",
  "provider":  { "name": "YouTube", "url": "https://www.youtube.com" },
  "author":    { "name": "Channel name", "url": "…" },
  "thumbnail": { "url": "https://i.ytimg.com/vi/…/maxresdefault.jpg", "proxy_url": "…",
                 "width": 1280, "height": 720 },
  "video":     { "url": "https://www.youtube.com/embed/…", "width": 1280, "height": 720 } }
```

Documented limits to mirror: title ≤256, description ≤4096, field name ≤256, value ≤1024, footer
≤2048, author name ≤256, ≤25 fields, **≤6000 chars summed across all embeds on a message**, and
embeds dedupe by `url`.

---

## 2. What Echo already has

| Piece | File | State |
|---|---|---|
| Embed storage | `Messaging.Domain/Entities/Message.cs:74` (`EmbedsJson`) | Opaque JSON string; identical on Scylla (`embeds_json text`) and Postgres/EF |
| Embed shape | `Bots.Contracts/Gateway/Payloads/InteractionPayloads.cs:279` | `EmbedPayload` — **partial**: title/description/url/color/author/fields/footer only |
| Create choke point | `Messaging.Application/Commands/CreateMessageCommand.cs` | The one place `MessageCreated` may be raised; already skips search indexing for encrypted/system messages |
| Edit choke point | `Messaging.Application/Commands/UpdateMessageCommand.cs` | Patch semantics already correct: `null` = leave alone, `"[]"` = clear |
| Embed broadcast | `Handler/Messages/MessageUpdatedHandler.cs` → `conversation.MessageUpdated`; `Guild.Application/Bus/Events/Messages/MessageUpdatedForChannelHandler.cs` → `guild.MessageUpdated` + `MessageUpdatedForBots` → Discord `MESSAGE_UPDATE` | **Already carries `EmbedsJson` end to end** |
| REST exposure | `Messaging.Application/Dtos/Response/MessageDto.cs` | `[Facet(typeof(Message))]` — `EmbedsJson` auto-projects, no DTO work needed |
| Async-after-save idiom | `Handler/Attachments/ProcessAttachmentHandler.cs` | `bus.SendAsync(new ProcessAttachment{…})` → handler → S3 + ImageSharp resize + Redis warm + state flip. Exact template. |
| Image processing | ImageSharp (`Lanczos3`, JPEG q90) + FFMpegCore, already referenced by `Messaging.Application` | Reusable for preview thumbnails |
| Object storage | `IAmazonS3` via `AppEnvironment/StorageInstance.cs`; serving pattern in `Controllers/AttachmentController.cs` (S3 read + 10-min Redis byte cache) | Reusable for the media proxy |
| SSRF guard | `Federation.Application/Security/FederationTargetGuard.cs` | IP validation at `ConnectCallback` (beats DNS rebinding), redirects disabled, blocks loopback/RFC1918/169.254/CGNAT/ULA/multicast. **Directly reusable — this is the single most important existing asset.** |
| Markdown parsing | Markdig already referenced by `Messaging.Domain` and imported (unused) in `Message.cs` | Gives correct code-span/code-fence exclusion for free |

**Nothing named `OpenGraph`, `unfurl`, `LinkPreview` or `Preview` exists.** The producer side is greenfield.

Net: the delivery half of the feature is done. We are building the producer half.

---

## 3. Where the unfurler lives — the one decision to make

**Recommendation: a new `Unfurl.*` service** (`Unfurl.Application` + `Unfurl.Contracts`, no DB).

Arbitrary attacker-chosen outbound HTTP is the highest-risk egress in the product. Today the only
comparable egress (federation) is operator-triggered; this one is triggered by anyone who can type a
URL into a chat box. Running it in `Messaging.Application` puts that fetch loop in the same process
and the same network policy as the message store, the MLS key material and the S3 credentials.
Discord separates it (Unfurler + Media Proxy) for exactly this reason, plus these:

- the cache is **global per URL**, not per message or per context — naturally a service, not a library;
- it needs its own egress network policy, its own per-domain rate limits and its own CPU budget for
  image decoding, none of which should contend with message writes;
- the media proxy serves **unauthenticated** URLs, whereas everything in `AttachmentController` is
  permission-checked — mixing the two invites a mistake.

Cost: one more compose service, Dockerfile, YARP route, CI target, and installer entry.

**The alternative** — `Messaging.Application/Services/Unfurl/*` with a dedicated hardened
`HttpClient` — is roughly a week cheaper and a plausible v1, but extracting it later means moving the
cache, the proxy endpoint and its public URLs, which are baked into stored `proxy_url` values in
every historical message. If we are going to split it, split it before the first byte is stored.

**Decision: separate service, confirmed and implemented.** `Unfurl.Contracts` + `Unfurl.Application`
+ `Unfurl.Tests`, no `.Domain`/`.Infrastructure` (nothing to persist). Gateway route, compose
service, CI matrix entry, both installers and an `alpine-infra/unfurl` Helm chart all landed with it.

---

## 4. Architecture

```
POST /api/v1/messaging
  └─ CreateMessageCommand  ── persists, cascades MessageCreated  (embeds: [] — never blocks)
        └─ MessageCreated ──▶ UnfurlLinksHandler            [Messaging]
              │  gates: Plain only, Type=Message, no author-supplied embeds,
              │         SUPPRESS_EMBEDS unset, ≥1 extractable link
              └─ bus.InvokeAsync(UnfurlUrlsRequest{ urls })  ──▶ [Unfurl]
                                                                 ├─ Redis cache hit? return
                                                                 ├─ SSRF-guarded GET (5s, ≤5 hops, 2 MB)
                                                                 ├─ parse: oEmbed → OG → Twitter → HTML
                                                                 ├─ provider registry → video/gifv
                                                                 ├─ media: fetch img, ImageSharp
                                                                 │   dims + resize + thumbhash → S3
                                                                 └─ cache + return EmbedPayload[]
              └─ ApplyGeneratedEmbedsCommand (CAS on content hash + flags)
                    └─ MessageUpdated ─┬─ conversation.MessageUpdated
                                       ├─ MessageUpdatedForChannel ─▶ guild.MessageUpdated
                                       └─ MessageUpdatedForBots     ─▶ Discord MESSAGE_UPDATE
```

Bus contracts (`Unfurl.Contracts`):

```csharp
public class UnfurlUrlsRequest  { public List<string> Urls { get; set; } = []; }
public class UnfurlUrlsResponse { public List<UnfurlResult> Results { get; set; } = []; }
public class UnfurlResult       { public string Url; public EmbedPayload? Embed; public string? FailureReason; }
```

Why request/response over the bus rather than fire-and-forget: the unfurler needs to answer the same
question for the REST "preview this URL as I type" path later, and `InvokeAsync` gives us the timeout
and the cache hit for free. The *outer* call is still async w.r.t. the user's send.

---

## 5. Schema and contract changes

### 5.1 `EmbedPayload` — fill out the Discord shape (additive)

`Bots.Contracts/Gateway/Payloads/InteractionPayloads.cs`. Add: `type`, `timestamp`, `image`,
`thumbnail`, `video`, `provider`, `flags`; add `url`/`icon_url`/`proxy_icon_url` to `EmbedAuthorPayload`;
add `icon_url`/`proxy_icon_url` to `EmbedFooterPayload`. New `EmbedMediaPayload { url, proxy_url,
width, height, content_type, placeholder, placeholder_version }`.

Purely additive, and `EmbedsJson` is stored opaquely, so **no migration and no data rewrite**. It does
mean bot-authored images/thumbnails/colors stop being silently dropped — a bug fix, but a behaviour
change worth a line in the changelog. `Messaging.Application/docs/message-embeds-frontend-guide.md`
currently states those fields "aren't carried yet"; update it.

One private extension: mark server-generated embeds so a re-unfurl replaces only its own output and
never a bot's card. Use an embed `flags` bit (`1 << 16`, well clear of Discord's allocations) rather
than a `_generated` key, so it survives a round trip through a Discord-compatible client.

### 5.2 `Message.Flags` — the suppression bit

New `int Flags` column, Discord-compatible bitfield, `SUPPRESS_EMBEDS = 1 << 2`.

> ⚠️ **Scylla column-order trap.** `Message.SelectColumns` (`Message.cs:119-132`) is a pinned explicit
> column list because Cassandra returns non-key columns from `SELECT *` in *alphabetical* order — when
> `embeds_json` was added it sorted before `encryption_state` and stale prepared statements started
> reading embed JSON as the encryption enum. **Append `flags` to `SelectColumns`** and add the
> `ALTER TABLE messages ADD flags int;` step in `ScyllaContext.RunMigrationsAsync`, plus a matching
> EF migration. Do not reorder the constant.

Why a flag and not just `EmbedsJson = "[]"`: the unfurl job retries, and edits re-unfurl. Without a
persistent "the human said no" bit, a suppressed preview comes back. This is also why Discord uses a
flag.

### 5.3 Don't mark suppressed/unfurled messages as "(edited)"

`UpdateMessageCommand.cs:38` bumps `UpdatedAt` unconditionally. Adding an embed would therefore make
every client render "(edited)" on a message nobody edited. Split the concepts: keep `UpdatedAt` as the
row-touch timestamp and add `EditedAt`, set only when `Content` actually changes; clients switch to
`EditedAt` for the "(edited)" marker. (Alternatively pass a `SuppressEditMarker` flag on the command —
cheaper, but it leaves `UpdatedAt` meaning two things.)

### 5.4 Event-payload gaps

`MessageUpdated` carries `EmbedsJson` but not `Flags` or `UpdatedAt`; `MessageUpdatedForChannel` and
`MessageUpdatedForBots` carry neither `Flags` nor `ComponentsJson`. Add `Flags` to all three (clients
need to know a preview was suppressed, not just that it vanished) and fix the `ComponentsJson` gap
while in there.

---

## 6. The unfurler

### 6.1 Link extraction — `Messaging.Domain/Previews/LinkExtractor.cs`

Markdig-based, not regex, because the exclusions are syntactic:

- skip links inside `CodeInline` and `FencedCodeBlock`/`CodeBlock`;
- skip `<https://…>` autolinks wrapped in angle brackets (Discord's sender-side opt-out);
- `http`/`https` only;
- dedupe case-insensitively on normalized URL (strip fragment, sort/strip known tracking params);
- cap at **5** per message; log when the cap truncates.

Pure function, no I/O — cheap and exhaustively testable.

### 6.2 Fetch — `Unfurl.Application/Fetching/`

Port `FederationTargetGuard` into a shared location (it is already static and dependency-free) and
reuse `CreateConnectCallback` verbatim. Redirects must be followed for unfurling — unlike federation —
so follow them **manually**, re-validating each hop through the same guard, ≤5 hops. Never hand
`AllowAutoRedirect = true` to a guarded handler; the guard runs per connection but a redirect to a
`file://`/`gopher://` scheme or a credential-bearing URL still needs scheme re-checking each hop.

- `User-Agent: EchoBot/1.0 (+{InstanceUrl}/bot)` — many sites (Twitter/X, Reddit) serve OG tags only
  to recognised crawlers, so this is functional, not cosmetic.
- Timeout 5s total; response cap **2 MB** (enforced on the read loop, not `Content-Length` — that
  header lies); `Accept: text/html,application/xhtml+xml`.
- Reject non-HTML content types except direct `image/*` and `video/*` (which become `image`/`video`
  embeds directly, as Discord does for a bare image link).
- Per-domain token bucket + global concurrency cap. Without this Echo is a DDoS amplifier: one message
  with a link fanned out across a large instance must still be exactly one origin fetch.
- Never send cookies, auth headers, or the requesting user's IP. This is a privacy *gain* over
  client-side unfurling and worth stating in user-facing docs.

### 6.3 Parse — `Unfurl.Application/Parsing/`

Use **AngleSharp** (robust HTML5 parsing; regex over `<meta>` breaks on real-world markup).
Precedence per field: **oEmbed → Open Graph → Twitter Card → bare HTML → nothing**.

| Embed field | Source |
|---|---|
| `title` | `og:title` → `twitter:title` → `<title>` |
| `description` | `og:description` → `twitter:description` → `<meta name=description>` |
| `url` | `og:url` (validated same-origin-ish) → final redirect URL |
| `provider.name` | `og:site_name` → registrable domain |
| `author.name/url` | `article:author`, `og:article:author`, oEmbed `author_name`/`author_url` |
| `thumbnail`/`image` | `og:image[:secure_url]` → `twitter:image` → `<link rel="apple-touch-icon">` |
| `type` | `og:type` mapped: `video.*`→`video`, `article`→`article`, `image`→`image`, else `link` |
| `color` | accent from the fetched image, or `theme-color` |

Then clamp to the §1 limits (title 256, description 4096 — Discord visually truncates link
descriptions much shorter, but store the full value and let the client clamp), and **HTML-decode plus
strip control characters** on every extracted string. Treat every field as hostile: it is
attacker-authored text that will be rendered in a chat client.

### 6.4 Media proxy — `Unfurl.Application/Media/`

Mirrors `ProcessAttachmentHandler` almost exactly:

1. SSRF-guarded fetch of the `og:image` (8 MB cap, `image/*` only).
2. ImageSharp: read real `width`/`height` (never trust `og:image:width`), reject absurd dimensions
   (>10000px either axis, >50 MP decode — decompression-bomb guard), re-encode to JPEG/WebP capped at
   1280px on the long edge.
3. Compute a blur placeholder → `placeholder` + `placeholder_version: 1`, matching Discord's field
   name. **Implemented as BlurHash, not Discord's thumbhash**: thumbhash has no published spec,
   whereas BlurHash is specified and has maintained decoders for web, iOS, Android and Flutter, so
   clients get a dependency rather than a reverse-engineering project. `placeholder_version` is
   what lets this change later without breaking clients holding old messages.
4. `PutObjectAsync` to `previews/{sha256(sourceUrl)}.jpg` in the existing bucket.
5. `proxy_url` = `{InstanceUrl}/api/v1/previews/media/{hash}`, served by a controller that mirrors
   `AttachmentController`'s S3-read + Redis-byte-cache pattern, but **unauthenticated** and
   long-`Cache-Control`.

Re-hosting rather than hot-linking is what stops the target site from seeing every viewer's IP, and
it is why Discord's `proxy_url` exists. Note the trade: proxy URLs are content-addressed by source
URL hash, so anyone who knows the source URL can derive the proxy URL. Since the content is public web
material either way, that is acceptable — but it means **the proxy must never be pointed at
authenticated internal content**, which the SSRF guard already prevents.

### 6.5 Cache — Redis via `IDistributedCache`

Key `unfurl:v1:{sha256(normalizedUrl)}`. Positive TTL from the origin's `Cache-Control`/`Expires`
clamped to **[15 min, 24 h]** (default 6 h); negative TTL **10 min** with the failure reason, so a
dead link is not re-fetched on every mention. Bump `v1` to invalidate the world after a parser change.

### 6.6 Provider registry — the in-app players

A table of host patterns → handler, tried before generic OG parsing:

| Provider | Result |
|---|---|
| YouTube (`youtube.com/watch`, `youtu.be`, `/shorts/`) | `type: video`, `video.url = https://www.youtube.com/embed/{id}`, thumbnail from `i.ytimg.com`, `provider.name: "YouTube"` |
| Vimeo | oEmbed → `player.vimeo.com/video/{id}` |
| Twitch (vod / clip / channel) | `player.twitch.tv/?video=…&parent=…` |
| Spotify | `open.spotify.com/embed/{type}/{id}` |
| SoundCloud | oEmbed |
| Twitter/X, Bluesky, Reddit, Mastodon | rich text card via OG/oEmbed, no player |
| Direct `.gif`/`.gifv` | `type: gifv`, transcode to mp4 (FFMpegCore is already present) |

**Security rule, non-negotiable: `video.url` may only ever be produced by an entry in this registry.**
Generic OG parsing must never emit a `video` object, because a client renders `video.url` in an
iframe — letting arbitrary sites populate it hands every attacker an iframe on every viewer's client.
Enforce it structurally: the generic parser's return type simply cannot carry a `video`, and there is
an architecture test asserting so.

Generic providers should also honour the standard oEmbed discovery link
(`<link rel="alternate" type="application/json+oembed">`) for *metadata only* — never for `html`/iframe
content.

---

## 7. Suppression — "the creator yeets it and it's gone for everybody"

`PATCH /api/v1/messaging/{messageId}/embeds` → `SuppressMessageEmbedsCommand { MessageId,
RequestingUserId, Suppress }`.

- **Authorization:** author always; otherwise `ExternalPermission.DeleteAnyMessage` (the closest
  existing analogue to Discord's `MANAGE_MESSAGES`) via `HasUserPermissionToChannelRequest`, matching
  the existing delete path in `MessagingEndpoints.cs:384-420`. In DMs, author only.
  Note this needs a path around `UpdateMessageCommand`'s author-only check — extend the existing
  `AllowBotAuthorEdit` idea into an explicit `AuthorizationAlreadyChecked` flag rather than widening
  the author comparison.
- **Effect:** sets `Flags |= SUPPRESS_EMBEDS` **and** clears generated embeds from `EmbedsJson`
  (author-supplied bot embeds are only hidden by the flag, not destroyed — Discord's unsuppress
  restores them). Emits the ordinary `MessageUpdated`, so it reaches web, guild and bot clients
  through the existing fan-out with zero new realtime plumbing.
- **Idempotent and re-entrant:** the flag is checked by `UnfurlLinksHandler` before fetching and again
  by `ApplyGeneratedEmbedsCommand` before writing, so an in-flight unfurl that lands after the
  suppression is discarded rather than resurrecting the card.
- Unsuppressing (`Suppress: false`) clears the bit and re-queues an unfurl. Discord supports this;
  it is cheap here.

---

## 8. Correctness details that will bite

1. **Race: edit lands while the unfurl is in flight.** `ApplyGeneratedEmbedsCommand` must carry the
   SHA-256 of the content it unfurled and abort if `message.Content` no longer hashes to it. Otherwise
   an edited message shows the previous link's card.
2. **Wolverine retries.** Handlers are retried; applying embeds must be idempotent (it is, if it
   replaces rather than appends the generated-flagged entries).
3. **Tuple double-publish.** `CreateMessageCommand`'s XML docs are emphatic that `MessageCreated` may
   only be raised there — and `InvokeAsync<T>` on a tuple-returning handler *still publishes the other
   members*. `ApplyGeneratedEmbedsCommand` must therefore not be invoked in a way that re-publishes
   `MessageUpdated` twice; use `SendAsync` and let the cascade do the publishing exactly once.
4. **Author-supplied embeds win.** If `EmbedsJson` is non-empty at create time (bot/webhook message),
   skip unfurling entirely. That matches Discord and avoids the merge problem.
5. ~~**Federated inbound messages** should *not* be re-unfurled.~~ **Reversed during implementation.**
   The federation contract (`FederatedMessageCreatedReceived`) carries no embeds at all, so the
   originating instance's preview never travels — gating on origin would mean federated messages
   simply never have previews. They are re-unfurled locally instead, which also means each instance
   re-hosts its own copy of the image and its own users' IPs stay behind its own proxy. The cost is
   one origin fetch per instance, bounded by each instance's own cache.
6. **Crosspost/publish** (`PublishEndpoint`) already copies `EmbedsJson`; it will now copy generated
   embeds too, which is correct — but the copy must also carry `Flags`.
7. **6000-char combined cap** across all embeds on a message — enforce server-side before persisting,
   or a malicious page can bloat every message row.
8. **AutoMod / blocked links.** Unfurling happens after AutoMod, so a message AutoMod rejected never
   reaches the unfurler. Worth confirming there is no path where a link is scrubbed from `Content` but
   still unfurled.

---

## 9. The E2EE ceiling

In `MessageEncryptionState.Encrypted` contexts, `Content` is MLS ciphertext. The server cannot see the
URL, so it cannot unfurl — the same wall that stops search indexing (`CreateMessageCommand` already
gates on `EncryptionState == Plain`). Gate the unfurl handler identically.

**Encrypted DMs will have no previews.** That is not a bug to fix but a property to document. If we
want them later, the only sound route is client-assisted: the sending client extracts the URL, calls
an authenticated `POST /api/v1/previews/resolve`, receives an `EmbedPayload`, and sends it as part of
the encrypted payload. That trades a little metadata (the server learns a URL was resolved by
*someone*, without learning who sent it to whom) for the feature, and it means the embed is
author-supplied — so suppression there is a plain edit, not a flag. Out of scope for v1; listed as
Phase 4 so the decision is explicit rather than forgotten.

---

## 10. Phases

| Phase | Scope | Output |
|---|---|---|
| **0 — Contracts** | Full `EmbedPayload` shape; `Message.Flags` + Scylla `SelectColumns` append + both migrations; `EditedAt` split; `Flags`/`ComponentsJson` on the three update events; suppression endpoint + permission check | Suppression works end-to-end against bot-authored embeds. No fetching yet. Independently shippable and independently testable. |
| **1 — Text previews** | `Unfurl` service skeleton; `LinkExtractor`; SSRF-guarded fetch; AngleSharp OG/Twitter/HTML parsing; Redis cache; `UnfurlLinksHandler` + `ApplyGeneratedEmbedsCommand` with CAS | Title/description/site-name cards appear a moment after send, in DMs and guild channels, on web and for bots. |
| **2 — Media proxy** | Image fetch + ImageSharp dims/resize/bomb-guards + thumbhash; S3 `previews/`; unauthenticated proxy controller; `proxy_url`/`width`/`height`/`placeholder` populated | Cards get images. Viewer IPs stay off third-party origins. |
| **3 — Players** | Provider registry (YouTube, Vimeo, Twitch, Spotify, SoundCloud, gifv), oEmbed discovery, `type: video`/`gifv`, iframe-URL whitelist + architecture test | In-app YouTube player and friends. |
| **4 — E2EE (optional)** | `POST /api/v1/previews/resolve` for client-assisted unfurl in encrypted contexts; per-user and per-guild "show link previews" settings | Parity in encrypted DMs, with the metadata trade stated above. |

**Phases 0–3 are implemented.** Phase 4 (client-assisted unfurl for E2EE conversations, and
per-user/per-guild preview settings) is not, and remains a deliberate decision rather than an
oversight — see [§9](#9-the-e2ee-ceiling).

Two things surfaced during implementation that the plan above did not anticipate, both now fixed:

- **The author was excluded from `*.MessageUpdated` broadcasts.** Reasonable for an author's own
  edit — their client already rendered it — and completely wrong for a server-attached preview,
  which would have made the person who posted the link the one person who never saw its card.
  `MessageUpdated.IsAuthorEdit` now drives that choice on both the DM and channel paths.
- **Re-unfurling on edit can loop.** Attaching a preview publishes `MessageUpdated` like any other
  write, so a handler that reacts to every update unfurls itself forever. The same `IsAuthorEdit`
  flag is the guard, and `UnfurlLinksHandlerTests.TheUnfurlersOwnUpdate_IsNotRequeued` pins it.

---

## 11. Testing

Per the repo's normal/edge/negative expectation:

- **`LinkExtractor`** — plain link; multiple links deduped; link in inline code; link in a fenced
  block; `<https://…>` suppressed; >5 links truncates; no link; malformed URL; `javascript:`/`data:`
  rejected.
- **SSRF guard** — literal `127.0.0.1`, `169.254.169.254`, `10.x`, `::1`, `fc00::`, IPv4-mapped IPv6;
  hostname resolving to private (rebinding); public host redirecting to private on hop 3; >5 hops;
  scheme downgrade mid-redirect.
- **Fetch limits** — 6s-responding origin times out; 3 MB body truncates; lying `Content-Length`;
  `image/*` and non-HTML content types.
- **Parser** — OG complete; Twitter-only; bare-HTML fallback; missing everything; over-long title and
  description clamped; HTML entities decoded; script/control chars stripped; combined 6000-char cap.
- **Provider registry** — each provider's URL forms; that the generic parser *cannot* emit `video`
  (architecture test, sibling to the existing `Domain.Tests/Facets` entity-leak test).
- **Suppression** — author suppresses; moderator with `DeleteAnyMessage` suppresses; unrelated user
  gets 403; DM non-author 403; suppress-then-late-unfurl does not resurrect; unsuppress re-unfurls;
  suppression visible to every viewer and to bots.
- **Races/idempotency** — edit during unfurl (CAS aborts); handler retried twice produces one embed
  set; encrypted message never unfurled; bot message with embeds never unfurled; federated inbound
  never re-unfurled.
- **E2E** (`Echo.E2E.Tests/Scenarios/`) — post a link against a stub origin served by the harness,
  assert `guild.MessageUpdated` arrives with the populated embed and that `MessageCreated` arrived
  before it with none. Mirrors the existing flow-test style.
- **Cache** — second identical URL does not re-fetch; negative cache honoured; TTL clamped both ways.

Regenerate `Docs.Generator` output (`asyncapi.json`, `realtime-inventory.json`) after the event-payload
changes, and update `Messaging.Application/docs/message-embeds-frontend-guide.md` — it currently tells
clients that images/colors/timestamps are dropped.

---

## Sources

- [Discord — Message Resource (embed object, limits, `SUPPRESS_EMBEDS`)](https://docs.discord.com/developers/resources/message)
- [Discord — Gateway Events (`MESSAGE_UPDATE`)](https://docs.discord.com/developers/events/gateway-events)
- [discord-api-docs #5406 — embeds arrive via a later `MESSAGE_UPDATE`](https://github.com/discord/discord-api-docs/issues/5406)
- [discord-api-docs #6392 — Unfurler service, Media Proxy `/_metadata`, Lilliput, size ceilings](https://github.com/discord/discord-api-docs/issues/6392)
- [discord-userdoccers — attachments & embeds, `placeholder` thumbhash, `proxy_url`](https://deepwiki.com/discord-userdoccers/discord-userdoccers/8.2-attachments-and-embeds)
- [URL unfurling: how Slack, Discord and Twitter generate link previews](https://dev.to/eatyou_eatyou_d79d27e5622/url-unfurling-how-slack-discord-and-twitter-generate-link-previews-5hgb)
