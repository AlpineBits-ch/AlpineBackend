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

A participant can publish up to four kinds of track at once. The **track name** is what peers and
the roster agree on, so the naming convention is the contract. Set it on the SDK publication; do not
let the SDK pick one for you.

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

Declare both in the **same** `POST .../voice/publish` call when you have both. You will get one
`TrackPublished` event per track, each with its own `kind`, and both carrying the same `shareId` so
the receiving client can group them into one tile.

---

## 3. Connection lifecycle

**The SFU is LiveKit, and your client talks to it directly.** This is the one thing that changed and
everything else in this section follows from it. There is no SDP relay any more: you do not send
offers to this backend, you do not receive answers from it, and you do not name transceiver MIDs to
anybody. You connect to the node with the `livekit-client` SDK and it owns the peer connection,
renegotiation and reconnect.

What this backend still does is decide **whether** you may connect, **what** you may send, and keep
the roster the rest of the product reads from - the channel list showing who is in voice, the share
viewer counts, the mute state, the subscription plan.

```
1. Join the room                -> you get a Snapshot immediately
2. Get a connection             -> POST .../voice/connection -> { url, token }
3. room.connect(url, token)     -> the SDK does the rest
4. Publish through the SDK, then declare it -> POST .../voice/publish
5. Subscribe through the SDK    -> from the snapshot, honouring your subscription set
6. Heartbeat every ~30s         -> keeps you alive AND repairs drift
```

Two traps that used to exist are gone with the negotiation. You no longer have to publish before you
subscribe - the SDK sequences that itself - and you no longer have to refetch the snapshot before
subscribing to a share, because there is no transceiver of yours that has to exist first. Read the
snapshot when you like; it is authoritative whenever you ask.

### 3.1 Join

**Guild channel**
```http
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/join
X-Device-Id: <your device id>
```

**Direct call** - joining is implicit in taking a primary connection (step 2). For an incoming call
you first accept:
```http
PUT /api/v1/voice/call/{callId}/accept
X-Device-Id: <your device id>
```

Joining puts you in the roster **before** any media work. It returns the snapshot directly *and*
pushes the same `Snapshot` over SignalR, so you can render the room from the HTTP response without
waiting for any event.

### 3.2 Get a connection

```http
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/connection?primary=true
POST /api/v1/voice/calls/{callId}/connection?primary=true
```
```json
{
  "backend": "livekit",
  "url": "wss://sfu-fsn1.venta.gg",
  "token": "eyJhbGciOiJIUzI1NiIs...",
  "room": "channel-123",
  "identity": "user-1",
  "mediaSessionId": "user-1",
  "expiresAt": "2026-08-16T12:10:00Z",
  "canPublishAudio": true,
  "canPublishVideo": true
}
```

Then:

```js
import { Room, RoomEvent } from "livekit-client";

const room = new Room({ adaptiveStream: true, dynacast: true });
room.on(RoomEvent.TrackSubscribed, (track, _pub, participant) => attach(track, participant));
await room.connect(url, token);
await room.localParticipant.setMicrophoneEnabled(true);
```

`adaptiveStream` drops video quality for elements that are off-screen or small, and `dynacast` stops
publishing layers nobody is subscribed to. Both cut bandwidth meaningfully and are safe defaults.

Fields worth understanding:

- **`url` is the node your room is on.** There is no shared hostname in front of the fleet: a room
  lives on exactly one node, and this field is the routing answer. Do not cache it against a room id
  and do not derive it yourself - ask again.
- **`token` is short-lived** (minutes, not hours) and is only consulted while the WebSocket is
  opened. Once you are connected it does not matter that it expires. If you took too long, or you
  are reconnecting after a network change, call this route again - it is cheap, it does not touch
  the roster, and only the token is new.
