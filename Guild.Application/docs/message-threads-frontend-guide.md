# Threads on a message - frontend integration guide

A thread can now be started from a specific message, the way Discord's "Create Thread" on a message
context menu works. The message stays where it was posted and gains a thread card underneath it;
the thread itself is an ordinary `Thread` channel, so everything you already do with threads -
listing, archiving, posting into them - is unchanged.

All URLs below are public, through the gateway (`https://api.venta.gg`) - never call the Guild or
Messaging microservice directly. The gateway strips a leading `/guild` or `/messaging` segment; the
routes shown already include it.

## Starting a thread from a message

```
POST https://api.venta.gg/api/v1/guild/channels/{channelId}/messages/{messageId}/threads
{ "name": "about that message", "content": "optional first reply" }
```

- `{channelId}` is the channel the message is in, and it must be a **Text** channel. Forum and
  Media posts already are threads, and a thread cannot be started inside a thread.
- `name` is required. Clients typically pre-fill it from the first few words of the message.
- `content`, when present, is posted as the thread's first message. The starter message is *not*
  copied into the thread - it stays in the parent channel.
- `tagIds` is ignored here; tags belong to forum posts.
- Requires `Permissions.CreateThreads` on the parent channel, the same permission as the plain
  thread route.

Returns `200` with a `ChannelDto`, which now carries `starterMessageId`:

```json
{
  "id": "chan_...",
  "type": "Thread",
  "parentChannelId": "chan_...",
  "starterMessageId": "mesg_...",
  "name": "about that message",
  "isArchived": false
}
```

### Errors

| Status | When |
| --- | --- |
| `400` | The parent is not a Text channel, or it is end-to-end encrypted (see below). |
| `403` | The caller lacks `CreateThreads`. |
| `404` | No such channel, or no such message in that channel. |
| `409` | The message already has a thread. The body is `{ "threadId": "chan_..." }` - open that one. |

Treat `409` as a normal outcome, not an error toast: two people pressing the button at the same
moment is exactly what it is for, and the response tells you where to navigate.

## Rendering the card

Messages returned by Messaging now carry `threadId`:

```json
{ "id": "mesg_...", "content": "...", "threadId": "chan_...", "flags": 32 }
```

- `threadId` is null on a message with no thread.
- `flags` has bit `1 << 5` (`HAS_THREAD`) set whenever `threadId` is present. It is derived from
  `threadId` rather than stored separately, so the two can never disagree - read whichever suits
  you.

To render reply count and last activity, fetch the thread channel:

```
GET https://api.venta.gg/api/v1/guild/channels/{threadId}
```

`messageCount` and `lastActivityAt` on the channel are maintained server-side on every post.

A `threadId` that resolves to nothing is possible and is not a bug to report: render the message
without a card. It means the thread was deleted, or a create failed midway after the message had
already been stamped.

## Realtime

Two events fire when a thread is started from a message, both to everyone present in the guild:

- `guild.ThreadCreated` - `{ channelId, parentChannelId, guildId, tagIds }`, the same event a plain
  thread raises. Add the thread to any thread list you are showing.
- `guild.MessageThreadAttached` - `{ channelId, guildId, messageId, threadId, name }`. Redraw the
  one message named by `messageId` so its card appears. This is deliberately separate: a client
  showing the parent channel has to update a message it already has, which is not what
  "a new thread exists" means.

## Deleting

Deleting the thread (`DELETE .../channels/{threadId}`) clears the pointer, and the starter message
goes back to rendering without a card. Archiving does not - an archived thread still shows its
card.

## Encrypted channels

The route returns `400` in a channel with end-to-end encryption on. A thread is its own channel and
would be created unencrypted, so a thread hanging off an encrypted conversation would quietly be
plaintext. Hide the "Create thread" affordance in encrypted channels rather than letting the
request fail.

## Bots

`THREAD_CREATE` carries a non-standard `starter_message_id` alongside the standard thread object.
Discord conveys the same fact by giving the thread the starter message's own id, which Echo cannot
do because ids are prefixed per entity.
