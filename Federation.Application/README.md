# Venta Federation Protocol — Specification

Protocol identifier: `venta/v0.1`

## Overview

Venta is a server-to-server federation protocol for the Echo platform. Instances exchange signed
events over HTTPS. Each event is cryptographically tied to its origin server and carries causal
ordering metadata so distributed instances can reconstruct a consistent event history.

---

## Identity

Federated identifiers follow the format:

```
<localId>:<domain>
```

Examples:

| Type         | Example                          |
|--------------|----------------------------------|
| User         | `usr_abc123:social.example.com`  |
| Channel      | `ch_abc123:guild.example.com`    |
| Guild        | `gld_abc123:guild.example.com`   |
| Conversation | `conv_abc123:chat.example.com`   |
| Profile      | `prf_abc123:social.example.com`  |
| Friendship   | `frd_abc123:social.example.com`  |

The domain suffix is resolved to `https://<domain>` when sending events. If the suffix already
contains a scheme (e.g. `https://...`) it is used as-is.

---

## Instance Registration

Before two instances can exchange events they must mutually register each other. A
`FederationInstance` record is stored on each side containing:

| Field                | Description                                         |
|----------------------|-----------------------------------------------------|
| `Host`               | Full URL of the remote instance (`https://...`)     |
| `Name`               | Human-readable display name                         |
| `PublicKey`          | Raw Ed25519 public key bytes (32 bytes)             |
| `Status`             | `Active`, `Suspended`, `Defederated`, or `Blocked`  |
| `DefederationReason` | Optional reason string when status is Defederated   |

Only instances with status `Active` may send events. Instances with any other status are rejected
at the inbound endpoint with HTTP 403.

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

1. Deserialize body — 400 on malformed JSON
2. Look up `payload.Host` in registered instances — 400 if unknown
3. Check instance status is `Active` — 403 otherwise
4. Verify Ed25519 signature — 400 on invalid signature
5. Check `payload.ProtocolVersion == "venta/v0.1"` — exception if mismatched
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
|--------------------|------------|-----------------------------------------------------------|
| `$eventType`       | string     | JSON type discriminator (see event catalogue below)       |
| `host`             | string     | Sender's instance URL — set automatically when signing    |
| `protocolVersion`  | string     | Always `"venta/v0.1"`                                     |
| `eventId`          | string     | UUID, globally unique per event                           |
| `previousEventIds` | string[]   | Parent event IDs in the causal DAG                        |
| `depth`            | long       | Topological depth: `max(parent depths) + 1`, 0 for roots  |
| `channelId`        | string     | Scope key for channel-scoped events; empty for global     |
| `senderId`         | string     | Federated ID of the acting user                           |
| `originServerTime` | DateTime   | Wall-clock time on the sending server (informational)     |

---

## Causal DAG

Events form a DAG scoped per channel (or per sending instance for non-channel events).

- `previousEventIds` names all current forward-extremities (tips) of the DAG at send time.
- A root event (no prior events in scope) has `previousEventIds = []` and `depth = 0`.
- When two branches diverge concurrently, both are valid. A subsequent event pointing to both
  branch tips acts as a merge commit, resolving the split.
- Receivers must apply events in topological order. Events whose parents have not yet been
  received are buffered until their dependencies arrive.

```
A ──► B ──► D (merge)
 └──► C ──►
```

In this example B and C are concurrent (both have A as parent). D merges them:
`D.previousEventIds = [B.eventId, C.eventId]`, `D.depth = max(B.depth, C.depth) + 1`.

---

## Event Catalogue

### Messaging

| `$eventType`           | Direction    | Extra fields                              |
|------------------------|--------------|-------------------------------------------|
| `messageCreated`       | Bidirectional | `messageId`, `content` (bytes), `mentions` |
| `messageEdited`        | Bidirectional | `messageId`, `content` (bytes)            |
| `messageDeleted`       | Bidirectional | `messageId`                               |
| `messageReactionAdded` | Bidirectional | `messageId`, `emoji`                      |
| `messageReactionRemoved` | Bidirectional | `messageId`, `emoji`                    |

### Guild

| `$eventType`         | Direction    | Extra fields                     |
|----------------------|--------------|----------------------------------|
| `guildMemberJoined`  | Bidirectional | `guildId`                       |
| `guildMemberLeft`    | Bidirectional | `guildId`                       |
| `guildMemberBanned`  | Bidirectional | `guildId`, `bannedUserId`, `reason` |
| `guildInviteAccepted`| Outbound     | `guildId`, `inviteCode`          |
| `guildInviteRevoked` | Outbound     | `guildId`, `inviteCode`          |
| `guildInviteRedeemed`| Inbound      | `guildId`, `inviteCode`          |
| `guildJoinRequest`   | Inbound      | `guildId`                        |

### Social

| `$eventType`          | Direction    | Extra fields         |
|-----------------------|--------------|----------------------|
| `socialFriendRequest` | Bidirectional | `targetUserId`      |
| `socialFriendAccepted`| Bidirectional | `initiatorUserId`   |
| `socialFriendRejected`| Bidirectional | `initiatorUserId`   |
| `socialFriendRemoved` | Bidirectional | `targetUserId`      |

### Conversation

| `$eventType`              | Direction    | Extra fields                     |
|---------------------------|--------------|----------------------------------|
| `conversationCreated`     | Bidirectional | `conversationId`, `memberIds`   |
| `conversationEdited`      | Bidirectional | `conversationId`                |
| `conversationDeleted`     | Bidirectional | `conversationId`                |
| `conversationMemberAdded` | Bidirectional | `conversationId`, `userId`      |
| `conversationMemberLeft`  | Bidirectional | `conversationId`, `userId`      |

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
