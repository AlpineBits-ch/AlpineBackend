# MLS Hardening & Backup Plan

Cross-repo audit and remediation plan covering:

- **Echo** — `C:\Users\Domin\RiderProjects\Echo` (.NET backend)
- **Alpine** — `C:\Users\Domin\WebstormProjects\Alpine` (Angular + Tauri desktop)
- **venta-mobile** — `C:\Users\Domin\venta-mobile\venta_mobile` (Flutter + Rust FFI)

Audit date: 2026-08-01. Status: findings verified, fixes not yet applied.

---

## 0. Executive summary

The reported symptom — *"my friend texts me on his desktop and I can't decrypt on my mobile"* —
is **not one bug and not a crypto bug**. Both Rust engines are byte-identical (`openmls 0.8.1`,
same lockfile hashes, same ciphersuite `MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519`, same
credential type, same TLS codec, same base64 variant). A ciphersuite/version mismatch is
**ruled out** — do not chase it.

Instead there are **three independent breakers stacked on the same path**, each of which alone
is sufficient to produce the symptom:

| # | Breaker | Repo | Confidence |
|---|---------|------|-----------|
| **R1** | Mobile never strips the extra base64 layer the server adds to `Message.Content` | venta-mobile | **Verified** |
| **R2** | No mechanism exists to admit a *newly registered device* into an *existing* conversation | Echo | **Verified** |
| **R3** | A client-side state wipe leaves the server handing out key packages whose private halves are gone | Echo + both clients | **Verified** |

Every one of them fails **silently**. That is the fourth, meta-bug.

---

## 1. Root cause R1 — the double-base64 (verified)

### The chain

`CreateMessageDto.Content` is a `string`; `Message.Content` is a `byte[]`:

```csharp
// Messaging.Application/Endpoints/MessagingEndpoints.cs:202  (and :373 for channels)
Content = Encoding.UTF8.GetBytes(dto.Content),
```

`MessageDto` is a Facet copy of a `byte[]`, so `System.Text.Json` base64s it on the way out.
The read-side wire value is therefore:

```
content == base64( utf8bytes( <whatever the client POSTed as content> ) )
```

For an encrypted message the client POSTs a base64 MLS `PrivateMessage`, so the reader receives
**base64 of base64**. Confirmed by the existing E2E test, which reads plaintext back with
`.GetBytesFromBase64()` (`Echo.E2E.Tests/Scenarios/ThreadFlowTests.cs:105-110`).

### Who strips it

| Path | Call | Correct? |
|---|---|---|
| Alpine REST | `processMessage(groupId, fromBase64(msg.content))` — `message.store.ts:116` | ✅ |
| Alpine socket | `processMessage(groupId, fromBase64(data.content))` — `messaging-websocket.service.ts:308` | ✅ |
| **Mobile REST + socket** | `processMessage(groupIdB64: groupId, messageB64: message.content)` — `message_decryptor.dart:55-58` | ❌ **no strip** |
| Mobile push | `messageB64: ciphertext` — `message_push_decryptor.dart:74` | ✅ (see below) |

The engine base64-decodes once (`packages/venta_mls/rust/src/mls.rs:817`), gets ASCII bytes,
and `MlsMessageIn::tls_deserialize_exact_bytes` fails. The failure is swallowed by a bare
`catch (_)` at `message_decryptor.dart:81-84` that logs nothing.

### Why it looks intermittent rather than total

The push path is deliberately single-encoded server-side:

```csharp
// Messaging.Application/Services/MessagePushService.cs:142-143
var encoded = Encoding.UTF8.GetString(payload.Content);
ciphertext = encoded.Length is > 0 and <= MaxCiphertextChars ? encoded : null;
```

So when a push lands and decrypts, the plaintext is written to the local message cache and the
conversation renders it. When the push is dropped, throttled by an OEM battery manager, truncated
(`>3000` chars → `truncated: 1`), or beaten by the socket because the app is foregrounded, the
message is permanently unreadable. **Hence "sometimes", not "never".**

### The fix

Decode once in `message_decryptor.dart:55-58`, mirroring Alpine's `fromBase64`, guarded so a
malformed value falls through to `isUndecryptable`. **Must not** be applied to
`MessagePushPayload.ciphertext`, which is already correct.

