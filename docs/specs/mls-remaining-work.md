# MLS — Remaining Work

Handover state as of 2026-08-02. Companion to `mls-hardening-plan.md`,
`mls-hardening-contract.md` (§A–§L) and `mls-security-findings.md`.

## Landed

| Repo | Commit | Verified |
|---|---|---|
| Echo | `6b4a560` (pushed) | 3119 passed / 0 failed, all 12 suites incl. the 220 Identity+E2E that had never run |
| venta-mobile | `c707bc3`, released as `v1.0.55` | 227 Dart / 52 Rust / analyze clean / `swiftc -parse` clean |
| Alpine | `d016285` (not pushed) | 310 Rust / 970 TS / release build clean |

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

## 3. Cross-cutting residuals

- **Legacy bare-`messageId` cache fallback exists on both platforms.** `MlsStore._cacheKey`'s own
  comment documents it as a cross-conversation plaintext-disclosure vector. Both clients now
  *write* the composite key and only *read* the bare one as a fallback. Draining it is a
  coordinated decision — do not diverge on one platform.
- **Alpine's frontend suite has timing-sensitive tests** that fail under CPU load (4 failures
  with a concurrent `cargo test`, then four clean runs alone, with zero TypeScript changed).
  Worth fixing before it bites in CI.
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
