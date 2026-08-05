# Runbook - deletion vs. backups

Status: **decision required, and not yet made.** 2026-08-04.
Owner: whoever operates the deployment (this cannot be decided in code).
Source requirement: `docs/specs/privacy.md`, T1-9, last bullet.

---

## The requirement

> Deletion must propagate into backups, **or** the retention window of backups must be documented
> and shorter than the deletion SLA.

Echo's account purge is complete and verified *in the live system*: `AccountDeletionSaga` fans
`PurgeUserDataCommand` to all eight services and only finishes when every one has acknowledged
(and now alerts if one never does). **Nothing in that path touches a backup.** A restore taken
before the purge brings the account back - username, email, messages, IP addresses, all of it.

## Why this is not a nice-to-have

A restore that resurrects a purged account is processing personal data with no lawful basis, after
the subject exercised their Art. 17 right and after we told them it was done. In practice it
becomes a personal-data breach with a 72-hour notification clock (Art. 33) and it is discovered by
the subject, not by us - the usual symptom is a deleted account receiving mail again. "The backup
was old" is not a defence; the backup being able to do that is the finding.

The same applies, less obviously, to a **partial** restore: restoring one service's database from
before a purge while the other seven stay current leaves an orphaned copy that nothing will ever
purge again, because the saga for that account has long since completed.

## The two acceptable resolutions

Pick exactly one per backup surface. Both are legitimate; the unacceptable state is the current
one, which is neither.

**A. Propagate deletion into backups.** Keep a durable log of purged subject ids (Identity's
`IdentityAuditEvent` rows for `account.purged` already are one) and re-apply it after every
restore, before the restored system is allowed to serve traffic or send mail. This is the only
option if backups must be kept longer than the erasure SLA - e.g. because a finance or legal hold
requires it. It costs a documented, rehearsed post-restore step; an unrehearsed one does not count.

**B. Bound the backup retention window below the deletion SLA.** If every backup is destroyed
within *N* days and the purge is guaranteed to have run within *N* days, no restorable copy can
outlive an erasure. This is much cheaper, and it is the right answer for most deployments - but it
only works if the window is enforced by the storage (a lifecycle rule, a rotation script that
actually deletes) rather than by intention, and if it is genuinely shorter than the SLA. Note the
grace period counts: `ACCOUNT_DELETION_GRACE_PERIOD_SECONDS` defaults to 30 days *before* the purge
starts, so the SLA to compare against is the time from the user's deletion request to the purge
completing, not the purge itself.

## What is backed up here

Nothing in this table has a retention policy today. Every row needs A or B written next to it.

| Surface | What it holds | Notes |
|---|---|---|
| PostgreSQL (all service DBs) | Identity accounts, guilds, social graph, audit log, sessions | The bulk of it. `ventactl backup` runs `pg_dumpall`. |
| ScyllaDB | message history | Not covered by `ventactl backup` at all - snapshots, if any, are whatever the operator set up. |
| MinIO / S3 | attachments, avatars, **data-export archives** | Export archives are the densest personal-data objects in the system; they expire in 7 days live (`DATA_EXPORT_ARTIFACT_TTL_SECONDS`) and that expiry means nothing if a bucket backup keeps them. |
| Redis (`redis_data` volume) | caches, presence, verification codes | Rebuildable. Safe to exclude from backups entirely, which is the cleanest answer. |
| RabbitMQ (`rabbitmq_data`) + Wolverine envelope tables | in-flight messages, which carry message bodies and account ids | Restoring these can also *replay* traffic. Treat as data, not plumbing. |
| `ventactl backup` output | `pg_dumpall` **plus a copy of `.env`** | `/var/backups/venta` on Linux, `%ProgramData%\venta\backups` on Windows. It writes a new timestamped file each run and **deletes nothing, ever**. Also note the `.env` copy is a credential file. |

Not in scope here: Identity's `BackupController` and the device key-backup flow. Those are an
end-to-end-encrypted product feature owned by the user, already purged with the account, and
unrelated to infrastructure backups despite the shared word.

## What the operator must actually do

1. For each row above, choose A or B and write it down (see below). "We don't back that up" is a
   valid answer - record it, because it is also the thing someone will silently change later.
2. If B: make the window real. A lifecycle rule on the bucket, a `find -mtime +N -delete` in the
   rotation job, an actual retention setting on the backup product. Then confirm it by looking at
   what is on disk, not at the script.
3. If B: check the window against the SLA, grace period included. A 90-day backup retention with a
   30-day grace period does not satisfy B, whatever the purge does.
4. If A: write the post-restore purge-replay step into the restore procedure itself, and rehearse
   it. A step that lives only in this document will not run at 3am.
5. Restrict who can read backups, and log reads. A backup is a complete copy of every subject's
   data with none of the access control the live system has.
6. Re-check after any change to `ACCOUNT_DELETION_GRACE_PERIOD_SECONDS`, to
   `DATA_EXPORT_ARTIFACT_TTL_SECONDS`, or to the backup schedule.

## Decision record

Fill this in when the decision is made; until then this section is the evidence that it was not.

| Surface | Resolution (A/B/not backed up) | Retention window | Enforced by | Verified on |
|---|---|---|---|---|
| PostgreSQL | *unset* | *unset* | *unset* | *unset* |
| ScyllaDB | *unset* | *unset* | *unset* | *unset* |
| MinIO / S3 | *unset* | *unset* | *unset* | *unset* |
| Redis | *unset* | *unset* | *unset* | *unset* |
| RabbitMQ | *unset* | *unset* | *unset* | *unset* |
| `ventactl backup` output | *unset* | *unset* | *unset* | *unset* |

Do not fill these in with a plausible-looking number to make the table look finished. An
undocumented window is a gap; a documented window that nothing enforces is a false assurance, which
is worse.