> ⚠️ `test/mls_decrypt_test.dart:98` currently asserts `messageB64: 'ciphertext'` verbatim —
> the test suite **actively protects the bug**. It must be updated in the same change.

---

## 2. Root cause R2 — no device admission path for existing conversations (verified)

A group member is a **device**, not a user: `PendingWelcome.DeviceId`
(`Messaging.Domain/Entities/PendingWelcome.cs:44-46`), `MlsJoinRequest.RequesterDeviceId`,
one token per device from `ConsumeMlsTokensForUserHandler`.

But the server never records *which* devices hold a leaf. `ConversationMemberDevice`
(`Messaging.Domain/Entities/ConversationMember.cs:62-69`) is mapped
(`MicroserviceContext.cs:211-218`) and **written by nothing**.

Trace for "Bob installs the mobile app after the DM already exists":

1. `POST api/v1/devices` → `DeviceRegistered` emitted (`MlsDeviceEndpoint.cs:49-54`).
2. **`DeviceRegistered` has no consumer anywhere in the solution.** Nothing tells any conversation.
3. Mobile uploads 100 key packages. They sit unused.
4. `GET /conversations/welcomes?deviceId=<mobile>` → empty, and always will be.
5. There is no `POST /api/v1/conversations/{id}/mls/join-requests` — join requests are
   **channel-only** (`MlsEndpoints.cs:246-346`).
6. Alice's desktop cannot discover the device either: no endpoint lists another user's devices,
   and the only enumeration (`/consume-tokens`) *consumes* a key package per call, so polling it
   is destructive by construction.
7. Both clients only call `consume-tokens` when a **new user** joins the roster
   (`Alpine/src/app/services/mls-sync.service.ts:323-357`,
   `venta-mobile/lib/core/mls/mls_sync_service.dart:370-413`), and both filter out their own device.

**Result: the device is permanently outside the group, with no server-side or client-side repair
path and no error surfaced to anyone.**

Related: there is no `/conversations/{id}/mls/enable|disable` route at all, so a conversation's
generation is frozen at creation forever — no re-key, no rotation, no recovery.

---

## 3. Root cause R3 — stale key packages after a local wipe (verified)

```csharp
// Identity.Application/Controllers/MlsDeviceController.cs:61-74
var usable = await ctx.UserKeyPackages.CountAsync(p =>
    p.DeviceId == device.Id && p.ConsumedAt == null && !p.IsLastResort && p.ExpiresAt > now);
Count = Math.Clamp(TargetKeyPackageCount - usable, 0, TargetKeyPackageCount),
```

The replenish count is derived **purely from server rows**. The private init keys live only in
the client's local MLS store. Both clients wipe that store on any corruption
(`Alpine/.../main-page.component.ts:273-277`, `venta-mobile/.../mls_service.dart:70-83`) and keep
the same `ClientDeviceId`. Afterwards the server still holds ~100 unconsumed packages → answers
`Count = 0` → the client uploads nothing → **every Welcome sealed to those packages is
undecryptable by the device it was addressed to.**

The same state is reached from a signing-key rotation: re-registration returns the existing row
unchanged and never updates `IdentityPublicKey` (`MlsDeviceEndpoint.cs:30-37`), while
`device-registration-modal.component.ts:52-100` has already minted a fresh Ed25519 keypair.

Aggravating factor on Alpine: `save_to_disk` is a **non-atomic** `std::fs::write`
(`src-tauri/src/crypto/mls.rs:150-157`) executed on every send/receive/commit. One truncated write
→ `main-page.component.ts:272-278` catches the parse error and calls `clearStorage()`, destroying
every private key. Mobile already does tmp+rename correctly.

There is no endpoint to invalidate a device's key-package stock short of deleting the device.

---

## 4. Release blockers (P0)

Ordered by "will bite on day one".

