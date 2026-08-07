# Voice presence, stream viewers and call history - frontend guide

Backend contract for four client features that had no server support: a voice indicator in the
server list, viewer counts on screen shares, joining a call already in progress, and calls appearing
in the conversation history. Guild voice channels and 1:1/group calls are covered symmetrically -
where the two differ, it is noted.

> **Paths below are service-internal.** The public surface is behind the gateway, which strips a
> service prefix: prepend `/api/v1/guild` for guild routes and `/api/v1/messaging` for call and
> conversation routes, replacing their own `/api/v1`. A client that copies these literally gets a
> 404.

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
not appear at all - a bare "something is happening here" would still leak a private channel.

**This is a snapshot, not a subscription.** Live updates already exist and need no new events:
`guild.voice.UserJoinedVoice` and `guild.voice.UserLeftVoice` carry `{ userId, channelId, guildId }`
and are already sent to *every member of the guild*, whether or not they are looking at it. Call
this endpoint on launch and after a reconnect gap; maintain the counts incrementally in between.

Screen-share state rides along: `guild.voice.ScreenShareStarted` / `ScreenShareStopped` update
`streamerIds` server-side, so re-fetching after a reconnect is enough to resync the "live" pip.

Backed by a per-guild index (`voice:guild:{guildId}`) written by join/leave/move/screen-share, and
rebuilt from the authoritative channel rosters by the 60-second heartbeat sweep. A dropped write
therefore self-heals within one interval rather than persisting - but it also means the endpoint can
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
who was never invited - none of whom the existing signals reach (`call.IncomingCall` is addressed to
invitees and never replayed; `voice/call/pending` answers only for someone currently being rung).
A non-member gets `404`, not `403` - the existence of the call is itself withheld.

**Deliberately not the `Call` object.** It carries no `mediaSessionId` and no `audioTrackName`:
those are a live capability over media on a shared SFU app, and a member who is not in the call has
no business holding them. Once you have actually joined, they come from the room snapshot - see
[voice-frontend-guide.md](voice-frontend-guide.md).

Live counterpart - **`conversation.CallStateChanged`**, sent to every member of the conversation:

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

One entry per call, written when it ends, authored by **whoever placed the call** - which is what
lets a client render "you missed a call from X" without a second lookup. A ring timeout, a decline,
and an unanswered cancel all produce `CallMissed`; answering and hanging up seconds later produces
`CallEnded`, never `CallMissed`.

These arrive through the normal `conversation.MessageCreated` fan-out. **They send no push
notification** - the recipient just lived through the call, and a missed one would otherwise alert
twice, right behind the VoIP push that already fired.

---

## 5. Everything about joining, publishing and subscribing

**See [voice-frontend-guide.md](voice-frontend-guide.md).**

This document used to carry its own account of how a client learns which session and track to pull
in a call. That is gone, because the mechanism it described is gone. Guild channels and calls now
run one implementation with one contract: rooms, a versioned snapshot that is sufficient on its own,
and a heartbeat that repairs drift. Anything that section told you is either restated properly there
or no longer true - in particular:

- `cfSessionId` is now `mediaSessionId`, and `POST .../cf/tracks/new` is now `POST .../tracks` with
  `direction: "publish"` / `"subscribe"`.
- Announcements *are* now repeatable. The old section's central warning - that a missed
  announcement is lost for good, so a client must never drop one - was true and is not any more.
  Missing an event is recoverable from the version and the snapshot.
- The media handles no longer live on the `Call` object at all, so `GET .../voice/call/{callId}` no
  longer carries them. Use `GET .../voice/call/{callId}/snapshot`.
- Calls now have the same liveness sweep as guild channels.

What stays here is the part that is genuinely not the room contract: the guild voice-activity index
(§1), viewer counts (§2), discovering a call already in progress (§3), and call history (§4).

---

## Not covered here

Per-viewer stream quality (Auto/720p/480p) is not implemented. It needs simulcast layers from the
publisher, which is client-side encoder work; the backend piece is a passthrough parameter on the
negotiate call and can follow once the publisher sends more than one encoding.
