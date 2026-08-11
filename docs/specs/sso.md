# auth.venta.gg - single sign-on

A hosted, branded sign-in surface for every Venta property that is not the chat client: the landing
site, the Isle web tools, community sites, anything we put up later. One account, one login screen,
one place a person manages the sites they have signed in to.

This is the fourth gateway-hosted site after `admin`, `support` and `status`, and it follows their
conventions (host-gated static files, no build step, same-origin API). It differs from them in one
important way: it is the only one that collects credentials, so it is the only one that gets a
content security policy, and it is the only one whose UI is load-bearing for the product's first
impression. **UX is a primary requirement here, not a finishing pass.**

---

## 1. What already exists

Worth stating plainly, because it is most of the hard part and it is easy to plan work that is
already done.

| Capability | Where | State |
| --- | --- | --- |
| OpenIddict 7.6 server, EF-backed | `Identity.Application/Program.cs:60-137` | Live |
| Password grant + lockout + TOTP/recovery MFA | `Controllers/ConnectController.cs:38-101`, `:295-316` | Live |
| Steam OpenID 2.0 login and account linking | `Services/Steam/SteamOpenIdService.cs`, `Controllers/SteamAuthenticationController.cs` | Live |
| QR cross-device pairing (start/scan/approve/redeem) | `Services/Qr/QrLoginService.cs`, `Controllers/QrLoginController.cs`, `ConnectController.cs:175-220` | Live |
| Mobile QR **approver** screen (camera, claim, confirm, approve/deny) | `venta-mobile: lib/features/settings/presentation/screens/qr_login_screen.dart` | Live |
| Session tracking + revocation (`session_id` claim, `LoginSession`) | `Domain/Entities/LoginSession.cs`, `Controllers/SessionController.cs` | Live |
| Register / verify email / reset password / MFA enrolment | `Endpoints/UserVerificationEndpoint.cs`, `PasswordResetEndpoint.cs`, `MfaEndpoint.cs` | Live |
| OpenIddict `applications`/`authorizations`/`tokens`/`scopes` tables | migration `20260423202855_AddOpenDicct` | Live |
| Host-gated static site hosting | `Echo/Sites/SiteHosting.cs` | Live |

**What does not exist**, and is the actual work:

1. No authorization-code flow. No `/connect/authorize`, `/connect/userinfo`, `/connect/logout`,
   `/connect/revoke`. `Program.cs:73-77` allows only password, refresh, client_credentials and the
   two custom grants. A website cannot federate against this today.
2. No browser session at the identity provider. Every grant is a bare API call; nothing persists a
   signed-in browser, so "single sign-on" has nothing to be single about.
3. No client registry. Exactly one client, `echo`, hardcoded at `Program.cs:288-324`.
4. No UI. `AddRazorComponents()` is registered at `Program.cs:156-157` and there is not one `.razor`
   page behind it.
5. Steam's return URL is a single global value (`STEAM_CLIENT_RETURN_URL`, default
   `venta://steam-auth`), so the flow can only ever land in the mobile app.
6. Steam login of an unlinked SteamID dead-ends at `no_account`
   (`SteamAuthenticationController.cs:143`).

---

## 2. Host and issuer model

**`https://auth.venta.gg` becomes the OIDC issuer.** This is a deliberate breaking change, taken
because an issuer is a permanent, publicly-quoted identity: every partner site bakes it into its
config, and moving it later is worse than moving it now while there are no partners.

Everything OIDC answers on the auth host:

```
issuer                  https://auth.venta.gg
authorization_endpoint  https://auth.venta.gg/connect/authorize
token_endpoint          https://auth.venta.gg/connect/token
userinfo_endpoint       https://auth.venta.gg/connect/userinfo
end_session_endpoint    https://auth.venta.gg/connect/logout
revocation_endpoint     https://auth.venta.gg/connect/revoke
jwks_uri                https://auth.venta.gg/.well-known/jwks
discovery               https://auth.venta.gg/.well-known/openid-configuration
```

No new YARP routes are needed. `ProxyConfig.cs` matches on **path only, never host** (`:129-160`),
so `/connect/**` and `/.well-known/**` already proxy to Identity on whatever hostname reaches the
gateway. `api.venta.gg/connect/token` keeps answering exactly as it does today; it simply stops
being the advertised address.

### 2.1 The issuer migration

**Flip it in one step. Every live token becomes invalid and everyone signs in again.** At roughly two
monthly actives that is a smaller cost than carrying a dual-issuer acceptance window through the rest
of this work, and a window that exists "just in case" is a window nobody ever gets round to closing.
The bar this has to clear is: a service restart and a fresh sign-in on each client fully recovers.

