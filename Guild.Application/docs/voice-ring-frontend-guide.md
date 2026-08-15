# Inviting somebody into a voice channel (the "ring") - frontend integration guide

Backend support for "you are in a voice channel, ask a specific person to come and join you" is
done. There was no backend counterpart of any kind before this - no endpoint, no event, no push -
which is why `CallInviteCardComponent` on web has been a dead button, and why mobile has nothing at
all. This document is the whole contract; both clients can be built from it without reading the
server.

All URLs are **public, through the gateway (`https://api.venta.gg`)**, under `/api/v1/guild/**`.
Never call the microservice directly.

## What this is, and the two things it is not

A **ring** is an ephemeral, one-to-one invitation. Somebody already sitting in a voice channel asks
one named member to come in. It lives for **60 seconds** and then stops existing. It grants nothing:
accepting it does not put anybody in a channel.

It is **not the DM call ring** (`call.IncomingCall` and friends). That is a phone call - the caller
is on the line waiting, it rings a CallKit screen on iOS, and it appears in the system call log.
This does not and must not: it is an ordinary notification, and the card belongs inline in the UI,
not fullscreen. See the comparison table at the end.

It is **not the persistent invite link with a voice-channel target** (`GuildInvite`, shipped
separately). That is a credential - a URL anybody can paste anywhere, which admits a stranger to the
guild and can drop them into a channel. This is a message to somebody who is already a member of
the guild, addressed to them personally, that expires whether or not anyone looks at it. The two
share nothing but the word "invite"; do not build one UI over both.

## The one rule that shapes everything

**Accepting a ring does not join you to the channel.** It resolves the invitation and hands you the
channel's coordinates. The client then calls the ordinary join endpoint:

```
POST /api/v1/guild/api/v1/guilds/{guildId}/channels/{channelId}/voice/join
```

which is unchanged and already handles device resolution, media negotiation, leaving whatever
channel you were in, and reporting entitlement degradations. Two calls, in that order. If the join
fails you are not in the channel, but the invitation is still correctly closed - offer a plain
"Join" button pointing at the same channel, not a second accept.

---

## Endpoints

### Send an invitation - quietly, loudly, or both

```
POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/rings
{ "targetUserId": "user_...", "delivery": "Message" | "Ring" | "Both" }
```

Send `X-Device-Id` if you have one. It is optional here and never fatal.

`delivery` decides what actually happens. It defaults to **`Both`**, which is what this route did
before the field existed - so a client that omits it is unchanged. **Send it explicitly anyway**: a
request that says what it wants does not change meaning if the default ever moves.

| | `Message` | `Ring` | `Both` |
|---|---|---|---|
| Realtime card + push | no | yes | yes |
| Card in the DM | yes | no | yes |
| Creates a ring to accept/decline | no | yes | yes |
| Expires | never | 60s | 60s (the card stays) |
| Counts against the ring rate limits | no | yes | yes |
| Caller must be sitting in the channel | no | yes | yes |
| Can be refused by the recipient's DM policy | **yes** | no | no |

**`Message` is the one to reach for by default.** Ringing interrupts: it buzzes a phone, and a
decline locks the sender out for 15 minutes, then 2 hours, then 24. Most of the time "invite them"
means the quiet thing. Alpine puts `Message` on the row and `Ring` behind a separate bell.

Three things about `Message` are different enough to plan for:

- **It needs no presence.** The sender only has to be able to see and connect to the channel
  themselves. A ring refuses with `403` unless they are *in* it, because the ring's whole claim is
  "I am in here".
- **It can be refused outright, and often is.** `403 RecipientPolicy` means the recipient does not
  accept direct messages from this sender - the product default is friends-only, and two people
  sharing a server are frequently not friends. A ring never has this failure, because a ring needs
  no conversation. Surface it per person, not as a channel-wide error.
- **It answers a conversation, not a ring.** There is no id to accept and no instant to count down:

```json
{ "conversationId": "conv_3H66JNBG6BTA8FINHJVTTE2H846" }
```

That conversation may not have existed a second ago. It is useful for an "open the DM" affordance.

**200** for `Ring` and `Both`, with a `VoiceRing`:

