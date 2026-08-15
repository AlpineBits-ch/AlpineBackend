# Internal-link embeds - frontend integration guide

The server now generates cards for links that point back at this instance: guild invites and wiki
pages. They arrive as ordinary embeds on the message, through the same field and the same events
everything else already uses. **This document exists so both clients can delete their own invite and
wiki link handling entirely** - the regexes, the body-splitting, the bespoke card components and the
per-message fetches that came with them. What is left is rendering what the server sends.

Companion documents: [`message-embeds-frontend-guide.md`](message-embeds-frontend-guide.md) for the
bot-authored embed shape, and
[`../../docs/specs/message-previews-frontend-guide.md`](../../docs/specs/message-previews-frontend-guide.md)
for external link previews - async delivery, suppression, `proxy_url`, players. Everything in those
still applies; this is one more embed `type` on the same object.

## What changed

Before, `LinkExtractor` handed an invite URL to the outbound fetcher like any other link. It either
failed or scraped the web client's shell, so no useful card ever came back and both clients built
their own. Now an instance URL is recognised by host, resolved in-process, and turned into an embed
carrying `EmbedFlags.ServerGenerated` before it is stored on the message.

Nothing about delivery changed. The card arrives the way every generated preview does: the message
lands first with no embeds, and a `MessageUpdated` follows a moment later carrying them.

## Where it shows up

Exactly where `embedsJson` already does. Nothing new to fetch, no new event.

| Surface | Where |
|---|---|
| Realtime, guild channels | `guild.MessageCreated`, `guild.MessageUpdated` |
| Realtime, DMs | `conversation.MessageCreated`, `conversation.MessageUpdated` |
| Channel history | `GET https://api.venta.gg/api/v1/messaging/messaging/channels/{channelId}/messages` |
| DM history | `GET https://api.venta.gg/api/v1/messaging/messaging/conversations/{conversationId}/messages` |
| Send response | `POST https://api.venta.gg/api/v1/messaging/messaging` |

`embedsJson` is a **JSON-encoded string**, not a nested object:

```ts
const embeds: Embed[] = message.embedsJson ? JSON.parse(message.embedsJson) : [];
```

## The `venta` object

Two new embed types, and one new optional object on the embed that carries the identity of whatever
the link points at.

```ts
interface Embed {
  // ... everything in the link-previews guide, unchanged ...
  type: "rich" | "link" | "article" | "image" | "video" | "gifv"
      | "venta.invite" | "venta.wiki_page" | "venta.voice_invite";

  /** Present only on venta.* embeds. Trust it only when `flags & 65536`. */
  venta?: EmbedVenta;
}

interface EmbedVenta {
  /** `type` without the "venta." prefix, so you can switch on one value. */
  kind: "invite" | "wiki_page" | "voice_invite";

  /** Whether the server filled in title/description, or deliberately left them out. See "Stubs". */
  resolved: boolean;

  guild_id?: string;

  // invite only
  invite_code?: string;
  /** ISO-8601, or absent for an invite that never expires. */
  max_uses?: number;

  // invite and voice_invite
  /** The channel: the one a joiner lands on for an invite, the one you are asked into for a
   *  voice_invite. Id only - except on voice_invite, which also carries channel_name. */
  channel_id?: string;
  /** ISO-8601. On an invite, absent means it never expires. On a voice_invite it is present only
   *  when there was a ring, and is then about a minute after the message; absent means a standing
   *  invitation, NOT an expired one. */
  expires_at?: string;

  // wiki_page only
  page_id?: string;

  // voice_invite only
  /** The ring to accept through while it is still live. Absent entirely when the invitation was
   *  sent without one. Meaningless past `expires_at` - treat that as absent rather than callable. */
  ring_id?: string;
  /** Who asked. Same as the message's `authorId` today; carried so the card keeps meaning what it
   *  says if it is ever quoted. */
  inviter_id?: string;
  /** The channel's name when the invitation was sent. The one place a venta.* card carries a name
   *  rather than only an id - see the voice_invite section for why. */
  channel_name?: string;
}
```

**Note the casing.** Fields inside an embed are `snake_case` on the wire (`proxy_url`,
`icon_url`, `placeholder_version`, and now `invite_code`, `expires_at`). The top-level message
fields around it are `camelCase` (`embedsJson`, `editedAt`). That split is not new and is not
negotiable - `embedsJson` is an opaque string produced by a different serializer.

