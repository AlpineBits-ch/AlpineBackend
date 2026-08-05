# QR cross-device login - desktop / web frontend guide

Lets a signed-out Tauri or web client log in by having an already-signed-in phone scan and
approve a QR code, instead of typing a password. Works like Discord's QR login: any device can
initiate a pairing, and the device that approves it does **not** transfer its own session - the
new device gets its own fully independent access + refresh token pair.

This flow is desktop/web-only. Mobile never displays a QR code in this feature - it only scans.

## 1. Start a pairing code

```
POST https://api.venta.gg/api/v1/identity/qr-login/start
Content-Type: application/json

{ "deviceName": "Chrome on Windows", "deviceType": "Web" }
```

`deviceType` is one of `"Desktop"`, `"Mobile"`, `"Web"` - use `"Desktop"` for the Tauri app.
`deviceName` is a human-readable label shown to the phone at scan time and later in the "manage
devices" list (§4) - make it something like `"{browser} on {OS}"` or `"Echo Desktop - {hostname}"`.

Response:
```json
{ "code": "3fa2b1...", "expiresInSeconds": 180 }
```

Render `code` as a QR code (plain text payload, no custom URI scheme). The code and everything
derived from it expires in 3 minutes - if the user hasn't scanned it by then, call `/start` again
for a fresh code rather than trying to reuse the expired one.

## 2. Poll for status

```
GET https://api.venta.gg/api/v1/identity/qr-login/status/{code}
```

Poll roughly every 1.5 seconds. Response:
```json
{ "status": "pending" }
```

`status` is one of:
- `pending` - nobody has scanned it yet.
- `scanned` - a phone scanned it and is showing a confirmation prompt. Update the UI to
  "Confirm on your phone".
- `approved` - the phone approved it. Immediately move to step 3.
- `denied` - the phone explicitly rejected it. Show an error and let the user retry (call
  `/start` again - a denied code cannot be re-approved).

`404 Not Found` means the code expired or never existed - treat the same as `denied` and offer to
generate a new one.

## 3. Exchange the approved code for tokens

Once `status` is `approved`, redeem it at the normal token endpoint using a custom grant type -
this is the only place tokens are ever issued, so treat the response exactly like a password-grant
login:

```
POST https://api.venta.gg/connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:echo:params:oauth:grant-type:qr_login&qr_code={code}
```

Response (standard OAuth token response):
```json
{
  "access_token": "...",
  "refresh_token": "...",
  "token_type": "Bearer",
  "expires_in": 3600
}
```

The pairing code is single-use - it's deleted server-side the moment this call succeeds, so don't
retry it after a successful exchange, and don't call this before `status` is `approved` (it will
return `401 Unauthorized`).

## 4. Managing sessions ("logged-in devices")

Every login (password, Steam, or QR) creates a session. Once signed in, a device can list and
revoke sessions on the account - useful for a settings screen showing "Chrome on Windows · this
device", "iPhone", etc.

```
GET https://api.venta.gg/api/v1/identity/sessions
Authorization: Bearer <access_token>
```
```json
[
  {
    "id": "lgsn_...",
    "deviceName": "Chrome on Windows",
    "deviceType": "Web",
    "ipAddress": "203.0.113.4",
    "createdAt": "2026-07-30T12:00:00Z",
    "lastUsedAt": "2026-07-30T12:05:00Z",
    "isCurrent": true
  }
]
```

```
DELETE https://api.venta.gg/api/v1/identity/sessions/{id}
Authorization: Bearer <access_token>
```

Revoking a session blocks it from refreshing its access token going forward. Its current access
token (if any) keeps working until it naturally expires (up to ~1 hour) - this is a known
limitation of stateless access tokens, not a bug; don't rely on revoke being instantaneous for the
revoked device's *current* token, only for its ability to stay logged in past that token's expiry.