- Introduce `Env.AuthConfiguration.IssuerUrl` (`AUTH_ISSUER_URL`, defaulting to `https://` + the
  derived auth host, falling back to `INSTANCE_URL` so a self-hosted instance that never sets it
  keeps working).
- Replace the eight duplicated `AddJwtBearer` blocks with one shared extension. **Read §2.2 before
  writing it** - the obvious implementation rejects every token in the system.
- Change `Program.cs:68` to `options.SetIssuer(Env.AuthConfiguration.IssuerUrl)`.
- Deploy everything together. Tokens minted before the flip fail `ValidateIssuer` and their holders
  re-authenticate. Refresh tokens fail with them, so it is a real sign-out, not a silent renewal.

The signing keys do not change, so this is purely the `iss` string. No client validates `iss`: the
mobile app posts to `{baseUrl}/connect/token` and stores whatever comes back
(`venta_mobile/lib/features/auth/data/auth_api.dart`), and the admin console does the same
(`Echo/wwwroot/admin/app.js:98-119`).

> **Phase 0 spike: resolved, done.** OpenIddict 7.6 does **not** validate the request host against
> the configured issuer. A password grant sent with `Host: some-instance.example.org` to a server
> whose issuer is `https://api.venta.gg` is served normally and mints a token stamped with the
> configured issuer. So `api.venta.gg/connect/token` keeps answering after the issuer moves, and the
> flip cannot strand the shipped mobile app behind a store release.
>
> Pinned by `Identity.Tests/Controllers/IssuerHostBindingTests.cs` so an OpenIddict upgrade that
> changes its mind fails there rather than in production. This is also what makes the gateway's
> host-agnostic `/connect/**` route (`Echo/Proxy/ProxyConfig.cs:129-138`) correct rather than
> incidental.

### 2.2 The trailing slash, which is a live trap

The spike turned up something that has to be built around. **The `iss` claim is
`https://api.venta.gg/` - with a trailing slash** - because OpenIddict stamps `Uri.AbsoluteUri`, and
an absolute URI always has a path of at least `/`. `INSTANCE_URL` is written by an operator and
almost never has one.

Every service today sets `ValidIssuer = Env.GeneralConfiguration.InstanceUrl`, a string that does
**not** equal the `iss` in the tokens it accepts. This works only because those services also set
`Authority`, and the metadata document fetched from it contributes the slash-terminated form as an
additional accepted issuer. The explicit `ValidIssuer` has been decorative the whole time.

So the shared extension must either keep `Authority`, or list both spellings in `ValidIssuers`. A
tidy-looking rewrite that sets only `ValidIssuer` from configuration - the natural thing to write
when consolidating eight copies - rejects every token on every service simultaneously, and presents
as a signing-key failure rather than a string-comparison one. `Env.GeneralConfiguration.InstanceBaseUrl`
is especially tempting here and is exactly wrong: it deliberately trims the slash.

Pinned by `IssuerHostBindingTests.The_iss_claim_is_a_normalised_absolute_uri_and_keeps_its_trailing_slash`.

### 2.3 Why not a `.venta.gg` cookie

Because the authorize endpoint lives on the auth host, the SSO cookie can be host-scoped with the
`__Host-` prefix: no `Domain` attribute, no path scoping games, and it is structurally impossible
for it to be sent to `admin.`, `support.`, `status.` or the API. A domain-wide cookie would have
been the only alternative if authorize had stayed on `api.`, and it would have put a session
credential on four hosts that have no use for it.

---

## 3. The OIDC surface

Added to `Identity.Application/Program.cs`:

```csharp
options.SetIssuer(Env.AuthConfiguration.IssuerUrl);
options.SetAuthorizationEndpointUris("/connect/authorize");
options.SetEndSessionEndpointUris("/connect/logout");
options.SetUserInfoEndpointUris("/connect/userinfo");
options.SetRevocationEndpointUris("/connect/revoke");

options.AllowAuthorizationCodeFlow()
       .RequireProofKeyForCodeExchange();

options.UseAspNetCore()
       .EnableAuthorizationEndpointPassthrough()
       .EnableEndSessionEndpointPassthrough()
       .EnableUserInfoEndpointPassthrough();
```

PKCE is required for every client, confidential ones included. There is no implicit flow, no hybrid
flow, and no device-code flow - the QR pairing already covers the cross-device case better than
RFC 8628 would, and adding a second device flow would be two things to reason about.

### 3.1 `/connect/authorize`

A new `AuthorizationController` in `Identity.Application/Controllers/`. It is the only genuinely new
piece of protocol logic; everything else is reuse.

