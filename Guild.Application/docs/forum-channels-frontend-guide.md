# Forum channels - frontend integration guide

> **Superseded in part by [forum-parity-frontend-guide.md](forum-parity-frontend-guide.md)**, which
> adds tags, per-forum config, pinning/locking, and a filtered, paginated post list. The v1
> behaviour below still holds; the limitations at the bottom of this page are addressed there.

Backend support for Forum channels is done and live. There's no new entity behind this - a Forum
channel is a container, and each "post" in it is exactly the same `Thread` channel type already
used for text-channel threads, just parented to a Forum instead of a Text channel.

## What's new

- `ChannelType.Forum` is now creatable through the normal channel-create endpoint (previously it
  was a declared enum value with no creation path).
- `POST .../channels/{channelId}/threads` now accepts a Forum channel as `{channelId}`, not
  just a Text one - that's how you create a forum post.
- `CreateThreadDto` gained an optional `content` field.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call the Guild
microservice directly. The gateway strips a leading `/guild` segment; the routes shown already
include it.

## Creating a forum channel

```
POST https://api.venta.gg/api/v1/guild/guilds/{guildId}/channels
{ "type": "Forum", "name": "feedback", "categoryId": "..." }
```

Same permission (`Permissions.ManageChannel`) and response shape as creating a Text or Voice
channel - nothing forum-specific here.

## Creating a forum post

```
POST https://api.venta.gg/api/v1/guild/channels/{forumChannelId}/threads
{ "name": "Dark mode is too bright", "content": "The dark theme background is basically grey?" }
```

- `name` is the post title.
- `content`, when present, is posted as the post's first message automatically (server-side, one
  round trip) - author is the requester, no separate message-create call needed. Omit it and the
  post opens empty, same as today's plain threads.
- Requires `Permissions.CreateThreads` on the forum channel, same permission as thread creation
  under a Text channel.
- Returns a `ChannelDto` (`type: "Thread"`, `parentChannelId` pointing at the forum) - identical
  shape to a Text-channel thread.

## Listing posts in a forum channel

`GET https://api.venta.gg/api/v1/guild/channels/{forumChannelId}/threads` - unchanged endpoint,
works for both Text-parented and Forum-parented threads already since it filters by
`parentChannelId` regardless of the parent's own type. Requires `Permissions.ViewChannel`.

## Replying / everything else

A forum post is a normal channel once created - send messages into it with the usual
`POST https://api.venta.gg/api/v1/messaging/messaging` (`channelId` = the post's id; yes, `messaging`
appears twice - pre-existing gateway/route naming, not new here), react, pin, attach files, all
exactly as in any other channel. Archiving
(`PATCH https://api.venta.gg/api/v1/guild/threads/{threadId}/archive`) also works unchanged.

## Realtime events

No new events - `guild.ThreadCreated` / `guild.ThreadUpdated` fire exactly as they do for
Text-parented threads today; `parentChannelId` tells you whether the client should render it in a
thread sidebar (Text parent) or a forum post list (Forum parent).

## Rendering guidance

- Forum channel view: a list of posts (query the threads endpoint), each row showing the post
  title (`name`), the first message's `content` as a preview snippet, and reply/participant counts
  if you're already tracking those elsewhere.
- Opening a post is just opening a channel - reuse your existing message-list view.

## Known limitations (v1)

- No tags/categories on posts (Discord's forum tag system isn't implemented).
- No "pinned post" concept distinct from regular message pinning - use the existing
  [message pinning](../../Messaging.Application/docs/message-pinning-frontend-guide.md) feature
  inside a post if you want to highlight a reply within it.
- Posts don't currently support reactions on the post itself (only on individual messages within
  it) since a post has no message of its own beyond the optional first one.
