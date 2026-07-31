# Device Identity Consolidation — Client Guide

Audience: desktop/web and mobile client engineers.
Status: **implemented server-side, not yet deployed.** Every endpoint below exists in the codebase; the database migration (`ConsolidateDeviceConcepts`) has not been applied to production yet.

All URLs are **public, through the gateway (`https://api.venta.gg`)** — never call a microservice directly.

Companion document: [multi-device-voice-and-calls.md](multi-device-voice-and-calls.md), which introduced the device id. This guide is what changed since.

---

## 1. What changed, in one paragraph

The backend had six unrelated notions of "a user's device". They are now one: a registered **device**, identified by the `ClientDeviceId` your client already generates for MLS. Push tokens attach to it, login sessions link to it, and the `X-Device-Id` header is now checked against it instead of being trusted blindly. Concretely, for you that means: **the device id is no longer optional in practice**, push tokens should be registered with the device they belong to, and there is finally a way to unregister a device.

Nothing here breaks a client that does nothing — all of it degrades to today's behaviour. But the fixes only land once you send the device id.

---

## 2. `X-Device-Id` is now validated

The three endpoints that read the header (`Messaging` call actions, the Cloudflare session, `Guild` voice join) previously accepted any string. They now check it against your registered devices:

| What you send | What happens |
|---|---|
| No header | Unchanged. Treated as the implicit `default` device — today's single-device behaviour. |
| A header naming one of your registered devices | Accepted, as before. |
| A header naming a device you have not registered | **`400 Bad Request`**, body `Unknown X-Device-Id '<id>' - register the device first.` |

The last row is the new failure. It fires when a client sends a device id it never registered — a typo, a value regenerated on each launch, or a device id copied from another install. Previously that silently reintroduced the multi-device bugs the header exists to fix.

**Action:** register the device (§3) at first launch, before any call or voice-channel action, and reuse the same `ClientDeviceId` forever. If Identity is unreachable, the header is accepted unverified rather than failing the call — you will not see 400s from a backend outage.

Affected endpoints:

- `POST https://api.venta.gg/api/v1/messaging/voice/calls/{callId}/session`
- `PUT https://api.venta.gg/api/v1/messaging/voice/call/{callId}/accept`
- `PUT https://api.venta.gg/api/v1/messaging/voice/call/{callId}/decline`
- `PUT https://api.venta.gg/api/v1/messaging/voice/call/{callId}/leave`
- `POST https://api.venta.gg/api/v1/guild/guilds/{guildId}/channels/{channelId}/voice/join`

---

## 3. Device registration (unchanged shape, changed semantics)

`POST https://api.venta.gg/api/v1/identity/devices`

```json
{
  "clientDeviceId": "a-stable-per-installation-id",
  "deviceName": "Alice's MacBook",
  "deviceType": "Desktop",
  "identityPublicKey": "<base64 bytes>"
}
```

`deviceType` is one of `Desktop`, `Mobile`, `Web`.

**What changed:** `clientDeviceId` is now unique **per account** rather than globally. Registering an id that another account already uses is fine and no longer touches their record. (Previously the server deleted the other account's device row, cascading away their MLS key packages — any account could destroy another's device registration by claiming its id.)

Re-registering your own existing id is still idempotent and returns the existing device.

`GET https://api.venta.gg/api/v1/identity/devices` — list your devices. Unchanged.

### 3.1 NEW: unregistering a device

`DELETE https://api.venta.gg/api/v1/identity/devices/client/{clientDeviceId}`

- `204 No Content` on success.
- `404 Not Found` if it is not one of your devices.

Removes the device and, by cascade, its MLS key packages, its encrypted backup and its push tokens; login sessions from that device are revoked. Use it from a "sign out and forget this device" action, and from a settings screen listing devices.

Until now there was no removal path at all, which is why a reinstalled or sold handset kept receiving push forever.

---

## 4. Push tokens: two endpoints became one

### 4.1 The new endpoint

`POST https://api.venta.gg/api/v1/identity/users/self/push-token`

```json
{
  "token": "<the FCM or APNs VoIP token>",
  "kind": "Fcm",
  "deviceId": "a-stable-per-installation-id"
}
```

| Field | Notes |
|---|---|
| `token` | Required. |
| `kind` | Required. `"Fcm"` (Firebase — Android notifications *and* the Android call ring) or `"ApnsVoip"` (the iOS PushKit token CallKit needs). Case-insensitive. |
| `deviceId` | Optional but **strongly recommended** — your `ClientDeviceId`. Without it the token cannot be targeted or cleaned up. |

Responses: `201 Created` for a new token, `202 Accepted` when an existing row was re-pointed at you, `400` if `token` is missing.

An unknown `deviceId` does not fail the call: the token is registered unattached and a warning is logged. Do not rely on this — register the device first.

### 4.2 NEW: deregistering a push token

`DELETE https://api.venta.gg/api/v1/identity/users/self/push-token?token=<token>&kind=Fcm`

`kind` is optional (omit to delete the token under every transport). `204` on success, `404` if you hold no such token. Call this on sign-out so a signed-out handset stops being rung.

### 4.3 The old endpoints still work

`POST /api/v1/identity/users/self/device-token` and `POST /api/v1/identity/users/self/voip-token` are **deprecated but functional** — they now write to the same store with `kind` fixed to `Fcm` and `ApnsVoip` respectively. Their body gained an optional `deviceId`:

```json
{ "token": "...", "deviceId": "a-stable-per-installation-id" }
```

Migrate to `self/push-token` when convenient; adding `deviceId` to the legacy call is a smaller change that gets you most of the benefit today.

### 4.4 Why this matters to you

Push tokens now carry their device, so the server can leave out the device that is already dealing with the event. The first user-visible consequence: **when you accept a call on one device, your other devices now receive the cancel push** (they were previously skipped along with the accepting device), while the device that accepted does not. If your call UI reacted to a cancel push by tearing down an active call regardless of which device answered, check that logic — the accepting device is excluded server-side, but only when it sent a registered `X-Device-Id` on accept.

---

## 5. Login sessions know their device

### 5.1 Send your device id at token exchange

`POST https://api.venta.gg/connect/token`

Add a `device_id` form parameter alongside the existing optional `device_name` / `device_type`:

```
grant_type=password
username=...
password=...
client_id=echo
device_name=Alice's MacBook
device_type=Desktop
device_id=a-stable-per-installation-id
```

(An `X-Device-Id` header on the token request works too, if that is easier in your HTTP stack.)

An unknown or absent `device_id` is ignored rather than rejected — a first login necessarily happens before the device can be registered. Practical sequence for a fresh install: log in without it, register the device, and it will be linked from the next login onward.

### 5.2 QR login carries it too

`POST https://api.venta.gg/api/v1/identity/qr-login/start` gained an optional `clientDeviceId`:

```json
{ "deviceName": "Alice's MacBook", "deviceType": "Desktop", "clientDeviceId": "a-stable-per-installation-id" }
```

It is carried through the pairing and attached to the session minted at `/connect/token`. Nothing else in the QR flow changes.

### 5.3 The sessions list gained a field

`GET https://api.venta.gg/api/v1/identity/sessions` now returns `clientDeviceId` per session (null for logins that sent none):

```json
[
  {
    "id": "lgsn_...",
    "deviceName": "Alice's MacBook",
    "deviceType": "Desktop",
    "ipAddress": "203.0.113.9",
    "createdAt": "2026-07-31T10:00:00+00:00",
    "lastUsedAt": "2026-07-31T12:00:00+00:00",
    "isCurrent": true,
    "clientDeviceId": "a-stable-per-installation-id"
  }
]
```

Use it to match a session row to the machine your client is running on, and to reconcile the "sessions" list with the "devices" list (`GET /api/v1/identity/devices`) — the two used to be unrelatable.

### 5.4 Revoking a session now kills its push

`DELETE https://api.venta.gg/api/v1/identity/sessions/{sessionId}` additionally deletes the push tokens of the device that session came from, when the session recorded one. A revoked login therefore stops ringing that handset — previously it kept receiving calls and messages indefinitely.

---

## 6. Realtime hub — unchanged

`wss://api.venta.gg/api/v1/ws/hub?deviceId={yourDeviceId}`

Still the same query parameter, still falls back to the `default` bucket when omitted, still the transport for the per-device events (`call.CallDeviceDismissed`, `call.CallDeviceTakeover`, `guild.voice.KickedByOtherDevice`). No hub-side validation is applied — only the REST endpoints in §2 reject an unknown id.

---

## 7. Checklist

- [ ] Register the device (`POST /api/v1/identity/devices`) at first launch, before any call/voice action, reusing one stable `ClientDeviceId` per installation.
- [ ] Keep sending `X-Device-Id` on call accept/decline/leave, the Cloudflare session create, and guild voice join — and handle the new `400` for an unregistered id by re-registering the device and retrying once.
- [ ] Move push registration to `POST /api/v1/identity/users/self/push-token` with `kind` and `deviceId`; at minimum, add `deviceId` to the legacy `device-token`/`voip-token` calls.
- [ ] On sign-out, `DELETE /api/v1/identity/users/self/push-token`.
- [ ] Add "forget this device" to settings via `DELETE /api/v1/identity/devices/client/{clientDeviceId}`.
- [ ] Send `device_id` at `/connect/token` (and `clientDeviceId` on `qr-login/start` for desktop).
- [ ] Surface `clientDeviceId` in the sessions UI so "sessions" and "devices" can be shown as one list.
- [ ] Verify your cancel-push handling: an accepting device no longer gets a cancel, but its siblings now do.

---

## 8. Server-side notes (not client-facing)

- Migration: `Identity.Infrastructure/Migrations/20260731133513_ConsolidateDeviceConcepts.cs`. Creates `user_push_tokens`, copies `user_device_tokens` (as `fcm`) and `user_voip_tokens` (as `apns_voip`) into it, then drops both. Copied tokens land with `device_id` null — nothing ever recorded which installation registered them, so attribution begins when clients re-register.
- The unique index is `(kind, token)`. Tokens duplicated across accounts in the old tables are deduped to the most recently updated row, since that is the account the handset actually belongs to.
- `client_device_id` uniqueness moved from global to `(user_id, client_device_id)`.
- Bus contracts `GetDeviceTokenForUserIdRequest` / `GetVoipTokenForUserIdRequest` are gone, replaced by `GetPushTokensForUsersRequest` (with `Kinds` and `ExcludeClientDeviceIds`). All services must be deployed together.
