# Custom guild emoji — frontend integration guide

Backend support for per-guild custom emoji, usable as message reactions, is done and live.
Custom emoji in message *content* (inline `:pepega:` rendering while typing) is not part of this
pass - see Known limitations.

## Managing a guild's emoji

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly. Guild endpoints sit behind the gateway's `/api/v1/guild/**` prefix,
Messaging endpoints behind `/api/v1/messaging/**` - both shown in full below.

| Action | Method & path | Permission |
|---|---|---|
| List emoji | `GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/emojis` | `ViewChannel` (any member) |
| Upload emoji | `POST https://api.venta.gg/api/v1/guild/guilds/{guildId}/emojis` (multipart: `name`, `animated`, `file`) | `ManageEmojis` |
| Delete emoji | `DELETE https://api.venta.gg/api/v1/guild/guilds/{guildId}/emojis/{emojiId}` | `ManageEmojis` |

`ManageEmojis` is a new permission bit - existing roles don't have it by default, grant it through
the normal role-permission editor.

Upload is `multipart/form-data`, not JSON (same reasoning as guild icon upload):

```
POST https://api.venta.gg/api/v1/guild/guilds/{guildId}/emojis
Content-Type: multipart/form-data

name=pepega
animated=false
file=<binary>
```

Emoji names must be unique per guild (case-insensitive) - a duplicate name returns `409 Conflict`.

### Response shape

```ts
interface GuildEmoji {
  id: string;
  guildId: string;
  name: string;
  animated: boolean;
  createdByUserId: string;
  createdAt: string;
  imageUrl: string;   // presigned, expires ~1h - refetch the list rather than caching long-term
}
```

## Reacting with a custom emoji

`POST https://api.venta.gg/api/v1/messaging/messages/{messageId}/reactions` gained an optional
`emojiId` field:

```json
{ "channelId": "chan_...", "emojiId": "emoj_3H66..." }
```

- When `emojiId` is present, omit `reaction` entirely - the server resolves and fills in the
  emoji's name server-side (rejects the call with `404` if the emoji doesn't belong to the guild
  that owns `channelId`).
- When `emojiId` is absent, behavior is unchanged - `reaction` must be a single unicode emoji
  character, exactly as before.
- **Custom emoji reactions only work in guild channels, not DMs** (`emojiId` + `conversationId`
  together is rejected with `400`) - custom emoji are guild-owned data with no meaning outside
  their guild.

### Removing a reaction

`DELETE https://api.venta.gg/api/v1/messaging/messages/{messageId}/reactions` body gained two
optional fields:

```json
{ "contextId": "chan_...", "reaction": "pepega", "channelId": "chan_..." }
```

Pass `channelId` (guild channel) when that's what you're removing a reaction from - it's what lets
the removal broadcast to other guild members in realtime (see below). This was previously silently
missing for *all* channel reactions, not just custom-emoji ones - fixed as part of this feature.

### Reading reactions back

Every reaction returned from message history or realtime now carries an `emojiId`:

```json
{ "emoji": "pepega", "userId": "user_...", "emojiId": "emoj_3H66..." }
```

`emojiId` is `null`/absent for ordinary unicode reactions. `emoji` is always populated - either the
literal unicode character, or the custom emoji's name as a text fallback. Look the id up against
the guild's emoji list (from the management endpoints above) to render the actual image; fall back
to rendering `emoji` as plain text (e.g. "`:pepega:`") if you don't have it cached.

## Realtime events

| Event | Target | When |
|---|---|---|
| `guild.EmojiCreated` | Guild members | `{ guildId, emojiId, name, animated }` |
| `guild.EmojiDeleted` | Guild members | `{ guildId, emojiId }` |
| `guild.ReactionCreated` | Guild channel members | Now fires for **every** channel reaction (previously didn't fire at all - see below) |
| `guild.ReactionRemoved` | Guild channel members | Same fix as above |

### Note: this also fixes plain (unicode) reactions in guild channels

Before this change, reacting to a message in a guild channel updated nothing in realtime for other
members - only DM reactions broadcast. If your client was already rendering reactions optimistically
and just refetching on next load, you'll now also get `guild.ReactionCreated`/`guild.ReactionRemoved`
pushed live, same pattern as `guild.MessageCreated`.

## Rendering guidance

- Emoji picker: fetch `GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/emojis` once per guild session, refresh on
  `guild.EmojiCreated`/`guild.EmojiDeleted`, cache `imageUrl` per emoji but re-fetch the list
  (not just the URL) once it's been an hour, since presigned URLs expire.
- Reaction pills: same layout as today, just resolve `emojiId` to an image via the cached list
  instead of rendering `emoji` as text when present.

## Known limitations (v1)

- No inline custom-emoji rendering in message *content* while composing/reading (`:pepega:`
  autocomplete-and-render) - reactions only.
- No animated-emoji-specific handling beyond the `animated` flag passthrough - rendering an
  animated image vs static is entirely client-side.
- No per-emoji usage/rate limits, no emoji count cap per guild.
- Deleting an emoji does not retroactively strip existing reactions that used it - old reactions
  keep their `emojiId`/name but the emoji list lookup will simply miss, so render your text
  fallback (`:name:`) in that case.
