# Guild role system, Discord parity release - frontend integration guide

This release reworks the guild role system end to end: the permission mask was split in two, twelve
Discord permissions were added, per-channel denies actually work now, and roles gained the display
metadata Discord has always had. See `docs/specs/guild-role-system-parity.md` for the review this
came from.

Read section 1 first. It is the change most likely to break a client silently, without an error.

---

## 1. The permission mask is now two fields

### Wire format

Permissions serialize as a **single comma-separated string of member names**, not a number:

```json
{
  "id": "role_...",
  "permissions": "ViewChannel, SendMessages, AddReactions, ReadMessageHistory",
  "modulePermissions": "ViewWiki, AddListItems, CompleteChores"
}
```

This is what a `[Flags]` enum does under the service's globally registered
`JsonStringEnumConverter`. It was already true before this release; it is restated because the
older `everyone-role-defaults-frontend-guide.md` discusses these values as decimal bit masks, which
describes the underlying storage rather than what arrives over HTTP.

On the way in, both the string form and a numeric mask are accepted.

### What moved

`permissions` no longer contains any wiki or household name. They live in `modulePermissions`, a
separate 64-bit space with its own bit numbering. **The two spaces overlap numerically and mean
different things**: bit 0 is `ViewChannel` in the core mask and `ViewWiki` in the module mask. Never
OR them together.

Everything in this list moved out of `permissions` and into `modulePermissions`:

```
ViewWiki, CreateWikiPages, EditOwnWikiPages, EditAnyWikiPage, DeleteWikiPages,
ManageWikiRevisions, ManageWikiStructure, ModerateWikiComments, PublishWikiPublicly,
ManageLists, AddListItems, CheckOffListItems, ManageChores, CompleteChores,
ManageLedger, AddExpenses, ManagePantry, CreateDecisions, VoteDecisions,
ManageGuests, PlanMeals, ManageMeals, LogMaintenance, ManageMaintenance
```

A client that checks `role.permissions` for any of those now finds nothing and renders the
capability as absent. Every read site needs to know which mask a bit lives in.

Every other permission kept its exact bit position, so a stored core mask means today what it meant
yesterday.

The module mask also appears on permission overwrites and on the caller's own resolved permissions.
**Section 8 covers both, and one of them changes how you must write overwrites** - read it before
touching that endpoint.

### Twelve new core permissions

```
ReadMessageHistory      SendVoiceMessages       SendPolls
UseExternalEmojis       UseExternalStickers     CreatePrivateThreads
UseApplicationCommands  CreateExpressions       ManageExpressions
PrioritySpeaker         RequestToSpeak          UseVoiceActivity
```

Four of these are **storage only** - the bit exists and is editable, but nothing enforces it yet
because Echo has no feature behind it: `CreatePrivateThreads` (no private-thread concept),
`PrioritySpeaker` and `RequestToSpeak` (no stage channels, no per-speaker volume), and
`UseVoiceActivity` (not server-enforceable; VAD versus push-to-talk is a client capture choice).
`SendPolls`, `SendVoiceMessages` and `UseExternalStickers` are likewise inert because Echo has no
poll, voice-message or sticker concept at all. Show them in a role editor if you want the mask to
round-trip faithfully; do not gate any UI affordance on them.

`ReadMessageHistory`, `UseApplicationCommands`, `UseExternalEmojis`, `CreateExpressions` and
`ManageExpressions` **are** enforced.

### The failure mode to avoid

If your permission editor assembles a mask from a fixed list of checkboxes and PUTs the result, then
saving any role from a client that predates this release **silently strips every bit the list does
not know about** - all twelve new ones, and the entire module mask. Ship the editor's updated bit
lists before or with this release.

Refetch guilds after deploy; any permission mask your client cached is stale.

---

## 2. Role objects gained fields

| Field | Type | Notes |
|---|---|---|
| `modulePermissions` | string | See section 1 |
| `hoist` | bool | Display this role's members separately in the member list |
| `mentionable` | bool | Defaults to **true**. See section 5 |
| `iconUrl` | string? | Role badge image |
| `unicodeEmoji` | string? | Role badge emoji. Mutually exclusive with `iconUrl` |
| `isManaged` | bool | Integration-owned. Not editable or deletable by anyone |
| `botUserId` | string? | Set when a bot owns this role |
| `integrationId` | string? | Set when an integration owns this role |

