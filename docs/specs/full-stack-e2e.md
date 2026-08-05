# Full-stack E2E suite (Alpine client + Echo backend)

Status: proposal. Nothing here is built yet.

## What it is for

One suite that drives the **real Angular client** against a **real backend stack** through the
journey a new user actually takes:

1. Register an account
2. Receive and type the six-digit verification code
3. Log in
4. Log out
5. Log back in (the same account, second session)
6. Create a guild ("server")
7. Send a message in a text channel and see it arrive
8. Connect to a voice channel

Every existing test tier stops short of this. `Echo.E2E.Tests` boots the whole backend but speaks
HTTP to it directly, so it cannot catch anything that lives in the client or in the contract
between them. Alpine's vitest suites mock the backend, so they cannot catch anything that lives on
the other side. The regressions this suite exists for are exactly the ones that fall in the gap:
a renamed field, a changed status code, a header the client stopped sending, an event name that
drifted. Those are the "nasty system-wide" ones, and today nothing looks for them.

## The four things that block this today

These are the findings that shape the design. Each one is a real obstacle in current code, not a
hypothetical.

### 1. The client cannot boot outside Tauri

`DeviceIdentityService.resolve()` (`src/app/services/device-identity.service.ts`) reads the device
id from `LazyStore` (`@tauri-apps/plugin-store`) with no fallback. In a plain browser that call
rejects.

That matters more than it looks. `MainPageComponent.runDeviceLaunch()` calls
`getOrCreateDeviceIdentifier()` **before** its `try`, so the rejection escapes, `appReady.markReady()`
never runs, and the app sits on the `#app-loading` overlay forever. The same id is what feeds the
`X-Device-Id` header, and `GuildVoiceController.Join` rejects a request without a device it
recognises. So no device id means no app shell and no voice.

**Fix:** give `DeviceIdentityService` a non-Tauri storage backend (localStorage) behind the same
interface, selected by `isTauri()`. Roughly a 30-line change in one file. It is worth doing on its
own merits: it is the only thing standing between this codebase and a browser build.

Note the app is otherwise browser-tolerant already. `main.ts` fires `getSecureKey()` without
awaiting it, so its rejection is an unhandled promise and not a boot failure, and the window-show
block is wrapped in `try/catch` with an explicit "running in a browser" comment. MLS itself fails
closed into banner flags rather than blocking, and guild channel encryption is opt-in per channel,
so a fresh guild's text channel is plaintext and sends fine without an MLS engine.

### 2. The API URL is hardcoded - but federation already solves this

Both `src/environments/environment.ts` and `environment.development.ts` set
`apiUrl: 'https://api.venta.gg'`, and `ApiConfigService.domainToUrl` always builds `https://${domain}`.

The first version of this spec proposed an `environment.e2e.ts` plus a new `e2e` configuration in
`angular.json`. **That is not needed and is not the plan.** The client already supports
self-hosted and federated instances as a shipped feature: `LoginComponent` has a real server picker
(`serverDomain`, `serverInputValue`, `confirmServer()`) present in **both** login and register
modes, and `confirmServer()` strips any scheme prefix and trailing slash before storing the bare
domain.

**So the suite points the client at the local stack by typing the address into the app's own server
picker, exactly as a self-hoster would.** That needs no product change at all, and it means the
custom-instance path - a shipped feature with no E2E coverage today - is now covered on every run.

The one consequence: `domainToUrl` always yields `https://`, so **the gateway must be reachable
over TLS**. The compose stack terminates TLS with `caddy:2-alpine` using an internal cert, the
tests use `localhost:8443`, and Playwright sets `ignoreHTTPSErrors: true`. Everything derived from
the base URL - the OAuth token endpoint, the REST calls, and the SignalR hub - goes through Caddy,
so any service handing back an absolute `http://` URL will surface as a mixed-content failure.

### 3. There are no stable selectors

`grep -c data-testid` over `src/app/**/*.html` returns **0**. The suite would have to select on
Tailwind utility classes and translated label text, which is the classic way an E2E suite becomes
a maintenance tax that gets deleted a year later.

