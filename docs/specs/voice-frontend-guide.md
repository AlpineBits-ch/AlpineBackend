# Voice: the complete frontend guide

Everything a client needs to implement voice, video and screen sharing, for both **guild voice
channels** and **direct calls**.

This is the whole contract. There is no legacy surface left to fall back on: the old routes,
response shapes and heartbeat have been removed, so everything below is the only way voice works.

---

## 1. The one idea to take away

**Guild voice channels and direct calls are the same system.** A call is a voice room whose id
happens to be a call id; a channel is a voice room whose id happens to be a channel id. The server
runs one implementation for both, so anything true below is true in both places.

The only differences are:

| | Guild channel | Direct call |
|---|---|---|
| Event prefix | `guild.voice.` | `call.` |
| Room id field in payloads | `channelId` | `callId` |
| Room kind | `channel` | `call` |
| Has a ring phase | no | yes (accept/decline) |
| Has moderation | yes (server mute, move) | no |
| Guild-wide presence fan-out | yes | no |

Everything else - joining, publishing, subscribing, snapshots, versions, heartbeats, screen
sharing, viewer counts - is identical.

**The second idea:** every event carries a `version`. If you receive one *ahead* of the version you
hold, you missed something - refetch the snapshot. This is the mechanism that makes voice
recoverable, and a client that ignores it will eventually show a stale roster forever. The exact
rule has three subtleties and is written out in full in §4.2; implement it from there, not from this
paragraph.

**The third idea, and the newest:** in a large room **you are not subscribed to everyone, and what
you are subscribed to changes on its own**. Above a configured room size the server subscribes each
client to the people actually talking rather than to all forty-nine other participants, so your
subscription set moves as the conversation moves - with nobody having touched anything. §6 is the
whole contract. It is additive and optional: a client that ignores it keeps working exactly as it
does today, and simply keeps paying for streams nobody is listening to.

---

## 2. Media model: what a "stream" actually is

A participant can publish up to four kinds of track at once. The **track name** is the only thing
the SFU stores, so the naming convention is the contract.

