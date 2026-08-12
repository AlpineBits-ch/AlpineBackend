# Web Push - frontend integration guide

Everything the browser client needs to receive notifications when its tab is closed. Written to be
worked from independently: every endpoint, every field, and every failure mode spelled out, with no
backend reading required.

Applies to Alpine running in a browser. The desktop (Tauri) and mobile builds keep using FCM and APNs
and need nothing from this document.

## URLs in this document

Every URL is a public gateway URL (`https://api.venta.gg`) and is written out in full. A self-hosted
instance substitutes its own `INSTANCE_URL`.

---

## 1. The three things that will surprise you

**1. Every push you receive must draw a notification.** Chrome refuses a subscription unless the page
asks for `userVisibleOnly: true`, and then holds you to it: if your service worker handles a `push`
event without calling `showNotification`, the browser draws its own *"This site has been updated in
the background"* toast instead. That is a notification you did not write, for an event the user was
not meant to see. Because of this the server never sends a browser a data-only push - presence
changes, MLS commit nudges and call cancellations are filtered out server-side, so anything that
arrives here is meant to be shown.

**2. The payload is the same object the mobile clients already parse.** It is the FCM data dictionary,
serialised as JSON. The keys in §5 are exactly the keys `MessagePushPayload.tryParse` (Dart) and
`NotificationService.swift` read. This is deliberate - one payload contract across three clients, so
the routing ids and the hidden-content rules cannot drift apart.

**3. An instance may not have Web Push at all.** A self-hoster with no VAPID keypair configured gets
`404` from §2 and no push. Capability-gate the whole feature on that response rather than letting
`PushManager.subscribe` throw.

---

## 2. Get the application server key

```
GET https://api.venta.gg/api/v1/users/push/vapid-public-key
```

Anonymous - no bearer token. You need it before the permission prompt, which is before you can be
sure you have a session.

**200:**

```jsonc
{ "publicKey": "BJ3x…" }   // base64url, unpadded, 87 chars: an uncompressed P-256 point
```

**404:** this instance has no VAPID keypair. Web Push is off. Do not prompt, do not subscribe, hide
the toggle. This is a supported configuration, not an error.

The Push API wants the key as a `Uint8Array`, so decode the base64url yourself - do not pass the
string through:

```js
const decode = (b64url) => {
  const b64 = (b64url + '='.repeat((4 - b64url.length % 4) % 4)).replace(/-/g, '+').replace(/_/g, '/');
  return Uint8Array.from(atob(b64), c => c.charCodeAt(0));
};

const registration = await navigator.serviceWorker.ready;
const subscription = await registration.pushManager.subscribe({
  userVisibleOnly: true,                        // required - see §1
  applicationServerKey: decode(publicKey),
});
```

The key is effectively permanent. If it is ever rotated, every existing subscription dies at once and
each browser has to re-subscribe - so treat a changed key as "re-subscribe everyone", not as a
routine refresh.

---

## 3. Register the subscription

```
POST https://api.venta.gg/api/v1/users/self/push-token
Authorization: Bearer <token>
```

```jsonc
{
  "kind": "WebPush",
  "endpoint": "https://fcm.googleapis.com/fcm/send/…",  // subscription.endpoint
  "p256dh":   "BM9…",                                   // getKey('p256dh'), base64url, 87 chars
  "auth":     "k9Z…",                                   // getKey('auth'),   base64url, 22 chars
  "deviceId": "…"                                       // optional; same value as X-Device-Id
}
```

There is **no `token` field** for `kind: "WebPush"` - a browser has no token. The endpoint is the
routable identity and the server stores it in the same column an FCM token goes in.

`PushSubscription.toJSON()` gives you all three fields directly:

```js
const { endpoint, keys: { p256dh, auth } } = subscription.toJSON();
```

| Status | Meaning |
|---|---|
| `201 Created` | New subscription stored. |
| `202 Accepted` | This endpoint was already known and has been re-pointed at you, with the keys refreshed. |
| `400 Bad Request` | The subscription is unusable. The body is a plain-text sentence naming the field. |

**Re-send this on every app start**, not only the first time. A browser can silently re-subscribe
against the *same* endpoint with *new* keys, and if the server keeps the old pair the push service
still returns `201` while the browser can no longer decrypt anything. Re-registering is idempotent -
one row per endpoint, enforced by a unique index.