**Fix:** add `data-testid` to the ~25 elements this journey touches, and nothing else. Not a
codebase-wide convention, just the path under test. Listed in the appendix.

### 4. Verification codes are not deliverable in a test environment

`Messaging/EmailService.cs` is hardcoded to Microsoft Graph, and returns early doing nothing when
`AUTH_REQUIRE_USER_EMAIL_VERIFICATION=false` - which is what the existing E2E harness sets, which
is precisely why it has never tested this step.

The good news: `OneTimeCodeService` stores the code **in plaintext** in Redis under
`verification_code:{email}` (`Identity.Application/Services/OneTimeCodeService.cs:81`).

**Fix, phase 1:** run with `AUTH_REQUIRE_USER_EMAIL_VERIFICATION=true` and have the test read the
code out of Redis. The Graph send will fail on absent credentials, but `AccountEmailDispatcher.Queue`
catches and logs every failure by design, so the flow is unaffected: the code is minted on the
request path before the send is queued. Zero backend changes. Supply dummy `MicrosoftGraph`
credentials so `ClientSecretCredential`'s constructor does not throw on a null argument.

**Fix, phase 2 (recommended, separately valuable):** add an SMTP branch to `EmailService` selected
by an env var, and point it at a Mailpit container; the test then scrapes the code from Mailpit's
HTTP API. This tests the template render and the send, and it is a prerequisite for self-hosting
anyway - a self-hoster cannot use Alpinebits' Graph tenant, so email verification is currently
broken for every deployment that is not ours.

## Topology

```
                   Playwright (chromium, headless)
                              |
                    http://localhost:4200
                              |
                 Angular dev server, e2e configuration
                              |
                    http://localhost:8080
                              |
                     Echo gateway (YARP + sagas)
                    /      |       |       |     \
             identity   guild  messaging  social  unfurl
                    \      |       |       |     /
              postgres | rabbitmq | redis | minio | mailpit
```

Everything is `docker compose` on one runner. The client is served by `ng serve`, not built into
a Tauri binary.

### Why the browser and not the real desktop app

Driving the shipped artifact with `tauri-driver` is possible and it is the only way to cover the
Rust half. It is also Linux/Windows only (no macOS WebDriver), needs `xvfb`, and is materially
slower and flakier. For seven of the eight steps it buys nothing the browser does not already give.

The recommendation is to build the browser suite now and treat a `tauri-driver` smoke as a
possible later phase, scoped to the things only it can reach: MLS, the native key store, and
actual voice media.

### What "connect to a voice channel" means here, precisely

This is the part worth being exact about, because it looks like it needs a Cloudflare account and
it does not.

`GuildVoiceController.Join` (`Guild.Application/Controllers/GuildVoiceController.cs:49`) does
**no Cloudflare work at all**. It checks the `Connect` permission, verifies the channel is of type
`Voice`, resolves `X-Device-Id`, reconciles any existing voice presence, writes
`ChannelVoiceState` to Redis, broadcasts `guild.voice.UserJoinedVoice` over SignalR, and publishes
`VoiceStateForBots`. Cloudflare is only touched by `GuildCloudflareController` (the SDP exchange)
and by the device-takeover path, and both of those are driven by the Rust engine.

So the browser suite covers the entire signalling path - permission check, device identity, voice
state, and realtime fan-out to other members - with no Cloudflare credentials, no TURN, and no real
WebRTC. Media negotiation is explicitly out of scope and belongs to the `tauri-driver` phase.

Assert on: the join returns the participant roster containing the user; a **second** browser
context already in the guild receives `guild.voice.UserJoinedVoice`. That second assertion is the
one that catches realtime regressions, and it is cheap.

## Where it lives

**Decided: `AlpineBits-ch/venta-e2e`, and it is PUBLIC.**

Public was chosen for free GitHub Actions minutes (a Docker-heavy nightly is minute-hungry), on the
condition that frontend source must not leak. That condition is what dictates the architecture
below, and it rules out the obvious approach.

