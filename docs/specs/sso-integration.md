# Sign in with Venta - integration guide

How to put "Sign in with Venta" on a site, and how an operator allows that site to do it.

Two audiences, and you probably are only one of them:

* **Building the site?** Start at §2. You need the discovery URL, a client id, and a library.
* **Running the instance?** Start at §5. You are the one who writes `AUTH_CLIENTS`.

Design document and rationale: [sso.md](./sso.md). This guide is the contract; that one is the
argument.

---

## 1. What this is

`auth.<your-instance>` is a standard **OpenID Connect** provider. `auth.venta.gg` on the hosted
instance. There is no Venta-specific SDK and there should not be one: use whatever OIDC library your
framework already has, point it at the discovery document, and everything below is the answer to
questions that library will ask you.

Two things are worth knowing before you start, because they are the ones people get wrong:

**The issuer and the API are different hostnames.** Tokens say `iss: https://auth.venta.gg/` while
the chat API lives on `api.venta.gg`. Your library only ever needs the first. If you are also
calling the Venta API with the access token you get here, that is the second.

**PKCE is required of every client, confidential ones included.** There is no implicit flow and no
hybrid flow. A library that offers to skip PKCE because you have a client secret will be refused.

---

## 2. The five minutes version

```
Discovery   https://auth.venta.gg/.well-known/openid-configuration
Flow        authorization_code + PKCE (S256)
Scopes      openid profile email roles offline_access
```

Everything else - authorization endpoint, token endpoint, JWKS, supported algorithms - comes out of
the discovery document, and you should read it from there rather than hard-coding it. The endpoints
are listed in §7 for when you are debugging with curl and do not want a second tab open.

Ask your operator for a **client id**, and give them the **exact redirect URI** your app will use.
Both are covered in §5, and the redirect URI is matched exactly: one trailing slash of difference is
a refused sign-in.

Minimal end-to-end, as most libraries express it:

| Step | What happens |
| --- | --- |
| 1 | You send the browser to the authorization endpoint with `client_id`, `redirect_uri`, `response_type=code`, `scope`, `state`, `nonce`, `code_challenge`, `code_challenge_method=S256`. |
| 2 | The person signs in at `auth.venta.gg` - password, a QR scan from the Venta app, or Steam. Not your problem, and deliberately not on your page. |
| 3 | The browser comes back to your `redirect_uri` with `code` and your `state`. |
| 4 | Your server POSTs the code to the token endpoint with the `code_verifier` and gets `access_token`, `id_token`, and `refresh_token` if you asked for `offline_access`. |
| 5 | You validate the `id_token` (your library does this), read `sub`, and that is the account. |

---

## 3. Claims: what you actually get

**`sub` is the account.** It is stable, it is opaque, it never changes, and it is the only thing you
should ever store as "who this is". It is not the username - usernames change.

The `id_token` is deliberately thin. It reliably carries:

| Claim | Meaning |
| --- | --- |
| `sub` | The account. Store this one. |
| `auth_time` | When the person actually authenticated, as a Unix timestamp. |
| `amr` | How: `pwd`, `steam`, `qr`, plus `mfa` alongside the first two when a second factor was used. |
| `iss`, `aud`, `exp`, `iat`, `nonce` | Protocol. Your library checks these. |

**For anything about the person, call the userinfo endpoint** rather than mining the id_token. That
is where the profile is assembled, and it is scope-gated:

| Scope | Userinfo returns |
| --- | --- |
| (always) | `sub` |
| `profile` | `preferred_username`, `name`, `updated_at` |
| `email` | `email`, `email_verified` |
| `roles` | `role` - the account's roles **on the Venta instance**, not on your site | 

`email_verified` is worth one sentence of thought: it means the address was confirmed against this
Venta instance. Treat it as "this person controls this mailbox", not as an authorization decision,
and never auto-link a local account by email address without a second confirmation step.

### `amr` and `auth_time` are usable, and that is unusual enough to say

Most providers stamp these and nobody looks. If your site has a step-up requirement - an admin area,
a destructive action, a payment - you can send `max_age=300` on the authorization request and be
re-prompted if the person's session is older than that, or read `amr` and require `mfa` to be in it.
Both are honoured properly here (`prompt=login` too), rather than being decorative.