`hoist` is a real feature, not a flag to store: the member list should group hoisted roles into their
own headed sections, ordered by role position, with everyone else below.

`isManaged` should disable the edit and delete affordances entirely. The server returns 400 on both.

---

## 3. New and changed endpoints

### New

| Route | Purpose |
|---|---|
| `GET /api/v1/guilds/{guildId}/roles` | List roles, ordered by position descending |
| `GET /api/v1/roles/{roleId}` | Fetch one role |
| `PATCH /api/v1/guilds/{guildId}/members/{memberId}/roles` | Set a member's complete role set |
| `GET|POST|DELETE /api/v1/guilds/{guildId}/roles/{roleId}/icon` | Role badge image |
| `POST /api/v1/guilds/{id}/mfa` | Owner-only; toggle the guild's MFA requirement |

The bulk member-role endpoint takes the **desired full set**, matching Discord's member patch:

```json
{ "roleIds": ["role_a", "role_b"] }
```

The server diffs it against what the member holds, so re-sending the same set is a no-op. It
replaces N sequential PUT/DELETE calls and emits one event and one audit entry instead of N.

### Changed

**`PATCH /api/v1/roles/{roleId}` is now a real PATCH.** Every field is nullable and an omitted field
means "leave alone". It previously behaved like a PUT, so a client patching only the colour was
writing null over the role's name and description. One consequence: clearing a description is not
expressible by omission - send an empty string. Same for clearing a badge.

**`PATCH /api/v1/guilds/{guildId}/roles/reorder` is a partial reorder.** Send only the roles that
move; everything unlisted keeps its position. A client that submits a full permutation of the
guild's roles **will now get a 400**, because that list necessarily includes @everyone. The rules:

- position must be at least 1 - position 0 belongs to @everyone and nothing else
- no duplicate role ids, no duplicate positions within the submitted set
- no position already held by a role you did not list (a tie would be manageable by neither party,
  since rank is compared with strict greater-than)
- no @everyone role, no managed role
- no position at or above your own highest role - 403

**`POST /api/v1/guilds/{guildId}/roles`** no longer accepts `type` or `guildId` in the body. Extra
JSON fields are ignored, so an old client keeps working; a forged `type` is simply dropped. New
roles are created at **position 1**, just above @everyone, matching Discord - not at the top.

**`PUT /api/v1/roles/{roleId}/members/{memberId}`** returns **204** when the member already holds the
role (it was 202 unconditionally, and each repeat call used to add a duplicate row).

**400 responses** now come back from: renaming or deleting @everyone, removing a member from
@everyone, and any edit or delete of a managed role.

---

## 4. MFA-gated guilds

A guild owner can require two-factor authentication for moderation and permission changes. When set,
role writes, permission overwrites, kick/ban/mute and guild settings return:

```json
{
  "error": "mfaRequired",
  "action": "enrollMfa",
  "message": "This guild requires two-factor authentication for moderation and permission changes."
}
```

with status **403**. Switch on `error` and route the user to MFA enrolment rather than showing a
generic "forbidden". The shape matches the existing `staleSubscription` / `sessionGone`
discriminators.

The gate runs *after* the action's own permission check, so a caller who would be refused anyway
gets a plain 403 and learns nothing about the guild's posture.

Turning the requirement **on** requires the caller to have MFA on their own session. Turning it
**off** does not, deliberately, so an owner who loses their authenticator cannot end up with a guild
nobody can administer. Bots are exempt - a bot token is not a user session. Nickname changes are not
gated, matching Discord.

---

## 5. Role mentions

Role pings are now gated. A role mention survives only if the role belongs to the channel's own
guild **and** (`role.mentionable` is true **or** the author holds `MentionEveryone` in that channel).
Role mentions are dropped entirely in DMs and group conversations.

Failed mentions are **stripped, not rejected** - the message still sends, it just does not ping.
This matches how `@everyone` / `@here` already behave, and it means the user gets no error to
explain the silence. **The composer should show which roles are mentionable** and indicate when a
ping will not fire, or this is invisible.

---

## 6. Realtime events