### The frontend is never checked out

A public repo cannot hold a PAT that reads private frontend source, and it cannot upload Playwright
traces built from that source: traces embed page resources, and artifacts on a public repo are
downloadable by anyone. `angular.json` makes this worse than it sounds -
`build.options.sourceMap` is `true` and the `production` configuration does not override it, so a
normal production build **emits the full original TypeScript**.

Instead, `AlpineFrontend` publishes a prebuilt, source-free client image
(`ghcr.io/alpinebits-ch/venta-client-e2e`): `nginx:alpine` serving the built app with an SPA
fallback, built with sourcemaps **explicitly disabled**. `venta-e2e` pulls it like any other
service. The minified bundle is the same code that already ships inside the downloadable desktop
client, so publishing traces built from it leaks nothing that is not already public.

That inverts the dependency: the private repo pushes, the public repo pulls, and no secret capable
of reading source ever exists in the public repo.

### Rules that keep it safe

- Never `pull_request_target`. It runs untrusted fork code with secrets available.
- Never upload `dist/`, any `.map`, or anything derived from frontend source.
- Explicit minimal top-level `permissions:` (`contents: read`, `packages: read`).
- Pin third-party actions by SHA. A public repo is a more attractive supply-chain target.
- The client image build must keep asserting that no `.map` files are produced. That check is the
  single thing standing between this design and a source leak, so it belongs in CI, not in a
  reviewer's memory.

### Registry access

The backend images are **private packages** (verified: anonymous pulls return 403). `venta-e2e`
needs read access to nine packages: `echo`, `guild-application`, `messaging-application`,
`identity-application`, `social-application`, `federation-application`, `import-application`,
`unfurl-application`, and `venta-client-e2e`.

**This grant is a manual, UI-only step.** GitHub exposes no REST API for package-to-repository
access grants (verified: the plausible endpoint 404s). For each package: Package settings ->
Manage Actions access -> Add repository -> `venta-e2e` -> Read. Once granted, the workflow's
built-in `GITHUB_TOKEN` is sufficient and no long-lived secret is needed.

### Why a third repo rather than either product repo

Ownership. The suite depends on both sides, so hosting it in either one makes it a second-class
citizen of that repo and an awkward cross-repo dependency for the other. A separate repo gives it
its own nightly cron and its own red/green signal that does not muddy either product repo's CI, and
it is triggered identically from both sides:

```yaml
# In AlpineBackend and in AlpineFrontend, after images publish / on demand
- uses: peter-evans/repository-dispatch@v3
  with:
    token: ${{ secrets.E2E_DISPATCH_TOKEN }}
    repository: AlpineBits-ch/venta-e2e
    event-type: upstream-changed
    client-payload: '{"backend_sha": "${{ github.sha }}"}'
```

Contents: `docker-compose.e2e.yml`, `playwright.config.ts`, `tests/`, `fixtures/`, and one workflow
with `on: [repository_dispatch, workflow_dispatch, schedule, push]`.

The dispatching repos each need a token with permission to dispatch to `venta-e2e`, stored as
`E2E_DISPATCH_TOKEN`. Note this is the one remaining secret in the design, it lives in the
**private** repos, and it can only trigger a workflow - it grants no read access to anything.

## Test design

### Fixtures

- **`stack`** (worker-scoped): `docker compose up --wait`, one stack per worker. Health-gate on
  `/health` for the gateway and each service's `/{service}/health`, matching what
  `SpawnedServiceProcess` already does.
- **`freshUser`** (test-scoped): generates `e2e-{uuid}@example.test`. Never reuse an address;
  `RegistrationNoticeThrottle` will start suppressing, and a reused address takes the
  "account already awaits verification" branch instead of the one under test.
- **`verificationCode(email)`**: reads `verification_code:{email}` from Redis (phase 1) or polls
  Mailpit's `/api/v1/message/latest` (phase 2). Poll with a timeout, do not sleep: the mint is on
  the request path but Redis is a network hop.

