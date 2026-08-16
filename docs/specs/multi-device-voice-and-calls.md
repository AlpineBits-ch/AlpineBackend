# Multi-Device Calls & Voice Channels - Client Spec

Audience: frontend (desktop/web) and mobile client engineers.
Status: **implemented server-side.** This document describes the contract as built.

> **Follow-up:** the device id is now validated against the caller's registered devices, and push tokens, login sessions and device registration were consolidated around it. See [device-identity-consolidation-client-guide.md](device-identity-consolidation-client-guide.md) for what changed since this document.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call a microservice directly.

---

## 1. Why this is changing

Today the backend has no concept of "which device" a user is acting from - a call participant and a voice-channel participant are each a single record keyed by `userId`. This causes two concrete bugs:

1. **Calls**: if you accept an incoming call on your phone and then decline it on your laptop (both ringing for the same account), the decline silently ends the whole call - even though you're actively connected on the phone.
2. **Guild voice channels**: if you're in a voice channel on desktop and then join the *same* channel from mobile, nothing kicks the desktop session. Both devices fight over the same underlying media session and desktop's audio quietly breaks with no explanation to the user.

We're also changing call-leave behavior: today, anyone leaving a call kills it for everyone. Going forward, leaving a group call only removes you - the call keeps going for the rest. If that leaves exactly one person alone in the call, the call doesn't end immediately: that person gets a 5-minute grace period before the server disconnects them.

Fixing this properly requires the client to identify itself with a stable **Device ID** on every relevant call.

---

## 2. Device ID - new required field

- A stable, per-installation identifier. **Reuse the existing MLS device ID your client already generates and persists** (`ClientDeviceId`, used today for E2E key registration) - do not invent a second ID.
- Required on:
  - The realtime hub connection (query string).
  - All Call action endpoints (accept/decline/leave/end/create).
  - The guild voice channel join endpoint.
- Sent as header **`X-Device-Id`** on REST calls, and as query param **`deviceId`** on the hub connection URL.
- **Backward compatibility during rollout**: if omitted, the server treats the request as coming from an implicit `default` device per user. This means old client builds keep today's (buggy) single-device behavior until updated - they won't error, but they also won't get the fix. Update all platforms together for this to actually resolve the bugs.

---

## 3. Realtime hub connection

No path change, only a new query parameter:

```
wss://api.venta.gg/api/v1/ws/hub?deviceId={yourDeviceId}
```

Nothing else about connecting/auth changes. This is what lets the server target a push/event at *one specific device* instead of every open session for your account (used by the new `CallDeviceDismissed`, `CallDeviceTakeover`, and `guild.voice.KickedByOtherDevice` events below).

---

## 4. Calls (`Messaging` service)

Base path: `https://api.venta.gg/api/v1/messaging/voice`

### 4.1 Endpoints

| Action | Method & path | Change |
|---|---|---|
| Create call | `POST /call` | Add `X-Device-Id` header for consistency (not currently used by the creation step itself - the creator's active device is recorded moments later when their client establishes the call's media session). |
| Accept | `PUT /call/{callId}/accept` | Add `X-Device-Id` header. **Required.** |
| Decline | `PUT /call/{callId}/decline` | Add `X-Device-Id` header. **Required.** |
| Leave | `PUT /call/{callId}/leave` | **NEW endpoint.** Add `X-Device-Id` header. Removes you from an active call without ending it for everyone else. |
| End | `PUT /call/{callId}/end` | Unchanged path. Semantics narrowed - see 4.3. |

Accept/Decline/Leave/End all still return the current call state as before; no response shape changes.

### 4.2 Multi-device accept/decline resolution

Server-side, each call participant tracks a single `activeDeviceId` - the device currently connected to the call's audio, if any. The rule is simple and order-independent: **once a participant is connected, a decline can no longer un-connect them.** Any decline that arrives for an already-connected participant is treated as stale and left alone - it doesn't touch the call, it just tells that one device to stop ringing.

Behavior your client must handle:

- **Accepting on device A while device B is still ringing**: device B receives **`call.CallAccepted { callId, userId, deviceId, call }`** and must dismiss its local incoming-call UI. It broadcasts to every participant's sessions, so the *caller* gets it too - and the caller should use it to stop their outgoing ringback. Do not wait for `call.ParticipantJoined` for that: that one fires only once the answering client has actually published a microphone, it is addressed only to people already in the voice room, and nothing repeats it. `call.CallAccepted` is the answer; `ParticipantJoined` is the media that follows it.
  - `deviceId` is the device that answered, and may be absent for a client that sent no `X-Device-Id`.
  - `call` is the full call entity (the same shape `call.IncomingCall` carries), so its own id field is `id`. Read the top-level `callId`, not `call.id`.
  - Unlike the voice-room events, this one carries **no `instanceId`/`version` envelope** - it is a call-lifecycle event, not a room delta. Do not put it behind a version gate.
- **Declining on device B after you already accepted on device A (race)**: device B receives a new event, **`call.CallDeviceDismissed { callId, deviceId }`**, sent *only to that device*. Treat it exactly like a locally-cancelled ring - dismiss the incoming-call UI, do **not** show "call ended," because it isn't. The call keeps running on device A.
- **Declining on your only ringing device (normal case)**: unchanged - `call.CallDeclined` fires as today.
- **Accepting on device B while already connected via device A** (e.g. you pick up on your laptop, then tap accept on your phone too): this is treated as a **device switch**, not two simultaneous connections. Device A receives **`call.CallDeviceTakeover { callId, oldDeviceId, newDeviceId }`** targeted only at it. On receipt, device A must immediately tear down its local WebRTC/audio session and show something like "You joined this call on another device" - it must **not** call `leave` itself, the server has already updated state.

You cannot be connected to the same call from two devices simultaneously - accepting on a new device always transfers the call, never duplicates it.

### 4.3 Leave vs. End, and the "alone" timeout

- **`leave`**: removes *you* from the call. Broadcasts `call.CallParticipantLeft { callId, userId }` to the remaining participants. The call keeps running for everyone else. This is the action your "leave call" / hang-up button should call in a group call.
- **`end`**: force-terminates the call for **everyone**, regardless of how many participants remain. Keep this available for an explicit "End call for everyone" action if your UI has one; otherwise you generally want `leave`.
- This applies uniformly, including 1:1 calls - hanging up on a 1:1 call no longer instantly destroys the call record. It calls `leave`, which drops the other side to "alone," starting the grace timer described next, rather than ending immediately. In practice a 1:1 call will still end almost immediately in the normal case (the other party also leaves/was never fully connected), but the mechanism is the same as group calls.
- **Alone timeout**: whenever a `leave` drops the call to exactly **one** remaining connected participant, the server starts a 5-minute timer for that person and sends **`call.CallAlone { callId, userId, deadline }`** (`deadline` = ISO-8601 timestamp, ~5 minutes out) to that participant's active device. Client should show a "waiting for others to rejoin - call ends at {deadline}" indicator. If nobody else joins/accepts before the deadline, the server force-ends the call: `call.CallEnded { callId, reason: "AloneTimeout" }`. If a second participant rejoins in time, the timer is cancelled server-side with no separate event - you'll simply see the call's participant list grow again; clear your "alone" UI once you observe more than one connected participant.
- If a `leave` drops the call to **zero** connected participants, it ends immediately: `call.CallEnded { callId, reason: "AllParticipantsLeft" }`.

### 4.4 `call.CallEnded` reason field (NEW)

`call.CallEnded` now always carries a `reason`, one of:

| reason | meaning |
|---|---|
| `Declined` | Everyone who was invited declined (existing behavior, e.g. no one picked up). |
| `UserEnded` | Someone explicitly called `end`. |
| `AllParticipantsLeft` | The last connected participant called `leave`. |
| `AloneTimeout` | One participant was alone for 5 minutes with nobody rejoining. |

Use this to pick the right UI copy ("Call declined" vs. "Call ended" vs. "Call timed out").

### 4.5 Event summary (Call)

