# Slash commands - frontend integration guide

How the client surfaces and invokes bot slash ("/") commands. Backend work is done and live -
this is what the client needs to build against it.

## The core thing to understand

There is no separate "real Discord client" anywhere in this system - **venta.gg's own client
plays that role**. On real Discord, Discord's client reads a bot's registered commands and builds
an "Interaction" when a user submits one. Here, the bot registers its commands with our backend
the same way (via its own SDK, already working), and **the venta client calls straight into our
backend to discover and invoke them** - there's no local command parsing to build, "/" is just a
trigger for two REST calls.

A command's *result* is not returned synchronously from the invoke call. The bot receives the
interaction over its own connection, does its work, and responds - which shows up as a **normal
message** in the channel through the exact same paths the client already uses for every other
message (REST fetch + the `guild.MessageCreated` realtime event). There is nothing new to build
for displaying the result - only for triggering the command.

## Base URL

All endpoints below go through the Yapper/Echo gateway, same as every other authenticated call in
the app:

```
https://api.venta.gg/api/v1/bots/...
```

Not an internal service hostname - always the public gateway URL for whatever environment you're
pointed at (dev/staging/prod), with the `/bots/` segment included (the gateway strips it before
forwarding to the Bots service internally, but the client always includes it).

## Auth

Normal `Authorization: Bearer <token>` - the same access token every other authenticated endpoint
in the app uses. No bot-specific auth on either endpoint below; both act on behalf of whichever
human user is logged in.

## 1. Discover available commands

```
GET /api/v1/bots/guilds/{guildId}/commands
```

Returns every command registered by every bot currently installed in that guild - merges each
bot's global commands with any commands scoped to just this guild:

```json
[
  {
    "botUserId": "user_3H5WAJjhh4BBdF0zAqsOzKLkIir",
    "botName": "Captain Hook",
    "name": "ping",
    "description": "Replies with pong",
    "options": [
      { "name": "target", "description": "Who to ping", "type": 6, "required": false }
    ],
    "scope": "global"
  }
]
```

- `options` is the command's raw option schema, straight from the bot's own registration - same
  shape as Discord's option objects (`type` is Discord's numeric option type: 3=string, 4=integer,
  5=boolean, 6=user, 7=channel, 8=role, 9=mentionable, 10=number). Use it to render the right input
  control per option when building the "/" autocomplete/argument UI.
- `scope` is `"global"` or `"guild"` - informational only, doesn't change how you invoke it.
- Call this whenever the "/" picker opens for a channel (or cache per-guild and refresh on
  `guild.BotInstalled`/`guild.BotUninstalled` realtime events - see below).
- Empty array is a normal response (no bots installed, or none have registered commands) - show
  the composer's normal empty state, not an error.

## 2. Invoke a command

```
POST /api/v1/bots/guilds/{guildId}/channels/{channelId}/interactions
```

```json
{
  "botUserId": "user_3H5WAJjhh4BBdF0zAqsOzKLkIir",
  "commandName": "ping",
  "options": [
    { "name": "target", "value": "user_abc123" }
  ]
}
```

- `options` only needs `name`/`value` pairs for whatever the user actually filled in - omit ones
  they left blank. `value` is sent as-is (string/number/bool matching what the option expects);
  the server looks up the option's declared type from the command's own registration, so you don't
  need to encode type information here.
- **Success is `202 Accepted` with no body.** This just means the invocation was handed off to the
  bot - not that it has responded yet. Show the composer's normal "message sent" UX (clear the "/"
  input, maybe a brief pending/typing indicator) and then let the bot's actual reply arrive through
  the normal message pipeline like anything else in the channel.
- A bot can take anywhere from under a second up to ~15 minutes to respond (it may defer while it
  does real work, then follow up later) - don't block the UI waiting on this call beyond the normal
  network-request spinner. There's no separate "loading" state to build for the reply itself, since
  it's just a message arriving the same way any other message would.
- Errors:
  - `401` - not logged in.
  - `403` - the user doesn't have `SendMessages` in that channel (same permission gate as sending a
    normal message there - if they can't type in the channel, they can't invoke a command in it).
  - `404` - the bot isn't installed in this guild, or the command name doesn't exist (can happen if
    your cached command list is stale - refetch discovery and retry once before surfacing an error).

## Known v1 limitations (by design, not bugs)

- **No autocomplete interactions** - option values are whatever the user typed, no live
  suggestion round-trip to the bot as they type.
- **No buttons/select menus** - a bot's response is plain message content only.
- **"Ephemeral" responses aren't private** - a bot can set Discord's ephemeral flag, but there's no
  "only visible to the invoker" concept in venta's channel model yet, so it just shows as a normal
  message everyone in the channel can see. Not something to special-case client-side; just be aware
  a bot's docs might claim a reply is ephemeral when it visibly won't be.
- **Only slash (`/`) commands** - no right-click-user or right-click-message command types.

## Realtime updates

The existing SignalR hub already broadcasts when a bot is installed/removed from a guild:

- `guild.BotInstalled` - `{ guildId, userId }` (`userId` here is the bot's id)
- `guild.BotUninstalled` - `{ guildId, userId }`

Use these to invalidate/refetch the command list for a guild rather than polling - same pattern
as any other realtime-driven cache invalidation already in the client.

A bot's response message arrives via the existing `guild.MessageCreated` event / message-history
REST endpoint, same as every other message. If you want to visually badge bot replies, the message
object already carries `authorIdType: "Bot"` for messages authored by a bot.