```json
{
  "ringId": "ring_9f2c...",
  "guildId": "guild_...",
  "channelId": "chan_...",
  "channelName": null,
  "inviterId": "user_me",
  "targetUserId": "user_them",
  "status": "Pending",
  "reason": null,
  "createdAt": "2026-08-15T12:00:00Z",
  "expiresAt": "2026-08-15T12:01:00Z",
  "expiresInSeconds": 60,
  "resolvedByDeviceId": null
}
```

**Count down from `expiresInSeconds`, not from `expiresAt`.** A handset whose clock is a few minutes
out is ordinary, and trusting `expiresAt` there draws an invitation that is already dead or one that
never lapses.

`channelName` is null on this response - you sent the ring, you know the channel. It is populated on
the catch-up read and on the realtime event, where the reader may not.

Everything that can go wrong:

| Status | Body | What happened | What to show |
|---|---|---|---|
| `200` | the existing ring | You already have a live ring out to this person **into this channel**. Nothing was re-sent. | Nothing new. Keep showing the pending state. |
| `400` | text | You rang yourself, the channel is not a voice channel, or `delivery` was not one of the three values. | A bug in your client; do not surface it. |
| `403` | *(empty)* | Ringing: you are not in that voice channel. `Message`: you cannot see or connect to it yourself. | Hide the ring affordance unless the user is in the channel; hide both if they cannot see it. |
| `403` | `{"reason":"RecipientPolicy","retryAfterSeconds":0}` | **`Message` only.** They do not accept direct messages from you - or one of you has blocked the other, deliberately undistinguished. | "They do not accept messages from you." Offer the ring instead: it does not need a conversation. |
| `403` | `{"reason":"TargetCannotJoinChannel","retryAfterSeconds":0}` | They cannot see or cannot connect to it. | "They do not have access to this channel." |
| `403` | `{"reason":"Unavailable","retryAfterSeconds":0}` | A block exists, in one direction or the other. | "You cannot invite this person." Do **not** say "blocked" - the server deliberately does not tell you which. |
| `404` | *(empty)* | No such channel, or that user is not a member of this guild. | Refresh the member list. |
| `409` | `{"reason":"TargetAlreadyInChannel","retryAfterSeconds":0}` | They walked in while you were clicking. | Nothing. They are in the channel; the roster will say so. |
| `429` | `{"reason":"...","retryAfterSeconds":N}` | Rate limited. See below. | The message for the reason, and disable the button for `N` seconds. |

The three `429` reasons:

- **`RecentlyDeclined`** - this person turned you down recently. `retryAfterSeconds` can be up to
  24 hours. Say something like "They declined an invitation recently. You can try again later." Do
  not present a countdown timer for a multi-hour value; "later" is kinder and just as true.
- **`InviterFlooding`** - you have sent too many rings (6 in 5 minutes, to anybody).
- **`TargetSaturated`** - they have been rung too many times by too many people (4 in 5 minutes).
  Not your fault and still your `429`; word it as "They have had a lot of invitations just now."

### Read the rings currently asking you in

```
GET /api/v1/guilds/voice/rings/pending
```

**200** with an array of `VoiceRing` (empty array, never 204), each with `channelName` filled in.

**Call this on every app launch and on every realtime reconnect.** `guild.VoiceRingIncoming` is a
live broadcast that is never replayed, and the push is best-effort. This read is the third leg, and
without it a client that was offline for ten seconds never finds out it was asked.

There can be more than one: two different people can ask you into two different channels at once.
Show them as a stack, newest first.

### Accept

```
POST /api/v1/guilds/voice/rings/{ringId}/accept
```

Send `X-Device-Id`. **200** with the ring (`status: "Accepted"`, `channelName` populated,
`resolvedByDeviceId` set to your device). Then call the join endpoint with `guildId`/`channelId`
from the response.

| Status | Meaning |
|---|---|
| `403` | You are not this ring's target. |
| `404` | The ring never existed, or its 5-minute retention has passed. |
| `409` | Already resolved - expired, declined, cancelled, or answered on another of your devices. The body is the resolved ring; render what actually happened and take the card down. |
| `410` | `{"reason":"ChannelGone"}` - the channel was deleted, stopped being a voice channel, or you lost access to it while the ring was out. Take the card down; do not offer a join button. |

### Decline

```
POST /api/v1/guilds/voice/rings/{ringId}/decline
```

