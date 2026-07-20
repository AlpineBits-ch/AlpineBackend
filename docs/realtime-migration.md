# Realtime (SignalR) migration — one connection per user

**Audience:** frontend.
**What changed:** the three separate SignalR connections (messaging, voice, guild) are replaced by **one** connection, terminated on the Echo gateway. Event and method names are now **domain-prefixed** so a single connection can carry all of them unambiguously.

There is **no dual-run** — this is a coordinated cutover. When the backend deploys, the old hub endpoints are gone. Ship the frontend change together with the backend release.

---

## 1. Connection

**Before** — three connections:

| Hub | URL |
|---|---|
| Messaging | `/api/v1/messaging/ws/hubs/messaging` |
| Voice (calls) | `/api/v1/messaging/ws/hubs/voice` |
| Guild | `/api/v1/guild/ws/hubs/guild` |

**After** — one connection:

```
/api/v1/ws/hub
```

Auth is unchanged: pass the JWT as the `access_token` query-string parameter (or `accessTokenFactory`). Keep the WebSocket transport.

```ts
const conn = new signalR.HubConnectionBuilder()
  .withUrl("/api/v1/ws/hub", { accessTokenFactory: () => token })
  .withAutomaticReconnect()
  .build();
```

Delete the other two `HubConnection` instances — everything now flows over this one.

---

## 2. Server → client events (`connection.on(...)`)

Payloads are **unchanged** — only the event name changes. Register handlers under the new names.

### Presence
| Old | New | Payload |
|---|---|---|
| `UserOnline` | `presence.UserOnline` | `string userId` |
| `UserOffline` | `presence.UserOffline` | `string userId` |

### Conversations / DMs
| Old | New | Payload |
|---|---|---|
| `UserTyping` | `conversation.UserTyping` | `{ conversationId, userId }` |
| `MessageCreated` | `conversation.MessageCreated` | (unchanged) |
| `MessageUpdated` | `conversation.MessageUpdated` | (unchanged) |
| `MessageCreated` *(was emitted on delete — bug)* | `conversation.MessageDeleted` | **fixed:** now a distinct event on delete |
| `ReactionCreated` | `conversation.ReactionCreated` | (unchanged) |
| `ReactionCreated` *(was emitted on remove — bug)* | `conversation.ReactionRemoved` | **fixed:** now a distinct event on remove |
| `ConversationCreated` | `conversation.ConversationCreated` | `string conversationId` |
| `ConversationDeleted` | `conversation.ConversationDeleted` | (unchanged) |
| `MemberLeft` | `conversation.MemberLeft` | (unchanged) |
| `Welcome` | `conversation.Welcome` | `string conversationId` |
| `FriendRequestAccepted` | `conversation.FriendRequestAccepted` | (unchanged) |

> **Behavior change (bug fixes):** previously the backend emitted `MessageCreated` when a message was *deleted* and `ReactionCreated` when a reaction was *removed*. These now emit `conversation.MessageDeleted` and `conversation.ReactionRemoved`. If your UI was working around the old behavior, remove the workaround and handle the new events.

### 1:1 calls
| Old | New | Payload |
|---|---|---|
| `IncomingCall` | `call.IncomingCall` | `Call` |
| `CallEnded` | `call.CallEnded` | `{ callId }` |
| `CallAccepted` | `call.CallAccepted` | `Call` |
| `CallDeclined` | `call.CallDeclined` | `Call` |
| `ParticipantJoined` | `call.ParticipantJoined` | `{ userId, cfSessionId, audioTrackName }` |
| `ParticipantLeft` | `call.ParticipantLeft` | `{ userId }` |
| `MuteChanged` | `call.MuteChanged` | `{ userId, isMuted }` |
| `CameraChanged` | `call.CameraChanged` | `{ userId, isCameraOn }` |
| `SpeakingChanged` | `call.SpeakingChanged` | `{ userId, isSpeaking }` |
| `ScreenShareStarted` | `call.ScreenShareStarted` | `{ shareId, userId, cfSessionId, trackName }` |
| `ScreenShareStopped` | `call.ScreenShareStopped` | `{ shareId }` |
| `TrackPublished` | `call.TrackPublished` | `{ userId, cfSessionId, trackName, kind, shareId }` |
| `TrackClosed` | `call.TrackClosed` | `{ userId, trackName, shareId }` |

### Guild (text / structure / wiki)
| Old | New | Payload |
|---|---|---|
| `UserTyping` | `guild.UserTyping` | `{ userId, channelId }` |
| `MessageCreated` | `guild.MessageCreated` | (unchanged) |
| `ReactionCreated` | `guild.ReactionCreated` | (unchanged) |
| `ReactionRemoved` | `guild.ReactionRemoved` | (unchanged) |
| `ChannelCreated` / `ChannelDeleted` / `ChannelReordered` | `guild.ChannelCreated` / `guild.ChannelDeleted` / `guild.ChannelReordered` | (unchanged) |
| `CategoryCreated` / `CategoryDeleted` | `guild.CategoryCreated` / `guild.CategoryDeleted` | (unchanged) |
| `WikiPageCreated` / `WikiPageUpdated` / `WikiPageDeleted` | `guild.WikiPageCreated` / `guild.WikiPageUpdated` / `guild.WikiPageDeleted` | (unchanged) |
| `WikiCategoryCreated` / `WikiCategoryUpdated` / `WikiCategoryDeleted` | `guild.WikiCategoryCreated` / `guild.WikiCategoryUpdated` / `guild.WikiCategoryDeleted` | (unchanged) |

