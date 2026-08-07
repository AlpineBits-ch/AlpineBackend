# Feedback on the unified voice contract, from implementing the client

I've implemented `docs/specs/voice-frontend-guide.md` end to end on the desktop client (Alpine
`95f50a7`): snapshots, version tracking, `Resync`, the state-asserting heartbeat, and the
screen-share backfill, on both guild channels and DM calls. It works, and the backfill bug that
started this is closed — `shares[].trackNames` was exactly the missing piece.

Below is what I hit on the way. Two of these are guide bugs that will send the next implementer
into a wall; one is a design question only you can settle.

---

## 1. The version rule in §4.2 drops real events — please fix the guide, or the backend

**This is the important one.** §4.2 prints:

```
if (e.version <= held.version):   # duplicate or out of order
    ignore; return
```

Applied literally against the current backend, that discards events that are not duplicates:

- **Batched announcements share one version.** `VoiceRoomService.RecordTracksAsync` mutates the
  room once and then loops `foreach (var track in described)` emitting one `TrackPublished` per
  track, each `Envelope(room, …)` reading the same `room.Version`. Publishing a screen share with
  audio — which §2 explicitly tells clients to do in a single `tracks/new` — is therefore **two
  events at one version**, and the guide's rule throws away the second. Follow the guide and every
  screen share arrives silent.
- **Relay events never bump the version at all.** `SetSpeakingAsync` and `SetCameraAsync` call
  `rooms.LoadAsync`, not a mutation, so their events carry the version the client already holds.
  Under the guide's rule every speaking indicator and every camera toggle is ignored.

I shipped this rule instead, and it is what I'd suggest documenting:

```
if (e.instanceId !== held.instanceId):  refetch          # unchanged
if (e.version <  held.version):         ignore           # was <=
if (e.version >  held.version + 1):     refetch          # unchanged
apply; held.version = e.version                          # equality now applies
```

Only a *strictly lower* version is stale, which I think is the case §4.2 was actually reaching for
("an older one arriving late must not overwrite newer state"). The gap and instance checks — the
two that carry the recovery guarantee — are untouched.

**Your call on which side to fix.** The alternative is to make `VoiceAnnouncer` bump the version
per announcement rather than per mutation, which would make the printed rule true and give clients
strict per-event sequencing. That's a bigger change and it makes relay events versioned state,
which you deliberately decided they are not (`SetSpeakingAsync`'s comment says as much). I'd leave
the backend alone and fix the doc, but I don't want to assume.

## 2. `Resync` must not be version-gated — §4.2 doesn't say so

`VoiceAnnouncer.SendRoomGoneAsync` sends `instanceId: ""` and `version: 0`. Run that through §4.2
as written and the instance check fires `refetch` — which is survivable — but a client that
implemented the version branch first would classify the single most important event in the protocol
as a stale duplicate and drop it.

`Resync` is an instruction, not state. Worth one sentence in §4.2: resync events bypass the version
gate entirely and are always acted on.

## 3. §3 and §9 endpoint paths are missing the gateway prefix

Every path in §3, §9 and §10 is the service-internal route. The public surface goes through YARP
(`Echo/Proxy/ProxyConfig.cs`), which rewrites `/api/v1/guild/{**catch-all}` → `/api/v1/{**catch-all}`:

| Guide says | Client must call |
|---|---|
| `/api/v1/guilds/{g}/channels/{c}/voice/…` | `/api/v1/guild/guilds/{g}/channels/{c}/voice/…` |
| `/api/v1/voice/call[s]/{id}/…` | `/api/v1/messaging/voice/call[s]/{id}/…` |

The controllers really are `[Route("api/v1/guilds/…")]` and `[Route("api/v1/voice")]`, so the guide
is accurate about the *service* and wrong about the *client*. Everything in §9 404s as written.
`Echo/Docs/DocsOptions.cs` already documents this exact hazard for the docs generator — the same
note belongs at the top of §9.

## 4. The join snapshot arrives before a client can use it

`VoiceRoomService.JoinAsync` sends the snapshot immediately, which is right. But it lands *before*
the client has a peer connection or an SFU session, because those are created in step 2/3 of §3's
own ordering. Audio survives it (my subscribe path waits for the session); **screen shares do not**
— there is nothing to attach a recvonly transceiver to yet, so the `shares[]` in that first
snapshot go straight on the floor.

Both my paths now refetch `GET …/snapshot` after the transport is up. That's correct behaviour and
I'm not asking for a backend change — but it is non-obvious, it is the entire feature failing
silently, and §3's worked example in §10 doesn't do it. Worth an explicit step: *after publishing,
read the snapshot again and subscribe from it.*

## 5. Smaller things

- **`Snapshot` has a different payload shape from every other event.** `SendSnapshotAsync` sends
  the bare `VoiceRoomSnapshot` (so: `roomId`, and no `channelId`/`callId`), while everything else
  goes through `Envelope` and gets the room-id field. §4.1 shows the right shape but doesn't call
  out that it's the exception. A client that routes events by `channelId` drops it.
- **`ScreenShareStarted`'s `trackName` argument is accepted and never used.** Both
  `GuildVoiceScreenShareStartCommand` and `CallScreenShareStartCommand` carry `TrackName`, and both
  handlers pass only `ShareId` to `SetStreamingAsync`. The tracks themselves are recorded by
  `RecordTracksAsync` from `cf/tracks/new`. §5 documents the argument as if it matters — either
  drop it from the command and the doc, or say what it's for.
- **The heartbeat sweep is guild-only.** `VoiceHeartbeatCleanupService` iterates
  `VoiceRoomKey.Channel(...)` only, so a call participant who stops heartbeating is never evicted.
  Probably fine given calls end on their own, but §4.3's "stop heartbeating and you are swept after
  90 seconds" is only true for channels. Either sweep both or scope the sentence.
- **`GET …/voice` (the legacy shape) is still what `join` returns.** §3.1 says joining hands you a
  snapshot over SignalR, which it does — but the HTTP response to `POST …/voice/join` is still the
  old `VoiceStateDto` with no media handles. Not a problem, just worth a line so nobody tries to
  use it.

## 6. Still open on my side, for your awareness

- **Screen-share audio is not published yet.** §2's `screen-audio-{shareId}` is implemented on the
  receive side for guild channels, but the Rust screen publisher is video-only, so nothing produces
  that track today. The DM receive path skips it too.
- **Isle is not covered by this contract** and remains a third implementation.

---

Everything above is from reading the code alongside the guide, not from guessing — happy to point
at the exact lines for any of it.
