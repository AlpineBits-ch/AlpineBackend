# Device coverage - client guide

Audience: Alpine (web/desktop) and venta-mobile engineers.
Status: **server-side implemented.** Both clients are unbuilt.

All URLs are public, through the gateway (`https://api.venta.gg`).

---

## 1. The problem this closes

An MLS group member is a **device**, not a person. A device gets into a group exactly one way: some
member's client seals it a Welcome. If it had no key package available at that moment - it was new,
it was offline, it had run dry - it is simply left out, and **nothing ever adds it afterwards**.

The server already reports this, at three moments: creating a conversation, enabling encryption, and
the commit that admits somebody. Each of those is a single response handed to a single client. So the
information exists for a few seconds, in one place, and then it is gone.

What that looks like to the person holding the left-out device is nothing at all. No error, no
warning, no failed request. The conversation opens and it is empty, which is indistinguishable from a
conversation nobody has written in. They will not report a bug, because from where they are sitting
there is no bug - and the person on the other side sees their messages delivered.

`GET .../mls/coverage` lets that question be asked again, at any time, by any participant.

**It reports, it does not repair.** The server holds no group keys and cannot add a device to a
group. The repair is the join request in §5.

---

## 2. The routes

| Context | Route |
|---|---|
| Conversation | `GET /api/v1/messaging/conversations/{conversationId}/mls/coverage` |
| Guild channel | `GET /api/v1/messaging/channels/{channelId}/mls/coverage` |

Conversation: any member. Channel: anyone with `ViewChannel`.

### Response

```json
{
  "contextId": "conv_01H...",
  "encrypted": true,
  "generation": 2,
  "ownDevices": [
    { "deviceId": "d-9f2...", "deviceName": "MacBook Pro", "covered": true },
    { "deviceId": "d-4c8...", "deviceName": "Pixel 8",     "covered": false }
  ],
  "unreachableDevices": [
    { "userId": "usr_01H...", "deviceId": "d-77a...", "deviceName": "iPhone 15" }
  ],
  "coverageUnavailable": false
}
```

| Field | Meaning |
|---|---|
| `encrypted` | False when the context has no live group. Both lists are then empty and mean *there is nothing to be outside of* - **not** "everybody is outside". |
| `generation` | Which group the answer is about. Null when `encrypted` is false. A device covered in generation 2 is not covered in generation 3. |
| `ownDevices` | **Every** active device on the caller's account, with a verdict each. |
| `unreachableDevices` | Other participants' devices that hold no leaf. Only the uncovered ones appear - covered ones are deliberately not listed, so this is not a directory of everyone's hardware. Always empty for a channel, whose roster the Messaging service cannot enumerate. |
| `coverageUnavailable` | True when the device list could not be read at all. See §6. |

### What `covered` is computed from

A Welcome addressed to that device in this generation, a commit published from it, or the record that
it built the group. Nothing else - the server has no group state to read.

**`covered: false` is evidence, not proof.** A device that joined by external commit leaves none of
those three traces and reads as uncovered while decrypting perfectly. Treat false as *ask whether this
device can read the conversation*, which the device itself can answer locally, and use it to offer a
repair - never to assert a fault.

---

## 3. Also required: `X-Device-Id` on enable

`POST .../mls/enable` (conversation and channel) now reads `X-Device-Id`.

Send it. Without it the server cannot tell which of your devices built the group, so it declines to
say anything about your account's devices at all - you lose the report on your own hardware, on
exactly the path (re-keying) where your other devices are most likely to fall out. Nothing errors;
the answer is just narrower.

`POST /api/v1/messaging/conversations` already required it and both clients already send it there.

---

## 4. When to call it

Not on a timer. The answer only changes when the group does.

- When an encrypted conversation or channel is opened, at most once per context per session.
- After the local client applies a Welcome or publishes a commit for that context.
- When the user opens the conversation's encryption/security screen - here, always refetch.
- After a join request of theirs is approved or denied.

Cache per `(contextId, generation)`. A changed `generation` invalidates everything you knew.

---

## 5. What the user sees

Three distinct situations. They read differently, they resolve differently, and collapsing them into
one warning is the main way this feature can be made worse than the silence it replaces.

### 5a. This device cannot read the conversation