1. Read the request via `HttpContext.GetOpenIddictServerRequest()`. OpenIddict has already
   validated `client_id`, `redirect_uri`, `response_type`, scopes and the PKCE challenge - an
   invalid one never reaches the action.
2. Authenticate the SSO cookie scheme. If absent, expired, or `prompt=login`, or older than
   `max_age`: **do not render anything**. Stash the request and 302 to
   `/login?rq={requestId}`. The static site owns every pixel; the controller only ever redirects or
   completes.
3. Re-check the account on every pass, never trusting the cookie's snapshot: `IsSigninAllowed()`,
   `EmailVerifiedAt != null`, and the referenced `LoginSession` is not revoked. A ban or a
   session revocation has to take effect at the next authorization, not at the next cookie expiry.
4. Consent. `first_party` clients are pre-authorized and skip it (see §7). Others get
   `/consent?rq={requestId}` unless a stored `OpenIddictAuthorization` already covers the scopes.
   `prompt=consent` forces the screen regardless.
5. Mint a `LoginSession` for this client (§4.2), build the principal, `SignIn` with the OpenIddict
   scheme. OpenIddict issues the code and redirects.

`prompt=none` is supported and answers `login_required` / `consent_required` / `interaction_required`
without rendering. It is there for correctness, not because we expect to use it: third-party cookie
blocking has made hidden-iframe silent renewal unreliable, and partner sites should use refresh
tokens.

**The stashed request.** The redirect to the login page must not carry the OIDC parameters in the
query string - they are long, they leak `login_hint` into browser history and referrers, and a
hand-edited `redirect_uri` in the address bar is exactly the attack the registered-URI check exists
to stop. Instead the whole validated request is stored in Redis under an opaque
`Identifier.New("authrq")` for 10 minutes, single-use on completion, and only that id travels in the
URL. Same pattern as the Steam state blob (`SteamAuthenticationController.cs:29-34`).

A companion `GET /api/v1/identity/authorize-request/{rq}` returns the *display* projection of a
stashed request - client display name, logo, the scopes it asked for, and `login_hint` - so the login
page can say "Sign in to continue to Isle" instead of showing a naked form. It returns nothing
sensitive and nothing that lets a caller alter the request.

### 3.2 `/connect/userinfo`

`sub`, `preferred_username`, `email`, `email_verified`, `picture`, `updated_at`, gated on the granted
scopes. This is the first time we need one; downstream services read claims off the access token
today, but a partner site holding an opaque-to-it JWT should not be parsing it.

Related cleanup, in scope: `ConnectController.cs:256-259` currently gives **every** claim both
`access_token` and `id_token` destinations in a blanket loop. That was tolerable when the only client
was our own; it is not once a partner site receives an id_token. Replace with an explicit
destination map (`sub` and `session_id` to both; `email` to id_token only when `email` was granted;
`user_type` to the access token only).

### 3.3 `/connect/logout`

RP-initiated logout. Validates `post_logout_redirect_uri` against the client's registered list,
shows a one-tap confirmation ("Sign out of Venta?" plus which site asked), then clears the SSO
cookie and revokes the browser's `LoginSession`.

The confirmation is not optional: a bare `GET /connect/logout?post_logout_redirect_uri=...` that
signs a person out with no interaction is a one-click denial-of-service any page on the internet can
embed. Skipping it is only safe with a validated `id_token_hint`, which we accept as the exception.

Back-channel logout to other RPs is **out of scope for v1** and is called out here so it is a
decision rather than an omission: signing out of the IdP ends the SSO session, so no *new* sign-in
happens anywhere, but a partner site's own cookie survives until it expires. Revisit when there is
more than one partner site.

---

## 4. The SSO session

### 4.1 The cookie

```
__Host-venta_sso
  HttpOnly, Secure, SameSite=Lax, Path=/, no Domain
  sliding 14 days, absolute 30 days
```

`SameSite=Lax` and not `Strict`: the return leg from Steam and from a partner site's
`/connect/authorize` is a top-level cross-site GET, which `Strict` would strip, producing an
infinite re-login loop that only reproduces on the second sign-in.

The cookie is an ASP.NET Core cookie-auth ticket. This means adding a **second** authentication
scheme to Identity, which today registers only
`AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)` at `Program.cs:145`.
The bearer scheme stays the default so no existing `[Authorize]` changes meaning; the cookie scheme
is named and opted into explicitly by the authorize/logout/session endpoints.

