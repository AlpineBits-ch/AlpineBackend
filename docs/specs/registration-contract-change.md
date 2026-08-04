# Registration contract change — frontend migration guide

`POST /api/v1/identity/authentication/register` has changed, and **it is breaking**. The response is
now the same whether or not the email address already has an account, and the `userId` it used to
return is gone.

Everything below is a **public URL through the gateway (`https://api.venta.gg`)** — never call the
Identity microservice directly. Note that `/connect/token` has no `identity` segment: it is proxied
through unchanged because it is an OAuth2 endpoint whose address is part of the OIDC discovery
document.

## What changed, in one table

| | Before | After |
|---|---|---|
| New address | `200 OK` + `{"userId": "..."}` | `202 Accepted` + fixed body |
| Address already registered | `400 Bad Request` + `"Email already exists"` | `202 Accepted` + **the same** fixed body |
| Username already taken | `400` + `"Could not create the account."` | `400` + `"That username is already taken."` |
| Account id in the response | yes | **no** |
| Mail to the existing account holder | none | "someone tried to sign up with your address" |

The request body is unchanged. Nothing else about signup moved.

## Breaking elements — the checklist

1. **`200` is never returned any more.** Any client that checks `status === 200` will treat every
   successful registration as a failure. Accept `202`, or check `response.ok` / 2xx.
2. **`userId` is gone from the body.** Any client that reads it will get `undefined`. See
   "Where the user id comes from now" below.
3. **`400 "Email already exists"` is gone.** Any client with an "email already in use" branch will
   never enter it again. Delete it — do not try to reconstruct it from anything else.
4. **A `400` no longer means "the address is taken".** It now means the birth date, the username or
   the address itself was unacceptable. Read `propertyName` to route it.
5. **Success no longer means an account was created.** It means "your request was accepted and, if
   that address could be registered, mail is on the way". Copy must not say "account created".

## The new contract

### Request (unchanged)

```http
POST /api/v1/identity/authentication/register
Content-Type: application/json
```

```json
{
  "email": "user@example.com",
  "password": "SecurePass123!",
  "username": "someuser",
  "birthDate": "2000-01-01T00:00:00Z"
}
```

### Response — 202 Accepted

Byte-for-byte identical for a free address and a taken one. There is nothing per-request in it.

```json
{
  "status": "verification_pending",
  "message": "If that address can be registered, we have sent it an email. Check your inbox to continue."
}
```

Branch on `status`, not on `message` — the wording may be reworded, the discriminator will not.
`message` is safe to render verbatim if you have no localised copy of your own.

### Response — 400 Bad Request

An array of validation failures (the shape this endpoint already returned):

```json
[
  {
    "propertyName": "Username",
    "errorMessage": "That username is already taken.",
    "attemptedValue": null,
    "customState": null,
    "severity": "Error",
    "errorCode": null,
    "formattedMessagePlaceholderValues": null
  }
]
```

## Every status this endpoint can return

| Status | When | What the client does |
|---|---|---|
| `202` | The request was accepted. Either an account was created, or the address already had one. **You cannot tell which, and that is the point.** | Go to the "check your email" screen |
| `400` | Taken username, birth date under 13, missing/malformed email — see the table below | Show the field error, stay on the form |
| `400` `propertyName: "General"` | The account genuinely could not be created (a database failure, or a lost race). Message is always `"Could not create the account."` | Generic "something went wrong, try again" |
| `500` | Unhandled server fault, including the 15-second internal timeout | Generic retry |

There is **no** `409`, and there never was. Do not add one speculatively.

### The `400` bodies you will actually see

| Cause | `propertyName` | `errorMessage` | `errorCode` |
|---|---|---|---|
| Username taken | `Username` | `That username is already taken.` | `null` |
| Email missing/blank | `Email` | `Email cannot be empty` | `null` |
| Email malformed | `Value` | `Invalid email format` | `EmailInvalidFormat` |
| Disposable email domain | `Value` | `One-time or disposable email addresses are not allowed.` | `EmailDisposableNotAllowed` |
| Under 13 | `""` (empty) | `Age must be greater than 13` | `LessThanValidator` |
| Creation failed | `General` | `Could not create the account.` | `null` |

