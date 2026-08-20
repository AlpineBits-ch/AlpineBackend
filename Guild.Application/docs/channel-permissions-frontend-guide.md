# Channel permission tracing and sync - frontend integration guide

Two routes for the permissions editor: one answers "what does this role or member actually get in
this channel, and why", the other copies a category's overwrites onto one of its channels. Product
spec lives in the client repo at `docs/specs/channel-permissions-ux.md`.

## Base URL

Through the gateway, same as everything else:

```
https://api.venta.gg/api/v1/guild/...
```

The gateway rewrites `/api/v1/guild/{rest}` to `/api/v1/{rest}`, so the service route
`/api/v1/channels/{channelId}/...` is reached as `/api/v1/guild/channels/{channelId}/...`. One
`guild` segment, then the resource.

---

## 1. Effective permissions with provenance

```http
GET /api/v1/guild/channels/{channelId}/effective-permissions?roleId={roleId}
GET /api/v1/guild/channels/{channelId}/effective-permissions?memberId={memberId}
```

Exactly one of `roleId` or `memberId`. Both or neither is a `400` with a plain-string body
(`"Pass exactly one of roleId or memberId."`), not a JSON error object.

Needs `ManagePermissions` on the guild, the same audience that may write an overwrite. No MFA gate:
this is a read.

```json
{
  "channelId": "chan_1",
  "subjectKind": "Role",
  "subjectId": "role_1",
  "permissions": "ViewChannel, ReadMessageHistory",
  "modulePermissions": "None",
  "sources": [
    {"permission": "ViewChannel", "granted": true, "decidedBy": "CategoryRoleAllow"},
    {"permission": "SendMessages", "granted": false, "decidedBy": "ChannelEveryoneDeny"},
    {"permission": "AttachFiles", "granted": false, "decidedBy": "Implied"}
  ]
}
```

`permissions` and `modulePermissions` serialize as `[Flags]` name lists, same as everywhere else,
so parse them with the existing flag codec. A mask setting bit 63 cannot be a JSON number.

**The two spaces never mix.** `permissions` and `modulePermissions` overlap numerically bit for
bit; never OR them together or compare one against the other.

### `sources`

One entry per permission a channel or category overwrite can carry, always present whether granted
or not, in this fixed order:

```
ViewChannel, CreateInvite, UseApplicationCommands, SendMessages, ReadMessageHistory,
EditOwnMessages, EditAnyMessage, DeleteOwnMessages, DeleteAnyMessage, PinMessages, MentionEveryone,
AttachFiles, EmbedLinks, AddReactions, UseExternalEmojis, Connect, Speak, Stream, MuteMembers,
DeafenMembers, MoveMembers, CreateThreads, SendMessagesInThreads, ManageOwnThreads,
ManageAnyThread, ManageChannel, ManagePermissions, ManageWebhooks
```

Guild-only permissions (`KickMembers`, `ManageRoles`, and so on) never appear here; a channel
overwrite cannot express them.

`decidedBy` is the layer that last wrote that bit, one of:

`Base`, `MemberGuildAllow`, `MemberGuildDeny`, `CategoryEveryoneAllow`, `CategoryEveryoneDeny`,
`CategoryRoleAllow`, `CategoryRoleDeny`, `CategoryMemberAllow`, `CategoryMemberDeny`,
`ChannelEveryoneAllow`, `ChannelEveryoneDeny`, `ChannelRoleAllow`, `ChannelRoleDeny`,
`ChannelMemberAllow`, `ChannelMemberDeny`, `Implied`, `Superadmin`, `Muted`, `ModuleDisabled`,
`SceneRestricted`.

`Implied` means the bit was taken by the reverse closure of some other deny, not named by any
overwrite directly. `Base` means the role union decided it and nothing overwrote it.

### The two gates outside the overwrite pipeline

Enforcement applies two checks that no overwrite can express, and the readout applies both so it
cannot disagree with what the server will actually do.

`ModuleDisabled` means the permission belongs to a module the guild has switched off, or that its
plan does not cover. `Connect`, `Speak`, `Stream`, `MuteMembers`, `DeafenMembers` and `MoveMembers`
need `VoiceChannels`; `CreateThreads`, `CreatePrivateThreads`, `SendMessagesInThreads`,
`ManageOwnThreads` and `ManageAnyThread` need `Threads`. Nobody escalates past this, the guild owner
included, and `modulePermissions` is clamped the same way. Present it as unavailable rather than as
denied: turning the module back on restores it without touching a single overwrite.

