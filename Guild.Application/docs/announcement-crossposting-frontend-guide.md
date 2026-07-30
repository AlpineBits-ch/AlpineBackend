# Announcement channel cross-posting — frontend integration guide

Backend support for Discord's "Follow Channel" + "Publish" mechanic is done and live.
`ChannelType.Announcement` was already creatable (see the forum-channels guide, which mentions
this in passing) - this adds the actual cross-posting behavior on top of it.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly. Guild endpoints (follow/unfollow) sit behind `/api/v1/guild/**`; the
publish endpoint sits behind `/api/v1/messaging/**` (and, per the pre-existing quirk noted in the
[message pinning guide](../../Messaging.Application/docs/message-pinning-frontend-guide.md),
`messaging` appears twice in a row in that one path).

## Following an announcement channel

Initiated from the **receiving** side, same direction as Discord: you pick an announcement channel
you can see in some other guild, and choose which of *your own* channels should receive its posts.

```
POST https://api.venta.gg/api/v1/guild/channels/{sourceChannelId}/followers
{ "targetChannelId": "chan_..." }
```

- `sourceChannelId` must be an `Announcement`-type channel - `400` otherwise.
- Requires `Permissions.ViewChannel` on the source (you must be able to see it to follow it) and
  `Permissions.ManageChannel` on the target channel's guild (you're piping external content into
  your own server, so you need admin rights there).
- `409 Conflict` if that exact source→target pairing already exists.

```
GET https://api.venta.gg/api/v1/guild/channels/{sourceChannelId}/followers
```
Lists every channel following a given source (any guild) - requires `ManageChannel` on the
*source* channel's own guild (this is "who's subscribed to us", a source-side admin view).

```
DELETE https://api.venta.gg/api/v1/guild/channels/{sourceChannelId}/followers/{followId}
```
Either side can unfollow: a manager of the target guild ("stop receiving these") or a manager of
the source guild ("revoke this subscriber"). `Forbid` if the caller manages neither.

## Publishing a message

```
POST https://api.venta.gg/api/v1/messaging/messaging/{messageId}/publish
```
(empty body) - copies the message to every channel currently following its (Announcement) channel.
Requires `Permissions.PinMessages` on the source channel - reused as the "elevated action" gate
rather than adding yet another permission bit; if your role editor shows permission names, "Pin
Messages" is what controls who can publish in a given announcement channel.

```json
{ "published": 3 }
```
`published` is the number of channels the message was copied into (`0` is a valid, non-error
response - it just means nobody follows that channel yet). `400` if the message isn't in an
Announcement channel at all.

## Rendering guidance

- A "Publish" icon/button on messages in Announcement channels specifically (check the channel's
  `type` before showing it - it's meaningless anywhere else).
- "Follow Channel" as an action available when viewing an Announcement channel in a server you
  don't otherwise administer but can see (e.g. a public-ish announcements channel) - prompts for
  "which of your channels should receive this."
- Crossposted messages arrive as completely ordinary messages via the normal
  `guild.MessageCreated` event in the target channel - no special "crossposted" flag or badge to
  render (see limitations).

## Known limitations (v1)

- **No visual "crossposted" indicator.** Discord shows a small icon + the origin server name on a
  crossposted message; here it's indistinguishable from a normal message once it lands, beyond
  the fact its author is someone from another guild's member list.
- **No re-publish guard.** Publishing the same message twice sends duplicate copies - there's no
  "already published" tracking, so build your own UI-side guard (disable the button after one
  successful publish) if you want to prevent double-sends.
- Mentions are stripped on the crossposted copy (`@user`/`@role`/`@everyone` all clear) - a
  mention only means something in the guild it was written in.
- No way to discover followable announcement channels beyond already being able to see one
  directly - no cross-server directory/search.
