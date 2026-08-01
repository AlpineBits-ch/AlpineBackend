# MLS Hardening — Cross-Repo API Contract (v1)

**This file is the coordination point for three repos working in parallel.** Echo implements it;
Alpine and venta-mobile code against it. Nobody changes a shape here without updating this file.

Companion to `mls-hardening-plan.md`. Route-attribute style follows the existing code: Messaging
uses `[WolverinePost("/api/v1/...")]` (leading slash), Identity uses
`[WolverinePost("api/v1/...")]` (no leading slash).

---

## A. Key-package reset (plan P0-3, root cause R3)

The single most important new route. A client that wipes local MLS storage **must** call this
before re-uploading, or the server keeps handing out key packages whose private halves are gone.

```
DELETE api/v1/devices/client/{deviceId}/key-packages
  → 200 { "deletedCount": <int> }
  → 403 if {deviceId} is not a device of the caller
  → 404 if no such device
```

Deletes **every** `UserKeyPackage` for the device, including the last-resort one and including
already-consumed rows (they are dead either way). Does **not** touch the device row, push tokens,
or sessions. Idempotent.

**Client obligation — both clients, non-negotiable:** every code path that clears local MLS state
must call this *first*, then re-register, then run the normal replenish. Specifically:

- Alpine: `main-page.component.ts:272-278` (the wipe-on-parse-error path) and the logout wipe.
- Mobile: `mls_service.dart:69-82` (the corrupt-state wipe).
- Both: immediately after minting a **new signing keypair** on an existing `clientDeviceId`.

Server-side companion fix: `POST api/v1/devices` (`MlsDeviceEndpoint.cs:30-37`) currently returns
an existing row unchanged. It must now **update `IdentityPublicKey`** when the submitted key
differs, and when it does, purge that device's key packages as above and return
`"identityRotated": true` so the client knows to re-upload.

---

## B. Conversation-scoped MLS join requests (plan P0-2, root cause R2)

Mirror the channel routes exactly, reusing `MlsJoinRequestService` verbatim — it is already
context-agnostic. `contextId == conversationId`.

```
POST   /api/v1/conversations/{conversationId}/mls/join-requests
GET    /api/v1/conversations/{conversationId}/mls/join-requests
POST   /api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/approve
POST   /api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/deny
DELETE /api/v1/conversations/{conversationId}/mls/join-requests/{requestId}
```

Request/response DTOs are **identical** to the channel equivalents (`MlsEndpoints.cs:246-346`).
Authorization is conversation membership instead of `ViewChannel`.

### Approval threshold for conversations

`MlsJoinRequestService.RequiredApprovalsFor` currently returns 2, relaxed to 1 when only one actor
has been seen. For a **conversation** the threshold is **always 1** — a DM has two humans and
requiring two approvals deadlocks it. Add an explicit context-kind parameter rather than inferring
it from actor count.

Also fix, in the same change: the `AddMlsGenerations` backfill wrote `activated_by_user_id = ''`,
and the empty string is counted as a real actor by `RequiredApprovalsFor`. Treat null/empty as
"no actor".

### Discovery — how a new device knows to ask

Client-driven; no new server push required for the happy path.

On first launch after registration, and on every launch where the local MLS store holds no group
for a conversation, the client:

1. lists its conversations,
2. calls the existing `GET /api/v1/conversations/{id}/mls/state`,
3. for each conversation reporting encrypted **where it holds no local group**, submits a join
   request (idempotent — resubmitting an open request returns the existing one).

### Notifying the approver

`MlsJoinRequestService` already pushes on submit for channels. Extend the same push to
conversations:

```
conversation.MlsJoinRequest   → { contextId, conversationId, requestId, requesterUserId,
                                  requesterDeviceId, requesterDeviceName, generation }
```

Delivered to all conversation members except the requester. Clients surface it as
*"<name> added a new device — approve?"*, showing the signature-key fingerprint for out-of-band
comparison.

Whether a request can be satisfied **without** a human tapping approve is governed by the
protection level in **§G**. Read that before implementing the approval path.

---

## C. Encrypted backup blobs (plan §6)

### C.1 Recovery-key envelope

Generalises the existing `ApplicationUser.EncryptedMasterKey`.

```
PUT api/v1/backup/recovery-key
  body { version, kdf: "argon2id", iterations, memoryKiB, parallelism,
         salt: b64, iv: b64, cipherText: b64, publicVerifier: b64 }
  → 200 { version, orphanedBlobDeviceIds: [ ... ] }
  → 409 if blobs would be orphaned and ?acknowledgeOrphans=true was not passed
  Requires re-authentication (password or step-up). Rate limited.

GET api/v1/backup/recovery-key
  → 200 <envelope>   |   404 if never set
```

Bumping `version` invalidates every blob stored under the previous version. The endpoint must
return the affected device ids and refuse without `?acknowledgeOrphans=true`.

Fix in the same change: the write-once guard at `UserController.cs:45-48` compares
`user.EncryptedMasterKey?.Version == dto.Version`, which silently permits overwriting the wrapped
master key with a *different* version. Require re-auth and write an audit row.

#### C.1.1 The master key must be wrapped twice — password reset otherwise destroys it

`PasswordResetEndpoint.ResetPassword` (`Identity.Application/Endpoints/PasswordResetEndpoint.cs:73`)
calls `manager.ResetPasswordAsync(...)` and **never touches `EncryptedMasterKey`**. The envelope
stays wrapped under Argon2(old password) — a password the user has, by definition of a reset,
forgotten. Every backup blob, and the account identity key of §H.2, become permanently unopenable,
silently, at the exact moment the user is trying to recover their account.

This is not a hypothetical: it is today's behaviour, and §H.7 makes it fatal by naming the password
as the recovery credential for `TrustedSignIn`.

**Required shape.** The master key is a random 32 bytes and is wrapped **twice**, under two
independently-derived keys:

```
masterKey            = random(32)                       # never leaves the client
wrapped_password     = AEAD(Argon2id(password,      salt_p), masterKey)
wrapped_recoveryCode = AEAD(Argon2id(recoveryCode,  salt_r), masterKey)
```

Both wrappings are stored; the server holds only ciphertext and KDF parameters. Consequences:

- **Password reset** invalidates `wrapped_password` only. The client re-derives the master key from
  the recovery code and re-wraps it under the new password. Nothing is lost.
- **Password change** (user knows the old password) re-wraps in place, no recovery code needed.
- **Losing both** the password and the recovery code is unrecoverable — state this in the UI at
  setup time, before the user can proceed, not in a help article.

The recovery code is generated at encryption setup, shown once, and the user must confirm it. It is
the *only* credential that survives a password reset, which is precisely why §H.7 requires it for
`VerifiedDevices` — and why `TrustedSignIn` accounts should be strongly encouraged to save one too.

#### C.1.2 Recovery code format — **authoritative, supersedes §K.7**

The two clients implemented different alphabets, which silently breaks cross-client recovery. This
subsection is the single source of truth; §K.7 is superseded where it disagrees.

| | Value |
|---|---|
| Alphabet | `23456789ABCDEFGHJKMNPQRSTUVWXYZ` — **31 symbols, exactly** |
| Length | 32 characters, eight groups of four, joined with `-` |
| Entropy | ~158.5 bits |
| Generation | **Rejection sampling**: draw a byte, discard if `b >= 248`, else `alphabet[b % 31]` |
| Normalisation | Strip all whitespace and `-`, uppercase, then validate |
| Invalid input | **Return an explicit error. Never fall back to the raw input.** |