---

## 4. Signing out

`end_session_endpoint` is in the discovery document (`/connect/logout`). Send the browser there with
`id_token_hint` set to the id_token you were issued and `post_logout_redirect_uri` set to a URI your
operator registered, and the person is signed out of Venta and returned to you.

**Two things it does not do, and you have to design around both:**

1. **There is no back-channel logout.** Signing out at Venta does not sign the person out of *your*
   site. Clear your own session yourself; do not assume the redirect did it.
2. **Without an `id_token_hint`, the person is asked to confirm.** That is intentional - a bare link
   to a logout endpoint is otherwise a one-click way for any site to sign your visitors out. Send
   the hint and they will not see the interstitial.

An access token also survives revocation for up to its own lifetime. If you need "banned right now"
rather than "banned within the hour", check with your own backend, not with an old token.

---

## 5. Registering a client (operators)

There is no self-service registration and no admin UI. Clients come from **`AUTH_CLIENTS`**, a JSON
array in the Identity service's environment. It is reconciled into the database at every startup, so
the config file is the source of truth and the database is a cache of it.

```json
[
  {
    "clientId": "wiki",
    "displayName": "Venta Wiki",
    "logoUri": "https://wiki.example.com/icon.png",
    "redirectUris": ["https://wiki.example.com/signin-oidc"],
    "postLogoutRedirectUris": ["https://wiki.example.com/"],
    "scopes": ["openid", "profile", "email"],
    "firstParty": true,
    "public": true
  }
]
```

| Field | |
| --- | --- |
| `clientId` | What the site sends as `client_id`. Stable; renaming it is a new client. |
| `displayName` | Shown on the consent and sign-in screens. Write it as the person would name the site. |
| `logoUri` | Optional, shown on the consent screen. |
| `redirectUris` | **Matched exactly.** Every environment needs its own entry - a staging URL is not covered by the production one. |
| `postLogoutRedirectUris` | Where `post_logout_redirect_uri` may point. Also exact. |
| `scopes` | The most the client may ask for. Asking for more is `invalid_scope`. Include `offline_access` to permit refresh tokens. |
| `firstParty` | `true` skips the consent screen. Only for sites you run - it is a statement that the person has already agreed to this by using your product. |
| `public` | `true` for anything running in a browser, a mobile app or a desktop app: no secret, PKCE only. `false` only for a server-side client that can genuinely keep one. |

### Deploying it

**Compose:** set `AUTH_CLIENTS` in `deploy/.env` on one line, in single quotes, then
`ventactl restart identity`.

**Kubernetes:** it is a key in the `identity-configmap`; apply and restart the deployment.

**A confidential client needs its secret separately**, under a name derived from the client id:
uppercased, every non-alphanumeric character replaced with `_`, prefixed `AUTH_CLIENT_SECRET_`. So
`clientId: "wiki-staging"` reads `AUTH_CLIENT_SECRET_WIKI_STAGING`. Compose cannot forward a variable
name it does not know about, so add the line to the `identity` service's `environment:` block
yourself; the file has a comment marking the spot.

### If the site also calls the API from a browser

An `AUTH_CLIENTS` entry buys sign-in and nothing else. A browser app that then calls `api.<domain>`
with the token is making a cross-origin request, and its origin has to be in
**`CORS_ALLOWED_ORIGINS`** as well. That is a different variable, read by *every* service rather
than by Identity, and it is set in the shared environment block (`AppEnvironment/ClientOrigins.cs`).

This is worth spelling out because of how it fails. `http://localhost:4200` and
`http://localhost:1420` are built in, so a site being developed against the instance works, and
keeps working, right up to the moment it is deployed to its real hostname. The refusal then happens
in the browser: the request reached the API, the API answered, and the browser threw the answer away
for want of a header. Nothing appears in any server log, so it reads as a bug in the site.

The list is additive (it never removes a built-in or the web client derived from `INSTANCE_URL`),
comma/semicolon/space separated, and rejects `*`. Entries it could not parse are named in the
startup log.

### A worked first-party example

The Isle companion site, `isle.venta.gg`, as it is configured on the hosted instance:

```
CORS_ALLOWED_ORIGINS=https://isle.venta.gg
```

```json
[
  {
    "clientId": "isle",
    "displayName": "VentaIsle",
    "redirectUris": [
      "https://isle.venta.gg/auth/callback",
      "http://localhost:4200/auth/callback"
    ],
    "postLogoutRedirectUris": ["https://isle.venta.gg/"],
    "scopes": ["openid", "profile", "email"],
    "firstParty": true,
    "public": true
  }
]
```

Both redirect URIs are listed because the match is exact and the development one is simply a
different URI. `public` because it is a browser app with nowhere to keep a secret, and `firstParty`
because it is ours, which skips the consent screen.

No `offline_access`, deliberately: the site keeps no refresh token, and renews instead with a
top-level `prompt=none` authorization redirect just before the access token expires. So the SSO
cookie's sliding 14-day life is what the person actually experiences as "still signed in", and a
`login_required` back from one of those renewals is a normal, expected outcome that the site has to
handle as "sign them out here" rather than as an error (see §8). A silent-renew **iframe** would be
the usual alternative and cannot work against this provider: the sign-in site sends
`frame-ancestors 'none'`.

### Removing one

Delete the entry and restart. The client is **disabled, not deleted** - which means tokens it already
issued keep validating until they expire. If that matters, revoke the sessions too.

Rows the registry did not create are left alone entirely, including Identity's own `echo` client. You
cannot accidentally delete the thing your own apps sign in with by writing an empty array.

---

## 6. Hosting requirements (operators)

`auth.<domain>` must resolve publicly and terminate real TLS. Both installers configure Caddy for it
and both ask for the hostname; on Kubernetes it is a host rule on the gateway Ingress. A partner
site's **server** fetches `/.well-known/openid-configuration` and the JWKS from outside your network,
so an internal-only name will fail in a way that looks like a client bug.

Do not put a gate in front of it - no IP allowlist, no basic auth. Everyone who needs it is by
definition not signed in yet.

`AUTH_ISSUER_URL` is read by **every** service, not just Identity, because each one checks the `iss`
on the tokens it is handed. If you ever set it, set it in the shared block. One service left on the
old value rejects every token the others accept, and it presents as a signing-key failure.

---

## 7. Endpoint reference

Relative to `https://auth.venta.gg`:

| | |
| --- | --- |
| `GET /.well-known/openid-configuration` | Discovery. Start here. |
| `GET /.well-known/jwks` | Signing keys. |
| `GET /connect/authorize` | Authorization endpoint. |
| `POST /connect/token` | Token endpoint. |
| `GET /connect/userinfo` | Profile, bearer-authenticated, scope-gated. |
| `GET /connect/logout` | RP-initiated logout. |
| `POST /connect/revoke` | Token revocation. |

The same endpoints also answer on `api.venta.gg`, because the shipped mobile app predates the issuer
move and calls them there. **Do not build against that.** The tokens say `auth.venta.gg` either way,
and a strict OIDC client comparing the issuer against where it fetched the metadata will reject the
result.

---

## 8. When it does not work

| Symptom | Cause, nearly always |
| --- | --- |
| `invalid_client` | The client id is not in `AUTH_CLIENTS`, or Identity has not been restarted since it was added. |
| `invalid_request`, redirect URI | Exact-match failure. Compare character by character, including the trailing slash and the scheme. |
| `invalid_scope` | Asking for a scope the client entry does not list. Note `roles` is not implied by `profile`. |
| `invalid_grant` on the token call | The `code_verifier` does not match the challenge, the code was already used, or more than a few minutes passed. |
| `login_required` | You sent `prompt=none` and there was no session. Expected; fall back to an interactive attempt. |
| Sign-in works, then every API call fails in the browser only | The site's origin is not in `CORS_ALLOWED_ORIGINS`. See §5. Nothing is wrong with the token, and nothing is in the server log. |
| Everything 401s after a deploy | The issuer changed. See §6 - check that every service has the same `AUTH_ISSUER_URL`. |
| Sign-in page loads but the browser never comes back | Usually the SSO cookie: `auth.<domain>` must be HTTPS. The cookie is `__Host-` prefixed and a browser will not store it over plain http. |