- **`identity`** is how the SFU names you, and it is the same handle the roster records as
  `mediaSessionId`. Both fields carry it so an existing client can adopt the new name without
  changing its snapshot handling on the same day.

  **It is the bare user id, not `user-{userId}`.** The `user-1` above is a user id in this
  example, not a prefix. A secondary connection is `{userId}#{tag}` - `user-1#screen` - and the
  `#` separator is guaranteed: user ids are Sqids and never contain one, and the tag is stripped
  to alphanumerics before it is appended, so splitting on the first `#` always recovers the user.
  You can map a remote LiveKit participant to a user without consulting the snapshot.
- **`canPublishAudio` / `canPublishVideo`** are what the token actually grants. Render your
  microphone and camera buttons from these rather than from a permission you computed locally: a
  member whose plan has no video left connects, hears everyone, and cannot turn a camera on however
  the client is patched.
- **`primary=false`** is for a *second* connection opened only for a screen share - a desktop client
  publishing screen from a separate process. It matters more than it looks: the SFU keys
  participants by identity and disconnects an earlier session that reappears under the same one, so
  a secondary connection minted as primary would kick your own call off the air. Pass
  `?primary=false&tag=screen` and you get a distinct identity.

  `tag` is free-form, not an allow-list: it is stripped to letters and digits, truncated to 32
  characters, and falls back to `alt` if nothing survives. `tag=view` for a video-only connection
  is fine. One tag per connection per user, though - two connections sharing a tag share an
  identity, and the second evicts the first.

> Taking a connection does **not** make you audible. You are `Joined`, not `Publishing`, until a
> track exists and you have declared it in step 4.

#### Reconnecting

**Resume with the token and URL you already have.** The SDK's own reconnect path reuses them and
that is correct here: a room is placed on a node once and is never moved while it exists, so the URL
you connected with cannot become the wrong one. The registry row is only ever dropped for a room the
SFU itself no longer has - and a room the SFU no longer has is one there is nothing to resume into.

The token is the only thing that can expire under you. It is good for ten minutes by default
(`LIVEKIT_JOIN_TOKEN_TTL_SECONDS`), which outlasts any normal reconnect ladder, so the rule is:

1. Resume with the cached token.
2. If the resume is refused as unauthorised, or you have been disconnected longer than the TTL -
   a laptop sleeping, a long tunnel - call `POST .../voice/connection` again and do a full connect.

Re-fetching is cheap and safe at any point: it does not touch the roster, does not re-announce you,
and does not disturb a connection you still hold. Only the token is new. Do **not** pre-emptively
re-fetch on every attempt of a reconnect ladder - you will be minting tokens at the SFU's retry rate
to solve a problem you have not got.

### 3.3 Publish through the SDK

Publish with the SDK, setting the track **name** to the convention in §2 - the name is what peers
and the roster agree on:

```js
await room.localParticipant.setMicrophoneEnabled(true);           // name: "audio"
await room.localParticipant.setScreenShareEnabled(true, { audio: true });
```

For a screen share, publish the two halves under `screen-{shareId}` and `screen-audio-{shareId}` so
the receiving client can group them into one tile.

### 3.4 Declare what you published

```http
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/publish
POST /api/v1/voice/calls/{callId}/publish
```
```json
{
  "trackNames": ["screen-abc123", "screen-audio-abc123"],
  "video": { "height": 1080, "framerate": 60 }
}
```
```json
{ "identity": "user-1", "rung": "1080p60", "height": 1080, "framerate": 60, "maxLayer": null }
```

`maxLayer` is the best simulcast layer of *your* video that the room will distribute to anybody,
in the same vocabulary as `layer` on a subscription set (§6.7). **`null` is the ordinary case** and
means nothing caps you - every publish inside its rung, and every publish that declared no size.
A non-null value means you declared more than your plan allows: you are still publishing, but no
viewer will be served above that layer however large their tile is. Re-encode to your `rung` and
declare it again and it goes back to `null`.

This is what puts you on the roster as publishing, announces you to the room, and feeds the share
viewer counts and the usage meter. **The media does not depend on it** - you are already publishing
by the time you call it - but nothing else in the product can see you until you do.

`video` describes what you intend to send and is optional; absent means "whatever the room allows".
It is only read when the body carries video, because the video ceiling has never had anything to say
about a microphone.