**What went wrong, so it does not recur.** Mobile appended `*` as a 32nd symbol purely so that
`b & 0x1f` would be uniform. That is sound reasoning about bias and the wrong trade here: `*` is
punctuation in a string a human copies off paper under stress, and — decisively — it is not in
Alpine's alphabet, so Alpine's validator rejects any mobile-generated code containing it. Alpine's
validator then does `else { input.to_string() }`, feeding the *unnormalised* input to the KDF. The
result is not "invalid code"; it is a **different key, silently**, during the one operation the
code exists for.

Rejection sampling removes the bias without adding a character. The discard rate is 8/256 and the
loop is bounded in practice; this is not a hot path.

**Both clients must therefore:**

1. Use the 31-symbol alphabet above. Mobile removes `*` and replaces masking with rejection
   sampling.
2. Make normalisation **total**: strip, uppercase, validate, and on failure return a typed error
   that the UI renders as *"that code isn't valid"*. A silent fallback to raw input is forbidden —
   it converts a recoverable typo into unrecoverable data loss with no diagnostic.
3. Never apply recovery-code normalisation to a **password**. Passwords are case-sensitive and may
   contain anything; both clients already guard this, keep it.
4. Carry a test asserting a code generated by *the other client* opens this client's wrapping. The
   golden-vector pattern of §F applies: check in a fixture code plus its wrapping, and assert
   cross-consumption in both directions. Without it, this class of bug is invisible until a user
   is mid-recovery.

> **⚠ Open server-side blocker: the retrofit path (point 5) cannot currently succeed.**
>
> `PUT api/v1/backup/recovery-key` returns early on a same-version write:
>
> ```csharp
> if (current is not null && dto.Version == current.Version)
>     return Ok(new PutRecoveryKeyResultDto { Version = current.Version });   // BackupController.cs
> ```
>
> That branch never writes `RecoveryCodeWrappedMasterKey`. So an existing account — every account
> in the field, all of which have only the password wrapping — cannot add the second one:
>
> - **Same version** → `200 OK`, nothing stored. The client would show the user a recovery code
>   that opens nothing, while telling them they are now protected. Worse than not offering it.
> - **Version + 1** → writes, but `orphaned` is computed as `RecoveryKeyVersion <= current.Version`,
>   so it invalidates *every* existing device backup blob. The master key has not changed; nothing
>   should be orphaned. This is the opposite of what a user asking for more safety expects.
>
> **Required fix:** allow a same-version write when it only *adds* `RecoveryCodeWrappedMasterKey`
> to an account that has none. It is purely additive — same key, same version, no blob affected.
> Keep the early return for the genuinely idempotent case (both wrappings already present and
> unchanged).
>
> Alpine implements the correct client behaviour (same version, additive) and **verifies the write
> landed by re-reading the envelope**, refusing to show the user a code the server did not store.
> Until the server is fixed the retrofit fails visibly instead of silently, which is the best a
> client can do on its own.

**Server obligations:** `ResetPassword` must mark the password wrapping stale (a
`MasterKeyPasswordWrappingInvalidatedAt` stamp) and return a flag telling the client to run
re-wrapping on next unlock. An account with no recovery-code wrapping and a completed password reset
must be reported to the user as *"encrypted history is no longer recoverable"* rather than silently
appearing to work until the first restore attempt fails.

### C.2 Per-device blob

```
PUT api/v1/devices/client/{deviceId}/backup
  Content-Type: application/octet-stream          (opaque ciphertext — server never parses it)
  Headers: X-Backup-Version, X-Backup-Recovery-Key-Version, If-Match: <etag>
  → 200 { blobId, version, etag, sizeBytes, updatedAt }
  → 409 etag mismatch | → 412 recoveryKeyVersion mismatch | → 413 over cap

GET    api/v1/devices/client/{deviceId}/backup        → octet-stream + ETag + metadata headers
GET    api/v1/devices/client/{deviceId}/backup/meta   → { version, recoveryKeyVersion, sizeBytes,
                                                          updatedAt, etag }
DELETE api/v1/devices/client/{deviceId}/backup
GET    api/v1/devices/backups                          → metadata for all the caller's devices
```

Backed by the existing, currently-unused `UserDeviceBackup` entity
(`Identity.Domain/Entities/UserDeviceBackup.cs`, mapped at `MicroserviceContext.cs:135-144`).
It needs new columns: `Version`, `RecoveryKeyVersion`, `SizeBytes`, `UpdatedAt`, `ETag`.

**Server rules:**

1. `{deviceId}` must be a device of the caller **and equal the validated `X-Device-Id`** — a
   compromised web session must not be able to read the desktop's blob.
2. Every **read** writes an audit row and pushes `identity.BackupRead` to the owner's other
   devices. Backup exfiltration must be visible.
3. Size cap **16 MiB**; per-device write rate limit of 1/minute.
4. Retain the last **3** versions per device so a corrupt write cannot destroy the only copy.
5. Cascade-delete with the device (already the FK shape) and on account purge.

### C.3 Device-to-device transfer

```
POST api/v1/backup/transfers
  body { targetDeviceId, wrappedTo: <target's HPKE public key>, cipherText: b64, expiresInSeconds }
  → { transferId, expiresAt }
GET  api/v1/backup/transfers/pending          → for the calling device only
POST api/v1/backup/transfers/{id}/claim       → the ciphertext, then hard-deleted (single use)
```

---

## D. Backup envelope format (both clients — must match byte for byte)

```jsonc
{ "v": 1,
  "kdf":   { "alg": "argon2id", "salt": "<16B b64>", "m": 65536, "t": 3, "p": 4 },
  "aead":  "AES-256-GCM",
  "nonce": "<12B b64>",
  "aad":   "venta.keybackup.v1|<userId>|<deviceId>",
  "ct":    "<b64>" }
```

Decrypted payload:

```jsonc
{ "userId": "...", "deviceId": "...", "createdAt": "<iso8601>", "appVersion": "...",
  "engineRestore": "same-device-only",
  "signing":       { "pub": "b64", "priv": "b64", "identity": "<userId>" },
  "engine":        { /* PersistedMlsState: { version, group_ids, storage } */ },
  "groupRegistry": { "<ctxId>#<gen>": "<groupIdB64>", "<ctxId>#active": 3 },
  "messageCache":  { "<messageId>": "<plaintextB64>" }
}
```

All base64 is **standard with padding**, matching the existing `base64::engine::general_purpose::STANDARD`
used across both Rust engines. File extension `.venta-keys`.

> **The envelope's `p: 4` is not the same as the master key's `p: 1`, and that is intentional.**
> `ApplicationUser.EncryptedMasterKey` uses Argon2id with **one** lane over the same `m`/`t`; this
> envelope uses four. They are separate KDF uses that merely share a shape — do **not** "align"
> them, because doing so would orphan every blob already written under the other value.
>
> This is safe only because **both formats are self-describing and both readers derive from the
> declared parameters, never from a compiled-in constant.** `EncryptedMasterKey` carries
> `argon2_iterations`/`argon2_memory`/`argon2_parallelism`; this envelope carries `kdf.m/t/p`. Any
> implementation that hardcodes either set on the *reading* side is wrong, and will fail only on a
> cross-device or cross-version restore — the exact moment recovery matters. Alpine pins this with
> `declared_kdf_parameters_are_the_ones_actually_used` (`src-tauri/src/crypto/mls.rs`), which
> perturbs each declared parameter in turn and asserts the import then fails.

**Restore rules — enforced in code, not documentation:**

| Situation | Behaviour |
|---|---|
| `deviceId` in the blob **equals** this device's id | Import everything, including `engine`. |
| `deviceId` differs (moving to a new device) | Import `signing`, `groupRegistry`, `messageCache`. **Skip `engine`.** Then re-register, replenish key packages, and submit a join request per encrypted context (§B). |
| `userId` differs from the signed-in account | **Refuse.** |

