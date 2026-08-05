# Venta Federation Protocol - Specification

Protocol identifier: `venta/v0.1`

## Overview

Venta is a server-to-server federation protocol for the Echo platform. Instances exchange signed
events over HTTPS. Each event is cryptographically tied to its origin server and carries causal
ordering metadata so distributed instances can reconstruct a consistent event history.

This document is the single normative reference for the protocol: wire format, instance
lifecycle, the canonical-ID/shadow-entity data model, split-brain handling, delivery reliability,
security, and versioning. It supersedes the original wire-format-only spec that used to live at
`Federation.Application/README.md` (kept now as a short pointer here).

---

## Identity

Federated identifiers follow the format:

```
<localId>:<domain>
```

Examples:

| Type         | Example                          |
|--------------|-----------------------------------|
| User         | `usr_abc123:social.example.com`  |
| Channel      | `ch_abc123:guild.example.com`    |
| Guild        | `gld_abc123:guild.example.com`   |
| Conversation | `conv_abc123:chat.example.com`   |
| Profile      | `prf_abc123:social.example.com`  |
| Friendship   | `frd_abc123:social.example.com`  |

The domain suffix is resolved to `https://<domain>` when sending events. If the suffix already
contains a scheme (e.g. `https://...`) it is used as-is.

### Canonical-ID / shadow-entity model

Federated entities keep their **origin-assigned local ID as their identifier on both sides**.
Echo's IDs (KSUIDs) are already globally unique for practical purposes, so there is no separate
ID-mapping table per message/channel/user - the `<id>:<domain>` form exists only for
**cross-instance routing** (which host to send an event to, or which host an inbound event's
sender came from), never as a second identity a receiver has to reconcile against a first.

Concretely:

- When a guild is federated (an admin links it to a remote instance, creating a
  `FederatedResource` row of type `Guild`), a remote user who joins keeps their own instance's
  local user ID. This instance materializes a **shadow `GuildMember`** row with that same ID and
  `FederatedServerId` set to mark it as remote-origin.
- A DM conversation that includes a federated member gets a **shadow `Conversation`**
  (`Conversation.OriginInstanceId` set) and shadow `ConversationMember` rows
  (`ConversationMember.FederatedServerId` set) on the non-origin side.
- A cross-instance friendship materializes a **shadow `Profile`** (`Profile.FederatedServerId`
  set) for the remote party, so a normal local `Relationship` row can reference it exactly like
  any other profile.
- A federated message keeps its origin `MessageId`, but its `AuthorId` is stored in **full
  federated form** (`<id>:<domain>`), not stripped - this is what lets a client visually
  distinguish a remote author, and what lets outbound wiring recognize "this content already came
  from federation" (see [Split-brain and conflict resolution](#split-brain-and-conflict-resolution)).

`FederatedServerId`/`OriginInstanceId` fields predate this specific implementation pass (they
were added across `GuildMember`, `Profile`, `ConversationMember`, and `ApplicationUser` in an
earlier, incomplete federation attempt) - this pass is what actually populates and reads them.

---

## Instance Registration

Before two instances can exchange events they must mutually register each other. A
`FederationInstance` record is stored on each side containing:

| Field                | Description                                         |
|-----------------------|-----------------------------------------------------|
| `Host`               | Full URL of the remote instance (`https://...`)     |
| `Name`               | Human-readable display name                         |
| `PublicKey`          | Raw Ed25519 public key bytes (32 bytes)             |
| `Status`             | `Pending`, `Active`, `Suspended`, `Defederated`, or `Blocked` |
| `DefederationReason` | Optional reason string when status is Defederated   |

