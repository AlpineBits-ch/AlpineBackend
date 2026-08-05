# MLS Security Review - Consolidated Findings

Three independent adversarial reviews (Echo backend, venta-mobile, homegrown crypto/protocol),
2026-08-01, against the uncommitted MLS hardening work. Alpine's review is still outstanding.

**Verdict: do not ship this with a hostile-server security claim.** The bug fix (Waves 1-2) is
sound and shippable. The security apparatus built on top of it is not.

Counts across the three reviews: **~12 critical, ~20 high**, before dedup.

---

## 0. What changed about the threat picture

The earlier framing was "the §G/§H apparatus is built but not wired." That was too generous. Three
things are worse than that:

1. **Several links do not exist at all** - a route the client calls that the server never serves, a
   field the verifier needs that is never sent, an enforcement switch with no setter. §H.4 is not at
   reduced strength; it is at zero with no path to non-zero.
2. **Two findings would still be wrong after the wiring is finished** (C4, C6 below). Those are
   design errors in the spec, not incomplete plumbing.
3. **Ordinary users - not just a malicious server - can destroy other users' encryption state.**
   This is the biggest change. Previously the risk was framed around a hostile operator. Four
   findings (H1-H4) are exploitable by anyone who shares one channel or conversation with the
   victim.

---

## 1. Critical

### C1 - `rewrap-password` destroys the master key on a bare session token
`BackupController.cs:263-296`. No password, no re-auth, no proof. Overwrites `EncryptedMasterKey`
with caller-supplied bytes, clears `MasterKeyPasswordWrappingInvalidatedAt`, and returns
`encryptedHistoryRecoverable: true` - **erasing the loss signal**. For any account without a
recovery-code wrapping (per §I.2, every account in the field) this is permanent, irreversible
destruction of all encrypted history, in one request, reported as healthy.

The stated justification (§J.2: *"producing a valid wrapping is itself the proof"*) is a
non-sequitur - nothing verifies the wrapping, and the field that could (`publicVerifier`) is plumbed
through DTOs and entity but **never generated and never checked**.

> **Found independently by two reviewers.**

### C2 - `X-Device-Id` is self-asserted; per-device backup isolation is decorative
`BackupController.cs:62-69`. `IsCallingDevice` compares a header to a path parameter. Nothing binds
the header to the session. The JWT carries `session_id` and `LoginSession` has a `DeviceId` - the
join is never performed. `DeviceIdResolver` **fails open** when Identity is unreachable.

A stolen session reads any of the account's device backups. The audit row and `identity.BackupRead`
push both record the attacker-supplied device id, so **the forensic trail is forgeable**.
`PutBackup` and `GetBackupMeta` have no check at all.

§C.2 rule 1 and the controller's own doc-comment both claim the opposite.

### C3 - Account identity key: first publication needs no password, no audit, no broadcast
`AccountIdentityKeyEndpoint.cs:100-115`. Rotation requires the password; **first publication does
not**, and per §I.2 no existing account has one. Whoever publishes first becomes the account's
cryptographic identity to every peer that TOFU-pins it. Costs one session token.

> **Found independently by two reviewers.**

### C4 - Device certificates are never bound to the leaf they vouch for *(design error)*
`leaf_verification_service.dart:71-94`. `check()` takes `deviceId` and `deviceSignatureKey` and
**never reads either**; it verifies the certificate against the certificate's own self-reported
fields. Certificates are public. A server replays any genuine certificate for the account against a
leaf it injected and verification passes - **at full enforcement, with 100% coverage**.

§K.1's reasoning (sign the key as the base64 string "so neither side has to agree on a decoding") is
what produced this. The verifier must re-encode the signature key extracted from the MLS leaf and
compare.

### C5 - The §G admission ceremony is structurally unreachable *(spec contradiction)*
`MlsEndpoints.cs:435-438` pushes `conversation.MlsJoinRequest` to everyone **except** the requester.
`mls_realtime_bridge.dart:121-127` returns unless the request belongs to the receiving account.
**Disjoint sets** - the challenge is never issued.

Compounding: §G.1 requires the verifier to be the requester's *own other device* (the only party
holding the master key), but `ApproveAsync` explicitly forbids self-approval, and
`MlsJoinRequestDto` excludes `KeyPackage`, so the own-device path cannot get the bytes by any route.
**The only party who can approve is a peer, who by construction cannot verify a master-key HMAC.**

This is a hole in §G.1/§B, not a coding slip.

### C6 - The real device-add path verifies nothing
`consume-tokens` → `addMembers` is how devices actually join. Alpine passes `t.token` - whatever the
server returned - straight into `addMembers`. The `certificate` fields §J.1 added specifically to
prevent substitution are dropped (`mls.dto.ts:195-201` documents this). No fingerprint check, no
certificate check, no pin.