The discriminator is **the device id alone**. An earlier draft of this table also allowed the
`engine` import when "the engine holds no groups", which was wrong: a genuinely new device always
has an empty engine, so that clause collapsed the two rows into one and would have cloned ratchet
state onto every new device — precisely what §D exists to prevent.

Same-device recovery (reinstall, restore to a replacement handset) must therefore **adopt the
backup's `deviceId` before importing**, not after. This is required independently of the rule
above: on Alpine the keychain entries are named `alpine_mls_{deviceId}_*`, and on mobile the MLS
identity is scoped `venta.mls.<deviceId>.<userId>`, so a restore that keeps a freshly-generated
device id cannot find its own signing key. Gate the adoption on the old device having been
deregistered (`DELETE api/v1/devices/client/{id}`) so exactly one leaf ever exists.

### D.1 Client command signatures

The envelope is assembled and opened **inside the Rust engine**, because the signing keypair lives
in `MlsState.signers` and deliberately never crosses the FFI/IPC boundary in either direction.

The plan sketched these as `mls_export_backup(passphrase, includeMessageCache)`. That is not
implementable as written: the group registry and the message cache live in the *host* layer
(Alpine's `LazyStore` files, mobile's equivalent), and Rust cannot read them. So the host passes
those two in, and everything the engine already owns stays where it is:

```
mls_export_backup(passphrase, userId, deviceId, appVersion, keyHandle,
                  groupRegistry: Map<String, JsonValue>,
                  messageCache: Option<Map<String, String>>) -> String   // the envelope JSON

mls_import_backup(blob, passphrase, expectedUserId, currentDeviceId)
    -> { userId, deviceId, createdAt, appVersion, identity, keyHandle,
         engineRestored: bool, groupRegistry, messageCache }
```

`messageCache: None` **is** `includeMessageCache: false`, and is what the cloud target passes.
`engineRestored` reports the §D rule the engine applied, so the host can tell a full restore from a
keys-only one without re-deriving the decision. The registry and cache come back out for the host
to write into its own stores.

**This changes no bytes in the envelope** — it is a host/engine split, not a format change. Alpine
implements exactly the above (`src-tauri/src/crypto/mls.rs`); mobile must match the *envelope*, not
necessarily the parameter list, since its host layer differs.

Cloning `engine` onto a second concurrently-live device reuses ratchet generations (openmls treats
the repeat as a replay, so a device becomes unable to send), voids forward secrecy for that leaf,
and breaks post-compromise security. The `engineRestore` marker exists so the import path can
enforce this rather than relying on the operator remembering.

