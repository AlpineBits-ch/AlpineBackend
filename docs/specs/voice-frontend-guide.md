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

**The second idea:** every event carries a `version`. If you receive a version that is not exactly
one more than the one you hold, you missed something. Refetch the snapshot. This is the mechanism
that makes voice recoverable, and a client that ignores it will eventually show a stale roster
forever.

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
4. Subscribe to everyone else   -> POST .../tracks, direction "subscribe"
5. Heartbeat every ~30s         -> keeps you alive AND repairs drift
```

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
**502**, not a 200 - see §7.

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
        { "shareId": "abc123", "trackNames": ["screen-abc123", "screen-audio-abc123"] }
      ],
      "joinedAt": "2026-08-07T12:00:00Z"
    }
  ]
}
```

- `publishState` is `"Joined"` or `"Publishing"`. **Only subscribe to participants who are
  `Publishing`.**
- `mediaSessionId` and `audioTrackName` are `null` unless `publishState` is `Publishing`. A session id
  alone is not an invitation to subscribe.
- `shares[].trackNames` tells you exactly which halves of a share exist - video only, or video and
  audio.
- `guildId` is `null` for calls.

The same object arrives over SignalR as the `Snapshot` event. It is pushed on join, on publish, and
whenever the server decides you are out of date.

### 4.2 Versions - the part you must implement

Every voice event carries `version` and `instanceId`. Track both per room.

```
onEvent(e):
  if (e.instanceId !== held.instanceId):    # room was rebuilt
      refetchSnapshot(); return
  if (e.version <= held.version):           # duplicate or out of order
      ignore; return
  if (e.version > held.version + 1):        # gap - you missed something
      refetchSnapshot(); return
  apply(e); held.version = e.version
```

Why each branch exists:

- **`instanceId` mismatch** - the room was destroyed and rebuilt (a Redis loss). Version numbers
  restart from zero, so they can collide with numbers you have already seen behind a completely
  different roster. The instance id is the only reliable signal.
- **`version <= held`** - events from two server instances can interleave; an older one arriving
  late must not overwrite newer state.
- **gap** - you dropped an event. One refetch and you are correct again. Without this branch, a
  single dropped event leaves you wrong until the session ends.

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

Stop heartbeating and you are swept from the roster after 90 seconds.

Report your real state honestly. If you stopped publishing, send `null`s. The server will correct
its record and tell peers to drop you.

---

## 5. Events

Prefix with `guild.voice.` or `call.`. Every payload also carries the room id field
(`channelId`/`callId`), `instanceId` and `version`.

| Event | Payload (beyond the envelope) | Meaning |
|---|---|---|
| `Snapshot` | *the full snapshot object* | Authoritative state. Replace everything. |
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
guild.voice.ScreenShareStarted({ channelId, shareId, trackName })
guild.voice.ScreenShareStopped({ channelId, shareId })
guild.voice.ServerMute({ channelId, targetUserId, isMuted })      // needs MuteMembers
guild.voice.ServerDeafen({ channelId, targetUserId, isDeafened })  // needs DeafenMembers
guild.voice.MoveUser({ channelId, targetUserId, targetChannelId }) // needs MoveMembers

call.MuteChanged({ callId, isMuted })
call.CameraChanged({ callId, isCameraOn })
call.SpeakingChanged({ callId, isSpeaking })
call.ScreenShareStarted({ callId, shareId, trackName })
call.ScreenShareStopped({ callId, shareId })
```

The server sets the acting user from your authenticated connection. Any `userId` you send is
overwritten. You can only ever change your own state (except the moderation commands, which name a
target and check permissions).

---

## 6. Screen share viewers

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

---

## 7. Errors

| Status | Meaning | What to do |
|---|---|---|
| **502** | The media transport rejected the operation. Body: `{ operation, error }`. | Real failure. Roll back any local "subscribed" flag for that peer and retry later. **Do not** treat as success. |
| **503** | The room was contended and your change was not applied. | Retry after a short delay. This is transient, not a server fault. |
| **403** | Not permitted (missing `Connect`/`Speak`/`Stream`, not a participant, or acting as a session you do not own). | Do not retry blindly. |
| **404** | Room or call does not exist. | Stop, rejoin from scratch. |

**Critical:** if a subscribe fails, roll back whatever guard you use to dedupe subscriptions per
user. A guard that is consumed by a failed attempt and never released is how one transient error
becomes permanent silence for that participant.

---

## 8. Rules that will bite you if you skip them

1. **Publish before you subscribe.** Otherwise the SFU rejects the pull.
2. **Only subscribe to `publishState: "Publishing"`.** A session id alone is not enough.
3. **Roll back the dedupe guard on failure.** See §7.
4. **Handle version gaps.** Without this, one dropped event is permanent.
5. **Compare `instanceId` before `version`.** A rebuilt room reuses low version numbers.
6. **Send `X-Device-Id`** on join, accept, decline, leave and session creation. One user is in one
   room on one device at a time; joining elsewhere kicks the old device via
   `KickedByOtherDevice`.
7. **Heartbeat with your real state.** It is the repair channel, not just a keepalive.
8. **Unwatch and unsubscribe** when a stream is not visible.
9. **Screen audio is a separate track.** Group by `shareId`, do not assume it exists.

---

## 9. Reference: endpoints

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

## 10. Worked example

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

// 4. Subscribe to everyone already publishing, from the snapshot.
for (const p of snapshot.participants) {
  if (p.userId === me || p.publishState !== "Publishing") continue;
  await subscribeTo(p.mediaSessionId, p.audioTrackName);
}

// 5. Heartbeat.
setInterval(() => connection.invoke("voice.Heartbeat", "channel", channelId, {
  knownInstanceId: held.instanceId,
  knownVersion: held.version,
  mediaSessionId,
  audioTrackName: "audio",
}), 30_000);
```

---

## 11. There is no legacy surface

The previous implementation has been removed outright, not deprecated: no old routes, no old
response shape, no liveness-only `guild.voice.Heartbeat`, and no adoption of pre-existing cache
keys. A client written against an older description of this API will not work.

That is deliberate. Carrying a compatibility layer for an API with no users costs more than it
saves, and the two shims that mattered - a response that withheld the media handles, and a
heartbeat that could not repair anything - are the exact things that made voice unrecoverable.
