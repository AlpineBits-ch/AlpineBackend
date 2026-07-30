# Guild verification levels — frontend integration guide

Backend support for gating who can join a guild based on how established their account is (mirrors
Discord's verification levels) is done and live. **v1 gates joining only** - it does not restrict
already-joined members from sending messages, and there's no periodic re-check. See Known
limitations.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly.

## Levels

| Level | Requirement to join |
|---|---|
| `None` (default) | No requirement - anyone with a valid invite can join, same as today. |
| `Low` | Verified email. |
| `Medium` | Verified email **and** account registered 5+ minutes ago. |
| `High` | Verified email **and** account registered 10+ minutes ago. |

## Setting a guild's level

Part of the existing guild-update call - `UpdateGuildDto` gained an optional `verificationLevel`
field:

```
PATCH https://api.venta.gg/api/v1/guild/guilds/{guildId}
{ "name": "...", "verificationLevel": "Medium" }
```

Requires `Permissions.ManageGuild`. Omit the field to leave the current level untouched (same
null-means-unchanged convention as the rest of this endpoint). `GET`/guild-fetch responses now
include `verificationLevel` on every guild object.

## What happens when someone doesn't meet the bar

`POST https://api.venta.gg/api/v1/guild/invites/{inviteId}/redeem` now can return `403 Forbidden`
with a structured body instead of succeeding:

```json
{ "error": "verification_level_not_met", "requiredLevel": "Medium" }
```

Show this as a specific message ("This server requires a verified email and an account at least 5
minutes old to join") rather than a generic "couldn't join" error - `requiredLevel` tells you
exactly which tier to explain. If the issue is specifically an unverified email, consider linking
directly to your existing email-verification flow from this error state.

## Rendering guidance

- Server settings → Safety/Moderation: a single select (`None`/`Low`/`Medium`/`High`) with the
  requirement spelled out under each option, same UX pattern Discord uses.
- Invite-redemption error handling: catch the `verification_level_not_met` error code specifically
  (rather than treating every `403` from this endpoint the same as a plain "you're banned" case).

## Known limitations (v1)

- **Join-time only.** A member who meets the bar at join time is never re-checked, and someone who
  doesn't meet a level raised *after* they already joined is not retroactively restricted.
- No message-sending gate for very new accounts that did meet the join bar but are still "new" in
  a stricter sense - Discord's real behavior restricts brand-new accounts' first messages/media
  even post-join; not implemented here.
- No phone-verification tier - Discord's highest tier also considers phone verification, which
  this platform doesn't collect at all.
