# Password reset - frontend integration guide

Backend support for a "forgot password" flow is done and live. It mirrors the existing
email-verification-code flow exactly - same short-code-over-email UX, different cache key and
email template, so if you've already built a code-entry screen for signup verification, this is
largely the same component with different copy.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call the
Identity microservice directly.

## Flow

1. User enters their email/username on a "Forgot password?" screen.
2. `GET https://api.venta.gg/api/v1/identity/user/request-password-reset?email={email}` - always
   returns `202 Accepted`, whether or not that email/username has an account. Don't infer
   account existence from the response; this is deliberate (avoids leaking which emails are
   registered).
3. If an account exists, they receive a 6-character code by email, valid for **15 minutes**
   (longer than the 5-minute signup verification code, since "digging up a reset email" tends to
   take longer than "finishing signup you just started").
4. User enters the code + a new password on the next screen.
5. `POST https://api.venta.gg/api/v1/identity/user/reset-password`
   ```json
   { "email": "user@example.com", "code": "a1b2c3", "newPassword": "..." }
   ```
   - `200 OK` on success - password is changed immediately, no further step needed. Prompt them to
     log in again with the new password (existing sessions are not force-logged-out by this).
   - `400 Bad Request` with `"Invalid or expired code"` - wrong code, expired code, or unknown
     account (same message either way, again to avoid leaking account existence).
   - `400 ValidationProblem` (standard ASP.NET shape, `errors.newPassword: string[]`) - the new
     password failed the server's password policy (length/complexity). Render these messages
     directly; they're already user-facing ("Passwords must have at least one non alphanumeric
     character.", etc.).

## Rendering guidance

- Two screens: "enter your email" → "enter code + new password", same shape as email
  verification - reuse that flow's layout if you have one.
- Resending: calling `request-password-reset` again while a code is still valid returns the
  *same* code to the email (not a new one) - this is intentional server-side behavior, not
  something to work around client-side. A "resend code" button can safely just call the endpoint
  again.
- No rate limiting is enforced beyond the 15-minute code lifetime - don't build UI that assumes a
  cooldown exists server-side today.

## Known limitations (v1)

- Resetting the password does not invalidate other active sessions/refresh tokens - if that
  matters for your threat model (e.g. "someone else has my session open"), mention it in the UI
  copy rather than assuming it happens automatically.
- No email notification sent *after* a successful reset ("your password was just changed") -
  something to flag if you want defense-in-depth against silent account takeover via a leaked
  reset code.
