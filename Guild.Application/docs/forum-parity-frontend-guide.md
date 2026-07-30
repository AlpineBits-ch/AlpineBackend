# Forum tags & Discord parity — frontend integration guide

Extends [forum-channels-frontend-guide.md](forum-channels-frontend-guide.md), which covered the
v1 forum (a Forum channel plus threads-as-posts, no tags). This document is the **full contract**
for bringing forums to Discord feature parity: tags, per-forum config, pinning, locking,
filtering, sorting, and paginated post listing.

> **Status: backend complete, pending deploy.** Code, tests and the database migration
> (`20260730145530_AddOnboardingPromptsWelcomeScreenAndForums`) have all landed. Shapes and routes
> below match the merged code. The only thing left is a deploy — build against this now, but a
> running server won't answer until that ships.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call the Guild
microservice directly. The gateway strips the `/guild` segment; the routes shown already include
it. Every endpoint requires the normal `Authorization: Bearer <token>`.

## Contents

- [Concepts](#concepts)
- [Endpoint summary](#endpoint-summary)
- [Tags](#tags)
- [Forum config](#forum-config)
- [Posts](#posts)
- [Listing & filtering posts](#listing--filtering-posts)
- [Realtime events](#realtime-events)
- [Limits & error responses](#limits--error-responses)
- [Rendering guidance](#rendering-guidance)
- [Migrating existing forum UI](#migrating-existing-forum-ui)
- [Known limitations](#known-limitations)

## Concepts

A **forum channel** (`type: "Forum"`) is a container. Its **posts** are channels with
`type: "Thread"` and `parentChannelId` pointing at the forum — unchanged from v1.

A **forum tag** is a label defined *on a forum channel*, not globally. Tags belong to exactly one
forum; two forums never share a tag, and a tag id is meaningless outside its forum. Each tag has a
name, an optional emoji, a colour (for chip styling), a position (order in the picker), and a
`moderated` flag. Posts carry a set of **applied tags** drawn from their forum's available tags.

A **media channel** (`type: "Media"`) is a forum variant — same tags, same posts, same endpoints;
only the intended rendering differs (gallery-first, media-forward). It is creatable through the
normal channel-create endpoint. Everywhere this document says "forum", read "forum or media
channel".

## Endpoint summary

| Action | Method & path | Permission |
|---|---|---|
| List tags | `GET .../guild/channels/{forumId}/tags` | `ViewChannel` |
| Create tag | `POST .../guild/channels/{forumId}/tags` | `ManageChannel` |
| Update tag | `PATCH .../guild/forum-tags/{tagId}` | `ManageChannel` |
| Delete tag | `DELETE .../guild/forum-tags/{tagId}` | `ManageChannel` |
| Reorder tags | `PATCH .../guild/channels/{forumId}/tags/reorder` | `ManageChannel` |
| Get forum config | `GET .../guild/channels/{forumId}/forum-config` | `ViewChannel` |
| Update forum config | `PATCH .../guild/channels/{forumId}/forum-config` | `ManageChannel` |
| List posts (filter/sort/page) | `GET .../guild/channels/{forumId}/posts` | `ViewChannel` |
| Create post | `POST .../guild/channels/{forumId}/threads` | `CreateThreads` |
| Set a post's tags | `PUT .../guild/threads/{threadId}/tags` | creator, or `ManageAnyThread` |
| Pin / unpin a post | `PATCH .../guild/threads/{threadId}/pin` | `ManageAnyThread` |
| Lock / unlock a post | `PATCH .../guild/threads/{threadId}/lock` | `ManageAnyThread` |
| Archive a post | `PATCH .../guild/threads/{threadId}/archive` | `ManageOwnThreads` (creator) / `ManageAnyThread` |

Prefix every path with `https://api.venta.gg/api/v1` — e.g. the first row in full is
`GET https://api.venta.gg/api/v1/guild/channels/{forumId}/tags`.

**No new permission bits.** Everything above reuses flags your role editor already exposes, so
nothing needs granting before this works — existing moderators can manage tags on day one.

## Tags

### Shape

```ts
interface ForumTag {
  id: string;            // "ftag_..."
  channelId: string;     // the forum this tag belongs to
  guildId: string;
  name: string;          // 1-20 chars, unique per forum (case-insensitive)
  emojiId?: string;      // a guild emoji ("emoj_...") — mutually exclusive with emojiName
  emojiName?: string;    // a unicode emoji, e.g. "🐛"
  color: string;         // "#RRGGBB", defaults to "#000000"
  position: number;      // 0-based, ascending; ties broken by name
  moderated: boolean;    // if true, only moderators can apply/remove it
  postCount: number;     // non-archived posts currently carrying this tag
}
```

`emojiId` resolves against the guild's custom emojis — fetch those from
`GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/emojis`, which returns a presigned
`imageUrl` per emoji (see [custom-emoji-frontend-guide.md](custom-emoji-frontend-guide.md)). If a
tag's emoji is later deleted from the guild, `emojiId` will still be set but resolve to nothing —
fall back to rendering the tag without an emoji rather than showing a broken image.

`postCount` is computed per request and **excludes archived posts**. It's there so the tag filter
bar can show counts; don't treat it as a stable value to cache.

### List

```
GET https://api.venta.gg/api/v1/guild/channels/{forumId}/tags
```

Returns `ForumTag[]` ordered by `position`. Requires `ViewChannel` on the forum. Returns `404` if
the channel isn't a Forum or Media channel.

### Create

```
POST https://api.venta.gg/api/v1/guild/channels/{forumId}/tags
{ "name": "bug", "emojiName": "🐛", "color": "#e74c3c", "moderated": false }
```

`name` is required; everything else is optional. `position` is assigned automatically (appended to
the end) — use the reorder endpoint to move it. Returns the created `ForumTag`.

### Update

```
PATCH https://api.venta.gg/api/v1/guild/forum-tags/{tagId}
{ "name": "confirmed bug", "color": "#c0392b" }
```

Only the fields you send are touched — the same null-means-unchanged convention used elsewhere in
this API. To *clear* an emoji, send `"emojiId": ""` / `"emojiName": ""` (empty string, not null).
Renaming or recolouring a tag applies retroactively to every post carrying it; no post-side
update is needed and none is emitted.

Setting `moderated: true` does **not** strip the tag from posts that already have it — existing
applications stand, only future changes are gated.

### Delete

```
DELETE https://api.venta.gg/api/v1/guild/forum-tags/{tagId}
```

Returns `204`. The tag is removed from every post that carried it, in the same transaction. Posts
themselves are untouched. If the forum has `requireTag: true` and this leaves a post with zero
tags, the post stays as-is — `requireTag` is enforced at write time, never retroactively.

### Reorder

```
PATCH https://api.venta.gg/api/v1/guild/channels/{forumId}/tags/reorder
{ "tagIds": ["ftag_c", "ftag_a", "ftag_b"] }
```

Send the complete ordered list of the forum's tag ids — positions are assigned from the array
index. Partial lists are rejected with `400`; this mirrors the bulk-write shape of the existing
channel reorder endpoint. Returns `204`.

## Forum config

Per-forum settings that shape the posting experience. Every forum has a config; it's created with
default values the first time the forum is read, so `GET` never 404s on a valid forum.

```ts
interface ForumConfig {
  channelId: string;
  requireTag: boolean;                  // default false — posts must carry ≥1 tag
  defaultSortOrder: "LatestActivity" | "CreationDate";   // default "LatestActivity"
  defaultLayout: "List" | "Gallery";                     // default "List"
  defaultReactionEmojiId?: string;      // the one-tap reaction shown on post cards
  defaultReactionEmojiName?: string;
  defaultThreadSlowModeSeconds: number; // default 0 — slowmode inherited by new posts
  defaultAutoArchiveMinutes: 60 | 1440 | 4320 | 10080;   // default 4320 (3 days)
}
```

```
GET   https://api.venta.gg/api/v1/guild/channels/{forumId}/forum-config
PATCH https://api.venta.gg/api/v1/guild/channels/{forumId}/forum-config
```

`PATCH` takes any subset. `defaultLayout` and `defaultReactionEmoji*` are **presentation hints the
backend stores and echoes but never acts on** — they exist so the choice syncs across a user's
devices and so moderators can set a house style. Rendering is entirely yours.

`defaultSortOrder` is the sort applied when the post list is requested without an explicit `sort`
param. `defaultThreadSlowModeSeconds` is copied onto each new post at creation — changing it does
not retroactively alter existing posts.

## Posts

A post is a `Thread` channel with forum-specific fields layered on:

```ts
interface ForumPost {
  id: string;
  guildId: string;
  parentChannelId: string;   // the forum
  type: "Thread";
  name: string;              // the post title
  description?: string;
  createdAt: string;         // ISO 8601
  updatedAt: string;
  createdByUserId: string;

  tagIds: string[];          // applied tags, ordered by the tag's own position
  isPinned: boolean;
  isLocked: boolean;         // no new messages; distinct from archived
  isArchived: boolean;
  autoArchiveAt?: string;    // when it will auto-archive absent further activity
  autoArchiveMinutes?: number;  // the post's archive window, snapshotted from the forum at creation

  lastActivityAt?: string;   // last message timestamp; null if the post has no messages
  messageCount: number;

  isAgeRestricted: boolean;
  isPrivate: boolean;
  slowModeSeconds: number;
}
```

**Locked vs archived.** Archived means "off the default list, still readable, can be revived by
posting" (unchanged v1 behaviour). Locked means "no new messages, by moderator decision" and
persists independently — a post can be locked but not archived, and vice versa. Render them
differently: a lock icon and disabled composer for locked, a muted/collapsed card for archived.

### Creating a post

```
POST https://api.venta.gg/api/v1/guild/channels/{forumId}/threads
{
  "name": "Dark mode is too bright",
  "content": "The dark theme background is basically grey?",
  "tagIds": ["ftag_bug", "ftag_ui"]
}
```

`tagIds` is new and optional — unless the forum has `requireTag: true`, in which case an empty or
absent `tagIds` is rejected with `400`. `name` and `content` behave exactly as in v1 (`content`
posts the first message server-side, one round trip).

Applying a `moderated` tag here requires `ManageChannel` or `ManageAnyThread`; without it the whole
request is rejected with `403` rather than silently dropping the tag. Check `moderated` on the tag
list and hide those chips from the picker for non-moderators so this never fires.

Returns the created `ForumPost`.

### Setting a post's tags

```
PUT https://api.venta.gg/api/v1/guild/threads/{threadId}/tags
{ "tagIds": ["ftag_bug", "ftag_confirmed"] }
```

**Replace semantics** — send the complete desired set, not a delta. This makes the call idempotent,
which matters because a chip picker naturally emits whole sets and because retrying after a network
blip must not double-apply. Sending `{"tagIds": []}` clears all tags (rejected with `400` if the
forum has `requireTag: true`).

Allowed for the post's creator or anyone with `ManageAnyThread`. Adding *or removing* a `moderated`
tag additionally requires `ManageChannel`/`ManageAnyThread` — a post author cannot peel off a
`confirmed-bug` tag a moderator applied.

All ids must belong to this post's forum; a foreign or unknown tag id fails the whole request with
`400`. Returns the updated `ForumPost`.

### Pin and lock

```
PATCH https://api.venta.gg/api/v1/guild/threads/{threadId}/pin    { "pinned": true }
PATCH https://api.venta.gg/api/v1/guild/threads/{threadId}/lock   { "locked": true }
```

Both require `ManageAnyThread` and return `204`. Pinned posts always sort above unpinned ones,
regardless of the active sort. There is no cap on pinned posts, but the UI should discourage more
than a handful.

Note this is *post* pinning — entirely separate from pinning a *message inside* a post, which is
the Messaging service's existing feature and unchanged.

## Listing & filtering posts

```
GET https://api.venta.gg/api/v1/guild/channels/{forumId}/posts
```

| Param | Values | Default |
|---|---|---|
| `tagIds` | comma-separated tag ids | none (no filter) |
| `match` | `any` \| `all` | `any` |
| `sort` | `activity` \| `created` | the forum's `defaultSortOrder` |
| `archived` | `false` \| `true` \| `all` | `false` |
| `limit` | 1-50 | 25 |
| `cursor` | opaque string from a previous response | none (first page) |

```
GET .../guild/channels/{forumId}/posts?tagIds=ftag_bug,ftag_ui&match=all&sort=activity&limit=25
```

Response:

```ts
interface ForumPostPage {
  posts: ForumPost[];
  nextCursor: string | null;   // null when there are no further pages
}
```

- `match=any` returns posts carrying **at least one** of the given tags; `match=all` requires
  **every** one. Pass a single tag id and the two are equivalent.
- Pinned posts sort above unpinned ones *within* the ordering, so they occupy the top of page one
  and never reappear on a later page. Don't re-sort client-side — the cursor encodes the pinned
  flag, so a client-side re-sort desynchronizes from what the next page assumes.
- `sort=activity` orders by `lastActivityAt` descending, falling back to `createdAt` for posts with
  no messages. `sort=created` orders by `createdAt` descending.
- `cursor` is **opaque** — a base64 blob. Never parse, construct, or persist it beyond the session;
  pass back exactly what you got in `nextCursor`. Cursors are tied to the `sort` and filter params
  they were issued under; changing any filter means starting from page one.

Pagination is keyset-based, so page 40 costs the same as page 1 and no post is skipped or
duplicated when someone posts mid-scroll.

### The old threads endpoint

`GET .../guild/channels/{channelId}/threads` still exists, still returns a bare `ChannelDto[]`, and
still works for both Text-parented and Forum-parented threads. **It is now capped at the 50 most
recent threads** — previously it returned every thread in the channel unbounded, which was a
latent problem on any busy channel.

Keep using it for text-channel thread sidebars. For forums, move to `/posts`: the old endpoint has
no tag filtering, no `lastActivityAt` sort, no pinning awareness, and no pagination.

## Realtime events

Over the existing SignalR hub at `https://api.venta.gg/api/v1/ws/hub` — no new connection, no new
subscription step. All of these are delivered to guild members currently present.

| Event | Payload |
|---|---|
| `guild.ForumTagCreated` | `{ guildId, channelId, tag: ForumTag }` |
| `guild.ForumTagUpdated` | `{ guildId, channelId, tag: ForumTag }` |
| `guild.ForumTagDeleted` | `{ guildId, channelId, tagId }` |
| `guild.ForumTagsReordered` | `{ guildId, channelId, tagIds: string[] }` (full ordered list) |
| `guild.ForumConfigUpdated` | `{ guildId, channelId, config: ForumConfig }` |
| `guild.ThreadCreated` | `{ guildId, channelId, parentChannelId, tagIds }` — **`tagIds` is new** |
| `guild.ThreadUpdated` | `{ guildId, channelId, parentChannelId, name, tagIds, isPinned, isLocked, isArchived }` — **all but `archived` are new** |

Applied-tag changes, pins, locks and archives all arrive as `guild.ThreadUpdated` rather than as
separate events — one handler updates the post card for any of them. The payload carries the full
current state of those flags, so treat it as a replace, not a patch.

`guild.ForumTagDeleted` does **not** come with per-post updates for the posts that lost the tag.
Drop the tag id from every cached post in that forum when you receive it.

## Limits & error responses

| Rule | Limit | Violation |
|---|---|---|
| Tags per forum | 20 | `400` |
| Applied tags per post | 5 | `400` |
| Tag name length | 1-20 chars | `400` |
| Tag name uniqueness | per forum, case-insensitive | `409` |
| Colour format | `#RRGGBB` | `400` |
| `requireTag` with empty `tagIds` | — | `400` |
| Applying/removing a `moderated` tag without permission | — | `403` |
| Tag id not belonging to this forum | — | `400` |
| Tag / post / forum not found | — | `404` |
| Caller lacks the permission in the endpoint table | — | `403` |

`400` responses use the standard validation-problem body already returned across this API
(`{ errors: { "<field>": ["<message>"] } }`); `409` and `403` return a plain string body. Surface
the `409` on tag creation as an inline "a tag with that name already exists" on the name field
rather than a toast — it's the one error users will hit routinely.

## Rendering guidance

**Forum view.** Tag filter bar across the top from `GET .../tags` (chips with emoji, `color` as
background or border, `postCount` as a badge), post list below from `GET .../posts`. Multi-select
chips map to `tagIds` + `match=all`; a single-select filter maps to one id. Persist the user's
`sort` choice locally, seeded from `defaultSortOrder`.

**Post card.** Title (`name`), first-message snippet, applied tag chips (already ordered — render in
array order), `messageCount`, relative `lastActivityAt`, plus pin/lock/archive affordances.
`defaultLayout: "Gallery"` is the hint to render media-forward cards instead of rows.

**Tag picker.** Filter `moderated` tags out for users without `ManageChannel`/`ManageAnyThread`
rather than showing-then-rejecting. Enforce the 5-tag cap client-side. On save, send the whole set
to `PUT .../tags`.

**Moderator settings.** A tag editor under channel settings — create/rename/recolour/emoji/moderated
plus drag-reorder (one `PATCH .../tags/reorder` on drop, sending the full order). Forum config sits
on the same screen.

## Migrating existing forum UI

If you already shipped the v1 forum:

1. `ChannelDto` for a `Thread` gains `tagIds`, `isPinned`, `isLocked`, `lastActivityAt`,
   `messageCount`, `autoArchiveAt`. All additive — existing parsing keeps working.
2. Swap the post list from `.../threads` to `.../posts` and adopt the `{ posts, nextCursor }`
   envelope. Required if you rely on seeing more than 50 posts.
3. Handle the five new `guild.Forum*` events; extend your existing `guild.ThreadUpdated` handler
   for the new fields.
4. Nothing needs a permission-model change or a role migration.

## Known limitations

- **No per-post reactions.** `defaultReactionEmoji` is stored and echoed for your UI to use, but the
  backend does not track reactions on a *post* — only on individual messages inside it. A one-tap
  reaction chip on a post card has to target the post's first message.
- **`lastActivityAt` starts empty.** It's maintained going forward from deploy; posts created before
  then have `null` until someone posts in them again. Under `sort=activity` those fall back to
  `createdAt`, so ordering is sane but not historically exact.
- **No thread member list.** There's no join/leave-a-post concept and no per-post follower list, so
  no "N following" count and no follower-scoped notifications.
- **Auto-archive is a sweep, not a promise.** `autoArchiveAt` is honoured by a periodic background
  pass, so a post may sit a few minutes past its timestamp before flipping. Don't render a
  live countdown that hits zero and stalls.
- **Tags don't federate or export.** Server templates and Discord import carry channel structure but
  not forum tags in this pass — a forum imported from Discord arrives with its posts untagged.
- **No tag-based notification routing.** You can't subscribe to "only posts tagged `announcement`".
