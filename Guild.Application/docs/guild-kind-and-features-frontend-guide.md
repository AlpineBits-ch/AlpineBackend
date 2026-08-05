# Guild kind & features - frontend integration guide

Audience: web/desktop/mobile client engineers.

A guild is no longer one shape. It now declares **what it is** (`kind`) and **which modules it
has** (`features`), so a League community never sees a chore rota and a four-person flatshare never
sees a ban list, a verification level, or a rules-acceptance screen.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call a
microservice directly. Guild endpoints are reached under the `/api/v1/guild/` prefix; the gateway
strips the `guild` segment before forwarding, which is why the paths read
`/api/v1/guild/guilds/{guildId}`. That doubled-looking segment is correct.

**Status:** the `kind` + `features` layer described here is live. The household modules
(`Lists`, `Chores`, `Ledger`, `Pantry`, `Decisions`, `Presence`, `QuietHours`, `GuestAccess`) are
**flag values only** - no endpoints exist behind them yet. See §8. Nothing you already shipped
breaks; see §9.

---

## 1. The model in one screen

| | `kind` | `features` |
|---|---|---|
| What it is | One value. What the guild *is*. | A set. What the guild *has*. |
| Who reads it | Your client, for the shell | The server, for every permission check |
| Changes behaviour? | No - presentation only | **Yes** - gates permissions and channel types |
| Set at | Creation (and editable after) | Seeded from `kind`, editable independently |

`kind` is one of `Community` (default), `Household`, `Team`, `Study`, `Event`. Use it to pick
nomenclature ("House" vs "Server" vs "Team"), which settings pages you render, and whether you
offer discovery. **Do not gate features on it** - read `features` for that.

The split exists because the two aren't the same question. A gaming community might genuinely want
the shared ledger for a LAN party without becoming a household; a household might want a forum
without becoming a community. Picking a kind is a one-tap answer to "what am I making"; the
feature set is what actually gets enforced, and an owner can adjust it afterwards.

---

## 2. The features

Community-scale modules:

| Flag | Gates |
|---|---|
| `VoiceChannels` | Voice channels; `Connect`, `Speak`, `Stream`, `MuteMembers`, `DeafenMembers`, `MoveMembers` |
| `Threads` | Thread channels; `CreateThreads`, `SendMessagesInThreads`, `ManageOwnThreads`, `ManageAnyThread` |
| `Forums` | Forum **and Media** channels |
| `Announcements` | Announcement channels |
| `Tickets` | Ticket channels |
| `Moderation` | `KickMembers`, `BanMembers`, `ModerateMembers`, `ViewAuditLog` |
| `AutoMod` | The automod config surface |
| `Onboarding` | Rules gate, prompts, welcome screen |
| `Emojis` | `ManageEmojis` |
| `Bots` | Bot installs |
| `Wiki` | Every `*Wiki*` permission |
| `Events` | `ManageEvents` |

Household modules - **flags only today, nothing implemented behind them** (§8):

`Lists` · `Chores` · `Ledger` · `Pantry` · `Decisions` · `Presence` · `QuietHours` · `GuestAccess`

Text channels, invites, and the core message/attachment/reaction permissions have no flag. They're
the platform, not a module - a guild without them would be an empty room.

---

## 3. Wire format - read this before you write a bitmask

`features` is a **comma-separated string of flag names**, not a number:

```jsonc
{
  "id": "gild_...",
  "name": "The Flat",
  "kind": "Household",
  "features": "VoiceChannels, Threads, Wiki, Events, Lists, Chores, Ledger, Pantry, Decisions, Presence, QuietHours, GuestAccess"
}
```

A guild with no modules serializes as `"None"`.

Treat it as a set of strings. Split on `", "` once and keep a `Set<string>`:

```ts
type GuildFeature =
  | 'VoiceChannels' | 'Threads' | 'Forums' | 'Announcements' | 'Tickets'
  | 'Moderation' | 'AutoMod' | 'Onboarding' | 'Emojis' | 'Bots' | 'Wiki' | 'Events'
  | 'Lists' | 'Chores' | 'Ledger' | 'Pantry' | 'Decisions'
  | 'Presence' | 'QuietHours' | 'GuestAccess';

const parseFeatures = (s: string): Set<GuildFeature> =>
  s === 'None' ? new Set() : new Set(s.split(',').map(f => f.trim() as GuildFeature));

const has = (g: Guild, f: GuildFeature) => parseFeatures(g.features).has(f);
```

On **write** the server accepts either form - the name string (`"Lists, Wiki"`) or the raw numeric
mask. Send the string; it survives a flag being renumbered and is readable in a network log.

This matches how `permissions` already behaves on roles, so if you have a helper for that, reuse
the same shape.

---

## 4. Creating a guild

```
POST https://api.venta.gg/api/v1/guild/guilds
```

```jsonc
{
  "name": "The Flat",
  "description": "…",
  "kind": "Household"      // optional - omit for Community
}
```

