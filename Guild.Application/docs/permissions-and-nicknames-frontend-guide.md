# New permission bits & nicknames - frontend integration guide

Five new permission bits and the first-ever nickname endpoints. Backend work is done - this is
what the client needs to build against it.

## Base URL

Everything below goes through the gateway, same as every other authenticated call:

```
https://api.venta.gg/api/v1/guild/...
```

Normal `Authorization: Bearer <token>`.

---

## 1. Five new permission bits

`Permissions` is a 64-bit flag field. These are new:

| Bit | Name | What it gates |
|---|---|---|
| 50 | `MentionEveryone` | Using `@everyone` / `@here` |
| 51 | `ManageRoles` | Create/edit/delete/reorder roles |
| 52 | `ManageWebhooks` | Create/edit/delete webhooks and see their tokens |
| 53 | `ChangeNickname` | Change **your own** nickname |
| 54 | `ManageNicknames` | Change **other people's** nicknames |

As decimal masks, if your client works in `BigInt`:

```js
MentionEveryone  = 1n << 50n  // 1125899906842624
ManageRoles      = 1n << 51n  // 2251799813685248
ManageWebhooks   = 1n << 52n  // 4503599627370496
ChangeNickname   = 1n << 53n  // 9007199254740992
ManageNicknames  = 1n << 54n  // 18014398509481984
```

> The permissions field crosses the wire as a **decimal string**, not a JSON number - 2^53 is past
> `Number.MAX_SAFE_INTEGER` and bits 53/54 land exactly there. Parse with `BigInt`, never `Number`.
> This already mattered before these bits; it is now unavoidable.

### Two role-editor changes

**`ManageRoles` split out of `ManagePermissions`.** The role editor's "Manage Roles" toggle must
now write bit 51. `ManagePermissions` (bit 21) still exists and still means something - it now
means *only* "may edit per-channel and per-category permission overwrites". If your settings UI
showed one checkbox for both, it needs to become two.

**`ManageWebhooks` split out of `ManageChannel`.** Same shape: "Manage Webhooks" is its own toggle
now. A role with Manage Channel no longer implies webhook access.

Existing roles were backfilled on deploy - anyone who had `ManagePermissions` now also has
`ManageRoles`, anyone who had `ManageChannel` now also has `ManageWebhooks` - so no server loses
capability. But a role edited by an old client would silently drop the new bits, so ship the
editor change before or with this.

### `@everyone` behaves differently from Discord - deliberately

Two things to know:

**It is not granted to `@everyone` by default.** On Discord, the default `@everyone` role can ping
`@everyone`. Here it cannot: the bit is granted only to roles that already had Manage Channel or
Manage Guild. This is a deliberate divergence - "every member may ping every member" is the abuse
vector the bit exists to close. If a server wants Discord's behaviour they can grant it explicitly.

**A denied ping is silently downgraded, not rejected.** If a user without `MentionEveryone` sends
a message with `mentionsEveryone: true`:

- the message **still posts**, with `201 Created` as normal
- the returned message has `mentionsEveryone: false` and `mentionsHere: false`
- nobody is notified

There is no error to display. This matters for the composer: **render the ping preview from the
response, not from what you sent.** If your UI shows "this will notify 4,213 people" based on
local state, it will lie to users who lack the permission. Either check the permission before
showing that warning, or reconcile against the created message.

DMs and group conversations are unaffected - there is no `MentionEveryone` concept there and the
flags always stand.

---

## 2. Nicknames

`GuildMember.nickname` was previously set once at join and could never change. It now has
endpoints.

### Change your own

```http
PATCH /api/v1/guild/api/v1/guilds/{guildId}/members/me/nickname
Content-Type: application/json

{ "nickname": "Newt" }
```

Requires `ChangeNickname`, which **is** in the default `@everyone` role, so in practice every
member has it unless a server removed it.

### Change someone else's

```http
PATCH /api/v1/guild/api/v1/guilds/{guildId}/members/{memberId}/nickname

{ "nickname": "Renamed" }
```

Requires `ManageNicknames` **and** that you outrank the target - the same role-hierarchy rule as
kick/ban/timeout. The guild owner can never be renamed by anyone else.

Passing your own `memberId` to this route only needs `ChangeNickname`, so a client that always
addresses members by id doesn't need to special-case self.

### Rules for both

- **1-32 characters** after trimming. Longer → `400`.
- `null`, `""` or whitespace **clears** the nickname; the member falls back to their account
  username. This is how "reset nickname" is expressed - there is no separate endpoint.
- Leading/trailing whitespace is trimmed server-side.
- A no-op change (same nickname) returns `204` and emits no events.

### Responses

| Status | Meaning |
|---|---|
| `200` | Changed. Body: `{ "userId": "...", "nickname": "Newt" }` (nickname `null` if cleared) |
| `204` | No change - the nickname was already that value |
| `400` | Over 32 characters |
| `403` | Missing permission, or the target outranks you |
| `404` | Not a member of that guild / no such member |

### Realtime

Everyone in the guild, plus the renamed member, receives:

```js
connection.on("guild.MemberUpdated", ({ guildId, userId, nickname }) => { ... })
```

Update your member cache from this rather than only from the PATCH response - someone else's
rename arrives this way too. Note `nickname` may be `null`, meaning "fall back to username".

### Member search now matches nicknames

`GET /api/v1/guild/api/v1/guilds/{id}/members/search?search=...` previously matched only the
account username. It now matches **either** the username or the current nickname. No API change -
your existing call just returns better results. If you were client-side filtering on nickname to
compensate, you can drop that.

---

## Summary of client work

1. Parse the permissions field with `BigInt` (if you weren't already).
2. Split the role editor's Manage Roles and Manage Permissions toggles; same for Manage Webhooks
   and Manage Channel.
3. Add the five new bits to the permission picker with sensible labels.
4. Don't promise an `@everyone` ping in the composer unless the user actually holds bit 50 -
   render from the response.
5. Build nickname editing: own (member context menu → "Change nickname") and moderator
   (member list → "Change nickname", gated on `ManageNicknames` + outranking).
6. Handle `guild.MemberUpdated` for nickname changes.
