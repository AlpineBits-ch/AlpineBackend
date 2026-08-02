# MLS — Remaining Work

Handover state as of 2026-08-02. Companion to `mls-hardening-plan.md`,
`mls-hardening-contract.md` (§A–§L) and `mls-security-findings.md`.

## Landed

| Repo | Commit | Verified |
|---|---|---|
| Echo | `5ba86ca` (pushed) | **3131 passed / 0 failed / 6 skipped**, 12 assemblies |
| venta-mobile | `7310a13`, released as **`v1.0.59`** (TestFlight ✓) | **357 Dart / 59 Rust** / analyze clean |
| Alpine | `312d7f7`, released as **3.0.159** on `main` + `release` (CI ✓) | **332 Rust / 1090 TS (96 files)** / prod build clean |

> **Deploy state as of 2026-08-02:** Echo is pushed through `5ba86ca`. `1751460` — the fix for a
> conversation being created without the creator's own devices — **was still awaiting deploy** at the
> time of writing. Until it is live the server keeps producing the defect the clients now recover from.
> `5ba86ca` is docs/comments only and needs no deploy of its own.

<details>
<summary>Earlier state, superseded (kept for the commit trail)</summary>

| Repo | Commit | Verified |
|---|---|---|
| Echo | `6b4a560` (pushed) | 3119 passed / 0 failed, all 12 suites incl. the 220 Identity+E2E that had never run |
| venta-mobile | `c707bc3`, released as `v1.0.55` | 227 Dart / 52 Rust / analyze clean / `swiftc -parse` clean |
| Alpine | `d016285` (not pushed) | 310 Rust / 970 TS / release build clean |

</details>

The originally reported bug — encrypted messages undecryptable on mobile — is fixed and
regression-tested. Cross-client golden vectors pass in both directions.

**The TestFlight build succeeding resolves two previously-open questions:** the Swift compiles
against the real Apple SDKs, and the Runner provisioning profile does carry the
`S33LPKH83B.*` keychain wildcard (signing would otherwise have failed).

---

## 1. Alpine has never had an independent security review — **highest priority**

Echo and venta-mobile each got one; each came back with criticals. Alpine's fix list is
*derived* from those findings, so it is incomplete by construction. Every serious bug in this
effort was an inter-repo disagreement invisible from inside a single repo.

Run it against `d016285` with the tree frozen — a previous review had to skip Alpine precisely
because the tree was moving.

## 2. §G/§H are inert on both clients

This is the gap between "the code exists" and "the security model runs". Verified by grep on
mobile — **zero production callers** for all of:

- `RecoveryCodeScreen`
- `addRecoveryCode` / `setUp`
- `LeafVerificationService.check`
- `AccountIdentityService.ensure`

Two concrete consequences:

- **No user can obtain a recovery code.** §C.1.1's entire purpose — that a password reset stops
  destroying encrypted history — is inactive for every real account, despite the server half
  being done and the client mechanism being built and tested.
- **No device certificate is ever issued**, so coverage stays at 0, so `certificateEnforcement`
  can never advance past `Observe`, so §H.4's external-commit defence can never turn on.

### 2a. Generation side — do this first, it is safe at `Observe`

Wire, on both clients: account identity key + device certificate issuance at unlock (§I.2), the
recovery-code prompt and retrofit, capability reporting (§I.4). None of this enforces anything,
so it cannot remove a device — but it is what starts certificate coverage moving, and nothing
downstream can be switched on until it does.

### 2b. Enforcement side — the deferred block

C4, C6, C7, H6, §L.4. Certificate validation on the commit path, protection-level enforcement
with the monotonic floor, the admission ceremony, nonce freshness verified against the issuer's
own record. Deferred three times, correctly each time, on the reasoning that half-wired security
apparatus is worse than none.

Note §L.5: detection must work at **every** phase including `Observe`; `Observe` suppresses
*removal*, never detection.

## 2c. Nothing admits a device that appears after the group does — **the live multi-device failure**