Same shapes as accept, minus the `410`. **Declining is meaningful, not cosmetic** - it locks that one
inviter out of ringing you again for 15 minutes, then 2 hours, then 24 hours if they keep coming
back. Nobody else is affected, and the inviter is not told they have been locked out beyond the
`429` they get if they try. Make sure the decline button is obviously the decline button; letting the
card expire is not the same act and carries none of this.

### Cancel (the inviter takes it back)

```
DELETE /api/v1/guilds/voice/rings/{ringId}
```

**200** with the cancelled ring. `403` if you are not the inviter - the target has `/decline`, and
they are deliberately different acts.

---

## Realtime events

Four events, all on the existing hub connection.

### `guild.VoiceRingIncoming` - to the target, every device

```json
{
  "ringId": "ring_9f2c...",
  "guildId": "guild_...",
  "channelId": "chan_...",
  "channelName": "General",
  "inviterId": "user_them",
  "inviterName": "Ada",
  "inviterAvatarUrl": "https://cdn/.../a.png",
  "targetUserId": "user_me",
  "createdAt": "2026-08-15T12:00:00Z",
  "expiresAt": "2026-08-15T12:01:00Z",
  "expiresInSeconds": 60,
  "participantUserIds": ["user_them", "user_c"]
}
```

`inviterName` and `inviterAvatarUrl` can be null if the profile lookup failed. `inviterId` never is -
resolve the person yourself from it rather than trusting the frozen copy.

`participantUserIds` is who is already in the channel, so the card can show faces. Safe to render:
the server only sends this ring to somebody who has `ViewChannel` on the channel.

### `guild.VoiceRingSent` - to the inviter, every device

Identical payload. This is not a confirmation of your own request (you got that in the HTTP
response) - it is so your **other** windows and devices stop offering to send an invitation that is
already out.

### `guild.VoiceRingResolved` - to the target and the inviter, every device

```json
{
  "ringId": "ring_9f2c...",
  "guildId": "guild_...",
  "channelId": "chan_...",
  "inviterId": "user_them",
  "targetUserId": "user_me",
  "status": "Accepted",
  "reason": null,
  "resolvedAt": "2026-08-15T12:00:14Z",
  "resolvedByDeviceId": "device_abc"
}
```

One event for every way a ring ends, rather than one event name per ending. A client that forgets to
subscribe to a newly-added event silently stops noticing that its invitations finish; a client that
meets an unknown `reason` can always fall back on `status`.

`status` is one of `Accepted`, `Declined`, `Cancelled`, `Expired`. `reason` is null for a plain
accept or decline, and otherwise one of:

| `reason` | Means |
|---|---|
| `InviterCancelled` | The inviter pressed cancel. |
| `InviterLeft` | The inviter left the voice channel. The invitation is no longer true. |
| `Superseded` | The same inviter rang you into a different channel. The newer ring replaces this one. |
| `TargetJoined` | You joined that channel by ordinary means. Not an accept - you may never have seen the card. |
| `ChannelGone` | The channel was deleted, stopped being voice, or you lost access. |
| `TimedOut` | Nobody answered in 60 seconds. |

`resolvedByDeviceId` is the device that answered, or null for a resolution nobody pressed a button
for. **If it matches your own device id, you already know - do not re-render.**

### `guild.VoiceRingDismissed` - to exactly one device of the target

```json
{ "ringId": "ring_...", "deviceId": "device_laptop", "status": "Accepted", "reason": null }
```

Sent when a device sends an accept or decline for a ring that was already resolved - typically your
laptop answering a second after your phone did. Take the card down on that device. The ring itself is
untouched: the user already answered it somewhere else, and this device only needs to know.

There is no takeover event. A ring holds no media session and no roster slot, so a superseded device
has nothing to tear down - dismissal is all takeover would have done here.

---

## The state machine

```
                            POST .../voice/rings
                                    |
                                    v
                              [ Pending ]  ---- 60s ----> [ Expired ]      reason TimedOut
                             /    |     \
              accept -------/     |      \------- decline
                  |                \                 |
                  v                 \                v
            [ Accepted ]             \         [ Declined ]   -> inviter locked out 15m / 2h / 24h
                  |                   \
        client then calls              \--- cancelled by something nobody pressed:
        POST .../voice/join                  DELETE by inviter  -> reason InviterCancelled
                                             inviter leaves     -> reason InviterLeft
                                             same inviter rings
                                               a second channel -> reason Superseded (older one)
                                             target joins anyway-> reason TargetJoined
                                             channel deleted /
                                               access lost      -> reason ChannelGone (at accept)
```

