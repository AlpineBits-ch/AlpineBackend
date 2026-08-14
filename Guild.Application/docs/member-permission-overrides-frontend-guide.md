# Member permission overrides - frontend integration guide

A member row carries four permission masks of its own, applied **after** their roles are unioned and
**before** any channel or category overwrite. They have existed as columns since the first migration
and been resolved by `GuildPermissionService` the whole time, but until now nothing but the
bot-install path could write them: there was no endpoint. A client editing a member's permissions
was calling a route that did not exist and getting a `404`.

## Base URL

Through the gateway, same as everything else:

```
https://api.venta.gg/api/v1/guild/...
```

The gateway rewrites `/api/v1/guild/{rest}` to `/api/v1/{rest}`, so the service route
`/api/v1/guilds/{guildId}/...` is reached as `/api/v1/guild/guilds/{guildId}/...`. One `guild`
segment, then the plural resource.

---

## The endpoint

```http
PATCH /api/v1/guild/guilds/{guildId}/members/{memberId}/permissions

{
  "allowPermissions": "ViewChannel, SendMessages"
}
```

`memberId` is the member id (`gmbr_...`), not the user id - the same id the member list returns.

### Body

Four masks, all optional. **Omitted means "leave alone".**

| Field | Space | Meaning |
|---|---|---|
| `allowPermissions` | `Permissions` | Granted to this member on top of their roles |
| `denyPermissions` | `Permissions` | Taken away regardless of what their roles grant |
| `allowModulePermissions` | `ModulePermissions` | Module-space grant |
| `denyModulePermissions` | `ModulePermissions` | Module-space revocation |

Omission is not the same as empty. To clear a mask send `"None"` (or `0`); leaving the field out
keeps whatever is stored.

This matters for any editor that shows fewer than four masks. A two-state permission grid can only
express `allowPermissions`, and under replace-semantics every save from that screen would silently
wipe a deny override and both module masks that the screen cannot see and the user never touched.
Send only what you actually edited.

Masks accept the usual two wire forms: a flag-name list (`"ViewChannel, SendMessages"`) or a number.

### Response

`200` with the four resulting masks - not a member row:

```json
{
  "allowPermissions": "ViewChannel, SendMessages",
  "denyPermissions": "None",
  "allowModulePermissions": "None",
  "denyModulePermissions": "None"
}
```

Merge those four fields into the member you already hold rather than replacing it; the response
deliberately carries no profile, roles or presence. A save that changes nothing returns the same
`200` and writes no audit entry and no events.

| Status | Meaning |
|---|---|
| `200` | Applied (or nothing changed) |
| `401` | Not authenticated |
| `403` | Missing `ManageRoles`, target outranks you, a mask exceeds your own permissions, or MFA is required |
| `404` | No such member in that guild |

### Permissions

- **`ManageRoles`**, not `ManagePermissions`. The latter means only per-channel and per-category
  overwrites; what this writes is guild-wide, so it is the same power as handing somebody a role
  and takes the same permission.
- You must **outrank the target** - the same role-hierarchy rule as kick/ban/timeout, and nobody
  can rewrite the owner's.
- Every mask you send is **clamped to what you hold yourself**, in both directions and both spaces.
  A deny is as much an exercise of a permission as a grant, and a guild-wide deny is worse than a
  per-channel one. Bits belonging to a module the guild has switched off are grantable by nobody.
- MFA-gated guilds return the usual `{ "error": "mfaRequired" }` `403`.

### Realtime

Everyone in the guild, plus the target, receives the existing member event:

```js
connection.on("guild.MemberUpdated", ({ guildId, userId, nickname }) => { ... })
```

Not a new event: this is what clients already re-read `/guilds/{id}/me` on, which is exactly what a
member whose permissions just changed needs to do. The payload carries no masks - re-read the member
rather than patching from the event. The server invalidates the target's cached permission set
before broadcasting, so the re-read sees the new answer immediately rather than up to fifteen
minutes later.

---

## Reading the current values

`GET /guilds/{id}/members` returns `allowPermissions`, `denyPermissions`,
`allowModulePermissions` and `denyModulePermissions` on every row. Prefill the editor from
`allowPermissions`.

There is **no** single `permissions` field on a member row. A client reading one gets `undefined`,
which parses as "no bits" - so an editor prefilled from it opens with everything unticked no matter
what the member holds, and the save then takes away whatever was there. For the member's *effective*
permissions, use `effectivePermissions` from `GET /guilds/{id}/me` (it already folds in ownership,
roles, these overrides and the module clamp); nothing else resolves the tiers for you.
