# Group conversation name and icon - frontend integration guide

A group conversation (three or more members) can be renamed and given an icon. Any member may do
either; a 1-on-1 DM can do neither and both endpoints refuse it.

## Conversation shape

`ConversationDto` gains one field, everywhere it already appears:

```json
{
  "id": "conv_3H66JNBG6BTA8FINHJVTTE2H846",
  "name": "Die Gummibaerenbande",
  "iconUpdatedAt": "2026-08-17T19:24:49Z",
  "...": "..."
}
```

- `name` is `null` when the group has none, which is when the client titles it from the member list.
- `iconUpdatedAt` is `null` when there is no icon. Use it as the icon URL's cache key. `updatedAt`
  will not do: that moves on every message.

## Rename

```
PATCH /api/v1/messaging/conversations/{id}
{ "name": "Die Gummibaerenbande" }
```

Blank or whitespace clears the name. The cap is 100 characters. Returns the updated
`ConversationDto`. `400` for a 1-on-1 or an over-length name, `403` for a non-member.

## Icon

```
GET    /api/v1/messaging/conversations/{id}/icon     -> the image bytes, 404 when there is none
POST   /api/v1/messaging/conversations/{id}/icon     multipart, field name `file`
DELETE /api/v1/messaging/conversations/{id}/icon
```

The `GET` is authenticated and member-only, so it streams the bytes rather than redirecting to a
presigned URL: a cross-origin redirect would either drop the bearer or invalidate the signature.
A plain `<img src>` therefore cannot load it - fetch it with the session token and hand the blob to
the element. The response carries `Cache-Control: private, max-age=3600`.

`POST` accepts png, jpeg, webp and gif up to 8 MB, and both writes return the updated
`ConversationDto`.

## Realtime

```
conversation.ConversationUpdated
{ "conversationId": "conv_...", "name": "Die Gummibaerenbande", "iconUpdatedAt": "..." }
```

Sent to every member on a rename, an icon upload and an icon removal, including the member who made
the change. Patch the conversation you hold rather than refetching.

## System messages

Both changes leave a notice in the group's history, following the convention in
`system-messages-frontend-guide.md`. Neither carries a `systemMessageVariant`.

- `GroupNameChanged` - `content` is the new name, empty when the name was cleared.
- `GroupIconChanged` - `content` is empty for a new icon, `removed` when the icon was deleted.

The message's `authorId` is whoever made the change.
