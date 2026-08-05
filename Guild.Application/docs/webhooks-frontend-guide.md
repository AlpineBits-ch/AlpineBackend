# Webhooks - frontend integration guide

Webhooks now have tokens and an externally-reachable execute URL. Backend work is done - this is
what the client needs to build against it.

## What changed and why it matters

A webhook used to be a row with an id and a channel. The execute endpoint existed but sat behind
`[Authorize]` and had no token, so the only thing that could call it was an already-logged-in
venta user - which is nobody's use case. GitHub, Grafana, Sentry and CI systems have no venta
account and never will.

Now: each webhook carries a secret token, and the execute URL is anonymous and Discord-shaped, so
an existing "Discord webhook" integration works by swapping the host and nothing else.

## Base URL

```
https://api.venta.gg/api/v1/guild/...     (management - authenticated)
https://api.venta.gg/api/webhooks/...     (execution - anonymous, token in path)
```

Management routes need `Authorization: Bearer <token>` **and** the `ManageWebhooks` permission
(bit 52 - newly split out of `ManageChannel`; see the permissions guide).

---

## 1. Management

| | |
|---|---|
| `GET /api/v1/guilds/{guildId}/webhooks` | List, with tokens |
| `GET /api/v1/guilds/{guildId}/webhooks/{webhookId}` | One, with token |
| `POST /api/v1/guilds/{guildId}/webhooks` | Create |
| `PATCH /api/v1/guilds/{guildId}/webhooks/{webhookId}` | Rename / re-avatar / move channel |
| `POST /api/v1/guilds/{guildId}/webhooks/{webhookId}/regenerate-token` | Rotate the token |
| `DELETE /api/v1/guilds/{guildId}/webhooks/{webhookId}` | Delete |

Create/read responses:

```json
{
  "id": "weco_...",
  "guildId": "gild_...",
  "channelId": "chan_...",
  "createdBy": "user_...",
  "name": "Deploy Bot",
  "avatarUrl": "https://...",
  "type": 0,
  "token": "kQ9x...",
  "url": "https://api.venta.gg/api/webhooks/weco_.../kQ9x...",
  "createdAt": "...",
  "updatedAt": "..."
}
```

**Use `url` as-is.** It's pre-composed precisely so no client has to reassemble it from id + token
and get the shape subtly wrong. It's what the user pastes into GitHub.

`DELETE` returns the webhook **without** the token.

`type` is `0` = Incoming (the only kind creatable today), `1` = ChannelFollower, `2` = Application.

### Token handling

The token is a standing write credential for that channel, valid until rotated. Treat it like a
password in the UI:

- Mask it by default with a reveal toggle; offer "Copy URL" rather than showing it inline.
- It **is** returned on every management read (this matches Discord, and a management UI that
  can't show the URL is useless) - but only to callers holding `ManageWebhooks`.
- "Regenerate" is the only way to revoke a leaked URL, since the id in the URL stays the same.
  Warn clearly: **every integration using the old URL stops working immediately.**

`PATCH` takes a partial body - omitted fields are left alone:

```json
{ "name": "New name", "avatarUrl": "", "channelId": "chan_other" }
```

An explicitly empty `avatarUrl` clears it; `null`/omitted leaves it.

---

## 2. Execution (for the user to configure elsewhere, not for the client to call)

```http
POST https://api.venta.gg/api/webhooks/{webhookId}/{token}
Content-Type: application/json

{
  "username": "deploy-bot",
  "avatar_url": "https://...",
  "content": "Build #42 passed",
  "embeds": [
    {
      "title": "Pipeline #42",
      "description": "All checks green",
      "url": "https://ci.example/42",
      "color": "#22c55e",
      "fields": [{ "name": "Branch", "value": "main", "inline": true }]
    }
  ]
}
```

Discord's field names (`username`, `avatar_url`) are accepted, which is the whole point. Responses:

| Status | Meaning |
|---|---|
| `204` | Posted |
| `400` | Neither `content` nor `embeds` supplied |
| `404` | No such webhook **or** wrong token - deliberately indistinguishable, so the endpoint can't be used to enumerate ids |
| `429` | Rate limited (100/min, budgeted per webhook so one noisy integration can't starve others) |

`username` and `avatar_url` override the webhook's configured name/avatar **for that message
only**. Omit them to use the configured defaults.

If `embeds` are sent without `content`, the server generates a readable plain-text `content` from
them - an alerting integration that sends only an embed won't post a blank-looking message.

---

## 3. Rendering webhook messages

This is the part that needs real client work.

Messages now carry two new optional fields:

```json
{
  "authorId": "weco_...",
  "authorIdType": 2,
  "authorDisplayName": "deploy-bot",
  "authorAvatarUrl": "https://...",
  "embedsJson": "[...]"
}
```

- `authorIdType: 2` is `Webhook` (0 = User, 1 = Bot).
- `authorId` is now the **webhook's id** - a resolvable entity. Previously it was whatever
  free-text username the caller sent, which no client could resolve to anything.
- **When `authorDisplayName` / `authorAvatarUrl` are present, render those** instead of trying to
  resolve `authorId` to a profile. There is no profile to resolve - that's why they travel with
  the message. They're per-message, so the same webhook can post as "deploy-bot" and "test-runner"
  and both must render correctly in the same channel.
- Both are `null` for ordinary user and bot messages; fall back to your existing author resolution.

Mark webhook messages visually (Discord uses a "BOT"-style tag) so a webhook can't impersonate a
real member by picking their name - the display name is fully caller-controlled and unvalidated.

Embeds arrive in `embedsJson` as the same JSON array bot messages already use, so if you render
bot embeds you render these with the same code.

---

## Summary of client work

1. Build the webhook management screen against the six routes above, gated on `ManageWebhooks`.
2. Mask the token, offer "Copy URL", and warn on regenerate.
3. Render `authorDisplayName` / `authorAvatarUrl` when present, and tag webhook messages so they
   can't pass as a real member.
4. Render `embedsJson` for webhook messages (same path as bot embeds).
