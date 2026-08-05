# Message search - frontend integration guide

Backend support for searching message history within a single channel or conversation is done and
live. There is no cross-channel/global search in this pass - see Known limitations.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call a
microservice directly. `messaging` legitimately appears twice in a row in the path below (gateway
service prefix + the Messaging service's own route base) - same pre-existing quirk noted in the
[message pinning guide](message-pinning-frontend-guide.md).

## Endpoint

```
GET https://api.venta.gg/api/v1/messaging/messaging/search?query={text}&channelId={id}&limit={n}
GET https://api.venta.gg/api/v1/messaging/messaging/search?query={text}&conversationId={id}&limit={n}
```

| Param | Required | Notes |
|---|---|---|
| `query` | yes | Free text. Uses Postgres `websearch_to_tsquery` under the hood, so plain search-engine syntax works: quoted phrases (`"exact phrase"`), `-exclude`, `or`. |
| `channelId` | one of these two | Guild channel to search within. Requires `ViewChannel` permission. |
| `conversationId` | one of these two | DM/group conversation to search within. Requires conversation membership. |
| `limit` | no | Defaults to 25, capped at 50. |

Response is an array of `MessageDto` (same shape as `GET .../messages`), ordered by search
relevance (best match first), **not** chronologically.

## What gets indexed

- Only `Plain`-encryption messages. **MLS-encrypted messages are never indexed and never appear in
  search results** - the server cannot read encrypted content, so there is nothing to search.
  If your client relies heavily on E2E-encrypted conversations, expect search to come back empty
  for those and consider surfacing that as an explicit "search isn't available in encrypted
  conversations" state rather than an empty-results state.
- Ordinary messages only - system messages (member join/leave, invites) aren't indexed.
- Edits update the index; deletes remove from it. Both happen automatically, no client action
  needed.

## Rendering guidance

- A search box scoped to "this channel" / "this conversation" (matches the endpoint's scope -
  there's no global/cross-channel search to build a different UI for).
- Highlight the query terms in each result's `content` client-side - the API doesn't return
  match offsets or a pre-highlighted snippet in this pass.
- Clicking a result should jump to that message in the normal message view (you already have the
  message id and its `channelId`/`conversationId`).

## Known limitations (v1)

- No cross-channel or server-wide search - one channel or conversation at a time.
- No match highlighting/snippets from the API - full message `content` only.
- No search over attachment filenames or embed content, message text only.
- Encrypted (MLS) messages are invisible to search entirely, by design (see above).
