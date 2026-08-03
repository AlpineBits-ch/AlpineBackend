# Inbox — frontend integration guide

Two tabs — **Unread** and **Mentions** — spanning every guild the caller is in. Backend work is
done; this is what the client needs to build against it.

## Base URL

```
https://api.venta.gg/api/v1/guild/inbox/...
```

Normal `Authorization: Bearer <token>`. Every route acts on **the caller's own** inbox — there is no
user id in any path and no way to read anyone else's.

| | |
|---|---|
| `GET` | `/api/v1/guild/inbox/unread` |
| `GET` | `/api/v1/guild/inbox/mentions` |
| `GET` | `/api/v1/guild/inbox/summary` |
| `POST` | `/api/v1/guild/inbox/channels/{channelId}/read` |
| `POST` | `/api/v1/guild/inbox/read-all` |
| `DELETE` | `/api/v1/guild/inbox/mentions/{messageId}?createdAt=…` |

---

## Things to know before you build against this

**Timestamps decide what is unread, never ids.** Message ids sort by creation time *only* if they
were minted after the ULID change; older ones do not sort at all. Treat every id as opaque — never
compare two of them to work out which is newer. `lastActivityAt` and `createdAt` are what order
things.

**`lastMessageId` may point at a message that no longer exists.** Same caveat Discord documents on
its own. Don't assume a fetch for it will succeed.

**There is no backfill.** Mentions that predate the feature shipping do not appear. The index keeps a
rolling 31-day window and older rows expire on their own, so the Mentions tab is never a complete
archive — it is a recent-activity view. Do not build "all my mentions ever" on top of it.

**Counts are two different qualities.** `mentionCount` is exact. `unreadCount` is best-effort,
derived from denormalized counters that drift under message loss; it is clamped at zero and is for
display only. Show them differently if the distinction matters to you.

**`@here` reaches people who were online when it was sent.** If the user was idle, invisible, or
offline at that moment it is not their mention and will not appear. `@everyone` reaches everyone who
had already joined; `@role` reaches whoever already held the role. Being *given* a role does not
surface that role's older pings.

---

## `GET /api/v1/guild/inbox/unread`

Channels with messages newer than the caller's read cursor, newest activity first.

| Query | Type | Default | Notes |
|---|---|---|---|
| `limit` | int | `10` | Clamped to 1–25 |
| `cursor` | string | – | Opaque; from the previous response's `nextCursor` |

```jsonc
{
  "groups": [
    {
      "breadcrumb": {
        "guildId": "gild_01J…",
        "guildName": "Echo",
        "guildIconUrl": "/api/v1/guild/guilds/gild_01J…/icon",
        "guildIconThumbnailUrl": "/api/v1/guild/guilds/gild_01J…/icon/thumbnail",
        "categoryId": "cate_01J…",     // null when uncategorised
        "categoryName": "General",
        "channelId": "chan_01J…",
        "channelName": "announcements",
        "channelType": 0,
        "parentChannelId": null,        // set for threads and forum posts
        "parentChannelName": null
      },
      "lastActivityAt": "2026-08-03T10:14:22.115Z",
      "unreadCount": 8,                 // best-effort, clamped at 0
      "mentionCount": 2,                // exact
      "previews": [ /* InboxMessage, oldest first, max 5 */ ],
      "previewsTruncated": true
    }
  ],
  "nextCursor": "MTc1NDIxNzY2MjAwMDAwMDA6Y2hhbl8wMUo…",
  "previewsUnavailable": false
}
```

### `previewsUnavailable`

`true` means the message service could not be reached. **The groups, counts and breadcrumbs are still
correct** — they come from a different service — so render the tab, just without message bodies. This
is a `200`, not an error. Retrying the same request is reasonable.

### Guild icons

`guildIconUrl` is a fixed path, not a stored value. It redirects to a presigned URL, and returns
`404` when the guild has no icon — render the name-initial fallback on 404. Use the thumbnail variant
for the small avatar; it is what the inbox is sized for.

### What never appears here

Muted channels, categories and guilds (a mute is "not now"); channels set to notify `Nothing`;
channels the caller can no longer see; household-module channels, which keep no message history.
Muted channels **do** still appear under Mentions.

### Paging

Keyset, not offset — the list reorders under you as messages arrive. Follow `nextCursor` until it is
`null`.

A page can come back with **fewer groups than `limit`, or none at all, and still have a
`nextCursor`** — muting and permission filtering are applied after the page is taken. An empty
`groups` array with a non-null `nextCursor` means "keep going", not "you're done". Stop only when
`nextCursor` is `null`.

A cursor the server doesn't recognise is treated as the first page rather than rejected, so a stale
bookmark degrades instead of breaking.

---

## `GET /api/v1/guild/inbox/mentions`

Messages that mentioned the caller, newest first. Merges direct/`@here` mentions with
`@everyone`/`@role` pings.