### C7 - External-commit injection is open, and any channel viewer can do it
The server stores and serves live `MlsGroupInfo`. `GET /channels/{id}/mls/state` requires only
`ViewChannel`. **Any channel viewer can external-commit straight into the group, bypassing the
join-request review entirely.** Mobile already ships this call as `_tryRejoin`, attempted *before*
falling back to a join request - so a server that serves a GroupInfo for a group it controls gets
the victim's device to join it and repoint the registry.

The only defence (§H.4) is gated on `MlsPolicy.CertificateEnforcement`, which **the server serves**
and which has **no setter anywhere in the codebase**.

> **Found independently by all three reviewers.**

### C8 - Message plaintext and all MLS private keys are unencrypted on disk and in device backups
`mls_message_cache.json` (every message ever decrypted, base64, unbounded, never evicted) and
`mls_state.json` (epoch secrets, leaf HPKE private keys, init private keys) are plain JSON.

`android:allowBackup` is not declared → **defaults true**, no `dataExtractionRules`. iOS has no
`isExcludedFromBackup` and no file protection, at App Group container root. An Android
restore-to-attacker-device or any unencrypted iOS backup yields the **complete plaintext history
plus live group keys, with no keychain access and no unlock**.

AES-256-GCM is already in the file (used by `export_state`) and simply never applied to the live
store.

### C9 - The server can inject arbitrary text into an E2EE thread and attribute it to anyone
- `MessageUpdated` content is rendered verbatim - no decrypt, no encryption-state check, no
  indicator (`message_repository.dart:187-193`).
- The displayed author is `payload['authorId']`, a plain server field, **never compared** to the
  authenticated `senderIdentity`. The correct check exists in the push decryptor and was never
  applied in-app.
- `verifySenderInRoster` is a **tautology** - it checks that a credential openmls already verified
  against an in-tree leaf is in the roster. Its doc comment claims it stops server spoofing.
- Encryption state itself is a server field; a message marked `Plain` skips the decryptor entirely.
- Editing an encrypted message posts the new text **in cleartext**.

---

## 2. High - exploitable by ordinary users, no server compromise needed

### H1 - Cross-user device destruction
`DeviceRemovedHandler.cs:53-70` matches `PendingWelcomes` and `MlsJoinRequests` on `ClientDeviceId`
**with no user scope**, while `message.UserId` sits unused. Since this change set, `ClientDeviceId`
is only unique *per user*. Victim device ids are readable from `MlsJoinRequestDto`, the
`MlsDeviceAdmitted` push, and `unreachableDevices`.

Register a device with the victim's id, delete it → the victim's **unclaimed Welcomes are consumed
across every context** (single-use, unrecoverable) and their pending join requests cancelled.
`PurgeUserDataCommandHandler` scopes the identical queries correctly; this one doesn't.

### H2 - Cross-user join-request cancellation
`MlsJoinRequestService.cs:101-119`. The dedup lookup ignores `RequesterUserId`, and `dto.DeviceId` is
never validated against the caller's devices. Any co-member cancels the victim's pending request,
repeatably.

### H3 - Fabricated security notifications, and admission-budget burning
`FulfilledJoinRequestIds` is never tied to the commit's contents or the caller. A member attaches
arbitrary pending ids from the same context: they leave `Pending` (never approvable), count against
the 24h auto-admission budget, and **emit `identity.DeviceAdmitted` for devices that were never
added** - naming a real device and fingerprint.

### H4 - Any member can permanently wedge a group's epoch
The server never parses commits. Publish a proposal with `isProposal: false` (or garbage bytes) at
`epoch+1`; the server advances `active.Epoch` to a value no client can reach. Every honest commit
then 409s forever. Recovery requires a full re-key. **Channel variant needs only `ViewChannel`.**

### H5 - Unlimited online password guessing, no lockout
Four endpoints gate on `UserManager.CheckPasswordAsync`, which **does not increment lockout
counters**. Only bound is 100 req/min → ~144k guesses/day, no lockout, no audit, no notification.
§C.1 says "rate limited"; nothing is. This converts a stolen token into the password - and §G says
`TrustedSignIn`'s entire ceiling *is* password strength.

> **Found independently by two reviewers.**

### H6 - Protection-level rollback works
Client never compares the fetched `version` to the remembered one; a genuinely-signed older
assertion verifies and **overwrites the stored floor downward**. §G.3's stated purpose is not
achieved on either client. (Currently masked because `refresh()` has no caller - fixing that without
this makes it live.)