Two quirks worth coding around rather than being surprised by: the age failure has an **empty**
`propertyName` (it validates a bare date, which has no property name to report), and the email-format
failures report `Value` because they come from the `Email` value object rather than from the request
DTO. Map both to your email/birth-date fields by `errorCode` if you want to be precise.

## Username collisions are still distinguishable — deliberately

`400` with `propertyName: "Username"` is the one refusal that still tells the caller something
specific, and it is kept on purpose:

- Usernames are **already discoverable** in this product. `DiscoverableByUsername` defaults to true
  and looking a username up is how friend requests are addressed, so "that name is taken" reveals
  nothing that any account holder cannot already establish. Email addresses are the opposite —
  nothing in the product resolves one user's address for another.
- The server owns the username namespace. A user who is not told "pick a different one" cannot
  proceed. Folding this into the uniform `202` would silently drop those signups and send the user to
  an inbox that will never receive anything.

Two implementation details this depends on, in case you are tempted to "improve" it:

- The username is checked **before** the address is looked up, so this refusal is a function of the
  username alone. If it were checked after, submitting a known-taken username with an address you
  wanted to probe would answer `400` for a free address and `202` for a registered one — the same
  leak, wearing the fix as a disguise.
- The message is a fixed sentence. An earlier fix stopped this endpoint echoing raw Postgres
  constraint text (table, column and index names) to anonymous callers on a duplicate username; do
  not surface anything more detailed than the string above, and do not ask us to.

A live "is this username free?" check as the user types is fine and is the better UX anyway — it
leaks nothing this response does not.

## Where the user id comes from now

Registration cannot return it: the same response has to cover an address that already has an account,
and there is no honest id to put there. Returning a fabricated one would be worse than the leak — a
client that stores it and acts on it fails later, somewhere else, silently.

**The id is the `sub` claim of the access token**, available as soon as the user signs in. If you
prefer not to parse the token, `GET /api/v1/identity/users/self` returns the same value as `id`.

Practically: nothing between registration and first sign-in needs the id anyway. Every step of the
verification flow is keyed by the email address (or the username) the user just typed.

## The full flow, end to end

### 1. Register

```http
POST /api/v1/identity/authentication/register
```

→ `202`. Store the email address the user entered in local state; the next two steps need it.

### 2. Show "check your email" — in both cases

The user receives one of three things, and **you cannot tell which**:

| Their situation | What lands in the inbox |
|---|---|
| Address was free | Welcome email with a **6-digit** verification code, valid **5 minutes** |
| Address already has an unverified account | The same verification code, so they can finish the signup they abandoned |
| Address already has a verified account | "Someone tried to sign up with your email address" — no code, and a pointer to sign in or reset their password |

So the screen after registration is the code-entry screen, with copy that works for all three. See
"What to render" below.

### 3. Resend the code (optional, user-triggered)

```http
GET /api/v1/identity/user/generate-verification-code?email={emailOrUsername}
```

Always `202`, for every address, whether or not it exists. Never infer anything from the response.
Requesting again while a code is still live re-sends the **same** code rather than minting a new one,
so a "resend" button is safe to press repeatedly and will not invalidate the code already sitting in
the user's inbox.

### 4. Verify the address

```http
GET /api/v1/identity/user/verify-email?email={emailOrUsername}&code={code}
```

- `200 OK` — verified, move to sign-in.
- `400 Bad Request` — `"Invalid or expired verification code - request a new one."` This is the
  **only** refusal, and it covers wrong code, expired code, too many wrong attempts, unknown address
  and already-verified account. Render it as-is and offer "resend". The code dies after 5 wrong
  guesses, which is another case of the same message.