A `400` is worth surfacing in dev. It means the subscription would have failed silently once per
notification on a background path with nobody watching, so it is refused here instead. Reasons:

- `endpoint must be an absolute https URL.`
- `p256dh must be a base64url uncompressed P-256 point (65 bytes).`
- `auth must be 16 base64url-encoded bytes.`

---

## 4. Unregister at sign-out

```
DELETE https://api.venta.gg/api/v1/users/self/push-token?endpoint=<url-encoded endpoint>&kind=WebPush
Authorization: Bearer <token>
```

`endpoint` is an alias for `token` - either spelling deletes the same row, and `endpoint` exists
because that is the name a browser knows the value by. `kind` is optional and narrows the delete to
one transport.

`204` on success, `404` if there was nothing to delete, `400` if you send neither `token` nor
`endpoint`.

Call `subscription.unsubscribe()` too, but do not rely on it: unsubscribing in the browser tells the
push service, not us. Without the `DELETE` the row survives and is charged one HTTP request per
notification, forever.

**You do not have to clean up dead subscriptions yourself.** When a push service answers `404` or
`410` the server deletes the row on its own. Only those two statuses - a `429` or a `5xx` is the push
service having a bad minute and must never un-subscribe anybody.

---

## 5. The payload your service worker receives

`event.data.json()` gives a **flat object of strings** - every value is a string, including the
numeric and boolean-ish ones.

| Key | Always? | Notes |
|---|---|---|
| `type` | yes | `"message"` today. Switch on it; more types will come. |
| `messageId` | yes | |
| `contextId` | yes | Conversation or channel id - whichever this message belongs to. |
| `recipientUserId` | yes | Which signed-in account this is for. Needed to find that account's MLS state. |
| `encrypted` | yes | `"1"` or `"0"`. |
| `body` | yes | The text to show. Already the placeholder when hidden or encrypted. |
| `conversationId` | when a DM | |
| `channelId` / `guildId` | when a channel message | |
| `senderName` | unless hidden | |
| `senderAvatarUrl` | unless hidden, and when set | |
| `authorId` | unless hidden | |
| `ciphertext` | when encrypted and it fits | Base64 MLS ciphertext, ready for openmls. |
| `mlsGeneration` | when encrypted | Stringified integer. |
| `truncated` | sometimes | `"1"` means there *was* ciphertext and it did not fit. |
| `hidden` | sometimes | `"1"` means the reader has "hide push content" on. |

### The three body cases, in the order you must check them

1. **`hidden === "1"`** - the reader turned on "hide notification content". You get routing ids,
   `body` = `"You have a new message"`, and nothing else: no sender name, no avatar, and no
   ciphertext either. Show `body` verbatim and do not attempt to decrypt. Reconstructing the message
   on a lock screen is the exact thing that setting exists to prevent.
2. **`encrypted === "1"`** - `body` is `"You have a new encrypted message"`. If `ciphertext` is
   present, decrypt it and show the real text; if `truncated === "1"` the message was too long to
   travel and the placeholder is the best you can do. Either way you have a notification to draw.
3. **Otherwise** - `body` is the message text, truncated to 500 characters.

### Size

The whole JSON body must fit **4079 bytes** before encryption, and the server enforces that: the
`ciphertext` is dropped first (setting `truncated`), then the body is replaced with the placeholder.
The routing ids always survive - a notification you cannot click through to a conversation is barely
a notification.

### Coalescing

Each push carries an RFC 8030 `Topic` derived from the conversation, so a browser that has been
closed for an hour is woken by the newest message in a conversation rather than by every message in
it. Use the same conversation id as your `notification.tag` so the foreground behaviour matches.

### Sketch

```js
self.addEventListener('push', (event) => {
  const data = event.data.json();

  // Never return without showing something - see §1.
  event.waitUntil((async () => {
    let body = data.body;
    if (data.hidden !== '1' && data.encrypted === '1' && data.ciphertext) {
      body = await tryDecrypt(data.recipientUserId, data.ciphertext, Number(data.mlsGeneration)) ?? body;
    }

    await self.registration.showNotification(data.senderName ?? 'venta', {
      body,
      icon: data.senderAvatarUrl,
      tag: data.conversationId ?? data.channelId ?? data.contextId,
      data,
    });
  })());
});
```