| ID | Repo | Issue | Location |
|---|---|---|---|
| **P0-1** | mobile | Double-base64 on read (R1) | `message_decryptor.dart:55-58` |
| **P0-2** | Echo | No conversation device-admission path (R2) | `MlsEndpoints.cs`, `DeviceRegistered` |
| **P0-3** | Echo | `DELETE .../key-packages` reset endpoint missing (R3) | `MlsDeviceController.cs` |
| **P0-4** | all | Leave **proposal** published on the commit channel poisons the epoch counter | see §4.1 |
| **P0-5** | Echo | Legacy no-`deviceId` welcome fetch consumes welcomes for **all** of a user's devices — unrecoverable loss | `MlsEndpoints.cs:374-386` |
| **P0-6** | Echo | Conversation-creation reachability check is per-**user**, silently dropping a device | `ConversationEndpoints.cs:106-120` |
| **P0-7** | Echo | Channel commits are announced to **nobody** | `MlsEndpoints.cs:196-216` + `MlsGroupService.cs:347` |
| **P0-8** | Echo | Concurrent commits → **500**, not 409; clients only retry on 409 so the change is dropped | `MlsGroupService.cs:345` |
| **P0-9** | both clients | A commit whose publish response is lost strands the context **permanently** | `mls-sync.service.ts:284-292`, `mls_sync_service.dart:330-351` |
| **P0-10** | mobile | Account switch **merges two accounts' MLS state** (A's private keys land in B's file) | `mls_service.dart:62-84` + `mls.rs:1091-1120` |
| **P0-11** | Alpine | `mls_state.json` is **plaintext on disk** — every private key, in the clear | `src-tauri/src/crypto/mls.rs:136-157` |
| **P0-12** | Alpine | Non-atomic state write → truncation → total key loss | `mls.rs:150-157` |
| **P0-13** | Alpine | `save_to_disk` silently **returns `Ok(())` when `state_path` is `None`** | `mls.rs:150-153` |
| **P0-14** | Alpine | **Plaintext fallback**: channel composer sends cleartext when the local generation is unknown | `channel.component.ts:571-580` |
| **P0-15** | all | Every decrypt/join failure is silently swallowed | §4.2 |

### 4.1 The leave-proposal epoch poison (P0-4)

Both clients publish a Remove **proposal** through the *commit* channel at `serverEpoch + 1`
(`Alpine/.../mls-sync.service.ts:396-404`, `venta-mobile/.../mls_sync_service.dart:463-477`).
The server accepts it and advances `active.Epoch = dto.Epoch` (`MlsGroupService.cs:316`) — but
**processing a proposal does not advance any client's MLS epoch.**

Consequences:

- **Mobile hangs.** `_syncContextInner` (`mls_sync_service.dart:187-222`) loops: local epoch stays
  `N`, `getCommits(sinceEpoch=N)` keeps returning the proposal at `N+1`, `applied` is always ≥1,
  so `while(true)` never terminates. It holds the per-context queue, so the pending-proposal drain
  that would break the cycle can never run. It also issues an unbounded stream of HTTP requests.
- **Alpine loops or throws**, and every subsequent commit is rejected forever — the group can
  **never gain a member again**.
- Both test suites mock this into passing (`mls-sync.service.spec.ts:296-298` returns `[]` on the
  second call, which the real server does not do).

Fix at all three layers: don't count a proposal toward `applied`; give proposals a transport that
doesn't consume an epoch slot; and have the server not advance `active.Epoch` for a proposal.

### 4.2 Silence as a defect (P0-15)

- `venta-mobile/.../message_decryptor.dart:81-84` — bare `catch (_)`, no log.
- `Alpine/.../mls-sync.service.ts:122-125` — `console.error` only; a device that can never join
  logs one line per launch and shows the user nothing.
- `Alpine/.../messaging-websocket.service.ts:314-316` — passes raw base64 through to the UI.
- `Alpine/.../message.store.ts:122-124` — bare `catch {}`.

This is why R1 shipped undetected. **Every one of these needs a counted, surfaced failure state**
(`undecryptable` badge + a "this device can't read this conversation — re-link it" affordance)
before release.

---

## 5. High priority (P1)

### Echo