### 5. Sign in

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=password&username={usernameOrEmail}&password={password}&client_id=echo&scope=offline_access
```

- `200` → `{ "access_token": "...", "refresh_token": "...", "expires_in": ... }`. Decode `sub` for
  the account id.
- `401` → bad credentials **or** no such account (identical answer, on purpose).
- `401` with body `mfa_required` / `mfa_invalid` → the account has MFA; resubmit with `mfa_code`.
- `403 "Email not verified."` → send them back to step 2/3. This one is precise on purpose: the
  caller has already proved they hold the password, so nothing is leaked by telling them.
- `423` → temporarily locked after too many wrong passwords.

### 6. Read the account

```http
GET /api/v1/identity/users/self
Authorization: Bearer {access_token}
```

Returns `id` — the same value as the token's `sub` — plus the profile, preferences and privacy
settings.

> **Self-hosted instances with `AUTH_REQUIRE_USER_EMAIL_VERIFICATION=false`** skip the whole
> verification detour: the account is created already confirmed, no mail is sent, and step 5 works
> immediately after step 1. The registration response is identical, so the client cannot detect this
> — offering "resend code" and "I'll do this later, sign in" on the same screen covers both.

## What to render

The honest screen after a `202` is **"Check your email"**, showing the address they typed, a code
input, and a resend button. That single screen is correct in all three cases in the table above.

Copy that works:

> **Check your email**
> If `user@example.com` can be registered, we've sent it a message. Enter the code from that email to
> continue.
> Didn't get it? *Resend* — or *sign in* if you already have an account.

Copy that does not:

- ❌ "Account created!" — false whenever the address was already registered.
- ❌ "We've sent you a verification code." — false for an address with a verified account; they get a
  notice, not a code.
- ❌ "That email is already registered." — you no longer know that, and reconstructing it from timing
  or anything else is the thing this change exists to prevent.

Give the "already have an account? sign in" and "forgot password?" links equal weight on that screen.
For the user who really does already have an account, those links plus the notice in their inbox are
the whole recovery path, and they are the only affordance the server is allowed to give them.

One more consequence: **the user cannot be dropped straight into the app after registering.** There
is no session, no token and no id until they verify and sign in. If your current flow auto-signs-in
using the registration response, it has to become register → verify → sign in.

## Anti-flood behaviour you might notice in testing

The "someone tried to sign up" notice is capped at **3 per address per 24 hours**. The endpoint is
anonymous, so without a cap anyone could point it at a stranger's address in a loop and use signup as
a mail bomb. The HTTP response is identical whether or not a notice was actually sent — so if you are
hammering the same address in a test environment, expect the mail to stop and the `202`s to continue.
That is correct behaviour, not a bug.

## Why (please read before "improving" the error messages)

The old pair of responses was an **account enumeration oracle**. `POST /register` is anonymous:
anyone with a list of email addresses could POST each one and read, straight off the status code,
which of them have accounts here — no login, no rate limit worth the name, and no trace on the
account. For a chat product that is not a trivial leak: it turns a breach dump, a mailing list or a
company directory into a membership list, which is exactly the input for targeted phishing ("your
Venta.gg account…"), and it exposed people whose presence on this platform is their business alone.
It also walked straight around the discoverability controls in `docs/specs/privacy.md` (T2-16), which
go to considerable trouble to make "not discoverable" and "does not exist" indistinguishable to a
logged-in viewer — while an unauthenticated caller could ask the same question at the front door.

The standard resolution is the one implemented here: accept everything, create nothing when the
address is taken, and tell **the address owner** rather than the caller. The person with a stake in
knowing that someone tried to sign up with their address is the person who owns it.

So: if a "helpful" `409 Conflict`, an `emailTaken: true` flag, or a distinguishable error message
would make the client simpler, the answer is no — that is the bug being fixed. The uniform response
is enforced by regression tests that compare status, headers and body between a known and an unknown
address, so an attempt to re-add it will fail CI rather than ship.

## Related

- `docs/specs/privacy.md` — "Anonymous account enumeration", the full list of pre-auth oracles and
  what was done about each
- `Identity.Application/docs/password-reset-frontend-guide.md` — the same code-over-email pattern,
  and the same "always 202" rule, for the forgot-password flow
- `Identity.Application/docs/mfa-frontend-guide.md` — the `mfa_required` / `mfa_invalid` branches of
  step 5