Reported 2026-08-02: a recipient could read an encrypted message on their phone and not on their
desktop, same account. Not the base64 bug, and **not** the `POST api/v1/devices` 400 either — Echo
was redeployed with `3bde2f0` while this was being investigated and registration is healthy.

**The desktop's own root cause turned out to be client-side and Alpine has it:** one `try` block
wrapped key-package replenishment, the master-key check and pending-Welcome processing, so a single
failed key-package upload silently skipped the other two — permanently, because nothing else
processes pending Welcomes. The Welcome was sitting on the server, addressed correctly to that
device, and the device never read it. Not a registration problem: Alpine never sent a placeholder
identity key, so the §L.12 400 never fired for it.

The server-side mechanism this exposed is enumeration, and it is in the design rather than in one line
of code. An MLS group is whatever set of devices the *creating client* sealed Welcomes to, which is
one key package per active device that still had one; a device with none comes back in
`unreachableDevices` and is left out. **Nothing on either side ever adds it afterwards.** So a device
that was dry, that did not exist yet, or that failed to process the Welcome it was sent, stays outside
that group permanently — the server being fixed does not heal it.

Server side is now as loud as it can be (contract **§L.14**): creation reports the creator's own
devices as well as members' — it never did, which was a genuine silent skip and is fixed with tests —
and commit-publish and enable report coverage where they previously reported nothing. All three also
log a warning naming the device.

What that does **not** do is get the device back in, and only a client can:

- **Wire §B's conversation join-request sweep at launch.** This is the repair path; without it the
  new reports name a problem with no remedy attached. **Both clients now do this** — Alpine shipped its
  sweep in 3.0.159, and mobile's is wired into `MlsSessionManager.sync()` (§2e). Note that the sweep
  alone was not enough: it raised requests nothing could act on. See **§2e** for the four defects that
  made the ceremony unreachable for conversations.
- **Alpine's "Re-link device" — fixed in their tree, not yet released.** The **shipped** build only
  calls `refreshState`, which catches a device up on missed commits and cannot create a leaf that was
  never added, so the button clears the banner and the banner returns on reload. Alpine's working tree
  replaces that: `MlsJoinRequestService.relink` tries the cheap remedy first, then submits a §B join
  request. **Not an open bug — treat it as closed once Alpine ships**; it is listed only because the
  build in the field still behaves the old way and that is what a live report will show.
- **venta-mobile's creation check is per user, not per device**
  (`conversation_encryption_service.dart:66-74`): it reduces the server's per-device
  `unreachableDevices` to "did this *user* appear at all", so a member with one good handset and one
  dry one passes as reachable and the dry one is dropped in silence. Alpine's dialog already does this
  correctly. Mobile's agent owns this.

Prerequisite for a server-side coverage read route: `MlsGroupGeneration` records `ActivatedByUserId`
but not a device, so the server cannot distinguish the creator's own creating device from one left
out. Additive nullable column; not taken.

**A device that never registered cannot be reported at all, and there is no signal that one exists.**
Assessed and closed — see contract **§L.14.1** for the table. `LoginSession.DeviceId`,
`UserPushToken.DeviceId` and `UserDeviceBackup.DeviceId` are all FKs to `user_devices.Id`; the first
two resolve the client-supplied id and store **null** on a miss, the third cannot be written at all,
and backup transfers are refused unless the target resolves. No dangling client device id survives
anywhere. The residue (`DeviceId IS NULL`) is the documented normal state for old sessions, old
builds, and correct first-launch ordering, so warning on it would fire on most accounts forever.
**No warning was added and none should be**, on this evidence.

## 2d. A restored session never establishes a master key — **open, needs a UX decision**

Found 2026-08-02 while verifying why the desktop prompted for a recovery code and the phone never did.
The build-age explanation (`AccountEncryptionService` had no production callers until `ef0330d`,
first shipped v1.0.56) is **true but not the whole answer**.

venta-mobile establishes the master key **only on the sign-in path**. `main.dart:85` calls
`startAuthenticatedServices()` with no password on a restored session, `account_encryption_service.dart:208`
bails without one, and `_recoveryCodeOwed` short-circuits so the banner never appears.