Role changes now push. You can stop refetching the whole guild on every role edit.

| Event | Payload |
|---|---|
| `guild.RoleCreated` | `{ GuildId, Role }` |
| `guild.RoleUpdated` | `{ GuildId, Role }` |
| `guild.RoleDeleted` | `{ GuildId, RoleId }` |
| `guild.MemberRolesUpdated` | `{ GuildId, MemberId, UserId, AddedRoleIds, RemovedRoleIds }` |
| `guild.RolesReordered` | unchanged, still the reorder payload |

---

## 7. Behaviour changes your users will see

**Private and read-only channels work now.** Previously an @everyone overwrite denying `ViewChannel`
or `SendMessages` was a no-op, because the server re-granted the denied bit from any implying
permission the member still held - and `@everyone` holds several by default. A deny now carries
everything that implies it. If you built a client-side workaround (hiding channels the server
claimed were visible), remove it. Guilds imported from Discord will also stop showing channels that
were private on the source server.

**Timeouts revoke more.** A timed-out member is now reduced to exactly `ViewChannel` and
`ReadMessageHistory`, which is Discord's rule and applies to moderators too. Permission-driven UI
follows automatically; hardcoded assumptions do not.

**An allow grants exactly the bits it names.** Overwrite allows no longer widen to implied
permissions, so "you may see this channel and only see it" is now expressible.

**@everyone is implicit.** Every member of a guild resolves @everyone's permissions whether or not a
membership row exists. Bots and federated members previously resolved nothing.

---

## 8. Permission overwrites, and your own resolved permissions

### Overwrites now carry the module mask too

`PUT /api/v1/channels/{channelId}/permissions/roles/{roleId}` and its three siblings
(channel/member, category/role, category/member) accept two new **optional** fields:

```json
{
  "allowPermissions": "ViewChannel",
  "denyPermissions": "SendMessages",
  "allowModulePermissions": "CheckOffListItems",
  "denyModulePermissions": "None"
}
```

The two pairs have different omission semantics, deliberately:

- **The core pair replaces.** It predates the split, every client already sends both, and `"None"`
  has always meant "clear".
- **The module pair merges.** Omitting it leaves whatever is stored untouched. Sending `"None"`
  clears it - omission and explicit-empty are genuinely different requests.

This asymmetry exists because module overwrites can already exist in guilds today: template
instantiation writes them, and no shipped client can see or resend them. Under replace semantics,
any client saving an ordinary core-mask edit would silently destroy that state with no error. If
you are writing a new client, send all four fields and the distinction stops mattering.

The response body of those four routes now echoes both module fields.

Both module masks are subject to the same escalation clamp as the core pair, in **both** directions:
you cannot allow, and cannot deny, a module bit you do not hold yourself. A bit belonging to a
`GuildFeature` the guild has switched off is **rejected with 403**, not silently dropped - same as
the core path.

### Where the module mask now appears

| Shape | Field | Reached by |
|---|---|---|
| `RoleDto` | `modulePermissions` | `GET /guilds/{id}`, `GET /guilds/{id}/roles`, `GET /roles/{id}` |
| `FlatRoleDto` | `modulePermissions` | `me.roleMembers[].role` |
| `ChannelPermissionDto` | `allow`/`denyModulePermissions` | the four overwrite routes' 200 body |
| `FlatChannelPermissionDto` | `allow`/`denyModulePermissions` | `member.permissionOverwrites[]` in `GET /guilds/{id}/me` **and** `GET /guilds/{id}/members` |
| `SelfMemberDto` | `effectiveModulePermissions` | `GET /guilds/{id}/me` |

All of these are additive.

### Read `effectiveModulePermissions`, do not compute it

`SelfMemberDto` already carried `effectivePermissions` - the caller's fully resolved core mask,
including ownership, every role, member-level allow/deny, implied bits and the clamp to enabled
modules. It now carries `effectiveModulePermissions` alongside it.

Use it. Unioning `roleMembers[].role.modulePermissions` yourself produces the wrong answer for a
guild owner, whose membership row carries no permissions and whose only role is @everyone. That is
the same trap the core field was introduced to remove.

Both fields are nullable, and null means "not computed by this endpoint", not "no permissions".
Only `/me` populates them.