`kind` seeds `features` from that kind's preset. You cannot set `features` directly at creation;
create, then `PATCH` if the owner wants something non-standard. The response is the usual guild
object, now carrying `kind` and `features`.

Omitting `kind` gives you `Community` and exactly the behaviour you have today.

---

## 5. Changing kind & features

```
PATCH https://api.venta.gg/api/v1/guild/guilds/{id}
```

Requires `ManageGuild`. Both fields are optional and independent:

```jsonc
{ "kind": "Household" }                                  // re-seeds features from the preset
{ "features": "VoiceChannels, Threads, Wiki, Lists" }    // exact set, kind untouched
{ "kind": "Household", "features": "Wiki, Lists" }       // both - the explicit set wins
```

The rule: **sending `kind` alone re-seeds `features` from that kind's preset.** That makes "turn
this server into a house" one call. If the owner has customised their module set and you only mean
to relabel, send both fields, or you'll silently reset their choices. Worth a confirmation dialog
on the kind switch in your settings UI.

Effects are immediate - the server drops its cached feature mask on write. Re-fetch the guild and
its channel list afterwards.

---

## 6. What your client has to do

**Hide, don't disable.** A module that's off should be absent from navigation, not greyed out.
That's the whole point - a household should never see a "Bans" tab it can't press.

Gate on `features` at these points:

| Surface | Check |
|---|---|
| Channel-type picker in "create channel" | `VoiceChannels`, `Forums`, `Announcements`, `Tickets` |
| Thread affordances (start thread, thread list) | `Threads` |
| Ban list, kick/timeout actions, audit log | `Moderation` |
| AutoMod settings page | `AutoMod` |
| Onboarding / rules / welcome-screen settings | `Onboarding` |
| Emoji manager | `Emojis` |
| Bot install & management | `Bots` |
| Wiki nav entry and everything under it | `Wiki` |
| Scheduled-events calendar | `Events` |

Use `kind` for nomenclature and shell only - labels, empty states, whether you surface discovery
or a public-server browser.

### Error shapes

| What you did | Response |
|---|---|
| Called an endpoint whose permission belongs to a disabled module | `403` |
| Created a channel whose type belongs to a disabled module | `400`, plain text: `Channel type 'Forum' is not enabled for this guild.` |

Both are avoidable by reading `features` first. Handle them anyway - a second admin can disable a
module while your tab is open.

---

## 7. Four rules that will bite you otherwise

**1. The owner is not exempt.** Everywhere else in this API the guild owner short-circuits to
"yes". Not here. A disabled module is off for the owner, for admins, for everyone - it's a product
state, not a permission level. Don't render an admin-only escape hatch; there isn't one.

**2. `ManageGuild` is never gated.** It's how a feature gets switched back on, so gating it would
be a one-way door. An owner can always reach guild settings, whatever else is off.

**3. Disabling a module never deletes data.** Switching `Wiki` off hides the wiki and strips its
permissions; the pages are still there and come back intact when it's re-enabled. Say so in your
confirmation copy - "hidden, not deleted" - or nobody will dare touch the toggles.

**4. Existing channels of a disabled type are not deleted either.** Turning `Forums` off blocks
*creating* forum channels; it does not remove the ones already there. Decide deliberately whether
your sidebar hides them or shows them as inert, and be consistent.

---

## 8. Household modules: flags without endpoints

`Lists`, `Chores`, `Ledger`, `Pantry`, `Decisions`, `Presence`, `QuietHours` and `GuestAccess`
exist as flag values and are set by the `Household` preset, but **no endpoints, channel types or
permissions are behind them yet**. A guild created with `kind: "Household"` today gets text and
voice channels, threads, a wiki, and scheduled events - plus a set of flags nothing reads.

Don't build UI against them yet. They're in the enum now so the preset is stable and so that
enabling a module later is a data change rather than a migration. When the first one ships it'll
get its own guide.

---

## 9. Compatibility & other surfaces

**Existing guilds.** Every guild that existed before this landed is `Community` with the full
community preset - byte-for-byte the behaviour it already had. Nothing you shipped against the
old API changes.

**Old clients.** `kind` and `features` are additive fields on the guild object. A client that
ignores them behaves exactly as before, because `Community` gates nothing.

**Templates.** A server template now captures the source guild's `kind` and `features`, so
applying it reproduces the module set as well as the channel/role tree. Templates saved before
this change apply as `Community`.

**Discord import.** Imported servers are always `Community` - an imported tree assumes forums,
announcements, automod and bots are present.

**Bots.** The internal guild snapshot now carries `kind` and `features`, but the gateway's
`GUILD_CREATE` payload does **not** forward them yet - Discord has no equivalent field and it
would be a non-standard extension. Bots can't read a guild's module set today.

**Federation.** Nothing to do: no code path materializes a remote guild as a shadow row today,
only remote *members*. Whenever that lands it will have to carry these two fields.