Two answers you must handle:

- **200 with a `degradations` array.** You asked for more than your plan allows and were clamped.
  `granted.rung` tells you what to re-encode to. See the entitlements guide.
- **403 with a denial body.** There was nothing below what you asked for - a granted rung of `none`,
  or no publisher slot free. Stop the local track: your video will not reach anybody, because the
  token you connected with does not permit it either.

### 3.5 Declare a resolution change, and unpublish

```http
PUT  .../voice/video      { "height": 1080, "framerate": 60 }
POST .../voice/unpublish  { "trackNames": ["screen-abc123"] }
```

`PUT .../video` is for a client that changes what it sends **without republishing** - a screen share
that switches source, a camera that changes resolution. It replaces the `video` field the old
renegotiate route carried, for the same reason: a quality ceiling computed once at publish time is
one that a later resolution change walks straight past. It never refuses anything.

Declaring nothing leaves your ceiling exactly where your last publish put it. An unchanged
resolution needs no call at all.

`unpublish` marks the tracks closed and tells peers to drop them. Unpublishing `audio` marks you no
longer publishing.

### 3.6 Leave

```http
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/leave
PUT  /api/v1/voice/call/{callId}/leave
```

Disconnect the SDK room as well. The roster does not wait for the SFU and the SFU does not wait for
the roster; both want telling.


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
      "mediaSessionId": "user-1",
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
          "mediaSessionId": "user-1#screen"
        }
      ],
      "videoTracks": [
        {
          "trackName": "camera",
          "mediaSessionId": "user-1#camera"
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
        "mediaSessionId": "user-1",
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
- `videoTracks[]` is their cameras: every published track that is neither the microphone nor part of
  a share. **Render from this, not only from `TrackPublished`.** A camera used to be announced and
  described nowhere else, so a client that was not listening at that moment - joined after it was
  already on, resynced, reconnected - drew a black tile until the publisher toggled it. This is what
  gives a camera the same recovery path everything else already had.
- `videoTracks[].mediaSessionId` is the session the camera is published on, which need not be the
  publisher's microphone session, exactly as for shares. It is `null` on tracks published before the
  server recorded it - do **not** fall back to `mediaSessionId`, since that names a track the
  microphone session does not have.
- `videoTracks[]` is **not** gated on `publishState`, for the same reason `shares[]` is not: a camera
  is only listed while it is published, and closing it removes it.
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
liveness to 75 seconds, and reconnecting restores it before your next heartbeat is even due. The
window is sized to outlast the reconnect ladder of the clients we ship, so a client that is still
retrying is never swept while it retries. Two things follow for the client:

- Do **not** tear down your SFU connection because the hub connection dropped. They are entirely
  independent transports - the hub carries roster events, the SFU carries media - and the SDK has
  its own reconnect for its own socket. Rebuilding media on a hub blip drops audio for a fault
  that was never on the media path.
- **Do** send a heartbeat as soon as you reconnect, rather than waiting for the next tick of your
  30-second timer. That is also when you find out whether anything changed while you were away.

A disconnect caused by the *gateway* going away - a rollout, a pod recycle - opens no window at
all. Every socket on the instance closes in the same second and none of those closures say anything
about the clients behind them, so nothing is shortened and your ordinary 90-second liveness stands.

**If you are evicted, you are told.** The sweep sends the evicted participant `Resync` with
`reason: "roomGone"`, individually, and everyone still in the room `reason: "participantsEvicted"`.
Handle the first the same way you handle any other `roomGone`: you are not in a room any more, stop
treating yourself as joined, and rejoin through the authorised path if the user asks. Do not rely on
noticing this some other way - before this existed, an evicted client had no signal at all and went
on publishing into a room that had forgotten it, with every subsequent request answering 404.

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
| `PublishCapped` | `degradations` | **Only to you.** What the SFU sees you sending is above your plan - or, when the array is empty, no longer is. Not a reply to anything you sent: it is a measurement taken on a timer. Entitlements guide §8.3. |
| `ShareViewersChanged` | `shareId`, `viewerCount`, `viewerIds` | |
| `SubscriptionsChanged` | `mode`, `revision`, `activeSpeakers`, `tracks` | **What you should now be pulling.** Flattened onto the envelope - the track list is `tracks`, not nested under a `subscriptions` key. Sent without any user action. Relay only; see §6. |

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
guild.voice.SpeakingChanged({ channelId, isSpeaking })
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
    { "userId": "user-1", "mediaSessionId": "user-1", "trackName": "audio",
      "kind": "audio", "shareId": null, "layer": null },
    { "userId": "user-4", "mediaSessionId": "user-2", "trackName": "screen-abc123",
      "kind": "screen", "shareId": "abc123", "layer": "b" }
  ]
}
```

The snapshot's `subscriptions` object and the `SubscriptionsChanged` payload have the same **fields**,
so one parser handles the object itself. They differ in **nesting**, and this is worth reading twice:

- On a **snapshot**, the object is nested: `snapshot.subscriptions.tracks`. The whole
  `subscriptions` key is absent when no set is in force.
- On the **event**, the fields are flattened straight onto the envelope: `payload.tracks`,
  `payload.mode`, alongside the usual room-id, `instanceId` and `version`. There is **no
  `subscriptions` key on the event.**

So parse the inner object with shared code, but reach it differently in the two places. A parser that
looks for `payload.subscriptions` on the event finds nothing, and if it then defaults to an empty
track list it will unsubscribe the client from the entire room - see §6.2b.

| Field | Meaning |
|---|---|
| `mode` | `"all"` or `"activeSpeaker"`. It describes **audio ranking only**. `"all"` does not mean the set can be ignored - if you were sent one, something is being withheld. |
| `revision` | Increases whenever the ranked set changes. **Ignore a payload whose `revision` is lower than one you have already applied** - two server instances can interleave. It is unrelated to `version`. |
| `activeSpeakers` | The ranked set, for rendering. Not the same as your track list, which also carries your pins. |
| `tracks` | Everything to pull, complete. Not a delta. |
| `layer` | Simulcast layer for a video track: `"a"` full, `"b"` medium, `"c"` low. `null` for audio. The names are alphabetical in **descending** quality because the SFU sorts rids a-z and reads that order as best-to-worst (§6.7) - they are a ranking, not an abbreviation. **The server sends this to the SFU on your behalf**, so it describes what you will actually be served, not a request you have to make. |
| `mediaSessionId` | May be `null` for a share published before the server recorded its session. Use the handle you already have from `TrackPublished`. Never `null` for audio. |

### 6.2a One plan per user, not per connection

**The plan is computed per `userId`, and there is no way to address one connection.** If you hold two
connections for one user - a media engine taking audio, a webview taking video - both are the same
subscriber to this server. `POST .../voice/subscriptions` does not name a connection and does not need
to; it speaks for the authenticated user. Split `tracks[]` by `kind` yourself and route each half to
the connection that wants it.

**Reporting `tileHeights` from the video side cannot affect the audio side.** That is structural, not
a convention: audio entries are built with `layer: null` unconditionally, and every tile-derived
input - `tileHeights`, `pausedPublishers`, `paused` - is only consulted when choosing a video layer or
when deciding whether to include a video track. A collapsed tile stops paying for pixels, not for
sound; a backgrounded client keeps hearing the room. So report tile sizes freely from whichever
connection renders them.

### 6.2b `tracks: null` and `tracks: []` are opposite instructions

This is the single most dangerous field in the voice contract to get wrong, because getting it
wrong is silent: the room simply goes quiet and nothing errors.

| Value | Meaning |
|---|---|
| `tracks` **absent or `null`** | No set is in force. **Pull everyone who is `Publishing`.** The ordinary small room. |
| `tracks: []` | A real set that is empty. **Pull nobody.** A subscriber who has collapsed every tile. |
| `tracks: [...]` | Pull exactly these. |

The rule is the same in all three places a set can reach you - the `subscriptions` block on a
snapshot (absent when no set is in force), the `SubscriptionsChanged` event, and the reply to
`POST .../voice/subscriptions`. Keep "absent" and "empty" strictly distinct in your parser; a
default of `[]` for a missing key unsubscribes the client from the whole room.

**`POST .../voice/subscriptions` always answers `200` with the full object**, never 204 and never a
bare `null`. In a room with no plan that object is `{ "mode": "all", "revision": 0,
"activeSpeakers": [], "tracks": null }` - and that reply *is* the revocation signal. A room that has
just dropped back below the ranking threshold tells you so with `tracks: null`, which is your cue to
go back to pulling everyone rather than to keep honouring the narrow set you were holding.

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

```http
POST .../voice/subscriptions              # or /voice/calls/{callId}/subscriptions
```

```jsonc
{
  "paused": false,
  "pinned": ["user-1"],
  "pausedPublishers": ["user-7"],
  "tileHeights": { "user-4": 180 },
  "screenAudioShares": ["abc123"]
}
```

Every field is optional and an omitted one is left alone, so a tile resize is a body with
`tileHeights` in it and nothing else. The reply is your own subscription set in the same shape as
§6.2, so you do not have to wait for the push to act on what you just reported.

It is `POST` rather than a hub invocation deliberately: it carries a body with maps in it, it needs a
reply, and it is not high frequency if you debounce it as described above.

### 6.5 Speaking reports are the input

Active-speaker ranking has exactly one input: `SpeakingChanged` from clients. A room whose clients
never report speech has no basis for ranking and correctly stays `mode: "all"`, so nothing is lost -
but nothing is saved either.

**Apply voice-activity hysteresis before you report.** The server admits a speaker the instant it is
told, deliberately, because gating entry on duration means the first person to talk in a quiet room
is inaudible for seconds. The consequence is that an un-hysteresised cough costs every subscriber in
the room a renegotiation. Debounce on your side; that is where it belongs.

Both room kinds report it: `call.SpeakingChanged` and `guild.voice.SpeakingChanged`. **A guild
channel whose clients do not send the guild one stays `mode: "all"` at any size**, which is what
every guild room did until the command existed - nothing looks broken, and nothing is saved.

### 6.6 The set is advice, and it is yours to honour

**Nothing refuses a subscription any more, and that is a real change.** When every pull was relayed
by this backend, a subscribe the plan did not include was answered with a 409 and you had to
implement §6.3 or be refused. The SDK subscribes directly now, so there is no request left to turn
away.

That makes §6.3 more important rather than less. A client that ignores its set is not corrected by
anybody: it pulls everyone, it costs what it always did, and nothing about the room looks wrong. Use
the SDK's selective subscription - `autoSubscribe: false` on connect, then `setSubscribed` per
publication - and drive it from the set.

The server-side switch that used to govern enforcement now governs only whether the usage meter
believes the plan, and it is still off by default for the same reason: billing against a reduction
nobody made prices a tier against egress that is still being paid for.

### 6.7 Layers are chosen by the server and applied by you

`layer` on a track is the server's answer to "how large is this viewer drawing it", computed from the
tile sizes you report. Apply it with `publication.setVideoQuality(...)` on the subscription.

Two things follow:

1. **Report `tileHeights`.** It is the only measurement the server has. Without it a ranked room
   falls back to the middle layer for cameras and full quality for screen shares, which is a
   deliberate guess and a worse one than yours.
2. **Apply what you are told.** The layer used to ride the subscribe this backend made on your
   behalf, so it bound whether you cooperated or not. It does not any more - a tile you report as
   120 pixels tall is served the top layer until you ask for something smaller.

A publisher that sends a single encoding has no layers to choose between, so publish simulcast
encodings if you want the saving to be real for your own video.

**Name them whatever your SDK names them** - `f`/`h`/`q` for livekit-client. The server never sees a
rid, never sends one, and never matches one.

`layer` is a **ranking vocabulary of ours**, not a rid to look up: `a` is the top encoding, `b` the
middle, `c` the bottom, and your job is to map those onto your SDK's quality enum
(`VideoQuality.HIGH` / `MEDIUM` / `LOW`) when you call `setVideoQuality`. It is deliberately opaque
so that it cannot be mistaken for a resolution.

> This paragraph previously told you to name your encodings `a`/`b`/`c`, because the string went on
> the wire as a `preferredRid` and the old SFU ranked rids alphabetically. That is no longer true of
> anything. Naming your encodings `a`/`b`/`c` still works, but so does `f`/`h`/`q`, and the latter is
> what your SDK will do on its own.

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
| **503** | `{ error: "voiceNotConfigured", action: "contactOperator" }`. This instance has no SFU. | Not a fault and not transient. A self-hosted install that has not configured voice is a supported state - hide the feature and say so. **There is no capability read yet**: probe once on first join attempt and cache the answer for the session. |
| **503** | `{ error: "sfuUnavailable", action: "retry" }`. The control plane could not be reached. | Retry with backoff. Every call already in progress is unaffected - media does not travel the path that failed - so do **not** tear anything down. |
| **503** | The room was contended and your change was not applied. | Retry after a short delay. This is transient, not a server fault. |
| **502** | The SFU refused a control-plane operation. Body: `{ operation, error }`. | Real failure, and not fixed by retrying the same request. |
| **403** | Not permitted: missing `Connect`/`Speak`/`Stream`, or not a participant. | Do not retry blindly. |
| **403** | An entitlement denial body (see the entitlements guide) on `POST .../voice/publish`. | Stop the local track: the token you connected with does not permit it either, so nobody will receive it. |
| **404** | Room or call does not exist - **or you forgot the gateway prefix**, see §10. | Stop, rejoin from scratch. |

**`staleSubscription` and `sessionGone` are gone**, along with the negotiation that produced them.
There is no subscribe request for this backend to refuse, and there is no minted session id to go
stale - a connection is opened with a token, and a token that has expired is replaced by asking for
another. If your client still has handlers for either code, they are dead paths.

**A failed connection is not a failed room.** If `POST .../voice/connection` answers 503, ask again:
you are still on the roster, your peers still see you as joined, and the heartbeat is still what
keeps you there.

---

## 9. Rules that will bite you if you skip them

1. **Declare what you publish.** The media works without it; the roster, the viewer counts, the
   entitlement check and everybody else's UI do not. See §3.4.
2. **Only subscribe to `publishState: "Publishing"`.** An identity alone is not enough.
3. **Ask for a new connection rather than reusing an expired token.** It is cheap, it does not
   touch the roster, and there is nothing else to recover with.
4. **Handle version gaps.** Without this, one dropped event is permanent.
5. **Compare `instanceId` before `version`.** A rebuilt room reuses low version numbers.
6. **Send `X-Device-Id`** on join, accept, decline, leave and connection creation. One user is in
   one room on one device at a time; joining elsewhere kicks the old device via
   `KickedByOtherDevice` - and the SFU evicts it by itself, because both devices connect under the
   same identity.
7. **Heartbeat with your real state.** It is the repair channel, not just a keepalive.
8. **Unwatch and unsubscribe** when a stream is not visible.
9. **Screen audio is a separate track.** Group by `shareId`, do not assume it exists.
10. **A hub reconnect is not a rejoin.** Do not tear down media because the SignalR connection
    blipped; heartbeat as soon as it is back. See §4.3.
11. **A second connection for the same user needs `primary=false`.** The SFU keys participants by
    identity and disconnects the earlier session under the same one, so a screen-share connection
    minted as primary kicks your own call off the air. See §3.2.
12. **Honour your subscription set, and expect it to change without anybody doing anything.** See
    §6. Nothing enforces it any more, so a client that ignores it simply pays for streams nobody is
    listening to and nobody tells it.
13. **Debounce voice-activity detection before reporting `SpeakingChanged`.** It is the sole input
    to active-speaker ranking, and an un-debounced one costs the whole room a resubscription. See
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
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/alive
GET    /api/v1/guilds/{guildId}/channels/{channelId}/voice            (same as /voice/snapshot)
GET    /api/v1/guilds/{guildId}/channels/{channelId}/voice/snapshot
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/connection?primary=&tag=
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/publish
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/unpublish
PUT    /api/v1/guilds/{guildId}/channels/{channelId}/voice/video
POST   /api/v1/guilds/{guildId}/channels/{channelId}/voice/subscriptions
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
POST   /api/v1/voice/calls/{callId}/connection?primary=&tag=
POST   /api/v1/voice/calls/{callId}/publish
POST   /api/v1/voice/calls/{callId}/unpublish
PUT    /api/v1/voice/calls/{callId}/video
POST   /api/v1/voice/calls/{callId}/alive
POST   /api/v1/voice/calls/{callId}/subscriptions
POST   /api/v1/voice/call/{callId}/shares/{shareId}/watch
DELETE /api/v1/voice/call/{callId}/shares/{shareId}/watch
GET    /api/v1/voice/call/{callId}/shares/viewers
```

