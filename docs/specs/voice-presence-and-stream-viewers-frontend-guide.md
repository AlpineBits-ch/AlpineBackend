# Voice presence, stream viewers and call history — frontend guide

Backend contract for four client features that had no server support: a voice indicator in the
server list, viewer counts on screen shares, joining a call already in progress, and calls appearing
in the conversation history. Guild voice channels and 1:1/group calls are covered symmetrically —
where the two differ, it is noted.

Nothing here changes an existing payload. Everything is additive.

---

## 1. Voice activity per guild (server list indicator)

**`GET /api/v1/guilds/voice-activity`**

```jsonc
[
  {
    "guildId": "guild-1",
    "participantCount": 3,            // across every channel the caller may see
    "hasStream": true,
    "channels": [
      {
        "channelId": "channel-1",
        "participantCount": 3,
        "userIds": ["user-1", "user-2", "user-3"],
        "hasStream": true,
        "streamerIds": ["user-2"]
      }
    ]
  }
]
```

Guilds with nobody in voice are **omitted**, not returned empty. Channels the caller cannot
`ViewChannel` are filtered out per viewer, and a guild whose only occupied channel is hidden does
not appear at all — a bare "something is happening here" would still leak a private channel.

**This is a snapshot, not a subscription.** Live updates already exist and need no new events:
`guild.voice.UserJoinedVoice` and `guild.voice.UserLeftVoice` carry `{ userId, channelId, guildId }`
and are already sent to *every member of the guild*, whether or not they are looking at it. Call
this endpoint on launch and after a reconnect gap; maintain the counts incrementally in between.

Screen-share state rides along: `guild.voice.ScreenShareStarted` / `ScreenShareStopped` update
`streamerIds` server-side, so re-fetching after a reconnect is enough to resync the "live" pip.

Backed by a per-guild index (`voice:guild:{guildId}`) written by join/leave/move/screen-share, and
rebuilt from the authoritative channel rosters by the 60-second heartbeat sweep. A dropped write
therefore self-heals within one interval rather than persisting — but it also means the endpoint can
lag reality by up to that interval in the rare drift case. Treat live events as the fresher truth.

---

## 2. Who is watching a screen share

Watching is **announced explicitly and expires**. A subscribe on the SFU cannot stand in for it: it
has no teardown a client is obliged to send, and a hidden or paused stream stays subscribed. A
viewer that stops announcing is dropped after **90 s**, so re-post on the same timer as the voice
heartbeat.

### Guild voice

| | |
|---|---|
| `POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/shares/{shareId}/watch` | start / heartbeat |
| `DELETE` same path | stop |
| `GET /api/v1/guilds/{guildId}/channels/{channelId}/voice/shares/viewers` | catch-up snapshot, `{ "shareId": ["userId", …] }` |

Both mutating calls return `{ channelId, shareId, viewerCount, viewerIds }` and broadcast
**`guild.voice.ShareViewersChanged`** with the same payload to every participant in the channel.

- Caller must be **in the channel roster** (not merely permitted to view it) → else `403`.
- `shareId` must be one somebody is actually publishing → else `404`.
- Viewers are dropped automatically when the watcher leaves the channel, when the sharer leaves,
  when the share stops, and when the heartbeat sweep evicts a stale participant.

### Direct calls

| | |
|---|---|
| `POST /api/v1/voice/call/{callId}/shares/{shareId}/watch` | start / heartbeat |
| `DELETE` same path | stop |
| `GET /api/v1/voice/call/{callId}/shares/viewers` | catch-up snapshot |

Returns `{ callId, shareId, viewerCount, viewerIds }` and broadcasts **`call.ShareViewersChanged`**
to every participant. Caller must be a **connected** participant (an invitee still ringing is
refused) → else `403`. The whole table is dropped when the call ends.

---

## 3. A call already in progress (the "join call" affordance)

**`GET /api/v1/voice/conversations/{conversationId}/call`** → `200` or `204`.

```jsonc
{
  "callId": "call-1",
  "conversationId": "conv-1",
  "status": "Connected",            // or "Pending" while it is still ringing
  "creatorId": "user-1",
  "startedAt": "2026-08-06T12:00:00Z",
  "connectedUserIds": ["user-1", "user-2"]
}
```

Answers for **any member of the conversation**, including one who declined, one who left, and one
who was never invited — none of whom the existing signals reach (`call.IncomingCall` is addressed to
invitees and never replayed; `voice/call/pending` answers only for someone currently being rung).
A non-member gets `404`, not `403` — the existence of the call is itself withheld.