| Query | Type | Default | Notes |
|---|---|---|---|
| `guildId` | string | – | Restrict to one guild |
| `since` | string | `7d` | `24h`, `7d` or `30d`. Anything else falls back to the default |
| `includeEveryone` | bool | `true` | |
| `includeRoles` | bool | `true` | |
| `includeDms` | bool | `true` | |
| `limit` | int | `25` | Clamped to 1–50 |
| `cursor` | string | – | Opaque |

```jsonc
{
  "mentions": [
    {
      "messageId": "mesg_01J…",
      "createdAt": "2026-08-03T09:41:02.884Z",
      "kind": "Direct",              // Direct | Here | Everyone | Role
      "roleId": null,                // set when kind is Role
      "roleName": null,
      "authorId": "user_01J…",
      "breadcrumb": { /* as above; null for a DM mention */ },
      "conversationId": null,        // set for a DM mention
      "message": { /* InboxMessage */ }
    }
  ],
  "nextCursor": null
}
```

`kind` is the most specific one that applies: someone named directly inside an `@everyone` message
gets `Direct`, and the message appears once, not twice.

Messages deleted since being indexed are **skipped**, not rendered as a hole — so a page can be
shorter than `limit` while more pages exist. Same rule as Unread: page until `nextCursor` is `null`.

`since` is capped at the 31-day retention window regardless of what you ask for.

---

## `GET /api/v1/guild/inbox/summary`

The header badge.

```jsonc
{ "unreadChannelCount": 4, "mentionCount": 12, "capped": false }
```

`capped: true` means the real numbers are higher than reported — render as `99+`. Counting further
would mean an unbounded scan for a number that renders the same either way.

---

## `POST /api/v1/guild/inbox/channels/{channelId}/read`

Marks one channel read up to its current head. No body. `204` on success.

Same effect as the `guild.UpdateLastRead` hub method — this exists so the check button works without
a live socket. A channel with no messages is a `204` no-op, not an error.

| Status | Meaning |
|---|---|
| `204` | Done, or nothing to do |
| `403` | Not a member of the channel's guild |
| `404` | No such channel |

---

## `POST /api/v1/guild/inbox/read-all`

Marks every channel in every guild read. No body. `204`.

Idempotent. Rate-limited — this is a bulk write across the caller's whole account, so don't call it
on a timer.

---

## `DELETE /api/v1/guild/inbox/mentions/{messageId}?createdAt=…`

Dismisses one mention (the ✕). `204`, idempotent — dismissing twice is the same as once.

`createdAt` is **required** and must be the exact ISO-8601 timestamp from the mention row, not a
re-parse of the message's own timestamp. The index is keyed on it, so a mismatch silently deletes
nothing. Pass back what the mentions response gave you.

`400` if `createdAt` is missing or unparseable.

Only direct and `@here` mentions can be dismissed. `@everyone` and `@role` pings are not per-user
rows, so there is nothing to delete — dismissing one is accepted and does nothing.

---

## Shared object: `InboxMessage`

```jsonc
{
  "id": "mesg_01J…",
  "createdAt": "2026-08-03T09:41:02.884Z",
  "authorId": "user_01J…",
  "authorDisplayName": null,   // set for webhook and bot authors
  "authorAvatarUrl": null,
  "content": "SGVsbG8…",       // base64 bytes
  "isEncrypted": false,
  "mlsGeneration": null,
  "type": 0,                   // 0 Message, 1 Invite, 2 GuildMemberJoin, 3 GuildMemberLeave
  "systemMessageVariant": null,
  "embedsJson": null
}
```

`content` is raw bytes. When `isEncrypted` is `true` it is ciphertext — decrypt it exactly as you do
for channel history, using `mlsGeneration` to pick the group. **The server never decrypts, so it
cannot generate a text preview for an encrypted channel.** If you show a fallback line for encrypted
previews, that is a client decision.

For `type` 2 and 3, `content` is empty and `systemMessageVariant` (0–9) selects which localized
phrasing to render.

---

## Realtime

Server→client only, over the existing hub. No new client→server methods.

### `inbox.MentionAdded`

```jsonc
{
  "messageId": "mesg_01J…",
  "channelId": "chan_01J…",
  "guildId": "gild_01J…",
  "conversationId": null,
  "authorId": "user_01J…",
  "kind": "Direct",
  "createdAt": "2026-08-03T09:41:02.884Z"
}
```

Sent only to users the message actually mentioned, and only those who can see the channel.

### `inbox.ReadStateChanged`

Sent to the acking user's **other** devices so a second client's badge clears.

```jsonc
{ "channelId": "chan_01J…", "lastReadMessageId": "mesg_01J…", "mentionCount": 0 }
```

Read-all sends `{ "all": true }` instead — treat that as "clear every badge" rather than looking for
a channel id.

---

## Suggested client behaviour

- Open the popout → `GET unread` and `GET mentions` in parallel; render whichever lands first.
- Poll `summary` for the badge, or just keep it current from `inbox.MentionAdded` and
  `inbox.ReadStateChanged`. It is cheap, but it is not free.
- On `previewsUnavailable`, show the groups and retry previews in the background rather than showing
  an error.
- Treat an empty page with a non-null cursor as "keep paging" — see above. This is the single easiest
  thing to get wrong here.
