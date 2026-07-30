# Auto-moderation — frontend integration guide

Backend support for a per-guild blocked-word filter and a simple message-rate limit is done and
live. There is no ML-based content classification, link/invite filtering, or spam-account
detection in this pass - see Known limitations.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly.

## Configuring a guild's auto-mod

```
GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/automod
PUT https://api.venta.gg/api/v1/guild/guilds/{guildId}/automod
```

Both require `Permissions.ManageGuild`. Body/response shape:

```ts
interface AutoModConfig {
  enabled: boolean;
  blockedWords: string[];           // whole-word, case-insensitive match
  maxMessagesPerInterval?: number;  // null = no rate limit
  intervalSeconds?: number;         // required together with maxMessagesPerInterval
}
```

`PUT` fully replaces the config (not a partial patch) - always send the complete object, including
fields you aren't changing. Setting `maxMessagesPerInterval`/`intervalSeconds` to only one of the
two (leaving the other null) returns `400`.

Auto-mod is disabled by default for every guild (`enabled: false`, empty word list, no rate limit)
- this is opt-in, not something guilds need to explicitly turn off.

## What happens when a message is blocked

`POST https://api.venta.gg/api/v1/messaging/messaging` (the normal message-create call, channel
messages only - auto-mod does not apply to DMs) can now return `403 Forbidden`:

```json
{ "error": "automod_blocked", "reason": "blocked_word" }
```
or
```json
{ "error": "automod_blocked", "reason": "rate_limited" }
```

Show a specific inline error next to the composer rather than a generic send failure -
`blocked_word` ("Your message contains a word that isn't allowed here") vs `rate_limited`
("You're sending messages too quickly - try again in a moment") read very differently to a user.

Every block is recorded to the guild's audit log (`AutoModMessageBlocked`), visible via the
existing `GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/audit-log` endpoint - useful for
a moderator dashboard showing recent auto-mod activity without new plumbing.

## Rendering guidance

- Settings screen: a toggle, a tag-input style list editor for blocked words, and two number
  inputs (messages / seconds) for the rate limit, with the rate-limit fields disabled/hidden when
  toggled off.
- Bots and webhooks are never subject to auto-mod (a guild installing a bot is already an explicit
  trust decision) - don't build UI implying bot messages can be blocked by these rules.

## Known limitations (v1)

- Word filter is literal whole-word matching only - no regex, no wildcard, no fuzzy/leetspeak
  detection, no link or invite-URL filtering.
- Rate limiting is a fixed window per channel per user, not a true sliding window - close enough
  for spam prevention, but don't build UI that promises precise "N per M seconds" semantics.
- No configurable auto-mod *action* beyond "block the message" - no timeout/kick escalation, no
  "flag for review instead of blocking" mode.
- No allowlist/exempt-roles mechanism (e.g. "moderators bypass the word filter") - every non-bot
  message is checked equally.
