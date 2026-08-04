<!-- LEGAL REVIEW REQUIRED -->

# Privacy Policy — PLACEHOLDER, NOT IN FORCE

**This document is a structural outline. It is not a privacy policy, it has not been reviewed by
counsel, and it makes no representation to any user.** A plausible-looking generated privacy
policy is worse than an obvious placeholder, because a plausible one ships. This one cannot be
mistaken for the real thing.

Version: `0.1.0-placeholder`

---

## 1. Controller identity and contact

*To be drafted: the controller's legal name and address, and the DPO or privacy contact.*

## 2. What data is collected

*To be drafted. The outline below names the categories the system actually holds so the drafted
text can be checked against the code rather than against an assumption.*

- Account data — email, username, phone number, birth date, bio (`asp_net_users`)
- Age verification state (`AgeVerification`)
- Authentication and session data — login sessions with IP address and user agent
  (`login_sessions`), and the append-only security audit log (`identity_audit_events`)
- Device data — registered devices, device identity keys, push tokens
- Encrypted key material and encrypted device backups, which the operator cannot decrypt
- Privacy settings and consent records (`user_privacy_settings`, `user_consents`)
- Social graph — profiles, relationships, guild membership
- Message content and metadata, some of it end-to-end encrypted
- Voice — positional voice participation state; recording, if any clip feature ever exists
- Game data — Isle player records, storage, quests, kill logs
- Diagnostic data — error reports, pseudonymized unless `AllowDataCollection` is set

## 3. Why it is processed, and on what legal basis

*To be drafted per category. Must distinguish contractual necessity, legitimate interest, legal
obligation and consent, and must match what the code actually gates on consent (T0-4).*

## 4. Who it is shared with

*To be drafted: sub-processors, error-reporting and infrastructure providers, push-notification
transports (FCM, APNs), and federated instances.*

## 5. International transfers

*To be drafted by counsel.*

## 6. Retention

*To be drafted. Must state the periods the code actually enforces (T1-8) rather than a generic
"as long as necessary":*

| Data | Retention as implemented |
|---|---|
| `LoginSession.IpAddress` / `UserAgent` | scrubbed after 90 days, row kept |
| `IdentityAuditEvent.IpAddress` | scrubbed after 180 days, row kept indefinitely |
| Revoked `LoginSession` rows | deleted after 180 days |
| Account data after deletion | 30-day cancellable grace period, then anonymized in place |

*Backup retention is **not** yet documented and must be before this document is published — see
T1-9: a restore that resurrects a purged account is a reportable breach.*

## 7. Your rights

*To be drafted: access, rectification, erasure, restriction, portability, objection, and the right
to complain to a supervisory authority. Must name the routes that serve them and the response
window the DSR queue tracks (30 days).*

## 8. Consent, and how to withdraw it

*To be drafted. Must state what the code implements: optional consents (data collection,
personalization, voice recording in clips) are withdrawable immediately with no degradation of
core service; Terms and Privacy are not withdrawable while the account is active, and the
withdrawal path there is account deletion.*

## 9. Children and minors

*To be drafted. Must state the age of majority applied, the digital-consent age where different,
and the protections the code enforces server-side for minors (T1-11).*

## 10. Automated decision-making and profiling

*To be drafted. There is no such pipeline today; if that stays true, say so plainly.*

## 11. Security

*To be drafted: end-to-end encryption, device protection levels, and the honest limits of each.*

## 12. Changes to this policy

*To be drafted, matching the versioned-consent mechanism the code implements.*
