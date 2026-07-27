# Message embeds — frontend integration guide

Backend support for rich "embed" cards on messages (the kind bots reply with — title,
description, fields, footer, like Discord's embeds) is done and live. This is what the client
needs to build to actually render them.

## What's new

Every message that comes back from the API or over realtime now optionally carries an
`embedsJson` field:

```json
{
  "id": "mesg_3H66JNBG6BTA8FINHJVTTE2H846",
  "content": "",
  "authorId": "user_3H61jLFREDU2Gl6ummuoEj5ta0h",
  "embedsJson": "[{\"title\":\"Health: OK\",\"description\":\"All systems operational\",\"fields\":[{\"name\":\"Uptime\",\"value\":\"3d 4h\",\"inline\":true}],\"footer\":{\"text\":\"Checked just now\"}}]",
  "...": "..."
}
```

It's a **JSON-encoded string**, not a nested object — `JSON.parse(message.embedsJson)` to get an
array of embed objects. It's `null`/absent when the message has no embeds (the normal case for
regular chat messages).

## Where it shows up

Same places `content` already does — nothing new to fetch, just a new field to read:

- `guild.MessageCreated` (SignalR realtime event, channel messages)
- `conversation.MessageCreated` (SignalR realtime event, DMs)
- `GET /api/v1/messaging/channels/{channelId}/messages` (message history)
- `POST /api/v1/messaging/{conversationId or channelId}` response body (sending a message)

## Embed object shape

```ts
interface Embed {
  title?: string;
  description?: string;
  url?: string;
  author?: { name: string };
  fields: { name: string; value: string; inline: boolean }[];
  footer?: { text: string };
}
```

This is a subset of Discord's own embed object (same field names/meaning) — bots built against
Discord SDKs (`EmbedBuilder`, discord.js, discord.py, etc.) already produce exactly this shape, no
translation needed on the bot side. Colors, images/thumbnails, and timestamps aren't carried yet —
if a bot sets them they're silently dropped server-side today.

## Rendering

Suggested minimum: a bordered card per embed, in order, below the message's `content` (if any) —

```
[author name]
**title**            <- link to `url` if present
description

Uptime: 3d 4h          <- fields, `inline` ones side by side
─────────────────
footer text
```

## Fallback behavior (why you might already be "seeing" something)

If a bot replies with only embeds and no `content`, the backend also fills `content` with a
plain-text flattening of those embeds — so unmodified clients don't show a blank message. Once
you render `embeds` properly, prefer that over `content` when `embedsJson` is present, to avoid
showing the same information twice (once as a card, once as flattened text).