## `venta.invite`

```json
{
  "type": "venta.invite",
  "url": "https://app.venta.gg/invite/ABC23456",
  "title": "Sunday Raid Group",
  "description": "Casual mythic+ and too much chatting",
  "flags": 65536,
  "fields": [],
  "venta": {
    "kind": "invite",
    "resolved": true,
    "invite_code": "ABC23456",
    "guild_id": "gild_3H66JNBG6BTA8FINHJVTTE2H846",
    "channel_id": "chan_3H61jLFREDU2Gl6ummuoEj5ta0h",
    "expires_at": "2026-09-01T12:00:00+00:00",
    "max_uses": 25
  }
}
```

- `title` is the guild's name, `description` its description (often absent).
- `channel_id`, `expires_at` and `max_uses` are all optional and frequently absent.
- **`invite_code` is always the canonical generated code**, even when the link in the message was a
  vanity URL (`https://app.venta.gg/invite/sunday-raids`). `url` keeps the vanity form the sender
  actually pasted; `invite_code` is what you re-resolve against, and it survives a vanity rename.
- **There is no card at all for a code that does not exist, or for a revoked invite.** A typo'd or
  withdrawn invite renders as the plain text it is. Do not synthesise an "invalid invite" card from
  a URL you found yourself - finding URLs yourself is the thing this feature removes.

Suggested rendering: guild name, description, and a join affordance. The join action goes through
the existing redeem flow - `invite_code` is what you resolve first, via
`GET https://api.venta.gg/api/v1/guild/invites/code/{invite_code}` (the doubled-looking `guild`
segment is correct - the gateway strips its own prefix; see the invites guide).

## `venta.wiki_page` - a stub, by design

```json
{
  "type": "venta.wiki_page",
  "url": "https://app.venta.gg/wiki/gild_3H66JNBG6BTA8FINHJVTTE2H846/wkpg_7QZ1MMKTV9",
  "flags": 65536,
  "fields": [],
  "venta": {
    "kind": "wiki_page",
    "resolved": false,
    "guild_id": "gild_3H66JNBG6BTA8FINHJVTTE2H846",
    "page_id": "wkpg_7QZ1MMKTV9"
  }
}
```

> **Desktop: check the URL shape before you delete anything.** The server recognises
> `/wiki/{guildId}/{pageId}`. If the link `wiki-link.ts` currently builds has a different shape,
> say so rather than changing the client - the route table in
> `Messaging.Domain/Previews/InternalLinks.cs` is one line and the server should match the links
> already in people's message history, not the other way round.

**There is no `title` and there will never be one.** This is not an omission to work around.

A generated embed is stored once on the message and shown to everyone who can read the channel -
there is no per-viewer variant. Reading a wiki is gated on `ViewWiki` in the owning guild, per user
and per role, which is not implied by being able to read the message and not even implied by the
message being in the same guild. A server-resolved title would therefore leak a private page's name
to whoever the link was forwarded to, permanently, in a row nobody can revoke.

So the server says only *that* the link is a wiki page and *which* one. You fill the name in per
viewer:

1. Render a neutral placeholder card immediately - a wiki glyph and the page's URL is enough.
2. Fetch `GET https://api.venta.gg/api/v1/guild/guilds/{guild_id}/wiki/pages/{page_id}` with the
   user's token. (`guild/guilds` is not a typo - the gateway strips its own `/api/v1/guild` prefix
   and the Guild service's own wiki routes start with `/api/v1/guilds`. Same doubling as
   `messaging/messaging`.)
3. `200` -> swap in `title` (and `icon`, `coverUrl` if you want them).
   `403` -> keep the placeholder; this viewer may not see the page.
   `404` -> keep the placeholder; the page was deleted, or never existed.

Never turn a `403` or a `404` into "this page is private" or "this page was deleted" copy. The stub
is deliberately silent about which, and so should the card be.

This is not more work than before - the desktop wiki card already fetched per message. What you no
longer do is *find* the link.

## `venta.voice_invite` - the only card that is not about a link

```json
{
  "type": "venta.voice_invite",
  "title": "General",
  "description": "You have been invited to join this voice channel.",
  "flags": 65536,
  "fields": [],
  "venta": {
    "kind": "voice_invite",
    "resolved": true,
    "ring_id": "vrng_3H66JNBG6BTA8FINHJVTTE2H846",
    "guild_id": "gild_3H66JNBG6BTA8FINHJVTTE2H846",
    "channel_id": "chan_3H61jLFREDU2Gl6ummuoEj5ta0h",
    "channel_name": "General",
    "inviter_id": "user_3H61jLFREDU2Gl6ummuoEj5ta0h",
    "expires_at": "2026-08-15T12:01:00Z"
  }
}
```