### The journey, as one spec plus focused ones

Keep the eight steps as **one linear spec** (`journey.spec.ts`), not eight independent tests. They
are genuinely sequential - you cannot log out of an account you did not create - and splitting them
means either re-running the whole prefix per test or sharing mutable state between tests, which is
how E2E suites become flaky. Use `test.describe.serial`.

Then add short independent specs for things worth isolating: verification with a wrong code,
verification after expiry, login with a bad password.

```
tests/
  journey.spec.ts          # the eight steps, serial
  verification.spec.ts     # wrong code, expired code, resend
  realtime.spec.ts         # two contexts: message + voice fan-out
```

### The steps, and what each one is really checking

| # | Step | The regression it catches |
|---|------|---------------------------|
| 1 | Register | The registration contract. See `docs/specs/registration-contract-change.md` - this endpoint has already changed shape once. |
| 2 | Enter code | Verification end to end. Currently untested at every tier, and it has broken before (see the memo on the overwrite race). |
| 3 | Log in | Token exchange against OpenIddict, `issuer`/`tokenEndpoint` wiring in `ApiConfigService`. |
| 4 | Log out | Slot teardown, `ApiConfigService.reset()`, scoped OAuth storage cleanup. |
| 5 | Log back in | The one people skip. Second login on a slot that already has residual state is where account-switching bugs live. |
| 6 | Create guild | Guild creation plus the cross-service saga fan-out that materialises the profile. |
| 7 | Send message | Messaging write path plus SignalR delivery to a second context. |
| 8 | Join voice | Permission, `X-Device-Id` resolution, Redis voice state, `guild.voice.UserJoinedVoice` fan-out. |

### The production guard, which is not optional

The client image is an ordinary **production** build, so `environment.ts` is baked in and the app
boots pointed at `https://api.venta.gg` - the live service. It reaches the local stack only because
the test drives the server picker, and `LoginComponent.register()` commits that with
`apiConfigService.setServer(domain)` before calling the API (verified).

**So a test that forgets to set the picker registers a user against production.** The suite
therefore installs a global Playwright route guard in the shared fixture: an allowlist of the local
stack's origins, everything else aborted and the test failed loudly, with any `venta.gg` host
producing a distinct "attempted to contact production" failure. It is tested by a spec of its own,
because a safety rail nobody tests is one that quietly stops working, and this one fails open in
the worst possible direction.

It also blocks the Sentry DSN compiled into the bundle (`main.ts` calls `Sentry.init`
unconditionally), which keeps test noise out of the live Sentry project. That is intended, not
incidental - if something needs an external host, stub it in the fixture rather than widening the
allowlist.

### Flake control

The existing backend harness has already paid for these lessons; inherit them rather than
rediscovering them.

- **No fixed sleeps anywhere.** Playwright web-first assertions for UI, explicit polling with a
  budget for Redis/Mailpit reads.
- **Generous timeouts on cross-service steps.** Guild creation crosses a Wolverine saga over
  RabbitMQ. `EchoTestStack` sets its data-export saga deadline to 30s for exactly this reason and
  the comment there explains why a tight value turns a loaded runner into a false negative.
- **Trace, video and console log on failure only.** `trace: 'retain-on-failure'`. A full-stack
  failure without a trace is close to undebuggable.
- **Capture container logs on failure.** `docker compose logs` into the artifact bundle. When the
  cause is in the backend, the browser trace shows you a spinner and nothing else.
- **`retries: 1` in CI, `0` locally.** One retry distinguishes a genuine break from a flake without
  hiding a test that fails half the time; the report shows flaky separately.
- **Fully fresh volumes per run.** `docker compose down -v`.

## Phasing

**Phase 1 - make it possible (frontend).** Browser fallback in `DeviceIdentityService`;
`data-testid` on the journey path; the source-free client image workflow. All three are in
AlpineFrontend, and none of them changes shipped behaviour in Tauri. The API-URL work originally
listed here is gone: the server picker replaced it.