**Deliberately not the `Call` object.** It carries no `cfSessionId` and no `audioTrackName`: those
are a live capability over media on a shared Cloudflare Calls app, and guild voice withholds the
same two fields from its own HTTP state for the same reason. They arrive over SignalR once you have
actually joined.

Live counterpart — **`conversation.CallStateChanged`**, sent to every member of the conversation:

```jsonc
{ "conversationId": "conv-1", "callId": "call-1", "status": "Ongoing" | "Ended",
  "reason": null | "UserEnded" | "Declined" | "AloneTimeout" | "AllParticipantsLeft",
  "participantIds": ["user-1", "user-2"] }
```

Emitted when a call is placed, when someone accepts (so the roster stays right), and when it ends.

---

## 4. Calls in the conversation history

`MessageType` gains two members, appended so existing persisted values keep their meaning:

| Type | Content | Meaning |
|---|---|---|
| `CallEnded` | whole seconds as plain text, e.g. `"184"` | a call that somebody answered |
| `CallMissed` | empty | the call ended with nobody but the caller ever connecting |

One entry per call, written when it ends, authored by **whoever placed the call** — which is what
lets a client render "you missed a call from X" without a second lookup. A ring timeout, a decline,
and an unanswered cancel all produce `CallMissed`; answering and hanging up seconds later produces
`CallEnded`, never `CallMissed`.

These arrive through the normal `conversation.MessageCreated` fan-out. **They send no push
notification** — the recipient just lived through the call, and a missed one would otherwise alert
twice, right behind the VoIP push that already fired.

---

## 5. Learning what to pull in a call (`call.ParticipantJoined`)

A client never discovers another participant's audio by inspecting the SFU: it is told, over
SignalR, which Cloudflare session and track name to pull. `call.ParticipantJoined` is that telling,
and it is the **only** live source for a call — `GET /api/v1/messaging/voice/conversations/{id}/call`
deliberately omits both fields (§3), and `GET /api/v1/messaging/voice/call/{callId}` carries them
but is a catch-up read the client has to decide to make.

```jsonc
{
  "callId": "call_01K…",   // added — the engine runs several calls at once
  "userId": "user-2",
  "cfSessionId": "…",      // the session that participant PUBLISHES on
  "audioTrackName": "audio"
}
```

`callId` is new and additive; the other three fields are unchanged.

**When it is sent.** Three moments, all server-side:

1. **Somebody publishes.** `POST /calls/{callId}/cf/tracks/new` carrying a `local` track named
   `audio` announces that participant to every *connected* participant. Never before the publish — a
   session id with no track behind it names something Cloudflare has nothing for, the subscribe
   fails, and because clients dedupe subscriptions per user the failed attempt burns the guard and
   the real announcement moments later is dropped as a duplicate. One-way silence for the rest of
   the call.
2. **In return.** That same request replays every *other* participant who is already publishing back
   to the publisher.
3. **On entering the media path.** `POST /calls/{callId}/session?primary=true` replays every
   participant who is already publishing to whoever made that request. It announces *nothing about
   them*: they have opened a session and published nothing.

(3) exists because the two sides of a call are not symmetric. The **callee** is `Connected` the
instant `PUT /call/{callId}/accept` returns, before any media work starts. The **caller** never
accepts their own call: `POST /session?primary=true` is the only thing that marks them `Connected`,
and a client issues it from its audio publisher at the end of a multi-second startup (open the
microphone, build the peer connection, gather ICE). Until it lands they are `Pending`, and (1) skips
them deliberately — an invitee who is still ringing has no business holding anyone's session id.
Without (3) a callee who answered quickly published inside that window and the caller was never
told, with nothing anywhere to repeat it: SignalR does not replay, and calls have no heartbeat sweep
the way guild voice does. The caller stayed deaf to the callee for the whole call while being heard
perfectly.

**What the client has to get right.** Announcements are never repeated, so one that arrives before
the client can act on it is lost for good. If your audio publisher is started by a blocking call —
as on desktop, where the Rust engine's `voice_start` does not return until it has published — then
(2) and (3) both arrive *while that call is still in flight*, and a subscribe handler that discards
an announcement because "the publication does not exist yet" discards exactly the ones that matter.
Wait for the publication rather than dropping the announcement; the guild path has carried that wait
since the equivalent bug there. `GET /voice/call/{callId}` is the catch-up if you would rather
reconcile — but call it *after* the publisher is up, not before.

---

## Not covered here

Per-viewer stream quality (Auto/720p/480p) is not implemented. It needs simulcast layers from the
publisher, which is client-side encoder work; the backend piece is a passthrough parameter on
`cf/tracks/new` and can follow once the publisher sends more than one encoding.
