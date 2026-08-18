# Channel visibility - frontend integration guide

What the server will and will not tell a member about channels they cannot see, and how a channel is
made private.

## Base URL

Through the gateway, same as everything else:

```
https://api.venta.gg/api/v1/guild/...
```

The gateway rewrites `/api/v1/guild/{rest}` to `/api/v1/{rest}`, so the service route
`/api/v1/guilds/{guildId}/channels` is reached as `/api/v1/guild/guilds/{guildId}/channels`.

---

## 1. Channel lists are filtered server-side

```http
GET /api/v1/guild/guilds/{guildId}
GET /api/v1/guild/guilds/{guildId}/channels
```

Both return only the channels the caller holds `ViewChannel` on. A channel the caller is walled out
of is absent from the array - not present with a flag, not present with a null name. Its id, name,
description and category placement never reach the client.

Do not filter channel lists client-side, and do not treat a missing channel as an error. If a client
holds a `channelId` that has stopped appearing, the caller's access to it was removed; drop it from
local state rather than refetching it individually.

Roles and categories are still returned in full - those are guild-wide.

## 2. `isPrivate` means one specific thing

`isPrivate` on a channel is the `@everyone` role's `ViewChannel` deny on that channel. It is not a
separate label sitting beside the permission model: setting it writes that overwrite, clearing it
removes it, and the permission resolver answers accordingly.

### On create

```http
POST /api/v1/guild/guilds/{guildId}/channels

{
  "name": "staff-only",
  "type": "Text",
  "isPrivate": true
}
```

Defaults to `false`. A channel created with `isPrivate: true` is invisible to every member who has
no role or member-level overwrite granting them `ViewChannel` back.

### On update

```http
PATCH /api/v1/guild/channels/{channelId}

{
  "name": "staff-only",
  "isPrivate": true
}
```

`isPrivate` is **optional and nullable. Omitted means "leave alone."** A PATCH that sends only
`name` does not make a private channel public. Send `false` to explicitly make it public.

The other fields on this body are still replace-semantics, so send the values you want the channel
to end up with.

### It stays in agreement with the overwrite

Editing the `@everyone` overwrite directly through the permission-overwrite endpoints moves
`isPrivate` with it:

```http
PUT    /api/v1/guild/channels/{channelId}/permissions/roles/{everyoneRoleId}
DELETE /api/v1/guild/channels/{channelId}/permissions/roles/{everyoneRoleId}
```

Denying `ViewChannel` there sets `isPrivate`; clearing that deny unsets it. An editor that offers
both a "private" toggle and a permission grid can drive either one and read the result off the
other - they cannot disagree.

Granting a role or member `ViewChannel` back on a private channel is the normal way to let people
in; the channel stays `isPrivate: true`, because `@everyone` is still denied.

A guild with no `@everyone` role cannot express privacy this way, and the flag is refused rather than
recorded - there would be nothing enforcing it.

## 3. Read states are yours alone

```http
GET /api/v1/guild/guilds/{guildId}/me       -> carries readStates
GET /api/v1/guild/guilds/{guildId}/members  -> does not
```

Unread counts and last-read cursors come from `/me`. The member list is other people's rows and
carries no read state for anyone, including the caller.

Clients rendering unread badges should read `/me` once per guild and key the result by `channelId`,
rather than picking their own row out of the member list.

## Summary of client work

| Change | Action |
|---|---|
| Channel lists exclude invisible channels | Stop filtering client-side; treat a vanished channel as access removed |
| `isPrivate` is settable on create | Add the toggle to the create-channel form |
| `isPrivate` is nullable on PATCH | Omit it unless the toggle was actually changed |
| Privacy toggle and permission grid are one thing | Refetch the channel after editing either |
| `readStates` only on `/me` | Source unread badges from `/me`, not the member list |