After **any** import: reload the signing key into the engine, re-run `replenishKeyPackages` (the
imported engine's packages are already consumed server-side), and call §A to reset the stale stock.

Exclude `messageCache` from the cloud target by default; offer it only on the local-file path.

---

## E. Behavioural fixes visible across the boundary

| # | Change | Affects |
|---|---|---|
| **E1** | Message read path: `content` on the wire is `base64(utf8(posted))`. **Decode once before handing to the MLS engine.** The push payload's `ciphertext` is already single-encoded and must **not** be decoded. | mobile (broken), Alpine (already correct) |
| **E2** | Remove-**proposals** must not travel the commit channel. Server: do not advance `active.Epoch` for a proposal. Clients: do not count a proposal toward `applied` when deciding whether to keep paging. | all three |
| **E3** | Concurrent commit now returns **409 `MlsEpochConflictDto`** instead of 500. Clients already retry on 409; no client change needed beyond removing any 500-specific handling. | Echo |
| **E4** | Commit publish is **idempotent**. If a client re-publishes a commit it already sent (matched on `senderDeviceId` + `generation` + `epoch` + payload hash), the server returns 200 with the stored row rather than 409. This is what lets a client recover from a lost response instead of discarding a staged commit and forking itself off the group. | all three |
| **E5** | `GET /api/v1/conversations/welcomes` **requires** `deviceId`. The legacy no-`deviceId` branch is removed — it consumed welcomes across *all* of a user's devices, which is unrecoverable loss. Alpine must delete `conversation.service.ts:24-26`. | Echo, Alpine |
| **E6** | `POST /api/v1/conversations/welcomes/ack` is scoped by `(UserId, DeviceId)`, not `UserId` alone. `deviceId` becomes required. | Echo, both clients |
| **E7** | Conversation creation validates reachability **per device**, not per user. New response field `unreachableDevices: [{ userId, deviceId }]`, and creation is **rejected** if any member has an unreachable device unless the caller passes `?allowPartialDeviceCoverage=true`. Alpine currently creates anyway and merely displays the list — it must now either block or pass the flag explicitly. | Echo, Alpine |
| **E8** | Channel commits now fan out. New push `conversation.MlsCommit` is emitted for channel contexts too (it previously went to nobody). Clients must not assume a channel commit push implies conversation membership — switch on `contextId`. | Echo, both clients |
| **E9** | `DeviceRemoved` gains a consumer: the removed device is proposed out of every group it holds a leaf in, and members are nudged to commit the removal. Clients must handle being removed (surface it, stop offering to send, offer re-link). | all three |
| **E10** | `GET .../mls/commits` never returns commits from a different generation when the generation is unspecified and none is active. | Echo |

---

## F. Test obligations

Each repo owns its side. Two are **shared** and must agree:

- **Golden vectors.** One client's Rust generates a KeyPackage, Welcome, commit and application
  message; the fixture is checked into **both** repos under `testdata/mls-golden/v1/` and each
  repo asserts its engine consumes the other's output. This converts the version pin in
  `venta_mls/rust/Cargo.toml` from a comment into an assertion.
- **Wire-envelope round-trip.** Both clients assert `decode(base64(utf8(encrypt(x)))) == encrypt(x)`
  for the REST/socket path and `decode(utf8(encrypt(x))) == encrypt(x)` for the push path, so E1
  can never regress.

---

## G. Protection levels — two-tier device admission

Requiring a human to approve every device is correct for a threat model that includes a hostile
server, and wrong as a default: it strands people who reinstall an app at 2am with no second device
to hand. So admission is governed by an account-level **protection level**.

| | `TrustedSignIn` (default) | `VerifiedDevices` (opt-in) |
|---|---|---|
| Admitting a new device | Automatic, on proof of the account master key | Automatic proof **plus** a human approving on an existing device |
| Defends against a malicious or compromised **server** | ✅ | ✅ |
| Defends against a network attacker | ✅ | ✅ |
| Defends against someone who knows your **password** | ❌ | ✅ |
| Recovery if you lose every device | Password | Recovery code only — **no password reset can restore E2EE** |
| Last-resort key package (reusable) | Permitted | **Disabled** — no forward-secrecy loss on join |
| Cloud backup of engine state | Permitted | Requires the recovery code; off by default |

Names are the user-facing strings. If you prefer different wording, change it in one place here —
but keep the *mechanism* names `TrustedSignIn` / `VerifiedDevices` as the enum values.

### G.1 What makes `TrustedSignIn` more than "trust the server"

The naive version of auto-approval — the server says a device belongs to you, so an existing device
adds it — hands the server exactly the power MLS exists to remove. Do not build that.

Instead, **the joining device must prove possession of the account master key**, which is
Argon2id-derived from the password and which the server holds only in wrapped form
(`ApplicationUser.EncryptedMasterKey` — ciphertext, salt, IV and KDF parameters, no plaintext).

Flow:

1. The joining device submits its join request as in §B, including its signature-key fingerprint.
2. An existing online device of the same user receives `conversation.MlsJoinRequest`, and issues a
   challenge: 32 random bytes, posted to
   `POST /api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/challenge`.
3. The joining device signs `challenge || requesterDeviceId || signatureKeyFingerprint` with a key
   derived from the master key (HKDF, info `"venta.device-admission.v1"`) and submits the proof.
4. The existing device verifies the proof **locally** — it holds the master key too — and only then
   mints the Add commit.

The server relays but cannot forge: it never holds the master key, so it cannot produce a valid
proof for a device it injected. A server that adds a device to your account still cannot get that
device into any group.

This is why `TrustedSignIn` is a real security level and not a convenience flag. Its honest
limitation is stated in the table: it reduces to your password's strength.

### G.2 Non-negotiable obligations for `TrustedSignIn`

Auto-approval is silent *admission*, never silent *notification*:

1. **Broadcast.** Every other device of the user gets `identity.DeviceAdmitted`, and every affected
   conversation gets a timeline event. Both name the device and show its fingerprint.
2. **Revocable.** A one-tap revoke that removes the device (§E9) and rotates the group.
3. **Rate limited.** Maximum one auto-admitted device per 24h per account; the second concurrent
   request in that window falls back to manual approval. A burst of admissions is the signature of
   a compromise, so make it expensive.
4. **No history backfill.** The new device joins at the current epoch and reads forward only.
   Restoring history is the backup path (§D) and is separately gated.
5. **Window-bound.** The admission proof is valid for 15 minutes from issue and single-use.

### G.3 Storage, and the downgrade attack

The protection level must **not** be a plain server-side boolean. If it were, the server could
silently downgrade a `VerifiedDevices` account to `TrustedSignIn` and then auto-admit its own
device — defeating the whole tier.

- The level is stored as an **assertion signed by the user's identity key**, and every client
  independently enforces the last validly-signed level it has seen. An unsigned or
  wrongly-signed level is rejected, not defaulted.
- **Downgrades** (`VerifiedDevices` → `TrustedSignIn`) require re-authentication, are broadcast to
  every device, and are written to an append-only device audit log surfaced in the UI. A client
  that sees a downgrade it did not participate in must warn loudly.
- **Upgrades** need no ceremony; apply immediately.
- Clients cache the level and **fail closed** to the stricter interpretation if they cannot verify
  the current assertion.

```
GET  api/v1/identity/protection-level    → { level, signedAssertion, updatedAt, version }
PUT  api/v1/identity/protection-level    → body { level, signedAssertion, version }
                                           requires re-auth on downgrade; 409 on version conflict
```

### G.4 Mixed levels between participants

Your level governs **your own** devices. It does not and must not control what a peer does with
theirs.

- A conversation surfaces each participant's level.
- A `VerifiedDevices` user gets a warning banner when a `TrustedSignIn` participant admits a new
  device — informational, not blocking. Blocking would make the strict tier unusable for anyone
  whose friends haven't opted in, which is how security settings end up switched off.
- Never silently degrade a strict user's guarantees to match a peer's.

### G.5 Defaults and migration

- New accounts: `TrustedSignIn`.
- Existing accounts at rollout: `TrustedSignIn`, with an in-app prompt explaining both levels.
- The settings UI must state the password-strength limitation of `TrustedSignIn` in plain language,
  and must warn — with an explicit confirmation — that `VerifiedDevices` means **losing every
  device and the recovery code is unrecoverable data loss**.

### G.6 Test obligations

- A forged admission proof (server-generated, no master key) is **rejected** at both levels.
- A valid proof auto-admits under `TrustedSignIn` and does **not** auto-admit under
  `VerifiedDevices`.
- An unsigned or stale protection-level assertion is rejected and the client fails closed to strict.
- A downgrade without re-auth is rejected; a downgrade with re-auth is broadcast.
- The 24h auto-admission rate limit falls back to manual rather than failing the join.
- The admission proof expires at 15 minutes and cannot be replayed.
- Last-resort key packages are refused for a `VerifiedDevices` account.

---

## H. Account identity key, and the full-loss recovery journey

### H.1 Why this exists

§G's admission proof is verified against the **account master key**, which only the account
owner's own devices hold. That works when you still have a device. It does not work for the
journey a cloud backup exists to serve:

> "I dropped my only phone in a lake. I bought a new one. Get me back into my conversations."

With no device of yours online, nobody can verify your proof. A peer could approve you, but a peer
can only take the *server's* word that the device is yours — which is precisely the trust we are
refusing to grant. **As specified in §B–§G alone, full-loss recovery deadlocks.**

### H.2 The account identity key

Every account gets a long-lived **Ed25519 account identity key** at signup.

- **Private half**: wrapped under the recovery key (§C.1) and included in the backup envelope.
  Never leaves the client unwrapped. The server sees ciphertext only.
- **Public half**: published at `GET api/v1/users/{userId}/identity-key`, and **TOFU-pinned by
  peers** on first contact, exactly like a Signal safety number.

Every device carries a **device certificate** issued by that key:

```
cert = sign(accountIdentityPrivateKey,
            "venta.device-cert.v1" || deviceId || deviceSignatureKey || issuedAt || expiresAt)
```

Uploaded alongside the device's key packages and served with them. Certificates expire (suggest
180 days) and are reissued by any device holding the account identity key.

**What this buys:** any peer can verify offline that a device genuinely belongs to an account,
without that account having a device online, and without trusting the server. The server cannot
mint a certificate — it never holds the private half.

### H.3 Recovery journeys

| Journey | Mechanism |
|---|---|
| **Reinstall / same device** | Restore blob, import `engine` too. Full history, no re-join. |
| **New device, old one still works** | §G admission proof, verified by the old device. Skip `engine`. |
| **New device, all old devices gone** | **This section.** Restore the account identity key from backup, self-issue a device certificate, **external-commit** into each group, peers validate the certificate. Skip `engine`. |
| **Lost devices *and* recovery credential** | Unrecoverable by design. Say so in the UI before the user opts into `VerifiedDevices`. |

The third row uses `rejoinGroup` — external commit — which is **already implemented and
Rust-tested on both clients with zero call sites**, and the server already stores per-generation
`MlsGroupInfo`, refreshed on every commit. The primitive exists; it needs wiring, not building.

### H.4 External commits are not a free pass

Anyone holding `GroupInfo` can external-commit into a group — **including the server**, which
stores it. So an external commit must never be self-authorising:

1. On observing an external commit from an unrecognised leaf, every member's client fetches the
   joiner's device certificate and validates it against the **pinned** account identity key.
2. **Validation fails, certificate is missing, or the account identity key does not match the
   pinned one → the client immediately proposes removing that leaf** and surfaces a security
   warning naming the account.
3. Under `VerifiedDevices`, a valid certificate is still not sufficient for a *new* account
   identity key — a rotated identity key requires explicit re-verification, the same way Signal
   requires re-verifying a changed safety number.

This is what keeps the recovery path from becoming a server-side backdoor into every group.

### H.5 Identity-key rotation

Rotation is a security event, not a routine one — it invalidates every peer's pinning.

- Requires the recovery credential (not just a logged-in session).
- Signed by the **outgoing** key where possible, so peers can verify continuity automatically.
- Where it cannot be (the key was lost), peers see a **safety-number-changed** warning and must
  re-verify out of band. Do not auto-accept.
- Broadcast to every device and written to the append-only audit log (§G.3).

### H.6 Backup cadence and UX

A backup that exists only when the user remembers to press a button will not be there when the
lake happens.

- **Automatic and periodic** by default: on first setup, then whenever the engine state changes
  materially, debounced to at most once per hour, on unmetered connections.