Three things about it are different from the two above, and all three follow from it not standing
for a URL somebody pasted.

**There is no `url`.** There is no link shape for a channel anywhere in this product. Do not
synthesise one, and do not fall back to the link layout when `url` is missing.

**It arrives on a message you did not send and may be in a conversation you have never opened.** It
is written by the server into the 1:1 conversation between the two people when somebody sitting in
a voice channel rings you into it - `POST .../voice/rings`, see the voice-ring guide. If the two of
you already have a DM it goes in the most recently used one; if you have none, the server starts
one, so a `conversation.MessageCreated` may be the first thing you hear about that conversation
existing. Re-read the conversation list on one for a conversation id you do not know.

**`channel_name` is carried, unlike every other `venta.*` card.** The invite kind can omit names
because you re-resolve them from the code; the wiki kind must omit them because the audience for
the title is narrower than the audience for the message. Neither applies here - the recipient was
checked for `ViewChannel` before the ring was allowed at all, and there is no lookup that would let
them fill it in later. Render `channel_name`; do not fetch the channel to "get a fresher" one.

### `ring_id` and `expires_at` - three states, not two

An invitation can be sent with or without a ring (`delivery` on the send; see the voice-ring guide).
That gives the card three states, and the pair of fields is how you tell them apart:

| `ring_id` | `expires_at` | State | What to offer |
|---|---|---|---|
| set | in the future | a live ring | **Accept** - see below |
| set | in the past | a ring that lapsed | the ordinary join against `channel_id` |
| absent | **absent** | a standing invitation | the ordinary join against `channel_id` |

**A missing `expires_at` does not mean expired.** It means there never was a ring: the invitation was
sent quietly, it was valid the second it arrived, and it stays valid. Rendering "this invitation has
expired" there is wrong about a card that is still good. The lapsed state is one that *had* an
expiry and is past it.

- **Live**: accept through
  `POST https://api.venta.gg/api/v1/guild/guilds/{guild_id}/channels/{channel_id}/voice/rings/{ring_id}/accept`,
  the same path the realtime `guild.VoiceRingIncoming` card uses. In practice you will rarely be
  here - a client open enough to render the message already got the realtime event.
- **Lapsed or standing**: treat `ring_id` as absent and never call accept with it. Offer the
  ordinary "join this voice channel" action against `channel_id`, which is subject to the normal
  permission check and is an acceptance of nothing. For the lapsed one, say so; for the standing
  one, do not.

Nothing rewrites this message when a ring is accepted, declined or lapses. Compare `expires_at` to
your own clock at render time; an absolute instant stays right forever, which a stored "expired"
flag would not.

### Both people see this card

The message is authored by the inviter and lands in the conversation the two of them share, so the
sender reads the same row as the recipient. Offering the sender a Join button is nonsense - they are
already in the channel, which is the only way they were allowed to ring at all - so compare
`inviter_id` against the signed-in user and drop the affordance when they match.

Note also that the sender **does** receive `conversation.MessageCreated` for this message, unlike
every other message they author. They did not send it; the server did, on their behalf.

## Trust

| Field | Who wrote it |
|---|---|
| `type`, `venta.*`, `flags` | The server, when `flags & 65536` is set. Otherwise: whoever authored the message. |
| `title`, `description` on `venta.invite` | **A guild owner.** Server-*relayed*, not server-authored. |
| `title`, `description`, `provider.name`, `author.name`, `footer.text` on any other generated embed | A third-party web page. |
| Everything, on an embed without the flag | The message's author, which may be a bot. |

Two rules follow.

**Render a `venta.*` card only when `flags & 65536` is set.** Without the flag the embed was written
by whoever posted the message. A bot can author an embed with any `venta` block it likes. It buys an
attacker nothing - every action you take runs through an authenticated, permission-checked endpoint,
so a fabricated `page_id` fails the same way a real one the viewer cannot see does - but a card that
looks server-vouched when it is not is a phishing surface, and the flag is a one-line check.