**Phase 2 - the harness.** New repo, compose file, Playwright config, fixtures, health
gating. Prove it by running steps 1-3 locally.

**Phase 3 - the journey (~2 days).** All eight steps, plus the two-context realtime assertions.

**Phase 4 - CI (~half a day).** Nightly cron, `repository_dispatch` from both repos, artifact
upload, and a Slack or GitHub-issue notification on nightly failure. A nightly that fails into an
inbox nobody reads is not a test suite.

**Phase 5 - optional.** Mailpit and the SMTP branch in `EmailService` (also unblocks self-hosting).
A `tauri-driver` smoke for MLS and real voice media.

## What it found on the way up

Recorded because the point of this suite is the class of bug nothing else sees, and it is worth
knowing which ones those turned out to be. All of these were found by running the real client
against a real backend, and none of them were visible to any other tier.

**Every service in the client that reached for a Tauri API crashed the app outside Tauri, and the
unit suite could not see it.** Three separate stores (`DeviceIdentityService`,
`AccountRegistryService`, `UserTokenService`) plus `PlatformService`. The last one was the worst
shape: `isMobile` was a *field initialiser* calling `@tauri-apps/plugin-os`'s `type()`, so the
throw landed while the injector was constructing the service and took `MainPageComponent` with it.
Route activation for `/overview` failed, the router restored `/authentication`, and the outlet was
left empty - which presented as "login succeeds, bounced back to the login page, blank screen".
Every one of the 2143 unit tests passed throughout, because every spec that touches those services
mocks `isTauri` to `true`.

**The client hardcoded the venta.gg address into avatar URLs, and so did the backend in seven
places** - including `AvatarUrl`/`BannerUrl` on every profile projection, attachment thumbnails,
and update downloads. Every self-hosted and federated deployment was sending its users to our
servers for images its own instance was already serving. This is the defect the E2E suite existed
to catch, and it was nowhere near where it was being looked for.

**Two flakes that would have been blamed on the backend.** The verification dialog's `(onShow)`
fires when its *enter animation ends*, 300-450ms after it is in the DOM and typeable, and
`onShow()` clears the code field - so digits typed in that window vanish, `verify()` early-returns
on a short code, no request is made at all, and the test times out against an app nobody asked to
do anything. Roughly two runs in five. And `p-button` renders its `<button>` inside the tagged
host, so a `toBeEnabled()` assertion that was meant to prove the client had reached the stack over
TLS passed unconditionally and could never have failed.

**Blocking UI is a permanent tax on this suite, not a one-off.** A fresh account gets an onboarding
banner that intercepts pointer events on the shell chrome, and a policy-acceptance modal is coming.
The fixtures therefore dismiss blocking overlays through one shared, extensible helper rather than
special-casing whichever one exists this month.

## Open questions

1. **Backend images on pull requests.** Resolved for `main`: `docker-build.yml` already tags with
   `type=sha,prefix=` alongside `latest`, so a nightly can pin an exact backend build and a failure
   is attributable. But the whole `build` job is gated on `if: github.ref == 'refs/heads/main'`, so
   **no images exist for a backend PR**. Gating backend PRs on this suite therefore needs one of:
   publishing PR images under a throwaway tag, or having the E2E run build the ten images itself
   (slow, roughly 10 to 15 minutes added). Nightly-against-`main` works today with neither. My
   suggestion is to start nightly-only and add PR gating later, if it earns it.
2. **Consent coverage.** The legal-consent dialog is live and mounted app-wide, but it cannot fire
   in this suite: registration records consent for the then-current Terms and Privacy versions
   (`CreateUserCommandHandler`, T1-10), so an account created seconds ago has nothing outstanding.
   The dialog is the **upgrade** path. Covering it means publishing a new document version mid-run
   and re-reading `/users/self`. Worth doing, not done.

### Resolved

- **Onboarding.** Confirmed: a fresh account meets up to three full-screen takeovers before it can
  touch the shell - the interest picker (non-dismissable by design), device registration, and the
  consent dialog. They are handled by one extensible table in `dismissBlockingOverlays()` rather
  than special-cased. Master-key setup is deliberately **not** in that table: its "Not now" cancels
  the action that raised it, so dismissing it would turn "the guild was not created" into a mystery.