- **Setup is part of enabling encryption**, not buried in settings. The user picks a passphrase or
  is given a generated recovery code, and must confirm it once.
- Surface **last backup time** in settings, and warn when it is stale (>7 days).
- `mls_state.json` grows monotonically (plan items A-H7 / M-B7 make this worse — fix those first,
  or the 16 MiB cap will be hit by dead key packages). Prune consumed key-package private keys
  before serialising.
- The **message cache is opt-in** for the cloud target and clearly labelled as plaintext message
  history sealed under one credential. It is the single most sensitive thing in the envelope and
  also the thing users most want restored — so make the tradeoff explicit rather than deciding for
  them.

### H.7 Interaction with protection levels

| | `TrustedSignIn` | `VerifiedDevices` |
|---|---|---|
| Recovery credential | Account password (server holds the wrapped envelope) | **Recovery code only** — server-assisted password reset cannot restore E2EE |
| Cloud backup of `engine` | On by default | Off by default; requires the recovery code |
| Message cache in cloud | Opt-in | Opt-in, with a second confirmation |
| External-commit rejoin | Certificate validated automatically | Certificate validated **and** a peer re-verifies the safety number |

### H.8 Test obligations

- Full-loss recovery end to end: wipe every device, restore from blob alone, rejoin by external
  commit, and assert messages sent *after* the rejoin decrypt while messages sent *before* it do
  not (that is correct MLS behaviour and must not be papered over).
- A server-minted device certificate (no account identity private key) is **rejected**, and the
  offending leaf is proposed for removal.
- An external commit from a leaf with **no** certificate is removed.
- A rotated account identity key **not** signed by the outgoing key raises a safety-number warning
  rather than auto-accepting.
- Restoring onto a device whose `userId` differs is refused.
- A stale backup (recovery-key version older than the current envelope) is reported, not silently
  restored.
- Backup blob stays under the size cap with a realistic key-package population.

---

## I. Rollout and backward compatibility — **read before implementing §B, §E, §G or §H**

There are clients in the field. Several rules in this document are breaking changes, and at least
one of them, applied naively, is catastrophic. Nothing in §B/§E/§G/§H may ship without the
corresponding gate below.

### I.1 The dangerous one: §H.4 leaf removal

§H.4 says a leaf whose device certificate is missing or invalid is proposed for removal. **No
device in the field has a certificate.** A client that shipped this rule as written would begin
proposing the removal of every other device in every group it is in, including its owner's.

Certificate enforcement is therefore **three-state**, and the state is driven by a server-supplied
policy, not by client version:

| Phase | Behaviour on a leaf with no certificate | Behaviour on an *invalid* certificate |
|---|---|---|
| **Observe** (initial) | Allow. Count it. No UI. | Warn in logs; allow. |
| **Warn** | Allow, show an unverified-device indicator. | Security warning, allow, offer manual removal. |
| **Enforce** | Propose removal. | Propose removal. |

- The phase is served by `GET api/v1/identity/mls-policy` → `{ certificateEnforcement, minClientVersion, ... }`,
  cached with a short TTL, and **defaults to `Observe`** when unreachable or unparsable.
- An *invalid* certificate is always at least a warning, at every phase — that case cannot occur
  by accident, only by forgery.
- Advance to `Enforce` only when telemetry shows certificate coverage above ~99% of active devices.
  Put the actual coverage number behind an admin endpoint so the decision is made on data.
- **Never** let a client infer the phase from its own version. A single early client must not be
  able to start removing leaves.

### I.2 Existing accounts have no identity key

The account identity key (§H.2) is generated client-side and needs the master key, so it can only
appear when an upgraded client next unlocks.

- On unlock, an upgraded client checks for an identity key; if absent, generates one, wraps it,
  uploads the public half, and issues certificates for **its own** device.
- It cannot issue certificates for the user's *other* devices — those devices self-issue when they
  next upgrade and unlock. Expect a long tail; this is why §I.1 exists.
- Until an account has an identity key, it is treated as `Observe` regardless of global policy, and
  `VerifiedDevices` cannot be enabled (the UI must explain why rather than failing opaquely).

### I.3 Endpoint compatibility rules

| Change | Naive version breaks | Required approach |
|---|---|---|
| **§E5** welcome fetch requires `deviceId` | Old clients call without it and get nothing — or worse, keep consuming across devices | **Split the fix from the break.** Immediately make the legacy branch **non-consuming** (this alone removes the data loss, which is the actual bug). Keep serving it. Require `deviceId` only after `minClientVersion` is met. |
| **§E6** ack scoped by `(UserId, DeviceId)` | Old clients ack without `deviceId` and silently no-op, so welcomes are never cleared | When `deviceId` is absent, ack only welcomes whose `DeviceId` matches the caller's validated `X-Device-Id`; if that is also absent, reject with a clear error rather than acking broadly. |
| **§E7** per-device reachability rejects creation | Old clients cannot pass the new override flag and lose the ability to create encrypted conversations | Default to **permissive + telemetry** during transition: create, return `unreachableDevices`, and count it. Flip to rejecting once clients that understand the flag are the overwhelming majority. |
| **§A** `POST api/v1/devices` purges key packages on identity rotation | An old client that re-registers with an unchanged key must not be purged | Purge **only** when `identityPublicKey` actually differs from the stored value. Unchanged key → unchanged behaviour. |
| **§B** conversation join requests | Old clients never submit one, so their new devices stay stranded | Acceptable — that is today's behaviour, not a regression. Do not attempt a server-side shim. |
| **§G** protection level | Old clients cannot verify the signed assertion | They ignore it and behave as `TrustedSignIn`, which is the default anyway. **Do not** let an old client's ignorance downgrade a `VerifiedDevices` account: enforcement lives on the upgraded clients, and the account cannot enter `VerifiedDevices` until every active device reports support. |

### I.4 Client capability reporting

Add a capability set to device registration and refresh it on every launch:

```
POST api/v1/devices  { ..., capabilities: ["mls.device-cert.v1", "mls.join-request.conversation.v1",
                                           "mls.protection-level.v1", "mls.backup.v1"] }
```

The server uses this — not a version string — to decide whether an account may enable
`VerifiedDevices`, and to compute the coverage telemetry that gates §I.1. `GET api/v1/devices`
returns each device's capabilities so clients can explain *which* device is holding an upgrade back.

### I.5 Wire compatibility for the message-content fix (§E1)

The mobile base64 fix changes what mobile *reads*, not what anyone *writes*, and the send path is
already consistent across clients. So there is no dual-format window and no migration — an
upgraded mobile client immediately reads messages that older ones could not, including historical
ones still within ratchet reach. **Do not add a heuristic that sniffs whether content is
single- or double-encoded**; the encoding is deterministic per transport (REST/socket double,
push single) and a sniffer would silently misparse ciphertext that happens to look like base64.

### I.6 Database migrations

Five unapplied MLS migrations already exist; this work adds more. Apply Identity before Messaging.
Every new migration must be **additive and nullable** — new columns on `UserDeviceBackup`,
`identity_public_key`, `device_certificate`, `protection_level`, `capabilities` — so a rollback to
the previous application version leaves a working database. No column drops in this release.

Before deploying, run the check already flagged in the plan:

```sql
SELECT count(*) FROM conversations WHERE encryption_state = 'encrypted' AND mls_group_id IS NULL;
```

### I.7 Ordered deployment

1. **Server** with everything defaulted to compatible: `certificateEnforcement = Observe`,
   permissive reachability, non-consuming legacy welcome fetch, all new endpoints live but unused.
   Old clients are unaffected — verify that explicitly.
