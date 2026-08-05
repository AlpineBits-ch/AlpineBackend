# QR cross-device login - mobile frontend guide

Lets a signed-in phone approve a login on a desktop/web client by scanning a QR code it displays.
The phone's own session is never touched - approving just authorizes the *other* device to get its
own independent login. Mobile never shows a QR code itself in this feature, it only scans one.

All three calls below require the phone's normal `Authorization: Bearer <access_token>` header -
the user must already be signed in on the phone to use this.

## 1. Scan

The QR code's payload is a plain opaque string (no URI scheme, no JSON) - pass it straight through
as `code`.

```
POST https://api.venta.gg/api/v1/identity/qr-login/scan
Authorization: Bearer <access_token>
Content-Type: application/json

{ "code": "3fa2b1..." }
```

Response:
```json
{ "deviceName": "Chrome on Windows", "deviceType": "Web" }
```

`404 Not Found` means the code is expired, unknown, or was already scanned/decided by someone
else - show a generic "this code is no longer valid" error and let the user try scanning again.

Use the response to render a confirmation screen: **"`{deviceName}` wants to log in - is this
you?"** with Approve / Deny buttons. Don't auto-approve on scan - a code sitting on someone else's
screen (a phishing attempt, or just a public monitor) should never log in silently.

## 2. Approve or deny

```
POST https://api.venta.gg/api/v1/identity/qr-login/approve
Authorization: Bearer <access_token>
Content-Type: application/json

{ "code": "3fa2b1...", "approve": true }
```

`204 No Content` on success. Send `"approve": false` if the user taps Deny.

Notes:
- Only the same account/session that called `/scan` can call `/approve` for that code - `403
  Forbidden` if a different signed-in user tries.
- This must be called within the same ~3-minute window the code was generated in; after that the
  desktop side will show it as expired regardless of what `/approve` returns.
- There is no token or session data in the scan/approve responses - the desktop client redeems the
  approval for its own tokens separately, so nothing sensitive to the phone's own account passes
  through this flow.