### Guild voice
| Old | New | Payload |
|---|---|---|
| `MuteChanged` | `guild.voice.MuteChanged` | `{ userId, isMuted, channelId, serverForced }` |
| `DeafenChanged` | `guild.voice.DeafenChanged` | `{ userId, isDeafened, channelId, serverForced }` |
| `CameraChanged` | `guild.voice.CameraChanged` | `{ userId, isCameraOn, channelId }` |
| `ScreenShareStarted` | `guild.voice.ScreenShareStarted` | `{ userId, shareId, trackName, channelId }` |
| `ScreenShareStopped` | `guild.voice.ScreenShareStopped` | `{ shareId, channelId }` |
| `UserJoinedVoice` | `guild.voice.UserJoinedVoice` | `{ userId, channelId, guildId }` |
| `UserLeftVoice` | `guild.voice.UserLeftVoice` | `{ userId, channelId, guildId }` |
| `MovedToChannel` | `guild.voice.MovedToChannel` | `{ channelId, guildId, movedBy }` |
| `ParticipantJoined` | `guild.voice.ParticipantJoined` | `{ userId, cfSessionId, audioTrackName, channelId }` |
| `TrackPublished` | `guild.voice.TrackPublished` | `{ userId, cfSessionId, trackName, kind, shareId, channelId }` |
| `TrackClosed` | `guild.voice.TrackClosed` | `{ userId, trackName, channelId }` |

> Note the previously-colliding names (`MuteChanged`, `CameraChanged`, `ScreenShareStarted`, `ParticipantJoined`, `TrackPublished`, `TrackClosed`) are now split into `call.*` (1:1 calls) vs `guild.voice.*` (guild voice channels). Route on the prefix.

---

## 3. Client → server invocations (`connection.invoke(...)`)

Arguments are **unchanged** — only the method name changes. **Do not send `userId`** in any payload; the server derives it from your token (any value you send is ignored/overwritten).

### Conversations / DMs
| Old | New | Args |
|---|---|---|
| `StartTyping` | `conversation.StartTyping` | `conversationId: string` |
| `UpdateLastReadMessageByConversation` | `conversation.UpdateLastRead` | `{ conversationId, id }` |

### 1:1 calls
| Old | New | Args |
|---|---|---|
| `MuteChanged` | `call.MuteChanged` | `{ callId, isMuted }` |
| `CameraChanged` | `call.CameraChanged` | `{ callId, isCameraOn }` |
| `SpeakingChanged` | `call.SpeakingChanged` | `{ callId, isSpeaking }` |
| `ScreenShareStarted` | `call.ScreenShareStarted` | `{ callId, shareId, trackName }` |
| `ScreenShareStopped` | `call.ScreenShareStopped` | `{ callId, shareId }` |

### Guild (text)
| Old | New | Args |
|---|---|---|
| `StartTyping` | `guild.StartTyping` | `channelId: string` |
| `UpdateLastReadMessageByChannel` | `guild.UpdateLastRead` | `{ id, channelId }` |

### Guild voice
| Old | New | Args |
|---|---|---|
| `VoiceHeartbeat` | `guild.voice.Heartbeat` | *(none)* |
| `VoiceMuteChanged` | `guild.voice.MuteChanged` | `{ channelId, isMuted }` |
| `VoiceDeafenChanged` | `guild.voice.DeafenChanged` | `{ channelId, isDeafened }` |
| `VoiceCameraChanged` | `guild.voice.CameraChanged` | `{ channelId, isCameraOn }` |
| `VoiceScreenShareStarted` | `guild.voice.ScreenShareStarted` | `{ channelId, shareId, trackName }` |
| `VoiceScreenShareStopped` | `guild.voice.ScreenShareStopped` | `{ channelId, shareId }` |
| `VoiceServerMute` | `guild.voice.ServerMute` | `{ channelId, targetUserId, isMuted }` |
| `VoiceServerDeafen` | `guild.voice.ServerDeafen` | `{ channelId, targetUserId, isDeafened }` |
| `VoiceMoveUser` | `guild.voice.MoveUser` | `{ channelId, targetUserId, targetChannelId }` |

---

## 4. Semantics to be aware of

- **Presence is now global per user**, not per-feature. Online/offline fires once when the single connection opens/closes (not once per hub). This is almost certainly what you want, but if any UI assumed independent per-hub presence, revisit it.
- **Voice-moderation calls still fail silently** on missing permission (`guild.voice.ServerMute` / `ServerDeafen` / `MoveUser` / `ScreenShareStarted`): no error frame is returned, just no effect — unchanged from today.
- **Reconnect:** re-subscribe your `on(...)` handlers after `onreconnected` as before; nothing new here.

---

## 5. Suggested migration steps for the client

1. Replace the three connection builders with one pointing at `/api/v1/ws/hub`.
2. Search-and-replace event/method names using the tables above. A prefix-based dispatcher helps: split the incoming event name on the first/second `.` and route to the existing feature handler.
3. Remove any `MessageDeleted`/`ReactionRemoved` workarounds (see §2 bug-fix note).
4. Ensure no `invoke` payload includes `userId`.
