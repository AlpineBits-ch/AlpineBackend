# Scheduled events — frontend integration guide

Backend support for guild scheduled events (Discord's "Events" tab equivalent) is done and live.
**No reminder notifications are sent** in this pass (no "starts in 30 minutes" push) - see Known
limitations before building UI that implies they exist.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly.

## Endpoints

| Action | Method & path | Permission |
|---|---|---|
| List events | `GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/events` | Any guild member |
| Create event | `POST https://api.venta.gg/api/v1/guild/guilds/{guildId}/events` | `ManageEvents` |
| Update event | `PATCH https://api.venta.gg/api/v1/guild/events/{eventId}` | `ManageEvents` |
| Cancel event | `DELETE https://api.venta.gg/api/v1/guild/events/{eventId}` | `ManageEvents` |
| Mark interested | `POST https://api.venta.gg/api/v1/guild/events/{eventId}/interested` | Any guild member |
| Remove interest | `DELETE https://api.venta.gg/api/v1/guild/events/{eventId}/interested` | Any guild member |

`ManageEvents` is a new permission bit - grant it through the normal role-permission editor.

### Event shape

```ts
interface ScheduledEvent {
  id: string;
  guildId: string;
  creatorUserId: string;
  title: string;
  description?: string;
  startsAt: string;       // ISO 8601
  endsAt?: string;
  location?: string;      // freeform text ("the park", "our server's lobby")
  voiceChannelId?: string;
  status: "Scheduled" | "Active" | "Completed" | "Cancelled";
  interestedCount: number;
  isInterested: boolean;  // whether the requesting user has marked interest
}
```

Create/update accept a subset of these fields (`title`, `description`, `startsAt`, `endsAt`,
`location`, `voiceChannelId`) - `PATCH` only touches fields you include, same
null-means-unchanged convention used elsewhere in this API. `location` and `voiceChannelId` are
not mutually exclusive at the API level - send whichever (or both) fit your event.

`GET .../events` excludes cancelled events entirely - don't expect to see them in the list, they
just disappear. There's no separate "show cancelled" toggle in v1.

### Cancelling vs. nothing else

There is no delete endpoint - `DELETE .../events/{eventId}` **cancels** (soft), it doesn't remove
the row. This is intentional: members who'd marked interest should be able to tell "this got
called off" rather than the event just vanishing. The realtime event is `guild.EventCancelled`,
distinct from an update.

## Realtime events

| Event | Payload |
|---|---|
| `guild.EventCreated` | `{ guildId, eventId, title, startsAt }` |
| `guild.EventUpdated` | `{ guildId, eventId, title, startsAt }` |
| `guild.EventCancelled` | `{ guildId, eventId }` |

## Rendering guidance

- An "Events" tab/panel per guild, listing upcoming events sorted by `startsAt` (already sorted
  that way by the list endpoint).
- Interest toggle: a simple button/heart-icon wired to the mark/remove-interested endpoints,
  showing `interestedCount` and reflecting `isInterested` for the current user's own state.
- `voiceChannelId` present → offer a "join voice" shortcut using your existing guild-voice join
  flow for that channel.

## Known limitations (v1)

- **No reminder notifications.** Nothing pushes to members as an event approaches or starts -
  members only find out by checking the events list. If your product needs "starts soon" alerts,
  that's client-side polling/local-notification territory for now, not a server feature yet.
- No automatic status transitions - `status` starts at `Scheduled` and only ever moves to
  `Cancelled` (via the cancel endpoint); nothing server-side flips it to `Active`/`Completed` as
  `startsAt`/`endsAt` pass. Treat `startsAt`/`endsAt` as the source of truth for "is this
  happening now" client-side rather than relying on `status` for that.
- No RSVP tiers beyond a single "interested" - no separate "going"/"maybe".
- No recurring events - each event is a one-off.