`Host` is unique (enforced by a DB index) - nothing else about an instance disambiguates it, and
without this a race between an inbound handshake and a concurrently-initiated outbound one could
create two rows for the same remote host (see [Split-brain](#split-brain-and-conflict-resolution)).

### Status state machine

```
                    ┌───────────┐
        (inbound,   │  Pending  │  (inbound, auto-accept policy)
      approval req'd)└─────┬─────┘
              ┌────────────┼────────────┐
              │            │            │
              ▼            ▼            │
         ┌─────────┐  ┌────────┐        │
         │ Blocked │  │ Active │◄───────┘  (outbound handshake always lands
         └─────────┘  └───┬────┘            here directly - initiator implicitly
                           │                 trusts who it chose to reach out to)
              ┌────────────┼────────────┐
              ▼            ▼            │
        ┌───────────┐┌─────────────┐    │
        │ Suspended │ │ Defederated │    │
        └─────┬─────┘ └─────────────┘    │
              └──────────────────────────┘
             (reactivation - admin action)
```

- **Pending → Active**: admin approves (`POST /api/v1/admin/federation/{id}/approve`), or
  auto-accept policy (`FederationSettings.AcceptancePolicy = AutoAccept`) applies it immediately
  on handshake receipt.
- **Pending → Blocked**: admin denies (`POST /api/v1/admin/federation/{id}/deny`).
- **Active → Suspended**: not currently exposed via an admin endpoint - reserved for automated
  policy (e.g. abuse-rate detection) to land in without the finality of Defederated. Currently
  only reachable by direct DB action; a real trigger is a known gap.
- **Active → Defederated**: admin action (`POST /api/v1/admin/federation/{id}/defederate`), with
  a required reason. Terminal in practice - no endpoint moves a Defederated instance back to
  Active.
- **Any non-Active → Active**: only via re-running the handshake (`POST
  /api/v1/admin/federation/initiate` outbound, or the remote re-initiating inbound), which
  upserts rather than duplicating the existing row (see [Split-brain](#split-brain-and-conflict-resolution)).

**Only `Active` instances may send or receive events.** The inbound endpoint rejects any other
status with HTTP 403. Outbound sends check the same status before making the HTTP call (a fix
from this pass - previously outbound had no status check at all, so a send to a
just-defederated/suspended instance still fired and either got rejected by them or silently
failed).

---

## Transport

All events are sent as HTTP POST requests:

```
POST https://<target-instance>/api/v1/federation/events
Content-Type: application/json
X-Federated-Protocol: venta/v0.1
```

The request body is a `SignedFederationEvent`:

```json
{
  "payload": { ... },
  "signature": "<base64-encoded bytes>"
}
```

### Signature

The signature is an Ed25519 signature over the UTF-8 JSON serialization of `payload`. The sender
signs with its private key; the receiver verifies against the sender's registered public key.

Signing steps:
1. Set `payload.Host` = sender's instance URL
2. Set `payload.ProtocolVersion` = `"venta/v0.1"`
3. Serialize `payload` to UTF-8 JSON
4. Sign the bytes with Ed25519 (NSec `SignatureAlgorithm.Ed25519`, raw key format)

Verification: reject the event if `IsValid(instance)` returns false.

---

## Inbound Validation Pipeline

The receiver applies checks in this order:

1. Deserialize body - 400 on malformed JSON
2. Look up `payload.Host` in registered instances - 400 if unknown
3. Check instance status is `Active` - 403 otherwise
4. Verify Ed25519 signature - 400 on invalid signature
5. Check `payload.ProtocolVersion == "venta/v0.1"` - exception if mismatched
6. Pass to `IFederationProvider.HandleInboundEventAsync`

---

## Event Structure

All events share a common base:

```json
{
  "$eventType": "<discriminator>",
  "host": "https://sender.example.com",
  "protocolVersion": "venta/v0.1",
  "eventId": "<uuid>",
  "previousEventIds": ["<uuid>", "..."],
  "depth": 42,
  "channelId": "<federated-id>",
  "senderId": "<federated-id>",
  "originServerTime": "2025-01-01T00:00:00Z"
}
```

| Field              | Type       | Description                                               |
|--------------------|------------|-------------------------------------------------------------|
| `$eventType`       | string     | JSON type discriminator (see event catalogue below)       |
| `host`             | string     | Sender's instance URL - set automatically when signing    |
| `protocolVersion`  | string     | Always `"venta/v0.1"`                                     |
| `eventId`          | string     | UUID, globally unique per event                           |
| `previousEventIds` | string[]   | Parent event IDs in the causal DAG                        |
| `depth`            | long       | Topological depth: `max(parent depths) + 1`, 0 for roots  |
| `channelId`        | string     | Scope key for channel-scoped events; empty for global     |
| `senderId`         | string     | Federated ID of the acting user                           |
| `originServerTime` | DateTime   | Wall-clock time on the sending server (informational)     |

`senderId` is threaded all the way from each domain service's own "who did this" data
(`AuthorId`, `UserId`, etc.) through `IFederationProvider`'s outbound methods - earlier versions
of this provider never actually populated it (every outbound call left it as `""`), which this
pass fixed. A few flows still can't supply a real actor id because the *local* domain event that
triggers them doesn't carry one (conversation edit/delete, member-added/removed) - those go out
with an empty `senderId` rather than a fabricated one; a known, called-out gap rather than
something papered over.

---

## Causal DAG

Events form a DAG scoped per channel (or per sending instance for non-channel events).

- `previousEventIds` names all current forward-extremities (tips) of the DAG at send time.
- A root event (no prior events in scope) has `previousEventIds = []` and `depth = 0`.
- When two branches diverge concurrently, both are valid. A subsequent event pointing to both
  branch tips acts as a merge commit, resolving the split.
- Receivers must apply events in topological order. Events whose parents have not yet been
  received are buffered (`FederatedEventRecord.Applied = false`) until their dependencies arrive.

```
A ──► B ──► D (merge)
 └──► C ──►
```

In this example B and C are concurrent (both have A as parent). D merges them:
`D.previousEventIds = [B.eventId, C.eventId]`, `D.depth = max(B.depth, C.depth) + 1`.

### Buffer bound

A buffered event whose parent never arrives (permanent network partition, defederation mid-flight,
event lost before delivery retry existed) would wait forever without a bound. `FederationDagGcService`
sweeps hourly and drops any `Applied = false` row older than **7 days**, logging what was dropped.
This is a deliberate availability-over-consistency tradeoff: that branch of the scope's history is
permanently lost on this instance rather than blocking indefinitely. See
[Backfill / recovery](#backfill--recovery) for the alternative to losing it.

---

## Event Catalogue

### Messaging

| `$eventType`           | Direction    | Extra fields                              |
|------------------------|--------------|---------------------------------------------|
| `messageCreated`       | Bidirectional | `messageId`, `content` (bytes), `mentions` |
| `messageEdited`        | Bidirectional | `messageId`, `content` (bytes)            |
| `messageDeleted`       | Bidirectional | `messageId`                               |
| `messageReactionAdded` | Bidirectional | `messageId`, `emoji`                      |
| `messageReactionRemoved` | Bidirectional | `messageId`, `emoji`                    |

### Guild

| `$eventType`         | Direction    | Extra fields                     |
|-----------------------|--------------|------------------------------------|
| `guildMemberJoined`  | Bidirectional | `guildId`                       |
| `guildMemberLeft`    | Bidirectional | `guildId`                       |
| `guildMemberBanned`  | Bidirectional | `guildId`, `bannedUserId`, `reason` |
| `guildInviteAccepted`| Outbound     | `guildId`, `inviteCode`          |
| `guildInviteRevoked` | Outbound     | `guildId`, `inviteCode`          |
| `guildInviteRedeemed`| Inbound      | `guildId`, `inviteCode`          |
| `guildJoinRequest`   | Inbound      | `guildId`                        |

### Social

| `$eventType`          | Direction    | Extra fields         |
|------------------------|--------------|------------------------|
| `socialFriendRequest` | Bidirectional | `targetUserId`      |
| `socialFriendAccepted`| Bidirectional | `initiatorUserId`   |
| `socialFriendRejected`| Bidirectional | `initiatorUserId`   |
| `socialFriendRemoved` | Bidirectional | `targetUserId`      |

### Conversation

| `$eventType`              | Direction    | Extra fields                     |
|-----------------------------|--------------|-------------------------------------|
| `conversationCreated`     | Bidirectional | `conversationId`, `memberIds`   |
| `conversationEdited`      | Bidirectional | `conversationId`                |
| `conversationDeleted`     | Bidirectional | `conversationId`                |
| `conversationMemberAdded` | Bidirectional | `conversationId`, `userId`      |
| `conversationMemberLeft`  | Bidirectional | `conversationId`                |

`guildInviteAccepted`/`guildInviteRevoked` are outbound-only: a remote instance never sends them
to us (`InboundEventDispatcher` maps them to no materialization command at all if one somehow
arrives).

---

## Outbound wiring

`IFederationProvider`'s outbound methods (`SendMessageAsync`, `JoinChannelAsync`, etc.) were
fully implemented but **never called from anywhere** before this pass - no domain service
published into federation at all. Each domain now has a small `Federation.Application/Bus/Outbound/*`
handler class subscribing to that domain's existing cross-service Wolverine bus contracts:

| Domain       | Subscribes to                                                                 | Federation link check |
|--------------|--------------------------------------------------------------------------------|------------------------|
| Messaging    | `Guild.Contracts.Bus.Events.MessageCreatedForChannel`/`Updated`/`Deleted`, `ReactionCreatedEvent`/`RemovedEvent` (Messaging already publishes these for Guild's own realtime/bots fan-out) | Resolves the channel's owning guild via `GetChannelRequest`/`Response`, then checks for an `Active` `FederatedResource(Guild, guildId)` |
| Guild        | `MemberJoinedForBots`/`MemberRemovedForBots` (already published from the membership endpoints) | Same `FederatedResource(Guild, guildId)` check |
| Social       | New `Social.Contracts` events (`FriendRequestCreatedEvent`, `FriendRequestRejectedEvent`, `FriendRemovedEvent`; `FriendshipAcceptedEvent` already existed) | No stored link - friendships have no admin-managed federation relationship; whether a given friendship is cross-instance is determined purely by whether the other party's id is already in federated form |
| Conversation | `Messaging.Domain.Events.Conversation.*` directly (Messaging disables conventional local routing, so these already travel over the real broker even for its own in-service handlers) | Same federated-id check as Social, applied per member |

A resource only federates to instances with an `Active` link/relationship - `FederatedResourceLookup`
is the shared helper both Messaging and Guild's outbound handlers use for the guild-scoped case.

### Known outbound gaps

- **Social's own "add friend" flow only supports targeting a local username today** - there is no
  way for a user to actually address a federated user id, so `SocialOutboundHandlers` is wired
  correctly but structurally unreachable until that UX exists. This is a `Social.Application`
  product gap, not a federation wiring one.
- **`conversationEdited` has no local trigger** - `Messaging.Application` has no
  edit/rename endpoint for conversations at all yet.
- **Channel reactions don't fan out to Guild at all today**, federated or not - Messaging never
  publishes `ReactionCreatedEvent`/`RemovedEvent` for channel-scoped reactions (only DM
  conversation reactions get a realtime push). `MessagingOutboundHandlers`' reaction subscriptions
  are correct but currently unreachable for the same reason. Pre-existing gap, not introduced by
  federation wiring.
- **`senderId` is empty for a few flows** - see the Event Structure section above.

---

## Inbound materialization

Previously, `VentaFederationProvider.HandleInboundEventAsync` resolved the DAG correctly but the
"ready to apply" events were published as `FederationInboundEventReady`, which **had no
subscriber anywhere** - a successfully received and verified event was recorded and then silently
dropped.

`InboundEventDispatcher` now turns each DAG-resolved `FederationEvent` into a typed
`Federation.Contracts.Materialization.*` command (stripping the `<domain>` suffix back off ids
that are addressed to this instance, recovering the plain canonical id) and publishes it.
Guild/Messaging/Social.Application each have a `Bus/Federation/*MaterializationHandlers.cs`
subscribing to their domain's commands, writing shadow rows **directly via EF/the message
repository, bypassing the normal endpoints** (`InviteEndpoint`, `MemberEndpoint`,
`FriendshipEndpoints`, etc.) that those endpoints would otherwise use - this is what prevents an
outbound/inbound echo loop: a federation-originated change never re-triggers the bus event its
own domain's outbound handler subscribes to.

The one place this couldn't be avoided (materialized messages *do* republish the local
`Messaging.Domain.Events.Message.MessageCreated`/`Updated`/`Deleted` domain events, to get the
existing realtime-hub-push/bots-notification fan-out for free) is guarded explicitly instead:
`MessagingOutboundHandlers` checks whether `AuthorId` is already in federated
(`<id>:<domain>`) form and skips re-federating it if so, since a federated-form author means this
content just arrived via federation and doesn't need to go back out.

Materialization is idempotent by natural business key (e.g. `GuildId + UserId` already exists →
no-op), not by `EventId` - the DAG service already deduplicates by `EventId` before a
materialization command is ever published, so the handlers only need to be safe against Wolverine's
at-least-once bus delivery redelivering the same already-applied command, which a business-key
check on unique/queryable state handles.

### Known inbound gaps

- **`guildJoinRequest` has no handler** - there is no existing approval workflow for a remote user
  asking to join one of our guilds (guilds are joined via invite codes only today). A real gap,
  not something to improvise an approval UX for as part of this pass.
- **No display-name enrichment** - a materialized shadow `GuildMember`/`Profile` uses the
  federated user id itself as a placeholder username, since none of these events carry a display
  name in-band. `IFederationProvider.GetUserProfileAsync` already exists and is wired to
  `Social.Application`'s real profile lookup - the natural next step is calling it during
  materialization and backfilling the real name once available.

---

## Split-brain and conflict resolution

Concrete gaps found during this pass, and what each one's resolution actually is:

1. **Permanent DAG gaps.** See [Buffer bound](#buffer-bound) above (7-day GC) and
   [Backfill / recovery](#backfill--recovery) below (the alternative to losing that history).

2. **Outbound delivery wasn't durable.** `VentaFederationProvider` used to fire the POST and
   ignore the response entirely - a non-2xx (remote `Suspended`, a transient network blip, the
   remote restarting) silently dropped the event forever, even though the local DAG tip had
   already advanced, permanently orphaning that branch on the remote side. Fixed: every outbound
   `FederatedEventRecord` now tracks `Delivered`/`Attempts`/`TargetHost`, and
   `FederationOutboundRetryService` sweeps every 30 seconds retrying anything undelivered (capped
   at 10 attempts).

3. **Concurrent conflicting terminal state** (e.g. both instances toggle the same member's ban
   status close together, before either side's event naturally merges the branches). **Policy:**
   highest `depth` wins; ties broken by the lexicographically greatest `eventId`. This is the
   normative rule - materialization handlers should apply it whenever a conflicting write is
   possible, rather than "whichever write physically lands last," which is a real race today.
   *Implementation note:* this pass wires the core materialization paths (idempotent create/no-op
   on existing state), but does not yet implement the depth/eventId comparison inside every
   handler for the genuinely-conflicting-update case (e.g. simultaneous ban+unban) - that's the
   next hardening step once real multi-instance traffic exercises it.

4. **Duplicate `FederationInstance` rows from racing handshakes.** Fixed by a unique index on
   `Host`. Whichever handshake commits first wins; a second concurrent one for the same host is a
   constraint violation the handshake endpoint should treat as "already registered, treat as a
   re-handshake" rather than a hard failure (the current endpoint doesn't yet catch this
   specifically - worth a follow-up if the race is observed in practice, since a unique-constraint
   violation surfacing as a raw 500 is not a great failure mode even though it can't corrupt data).

5. **Status-transition precedence.** `Defederated`/`Blocked` always wins over any concurrent event
   processing. Checked both inbound (`FederationEndpoint`, pre-existing) and now outbound too
   (`VentaFederationProvider.SendEventAsync`, new in this pass - previously there was no outbound
   status check at all).

---

## Delivery reliability

- Every outbound event is persisted (`FederatedEventRecord`) before the HTTP POST, with
  `Delivered = false` initially.
- On a successful (2xx) response, `Delivered` flips `true`.
- On failure (non-2xx or a thrown exception), `Attempts` increments and the record stays eligible
  for retry.
- `FederationOutboundRetryService` (a `BackgroundService`) sweeps every 30 seconds for
  `!Delivered && Attempts < 10`, re-signs (fresh signature each attempt - the payload, including
  `EventId`/`Depth`/`PreviousEventIds`, is unchanged since those were stamped once at creation)
  and re-POSTs.
- An event that exhausts 10 attempts stays `Delivered = false` indefinitely, visible via direct
  query - there is no alerting on this yet.

## Backfill / recovery

`GET /api/v1/federation/events/{scopeKey}/backfill` (header: `X-Federated-Host: <caller's own
host>`) returns this instance's full applied event history for a scope, ordered by depth. A
receiver with a permanently-stuck buffered event (see [Buffer bound](#buffer-bound)) can call this
against the event's origin host and re-run the response through its own DAG resolution to fill
the gap without waiting on GC to drop it.

Access requires the caller to be a registered `Active` instance (checked via the same
`X-Federated-Host` header convention), not a full signature challenge - a backfill response only
re-serves events that instance already received and verified once through the normal signed path,
so proving authenticity a second time isn't necessary, only proving the caller is a real
federation partner and not an open scrape target.

**This is the serving side only.** The receiving side - detecting "this buffered event has been
stuck long enough, go pull its scope from `record.Host`" and actually calling this endpoint - is
not wired up automatically yet. A real follow-up, not a hidden gap: the endpoint exists and works,
nothing calls it proactively yet.

---

## Security considerations

- **Signing**: Ed25519, per-event, over the full payload. Verified against the sender's
  registered public key on every inbound event.
- **Replay protection**: relies on `EventId` uniqueness (a `FederatedEventRecord` with that id
  already `Applied` is a no-op). There is no timestamp-window check - a captured, valid signed
  event could in principle be replayed indefinitely without one, though replaying an already-
  applied event is harmless (dedup), and any event's *DAG position* means a replayed old event
  can't retroactively change already-resolved state. A timestamp window is still worth adding as
  defense in depth against a compromised/malicious peer resending old-but-not-yet-applied events
  to keep a target scope permanently backlogged.
- **Key rotation**: not implemented. `FederationInstance.PublicKey` is set once at handshake time;
  there is no mechanism to rotate it without a full re-handshake (which today would need to be
  modeled as defederate-then-re-register, since the handshake endpoint doesn't currently support
  updating an existing `Active` instance's key in place). A known, unaddressed gap.
- **What a malicious/compromised peer can do**: forge events as any local id on their own
  instance (this is inherent to federation - every instance is trusted to police its own users);
  cannot forge events as *our* users (would need our private key); cannot retroactively rewrite
  already-applied history (DAG + dedup); can flood buffered-but-unresolved events for a scope
  (bounded by the 7-day GC, at the cost of that scope's federation history from them being lost).
- **Backfill endpoint** exposes previously-received event payloads to any registered Active
  instance requesting a scope, not just the scope's original participants - acceptable since
  federation events aren't secret between federated instances generally, but worth being aware of
  if a scope is ever meant to be visible to a strict subset of an instance's federation partners
  (not a case the protocol currently distinguishes).

---

## Versioning

`ProtocolVersion` is checked for exact equality (`"venta/v0.1"`) on every inbound event and at
handshake time - there is no negotiation. A future `venta/v0.2` would need either:
- A transition period where both versions are accepted and translated, or
- A per-instance negotiated version stored alongside `FederationInstance` (established once at
  handshake, renegotiated only via a fresh handshake).

Neither exists yet; this is a deliberate simplicity choice for the current single-version
protocol, not an oversight, but should be revisited before an actual `v0.2` needs to ship.

---

## Wire Example

Sending a message from `instance-a.example.com` to `instance-b.example.com`:

```json
{
  "payload": {
    "$eventType": "messageCreated",
    "host": "https://instance-a.example.com",
    "protocolVersion": "venta/v0.1",
    "eventId": "3f2a1b4c-...",
    "previousEventIds": ["1e9d2c3b-..."],
    "depth": 7,
    "channelId": "ch_xyz:instance-b.example.com",
    "senderId": "usr_alice:instance-a.example.com",
    "originServerTime": "2025-06-24T10:00:00Z",
    "messageId": "msg_001",
    "content": "<base64 bytes>",
    "mentions": []
  },
  "signature": "<base64 Ed25519 signature>"
}
```