| ID | Issue | Location |
|---|---|---|
| E-H5 | **Device removal does not remove the device from any MLS group.** `DeviceRemoved` has no consumer. A sold/stolen/logged-out handset keeps decrypting all future messages. No post-compromise security. | `MlsDeviceEndpoint.cs:129-156` |
| E-H6 | `/consume-tokens` is an unrate-limited **key-package drain** — one friend can burn ~100 packages/min from every device you own, forcing everyone onto the reusable last-resort package (losing forward secrecy), then to `Unreachable`. | `ConversationEndpoints.cs:27-42` |
| E-H8 | 30-day commit prune can strand a device that joined from an older Welcome; it gets an empty catch-up list, indistinguishable from "up to date". | `MlsGroupService.cs:336-340` |
| E-M1 | **Early-return-after-mutation** — Wolverine auto-commit persists a half-applied generation on a `NotFound` path. Latent only until a conversation enable/disable route exists. | `MlsGroupService.cs:125-155`, `:203-224` |
| E-M2 | `StoreWelcomes` accepts arbitrary `UserId`/`DeviceId` from the publisher, unbounded, unvalidated. | `MlsGroupService.cs:417-442` |
| E-M3 | `AckWelcomes` scoped by `UserId` only — one device can ack another's Welcome. | `MlsEndpoints.cs:404-406` |
| E-M4 | `ConversationMemberDevice.DeviceId` has a **globally unique** index — a device could belong to at most one conversation. Landmine for whoever wires it up. | `MicroserviceContext.cs:217` |
| E-M5 | `GetCommitsAsync` with no generation **and** no active generation returns commits from *all* generations interleaved. | `MlsGroupService.cs:372-394` |
| E-M6 | Migration backfill writes `activated_by_user_id = ''`, which counts as a real actor → a DM demands 2 approvals and deadlocks. | `20260731122137_AddMlsGenerations.cs` |
| E-M9 | Purge handler never removes `PendingWelcomes`, `MlsCommits`, `MlsJoinRequests`, `MlsGroupGenerations`. Purged users' key packages and fingerprints survive. | purge path |

### Alpine

| ID | Issue | Location |
|---|---|---|
| A-H1 | `mls-message-cache.json` stores the **plaintext of every message**, unencrypted, never pruned. Larger at-rest exposure than the ciphertext on the server. | `mls.service.ts:157-158, 220-227` |
| A-H3 | `verifySenderInRoster` / `processAndVerifyMessage` are **dead code** — zero call sites. The documented anti-spoofing guard is not applied on any decrypt path. (Mobile *does* apply it.) | `mls.service.ts:506-543` |
| A-H4 | `exportGroupInfo` is called **before** `mergePendingCommit`, so every published `groupInfo` is one epoch stale → external-commit rejoin lands stale. Two code paths disagree. | `mls-sync.service.ts:276-282` |
| A-H7 | 10 key packages minted at registration are **never uploaded**; the device has zero on the server until replenish runs. | `device-registration-modal.ts:60` |
| A-M10 | Any transient keychain error → registration modal → new signing key → permanently orphans the device (R3). | `main-page.component.ts:291-298` |
| A-M13 | No `isTauri()` guard on any MLS path. Tauri-only today by accident (the app crashes at bootstrap in a browser), not by design. | `mls.service.ts` etc. |
| A-M8 | `pending_messages` is declared, retained and cleared but **never written to** — the promised future-epoch buffer doesn't exist; an early message is lost permanently. | `mls.rs:119, 917-919` |

### venta-mobile

| ID | Issue | Location |
|---|---|---|
| M-B4 | **Silent identity re-mint** — keychain miss → fresh Ed25519 pair over live group state. Device holds groups it can neither sign nor decrypt for, but `isUnlocked` is true and the UI offers to send. | `mls_service.dart:91-151` |
| M-B5 | `FlutterSecureStorage()` with **all defaults** — iOS `whenUnlocked` (fails on a post-reboot background launch), Android non-`encryptedSharedPreferences` (the path that loses data on backup/restore). Directly feeds M-B4. | `secure_storage_service.dart:11` |
| M-B6 | Corrupt-state recovery wipes the engine, but Welcomes are single-use and already acked. For a **conversation** there is no re-admission route → permanent silent removal from every encrypted DM. `rejoinGroup` exists and is **unwired**. | `mls_service.dart:69-82` |
| M-B7 | 10 key packages minted at identity creation are never uploaded and never freed — dead weight re-serialized on every mutating call. | `mls_service.dart:132` |
| M-B8 | The FCM background isolate constructs its own `VentaMls`; the Rust mutex prevents data races but **not ordering** — a push decrypt can interleave between stage and merge of a two-phase commit. | `message_push_decryptor.dart:68` |
| M-B9 | `MlsStore.stateDirectory` caches `_dir` ignoring `userId` — a push for account B can read account A's state directory. | `mls_store.dart:104-110` |
| M-B10 | A structurally broken Welcome is retried forever with no counter, no backoff, no GC. | `mls_sync_service.dart:120-152` |