The device you are running on appears in `ownDevices` with `covered: false`, **and** your local store
has no group state for this context. Both halves - the server's answer alone is not enough (§2).

This is the only case with a primary action, because this is the only device that can ask.

> **This device can't read these messages**
> It wasn't set up for this conversation's encryption. Older messages will stay unavailable here.
> **[ Request access ]**

- Place it inline, at the top of the message list, above the composer. Not a modal - the person may
  be here to read one thing and leave.
- The composer stays enabled. They can still send; sending works from an admitted device only, so if
  send is refused, surface the same notice rather than a generic failure.
- Be honest that history does not come back. A device admitted now joins at the current epoch;
  messages sent before it joined stay unreadable on it, forever. Do not write copy that implies
  otherwise ("we'll sync your messages" is false).
- **[ Request access ]** posts `POST .../mls/join-requests` with this device's id, name and key
  package. After it succeeds, the notice becomes a quiet waiting state:

> **Waiting to be let in**
> Approve this from another of your devices, or ask someone in this conversation to.

  Do not spin a loader indefinitely; this can take days. It is a status line, not a progress
  indicator.

### 5b. Another of your devices cannot read it

An entry in `ownDevices` with `covered: false` that is not the current device.

There is no action available from here - the stranded device has to ask for itself. So this is
information, and it belongs where someone goes looking for it, **not** on top of every conversation.

Put it on the conversation's encryption/security screen:

> **Pixel 8 can't read this conversation**
> Open Venta on that device and it will ask to be let back in.

Never a badge, never a red dot on the conversation list, never a push. The person is not currently
inconvenienced, and a warning that fires on an inconvenience they are not having is a warning they
learn to dismiss.

### 5c. Somebody else's device cannot read it

Entries in `unreachableDevices`. Same screen as 5b, same restraint.

> **Alex's iPhone can't read this conversation**
> They'll be asked to let it in the next time they open Venta on it.

Do not phrase this as their fault, do not offer to "notify" them, and do not show it in the message
list. Two exceptions where it is worth surfacing at the moment it happens, both of which already
exist and both of which are the *write* paths, not this one:

- Creating an encrypted conversation (`unreachableDevices` on the create response).
- Adding someone to one (`unreachableDevices` on the commit response).

At those moments the person is making a decision and the information changes it. Afterwards it is
reference material.

### Words to avoid

`MLS`, `leaf`, `generation`, `epoch`, `group`, `covered`, `unreachable`, `key package`. None of these
mean anything to the person reading. Say "can't read this conversation".

Say **can't read**, not "isn't secure" or "is not verified" - the conversation is not less secure
because a device is outside it. It is more secure and less useful, and the copy should be about the
useful part.

---

## 6. `coverageUnavailable`

True means the device list could not be read - a sibling service was down. Both lists come back
empty, and they are empty because nothing could be looked up.

**Do not render empty lists as "all clear".** Answering "no devices are stranded" when the truthful
answer is "cannot tell right now" reproduces the exact silence this route exists to break.

- Keep whatever you last knew and displayed. Do not clear a warning on this.
- Show nothing new. If the user explicitly opened the encryption screen and asked, a muted line -
  "Couldn't check right now" with a retry - is right; anywhere else, stay quiet.
- Retry no sooner than the next natural trigger in §4.

---

## 7. Parity checklist

Both clients, same behaviour, same copy.

- [ ] Send `X-Device-Id` on `POST .../mls/enable` for conversations and channels.
- [ ] `GET .../mls/coverage` for conversations and for channels, cached per `(contextId, generation)`.
- [ ] Cross-check `covered: false` for the current device against local group state before showing
      5a. Never show it on the strength of the server's answer alone.
- [ ] 5a: inline notice above the composer, primary action submits a join request, waiting state
      after, honest about history.
- [ ] 5b and 5c: encryption/security screen only. No badges, no pushes, no list decorations.
- [ ] `encrypted: false` renders nothing at all.
- [ ] `coverageUnavailable: true` never clears or contradicts a warning, and never reads as all-clear.
- [ ] No MLS vocabulary reaches the screen.

**Localization.** Alpine adds the strings to `venta-i18n`. venta-mobile has no localization layer at
all today, so its copy ships as English literals to match the rest of the app - do not introduce a
one-off mechanism for this screen.
