# Message pinning — frontend integration guide

Backend support for pinning messages (in both guild channels and DMs) is done and live. This is
what the client needs to build to surface it.

## What's new

Every message now carries three fields:

```json
{
  "id": "mesg_3H66JNBG6BTA8FINHJVTTE2H846",
  "content": "...",
  "isPinned": true,
  "pinnedAt": "2026-07-30T14:02:11Z",
  "pinnedById": "user_3H61jLFREDU2Gl6ummuoEj5ta0h",
  "...": "..."
}
```

`pinnedAt`/`pinnedById` are `null` when `isPinned` is `false`.

## Endpoints

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly. The gateway strips the leading `/messaging` segment before forwarding, and
the Messaging service's own message routes happen to start with `/messaging` too (pre-existing,
not new to this feature) — so yes, `messaging` legitimately appears twice in a row below.

| Action | Method & path | Notes |
|---|---|---|
| Pin a message | `POST https://api.venta.gg/api/v1/messaging/messaging/{messageId}/pin` | Guild channels: requires `PinMessages` permission. DMs: any conversation member. Idempotent — pinning an already-pinned message just returns its current pin state. |
| Unpin a message | `DELETE https://api.venta.gg/api/v1/messaging/messaging/{messageId}/pin` | Same permission rules as pinning. |
| List pinned messages | `GET https://api.venta.gg/api/v1/messaging/messaging/pins?channelId={id}` or `?conversationId={id}` | Requires the same access you'd need to view the channel/conversation at all (`ViewChannel` / conversation membership). Returns up to 50 messages, most-recently-pinned first. |

Normal bearer token auth — nothing pin-specific there.

### Pin/unpin response body

```ts
interface PinMessageResponse {
  success: boolean;
  channelId?: string;
  conversationId?: string;
  authorId?: string;
  pinnedById?: string;   // present on pin, absent on unpin
  pinnedAt?: string;     // ISO 8601, present on pin, absent on unpin
}
```

## Realtime events

| Event | Target | Payload |
|---|---|---|
| `guild.MessagePinned` | Guild channel members | `{ channelId, messageId, authorId, pinnedById, pinnedAt }` |
| `guild.MessageUnpinned` | Guild channel members | `{ channelId, messageId, authorId, unpinnedById }` |
| `conversation.MessagePinned` | Conversation members | `{ messageId, conversationId, authorId, pinnedById, pinnedAt }` |
| `conversation.MessageUnpinned` | Conversation members | `{ messageId, conversationId, authorId, unpinnedById }` |

These fire in addition to (not instead of) updating `isPinned` on the message object itself — if
you're re-fetching message history you don't strictly need to listen for them, but they're the
only way to update an already-rendered message list in place without a refetch.

## Rendering guidance

- A pin icon/badge on messages where `isPinned` is `true`, same as Discord's pin indicator.
- A "Pinned Messages" panel per channel/conversation, backed by `GET .../pins` — client-owned
  layout, but showing `pinnedById` and `pinnedAt` next to each entry (mirroring who-pinned-what)
  matches what moderators expect from the audit log entry described below.
- The pin/unpin action itself belongs wherever your existing message context menu lives.

## Moderation note

For guild channels, pinning is gated by `Permissions.PinMessages` (existing permission bit — no
new role setup needed if you've already wired up permission editing). Every pin/unpin in a guild
channel also writes a `MessagePinned`/`MessageUnpinned` audit log entry, visible via the existing
`GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/audit-log` endpoint — no separate integration needed to show "who
pinned this" in an audit trail.

## Known limitations (v1)

- No per-channel pin cap (Discord caps at 50 visible pins per channel via a warning banner; we
  return at most 50 from the list endpoint but don't block pinning past that).
- No system message posted into the channel when something is pinned (Discord posts a small
  "X pinned a message" system message) — out of scope for this pass.