**Consequence:** a user who updates the app and reopens it — rather than signing out and back in — is
still keyless and never prompted, on v1.0.59. That is the majority upgrade path. Alpine wins the
comparison because `checkMasterKey()` runs on every launch, not only at sign-in.

**Not fixed, deliberately.** Establishing requires the password, so the remedy is a new "enter your
password to set up encryption" prompt, not a wiring change. That is user-facing UX and was not
invented unasked. Whoever picks this up: mobile's `establish`-then-prompt split is **correct** and
should be preserved — see `MasterKeyService.establish`'s own reasoning about never minting a code
nobody has seen. Only the trigger is missing, not the flow.

Related and now **closed** — see §2e: mobile's `requestAccessWhereMissing` (§K.6) is wired into the
launch sequence at Alpine parity. Left here because §2d itself is still open and the two were reported
together.

Also noted: `ConversationMemberService` is registered in DI and reached from nowhere, so the
conversation add-member path has no UI to surface unreachable devices through yet.

### Verified correct, so nobody re-checks them

- **Conversation-creation enumeration.** `/consume-tokens` returns one key package per active device
  and reports the rest in `unreachableDevices` per device (`ConsumeMlsTokensForUserHandler.cs:52-104`)
  — with the creator's own devices now included in the creation-time scan (§L.14, the one real bug).
- **Welcomes are per device end to end.** Fetch filters on `deviceId` and never consumes
  (`MlsEndpoints.cs:734-751`); ack is scoped to `(UserId, DeviceId)` and refuses rather than acking
  broadly with neither body field nor header (`:773-805`). A Welcome for device B cannot be consumed
  or hidden by device A. §E5/§E6 hold.
- **Alpine's sender path.** Consumes tokens for every participant including its own account, blocks
  creation on any `unreachableDevices` until a human accepts, passes `allowPartialDeviceCoverage`
  explicitly, and re-reads the server's list off the creation response. A silent partial set is not
  coming from the desktop sender.
- **Alpine's Welcome fetch and ack** always send `deviceId` and ack only what actually joined.
- **The registration 400 was mobile-only** (§L.12, as corrected). Alpine has never sent a placeholder
  identity key, so `rotated` was false for it and the old gate never fired.
- `certificateEnforcement` is `Observe` and §G/§H enforcement is unwired, so no certificate check
  removes or excludes anyone.

## 2e. The admission ceremony was unreachable for conversations — **found and fixed 2026-08-02**

§2c said the repair path was the §B join-request sweep. The sweep worked. **Nothing could act on what it
produced**, so every request it raised sat `Pending` until it expired.

Reported live: a friend's desktop could not read an encrypted DM, sending threw
`Conversation … is not encrypted here`, and a real `Pending` request with `requiredApprovals: 1`,
`approverUserIds: []` was visible on the server. One tap from either party would have admitted it. Four
independent defects meant no tap was reachable:

1. **Alpine never subscribed to `conversation.MlsJoinRequest`.** `messaging-websocket.service.ts`
   handled `MlsCommit`, `MlsDeviceRemoved`, `MlsDeviceAdmitted`, `MlsStateChanged` and `Welcome`. The
   server had been pushing the notification to every member of the conversation the whole time and it
   was discarded on arrival.
2. **Neither client had a conversation-scoped review UI.** `approve()` was implemented and correct on
   both, and on both its only caller was the *guild channel settings* encryption page. A DM request had
   no screen anywhere. This is the one the user actually hit.
3. **Mobile's `MlsJoinRequestDto` had no `keyPackage` field**, so the bridge passed `''` into
   `tryAdmit` and `inspectKeyPackage('')` threw. The server does return the bytes for the caller's own
   account (`MlsJoinRequestService.cs:217`); the DTO simply dropped them. Own-device admission could not
   succeed even in principle.
