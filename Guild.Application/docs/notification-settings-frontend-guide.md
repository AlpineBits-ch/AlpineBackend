# Notification settings — frontend integration guide

Per-guild, per-category and per-channel notification levels and muting, plus DM muting. Backend
work is done — this is what the client needs to build against it.

**Guild channels now produce push notifications at all.** Before this, only DMs did: a mention in
a server was invisible unless the app happened to be open. That is the main reason this exists.

## Base URL

```
https://api.venta.gg/api/v1/guild/...        (guild, category, channel settings)
https://api.venta.gg/api/v1/messaging/...    (DM/group mute)
```

Normal `Authorization: Bearer <token>`. Every route here acts on **the caller's own** settings —
there is no permission dimension and no way to read or change anyone else's.

---

## The model

Three levels, most specific wins:

```
channel override  →  category override  →  guild setting  →  default (AllMessages)
```

`NotificationLevel` is an integer enum:

| Value | Name | Meaning |
|---|---|---|
| `0` | `AllMessages` | Notify on every message. The default. |
| `1` | `OnlyMentions` | Notify only on a direct @mention, a role mention, or @everyone/@here |
| `2` | `Nothing` | Never notify |

**Mute is separate from level.** A mute is a temporary silence with an expiry; a level is a
standing preference. They resolve *independently* — this is the part most likely to trip up a
client implementation:

- A channel override that only mutes does **not** reset the level inherited from the guild.
- A channel override that only sets a level does **not** clear an inherited mute.
- Unmuting returns the channel to its inherited/explicit level, not to `AllMessages`.

**Mute beats everything, including a direct @mention.** A muted channel notifies for nothing. If a
user wants "quiet unless someone actually pings me", that is `OnlyMentions`, not a mute.

### Suppression flags are guild-wide only

`suppressEveryone`, `suppressRoleMentions` and `mobilePush` live on the **guild** setting and have
no per-channel equivalent. Don't build per-channel toggles for them.

- `suppressEveryone` — drop `@everyone`/`@here` pings even at `OnlyMentions`
- `suppressRoleMentions` — same for `@role`
- `mobilePush: false` — never push to the phone; the in-app unread badge still updates

---

## 1. Read everything at once (do this on login)

```http
GET /api/v1/guild/api/v1/users/me/notification-settings
```

Returns one entry **per guild you're in**, including guilds you've never configured — those report
the effective defaults, so you never have to special-case "absent".

```json
[
  {
    "guildId": "gild_...",
    "level": 1,
    "mutedUntil": null,
    "suppressEveryone": true,
    "suppressRoleMentions": false,
    "mobilePush": true,
    "overrides": [
      { "channelId": "chan_...", "categoryId": null, "level": 2,    "mutedUntil": null },
      { "channelId": null, "categoryId": "cate_...", "level": null, "mutedUntil": "2026-08-01T10:00:00+00:00" }
    ]
  }
]
```

Note `"level": null` on an override — that means **inherit**, and is distinct from any concrete
value. An override with `level: null` and a `mutedUntil` is a pure mute that keeps its inherited
level for when the mute expires.

Single-guild read is also available: `GET /api/v1/guilds/{guildId}/notification-settings`.

## 2. Guild level

```http
PUT /api/v1/guild/api/v1/guilds/{guildId}/notification-settings

{
  "level": 1,
  "muteMinutes": 60,
  "muteForever": false,
  "suppressEveryone": true,
  "suppressRoleMentions": null,
  "mobilePush": null
}
```

**Every field is optional and omitting it means "leave alone".** This is a partial update — you
never have to read-modify-write. Sending `{ "mobilePush": false }` alone changes only that.

Mute semantics:

| Sent | Result |
|---|---|
| `muteMinutes: 60` | Muted until now + 60 minutes |
| `muteMinutes: 0` (or negative) | Unmuted |
| `muteMinutes: null` (or omitted) | Mute state untouched |
| `muteForever: true` | Muted indefinitely — outranks `muteMinutes` if both are sent |

"Muted forever" comes back as `mutedUntil: "9999-12-31T23:59:59+00:00"`. Render that as
"until I turn it back on" rather than printing the date.

Durations rather than absolute timestamps, deliberately: the client never has to reason about
clock skew against the server.

## 3. Channel and category overrides

Same body shape for both:

```http
PUT    /api/v1/guild/api/v1/channels/{channelId}/notification-settings
DELETE /api/v1/guild/api/v1/channels/{channelId}/notification-settings

PUT    /api/v1/guild/api/v1/categories/{categoryId}/notification-settings
DELETE /api/v1/guild/api/v1/categories/{categoryId}/notification-settings
```

```json
{ "level": 2, "muteMinutes": null, "muteForever": false }
```

Here `level: null` is meaningful and **is** written — it sets the override back to "inherit" while
leaving any mute in place. That is different from the guild endpoint, where null means "don't
touch". The distinction exists because "inherit" is a real state an override can be in.

An override that ends up expressing neither a level nor a mute is **deleted** rather than stored,
and returns `204` instead of the override body. So:

- `PUT` with `{ "level": 2 }` → `200` with the override
- `PUT` with `{ "level": null, "muteMinutes": 0 }` → `204`, row removed
- `DELETE` → `204`, always, even if there was nothing there (idempotent)

## 4. DM / group conversation mute

Lives on the Messaging service:

```http
PUT /api/v1/messaging/api/v1/conversations/{id}/notification-settings

{ "muteMinutes": 60, "muteForever": false }
```

Same mute vocabulary as above. **No level** — "only mentions" is meaningless in a conversation
you're one of two people in. Returns `{ "conversationId": "...", "mutedUntil": "..." }`.

A muted DM still delivers `conversation.MessageCreated` over the realtime connection so the unread
badge stays accurate. Only the phone notification is suppressed.

---

## 5. Push payload

Guild channel pushes now arrive. The FCM data payload is:

```json
{ "guildId": "gild_...", "channelId": "chan_...", "messageId": "mesg_..." }
```

DM pushes keep their existing shape (`{ "conversationId": "..." }`), so route on which key is
present.

Two things the server already handles, so the client doesn't need to:

- **Connected users are not pushed.** If the user has a live realtime connection, they get the
  message that way and no notification is sent. You do not need to suppress duplicates.
- **Encrypted messages** push the body `"You have a new encrypted message"` rather than
  ciphertext.

---

## Summary of client work

1. Fetch `/users/me/notification-settings` on login and cache it; it's one call for everything.
2. Implement the channel → category → guild → default resolution **client-side too** — you need it
   to render unread badges correctly (a muted channel should not bold), and the server won't tell
   you per-channel.
3. Resolve level and mute independently. Do not treat an override as an all-or-nothing unit.
4. Build the settings UI: guild level + mute + the three guild-wide flags; per-channel and
   per-category level + mute; DM mute.
5. Render `9999-12-31` as "muted indefinitely".
6. Route push notifications on `guildId`/`channelId` vs `conversationId`.