Ticket contents: `sub`, `session_id` (the browser's `LoginSession`), `auth_time`, and `amr`
(`pwd`, `mfa`, `steam`, `qr`) so `max_age`, `prompt=login` and per-client policy have something real
to read.

### 4.2 Two kinds of session, on purpose

- **The browser session at the IdP.** One `LoginSession` created when a person signs in at
  `auth.venta.gg`, referenced by the cookie. Revoking it signs the browser out of the SSO.
- **The client session.** One `LoginSession` per RP that completes an authorization, carrying that
  RP's `session_id` claim. Revoking it kicks that one site.

This reuses the existing entity with no schema change and no migration, and it makes
`GET /api/v1/identity/sessions` (`SessionController.cs:21`) immediately useful as "sites and devices
you are signed in to". `DeviceName` carries the browser label; `DeviceType` is `Web`.

The alternative - one session shared by every RP - was rejected because it collapses "sign out of
this one site" and "sign out everywhere" into the same button.

### 4.3 Revocation still has the known hole

`PasswordResetEndpoint.cs:100-104` revokes sessions on reset, and refresh checks
`LoginSession.IsRevoked` (`ConnectController.cs:127-128`), but **nothing consults the security
stamp**, and an already-issued access token stays valid for up to its lifetime. That is documented
today in `qr-login-desktop-guide.md:110-113`. SSO does not make it worse and does not fix it; it is
restated here so nobody assumes an identity provider implies instant revocation.

---

## 5. The three ways in

### 5.1 Username and password

Straight reuse. The login page POSTs a password grant to `/connect/token` exactly as the admin
console does (`admin/app.js:242-263`), and on success calls a new
`POST /api/v1/identity/sso/session` with the resulting access token to establish the cookie, which
then returns the user to `/connect/authorize?rq=...`.

Note the endpoint refuses **usernames only** (`ConnectController.cs:40` uses `FindByNameAsync`),
while `AuthenticationController.FindUserByUsernameOrEmail:27` accepts either but belongs to a dead
route. A sign-in field labelled "Username or email" that silently fails for every email address is
the sort of thing that reads as "my account is broken". **In scope:** widen the password grant to
accept either, keeping the existing timing-equalised unknown-account path
(`CheckDummyAsync`, `:50-55`) intact for both lookups.

MFA reuses the `mfa_code` parameter and the `mfa_required` / `mfa_invalid` bare-401 contract
(`:295-316`). Recovery codes work because `RedeemTwoFactorRecoveryCodeAsync` is already the fallback.

### 5.2 QR from the mobile app

Fully built on both sides. The page calls `POST /api/v1/identity/qr-login/start`, renders `code` as
a QR (plain text payload, no URI scheme - `qr-login-desktop-guide.md:28`), polls
`/qr-login/status/{code}` every 1.5s, and redeems the approved code at `/connect/token` with the
`qr_login` grant. The phone side ships today.

Two things to respect and one to add:

- The code lives 3 minutes (`QrLoginService.cs:40`). The panel shows the remaining time and offers a
  fresh code on expiry rather than silently failing.
- The QR grant **deliberately skips MFA** (`ConnectController.cs:210-215`) because approval already
  required an authenticated, MFA-passed device. Do not add a second factor here.
- Add `amr: ["qr"]` to the cookie so a client that asks for `prompt=login` or a short `max_age` can
  tell how the person got in.

### 5.3 Steam

The redirect and assertion verification are done (`SteamOpenIdService.cs`). Two changes:

**Per-flow return URL.** `Env.Steam.ClientReturnUrl` is one global value defaulting to
`venta://steam-auth`, so today every Steam callback lands in the mobile app
(`SteamAuthenticationController.cs:169`). Carry the return target in the existing single-use state
blob (`SteamAuthState`, `:41`) and validate it against an allowlist - the mobile deep link plus the
auth host - rather than trusting it from the query. An unrecognised value falls back to the
configured default rather than redirecting anywhere a caller names. The existing default stays the
default, so the mobile flow is untouched.

**The unlinked-SteamID screen.** `HandleLoginAsync` returns `no_account` when nothing is linked
(`:143`). The auth site turns that into a real fork rather than an error:

```
   [ steam avatar ]   Marbleslide

   No Venta account is linked to this Steam profile yet.

   ( Sign in to link it )        <- primary; password/QR, then auto-links
   ( Create a new account )      <- normal registration, pre-linked on verify
```

Both doors produce an ordinary, fully-formed account. Creation still collects email, birthdate and
consent, because `UserConsent`, the age gate and the DSR/export machinery all assume they exist, and
an account that skips them is one that cannot reset a password, cannot be told it was banned, and
cannot answer a data request. The Steam identity is held in the single-use ticket
(`SteamOpenIdService.LoginTicketCacheKey`, 2 minutes) and applied once the account is verified.

`SteamLinkedEvent` / `SteamUnlinkedEvent` already publish on the bus, so nothing downstream changes.

---

## 6. Account screens

Sign-in is the reason for the site, but a person who cannot get in needs somewhere to go, and today
those flows exist only as endpoints the mobile app calls. All are wired to live routes:

| Route | Backing endpoint |
| --- | --- |
| `/login` | `/connect/token` + `/api/v1/identity/sso/session` |
| `/register` | `POST /api/v1/identity/authentication/register` |
| `/verify` | `GET /api/v1/identity/user/verify-email` (6 digits, 5 min) |
| `/forgot` | `GET /api/v1/identity/user/request-password-reset` |
| `/reset` | `POST /api/v1/identity/user/reset-password` (12 hex chars, 15 min) |
| `/consent` | authorize-request projection + `POST /connect/authorize` decision |
| `/logout` | `/connect/logout` |
| `/sessions` | `GET`/`DELETE /api/v1/identity/sessions` |

Two behaviours the page must get right because the backend deliberately will not:

- Every code entry gets **5 attempts and then the code is destroyed** (`OneTimeCodeService.cs:111-115`).
  The UI has to say so before the last one, not after.
- `request-password-reset` and `generate-verification-code` **always return 202**, by design, so that
  an unknown address is indistinguishable from a known one. The page therefore says "If that account
  exists, we sent a code" and must never imply delivery.

`/reset` additionally has to handle `ResetPasswordResultDto.MasterKeyRewrapRequired`
(`PasswordResetEndpoint.cs:126-169`): a reset can leave encrypted history unreadable. The page shows
what was lost and what the rewrap ticket recovers, rather than dropping the person on a success
screen that quietly lied.

---

## 7. Client registry

Config-driven, reconciled at startup into the OpenIddict tables that already exist - the same shape
as the `echo` bootstrap at `Program.cs:288-324`, generalised.

```json
AUTH_CLIENTS=[
  { "clientId": "venta-landing",
    "displayName": "Venta",
    "redirectUris": ["https://venta.gg/auth/callback"],
    "postLogoutRedirectUris": ["https://venta.gg/"],
    "scopes": ["openid", "profile", "email", "offline_access"],
    "firstParty": true,
    "public": true }
]
```

- `firstParty: true` pre-authorizes the client and skips the consent screen. Asking someone to
  consent to Venta sharing their name with Venta trains them to click through consent screens, which
  is precisely the habit that makes the screen worth showing to a real third party.
- `public: true` means no secret (browser SPA); PKCE is required either way. Confidential clients
  take their secret from `AUTH_CLIENT_SECRET_{CLIENTID}` and it is never written to the config blob.
- Reconciliation is **additive-then-diff**, like the existing `else` branch at `:312-324`: an
  existing row has its permissions and URIs updated rather than being recreated, so rotating a
  redirect URI does not orphan live authorizations. Removing a client from the config **disables**
  it rather than deleting it, so that a config typo cannot silently destroy consent history.
- `echo` keeps its existing permission set untouched and does **not** gain the authorization-code
  flow. The first-party clients are separate rows.

`RegisterScopes` at `Program.cs:93-96` already covers `email`, `profile`, `roles`. No change needed;
`openid` and `offline_access` are protocol scopes and stay unregistered on purpose (`:90-92`).

A runtime registration UI in the moderation console is deliberately **not** in v1. It is a new
privileged surface with secret rotation and audit requirements, and there are currently fewer
partner sites than there are people who would use the UI.

---

## 8. The site

`Echo/wwwroot/auth/{index.html, auth.css, app.js}`, hand-written, no build step, served by
`SiteHosting.cs` on `AUTH_DOMAIN`. Assets are referenced **site-root relative** (`/auth.css`, not
`/auth/auth.css`) - the folder is mounted at `/` on its own host, and getting this wrong has shipped
as a live 404 before (`Echo.Tests/Sites/SiteAssetPathTests.cs:11-16`).

`SiteHosting.cs` changes, mirroring the three existing sites: `AuthLabel`/`AuthDomainVariable`/
`AuthHost` at `:26-36`, a `UseSiteHostDiagnostics` line at `:56-58`, and

```csharp
app.ServeSite(Path.Combine(webRoot, "auth"), auth, iconProvider, AuthClientRoutes);

private static readonly string[] AuthClientRoutes =
    ["/login", "/register", "/verify", "/forgot", "/reset", "/consent", "/logout", "/sessions"];
```

An explicit list, never a catch-all, for the reason given at `:93-96`: the API answers on this host
too, and a blanket extension-less rewrite would answer a mistyped `/api/v1/...` with HTML.

### 8.1 Security headers - the one place they are not optional

The existing three sites set no CSP, and that is defensible: `support` is anonymous and `admin` holds
a token in `sessionStorage` behind a staff check. This site takes passwords. It gets, applied only on
the auth host:

```
Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self';
  img-src 'self' data: https://avatars.steamstatic.com; connect-src 'self';
  form-action 'self'; frame-ancestors 'none'; base-uri 'none'
X-Frame-Options: DENY
Referrer-Policy: no-referrer
X-Content-Type-Options: nosniff
Cache-Control: no-store        (on HTML and on every /connect/* response)
```

`frame-ancestors 'none'` is what makes clickjacking a non-issue on the consent and logout screens,
and it is why §3.1 does not pretend hidden-iframe silent renewal is supported. `no-referrer` keeps
the `rq` id and any `login_hint` out of onward requests. `'unsafe-inline'` appears nowhere, which
means the page carries no inline `<script>` or `style=` attributes; the existing sites already
manage this.

The icons script (`assets/icons.js`) fetches SVGs and parses them with `DOMParser`, never
`innerHTML`, so it satisfies the policy unchanged.

---

## 9. UX

The stated goal is that this is the best-looking thing we ship. Concretely that means: one screen
that presents all three ways in without making any of them feel like the fallback, no layout shift
between states, and no dead ends.

### 9.1 The sign-in screen

```
+----------------------------------------------------------------+
|  [venta mark]                                                   |
|                                                                 |
|  Sign in                          |  Or scan to sign in         |
|  to continue to Isle              |                             |
|                                   |     +-----------------+     |
|  Username or email                |     |                 |     |
|  [__________________________]     |     |   [ QR code ]   |     |
|                                   |     |                 |     |
|  Password              Forgot?    |     +-----------------+     |
|  [__________________________]     |                             |
|                                   |  Open Venta on your phone,  |
|  [       Sign in        ]         |  go to Settings > Scan QR   |
|                                   |                             |
|  ----------- or -----------       |  Expires in 2:47            |
|  [  Continue with Steam  ]        |                             |
|                                   |                             |
|  New to Venta?  Create an account |                             |
+----------------------------------------------------------------+
```

- **The QR panel is a peer, not an afterthought.** It sits beside the form on desktop at full size.
  Below 900px it collapses to a "Sign in with your phone" button that expands the panel in place, so
  the mobile layout is not a desktop layout with a hole in it.
- **"to continue to Isle"** comes from the authorize-request projection (§3.1). A person who followed
  a link from a partner site needs to know where they are and why. When the site is opened directly
  with no `rq`, the line is omitted rather than replaced with filler.
- **The QR panel starts a pairing code on load and stops polling when the tab is hidden.** A
  3-minute code that expired in a background tab and a poll loop that ran all night are the same
  bug seen from two sides.
- **Steam is one button, no Steam-branded takeover.** It is a peer of the password form, below the
  divider, using the linked-account language ("Continue with Steam") rather than login language,
  because for most people it will be a link to an account they already have.

### 9.2 States that must be designed, not defaulted

Every one of these is a real response the backend already returns:

| State | Trigger | Screen |
| --- | --- | --- |
| MFA | 401 `mfa_required` | Code field replaces the password field in place; "Use a recovery code instead" is a link, not a second form |
| Wrong code | 401 `mfa_invalid` | Inline, attempts remaining shown |
| Locked out | 423 after 10 failures | "Too many attempts. Try again in 15 minutes." with the real number, not "later" |
| Unverified email | 403 `Email not verified` | Straight to `/verify` with the address prefilled and a code already sent |
| Banned / disabled | 403 not allowed | Points at `support.venta.gg/appeal`, which exists |
| QR scanned | status `scanned` | Panel swaps to "Confirm on your phone" with the device name |
| QR denied | status `denied` | "Sign-in denied" plus a fresh code, not an error toast |
| QR expired | 404 | New code offered, one tap |
| Steam unlinked | `status=no_account` | The two-door screen in §5.3 |
| Steam already linked elsewhere | `status=already_linked` | Explains that the SteamID belongs to another account, links to support |
| Reset lost history | `MasterKeyRewrapRequired` | What was lost, what the ticket recovers |

The lockout and the banned states are the two most likely to be met by someone already frustrated;
both must name the next action rather than only the problem.

### 9.3 Look

Reuse `assets/venta.css` - the Alpine brand tokens (`--brand: #4B5BC4`), the `.btn`/`.card`/`.field`
primitives, both colour schemes, and the shared icon set. This is what makes the site read as Venta
rather than as a generic identity provider, and it is already the reason `admin` and `support` look
related.

`auth.css` adds only what a sign-in surface needs: the centred single-column shell, the two-pane
split and its collapse, the QR panel, the code-input group, and the provider button. Dark mode comes
free from the shared tokens and must be checked, not assumed - this is the one page where a
mis-tokenised input border makes the whole product look unfinished.

Accessibility is a requirement, not a pass: labelled inputs, `autocomplete="username"` /
`"current-password"` / `"one-time-code"` so password managers and SMS autofill work, a visible focus
ring (already in `venta.css:170-251`), live-region announcements for the QR state changes, and the
QR code carrying a text alternative with the pairing code so it is not the only route.

---

## 10. Deployment

| Variable | Meaning | Default |
| --- | --- | --- |
| `AUTH_DOMAIN` | Hostname the site is served on | derived: `auth.` + registrable domain of `INSTANCE_URL` |
| `AUTH_ISSUER_URL` | OIDC issuer | `https://{AUTH_DOMAIN}` |
| `AUTH_CLIENTS` | Client registry JSON (§7) | empty |
| `AUTH_CLIENT_SECRET_{ID}` | Per-client secret, confidential clients only | unset |
| `STEAM_CLIENT_RETURN_URL` | Existing; now the default of an allowlist | `venta://steam-auth` |

Touch points: `deploy/compose.yaml:526-537` (env passthrough), `deploy/install.sh`
(arg/prompt/default/env-write at `:56-59, 79-80, 106-109, 301-304, 410-413, 556-559`, plus a Caddy
block cloned from the `$SUPPORT_DOMAIN` one at `:772-779`), and the same in
`deploy/Install-VentaStack.ps1`.

**No IP allowlist or basic auth on this host, ever** - the same rule `install.sh:781-786` already
states for support and status, and for a stronger reason: this is where everyone signs in.

`IDENTITY_SIGNING_CERT` becoming a hard requirement in production is a **prerequisite**, not a
nicety. `Program.cs:103-107` currently falls back to `AddDevelopmentSigningCertificate()` when the
variable is empty, in production, silently. That is survivable while the only consumer is our own
first-party client; it is not survivable once partner sites trust tokens signed by it. Make the
absence fatal at startup in production.

---

## 11. Tests

Following the existing conventions - normal, edge and negative per change.

**`Echo.Tests/Sites`** - add `[TestCase("auth")]` to every case in `SiteAssetPathTests`
(`:70-72, 99-101, 116-118, 197-200, 330`) and `SiteHostTests` (`:25-59`). These give asset-path
resolution, the no-duplicate-folder-prefix rule, the shared-CSS requirement, `node --check` parsing,
and icon existence for free. Add two new ones: every requested scope is registered
(`SiteAssetPathTests:157` already does this for admin, generalise it), and the auth pages contain no
inline script or style, which is what keeps the CSP honest.

**`Identity.Tests`** - the Phase 0 host/issuer spike; authorize with no cookie redirects to `/login`
and stashes; a stashed request is single-use; an unregistered `redirect_uri` is refused; PKCE is
required and a wrong verifier fails the exchange; a banned user with a valid cookie is refused at
authorize (not just at token); `prompt=login` re-prompts despite a live cookie; `max_age` is honoured
against `auth_time`; consent is skipped for first-party and required otherwise; logout without an
`id_token_hint` requires confirmation; the Steam return URL falls back rather than honouring an
unlisted target; the widened password grant accepts an email and still pays the dummy-hash cost for
an unknown one.

**E2E** - one full authorization-code round trip against a real Postgres and Redis, since the
authorize/stash/login/callback path crosses both and the interesting failures are ordering failures.

---

## 12. Work breakdown

| Phase | Work |
| --- | --- |
| **0** | **Done.** Host/issuer spike (§2.1, resolved: no host check). `IDENTITY_SIGNING_CERT` is now fatal in production when unset. |
| **1** | **Done.** `Echo.Auth.VentaJwtAuthentication` replaces eight `AddJwtBearer` blocks; `AUTH_ISSUER_URL`; host derivation shared via `AppEnvironment.InstanceHosts`. |
| **2** | **Done.** Endpoints declared, authorization code + PKCE allowed, issuer flipped, claim destinations mapped (`VentaClaimDestinations`), `amr`/`auth_time` stamped per grant. |
| **3** | **Done.** `SsoCookie` scheme, `AuthorizationRequestStash`, `AuthorizationController` (authorize/resume/userinfo/logout), `SsoController` (`/api/v1/sso/*`), per-client `LoginSession`, and the token endpoint's `authorization_code` branch. |
| **4** | **Done.** `AuthClientRegistry` reconciles `AUTH_CLIENTS` into the OpenIddict tables, additive-then-diff, disabling rather than deleting a withdrawn client. |
| **5** | **Done.** `SiteHosting` wiring on `AUTH_DOMAIN`, `AuthSiteSecurity` (CSP + `no-store`), `Echo/wwwroot/auth/{index.html,auth.css,app.js}`, and the states in §9.2. QR codes are rendered server-side (`GET /qr-login/{code}/svg`) because the CSP rules out a CDN library and a hand-written encoder fails invisibly. |
| **6** | **Done.** Steam return URL is per-flow and allowlisted (`SteamReturnTargets`), the unlinked SteamID gets the two-door screen backed by a 30-minute pending-link ticket, and the password grant accepts an email address. |
| **7** | **Done.** `/sessions` ("where you are signed in"), backed by cookie-authenticated `GET`/`DELETE /api/v1/sso/sessions` - not the bearer-authenticated `SessionController`, because the site trades its access token for the cookie and keeps no token. Reached from the sign-out screen, which is where somebody is already thinking about it. **`/register`, `/verify`, `/forgot` and `/reset` were pulled forward into Phase 5** rather than shipping a sign-in screen whose own links dead-ended. |
| **8** | **Done.** `AUTH_ISSUER_URL` (shared block - every service reads it), `AUTH_DOMAIN` and `AUTH_CLIENTS` in `deploy/compose.yaml`; `--auth-domain` plus a Caddy block in both installers; the host rule and both configmap keys in `alpine-infra`; and [sso-integration.md](./sso-integration.md), the partner guide. |

**Deployment note.** Everything above is config; the one thing that is not derivable is the
`auth.<domain>` **DNS record and its Ingress/Caddy host rule**. Without it the host resolves to the
edge, the edge has no router for it, and the symptom is a plain 404 over http and a TLS handshake
failure over https - not a gateway error, because the request never reaches the gateway. On the
hosted instance that rule is `echo/templates/ingress.yaml`; the runbook is
`alpine-infra/DEPLOYMENT-SSO.md`.

`AUTH_CLIENTS` ships empty, on both compose and Kubernetes. That is correct rather than
unfinished: the sign-in site, the OIDC endpoints and every first-party client work without it, and
an instance with no partner sites should have no client able to start an authorization-code flow.

Phases 1 and 2 ship together and are the only ones with a rollback cost; everything after is
additive.

## 13. Known risks

1. **The issuer flip signs everyone out.** Accepted deliberately (§2.1). The condition it had to
   meet - that a restart plus a fresh sign-in per client fully recovers - is now confirmed: Phase 0
   ruled out the OpenIddict host check that would have needed a mobile release instead. The
   remaining hazard is the trailing slash in §2.2, not the flip itself.
2. ~~**Production dev-certificate fallback.**~~ Closed in Phase 0: Identity now refuses to start in
   Production with no `IDENTITY_SIGNING_CERT` rather than silently signing with a key that changes
   on every restart.
3. **No back-channel logout** (§3.3). A deliberate v1 omission with a real consequence: signing out
   at Venta does not sign you out of a partner site's own session. No longer hypothetical now that
   `isle.venta.gg` exists. `/sessions` (Phase 7) is the mitigation and not a fix - it lets somebody
   revoke that session deliberately, but nothing propagates a sign-out on its own, and the revoked
   site keeps working until its current access token expires.
4. **Access tokens outlive revocation** by up to their lifetime (§4.3). Pre-existing, unchanged,
   and now visible to third parties.
5. **The `rq` parameter rides on the resumed authorization request.** `/connect/authorize/resume`
   reconstitutes the parked request and appends the stash id, so the second pass can see that
   `prompt=login` was already honoured and that consent was already given. That id is therefore
   load-bearing for two decisions, and it is guarded by `AuthorizationRequestStash.Matches` (same
   client, same redirect URI, same scopes) plus `DecidedBy` (same subject). A future change that
   relaxes either check hands somebody a way to carry a decision across requests.
6. **`ValidateAudience = false` everywhere.** Every service accepts any token this issuer signed,
   including one minted for a partner site. Today that is fine because every client is ours. It stops
   being fine the first time a genuinely third-party client exists, and the fix (per-service
   audiences) is a change to all eight services, so it should be planned before that client is
   onboarded rather than after.