---

## 6. Export / import / cloud backup

### 6.1 Current state

| Piece | Status |
|---|---|
| Rust `export_state`/`import_state` (AES-256-GCM, 12-byte nonce prefix) | ✅ implemented **identically in both clients**, round-trip tested in Rust |
| Alpine export UI | ⚠️ write-only — `logout-dialog.component.ts:86-114` downloads `alpine-keys-*.enc`; there is a literal `// TODO: Dominic - cloud backup` at `:99` |
| Alpine `importState` | ❌ **zero call sites.** No restore UI exists. |
| Mobile export/import | ❌ zero call sites; no Argon2/AES package in `pubspec.yaml`; no master-key service at all |
| `UserDeviceBackup` table (`UserId`, `DeviceId`, `byte[] Backup`, cascade-delete) | ⚠️ real entity, mapped at `MicroserviceContext.cs:135-144` — **no endpoint reads or writes it** |
| `ApplicationUser.EncryptedMasterKey` (Argon2 params + wrapped key) | ⚠️ exists, `POST /api/v1/users/master`; Alpine uses it, mobile never calls it |

### 6.2 The blocking gap

**Today's export cannot restore anything, on either client.** `export_state` covers the OpenMLS
provider storage and group ids. It **omits**:

1. The **Ed25519 signing keypair** — lives in `MlsState.signers` (memory) + the OS keychain, never
   in provider storage. Alpine's logout flow *deletes it immediately after exporting*.
2. The **device id** (`settings.json` / `mls_device_id`).
3. The **group registry** (`contextId#generation → groupId`) — without it the restored groups are
   unaddressable; every context reads as unencrypted.
4. The **plaintext message cache** — the only readable copy of history, since MLS decrypts from the
   wire exactly once.

### 6.3 The hard constraint, stated plainly

**Cloning live MLS ratchet state across two concurrently-live devices is unsafe and must not be
the restore mechanism.** Two devices sharing one leaf:

- derive the same sender-ratchet keys and reuse generations → openmls treats the repeat as a replay
  and at least one device becomes unable to send;
- have **no forward secrecy** for that leaf — possession of the blob decrypts everything from that
  point on;
- void post-compromise security — an Update from one clone leaves the other holding keys the group
  believes were rotated.

**Therefore:**

| Scenario | Mechanism |
|---|---|
| Same-device recovery (reinstall, disk restore, replacement handset where the old one is gone) | **Clone.** The only way to recover readable history. Gate on `DELETE /devices/client/{id}` for the old device first, and keep the device id, so exactly one leaf exists. |
| Adding a new, additional device | **Re-join, never clone.** Mint a fresh signing key + key packages, register as its own device row, be admitted by an existing member via Add commit + Welcome. History before the join stays unreadable — that is correct MLS behaviour and should be **shown in the UI**, not papered over. |
| Wanting history on the new device anyway | Restore **only** the plaintext message cache, sealed under the passphrase-derived key. Gives users what they actually want without touching ratchet state. |

> **Backup is not a substitute for fixing R2.** Without an Add-commit path, a restored backup on a
> new device id still can't read anything.

### 6.4 Envelope format (v1)

```jsonc
{ "v": 1,
  "kdf":   { "alg": "argon2id", "salt": "<16B b64>", "m": 65536, "t": 3, "p": 4 },
  "aead":  "AES-256-GCM",
  "nonce": "<12B b64>",
  "aad":   "venta.keybackup.v1|<userId>|<deviceId>",
  "ct":    "<b64>" }
```

Payload plaintext:

