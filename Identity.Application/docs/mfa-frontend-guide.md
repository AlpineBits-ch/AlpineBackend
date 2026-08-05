# MFA / 2FA (TOTP) - frontend integration guide

Backend support for authenticator-app-based two-factor authentication is done and live. Backup
(recovery) codes are included. SMS/email-based 2FA is not part of this pass - authenticator app
only (Google Authenticator, 1Password, Authy, etc.).

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call the
Identity microservice directly.

## Enrollment flow (settings screen, while already logged in)

1. `POST https://api.venta.gg/api/v1/identity/user/mfa/enroll` (empty body, bearer auth)
   ```json
   { "secret": "JBSWY3DPEHPK3PXP", "otpAuthUri": "otpauth://totp/Venta:user%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=Venta&algorithm=SHA1&digits=6&period=30" }
   ```
   Render `otpAuthUri` as a QR code (any client-side QR lib) for scanning, and show `secret` as
   selectable text underneath for manual entry - standard TOTP enrollment UX, nothing app-specific
   about it. **MFA is not yet enabled at this point** - calling `enroll` again before `enable`
   just re-returns the same pending secret.
2. User scans the QR (or types the secret) into their authenticator app, then enters the 6-digit
   code it's currently showing.
3. `POST https://api.venta.gg/api/v1/identity/user/mfa/enable`
   ```json
   { "code": "123456" }
   ```
   `400 Bad Request` ("Invalid code") if the code doesn't verify - let them retry, the secret from
   step 1 is still valid. On success:
   ```json
   { "recoveryCodes": ["a1b2-c3d4", "e5f6-g7h8", "... x8 total"] }
   ```
   **Show these exactly once** and tell the user to save them somewhere safe (password manager,
   printed, etc.) - there is no "view my recovery codes" endpoint; losing them means regenerating
   a fresh set (see below), which invalidates the old ones.

## Disabling MFA

`POST https://api.venta.gg/api/v1/identity/user/mfa/disable`
```json
{ "password": "..." }
```
Requires the account password (not a TOTP code - if they're disabling MFA they may have lost
access to their authenticator). `400 Bad Request` on wrong password. On success, MFA is off and
the old secret is invalidated - re-enrolling later starts fresh, old codes/secret won't work.

## Regenerating recovery codes

`POST https://api.venta.gg/api/v1/identity/user/mfa/recovery-codes`
```json
{ "password": "..." }
```
Returns a fresh `recoveryCodes` array (same shape as enable), **invalidating all previously issued
codes**. Surface this as a "generate new codes" action in account settings, with a confirmation
step warning that old codes stop working immediately.

## Logging in with MFA enabled

This is the part that touches your existing login flow, not just a new settings page. Login still
goes through the standard OAuth2 password grant at `https://api.venta.gg/connect/token` - MFA adds
one optional field and one new error response:

```
POST https://api.venta.gg/connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=password&username={email}&password={password}&mfa_code={code}
```

- Try logging in **without** `mfa_code` first, same as today.
- If the response is `401` with body `mfa_required`, the account has MFA enabled and you didn't
  supply a code - show a "enter your 6-digit code" screen, then retry the same request with
  `mfa_code` set to what the user enters.
- If the response is `401` with body `mfa_invalid`, the code was wrong (or expired - TOTP codes
  are time-windowed) - let them retry. This also accepts a recovery code in the same field (an
  8-character code instead of 6 digits) as a fallback if they've lost their authenticator device.
- A plain `401` with no MFA-specific body means the username/password itself was wrong, same as
  before this feature - don't show an MFA code prompt in that case.

## Rendering guidance

- Standard 6-digit code input (auto-advancing boxes or a single field, your call) for both the
  enable-confirmation step and the login-challenge step - same component works for both.
- Recovery codes: show as a copyable list, ideally with a "download as text file" affordance -
  this is a one-time reveal, treat it like showing a generated password.

## Known limitations (v1)

- No "remember this device" / trusted-device skip - MFA is required on every login once enabled,
  no exceptions.
- No SMS/email fallback factor - authenticator app (or a recovery code) is the only way in if
  enabled.
- Enrollment has no explicit "cancel" endpoint - an abandoned enrollment (secret generated,
  never confirmed via `/enable`) just sits inert; calling `/enroll` again reuses it rather than
  generating a new one, so nothing leaks or needs cleanup.