- **Package access.** The eight packages were made public, so no grants are needed. Both
  arrangements were verified working against a real run; the workflow keeps its GHCR login so that
  a package returning to private breaks nothing.
- **Triggering.** Both product repos now dispatch on publish, and the nightly cron re-checks
  `latest`. A missing dispatch token skips with a notice rather than reddening a good build, in
  both repos.

## Storage

**Postgres for everything. No Scylla in the E2E stack at all**, not even behind a compose profile.
`USE_SCYLLA_DB=false` puts Messaging on the EF Core / Postgres repository and also stops
`Messaging.Application` opening a Cassandra connection at startup, so the stack needs no Cassandra
driver and no Scylla container. `Echo.E2E.Tests/Hosts/EchoTestStack.cs` documents the reasoning at
length.

**One caveat to record.** Per that same comment, message *reactions* still write directly to
`ScyllaContext` and are **not** guarded by the flag. Reactions therefore do not work in this stack.
The eight-step journey never touches them, so this costs nothing today, but anyone adding reaction
coverage later needs real Scylla wired back in for that scenario.

## Appendix: selectors to add

Login/register (`features/login/login.component.html`): `tab-signin`, `tab-register`,
`register-username`, `register-email`, `register-password`, `register-submit`, `login-username`,
`login-password`, `login-submit`, `login-error`.

Verification (`features/email-verification/email-verification-dialog.component.html`):
`verification-code-input`, `verification-submit`, `verification-error`, `verification-resend`.

Guild (`features/guild/...`): `create-guild-button`, `create-guild-name`, `create-guild-submit`,
`guild-list-item`, `channel-list-item`, `voice-channel-item`, `voice-participant`.

Messaging: `message-composer`, `message-send`, `message-list`, `message-item`.

Shell: `user-menu`, `logout-button`, `app-ready`.

### Notes from actually placing them

**`app-ready` is the most important one, and it needed new code.** `AppReadyService.ready` was a
signal nothing read: `markReady()` only flipped it and imperatively removed the `#app-loading`
overlay. So there was no DOM-observable ready state at all. Worse, `AppComponent` has an 8-second
safety-net timer that calls `markReady()` regardless of whether the launch succeeded - so
"the overlay disappeared" would have let a **failed boot pass as a slow one**, which is precisely
the false green this suite must never produce. `main-page.component.html` now carries
`[attr.data-testid]="appReady.ready() ? 'app-ready' : null"` on its root, and `appReady` was
widened from `private` to `protected` so the template can reach it.

**`login-error` and `verification-error` do not exist as elements.** Both failures surface as
app-wide PrimeNG toasts (`login.component.ts` calls `toast.httpError(...)`;
`email-verification-dialog.component.ts` toasts `LOGIN.VERIFY.INVALID_CODE`). Rather than invent
inline error nodes - a real UI change for a test's benefit - the global `<p-toast>` in
`app.component.html` is tagged `data-testid="toast"`. Negative-path tests must match on the message
text, since that one element carries every toast in the app.

**PrimeNG attribute landing, which will bite whoever writes the assertions.** `pInputText` and
`pPassword` are directives on native inputs, so the testid lands on the input and `fill()` works.
But `p-button` is an element component that renders its `<button>` *inside* the host, so
`toBeDisabled()` against the testid will not work - use `[data-testid="login-submit"] button`.
`p-inputotp` renders six sibling inputs; use `pressSequentially`, not `fill`, so its key handling
runs.

**Two shape surprises.** `user-menu` is on the settings cog, not the profile popover, because the
popover has no sign-out and cog -> settings -> "Log Out" is the only real path. And logging out
opens a key-export dialog that must be dismissed, so `logout-button` is not the last click in that
step. Also `channel-list-item` covers text channels only; voice rows render a different component
and match `voice-channel-item` instead.