`Pending` is the only non-terminal state. **Exactly one transition out of it ever happens**, decided
server-side under a lock, whoever races for it.

### Every race, and what the client does

**The target is offline.** The realtime event goes nowhere. The push is attempted. The ring sits
pending until it expires. When the client comes back it calls `GET .../rings/pending` and finds it,
with the correct remaining time. This is why that read is not optional.

**The target is already in that channel.** The ring is never created - `409
TargetAlreadyInChannel`. Hide the invite affordance for anybody the roster already shows in the
channel.

**The target is in a different call or channel.** The ring is created normally, and the server does
**not** tell the inviter what the target is doing - they may be in a DM call, which is private. On
the target's side, show the card with a warning if they are currently in voice: accepting and then
joining will move them, and the join endpoint handles that eviction for you.

**The inviter leaves before you answer.** You get `guild.VoiceRingResolved` with `status:
Cancelled`, `reason: InviterLeft`. Take the card down. If the channel still has people in it, a plain
"Join" affordance in the channel list is fine - just not the invitation card, which was a claim about
a person who is no longer there.

**The channel is deleted mid-ring.** There is no proactive event for this; the ring dies on its own
within 60 seconds. If you accept in the meantime you get `410 ChannelGone` and the ring is closed
with that reason. Handle the `410`.

**The channel "fills up" mid-ring.** It cannot. Voice rooms never hard-reject a join; going over
`voice.max_participants` costs the joiner **video**, not admission, and the join response reports
that as an entitlement degradation exactly as it already does. So there is no full-channel state to
render here - only the ordinary degradation banner the join endpoint already returns.

**The same target is rung twice by the same person, same channel.** Idempotent. The second `POST`
returns the same ring with `200`, no second event and no second push. Your button can be naive.

**The same person rings you into a second channel.** The first ring is closed with `Superseded` and a
new one arrives. Never show two cards from the same face.

**Two different people ring you at once.** Both are live. Show a stack. Each is answered
independently.

**You have several devices.** The incoming event and the push go to all of them. Whichever answers
first wins; the rest get `guild.VoiceRingResolved` with `resolvedByDeviceId` naming the winner, plus
a silent cancel push. A device that answers late gets `409` from the endpoint and
`guild.VoiceRingDismissed` addressed to it alone. Never treat a `409` on accept/decline as an
error to surface - it is the normal multi-device outcome.

**The clock runs out while your accept is in flight.** The server compares against the deadline, not
against whether its own expiry timer has fired yet, so a late accept gets `409` with a ring that says
`Expired`. Trust the response over your local countdown.

---

## What to show, per state

| State | Target's UI | Inviter's UI |
|---|---|---|
| Pending | The invite card: inviter's avatar and name, channel name, who is already in there, Accept / Decline, and a countdown from `expiresInSeconds`. | The invite button in a pending state, with the same countdown and a cancel affordance. |
| Accepted | Card closes. Client calls join. Show the ordinary connecting state. | "Ada joined" or nothing - the voice roster will say so on its own. |
| Declined | Card closes silently. Do not confirm; the user knows what they pressed. | A quiet "Ada declined". Do **not** re-enable the invite button - it will `429`. |
| Expired / `TimedOut` | Card closes silently. | "No answer." Invite button re-enabled. |
| Cancelled / `InviterCancelled` | Card closes silently. | Back to idle. |
| Cancelled / `InviterLeft` | Card closes. Optionally "Ada left the channel." | n/a - they left. |
| Cancelled / `Superseded` | Card is replaced by the new one. | Replaced by the new pending state. |
| Cancelled / `TargetJoined` | Card closes; they are in the channel. | Back to idle. |
| Cancelled / `ChannelGone` | Card closes. "That channel is no longer available." | n/a |

The web tile already exists as `CallInviteCardComponent`; wiring its `invite` output to the `POST`
above and rendering the pending/resolved states is the whole of the web work.

---

## Push notification

An ordinary alert push over FCM/APNs - **not** the CallKit/VoIP path. A VoIP push obliges iOS to
report an incoming call to CallKit for every payload received, which would put a fullscreen system
call UI and a phone-log entry behind an invitation nobody is waiting on the line for.