| Event | Target | When |
|---|---|---|
| `call.CallAccepted` | all participants, all their devices | `{ callId, userId, deviceId, call }` - somebody answered. Stops the ring on their other devices *and* the caller's ringback |
| `call.CallDeclined` | all participants | unchanged - the participant declined without being connected elsewhere |
| `call.CallDeviceDismissed` | **NEW**, one specific device only | a stale decline arrived after that user was already connected elsewhere |
| `call.CallDeviceTakeover` | **NEW**, one specific device only | that device's connection was just taken over by another of the user's devices |
| `call.CallParticipantLeft` | **NEW**, remaining participants | a participant left a still-active call |
| `call.CallAlone` | **NEW**, the sole remaining participant | call dropped to 1 connected participant; carries the 5-min deadline |
| `call.CallEnded` | all participants | call fully terminated; now carries `reason` |

---

## 5. Guild voice channels (`Guild` service)

Base path: `https://api.venta.gg/api/v1/guild/guilds/{guildId}/channels/{channelId}/voice`

### 5.1 Join

`POST /join` - add `X-Device-Id` header. **Required.**

New behavior when you join a channel:

- **You're already in this exact channel from a different device** (e.g. desktop, now joining from mobile): the server performs a **device takeover**. The previous device's media session is closed server-side and it receives, targeted only to it:

  **`guild.voice.KickedByOtherDevice { channelId, guildId }`**

  On receipt, that device must immediately tear down its local WebRTC connection and audio, and show "You joined this channel from another device." It must **not** call `leave` itself - the server has already removed it.

- **You're in a different channel (same or a different guild) from any device**: unchanged behavior, generalized - your prior channel is cleanly left (`guild.voice.UserLeftVoice` fires there as today) before you join the new one. (Previously this cross-channel cleanup only worked within the same guild; a stale presence in another guild's voice channel is now also cleared.)

- **Same device rejoining the same channel** (reconnect after a network blip): treated as idempotent, same as today - no kick, session state is refreshed.

You can only be in one voice channel, on one device, at a time, app-wide - joining anywhere always supersedes wherever you were before.

### 5.2 Leave

`POST /leave` - unchanged, still fine to call without a device ID, but send `X-Device-Id` if you have it for consistency/logging.

### 5.3 Event summary (Guild voice)

| Event | Target | When |
|---|---|---|
| `guild.voice.KickedByOtherDevice` | **NEW**, one specific device only | you joined this same channel from another device |
| `guild.voice.UserJoinedVoice` / `UserLeftVoice` | all guild members | unchanged |

---

## 6. Required client changes, checklist

- [ ] Persist and reuse the existing MLS `ClientDeviceId` as the call/voice device identifier (don't generate a new one).
- [ ] Append `?deviceId=...` to the realtime hub connection URL.
- [ ] Send `X-Device-Id` header on: call create/accept/decline/leave/end, guild voice join.
- [ ] Handle `call.CallDeviceDismissed` - silently dismiss local ring UI, no "call ended" messaging.
- [ ] Handle `call.CallDeviceTakeover` - tear down local call session, show "joined on another device," don't call `leave`.
- [ ] Handle `guild.voice.KickedByOtherDevice` - tear down local voice session, show "joined on another device," don't call `leave`.
- [ ] Wire a "leave call" action (group calls) to the new `PUT /call/{callId}/leave`, distinct from "end call for everyone" (`.../end`).
- [ ] Handle `call.CallAlone` - show a countdown/notice using the `deadline` field; clear it once the participant list grows past 1 again.
- [ ] Branch `call.CallEnded` UI copy on the new `reason` field.

---

## 7. Rollout notes

- All of the above is additive and backward-compatible: omitting `deviceId` degrades to today's per-user (buggy) behavior rather than erroring, so mixed client versions in production won't crash - they just won't get the fix until updated.
- Recommend shipping desktop and mobile in the same release window regardless, since the multi-device scenarios this fixes are inherently cross-platform (e.g. the bug only reproduces when *one* client is updated and another isn't).
