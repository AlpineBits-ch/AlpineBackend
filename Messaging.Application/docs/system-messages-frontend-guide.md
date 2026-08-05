# Guild system messages (join messages) - frontend integration guide

Backend support for Discord-style system messages is done and live - starting with "User joined
the server" messages posted into a guild's system channel. This is what the client needs to build
to actually render them.

## What's new

Every message (REST responses and realtime events alike) now carries two fields that previously
didn't exist:

```json
{
  "id": "mesg_3H66JNBG6BTA8FINHJVTTE2H846",
  "channelId": "chan_3H61jLFREDU2Gl6ummuoEj5ta0h",
  "authorId": "user_3H61jLFREDU2Gl6ummuoEj5ta0h",
  "content": "cGxhY2Vob2xkZXIgLSBzZWUgYmVsb3c=",
  "type": "GuildMemberJoin",
  "systemMessageVariant": 4,
  "...": "..."
}
```

- **`type`** - string enum, one of `Message`, `Invite`, `GuildMemberJoin`, `GuildMemberLeave`.
  Ordinary chat messages are `Message` (the default - old messages and every message a human/bot
  sends still come back this way, nothing to change for those).
- **`systemMessageVariant`** - integer `0`-`9`, only set when `type` is not `Message`. The backend
  picks this randomly per-message. It's an index into a fixed set of ~10 copy variants **you own**
  - the backend never sends copy text for system messages, only the type + variant index + the
  joining user's id. This is the same convention Discord's own client uses for its system messages
  (`"X joined the party"`, `"X just showed up"`, etc. picked at random).
- **`content`** - for `GuildMemberJoin` this is still populated, but only as a plain-English
  fallback (`"alice joined the server"`, base64-encoded like every other message's `content`) for
  anything that doesn't understand `type` yet (push notification previews, bots, search indexing).
  **Prefer the templated rendering below over `content` once you support it** - same rule as
  `embedsJson` fallback behavior.

## Where it shows up

Same places `content`/`embedsJson` already do:

- `guild.MessageCreated` (SignalR realtime event - this is how you'll actually see join messages
  arrive live)
- `GET /api/v1/messaging/channels/{channelId}/messages` (channel history)

Join messages are **channel messages only** - they're never posted into DMs/conversations, so
`conversation.MessageCreated` and the `/conversations/{id}/messages` endpoint are unaffected.

Two separate realtime events fire when someone joins a guild - don't confuse them:

- `guild.MemberJoined` - `{ guildId, userId }`. Presence/roster update, fires immediately, always.
- `guild.MessageCreated` - the actual system message described above. **Only fires if the guild
  has a system channel configured** (see below) - if not, nothing gets posted anywhere.

## Rendering variants

You need ~10 copy templates per system `type`, each taking the joining user as a parameter. Pick
`variants[systemMessageVariant]`, substitute the user (render as a mention using `authorId`, same
as you already do for `@mentions` in regular message content), run the result through your normal
i18n pipeline. Example variant set (adapt freely - wording is entirely client-owned):

```ts
const GUILD_MEMBER_JOIN_VARIANTS = [
  (user) => `${user} joined the server`,
  (user) => `${user} just showed up`,
  (user) => `Welcome, ${user}. Say hi!`,
  (user) => `${user} joined. Everyone, look busy!`,
  (user) => `${user} slid into the server`,
  (user) => `${user} arrived`,
  (user) => `Glad you're here, ${user}`,
  (user) => `A wild ${user} appeared`,
  (user) => `${user} hopped into the server`,
  (user) => `Everyone welcome ${user}!`,
];
```

`type: "GuildMemberLeave"` and its variant set follow the identical contract for when a member
leaves - the enum value and `systemMessageVariant` field are already there and forward-compatible,
but **no backend event produces a `GuildMemberLeave` message yet** (leaving/kicks/bans don't post
one today). Build the renderer generically against `type` so it's a no-op addition later, but
don't expect to see one in production yet.

System messages typically render without an avatar/inline (a centered gray line, like Discord),
rather than as a normal chat bubble - but that's a client styling choice, not a backend constraint.

## System channel configuration

Every guild has a `systemChannelId` field (nullable) - the channel join messages get posted into.

```json
// GET /api/v1/guilds/{id} (and everywhere else GuildDto shows up)
{
  "id": "gild_...",
  "name": "My Guild",
  "systemChannelId": "chan_3H61jLFREDU2Gl6ummuoEj5ta0h",
  "...": "..."
}
```

- `systemChannelId` is `null` if no system channel is set - in that case joins never produce a
  message (only the `guild.MemberJoined` presence event fires).
- New guilds get one assigned automatically at creation (the first text channel).
- It's changeable - requires `ManageGuild` permission:

```
PATCH /api/v1/guilds/{id}
{
  "name": "My Guild",
  "description": "...",
  "systemChannelId": "chan_..."   // omit/null to leave it unchanged; must be a Text or
                                    // Announcement channel belonging to this guild, or the
                                    // request 400s
}
```

Suggested UI: a channel picker under guild Overview settings, filtered to Text/Announcement
channels, exactly like Discord's "System Messages Channel" setting - including a "no system
channel" option (send `systemChannelId: null`... note: **omitting the field, not sending
`null` explicitly**, is what's required to leave it unchanged today - explicitly clearing an
already-set system channel isn't wired up on the backend yet. If you need a "None" option, ask
backend to add it before shipping that part of the picker).