`SceneRestricted` means the channel is a cast-only scene the member has nobody in. Every permission
comes back denied, because that is what every enforcement path answers. It applies to a `memberId`
subject only: cast membership is a property of a person, so a `roleId` subject is left unclamped and
its answer can be wider than what a given holder of that role gets in that one scene.

### What a role subject answers

A role subject has no member row. `MemberGuildAllow`, `MemberGuildDeny` and `Muted` can never
appear for one, and the base is that role unioned with @everyone. This deliberately answers "what
would a member holding only this role get here", not any real member's actual result - use
`memberId` for that.

### The guild owner

If `memberId` resolves to the guild owner, every permission in the list comes back granted with
`decidedBy: "Superadmin"` and `modulePermissions` is every module bit, regardless of roles or
overwrites. There is no owner-specific escape from this shape; it is the same short-circuit the
resolver uses everywhere else. The one thing the owner does not escape is `ModuleDisabled`, which is
a product state rather than an authorization level.

### Muted or pending members

A member subject who is timed out, or has not accepted onboarding, is cut back to a fixed retained
set after every other layer resolves; the bits that removal took are reported as `Muted`. A role
subject is never muted - mute is a member-row state.

### Threads

A thread carries no overwrites of its own. The endpoint traces the parent channel and returns that
trace, but `channelId` in the response is still the thread's id, not the parent's.

Posting in a thread is governed by `SendMessagesInThreads`, not `SendMessages`, so read the former
when the channel is thread-shaped. `SendMessages` is still reported, as the parent's answer for it.

### Status codes

| Status | Meaning |
|---|---|
| `200` | Resolved |
| `400` | Neither or both of `roleId` / `memberId` given |
| `401` | Not authenticated |
| `403` | Missing `ManagePermissions` |
| `404` | No such channel, or the role/member is not in that channel's guild |

---

## 2. Sync a channel's permissions with its category

```http
POST /api/v1/guild/channels/{channelId}/permissions/sync
```

No body. Returns `ChannelPermissionDto[]`, the channel's overwrites after syncing - possibly empty,
if the category itself carries none.

Needs `ManagePermissions` plus the MFA elevation check, same gate as writing an overwrite directly.
The MFA check runs after the permission check, so a caller who lacks `ManagePermissions` gets a
plain `403` and learns nothing about the guild's MFA posture.

Every mask about to be copied is checked against what the caller could grant directly
(`CanGrantPermissionsAsync`, both spaces, both directions). If any row would exceed that, the whole
call is rejected with a bare `403` and nothing is written - copying a category row is not a way
round the clamp a direct overwrite write already has.

Every target on either side of the swap also has to be one the caller outranks, the same hierarchy
check that writing or deleting that overwrite directly would run. Clearing a row is a grant to
whoever it denied, so this covers the rows being deleted as well as the rows being created: a
moderator cannot sync away their own channel mute, or a deny sitting on a role above them. A target
the caller does not outrank is a bare `403` with nothing written.

Once both checks pass: every existing overwrite on the channel is deleted, and a copy of every
category-level overwrite is inserted in its place (same `roleId`/`memberId`, same four masks).
`Channel.IsPrivate` is then re-derived from the @everyone row the sync just produced. One
`ChannelPermissionChanged` invalidation is published per distinct target the swap moved.

The audit entry is written whether or not the sync changed anything. A no-op sync, where the channel
already held exactly the category's set, still logs. This is the opposite of the member permission
overrides endpoint, which skips its entry on a no-op.

### Status codes

| Status | Meaning |
|---|---|
| `200` | Synced. Body is the channel's new overwrite rows |
| `401` | Not authenticated |
| `403` | Missing `ManagePermissions`, MFA required (`{ "error": "mfaRequired", ... }` body), a mask being copied exceeds what the caller can grant, or a target on either side of the swap outranks the caller |
| `404` | No such channel, or the channel has no category |

### There is no stored "synced" flag

Sync is a one-shot copy, not a relationship the server remembers. Derive sync state client-side by
comparing the channel's overwrite set to the category's, keyed on `roleId ?? memberId`, comparing
all four masks (`allowPermissions`, `denyPermissions`, `allowModulePermissions`,
`denyModulePermissions`) for every target present in either set.
