# Friend requests over the websocket (`social.*`)

Client integration guide for the relationship-lifecycle pushes added to the Social service.

## Before this

The only relationship push in the entire system was `conversation.FriendRequestAccepted`, emitted
by Messaging and delivered **only to the initiator**. Creating, rejecting, revoking and unfriending
were completely silent, so the only way a client learned about an incoming friend request was by
polling `GET /api/v1/relationships`.

## Connection

Nothing new to connect to. These arrive on the existing hub:

```
/api/v1/ws/hub?deviceId=<your-device-id>
```

## Events

| Event | Emitted when | Who receives it |
|---|---|---|
| `social.FriendRequestCreated` | `POST /api/v1/relationships` | target **and** initiator |
| `social.FriendRequestAccepted` | `POST /api/v1/relationships/{id}/accept` | both parties |
| `social.FriendRequestRejected` | `POST /api/v1/relationships/{id}/reject` | both parties |
| `social.FriendRemoved` | `POST /api/v1/relationships/{id}/revoke` | both parties |

`revoke` is the single endpoint behind both "cancel the request I sent" and "unfriend", so
`social.FriendRemoved` covers both - distinguish them by the status you had before the event, if you
care.

The actor gets the event too. That is deliberate: it is what keeps a user's *other* devices in sync,
and it means a client can drive its friends list purely from the socket rather than re-fetching
after its own mutations.

## Payload

Every one of the four events carries the same object:

```jsonc
{
  "relationshipId": "rlsp_2f8Kd...",  // YOUR row, safe to POST back to /accept|/reject|/revoke
  "status": "PendingIncoming",        // YOUR view after the change
  "userId":    "usr_9xQ...",          // the OTHER party
  "profileId": "prfl_7bN...",         // the OTHER party
  "userName":  "someone"              // the OTHER party
}
```

`status` is one of `PendingIncoming`, `PendingOutgoing`, `Friends`, `None` - a **string**, not an
integer.

The important detail: a relationship is stored as two mirrored rows, one owned by each user, and
the payload is always written from the **recipient's** point of view. The two users therefore
receive *different* `relationshipId` and `status` values for the same event. You never have to
work out which of the two rows is yours, and you can never be handed a row id you aren't allowed to
act on.

Worked example - A sends a request to B:

| | A receives | B receives |
|---|---|---|
| `relationshipId` | A's row | B's row |
| `status` | `PendingOutgoing` | `PendingIncoming` |
| `userId` / `userName` | B | A |

## Delivery guarantees

- **Exactly one push per real state transition.** `Accept`/`Reject`/`Remove` are no-ops when the
  relationship is already in the target state, so a double-tapped accept, a client retry or two
  devices racing produce one event, not two. Repeating the HTTP call is safe and still returns
  success.
- Accepting a relationship that was already rejected or removed is refused with `400` - a dead
  request cannot be revived into a friendship without a fresh one.
- Federated (cross-instance) friend requests emit the same four events to the local user. Only the
  local half of a federated relationship exists on this instance, so exactly one side is notified.
- Pushes are emitted after the transaction commits, so a client that reacts by re-fetching
  `GET /api/v1/relationships` will see the new state.

Handling events idempotently client-side is still recommended - the transport is at-least-once
across a reconnect.

## Migration

`conversation.FriendRequestAccepted` still fires with its old (initiator-only) shape so nothing
breaks today. Move to `social.FriendRequestAccepted`; the old event will be removed once clients
have migrated.