```jsonc
{ "userId", "deviceId", "createdAt", "appVersion",
  "signing":       { "pub": "b64", "priv": "b64", "identity": "<userId>" },
  "engine":        <PersistedMlsState>,   // marked "engineRestore": "same-device-only"
  "groupRegistry": { "ctx#gen": "groupIdB64", "ctx#active": 3 },
  "messageCache":  { "<messageId>": "plaintextB64" }   // opt-in, size-capped
}
```

`argon2` is already a dependency in Alpine's `src-tauri/Cargo.toml:43`, and `MasterKeyService`
already does Argon2id in TS. Derive from a **passphrase**, not the account password directly, and
never send the derived key to the server.

### 6.5 Server API surface

All `[Authorize]`, all requiring a validated `X-Device-Id` that **equals** `{deviceId}` — so a
compromised web session cannot read the desktop's blob.

```
PUT    /api/v1/identity/backup/recovery-key      # generalises EncryptedMasterKey; requires re-auth
GET    /api/v1/identity/backup/recovery-key

PUT    /api/v1/identity/devices/client/{deviceId}/backup     # octet-stream + If-Match (etag)
GET    /api/v1/identity/devices/client/{deviceId}/backup
GET    /api/v1/identity/devices/client/{deviceId}/backup/meta
DELETE /api/v1/identity/devices/client/{deviceId}/backup
GET    /api/v1/identity/backup                                # metadata only, all devices

POST   /api/v1/identity/backup/transfers                      # device→device, HPKE-wrapped
GET    /api/v1/identity/backup/transfers/pending
POST   /api/v1/identity/backup/transfers/{id}/claim           # single-use, TTL, then hard-deleted
```

Rules the server must enforce:

1. Reads emit an audit row **and** a realtime notice to the owner's other devices — backup
   exfiltration must be visible.
2. Hard size cap (8–32 MiB) + per-device write rate limit. `mls_state.json` is the entire provider
   store re-serialized and grows monotonically (M-B7/A-H7 make this worse).
3. Keep the last N=3 versions per device so a corrupted write doesn't destroy the only copy;
   `If-Match` prevents lost updates between two sessions of the same device.
4. `recoveryKeyVersion` binds a blob to the envelope it was sealed under; reject mismatches.
5. Rotating the recovery key must return the list of blobs that will become unreadable and require
   an explicit `?acknowledgeOrphans=true`.
6. Fix the existing `POST /users/master` write-once guard (`UserController.cs:45-48`), which
   silently allows overwriting the wrapped master key with a *different* version — no re-auth, no
   rate limit, no audit.
7. Exclude the plaintext message cache from the cloud target **by default**; offer it only on the
   local-file path.

### 6.6 Client work

**Alpine** — new Tauri commands so key material never crosses IPC in the clear:
`mls_export_backup(passphrase, includeMessageCache)` (assembles Rust-side, since the TS cannot see
`MlsState.signers`) and `mls_import_backup(blob, passphrase) -> {deviceId, identity, keyHandle}`.
Then build the restore UI that `importState` has never had.

**Mobile** — add `cryptography`/`argon2` to `pubspec.yaml`, implement `MasterKeyService` mirroring
Alpine against the existing `/api/v1/user/master`, add a `MlsBackupService`, and wire `file_picker`
for both directions. Fix M-B5 first — the keychain isn't a durable key store until it is.