2. **Clients** ship the base64 fix, failure surfacing, key-package reset, atomic persistence, and
   identity-key + certificate *generation*. No enforcement yet.
3. **Soak.** Watch certificate coverage, undecryptable-message rate, and welcome-join failure rate.
4. **Clients** ship conversation join requests, protection levels, and backup.
5. **Flip** `certificateEnforcement` to `Warn`, then `Enforce`, on coverage data.
6. **Then** tighten `deviceId` requirements and reachability rejection.

Steps 1–2 alone fix the reported bug. Nothing after step 3 should be rushed to catch a release.

### I.8 Test obligations

- **An unmodified old client keeps working against the new server** for: sending and reading
  encrypted messages, fetching and acking welcomes, creating an encrypted conversation, and
  replenishing key packages. This is the single most important test in this document and it needs
  to run in CI against the real old client build, not a mock of it.
- A leaf with no certificate is **not** removed under `Observe` or `Warn`, and **is** under `Enforce`.
- An unreachable or malformed `mls-policy` response yields `Observe`, not `Enforce`.
- An account with any device lacking `mls.protection-level.v1` cannot enter `VerifiedDevices`.
- The legacy welcome fetch no longer consumes, and a subsequent device-scoped fetch still returns
  the welcome.
- Re-registering with an **unchanged** identity key does not purge key packages.
- Rolling the application back one version against the migrated database still starts and serves
  traffic.

---

## J. Backend implementation notes — shapes that differ from §A–§I

Echo's backend is implemented. Everything in §A–§I is honoured unless listed here. **These are the
concrete shapes the clients must code against** where the sections above were underspecified, or
where implementing them needed a field the spec did not name.

### J.1 Additive fields on existing shapes

| Shape | Added | Why |
|---|---|---|
| `PUT api/v1/backup/recovery-key` body | `password` | §C.1 requires re-authentication; the password is the only thing the server can actually verify. |
| `PUT api/v1/backup/recovery-key` body | `recoveryCodeWrapping: { kdf, iterations, memoryKiB, parallelism, salt, iv, cipherText, publicVerifier? }` | §C.1.1's second wrapping of the **same** master key. Optional on the wire so clients can roll out in two steps, but an account without it is one password reset away from losing every backup blob and its account identity key. Required to enter `VerifiedDevices` and to put engine state in the cloud. Both wrappings share the top-level `version` — they wrap the same bytes. |
| `PUT api/v1/devices/client/{id}/backup` | request header `X-Backup-Includes-Engine: true` | The blob is opaque, so the server cannot tell whether it carries engine state. §H.7 gates engine state in the cloud for `VerifiedDevices`; the client has to declare it. |
| `GET .../backup` | response header `X-Backup-Stale: true` | §H.8's "a stale backup is reported, not silently restored". Set when the blob's recovery-key version is behind the account's current envelope. The blob is still served — it may be the only copy. |
| `GET .../backup/meta`, `GET api/v1/devices/backups` | `isStale` | The same signal in the metadata shape. |
| `GET api/v1/backup/recovery-key` | `recoveryCodeWrapping`, `passwordWrappingInvalidatedAt`, `encryptedHistoryRecoverable` | §C.1.1. `encryptedHistoryRecoverable: false` is a **completed loss** — a reset invalidated the password wrapping and there was no recovery-code wrapping. Surface it as such, not as a warning about the future. |
| `POST api/v1/user/reset-password` response | `masterKeyRewrapRequired`, `encryptedHistoryRecoverable` | Was a bare `Ok()`. §C.1.1: the reset just made the password wrapping undecryptable, and the client has to be told to re-wrap from the recovery code on next unlock — or told that it is already too late. |
| `POST api/v1/users/master` response | `version`, `hasRecoveryCodeWrapping`, `encryptedHistoryRecoverable` | Was a bare `Ok()`. This legacy route writes the **password wrapping only**, so a client that uses it exclusively has an account one reset can destroy and no other way to discover that. |
| `PUT api/v1/identity/protection-level` body | `password`, `deviceId` | Password on downgrade only (§G.3). `deviceId` lands in the audit row and the broadcast, so a device can tell "I did this" from "something did this". |
| `POST .../mls/join-requests` body | `deviceName` | Display only, so the approval prompt can say "Alice's new phone" rather than an opaque id. Nothing is authorized on it. |
| `conversation.MlsJoinRequest` push | `signatureKeyFingerprint`, `requiresManualApproval` | The fingerprint is what a human compares out of band. The flag is the server's published verdict — see J.4. |
| `MlsJoinRequestDto` | `requiresManualApproval` | The same verdict on the review-queue read. |
| `POST .../mls/commits` body | `isProposal` | §E2 needs a wire flag: the server has to be told a payload is a proposal in order not to advance the epoch for it. |
| `MlsCommitResponseDto` | `isProposal` | So a client can honour "do not count a proposal toward `applied`" without guessing. |
| `MlsCommitPublishedDto` | `isProposal`, `duplicate` | `duplicate: true` is the §E4 idempotent replay. The publish succeeded — **keep** the merged state; do not treat it as a lost race. |
| `AckWelcomesDto` | `deviceId` | §E6. Falls back to `X-Device-Id`; with neither, 400 rather than a silent no-op. |
| `ConversationDto` (creation response only) | `unreachableDevices: [{ userId, deviceId, deviceName }]` | §E7. Added to the existing shape rather than wrapped in a new envelope, so clients already reading a `ConversationDto` off this response keep working. |
| `POST api/v1/devices` response | `identityRotated` | §A. Otherwise the same device fields at the top level as before. |
| `DeviceTokenResponse` (`/consume-tokens`) | `certificate`, `certificateExpiresAt`, `certificateIdentityKeyVersion`, `isLastResort` | §H.2 — the certificate travels *with* the key package, so the server cannot pair one device's package with another's certificate. `isLastResort` warns that the joining leaf has no forward secrecy from that point back. |

### J.2 Routes the spec named only in prose

The admission-proof relay (§G.1 named the challenge POST and "a proof-submission route"):

```
POST   /api/v1/conversations/{id}/mls/join-requests/{requestId}/challenge
         body { challenge: <32 bytes b64> }
         → { challengeId, requestId, challenge, issuedByDeviceId, expiresAt, answered }
GET    /api/v1/conversations/{id}/mls/join-requests/{requestId}/challenge
         requester only — the outstanding nonce
POST   /api/v1/conversations/{id}/mls/join-requests/{requestId}/proof
         body { challengeId, proof: b64 }    requester only, single use
GET    /api/v1/conversations/{id}/mls/join-requests/{requestId}/proof
         → { requestId, challengeId, challenge, proof, requesterDeviceId,
             signatureKeyFingerprint, expiresAt }
```

The verifier receives the nonce and the signed-over values alongside the signature, so it never has
to trust the server's account of *what* was signed. The server stores the proof verbatim, never
validates it, and enforces only single-use and the 15-minute window.

Account identity key (§H.2 named only the GET):

```
GET api/v1/users/{userId}/identity-key
      → { userId, publicKey, version, rotationSignature, updatedAt }
      → 404 when the account has not published one
PUT api/v1/users/identity-key
      body { publicKey, version, rotationSignature?, password?, deviceId? }
      first publication needs no password; rotation does
PUT api/v1/devices/client/{deviceId}/certificate
      body { certificate, issuedAt, expiresAt, identityKeyVersion }
```

Conversation MLS re-key, which had no route at all:

```
POST /api/v1/conversations/{id}/mls/enable    body EnableMlsDto
POST /api/v1/conversations/{id}/mls/disable
```

`PUT api/v1/backup/recovery-key` is **three operations distinguished by `version`**, which §C.1
did not spell out:

| `version` vs stored | Operation | Orphans blobs? |
|---|---|---|
| Lower | Refused | — |
| **Equal** | **Additive**: writes `recoveryCodeWrapping` against the master key already stored. This is the §C.1.2 retrofit path, and also covers regenerating a recovery code. | **Never** — blobs bind to the version and the version does not move. `orphanedBlobDeviceIds` is always empty. |
| Higher | Rotation: replaces the master key. | Yes — returns 409 with the affected device ids unless `?acknowledgeOrphans=true`. |

At the same version the **password wrapping is not rewritten**, and submitting a `cipherText` that
differs from the stored one is a 400. Different bytes under an unchanged version is either a re-wrap
under a new password — which `rewrap-password` exists for — or a different master key masquerading as
the same one, which would make every blob at that version unopenable while claiming nothing changed.
The server cannot distinguish them, so it refuses rather than guessing.

Send the stored `cipherText` unchanged (read it back from `GET api/v1/backup/recovery-key`) when
adding a recovery code to an existing account.

Master-key re-wrapping after a password reset (§C.1.1 named the obligation, not the route):

```
POST api/v1/backup/recovery-key/rewrap-password
      body { version, passwordWrapping: { kdf, iterations, memoryKiB, parallelism,
                                          salt, iv, cipherText, publicVerifier? } }
      → 200 { version, encryptedHistoryRecoverable }
      → 409 when version != the stored version
```

The client reaches this by unlocking from the recovery code — the only credential a reset leaves
intact — and re-sealing the **same** master key under the new password. `version` is unchanged, so
every backup blob stays readable; this is a re-wrap, not a rotation. There is deliberately no
password check: producing a valid wrapping of the master key *is* the proof, and demanding the
password would gate the recovery path on the thing that was just reset.

### J.3 Realtime events the server emits

```
conversation.MlsCommit             also emitted for channel contexts (§E8) — switch on contextId
conversation.MlsJoinRequest        conversation join request submitted (§B)
conversation.MlsDeviceRemoved      a removed device's leaf must be committed out (§E9)
conversation.MlsDeviceAdmitted     a device joined the group (§G.2 timeline event)
identity.DeviceAdmitted            to the owner's devices, auto-admissions only (§G.2)
identity.DeviceRegistered          carries identityRotated
identity.BackupRead                (§C.2 rule 2)
identity.ProtectionLevelChanged    carries isDowngrade (§G.3)
identity.AccountIdentityKeyRotated carries signedByOutgoingKey (§H.5)
```

### J.4 Two rules the server publishes but cannot enforce

Stated plainly, because a client that assumes otherwise is building on sand.

1. **The 24h auto-admission limit (§G.2.3).** The server holds no group keys; only a member's client
   can produce an Add commit, and only that client can decline to. So the server decides the budget,
   publishes the verdict as `requiresManualApproval` on the join request, and records how the budget
   was spent. Clients must honour it. The budget counts *devices*, not requests — one handset joining
   five conversations is one admission — and the requesting device is excluded from its own count.
2. **`ProtectionLevel` as served by the server.** The enum column is a cache, so the server can answer
   "may this be auto-admitted" without asking a client. The authority is the signed assertion.
   Clients verify that and fail closed to `VerifiedDevices` when they cannot; they must not treat the
   server's enum as authoritative.

### J.5 What the server deliberately does not do

- **It never validates an admission proof, or a device certificate's signature.** It holds neither the
  account master key nor the account identity private key. `POST api/v1/devices` and
  `PUT .../certificate` check certificate *structure and expiry only*, and reject malformed uploads —
  but a certificate the server accepted is not a certificate anyone should trust.
- **It does not choose the admission nonce.** A server-chosen nonce could be one it had precomputed a
  signature against.
- **It does not delete MLS commits or generations by author on account purge.** A commit is a link in
  the group's history; deleting one forks every remaining member. Purge removes the departing user's
  Welcomes and join requests (which carry the key package and fingerprint), and removes commits and
  generations only for conversations the purge left with no members at all.

---

## K. venta-mobile implementation notes — mechanisms the spec named but did not fix

**Read this before implementing §G or §H on Alpine.** Where §G/§H named a
mechanism in prose but not a concrete construction, mobile had to choose one, and
the two clients must agree byte for byte or every proof and certificate is
mutually unverifiable. Everything here is *additive* — nothing already agreed in
§A–§J changed.

Implemented in `packages/venta_mls/rust/src/mls.rs`; Alpine mirrors into
`src-tauri/src/crypto/mls.rs`.

### K.1 Canonical signing payloads

Every signed or MAC'd payload is built by one helper:

```
tagged_payload(label, fields) = label || len(f0) || f0 || len(f1) || f1 || ...
```

with each length a **4-byte big-endian** `u32`. The length prefixes are not
decoration: plain concatenation is ambiguous — `("ab","c")` and `("a","bc")`
produce identical bytes — so an attacker who can move a byte from one field into
the next can make one signature vouch for two different statements.

