# Message list & moderation — frontend integration guide

Three changes to the message surface: slowmode is now enforced, moderators can delete others'
messages (individually and in bulk), and message history can be paged by cursor. Backend work is
done — this is what the client needs to build against it.

## Base URL

```
https://api.venta.gg/api/v1/messaging/...
```

Normal `Authorization: Bearer <token>`.

---

## 1. Slowmode is now enforced

`Channel.slowModeSeconds` has been settable and displayable for a long time and did **nothing** —
it was never read on the send path. It is now enforced.

`POST /api/v1/messaging` can return:

```http
429 Too Many Requests
Content-Type: application/json

{ "retry_after": 12.4, "global": false, "error": "slowmode" }
```

`retry_after` is **seconds as a decimal**, from the server's own clock. Use it directly for the
countdown rather than computing from `slowModeSeconds` locally — the two drift.

### What the client should do

Ideally never hit the 429 at all: after a successful send in a channel with `slowModeSeconds > 0`,
start a local cooldown and disable the composer for that long, showing the countdown. Treat a 429
as the fallback for when local state was wrong (another device, a clock difference, a reconnect).

### Who bypasses it

- Bots and webhooks always.
- Any member with `ManageChannel` or `ManageAnyThread` in that channel.

The server decides this; the client can't compute it from the channel alone. If you want to skip
the composer cooldown for moderators, check those permissions — but a wrong guess only costs a
recoverable 429, so erring toward showing the cooldown is safe.

### Ordering note

Slowmode is checked **after** the permission and automod gates, so a message rejected for a
blocked word does not consume the author's cooldown. A user who gets a 403 can immediately retry
with different text.

---

## 2. Deletion

### Moderators can now delete others' messages

`DELETE /api/v1/messaging/{messageId}` previously allowed **only the author**. Moderators could
not remove another member's message at all, through any endpoint. It now allows the author OR
anyone with `DeleteAnyMessage` in that channel.

No request change. But the client should now show "Delete" in the message context menu for
moderators, not just authors — that option previously would have 403'd.

DMs are unchanged: no channel means no permission to check, so author-only still applies.

### Bulk delete

```http
POST /api/v1/messaging/bulk-delete

{ "channelId": "chan_...", "messageIds": ["mesg_1", "mesg_2"] }
```

Requires `DeleteAnyMessage`. Max **100** ids per call; over that is a `400`.

```json
{ "deleted": 2, "messageIds": ["mesg_1", "mesg_2"] }
```

**Check `deleted` against what you sent.** Ids that don't exist, or that belong to a different
channel, are silently skipped — the permission check covered one channel, so ids from elsewhere
are not acted on. A partial result is normal and not an error; reconcile your local state against
the returned `messageIds` rather than assuming everything you asked for went.

Every id must be in `channelId`. Mixed-channel batches are not supported — split them.

### Realtime

Two events fire for a bulk delete, and you want the aggregate one:

```js
// One per message — the same event a single delete has always emitted.
connection.on("guild.MessageDeleted", ({ messageId, channelId }) => { ... })

// One per bulk call. Prefer this: it removes a whole range in a single UI update.
connection.on("guild.MessagesBulkDeleted", ({ guildId, channelId, messageIds, actorUserId }) => { ... })
```

If you handle both, deduplicate — every id appears in both. Handling only the aggregate is fine
and simpler.

---

## 3. Cursor pagination (additive)

The two history endpoints now accept cursors:

```http
GET /api/v1/messaging/channels/{channelId}/messages?before={messageId}&limit=50
GET /api/v1/messaging/conversations/{conversationId}/messages?after={messageId}&limit=50
GET /api/v1/messaging/channels/{channelId}/messages?around={messageId}&limit=50
```

| Param | Returns |
|---|---|
| `before` | Messages older than the anchor, anchor excluded |
| `after` | Messages newer than the anchor, anchor excluded |
| `around` | Half a page either side, **anchor included** |

Cursors are **message ids**, not timestamps. Only one is honoured per request; if you send more
than one they're preferred in the order above rather than rejected.

### The response shape is unchanged

Same array of messages, same ordering (oldest-first), same everything. A client adopts cursors by
just… sending `before` instead of `offset`. There's no wrapper type and no new parsing.

Infer "there is more" the way you always have: a full page back (`count === limit`) probably means
another page exists.

### `offset` still works

Nothing breaks. `offset`/`limit` remain supported and behave identically to before. They are
**deprecated** though, and you should migrate, because:

- Offset paging drifts on a live channel. Messages arriving while a user scrolls shift the window,
  which shows duplicates or skips messages. Cursors are stable against concurrent writes.
- `around` is the only way to implement **jump-to-message** (permalinks, search results, reply
  references, jump-to-first-unread). Offset can't express it.

If no cursor is supplied the old path runs unchanged, so migration can be per-call-site.

### Stale cursors

An anchor that doesn't exist — deleted message, wrong channel — returns an **empty array**, not an
error. Treat empty-with-a-cursor as "your cursor is stale, re-fetch from the top" rather than
"you've reached the end".

---

## Summary of client work

1. Composer cooldown for slowmode channels; handle `429` + `retry_after` as the fallback.
2. Show "Delete" for `DeleteAnyMessage` holders, not only authors.
3. Multi-select + bulk delete in the moderation UI; reconcile against the returned `messageIds`.
4. Handle `guild.MessagesBulkDeleted` for one-shot range removal.
5. Migrate history paging from `offset` to `before`, and build jump-to-message on `around`.
6. Treat an empty cursor page as "stale cursor", not "end of history".
