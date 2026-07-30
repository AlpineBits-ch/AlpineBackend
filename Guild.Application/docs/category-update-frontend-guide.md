# Category rename — frontend guide

Adds the missing update endpoint for a channel-grouping category (rename). Categories can already
be created, deleted, and repositioned (via the bulk channel-reorder endpoint) — this closes the
last gap.

## Endpoint

```
PATCH https://api.venta.gg/api/v1/guild/categories/{categoryId}
Authorization: Bearer <token>
Content-Type: application/json

{
  "name": "New Category Name"
}
```

Requires `ManageChannel` permission in the category's guild. Only the name can be changed here —
reordering categories still goes through the existing bulk reorder endpoint
(`PATCH https://api.venta.gg/api/v1/guild/guilds/{guildId}/channels/reorder`).

### Responses

- `200 OK` — returns the updated category:
  ```json
  { "id": "cate_...", "guildId": "guild_...", "name": "New Category Name", "createdAt": "...", "updatedAt": "..." }
  ```
- `401 Unauthorized` — not authenticated.
- `403 Forbidden` — authenticated but lacks `ManageChannel`.
- `404 Not Found` — category doesn't exist.

## Realtime

Broadcasts `guild.CategoryUpdated` over the existing SignalR hub to every online member of the
guild, same shape as `guild.CategoryCreated`/`guild.CategoryDeleted`:

```json
{ "categoryId": "cate_...", "guildId": "guild_..." }
```

## Bots / federation

- **Installed Discord-compat bots**: category create/update/delete now dispatch as Discord Gateway
  `CHANNEL_CREATE`/`CHANNEL_UPDATE`/`CHANNEL_DELETE` events with `type: 4` (Discord's real
  `GUILD_CATEGORY` constant), matching how a real Discord server represents categories. This was a
  pre-existing gap — category lifecycle previously never reached installed bots at all — closed
  alongside this fix rather than left half-done.
- **Cross-instance federation**: no change needed. Federation only materializes guild
  *membership* (join/leave/ban) and message/profile data across instances — it has never
  synced channel/category structure and doesn't need to for this fix.
- **Discord import sync**: no change needed — the Discord-import sync handler already treats
  categories as `type:4` channels and already supported renaming one via that path.