4. **`answerChallenge` had zero production callers on mobile** — two test callers, so the mechanism was
   pinned and never wired. The joining device never signed, so the admitting device's `tryAdmit`
   returned `awaitingProof` forever. The bridge also treated every own-account push as though this
   device were the admitter, when the requesting device receives that same push and must take the other
   branch.

**The push notification placeholder was never a separate bug.** v1.0.59's NSE diagnostics returned
`noGroupForGeneration (generation 1)` and `noGeneration`, with no keychain, App Group or seal failure
alongside them — so that plumbing is healthy and the `kSecAttrAccessible` incident is not recurring.
Both outcomes reduce to the registry having no entry for the context: the handset was being asked to
decrypt a group it had never been admitted to, and said so correctly. Fixing admission fixes the
notifications.

Diagnostics gap closed at the same time: a registry file that does not exist yields an empty map, which
was indistinguishable from genuine exclusion — both arrived as `noGroupForGeneration`. `registryAbsent`
and `registryEmpty` now separate them, and both generation outcomes carry `entries N, for this context
M`. `read` still answers `[:]` for a missing file, decided by a separate existence check rather than by
weakening the absence-vs-sealed distinction.

Also closed here: **§K.6 is done** — mobile's launch sweep is wired into `MlsSessionManager.sync()` at
Alpine parity (per-launch cap, sequential, filtered entirely from local state, so a healthy device makes
zero requests where it previously made one `getState` per conversation). Mobile gained an encryption
floor (`hasEverHeldGroup`, derived from retained registry keys) which it had lacked entirely, so "the
server says plaintext" is no longer believed unconditionally.

**Known behaviour change, being addressed:** mobile has no `conversation.MlsDeviceRemoved` handling at
all, so it has no equivalent of Alpine's `'removed'` health reason. A sweep plus a review UI without one
turns a deliberate removal into a recurring approval prompt for the person who performed it — the exact
wear-down §E9 exists to prevent, and invisible before today because neither half existed on mobile.