**Treat `title` and `description` as hostile text even on an invite card.** They are typed by a
guild's owner. Render as text: never `innerHTML`, never a markdown pass that can resolve to an image
or a link, never interpolated into an attribute. The server clamps them to 256 / 4096 characters and
counts them against the 6000-character per-message budget, but it does not sanitise prose.

## The refresh contract

A generated embed is **frozen at post time**. It is written once when the message is posted and
never revisited. An invite card generated today does not know tomorrow that the invite was revoked.
That is the same reason Discord re-resolves invite embeds client-side, and the `venta` block exists
so you can do it too.

| Data | State | What you do |
|---|---|---|
| `type`, layout, `title`, `description` | Frozen, **authoritative** | Render it. Do not re-derive it from the URL, and do not overwrite it with something you fetched. |
| `expires_at`, `max_uses` | Frozen, **still correct** | Compare `expires_at` to the clock at render time. An absolute instant does not go stale the way "expired" does. |
| Invite revoked / exhausted, current use count | **Not carried** | Re-resolve if you want to show it. |
| Wiki page title | **Not carried** | Re-resolve per viewer, as above. |

Rules for re-resolving:

- **The `venta` block never contains a URL to call, and never will.** It carries identifiers. Build
  the request from your own route table. A server-supplied endpoint that a client hits with the
  user's credentials would hand any bot author a way to collect them, which is why the obvious
  `refresh` field is deliberately absent.
- **Re-resolve lazily, not on render.** Once per visible card, cached by id for the session, on
  viewport entry. The per-message-on-render fetch the current cards do is exactly what this feature
  is removing; reimplementing it against the new ids would be a net loss.
- **The server embed stays the authority for layout and content.** Refreshing fills in the volatile
  bits (is this invite still good, what is this page called). It does not replace the card.
- If a refresh fails for any reason, keep the frozen card. Never blank it out.

## `MessageType.Invite` is vestigial - stop branching on it

`MessageType.Invite` exists in the enum and is plumbed end to end, and **nothing has ever produced
it**. Mobile currently renders its invite card only when `message.type == MessageType.Invite`, which
means mobile's invite card has never once been shown.

It is staying in the enum (the value is a persisted ordinal; removing it would renumber every other
message type on every stored row) and it is staying unproduced. Making it meaningful would be wrong
anyway: a message containing an invite is an ordinary message with prose around the link, often with
other links in it too, and typing it `Invite` would suppress every other preview on it.

**The card belongs to the link, not to the message.** Branch on the embed.

## The `<…>` opt-out

`<https://app.venta.gg/invite/ABC23456>` produces no card, exactly as it does for an external link.
That is decided before any of this runs, so there is nothing for a client to do about it - but it is
also why the wiki share link no longer needs to bracket itself. See the deletion list.

## Summary of client work

**Delete - desktop (Alpine):**

1. `INVITE_URL_RE` in `message.component.ts`, and the body-splitting that produces segments from it.
2. `features/messaging/wiki-link.ts` in its entirety - both the URL regex and the `<…>` wrapping at
   line 46. A wiki share link should now be pasted bare, so that it gets a card.
3. `app-invite-card`'s and `app-wiki-card`'s **URL parsing and their per-message fetches**. Keep the
   components as presentational cards driven by an embed; they lose their inputs-from-a-regex and
   their `ngOnInit` fetch.
4. Any code path that renders an invite or wiki card from message *content* rather than from
   `embedsJson`.

**Delete - mobile (Flutter):**

1. `_inviteUrlRe` in `thread_view.dart`.
2. The `message.type == MessageType.Invite` branch that gated the invite card. It has never been
   true.

**Build - both:**

1. Parse `embedsJson` (already done for bot embeds and link previews).
2. Add three arms to the embed renderer, switched on `type`, gated on `flags & 65536`:
   `venta.invite`, `venta.wiki_page` and `venta.voice_invite`.
3. For `venta.wiki_page`, a lazy per-viewer title fetch with the 403/404 handling above.
4. For `venta.invite`, optionally a lazy validity refresh; `expires_at` alone covers the common case
   with no request at all.
5. For `venta.voice_invite`, no fetch at all: everything is in the card. Switch on the three states
   in the table above - live, lapsed, standing - hide the affordance from the sender, and handle a
   `conversation.MessageCreated` arriving for a conversation id you have never seen.
6. Ignore an unknown `venta.*` type rather than falling back to the link layout - a future kind will
   arrive before your next release, and a half-rendered card is worse than no card.