Note the call SFU routes are under `/voice/calls/{callId}/` (plural) while the lifecycle routes are
under `/voice/call/{callId}/` (singular). That is historical; both are correct as written.

---

## 11. Worked example

```js
import { Room, RoomEvent } from "livekit-client";

// 1. Join. The snapshot arrives over SignalR before this resolves in practice.
const snapshot = await api.post(
  `/api/v1/guilds/${guildId}/channels/${channelId}/voice/join`, null,
  { headers: { "X-Device-Id": deviceId } });

// 2. Connection. `url` is the node this room lives on - do not cache it, ask again.
const conn = await api.post(
  `/api/v1/guilds/${guildId}/channels/${channelId}/voice/connection?primary=true`, null,
  { headers: { "X-Device-Id": deviceId } });

// 3. Connect. The SDK owns the peer connection, renegotiation and reconnect.
//    autoSubscribe: false so that §6 decides what you pull rather than the room.
const room = new Room({ adaptiveStream: true, dynacast: true });
room.on(RoomEvent.TrackSubscribed, (track, _pub, participant) => attach(track, participant));
await room.connect(conn.url, conn.token, { autoSubscribe: false });

// 4. Publish, then say so. The media works without step 4b; nothing else does.
await room.localParticipant.setMicrophoneEnabled(true);
await api.post(`.../voice/publish`, { trackNames: ["audio"] });

// 5. Subscribe to everyone already publishing. No refetch needed - there is no
//    transport of yours that has to exist first.
for (const p of snapshot.participants) {
  if (p.userId === me || p.publishState !== "Publishing") continue;
  subscribeTo(room, p.userId);   // honour your subscription set in a ranked room - see §6
}

// 6. Heartbeat. mediaSessionId is your identity, which conn hands back under both names.
setInterval(() => connection.invoke("voice.Heartbeat", "channel", channelId, {
  knownInstanceId: held.instanceId,
  knownVersion: held.version,
  mediaSessionId: conn.identity,
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

### What the move to LiveKit removed

The SFU changed and the negotiation went with it. These are gone and will 404:

| Removed | Replaced by |
|---|---|
| `POST .../voice/session` | `POST .../voice/connection` (§3.2) |
| `POST .../voice/tracks` (publish and subscribe) | the SDK, plus `POST .../voice/publish` to declare it (§3.3, §3.4) |
| `PUT .../voice/negotiate` | the SDK; `PUT .../voice/video` for the quality declaration alone (§3.5) |
| `POST .../voice/tracks/close` | `POST .../voice/unpublish` (§3.5) |
| `GET /api/v1/voice/ice-servers` | nothing - the SDK negotiates TURN with the node as part of connecting |
| `staleSubscription`, `sessionGone` | nothing - neither condition exists (§8) |

Everything else is untouched. The snapshot, the version rules, every hub event, the heartbeat, the
subscription sets, the share viewer counts and the entitlement bodies are all exactly as they were,
because none of them were ever about which SFU was behind them. That was the point of keeping the
client contract free of vendor vocabulary, and this migration is what it bought.