Android notification channel: **`voice_invites`**. Create it. High priority, its own channel so
somebody who does not want to be pulled into calls can silence exactly that.

### Invite push - FCM `data`

```
type              = "voice_ring"
ringSubtype       = "invite"
ringId            = "ring_9f2c..."
guildId           = "guild_..."
channelId         = "chan_..."
inviterId         = "user_them"
inviterAvatarUrl  = "https://cdn/..."      (omitted when hidden)
recipientUserId   = "user_me"
hidden            = "0" | "1"
expiresInSeconds  = "60"
title             = "Ada"
body              = "Asked you to join General."
bodyLocKey        = "voice_ring_invite_body"   (only when the token declared push.loc.v1)
bodyLocArgs       = "[\"General\"]"            (JSON array; FCM data values are strings)
```

The same title/body/keys are also set in the notification block, so the OS draws it while the app is
dead. The `data` copy is for the foreground case, where the OS draws nothing and you do.

Tapping it should open the invite card, not join anything. `expiresInSeconds` is as of publication -
if the push sat in a queue longer than that, drop it rather than drawing a dead invitation.

### Cancel push - FCM `data`, silent, data-only

```
type            = "voice_ring"
ringSubtype     = "cancel"
ringId          = "ring_9f2c..."
guildId, channelId, inviterId, recipientUserId
cancelReason    = "Accepted" | "Declined" | "InviterLeft" | "TimedOut" | ...
excludeDeviceId = "device_abc"   (may be empty)
```

No notification block, no alert, no sound - its whole job is to take the card off the lock screen.
Cancel by the notification tag `voiceRing:{ringId}` (Android) / collapse id (iOS).

**If `excludeDeviceId` equals your own device id, ignore this push.** That is the device that
answered, and it already knows. The server addresses it anyway because a push token only knows which
device it belongs to if it was registered after the device-identity consolidation - filtering
server-side spared whole accounts and left handsets showing invitations that had been answered.

### Localization keys - mobile must ship these

Three new keys, resolved by the OS against the app bundle. They must be added to **all three client
tables in the same release**: `android/app/src/main/res/values*/strings.xml`,
`ios/Runner/*.lproj/Localizable.strings`, and `lib/core/push/household_strings.dart`'s sibling table
for voice. The server-side set is `Guild.Contracts/VoiceLocKeys.cs`, and
`household_strings_test.dart`'s cross-table check is the pattern to copy.

| Key | English | Args |
|---|---|---|
| `voice_ring_invite_body` | `Asked you to join %1$s.` | 1: the channel name |
| `voice_ring_hidden_title` | `Voice invite` | none |
| `voice_ring_hidden_body` | `Someone asked you to join a voice channel` | none |

A key the bundle does not contain does **not** fall back to the literal text - Android drops the
text, iOS shows the key name. So ship the resources and the `push.loc.v1` capability in the same
build, and never remove one without the other. Until the capability is declared, the server sends its
English and the notification is merely untranslated.

A recipient with hide-push-content on gets the `hidden` pair instead, and `hidden = "1"` in the data,
with the inviter's name, the channel name and the avatar all withheld.

### When there is no push

The push is skipped (the realtime event still goes out) if the target has muted that guild or
channel, or turned mobile push off for it. Somebody who silenced a server asked not to be buzzed by
it; the card still appears in an app they already have open, because that is not an interruption.

---

## The invitation in the DM - the only part that outlives the ring

Everything above disappears within the minute. A phone that was face-down for that minute is left
with a notification that has already been swiped away and nothing else, which is why the ring also
writes a message into the two people's direct conversation.

You write no message yourself - doing so would produce two. Sending an invitation with
`delivery: "Both"` (the default) or `delivery: "Message"` produces, in addition to the realtime
event and the push where those apply:

- a message of `type: "VoiceChannelInvite"`, authored by the inviter,
- in the 1:1 conversation between the inviter and the target,
- carrying exactly one embed, of type `venta.voice_invite`, with `flags & 65536` set.

The full card shape and the rendering rules are in the **embeds frontend guide**, section
`venta.voice_invite`. The short version, and it has **three** states rather than two:

| `ring_id` | `expires_at` | State | Affordance |
|---|---|---|---|
| set | in the future | a live ring | accept it through the ring endpoints |
| set | in the past | a ring that lapsed | the ordinary join against `channel_id` |
| absent | **absent** | a standing invitation (`delivery: "Message"`) | the ordinary join against `channel_id` |