**Both** — after any import: re-load the signing key, re-run `replenishKeyPackages` (the imported
engine's packages are already consumed server-side), and wire `rejoinGroup` (external commit),
which is implemented and tested in Rust on both clients and has **no caller** on either.

---

## 7. Test plan

Highest value first. The wire format — where the actual bug is — has **zero** coverage today.

1. **Wire-encoding contract test (mobile).** Assert what `MessageDecryptor` hands the engine is
   byte-identical to what `_sendEncrypted` posted, given `content = base64(utf8(posted))`. Catches
   R1. Update `mls_decrypt_test.dart:98`, which currently pins the bug.
2. **Cross-client golden vectors.** Check a fixture (KeyPackage, Welcome, commit, application
   message) produced by one client's Rust into **both** repos and assert the other consumes it.
   The version pin in `venta_mls/Cargo.toml` is a comment; this makes it an assertion.
3. **Push vs REST envelope round-trip** — assert the two paths differ and both work.
4. **Leave-proposal catch-up** must terminate (all three repos). Un-mock
   `mls-sync.service.spec.ts:296-298`.
5. **Self-published commit reappearing in `getCommits`** — catches P0-9.
6. **Account switch isolation (mobile)** — B's engine holds no groups, A's file intact. Catches P0-10.
7. **Identity re-mint guard (mobile)** — engine has groups + keychain returns null → must not
   silently mint.
8. **Multi-device conversation creation (Echo)** — a member with two devices and one Welcome must
   be rejected. Today only the zero-welcome case is covered.
9. **Cross-device welcome consumption (Echo)** — device A's legacy fetch must not consume device B's
   Welcome. Existing coverage is cross-*user* only, which is the safe case.
10. **Channel commit fanout (Echo)** — assert someone is notified.
11. **Concurrent commit → 409 not 500** — needs a Postgres-backed integration test; the InMemory
    provider ignores filtered unique indexes.
12. **Retention vs unconsumed Welcome** — Welcome at epoch 5, commits 6–7 pruned, device joins.
13. **Persistence/crash safety (Alpine)** — truncated `mls_state.json`, missing group id,
    `state_path == None` no-op save. None are tested; they need a Tauri `AppHandle`.
14. **Architecture test: every published bus event has a consumer.** Would have caught both
    `DeviceRegistered` and `DeviceRemoved` having none.
15. **E2E**: `Echo.E2E.Tests` has **zero** MLS references today.

---

## 8. Migration / deployment state

Five unreleased MLS migrations as of `e39173e`. Apply **Identity's two first**, then Messaging's
three in order.

- `Identity/20260731113558_AddMlsKeyPackageLifecycle` — **must not run without its backfill**
  (`expires_at = created_at + 90 days`, `:33-34`). Without it every package lands on `0001-01-01`,
  reads as expired, and **every registered device becomes `Unreachable` overnight**.
- `Identity/20260731133513_ConsolidateDeviceConcepts` — **hand-edited** (the scaffolded version
  dropped both token tables before creating the destination, i.e. would have deleted every push
  token in production). Changes `client_device_id` uniqueness from global to
  `(user_id, client_device_id)`, which MLS device registration depends on. Not yet applied to prod.
- `Messaging/20260731122137_AddMlsGenerations` — the backfill skips encrypted conversations with a
  `NULL mls_group_id`, which `Conversation.Create` permits (nothing validates it). Those rows get
  no generation, so every encrypted send is refused while the DTO still reports `Encrypted` —
  permanently unusable, with no recovery route.

  **Run before deploying:**
  ```sql
  SELECT count(*) FROM conversations WHERE encryption_state = 'encrypted' AND mls_group_id IS NULL;
  ```
- The filtered unique index on `mls_group_generations(context_id) WHERE state = 'active'` will fail
  the migration outright if any context already has two active generations. Impossible on a fresh
  table; a hazard when re-running against a partially-migrated DB.

---

## 9. Suggested sequencing

**Wave 1 — unblock the reported bug (small, high confidence)**
P0-1 (mobile base64) · P0-15 (surface failures) · P0-3 (key-package reset endpoint) ·
P0-13 (silent no-op save) · P0-12 (atomic write)

**Wave 2 — stop the silent data loss**
P0-5 (legacy welcome fetch) · P0-6 (per-device reachability) · P0-10 (account-switch merge) ·
P0-8 (409 not 500) · P0-9 (stranded commit) · P0-14 (plaintext fallback)

**Wave 3 — make multi-device actually work**
P0-2 (conversation join requests, generalising the channel machinery) · P0-4 (leave proposal) ·
P0-7 (channel fanout) · E-H5 (device removal → group removal) · wire `rejoinGroup` on both clients

**Wave 4 — at-rest hardening**
P0-11 (encrypt `mls_state.json`) · A-H1 (encrypt/prune the message cache) · M-B5 (secure-storage
options) · M-B4 (identity re-mint guard) · A-H3 (wire or delete the roster check)

**Wave 5 — export / import / cloud backup**
§6 in full, local-file first, cloud second.

Wave 1 is independently shippable and should measurably fix the user's complaint on its own.