Separately, no assertion can *ever* verify today: `PutProtectionLevelDto` has no `UpdatedAt` field,
so the client signs its own timestamp and verifies against the server's. Fail-closed, so not itself
a vulnerability, but the tier system is inert.

> **Found independently by two reviewers.**

### H7 - Key-package drain
20 calls / 2 min, each consuming one package from **every device of every user in an unbounded
list** ≈ 600/device/hour against a stock of 100. Drain in ~10 minutes by a friend. Under
`VerifiedDevices` (no last-resort fallback) the victim becomes `Unreachable` and cannot be added to
any new conversation.

### H8 - Backup destruction with a stolen token
`PutBackup` has no device check; retention is 3 versions. Three writes destroy the real blob.
`DELETE /devices/client/{id}` cascade-deletes the blob with **no device check and no re-auth**,
bypassing `DeleteBackup`'s guard.

### H9 - Ratchet configuration is backwards in both clients *(verified against the crate)*
`openmls-0.8.1/src/tree/sender_ratchet.rs:40` - `new(out_of_order_tolerance,
maximum_forward_distance)`, default `(5, 1000)`. Both clients call `new(500, 10)` with comments
asserting the reverse order. Live config: `out_of_order_tolerance = 500`,
`maximum_forward_distance = 10`.

- Up to **500 spent message secrets retained per sender per epoch**, written into the cleartext
  state file (C8). Intra-epoch forward secrecy degraded ~100×.
- Messages more than **10 generations ahead are rejected** → permanent loss after ~11 lock-screen
  notifications in one epoch, no attacker required.

One-line fix; the two literals are transposed.

---

## 3. Selected medium

- **Both `Down()` migrations are broken**, and app rollback is broken independently (the old
  one-to-one `UserDevice.Backup` navigation faces multiple rows). **§I.6/§I.8's rollback obligation
  is unmet** - contrary to what the contract claims.
- **The audit log is write-only.** No endpoint reads `IdentityAuditEvents`; §G.3's "surfaced in the
  UI" does not exist. `KeyPackagesReset` and `DeviceIdentityRotated` are declared and **never
  written** - the two destructive device operations leave no trace.
- **Backup-read visibility fails when it matters** - transient SignalR push, no durable fallback, no
  readable audit. A 3am read against an account with nothing online is invisible forever.
- **Channel Welcomes can be addressed to arbitrary users** (recipient filter skipped for channels).
- **No request-size caps** except the backup blob; 30 MB default applies to commits retained 30 days.
- **`capabilities: []` erases capabilities** with no password, permanently blocking entry to
  `VerifiedDevices` - while the downgrade it mirrors requires one.
- **Identity rotation purges key packages** with no re-auth and no audit row.
- **Backup import trusts unbounded Argon2 parameters** (`m` up to 4 TiB) - OOM/hang on the recovery
  path. *(Note: the "weak parameters make a stolen blob crackable" attack does **not** work - it
  fails closed. The exposure is resource exhaustion only.)*
- **No certificate revocation.** 180-day lifetime, no CRL, no status. Revoking a device does not
  revoke its certificate; the only remedy is identity-key rotation, which invalidates every peer's
  pin.
- **`MlsPolicy` is process-static with no configuration binding** - the §I.1 kill switch cannot be
  flipped without a redeploy and can diverge across instances.
- **Android `resetOnError` defaults `true`** - one Keystore fault wipes the master key, identity key
  and signing key permanently.
- **Release APK is signed with the debug keystore** (fixed password `android`) - anyone with that
  file ships a signature-matching update with the same UID, and therefore Keystore access.
- **Every `debugPrint` becomes a Sentry breadcrumb in release** (DSN hard-coded,
  `tracesSampleRate = 1.0`, no `kDebugMode` gate). No keys or plaintext, but conversation/message/
  user/device IDs leave the device.

---

## 4. Where the spec is wrong - these are mine

1. **§G.1 states the master key is "Argon2id-derived from the password." It is not** - the code uses
   `random(32)`, per §C.1.1. Anyone implementing §G.1 as written turns every intercepted admission
   proof into an **offline password-cracking oracle**. The code is right; the spec is dangerous.
2. **§K.1's base64-string rationale directly produced C4.**
3. **§G.1 contradicts §B** on who approves a conversation join request - see C5. Spec hole.
4. **§J.2 never requires the verifier to compare the nonce against its own record.** "Never has to
   trust the server's account of *what* was signed" is a different property from freshness. Both
   clients made the same mistake. The contract must require the issuer to persist
   `(challengeId, nonce, issuedAt)` and reject any proof whose challenge bytes differ.