**Do not read a missing `expires_at` as "expired".** That is the standing case, and it was valid the
second it arrived and stays valid. The lapsed case is one that *had* an expiry and is past it.

Nothing rewrites the message when a ring resolves - compare `expires_at` to your own clock instead.

Four consequences worth planning for:

- **The conversation may not exist yet.** If the two people already have a 1:1 conversation the
  message goes into the most recently used one. If they have none, the server starts one - so
  `conversation.MessageCreated` can be your first notice of a conversation id you have never seen.
  Refresh the conversation list on an unknown id rather than dropping the event.
- **The sender gets the realtime frame too.** Normally the author of a message is excluded from
  `conversation.MessageCreated`, because they sent it and the send returned it. Nobody sent this
  one, so both parties receive it. Do not treat "a message from me over the socket" as a duplicate.
- **It is not suppressed by a mute.** Unlike the push, this is written even when the target has
  silenced the server. A mute is a request not to be interrupted, not a request to be left out of
  your own message history - and it cannot buzz anything, because no system message produces a push.
- **For `Both`, it is not guaranteed.** The message is skipped, and only logged, when the two have
  no conversation and the target's `DirectMessagePolicy` would not admit a first contact from the
  inviter. The ring is unaffected, and the request still answers `200`. For `delivery: "Message"`
  that same refusal is the whole operation failing and comes back as `403 RecipientPolicy` - which
  is the difference between the two: with a ring in hand the card is a bonus, without one it is
  everything.

---

## Rate limits, in one place

| Limit | Value | Refusal |
|---|---|---|
| Rings sent by one account | 6 per 5 minutes | `InviterFlooding` |
| Rings received by one account | 4 per 5 minutes | `TargetSaturated` |
| After a decline, that pair | 15 min, then 2 h, then 24 h | `RecentlyDeclined` |

Repeating a ring you already have out costs nothing, and a `409 TargetAlreadyInChannel` is refunded.
Permission refusals are **not** refunded, deliberately: walking the member list to find out who can
see a private channel should cost a token per name.

---

## How this differs from the two things it resembles

| | This ring | DM call ring | Persistent invite link |
|---|---|---|---|
| Who can receive it | one named guild member with `ViewChannel` + `Connect` | conversation participants, subject to DM policy | anybody holding the URL |
| Lifetime | 60 seconds | 50 seconds | hours to forever, with a use count |
| Backed by | Redis only, no table | Redis only, no table | a database row |
| Push transport | ordinary alert, `type=voice_ring` | CallKit/VoIP + FCM, `type=call` | none |
| Localized push | yes, `push.loc.v1` | no - VoIP payloads cannot carry keys | n/a |
| Accepting | closes the invitation; you then join | connects you to the call | joins you to the **guild** |
| Grants access | nothing | nothing | guild membership |
| Realtime prefix | `guild.VoiceRing*` | `call.*` | `guild.MemberJoined` |
| Declining | locks that inviter out, escalating | ends the call for you | n/a |
| Revocable | `DELETE`, by the inviter | hang up | revoke, by a moderator |

The event names use the plain `guild.` prefix rather than `guild.voice.` on purpose: `guild.voice.*`
is reserved for voice **room state**, which every client must be able to reconstruct from a version
number after missing an event. A ring is not room state - its audience is somebody who is not in the
room and may never be - so it sits alongside `guild.MemberJoined` and `guild.HouseholdAlert` instead.

## Known limitations (v1)

- **No "invite to a channel you are not in."** You must be sitting in the voice channel. Discord
  permits inviting from a channel you are merely looking at; this does not, because the invitation's
  entire claim is "I am in here".
- **No invite-by-mention or invite-from-DM.** The target is picked by user id from the guild's own
  member list.
- **No delivery receipt.** The inviter learns nothing between sending and the resolution - not
  whether the target is online, not whether the push landed. That is deliberate: it would leak
  presence the target has not offered.
- **No history of the ring itself.** A ring is unreadable 5 minutes after it resolves, and there is
  no list of who rang whom. The DM card above is the durable record of the *invitation*, not of its
  outcome: it never says whether it was accepted, declined or simply lapsed.
- **No proactive event when the channel is deleted.** The ring lapses on its own inside a minute;
  accepting into a deleted channel returns `410`.