| Purpose | Label | Fields, in order |
|---|---|---|
| Device certificate (§H.2) | `venta.device-cert.v1` | `deviceId`, `deviceSignatureKey` (b64 **string**, not raw bytes), `issuedAt` (i64 BE), `expiresAt` (i64 BE) |
| Admission proof (§G.1) | `venta.device-admission.v1` | `challenge` (raw bytes), `requesterDeviceId`, `signatureKeyFingerprint` |
| Protection-level assertion (§G.3) | `venta.protection-level.v1` | `userId`, `level` (the enum's wire name), `version` (u64 BE), `updatedAt` (the exact string sent) |

Timestamps are **whole seconds since the epoch**, and the certificate signs the
signature key as the base64 *string* it travels as, so neither side has to agree
on a decoding before it can verify.

### K.2 Primitives

- **Device certificate** and **protection-level assertion**: Ed25519 over the
  account identity key, via `OpenMlsCrypto::sign` / `verify_signature`. No new
  crate — the same primitive the rest of the engine uses.
- **Admission proof**: **HMAC-SHA256**, keyed by
  `HKDF-SHA256(ikm = accountMasterKey, salt = none, info = "venta.device-admission.v1", L = 32)`.

  Symmetric rather than a signature because both parties are devices of the *same*
  account and both hold the master key, which is what §G.1 step 4 ("verifies the
  proof locally — it holds the master key too") already implies. A derived key
  rather than the master key itself so a leaked proof cannot be turned back into
  the key that unwraps the backup envelope. Verified with `verify_slice`, which is
  constant time; comparing the base64 strings would not be.

§G.3 said the protection level is "signed by the user's identity key" without
naming one — this system had no per-user key at the time. §H.2 introduced exactly
that, so the assertion is signed by the **account identity key**, which also gives
its rotation the ceremony §H.5 already defines.

### K.3 Backup envelope additions (§D)

One optional field, absent in blobs written before §H and absent for accounts
that have no identity key yet (§I.2 says that is every existing account):

```jsonc
"accountIdentity": { "pub": "b64", "priv": "b64" }
```

Without it a restored handset can issue no device certificate, so under §H.4
every peer would propose removing its leaf — which makes it the field the
full-loss recovery journey actually turns on.

`MlsBackupImportResult` also returns `signingPublicKey` / `signingPrivateKey`.
Mobile keeps the MLS identity in the OS keychain rather than in the engine's
store, and `unlock()` reads it from there on every cold start, so a restore that
only loaded it into memory would work until the app was next killed and then look
exactly like lost keys. Alpine may ignore both fields.

### K.4 Two engine-shape divergences Alpine should be aware of

1. **`read_only` is a field on `MlsState`, not an absent `state_path`.** Alpine's
   `save_to_disk` errors when `state_path` is `None`, which is right — but mobile
   has a legitimate no-save mode for the iOS notification-service extension, a
   *separate process* that must never write a stale copy over what the app
   committed. "Deliberately not saving" and "never initialised" need opposite
   treatment and used to be the same branch. Mobile's `save_to_disk` returns
   `Ok(())` when `read_only`, and errors on a missing path otherwise.
2. **`current_state_dir()`** is mobile-only. On Android the FCM background isolate
   shares a process with the app, so `init_storage` for another account would
   clear the foreground session's groups and signers. The push path asks which
   directory the engine is on and declines rather than tearing it down. Alpine's
   desktop process has no equivalent hazard.

### K.5 Admission-proof routes

Mobile's `MlsApi` already matches §J.2 exactly — `POST`/`GET` challenge,
`POST`/`GET` proof, per context. No deviation; noted so Alpine can copy the
call shapes rather than re-deriving them.

### K.6 What mobile has *not* wired

Same reasoning Alpine applied, and the same conclusion.

- `LeafVerificationService` implements §H.4's three-state check and is tested, but
  **nothing calls it from the commit path**. §I.1 defaults to `Observe`, so
  enforcement would allow everything anyway, and wiring it before certificate
  coverage exists is how a client starts removing its owner's other devices.
- No launch sequence establishes the account identity key or issues this device's
  certificate, so §H.2 coverage is currently zero from mobile.
- No settings UI for the protection level (§G.5), the backup passphrase, or the
  last-backup time (§H.6). `backUpIfStale` implements the debounce but has no
  caller.
- `requestAccessWhereMissing` implements §B's discovery sweep but is called
  per-context by the access banner rather than at launch across the conversation
  list.

### K.7 Dual-wrapped master key — mobile's construction (§C.1.1)

> **The recovery-code format in this section is superseded by §C.1.2 and §K.8.**
> The 32-symbol alphabet and the masking described below are exactly what broke
> cross-client recovery. The wrapping and Argon2 details still hold.

> **The recovery-code format below is superseded by §C.1.2 and §K.8.** The
> 32-symbol alphabet and masking described here are what broke cross-client
> recovery. Everything else in this section still holds.

Implemented in `packages/venta_mls/rust/src/mls.rs`. Alpine must match the
recovery-code alphabet and normalisation exactly, or a code generated on one
client will not open the wrapping on the other.

**Recovery code.** 32 characters over a 32-symbol alphabet, rendered in eight
groups of four (`XXXX-XXXX-…`), 160 bits:

```
23456789ABCDEFGHJKMNPQRSTUVWXYZ*
```

No `I`, `L`, `O`, `0` or `1` — those are the pairs people transcribe wrongly, and
a code that fails because of a misread character fails at the one moment its owner
has nothing else to try. Exactly 32 symbols so each character is 5 bits and
masking (`byte & 0x1f`) is uniform; a `%` over a non-power-of-two alphabet would
bias it.

**Normalisation, applied before the code is ever used as KDF input:** strip all
whitespace and `-`, then upper-case. Case and grouping are presentation, not
secret material. Both `setup_master_key` and every unwrap normalise, so the code
as *displayed* and the code as *typed* derive the same key.

**Wrapping.** Both wrappings use the master-key Argon2 parameters — m=65536, t=3,
**p=1** — each with its own 16-byte salt and 12-byte IV, over AES-256-GCM. They
seal identical bytes and share the top-level `version`.

Rust surface, all additive:

```
setup_master_key(password, recovery_code: Option<&str>) -> MasterKeySetup
    { passwordWrapping, recoveryCodeWrapping, recoveryCode, masterKey }
generate_recovery_code() -> String
normalize_recovery_code(&str) -> String
wrap_master_key_under(master_key_b64, secret) -> EncryptedMasterKey
```

`recovery_code` is accepted so a UI can display a code and take confirmation
*before* committing to it; one is generated when absent, so the entropy source is
never the caller's.

**Client rule mobile enforces, worth mirroring:** `VerifiedDevices` is refused
while `MasterKeyStatus != ready`. §H.7 names the recovery code as that tier's only
recovery credential — a server-assisted password reset explicitly cannot restore
E2EE — so entering it without a recovery-code wrapping promises a recovery path
that does not exist, and the first password reset would be silent, total loss.
`TrustedSignIn` is still allowed, since its credential is the password.

> **Do not align the two Argon2 parallelism values** (master key `p=1`, backup
> envelope `p=4`). They are safe only because both formats are self-describing and
> both readers derive from the declared header rather than from the write-side
> constants. Aligning them orphans every key and blob already written under the
> other value. Mobile pins this with `declared_kdf_parameters_are_the_ones_actually_used`
> and `declared_master_key_parameters_are_the_ones_actually_used`, which perturb
> each of `m`/`t`/`p` and assert the read then fails — both were verified to fail
> against a deliberately hardcoded reader.

### K.8 §C.1.2 applied on venta-mobile — supersedes K.7's recovery-code section

K.7's recovery-code paragraph is **withdrawn**; §C.1.2 is authoritative. What
changed on this side, and what Alpine can rely on:

| | Now |
|---|---|
| Alphabet | `23456789ABCDEFGHJKMNPQRSTUVWXYZ` — 31 symbols. `*` **removed**. |
| Generation | Rejection sampling: draw a byte, discard if `>= 248`, else `alphabet[b % 31]` |
| Length | 32 characters, eight groups of four, `-` joined (~158.5 bits) |
| Normalisation | Strip whitespace and `-`, uppercase, validate length and alphabet |
| Invalid input | `Err("RecoveryCodeInvalid: …")`, surfaced to Dart as `MlsErrorKind.recoveryCodeInvalid`. **Never** falls back to raw input. |
| Passwords | Structurally cannot reach normalisation — `unlock` takes `password` and `recoveryCode` as separate parameters and only the latter is normalised. No detect-or-passthrough branch exists to get wrong. |

`normalize_recovery_code` returns `Result<String, String>` rather than `String`.
That is a signature change on a shared function; Alpine's equivalent should make
the same move rather than keeping the `else { input.to_string() }` branch.

**Cross-client fixture.** `testdata/mls-golden/v1/recovery-code.json` is checked
in, produced by this engine:

```jsonc
{ "producedBy": "venta-mobile",
  "alphabet": "23456789ABCDEFGHJKMNPQRSTUVWXYZ",
  "recoveryCode": "PVK8-XHXZ-…",      // as displayed
  "normalized":   "PVK8XHXZ…",        // what the KDF consumes
  "masterKey":    "<b64, 32 bytes>",  // what the wrapping must yield
  "recoveryCodeWrapping": { …EncryptedMasterKey… } }
```

Regenerate with
`cargo test --manifest-path packages/venta_mls/rust/Cargo.toml -- --ignored generate_recovery_code_fixture`.

Alpine's reciprocal file is expected at `recovery-code-alpine.json` in the same
directory, same shape. `this_engine_opens_the_desktop_clients_recovery_code_fixture`
already consumes it and prints a loud `SKIPPED` until it lands, so the moment
Alpine checks the file in the assertion goes live with no change here.

**Retrofit write.** `addRecoveryCode` writes at the **same version** and sends the
stored `passwordWrapping` back **byte-identical to what `GET` returned** — never
re-encrypted, since a fresh IV would change the ciphertext and now earn a 400. It
then **re-reads and verifies** the wrapping actually landed, throwing
`RecoveryCodeNotStoredException` rather than showing a code the server did not
store. Pinned by `sends the stored password wrapping back byte-identical` and
`refuses to show a code the server did not store`.