| Track name | `kind` | What it is |
|---|---|---|
| `audio` | `audio` | The microphone. Exactly one per participant. |
| anything else not `screen-`prefixed (e.g. `camera`) | `video` | The camera. |
| `screen-{shareId}` | `screen` | Screen share **video**. |
| `screen-audio-{shareId}` | `screenAudio` | Screen share **audio** (the shared tab or app's own sound, not the mic). |

Notes that matter:

- **A screen share can carry audio**, and it is a *separate track* from the microphone. The two
  halves of one share are tied together by the same `{shareId}`.
- A share may be video-only. Do not assume `screen-audio-{shareId}` exists for every
  `screen-{shareId}`; check what is actually in the snapshot.
- Ordering trap, if you parse names yourself: `screen-audio-x` also starts with `screen-`. Test the
  longer prefix first, or you will treat a share's audio as the video of a share called
  `audio-x`, which does not exist.
- `shareId` is yours to generate. A UUID is fine. Reusing one after a share stops is allowed but
  the server drops the previous audience, so viewer counts do not leak across.
- Camera and microphone have no `shareId`.
- Video tracks may be published with **simulcast** layers. The server tells you which one to pull for
  each publisher, from the tile size you report; see §6. Publishing simulcast is optional and
  ignoring the recommendation is safe.

### Publishing a screen share with audio

```
screen-{shareId}         <- video track
screen-audio-{shareId}   <- audio track, same shareId
```

Publish both in the **same** `POST .../tracks` call when you have both. You will get one
`TrackPublished` event per track, each with its own `kind`, and both carrying the same `shareId` so
the receiving client can group them into one tile.

---

## 3. Connection lifecycle

The order matters. Doing it out of order is the single most common source of "I can hear them but
they cannot hear me".

```
1. Join the room                -> you get a Snapshot immediately
2. Open a media session         -> you get a mediaSessionId + backend
3. Publish your local tracks    -> POST .../tracks, direction "publish"
4. Refetch the snapshot         -> GET .../snapshot, now that transport exists
5. Subscribe from that snapshot -> POST .../tracks, direction "subscribe"
6. Heartbeat every ~30s         -> keeps you alive AND repairs drift
```

**Step 4 is not redundant, and skipping it breaks screen shares.** The snapshot from step 1 arrives
before you have a peer connection or a media session, because those are created in steps 2 and 3.
Audio usually survives that - a subscribe path that waits for the session will catch up - but there
is nothing to attach a receiving transceiver to yet, so any `shares[]` in that first snapshot are
dropped on the floor and the feature fails silently. Read it again once the transport is up and
subscribe from *that* copy.

**Always publish before you subscribe.** The SFU rejects a pull on a session that has not completed
its own negotiation. The snapshot tells you who is pullable so you can sequence this
correctly with full information.

### 3.1 Join

**Guild channel**
```http
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/join
X-Device-Id: <your device id>
```

**Direct call** - joining is implicit in opening a primary session (step 2). For an incoming call
you first accept:
```http
PUT /api/v1/voice/call/{callId}/accept
X-Device-Id: <your device id>
```

Joining puts you in the roster **before** any media work. It returns the snapshot directly *and*
pushes the same `Snapshot` over SignalR, so you can render the room from the HTTP response without
waiting for any event.

### 3.2 Open a media session

```http
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/session?primary=true
POST /api/v1/voice/calls/{callId}/session?primary=true
```
```json
{ "mediaSessionId": "...", "backend": "cloudflare" }
```

`primary=true` is the session carrying your microphone. Use `primary=false` for a **second session
opened only for a screen share** (a desktop client publishing screen from a separate process). A
secondary session must not take over the call.

`backend` names the SFU behind the session. Nothing else in this document is backend-specific: the
routes, request bodies and responses are all neutral, so the server can change SFU without changing
any of it. Branch on `backend` only where your media layer genuinely differs, and treat an
unrecognised value as "I cannot handle this room" rather than guessing.

> Opening a session does **not** make you audible. You are `Joined`, not `Publishing`, until a track
> exists. Nobody will be told to subscribe to you yet, by design.

### 3.3 Publish

```http
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/tracks
POST /api/v1/voice/calls/{callId}/tracks
```
```json
{
  "mediaSessionId": "...",
  "sessionDescription": { "type": "offer", "sdp": "..." },
  "tracks": [
    { "direction": "publish", "mid": "0", "trackName": "audio" },
    { "direction": "publish", "mid": "1", "trackName": "camera" },
    { "direction": "publish", "mid": "2", "trackName": "screen-abc123" },
    { "direction": "publish", "mid": "3", "trackName": "screen-audio-abc123" }
  ]
}
```

Set your local description first, then send the MIDs. The response is the answer SDP plus per-track
results.

Publishing `audio` is what flips you to `Publishing` and announces you to the room.

### 3.4 Subscribe

Same endpoint, every track `direction: "subscribe"`:

```json
{
  "mediaSessionId": "<your own session>",
  "sessionDescription": { "type": "offer", "sdp": "..." },
  "tracks": [
    { "direction": "subscribe", "mediaSessionId": "<theirs>", "trackName": "audio" }
  ]
}
```

The server retries the publisher-not-ready race for you (up to ~6s). If it still fails you get a
**409** or a **502**, never a 200 - see §8 for which means what.

**Subscribe from your subscription set, not from the roster, whenever you have one.** In a large
room the roster lists everybody and the set lists the handful you should actually pull. See §6.

### 3.5 Renegotiate / close

```http
PUT  .../voice/negotiate     { "mediaSessionId", "sessionDescription" }
POST .../voice/tracks/close { "mediaSessionId", "trackNames": ["screen-abc123"] }
```

Closing `audio` marks you no longer publishing and tells peers to drop you.

### 3.6 Leave

```http
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/leave
PUT  /api/v1/voice/call/{callId}/leave
```

---

## 4. State: snapshots and versions

### 4.1 The snapshot

This is the authoritative state of a room. It is **sufficient on its own** - whatever you missed,
whenever you ask.

```http
GET /api/v1/guilds/{guildId}/channels/{channelId}/voice/snapshot
GET /api/v1/voice/call/{callId}/snapshot
```

```json
{
  "roomId": "channel-123",
  "kind": "channel",
  "guildId": "guild-1",
  "instanceId": "f4904db35c9d4cc0befc8ad9793f33a9",
  "version": 42,
  "participants": [
    {
      "userId": "user-1",
      "mediaSessionId": "cf-abc",
      "audioTrackName": "audio",
      "publishState": "Publishing",
      "isSelfMuted": false,
      "isSelfDeafened": false,
      "isServerMuted": false,
      "isServerDeafened": false,
      "isStreaming": true,
      "shares": [
        {
          "shareId": "abc123",
          "trackNames": ["screen-abc123", "screen-audio-abc123"],
          "mediaSessionId": "cf-def"
        }
      ],
      "joinedAt": "2026-08-07T12:00:00Z"
    }
  ],
  "subscriptions": {
    "mode": "activeSpeaker",
    "revision": 12,
    "activeSpeakers": ["user-1", "user-9"],
    "tracks": [
      {
        "userId": "user-1",
        "mediaSessionId": "cf-abc",
        "trackName": "audio",
        "kind": "audio",
        "shareId": null,
        "layer": null
      }
    ]
  }
}
```

- `publishState` is `"Joined"` or `"Publishing"`. **Only subscribe to participants who are
  `Publishing`.**
- `mediaSessionId` and `audioTrackName` are `null` unless `publishState` is `Publishing`. A session id
  alone is not an invitation to subscribe.
- `shares[].trackNames` tells you exactly which halves of a share exist - video only, or video and
  audio.
- `shares[].mediaSessionId` is the session the share is published on, which is **not** necessarily
  the publisher's microphone session: a desktop client publishing screen from a separate process
  opens a second one. It is `null` on shares published before the server recorded it, where the
  handle is only in the `TrackPublished` event you already received.
- `subscriptions` is **absent or `null` in the ordinary small room**, and present only when the
  server is managing the set. Absent means "pull everyone who is `Publishing`", which is what you
  already do. See §6.
- `guildId` is `null` for calls.

The same object arrives over SignalR as the `Snapshot` event. It is pushed on join, on publish, and
whenever the server decides you are out of date.

### 4.2 Versions - the part you must implement

Every voice event carries `version` and `instanceId`. Track both per room.

```
onEvent(e):
  # Instructions and full state are never version-gated.
  if (e.type == "Resync"):    refetchSnapshot(); return
  if (e.type == "Snapshot"):  applySnapshot(e)
                              held = { instanceId: e.instanceId, version: e.version }
                              return

  if (e.instanceId !== held.instanceId):     # room was rebuilt
      refetchSnapshot(); return

  # Relay events are not versioned state. They carry the current version but do not
  # represent a change to it, so they are applied without advancing anything.
  if (e.type in ["SpeakingChanged", "CameraChanged", "SubscriptionsChanged"]):
      apply(e); return

  if (e.version < held.version):             # strictly older, so genuinely stale
      ignore; return
  if (e.version > held.version + 1):         # gap - you missed something
      refetchSnapshot(); return

  apply(e); held.version = e.version         # equality applies: see batching, below
```

Why each branch exists:

- **`Resync` is never gated.** It is an instruction, not state, and the `roomGone` variant carries
  `instanceId: ""` and `version: 0` precisely because there is no room left to describe. Gating it
  would drop the single most important message in the protocol.
- **`instanceId` mismatch** - the room was destroyed and rebuilt (a Redis loss). Version numbers
  restart from zero, so they can collide with numbers you have already seen behind a completely
  different roster. The instance id is the only reliable signal.
- **Relay events do not advance `held`.** `SpeakingChanged`, `CameraChanged` and
  `SubscriptionsChanged` are pure relays: the server does not store them on the roster and does not
  bump the version for them, so they arrive carrying the version you already hold. Advancing on them
  would let a relay stand in for a state change you actually missed, and the next real event would
  then look contiguous when it is not. `SubscriptionsChanged` is in this list because the
  subscription set moves at conversational frequency - versioning it would make every sentence look
  like a missed roster change to every client in the room. It carries its own `revision` instead;
  see §6.
- **`version < held`** - strictly older only. Events from two server instances can interleave, and
  one arriving late must not overwrite newer state.
- **equality applies** - **one mutation can produce several events.** Publishing a screen share with
  audio is a single request that bumps the version once and then emits one `TrackPublished` per
  track, all at that same version. Treating equal versions as duplicates would drop every track
  after the first, so a share with audio would arrive silent, and a share published alongside a
  camera would lose one of them.
- **gap** - you dropped an event. One refetch and you are correct again. Without this branch, a
  single dropped event leaves you wrong until the session ends.

Because equal versions apply, **event handlers must be idempotent**. They already are in practice:
every one of them sets a value or adds a track by name rather than incrementing anything.

Applying a snapshot: take it wholesale, set `held = {instanceId, version}` from it. Do not merge.

### 4.3 Heartbeat - liveness *and* repair

Every ~30 seconds, over SignalR:

```js
connection.invoke("voice.Heartbeat", roomKind, roomId, {
  knownInstanceId: held.instanceId,
  knownVersion:    held.version,
  mediaSessionId:     myPublishingSessionId ?? null,   // null if not publishing
  audioTrackName:  myPublishingSessionId ? "audio" : null
});
```

`roomKind` is `"channel"` or `"call"`.

This is not just a keepalive. You are **asserting your own state**, and the server reconciles in
both directions:

- you are behind → you get a `Snapshot`
- the server's record of your media is wrong → it is corrected, and peers are told
- the room is gone, or you are not in it → you get `Resync` with `reason: "roomGone"` and must
  rejoin through the normal authorised path

Stop heartbeating and you are swept from the roster after 90 seconds. This applies to calls and
guild channels alike.

Report your real state honestly. If you stopped publishing, send `null`s. The server will correct
its record and tell peers to drop you.

**A dropped SignalR connection does not remove you from the room.** SignalR reconnects by itself,
and losing the socket for a few seconds is not a departure - a disconnect only shortens your
liveness to 45 seconds, and reconnecting restores it before your next heartbeat is even due. Two
things follow for the client:

- Do **not** tear down your PeerConnection or your media session because the hub connection
  dropped. They are independent: media rides its own transport, and rebuilding it on every blip is
  how a healthy session ends up spending its session id (see §8, `sessionGone`).
- **Do** send a heartbeat as soon as you reconnect, rather than waiting for the next tick of your
  30-second timer. That is also when you find out whether anything changed while you were away.

If you stay gone past the grace window you are evicted by the sweep like any other silent client,
and everyone is told.

---

## 5. Events

Prefix with `guild.voice.` or `call.`. Every payload also carries the room id field
(`channelId`/`callId`), `instanceId` and `version`.

| Event | Payload (beyond the envelope) | Meaning |
|---|---|---|
| `Snapshot` | *the full snapshot object* | Authoritative state. Replace everything. **Shape exception:** this is the bare snapshot, so it has `roomId` and no `channelId`/`callId`. A client that routes events by room-id field will drop it. |
| `Resync` | `reason`, sometimes `userId` | Refetch the snapshot. `reason` is `roomGone`, `participantLeft` or `peerPublishChanged`. |
| `ParticipantJoined` | `userId`, `mediaSessionId`, `audioTrackName` | This user is now **pullable**. Subscribe to them. |
| `TrackPublished` | `userId`, `mediaSessionId`, `trackName`, `kind`, `shareId` | A camera or screen track appeared. |
| `TrackClosed` | `userId`, `trackName`, `shareId` | Drop that track. |
| `MuteChanged` | `userId`, `isMuted`, `serverForced` | `serverForced: true` means a moderator did it. |
| `DeafenChanged` | `userId`, `isDeafened`, `serverForced` | |
| `CameraChanged` | `userId`, `isCameraOn` | Relay only, not stored. |
| `SpeakingChanged` | `userId`, `isSpeaking` | Relay only. High frequency; do not persist. |
| `ScreenShareStarted` | `userId`, `shareId` | |
| `ScreenShareStopped` | `userId`, `shareId` | |
| `ShareViewersChanged` | `shareId`, `viewerCount`, `viewerIds` | |
| `SubscriptionsChanged` | `mode`, `revision`, `activeSpeakers`, `subscriptions` | **What you should now be pulling.** Sent without any user action. Relay only; see §6. |

**`ParticipantJoined` is never sent for someone who has merely opened a session.** If you receive
it, the track exists and the subscribe will work.

Guild-only:

| Event | Payload | Meaning |
|---|---|---|
| `UserJoinedVoice` / `UserLeftVoice` | `userId`, `channelId`, `guildId` | Guild-wide, sent to all members so the channel list can show who is in voice. Not room state. |
| `KickedByOtherDevice` | `channelId`, `guildId` | You joined from another device. Tear down. |
| `MovedToChannel` | `channelId`, `guildId`, `movedBy` | A moderator moved you. Rejoin the new channel. |

Call-only: `IncomingCall`, `CallEnded`, plus `conversation.CallStateChanged` for members of the
conversation who are not in the call.

### Client → server (hub invocations)

```
voice.Heartbeat(roomKind, roomId, state)

guild.voice.MuteChanged({ channelId, isMuted })
guild.voice.DeafenChanged({ channelId, isDeafened })
guild.voice.CameraChanged({ channelId, isCameraOn })
guild.voice.ScreenShareStarted({ channelId, shareId })
guild.voice.ScreenShareStopped({ channelId, shareId })
guild.voice.ServerMute({ channelId, targetUserId, isMuted })      // needs MuteMembers
guild.voice.ServerDeafen({ channelId, targetUserId, isDeafened })  // needs DeafenMembers
guild.voice.MoveUser({ channelId, targetUserId, targetChannelId }) // needs MoveMembers

call.MuteChanged({ callId, isMuted })
call.CameraChanged({ callId, isCameraOn })
call.SpeakingChanged({ callId, isSpeaking })
call.ScreenShareStarted({ callId, shareId })
call.ScreenShareStopped({ callId, shareId })
```

The server sets the acting user from your authenticated connection. Any `userId` you send is
overwritten. You can only ever change your own state (except the moderation commands, which name a
target and check permissions).

---

## 6. Subscription sets

This section is new, and it is the one behavioural contract in this document that changed without
any API signature moving.

### 6.1 Why it exists

The SFU bills egress, so a room costs `subscribers x bitrate x minutes`. All-to-all subscription
makes that quadratic: fifty people with their microphones on is 2,450 concurrent streams and roughly
**35 GB an hour**, which is more than a ten-person 1080p screenshare. Subscribing each client to the
people actually talking turns it into `n x k` and roughly **4 GB an hour** for the same room.

Nobody can hear five people at once anyway. This is what every large-call product does.

### 6.2 What changes for you

**The ordinary small room is completely unaffected.** At or below the configured threshold (10
participants by default), with nobody pinning, pausing or hitting the video publisher cap, the
server sends no subscription set at all and "subscribe to everyone who is `Publishing`" remains
exactly right. If you never implement this section, nothing breaks.

A set is sent when there is something to say, which is either of:

- the room is **above the threshold**, so audio is ranked (`mode: "activeSpeaker"`); or
- something is being **withheld** regardless of room size - a collapsed tile, a backgrounded client,
  a screen share's audio nobody asked for, a publisher past the video cap. Then `mode` is still
  `"all"` but the set is still authoritative. A four-person call where you minimised a 1080p share is
  exactly this case, and it is where a minimised share costs the most per person.

When you are sent a set:

- **It changes on its own.** Nobody clicked anything. The conversation moved.
- **It is per-recipient.** Your set is not your neighbour's, because your pins, your collapsed tiles
  and your tile sizes are yours.
- **It only ever removes.** A set never asks you to pull something the all-to-all behaviour would
  not also have asked for, so applying one can never break a subscription that was valid.

You receive it two ways, and they are the same object:

- `subscriptions` on the **snapshot** (`GET .../snapshot`, and the pushed `Snapshot` event).
- the **`SubscriptionsChanged`** event, whenever it changes.

```json
{
  "mode": "activeSpeaker",
  "revision": 12,
  "activeSpeakers": ["user-1", "user-9"],
  "tracks": [
    { "userId": "user-1", "mediaSessionId": "cf-abc", "trackName": "audio",
      "kind": "audio", "shareId": null, "layer": null },
    { "userId": "user-4", "mediaSessionId": "cf-xyz", "trackName": "screen-abc123",
      "kind": "screen", "shareId": "abc123", "layer": "h" }
  ]
}
```

The snapshot's `subscriptions` object and the `SubscriptionsChanged` payload have the same fields,
so one parser handles both. The event additionally carries the usual room-id, `instanceId` and
`version` envelope, which you ignore for this event - see §4.2.

| Field | Meaning |
|---|---|
| `mode` | `"all"` or `"activeSpeaker"`. It describes **audio ranking only**. `"all"` does not mean the set can be ignored - if you were sent one, something is being withheld. |
| `revision` | Increases whenever the ranked set changes. **Ignore a payload whose `revision` is lower than one you have already applied** - two server instances can interleave. It is unrelated to `version`. |
| `activeSpeakers` | The ranked set, for rendering. Not the same as your track list, which also carries your pins. |
| `tracks` | Everything to pull, complete. Not a delta. |
| `layer` | Simulcast layer for a video track: `"q"` low, `"h"` medium, `"f"` full. `null` for audio. Pull that layer when your transport can ask for one; ignoring it is safe and simply costs full resolution. |
| `mediaSessionId` | May be `null` for a share published before the server recorded its session. Use the handle you already have from `TrackPublished`. Never `null` for audio. |

### 6.3 What to do with it

1. **Diff it against what you are currently pulling.** Subscribe to what is new, close what is gone.
2. **Do not tear down and rebuild.** A set change usually moves one or two entries; rebuilding the
   whole PeerConnection turns a cheap renegotiation into a reconnect.
3. **Keep rendering everybody.** The roster is still the roster. Someone you are not subscribed to is
   still in the room, still shown, still has a mute state - you just are not pulling their audio.
4. **`activeSpeakers` is the ordering hint** if your UI wants to surface who is talking.
5. **On a snapshot, take the set wholesale**, exactly like the roster.

### 6.4 Telling the server what you can actually see

Everything here only ever *reduces* what you are sent, which is why it needs no permission beyond
speaking for yourself. All fields are optional, and an omitted field is left alone.

| Field | Effect |
|---|---|
| `paused` | Your client is backgrounded or hidden. **Drops video, never audio.** A backgrounded tab is still in the conversation. |
| `pinned` | Publishers to keep subscribed whatever the ranking says. Capped (3 by default). |
| `pausedPublishers` | Publishers whose tile you have collapsed. Video only, same as `paused`. |
| `tileHeights` | Publisher id to the height in **device pixels** of the largest tile you draw them in. Drives the simulcast layer. |
| `screenAudioShares` | Share ids whose audio half you want. **Screenshare audio is off by default** - most shares carry none, and distributing it doubles the stream count of the most expensive thing in a room. Ask for it when the user unmutes a share. |

Report a tile size when it changes materially, not on every animation frame. Report `paused` on
`visibilitychange`.

> **Not routed yet.** The server implements this, but no HTTP or hub endpoint is exposed for it in
> this release, so there is nothing to call today. The set you receive is computed from speech, room
> size and defaults only. This table is here so client work can be planned against the shape it will
> have; the endpoint lands with the enforcement change below.

### 6.5 Speaking reports are the input

Active-speaker ranking has exactly one input: `SpeakingChanged` from clients. A room whose clients
never report speech has no basis for ranking and correctly stays `mode: "all"`, so nothing is lost -
but nothing is saved either.

**Apply voice-activity hysteresis before you report.** The server admits a speaker the instant it is
told, deliberately, because gating entry on duration means the first person to talk in a quiet room
is inaudible for seconds. The consequence is that an un-hysteresised cough costs every subscriber in
the room a renegotiation. Debounce on your side; that is where it belongs.

Speaking is currently reported for calls (`call.SpeakingChanged`) and not for guild voice channels.
Until a guild equivalent exists, guild channels stay `mode: "all"` regardless of size.

### 6.6 Enforcement

Today the set is **advisory**: a client that ignores it and subscribes to everybody is not refused,
and nothing about voice behaves differently for it. When enforcement is switched on, a subscribe for
a track your set does not include is answered with the same **409** shape as a stale subscription -
`refetchSnapshot` - and the recovery is the same: refetch, reconcile, subscribe from the new set.
Implement §6.3 before that happens.

---

## 7. Screen share viewers

Watching is announced explicitly, because a subscribe is not a reliable signal - it has no teardown
a client is obliged to send, and a hidden or paused stream stays subscribed.

```http
POST   .../voice/shares/{shareId}/watch          # or /voice/call/{callId}/shares/{shareId}/watch
DELETE .../voice/shares/{shareId}/watch
GET    .../voice/shares/viewers                  # { shareId: [userId, ...] }
```

- Re-`POST` on the same timer as your heartbeat. A claim expires after 90 seconds.
- `DELETE` when the user closes, minimises or navigates away from the stream.
- **Also close your subscription** when you unwatch. Viewer counts are cheap; egress is not.

Stop heartbeating and you are evicted from the room after 90 seconds, in both room kinds.

---

## 8. Errors

| Status | Meaning | What to do |
|---|---|---|
| **409** | `{ error: "staleSubscription", tracks?, action: "refetchSnapshot" }`. You subscribed to media nobody is publishing - the share stopped, or the publisher never started. | Refetch the snapshot and reconcile. **Do not retry the same body** - the track is gone, not late. Roll back your subscribe guard. |
| **409** | `{ error: "sessionGone", action: "recreateSession" }`. Your *own* media session has no live PeerConnection - it was closed, or never reached `connected`. | `POST .../voice/session` for a fresh `mediaSessionId`, then republish and re-subscribe. **Do not retry the same body**: that session is spent and every call on it fails identically. |
| **502** | The media transport rejected the operation. Body: `{ operation, error }`. | Real failure. Roll back any local "subscribed" flag for that peer and back off before retrying. **Do not** treat as success. |
| **503** | The room was contended and your change was not applied. | Retry after a short delay. This is transient, not a server fault. |
| **403** | Not permitted (missing `Connect`/`Speak`/`Stream`, not a participant, or acting as a session you do not own). | Do not retry blindly. |
| **404** | Room or call does not exist - **or you forgot the gateway prefix**, see §10. | Stop, rejoin from scratch. |

**Critical:** if a subscribe fails - 409 or 502 - roll back whatever guard you use to dedupe
subscriptions per user. A guard that is consumed by a failed attempt and never released is how one transient error
becomes permanent silence for that participant.

**On `sessionGone` specifically:** the two ways to earn it are worth knowing, because both are
avoidable. You get it if you keep a `mediaSessionId` across a PeerConnection teardown - a session id
outlives the connection that gave it meaning, so a rebuilt `RTCPeerConnection` needs a new one - and
you get it if you pull a remote track before your own publish handshake has reached `connected`. The
SFU will not set up a receiver on a session that is not connected yet. Wait for
`pc.connectionState === "connected"` before your first subscribe.

---

## 9. Rules that will bite you if you skip them

1. **Publish before you subscribe.** Otherwise the SFU rejects the pull.
2. **Only subscribe to `publishState: "Publishing"`.** A session id alone is not enough.
3. **Roll back the dedupe guard on failure.** See §8.
4. **Handle version gaps.** Without this, one dropped event is permanent.
5. **Compare `instanceId` before `version`.** A rebuilt room reuses low version numbers.
6. **Send `X-Device-Id`** on join, accept, decline, leave and session creation. One user is in one
   room on one device at a time; joining elsewhere kicks the old device via
   `KickedByOtherDevice`.
7. **Heartbeat with your real state.** It is the repair channel, not just a keepalive.
8. **Unwatch and unsubscribe** when a stream is not visible.
9. **Screen audio is a separate track.** Group by `shareId`, do not assume it exists.
10. **A hub reconnect is not a rejoin.** Do not tear down media because the SignalR connection
    blipped; heartbeat as soon as it is back. See §4.3.
11. **A new PeerConnection needs a new media session.** Reusing the old `mediaSessionId` earns
    `sessionGone` on every call from then on. See §8.
12. **Honour your subscription set if you are sent one, and expect it to change without anybody
    doing anything.** See §6. A client that ignores it still works; it just pays for streams nobody
    is listening to, and it will start getting 409s once enforcement is on.
13. **Debounce voice-activity detection before reporting `SpeakingChanged`.** It is the sole input
    to active-speaker ranking, and an un-debounced one costs the whole room a renegotiation. See
    §6.5.

---

## 10. Reference: endpoints

> **All paths below are service-internal.** The public surface is behind the gateway, which strips
> a service prefix: prepend `/api/v1/guild` for guild routes and `/api/v1/messaging` for call
> routes, replacing their own `/api/v1`. So `/api/v1/guilds/{g}/channels/{c}/voice` is called as
> `/api/v1/guild/guilds/{g}/channels/{c}/voice`, and `/api/v1/voice/calls/{id}/tracks` as
> `/api/v1/messaging/voice/calls/{id}/tracks`. Every example in this document omits the prefix for
> readability; a client that copies them literally gets a 404.

### Guild voice
```
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/join
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/leave
GET    /api/v1/guilds/{guildId}/channels/{channelId}/voice            (same as /voice/snapshot)
GET    /api/v1/guilds/{guildId}/channels/{channelId}/voice/snapshot
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/session?primary=
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/tracks
PUT    /api/v1/guilds/{guildId}/channels/{channelId}/voice/negotiate
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/tracks/close
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/shares/{shareId}/watch
DELETE /api/v1/guilds/{guildId}/channels/{channelId}/voice/shares/{shareId}/watch
GET    /api/v1/guilds/{guildId}/channels/{channelId}/voice/shares/viewers
```

### Calls
```
POST   /api/v1/voice/call                                   place a call
PUT    /api/v1/voice/call/{callId}/accept|decline|leave|end
GET    /api/v1/voice/call/{callId}                          call + ring state
GET    /api/v1/voice/call/pending                           am I being rung? (204 if not)
GET    /api/v1/voice/conversations/{conversationId}/call     is a call happening here?
GET    /api/v1/voice/call/{callId}/snapshot                 media state
POST   /api/v1/voice/calls/{callId}/session?primary=
POST   /api/v1/voice/calls/{callId}/tracks
PUT    /api/v1/voice/calls/{callId}/negotiate
POST   /api/v1/voice/calls/{callId}/tracks/close
POST   /api/v1/voice/call/{callId}/shares/{shareId}/watch
DELETE /api/v1/voice/call/{callId}/shares/{shareId}/watch
GET    /api/v1/voice/call/{callId}/shares/viewers
GET    /api/v1/voice/ice-servers
```

Note the call SFU routes are under `/voice/calls/{callId}/` (plural) while the lifecycle routes are
under `/voice/call/{callId}/` (singular). That is historical; both are correct as written.

---

## 11. Worked example

```js
// 1. Join. The snapshot arrives over SignalR before this resolves in practice.
await api.post(`/api/v1/guilds/${guildId}/channels/${channelId}/voice/join`, null,
               { headers: { "X-Device-Id": deviceId } });

// 2. Session.
const { mediaSessionId, backend } = await api.post(
  `/api/v1/guilds/${guildId}/channels/${channelId}/voice/session?primary=true`);

// 3. Publish mic (+ camera, + screen with audio if you have them).
const pc = new RTCPeerConnection({ iceServers });
const micTx = pc.addTransceiver(micTrack, { direction: "sendonly" });
await pc.setLocalDescription(await pc.createOffer());

const publish = await api.post(`.../voice/tracks`, {
  mediaSessionId,
  sessionDescription: { type: "offer", sdp: pc.localDescription.sdp },
  tracks: [{ direction: "publish", mid: micTx.mid, trackName: "audio" }],
});
await pc.setRemoteDescription(publish.sessionDescription);

// 4. Refetch: the join snapshot predates the transport, so its shares are unusable.
const current = await api.get(`.../voice/snapshot`);

// 5. Subscribe to everyone already publishing, from the *refetched* snapshot.
for (const p of current.participants) {
  if (p.userId === me || p.publishState !== "Publishing") continue;
  await subscribeTo(p.mediaSessionId, p.audioTrackName);
}

// 6. Heartbeat.
setInterval(() => connection.invoke("voice.Heartbeat", "channel", channelId, {
  knownInstanceId: held.instanceId,
  knownVersion: held.version,
  mediaSessionId,
  audioTrackName: "audio",
}), 30_000);
```

---

## 12. There is no legacy surface

The previous implementation has been removed outright, not deprecated: no old routes, no old
response shape, no liveness-only `guild.voice.Heartbeat`, and no adoption of pre-existing cache
keys. A client written against an older description of this API will not work.

That is deliberate. Carrying a compatibility layer for an API with no users costs more than it
saves, and the two shims that mattered - a response that withheld the media handles, and a
heartbeat that could not repair anything - are the exact things that made voice unrecoverable.