Still deliberately not built, and the reasoning is worth keeping: **Alpine cannot do §G at all.** The
Rust port is about half a day (`hkdf`/`hmac`/`sha2`/`rand` are already in `src-tauri/Cargo.toml` and
mobile's primitives are ~120 contiguous lines), but both halves of §G need the account master key with
no user present, and Alpine's `MasterKeyService` requires the password on every call and caches nothing.
The choice is persisting the master key at rest — which changes what a compromised keychain yields from
one device's signing key to the key that opens every backup blob on the account — or prompting on both
sides, which deletes the point of `TrustedSignIn`. Protection level does not exist on Alpine in any
form either, and `tryAdmit` cannot decide auto-vs-manual without it. Realistically 3–5 days *after*
those two decisions, in the order: master-key-at-rest → protection level → Rust port →
`DeviceAdmissionService` → wire into the review panel. Not safely half-buildable, for the same reason
§2b records.

**§L.4 remains open and now has a second witness.** Mobile's `tryAdmit` re-fetches the challenge from
the server and checks only `proof.challengeId != challenge.id`; nothing local pins the nonce or measures
the window on its own clock, so a malicious server can replay a genuine (nonce, proof) pair for the same
device and fingerprint indefinitely. The contract already states both clients do this (§L.4, line 1249)
and it sits in the deferred set with C4/C6/C7/H6 — recorded here so Alpine's eventual §G work implements
it properly rather than mirroring mobile.

## 3. Cross-cutting residuals

- **Legacy bare-`messageId` cache fallback.** `MlsStore._cacheKey`'s own comment documents it as a
  cross-conversation plaintext-disclosure vector. Draining it is a coordinated decision — do not
  diverge on one platform.

  **Correction (2026-08-02, Alpine review).** An earlier version of this line claimed "both clients
  now *write* the composite key and only *read* the bare one as a fallback". That is true of
  venta-mobile (`mls_store.dart:160`, `:481`, draining fallback at `:457-467`) and **false of
  Alpine**, which has no composite key on either side — it writes and reads the bare, server-chosen
  `messageId` (`mls.service.ts:341`, `:347`). The parity this doc asserted did not exist, and the
  gap is exploitable: see the Alpine review's H1. Alpine must adopt mobile's exact key shape before
  either platform drains anything.
- ~~**Alpine's frontend suite has timing-sensitive tests** that fail under CPU load.~~
  **Wrong, and fixed 2026-08-02.** There was no timing dependency — `voice-engine.service.spec.ts`
  contains no timer at all. `@angular/build:unit-test` defaults Vitest to `isolate: false` to mimic
  the Karma experience, so every spec file in a worker shares one module registry and `vi.mock` is
  not per-file: whichever file in the batch registers last wins. Vitest batches by core count, which
  is why the suite passed on a 32-core dev box and failed on a 4-core CI runner, and why adding two
  spec files appeared to break four unrelated ones. "Passes when run alone" meant "no other file in
  the batch to clobber the mock", not "no CPU contention".

  Fixed with `test.isolate: true` via `runnerConfig` in `angular.json`; no assertion changed and
  nothing skipped. **Lesson worth keeping:** the symptoms (`vi.mocked(...).mockImplementation is not
  a function`, `Cannot read properties of undefined (reading 'invoke')`) never name the cause, and
  the nondeterminism across identical re-runs was the real tell.
- **`mls_current_state_dir`** was added to Alpine with no TypeScript consumer.
- **Alpine's `export_backup` drops mobile's 9th `account_identity` argument** — no §H identity key
  on Alpine yet. The import side still reads it, so mobile-written blobs open intact. Revisit when
  §H lands on Alpine.

## 4. Not closeable from a dev machine

- **§I.8 old-client compatibility test.** Needs a real old-client artifact in CI. It is the gate
  for the whole staged rollout in §I.7 and is currently unmet — individual guarantees are pinned
  as unit tests, which is *not* the same thing.
- **Deploy Echo and apply migrations.** Two new migrations, additive and nullable, both `Down()`
  methods now fixed with real-Postgres rollback tests. Clients must not ship ahead of the server.

- **⚠ DEPLOY ORDERING — Echo must not go out before Alpine's `publicVerifier` fix.** Echo `6b4a560`
  hard-refuses any key-establishing write to `PUT /backup/recovery-key` without a `publicVerifier`
  (`BackupController.cs:344-351`; a brand-new account with `current is null` still reaches the
  check). Alpine's first-time E2EE setup uses exactly that route (`key-setup-dialog.component.ts:159`)
  and sends no verifier — and that call path **predates the hardening work** (present at
  `d016285^`, last touched in `4734c8b`), so this breaks the Alpine build that is **already live**,
  not just the dev tree. The failure is silent: `catchError` at `:173` collapses the server's
  actionable 400 into "Something went wrong. Please try again.", looping forever with no diagnostic.

  venta-mobile is **not** affected — `MasterKeyService.establish` deliberately routes its first write
  through the legacy `POST users/master` and picks up the recovery wrapping via the additive
  same-version path Echo backfills (`master_key_service.dart:195-233`).

  The derivation is now normative in contract **§L.11** so the clients cannot diverge on it.
- **Run before deploying:**
  ```sql
  SELECT count(*) FROM conversations WHERE encryption_state = 'encrypted' AND mls_group_id IS NULL;
  ```
  Those rows are permanently unusable and the `AddMlsGenerations` backfill skips them.
- **Alpine `d016285` is committed but not pushed.**
- **An external audit**, before any "secure" or "hostile-server" claim. Three adversarial reviews
  and a clean test suite are not an audit.

## 5. What can and cannot be claimed today

**Can:** messages are end-to-end encrypted with MLS via an audited library; the server holds no
group keys; backup blobs are opaque to the server; recovery codes are ~158.5 bits with verified
uniform sampling; backup reads are audited.

**Cannot:** anything about defending against a malicious or compromised server. §G/§H are inert
(§2 above), `certificateEnforcement` is permanently `Observe`, and until §2b lands the
multi-device admission model the spec describes is not the one running. Ship as improvements and
bug fixes, not as a security posture.