5. **§I.1 hands the malicious server the off switch for §H.4.** "Never let a client infer the phase
   from its own version" is right for the missing-certificate case and wrong as a blanket rule.
   Independent of phase: a peer that has *ever* presented a valid certificate must not be accepted
   without one later, and an unknown credential identity must be surfaced at every phase.
6. **§G.3 specifies no monotonic version floor**, so rollback works (H6).
7. **§C.2 rule 1 assumes a validated `X-Device-Id`. No such thing exists** (C2).
8. **§C.1's `publicVerifier` is cited as the safety mechanism and is implemented nowhere.**
9. **§H.2's TOFU pinning has no comparison surface** - no safety-number UI, which is the half of
   Signal's mechanism that makes pinning mean anything.
10. **§K.1's admission payload binds `requesterDeviceId`, which is not present in a KeyPackage**, so
    it can never be cross-checked against the leaf being added.

---

## 5. What is genuinely sound

Traced in code by the reviewers, not taken from comments:

- **The original double-base64 bug is fixed** on all four paths, with a real regression guard, and
  no encoding sniffing anywhere.
- **No wrong-key-silently anywhere** on the decrypt path - one group resolved from the generation
  the message names, no fallback, no trial loop.
- **Recovery codes are correct**: 31-symbol alphabet, rejection sampling with the right 248 bound
  (248 = 8 × 31 exactly), 158.53 bits, total fail-closed normalisation, constant-time
  `verify_slice`.
- **Backup envelope AEAD hygiene is correct** - fresh salt and nonce per export, AAD bound and
  cross-checked after decryption, `userId` mismatch refused, engine skipped unless the device id
  matches (enforced in Rust, not documentation).
- **HKDF with `salt = None` is fine** here (uniform 32-byte IKM), and deriving a subkey does prevent
  proof → master-key inversion. The proof is **not** a master-key oracle, and because the master key
  is random (not password-derived) a captured proof is **not** an offline cracking target.
- **`tagged_payload` length-prefixing is correct** and unexploitable today - though safe partly by
  accident (the three labels are prefix-free).
- **`admit()`'s three re-derivations** from actual key-package bytes are real, applied on both
  paths, on both clients, and do more than the contract requires.
- **Epoch conflict handling** is sound: real partial unique index, 23505 → 409, filter correctly
  excludes proposals. Idempotent republish is correctly narrow (byte equality required).
- **`GetWelcomes` no longer consumes**; `AckWelcomes` correctly scoped; commit retention respects
  unconsumed Welcomes; key-package operations are correctly user-scoped.
- **iOS keychain configuration and the NSE read-only design are both right** - the NSE genuinely
  cannot desync the app's ratchet, and reads no keychain items.
- **No secret is ever logged** - verified by grep across all four languages.
- **Fail-closed directions are correctly chosen** where they exist: `MlsPolicy` → `Observe`,
  protection level → strict, `ResolveProtectionLevelAsync` → `VerifiedDevices`.

---

## 6. What can and cannot be claimed today

**Can:** messages are E2E encrypted with MLS via an audited library; the server holds no group keys;
backup blobs are opaque to the server; backup *reads* are audited; recovery codes are strong; a
password reset no longer silently destroys history on the reset path itself.

**Cannot:**
- *"Defends against a malicious or compromised server."* False at both tiers, and would remain false
  with §G/§H fully wired (C4, C6).
- *"`TrustedSignIn` is a real security level."* Not implemented; live behaviour is manual approval
  only - which is safe, and is not what the spec describes.
- *"External commits are not a free pass."* Nothing enforces this, and any channel viewer can walk in.
- *"Your history is safe from a stolen session."* C1 destroys it in one request.
- *"Device revocation removes access."* It doesn't revoke the certificate or the master key, and can
  be undone by proof replay.
- *"`VerifiedDevices`"* as a shipped tier at all.

---

## 7. Recommended order

1. **C1, C2, C3** - three one-request catastrophes, all server-side, all small.
2. **H1, H2** - add `UserId` to two queries. Two lines; closes cross-user destruction by any
   co-member.
3. **H9** - transpose two literals. Cheapest real security win in the list.
4. **C8** - encrypt the two state files (primitives already present); `allowBackup="false"` and iOS
   exclusion.
5. **C9** - route `MessageUpdated` through the decryptor, display the authenticated sender, refuse
   `Plain` in an encrypted context, encrypt the edit path.
6. **H4, H3, H5, H7, H8** - server-side hardening.
7. **C4, C5, C6, C7** - fix the *spec* first, then the code. These are the ones that would still be
   wrong after all remaining wiring is done.
8. Fix the `Down()` migrations before any deploy that might need a rollback.

Waves 1-2 remain shippable without any of this, provided no hostile-server claim is made.