---

## 6. CORS, since you are cross-origin

The web client is served from `https://app.venta.gg` and calls `https://api.venta.gg`, so every
request is cross-origin. Nothing to configure client-side, but two things are worth knowing:

- **Every call preflights.** `Authorization` and `X-Device-Id` are both non-safelisted request
  headers, so even a plain `GET` sends an `OPTIONS` first. Preflights are cached for two hours
  (Chrome's ceiling).
- **Only `Date`, `ETag` and `Retry-After` are readable from script.** Any other response header
  arrives and is hidden by the browser - `headers.get(...)` returns `null` with no error. If you need
  a new one, it has to be added to the server's expose list.

The realtime hub's `POST /api/v1/ws/hub/negotiate` is a *credentialed* cross-origin request, because
`@microsoft/signalr` defaults `withCredentials` to `true`. That is already allowed for; just do not
set the origin header yourself.

---

## 7. Deep links arrive as https, not `venta://`

Flows that end in an out-of-band redirect used to send the browser to `venta://…`, which a browser
cannot follow - the tab dies on an unknown protocol or offers to open a desktop app the user
deliberately did not install. For the web client the server now redirects to your own origin instead,
on the same paths the custom-scheme links carried:

| Flow | Where you land | Wired? |
|---|---|---|
| Steam link/login | `https://app.venta.gg/steam-auth?status=…` | yes |
| Discord import | `https://app.venta.gg/discord-import?jobId=…` | yes |
| Bot install | `https://app.venta.gg/install-bot` | **not yet** - the path is reserved, no server redirect targets it |
| Invite | `https://app.venta.gg/invite/{code}` | **not yet** - as above |

Only the first two flows redirect today; the other two paths are declared so the names cannot drift
when they are wired, and are pinned by a test. Do not wait on them.

The paths are unchanged from the custom-scheme links, so a dispatcher that matches on path and query -
`url.includes('steam-auth')`, `/invite\/([^/?#]+)/` - keeps working with no changes.

**Steam** picks the target from the `returnUrl` the client passes when it starts the flow.
**Discord import** takes `returnUrl` on `GET /api/v1/discord/start`. Both are allowlisted by exact
match: an unrecognised value silently falls back to the desktop deep link rather than being honoured,
because these redirects carry single-use tickets and an open redirect here would hand them away.

---

## 8. Operator configuration

For whoever deploys the instance, not for the client:

| Variable | Meaning |
|---|---|
| `VAPID_PUBLIC_KEY` | Uncompressed P-256 point, base64url unpadded. Served by §2. |
| `VAPID_PRIVATE_KEY` | Raw 32-byte P-256 scalar (`d`), base64url. **Not** a PKCS#8 or PEM blob. |
| `VAPID_SUBJECT` | `mailto:` or `https:` URI a push service can use to reach you. Defaults to `INSTANCE_URL`. |
| `APP_DOMAIN` | Where the web client is served. Defaults to the `app.` sibling of the API host. |
| `CORS_ALLOWED_ORIGINS` | Extra allowed origins, comma/semicolon/space separated. Additive. |

Leave the two keys unset and the instance simply has no Web Push - §2 answers `404` and nothing is
ever sent.

Generating a pair:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out vapid.pem
# public: 65-byte uncompressed point, base64url unpadded
openssl ec -in vapid.pem -pubout -outform DER \
  | tail -c 65 | basenc --base64url | tr -d '=\n'
# private: raw 32-byte scalar, base64url unpadded
openssl ec -in vapid.pem -outform DER \
  | tail -c +8 | head -c 32 | basenc --base64url | tr -d '=\n'
```

Verify before deploying: the public key must decode to 65 bytes starting `0x04`, and the private key
to 32 bytes. A mismatched pair fails at startup rather than as a `401` from every push service.

---

## 9. Open item

The encryption is checked against the RFCs' specified structure and round-tripped through an
independently written decryptor, but it has **not yet been verified against a real browser
subscription**. The first end-to-end push to a real Chrome or Firefox subscription is the check that
closes that gap - if a notification never arrives, say so rather than assuming the client is at
fault.
