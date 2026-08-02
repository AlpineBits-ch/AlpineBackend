using System.Security.Claims;
using System.Security.Cryptography;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;

// Aliased rather than a plain `using Domain;`: inside Identity.Application the bare name `Domain`
// binds to Identity.Domain first, so the shared enum would silently fail to resolve.
using ProtectionLevel = global::Domain.ProtectionLevel;

namespace Identity.Application.Controllers;

/// <summary>
/// Encrypted key backup: the recovery-key envelope, one opaque blob per device, and the
/// device-to-device handover.
///
/// <para><b>The server is storage, not a participant.</b> Every blob arrives already sealed under a
/// key derived from a passphrase that never leaves the client, and is handed back byte for byte. It
/// is never parsed, never re-encrypted and never inspected - which is also why the size cap, the
/// write interval and the version retention are the only rules it can meaningfully enforce.</para>
///
/// <para><b>Reads are the dangerous operation, not writes.</b> A stolen session that downloads every
/// device's backup leaves nothing behind: no blob changes, nothing is deleted, and the legitimate
/// user has no way to notice. So a read is audited and announced to the account's other devices.
/// Exfiltration that cannot be prevented is at least made visible.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1")]
public class BackupController(
    MicroserviceContext ctx,
    UserManager<ApplicationUser> users,
    IAccountPasswordVerifier passwords,
    SessionDeviceResolver sessionDevices,
    MasterKeyRewrapTicketService rewrapTickets,
    IMessageBus bus)
    : ControllerBase
{
    /// <summary>
    /// Header carrying the calling device's client device id.
    ///
    /// <para><b>Never trusted for authorization.</b> It is a string the caller writes; comparing it
    /// to a path parameter compares one attacker-controlled value to another. Every per-device rule
    /// below resolves the device from the session instead - see
    /// <see cref="SessionDeviceResolver"/>. The header is kept because clients send it and because
    /// it is still a useful routing hint, and it is recorded nowhere that matters.</para>
    /// </summary>
    public const string DeviceIdHeader = "X-Device-Id";

    public const string BackupVersionHeader = "X-Backup-Version";
    public const string RecoveryKeyVersionHeader = "X-Backup-Recovery-Key-Version";

    /// <summary>Declares whether the opaque blob carries MLS engine state (the provider store) as
    /// opposed to only the signing key, group registry and message cache. The server cannot tell by
    /// looking - the blob is ciphertext - so the client has to say, and the strict tier's rule about
    /// engine state in the cloud is enforced on that declaration.</summary>
    public const string IncludesEngineHeader = "X-Backup-Includes-Engine";

    /// <summary>Set on a blob read whose recovery-key version is behind the account's current
    /// envelope. The blob is still served - it may be the only copy - but a restore that silently
    /// used it would fail to decrypt with no explanation.</summary>
    public const string StaleHeader = "X-Backup-Stale";

    private string? CallerId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private UserDevice? _sessionDevice;
    private bool _sessionDeviceResolved;

    /// <summary>The device this session was established from, resolved through
    /// <c>session_id -> LoginSession.DeviceId</c>. Null when the session is not bound to one.
    /// Memoised: several routes need it more than once and it is a join, not a header read.</summary>
    private async Task<UserDevice?> SessionDeviceAsync(string userId)
    {
        if (_sessionDeviceResolved) return _sessionDevice;

        _sessionDevice = await sessionDevices.ResolveAsync(User, userId);
        _sessionDeviceResolved = true;
        return _sessionDevice;
    }

    /// <summary>The id to put in an audit row or a security push: the one derived from the session,
    /// never the one the caller asked us to record. A forgeable forensic trail is worse than
    /// none.</summary>
    private async Task<string?> AuditDeviceIdAsync(string userId) =>
        (await SessionDeviceAsync(userId))?.ClientDeviceId;

    /// <summary>
    /// Refuses unless the caller's session belongs to <paramref name="device"/>.
    ///
    /// <para>Distinguishes "your session is not bound to a device" from "your session is a different
    /// device", because the first is a fixable client state (sign in again with the device id, or
    /// register the device from this session) and the second is the case this check exists for.</para>
    /// </summary>
    private async Task<IActionResult?> RequireCallingDeviceAsync(string userId, UserDevice device)
    {
        var session = await SessionDeviceAsync(userId);

        if (session is null) return SessionDeviceRequired(device.ClientDeviceId);

        if (!string.Equals(session.Id, device.Id, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "wrong_device",
                detail = "Only the device that owns this backup may act on it.",
                callingDeviceId = session.ClientDeviceId,
            });
        }

        return null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Recovery-key envelope
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes, extends, or rotates the recovery-key envelope.
    ///
    /// <para>Gated on the account password rather than merely on holding a token: this envelope is
    /// what makes every stored backup readable, and a rotation silently orphans every blob sealed
    /// under the old version. The orphan list is returned on the refusal too, so the client can show
    /// exactly what is about to become unopenable before asking again with
    /// <paramref name="acknowledgeOrphans"/>.</para>
    ///
    /// <para><b>Three operations, distinguished by version.</b> A <i>higher</i> version rotates the
    /// master key and orphans the blobs sealed under the old one. The <i>same</i> version adds or
    /// replaces the recovery-code wrapping of the key already stored - the retrofit path for the
    /// accounts in the field that only ever had a password wrapping - and cannot orphan anything,
    /// because blobs bind to the version and the version does not move. A <i>lower</i> version is
    /// refused.</para>
    ///
    /// <para><b>This is where <c>publicVerifier</c> comes from.</b> The server cannot derive it - it
    /// never sees the master key - so it can only ever compare a value some earlier write established.
    /// §C.1 cited the field as the safety mechanism for <see cref="RewrapPassword"/> while nothing
    /// wrote it, which made that gate inert on every account in existence. Two rules fix that at the
    /// source: a rotation (or a first write) must carry one, because that is the moment new key
    /// material is established and the only moment a client can be made to produce it; and the
    /// same-version path will backfill one onto an envelope that has none, so accounts in the field
    /// acquire the check without a rotation that would orphan all their blobs.</para>
    /// </summary>
    [HttpPut("backup/recovery-key")]
    public async Task<IActionResult> PutRecoveryKey(
        [FromBody] PutRecoveryKeyDto dto,
        [FromQuery] bool acknowledgeOrphans = false)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var user = await users.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var check = await passwords.CheckAsync(user, dto.Password);
        if (!check.IsOk()) return BadRequest(check.Describe("Writing the recovery-key envelope"));

        if (dto.CipherText is null or { Length: 0 } || dto.Salt is null or { Length: 0 })
            return BadRequest("Salt and cipherText are required");

        if (KdfParameterError(dto.Kdf, dto.Iterations, dto.MemoryKiB, dto.Parallelism) is { } kdfError)
            return BadRequest(kdfError);

        if (dto.RecoveryCodeWrapping is { } wrapping
            && KdfParameterError(wrapping.Kdf, wrapping.Iterations, wrapping.MemoryKiB, wrapping.Parallelism)
                is { } recoveryKdfError)
        {
            return BadRequest(recoveryKdfError);
        }

        // A strict account must have the second wrapping: the tier's whole promise is that a
        // server-assisted password reset cannot restore encrypted history, and that is only true
        // when a credential other than the password opens the master key. Checked before anything is
        // mutated, so the refusal cannot leave a half-written envelope behind.
        if (user.ProtectionLevel == ProtectionLevel.VerifiedDevices && dto.RecoveryCodeWrapping is null)
        {
            return BadRequest("This account is on VerifiedDevices, which requires the master key to "
                              + "be wrapped under a recovery code as well as the password.");
        }

        if (dto.RecoveryCodeWrapping is { } recovery
            && (recovery.CipherText is null or { Length: 0 } || recovery.Salt is null or { Length: 0 }))
        {
            return BadRequest("recoveryCodeWrapping requires salt and cipherText");
        }

        // Both wrappings seal the same master key, so a verifier derived from that key must be the
        // same on both. Different values mean the client wrapped two different keys and called them
        // one - the recovery code would then open something no blob is sealed under, discovered years
        // later on the one journey that has no fallback. §L.8's other half.
        if (dto.RecoveryCodeWrapping?.PublicVerifier is { Length: > 0 } recoveryVerifier
            && dto.PublicVerifier is { Length: > 0 } passwordVerifier
            && !CryptographicOperations.FixedTimeEquals(recoveryVerifier, passwordVerifier))
        {
            return BadRequest(
                "publicVerifier differs between the password wrapping and the recovery-code wrapping. "
                + "Both wrap the same master key, so both must derive the same verifier; two different "
                + "values mean the recovery code opens key material no backup is sealed under.");
        }

        var current = user.EncryptedMasterKey;

        // The old write-once guard compared versions and let anything with a *different* number
        // through, which is the exact opposite of write-once: it permitted silently replacing the
        // wrapped master key while refusing a harmless idempotent re-post of the same one.
        if (current is not null && dto.Version < current.Version)
            return BadRequest($"Recovery key version must not go backwards; current version is {current.Version}.");

        // ── Same version: the retrofit path ───────────────────────────────────────────
        //
        // Every account in the field has only the password wrapping, and adding a recovery code to
        // one is not a rotation - it is a second wrapping of the *same* master key at the *same*
        // version. This used to return Ok without writing anything, so the user was shown a recovery
        // code that opened nothing while being told they were protected; bumping the version instead
        // "worked" but orphaned every backup blob the account had, for a key that never changed.
        // Both outcomes are worse than not offering the feature.
        //
        // Nothing here can orphan a blob: blobs bind to the version, and the version does not move.
        // The orphan computation below is unreachable from this branch by construction.
        if (current is not null && dto.Version == current.Version)
        {
            // The password wrapping is not rewritten here. Different bytes under an unchanged
            // version means either a re-wrap under a new password - which is what
            // rewrap-password exists for and which this route cannot distinguish - or a different
            // master key masquerading as the same one, which would make every blob at this version
            // unopenable while claiming nothing changed.
            if (!current.CipherText.AsSpan().SequenceEqual(dto.CipherText))
            {
                return BadRequest(
                    "The password wrapping differs from the stored one at the same version. Use "
                    + "POST api/v1/backup/recovery-key/rewrap-password to re-wrap under a new "
                    + "password, or bump the version to rotate the master key.");
            }

            // ── Verifier backfill ─────────────────────────────────────────────────────
            //
            // The accounts in the field have no verifier at all, so the check §C.1 advertises on
            // rewrap-password has nothing to compare against and passes vacuously. It cannot be
            // computed server-side and it cannot be demanded by rotating, because rotation orphans
            // every blob the account has. This is the remaining route: the same key, at the same
            // version, gaining the value derived from it. Safe to accept on the client's word because
            // reaching here already cost the account password - the same credential that could
            // rotate the key outright.
            //
            // Once present it is immutable at a given version. A stored verifier that a later request
            // could overwrite is not a check, it is a field an attacker sets to whatever their own
            // wrapping produces.
            var backfilled = false;
            var effectiveVerifier = current.PublicVerifier;

            if (dto.PublicVerifier is { Length: > 0 } submitted)
            {
                if (effectiveVerifier is { Length: > 0 } stored)
                {
                    if (!CryptographicOperations.FixedTimeEquals(stored, submitted))
                    {
                        return BadRequest(
                            "publicVerifier does not match the one stored at this version. The verifier "
                            + "is derived from the master key, so a different value means different key "
                            + "material - bump the version to rotate, which is the operation that "
                            + "actually replaces a key.");
                    }
                }
                else
                {
                    effectiveVerifier = submitted;
                    backfilled = true;
                }
            }

            // The recovery-code wrapping seals the key the envelope already holds, so it inherits that
            // key's verifier rather than being allowed to introduce a competing one.
            if (dto.RecoveryCodeWrapping?.PublicVerifier is { Length: > 0 } incomingRecoveryVerifier
                && effectiveVerifier is { Length: > 0 } known
                && !CryptographicOperations.FixedTimeEquals(known, incomingRecoveryVerifier))
            {
                return BadRequest(
                    "recoveryCodeWrapping.publicVerifier does not match the one this account's master "
                    + "key already has. This wrapping seals different key material, so the recovery "
                    + "code would open nothing any backup is sealed under.");
            }

            effectiveVerifier ??= dto.RecoveryCodeWrapping?.PublicVerifier;
            if (effectiveVerifier is { Length: > 0 } && current.PublicVerifier is null or { Length: 0 })
            {
                user.EncryptedMasterKey = current.WithPublicVerifier(effectiveVerifier);
                backfilled = true;
            }

            if (dto.RecoveryCodeWrapping is null)
            {
                if (backfilled)
                {
                    Audit(userId, IdentityAuditActions.RecoveryKeyWritten,
                        $"publicVerifier backfilled at v{dto.Version} (no rotation)",
                        await AuditDeviceIdAsync(userId));
                    await ctx.SaveChangesAsync();
                }

                // Otherwise genuinely idempotent: nothing submitted that is not already stored.
                return Ok(new PutRecoveryKeyResultDto
                {
                    Version = current.Version,
                    HasPublicVerifier = effectiveVerifier is { Length: > 0 },
                });
            }

            // Additive. Also covers regenerating a recovery code, which is a re-wrap of the same key
            // under a new code - forcing a version bump for that would orphan every blob on the
            // account to change a credential that none of them are sealed under.
            user.RecoveryCodeWrappedMasterKey = ToWrapping(dto.RecoveryCodeWrapping, dto.Version)
                .WithPublicVerifier(effectiveVerifier);

            Audit(userId, IdentityAuditActions.RecoveryKeyWritten,
                $"recovery-code wrapping added at v{dto.Version} (no rotation)"
                + (backfilled ? "; publicVerifier backfilled" : string.Empty),
                await AuditDeviceIdAsync(userId));

            await ctx.SaveChangesAsync();

            return Ok(new PutRecoveryKeyResultDto
            {
                Version = dto.Version,
                HasPublicVerifier = effectiveVerifier is { Length: > 0 },
            });
        }

        // ── Version bump: a genuine rotation, which does orphan blobs ─────────────────

        // Required here and only here. This is the write that establishes key material, so it is the
        // one point at which the value can be obtained at all - and making it mandatory is what stops
        // the account from arriving at rewrap-password with nothing to compare against, which is the
        // state every account is in today. A client that omits it gets an actionable 400 on a rare,
        // deliberate operation; the alternative is a security mechanism the spec names and the code
        // never performs.
        if (dto.PublicVerifier is null or { Length: 0 })
        {
            return BadRequest(
                "publicVerifier is required when writing or rotating the master key. Derive it from "
                + "the master key (not from the password) so the server can later confirm that a "
                + "re-wrap seals the same key; without it, POST backup/recovery-key/rewrap-password "
                + "has nothing to check and any holder of a session can orphan every backup blob.");
        }

        var orphaned = current is null
            ? []
            : await ctx.UserDeviceBackups
                .Where(b => b.UserId == userId && b.RecoveryKeyVersion <= current.Version)
                .Join(ctx.UserDevices, b => b.DeviceId, d => d.Id, (_, d) => d.ClientDeviceId)
                .Distinct()
                .ToListAsync();

        if (orphaned.Count > 0 && !acknowledgeOrphans)
        {
            return Conflict(new PutRecoveryKeyResultDto
            {
                Version = current!.Version,
                OrphanedBlobDeviceIds = orphaned,
            });
        }

        user.EncryptedMasterKey = new EncryptedMasterKey
        {
            CipherText = dto.CipherText,
            Salt = dto.Salt,
            Iv = dto.Iv ?? [],
            Argon2Iterations = dto.Iterations,
            Argon2Memory = dto.MemoryKiB,
            Argon2Parallelism = dto.Parallelism,
            Version = dto.Version,
            Kdf = dto.Kdf,
            PublicVerifier = dto.PublicVerifier,
        };

        if (dto.RecoveryCodeWrapping is null)
        {
            user.RecoveryCodeWrappedMasterKey = null;
        }
        else
        {
            // Same key, so same verifier; the cross-check above already refused a conflicting one.
            user.RecoveryCodeWrappedMasterKey = ToWrapping(dto.RecoveryCodeWrapping, dto.Version)
                .WithPublicVerifier(dto.RecoveryCodeWrapping.PublicVerifier ?? dto.PublicVerifier);
        }

        // A fresh envelope means a freshly wrapped password half, so any stale stamp from an earlier
        // reset no longer applies.
        user.MasterKeyPasswordWrappingInvalidatedAt = null;

        Audit(userId, IdentityAuditActions.RecoveryKeyWritten,
            $"version {current?.Version.ToString() ?? "none"} -> {dto.Version}");

        await ctx.SaveChangesAsync();

        return Ok(new PutRecoveryKeyResultDto
        {
            Version = dto.Version,
            OrphanedBlobDeviceIds = orphaned,
            HasPublicVerifier = true,
        });
    }

    [HttpGet("backup/recovery-key")]
    public async Task<IActionResult> GetRecoveryKey()
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.EncryptedMasterKey is null) return NotFound();

        var key = user.EncryptedMasterKey;
        return Ok(new RecoveryKeyDto
        {
            Version = key.Version,
            Kdf = key.Kdf ?? "argon2id",
            Iterations = key.Argon2Iterations,
            MemoryKiB = key.Argon2Memory,
            Parallelism = key.Argon2Parallelism,
            Salt = key.Salt,
            Iv = key.Iv,
            CipherText = key.CipherText,
            PublicVerifier = key.PublicVerifier,
            RecoveryCodeWrapping = FromWrapping(user.RecoveryCodeWrappedMasterKey),
            PasswordWrappingInvalidatedAt = user.MasterKeyPasswordWrappingInvalidatedAt,
            EncryptedHistoryRecoverable = user.EncryptedHistoryRecoverable,
        });
    }

    /// <summary>
    /// Re-wraps the master key under a new password after a reset invalidated the old wrapping.
    ///
    /// <para>The client reaches this by unlocking from the recovery code - the only credential a
    /// reset leaves intact - and re-sealing the same master key under the password the user just
    /// set. Nothing is rotated: the version is unchanged, so every backup blob stays readable, which
    /// is the entire point of wrapping twice.</para>
    ///
    /// <para><b>This route used to require nothing.</b> The justification on record was that
    /// producing a valid wrapping is itself the proof - but nothing verified the wrapping, and the
    /// field that could (<c>publicVerifier</c>) was carried through the DTOs and the entity and
    /// never generated or checked. So the actual behaviour was: any holder of a session token
    /// overwrites the master key with bytes of their choosing, <i>and</i> the write clears
    /// <see cref="ApplicationUser.MasterKeyPasswordWrappingInvalidatedAt"/>, so the account then
    /// reports its encrypted history as recoverable when in fact every blob on it has just become
    /// permanently unopenable. One request, irreversible, reported as healthy.</para>
    ///
    /// <para>Three things now gate it:</para>
    /// <list type="number">
    /// <item><b>A credential.</b> Either the account password (the in-app case, where the user
    /// changed it while still signed in) or a single-use ticket minted by the reset that made the
    /// rewrap necessary (the recovery case, where there is no usable password by definition but the
    /// caller did just prove control of the account's email). A session token alone is not one. This
    /// one always bites.</item>
    /// <item><b>The public verifier.</b> Derived from the master key, so a caller who cannot open it
    /// cannot reproduce the value the account already stores. This is what stops a <i>correct</i>
    /// credential plus wrong key material from silently orphaning every blob - <b>on an account that
    /// has a stored verifier to compare against.</b> See below; this is not yet every account.</item>
    /// <item><b>The version.</b> Unchanged, as before - this re-wraps, it does not rotate.</item>
    /// </list>
    ///
    /// <para><b>What gate 2 does and does not cover.</b> The server never sees the master key, so it
    /// can only compare a verifier some earlier write stored. Accounts created before
    /// <see cref="PutRecoveryKey"/> started demanding one have none, and for those this comparison
    /// cannot happen at all - refusing them instead would brick the recovery journey for the entire
    /// install base to enforce a check whose input does not exist. So the submitted value is stored on
    /// first use, the audit row records that it went unverified, and the response says
    /// <c>verifierChecked: false</c> rather than letting the caller infer a proof that was not
    /// performed. Every subsequent re-wrap on that account is checked for real. §C.1's claim is
    /// accurate from the second write onward and the response is honest about the first, which is the
    /// most that is true - and considerably more than a field the spec cites and nothing ever
    /// writes.</para>
    /// </summary>
    [HttpPost("backup/recovery-key/rewrap-password")]
    public async Task<IActionResult> RewrapPassword([FromBody] RewrapMasterKeyDto dto)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.EncryptedMasterKey is null) return NotFound();

        if (dto.PasswordWrapping is null
            || dto.PasswordWrapping.CipherText is null or { Length: 0 }
            || dto.PasswordWrapping.Salt is null or { Length: 0 })
        {
            return BadRequest("passwordWrapping requires salt and cipherText");
        }

        if (KdfParameterError(dto.PasswordWrapping.Kdf, dto.PasswordWrapping.Iterations,
                dto.PasswordWrapping.MemoryKiB, dto.PasswordWrapping.Parallelism) is { } kdfError)
        {
            return BadRequest(kdfError);
        }

        // ── 1. A credential, not a session ───────────────────────────────────────────
        var authorised = await rewrapTickets.TryConsumeAsync(userId, dto.RewrapTicket);

        if (!authorised)
        {
            var check = await passwords.CheckAsync(user, dto.Password);
            if (check == PasswordCheckResult.LockedOut)
                return BadRequest(check.Describe("Re-wrapping the master key"));

            authorised = check.IsOk();
        }

        if (!authorised)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "credential_required",
                detail = "Re-wrapping the master key needs either the account password or the "
                         + "single-use ticket returned by the password reset that invalidated it. "
                         + "This write destroys the account's encrypted history if it is wrong, so a "
                         + "session token is not enough on its own.",
            });
        }

        // ── 2. Proof that the caller actually holds the master key ───────────────────
        //
        // The verifier travels with whichever wrapping the account already has. The recovery-code
        // wrapping is preferred: on the journey this route exists for, that is the wrapping the
        // client just opened, so it is the one whose verifier the client is in a position to
        // reproduce.
        var storedVerifier = user.RecoveryCodeWrappedMasterKey?.PublicVerifier is { Length: > 0 } rv
            ? rv
            : user.EncryptedMasterKey.PublicVerifier;

        var submittedVerifier = dto.PasswordWrapping.PublicVerifier;

        if (submittedVerifier is null or { Length: 0 })
        {
            return BadRequest(
                "passwordWrapping.publicVerifier is required. It is what proves this wrapping seals "
                + "the master key the account already has, rather than replacing it with bytes "
                + "nothing can open.");
        }

        var verifierChecked = storedVerifier is { Length: > 0 };

        if (verifierChecked
            && !CryptographicOperations.FixedTimeEquals(storedVerifier!, submittedVerifier))
        {
            // Refused before anything is written. A mismatch means the wrapping seals a different
            // key, and storing it would make every blob at this version unopenable while the account
            // went on reporting itself healthy.
            return BadRequest(
                "publicVerifier does not match the one this account's master key already has. This "
                + "wrapping seals different key material; writing it would orphan every backup blob.");
        }

        // ── 3. Same key, same version ────────────────────────────────────────────────
        if (dto.Version != user.EncryptedMasterKey.Version)
        {
            return Conflict(new { currentVersion = user.EncryptedMasterKey.Version, submitted = dto.Version });
        }

        user.EncryptedMasterKey = ToWrapping(dto.PasswordWrapping, dto.Version);
        user.MasterKeyPasswordWrappingInvalidatedAt = null;

        // Trust-on-first-use for the accounts that predate the verifier, so the *next* re-wrap has
        // something to compare against. Carried onto the recovery-code wrapping too - it seals the
        // same key, and leaving it blank would keep re-electing the weaker of the two sources above.
        if (!verifierChecked && user.RecoveryCodeWrappedMasterKey is { } recoveryWrapping
            && recoveryWrapping.PublicVerifier is null or { Length: 0 })
        {
            user.RecoveryCodeWrappedMasterKey = recoveryWrapping.WithPublicVerifier(submittedVerifier);
        }

        Audit(userId, IdentityAuditActions.MasterKeyRewrapped,
            $"password wrapping restored at v{dto.Version}"
            + (verifierChecked
                ? "; publicVerifier verified"
                : "; publicVerifier accepted unverified (account had none stored)"),
            await AuditDeviceIdAsync(userId));

        await ctx.SaveChangesAsync();

        return Ok(new
        {
            version = dto.Version,
            encryptedHistoryRecoverable = true,
            // Not decoration. False means the credential was the only thing that gated this write:
            // the account had no verifier, so nothing confirmed the wrapping seals the key the blobs
            // are sealed under. A client that wants to warn the user before a restore needs to know
            // which of those two things happened.
            verifierChecked,
        });
    }

    /// <summary>
    /// Refuses KDF parameters that would cost more to honour than any client could reasonably ask
    /// for.
    ///
    /// <para>The server never derives anything from these - they are stored and handed back - but
    /// they are handed back to <i>clients</i>, on the recovery path, and an envelope claiming four
    /// terabytes of Argon2 memory is a denial of service against the one journey that has no
    /// fallback. Weak parameters are not the risk here: a blob sealed under a weak KDF still fails
    /// closed on the wrong passphrase. Unbounded ones are.</para>
    /// </summary>
    private static string? KdfParameterError(string? kdf, int iterations, int memoryKiB, int parallelism)
    {
        const int maxMemoryKiB = 1024 * 1024;   // 1 GiB
        const int maxIterations = 10;
        const int maxParallelism = 16;

        if (!string.IsNullOrWhiteSpace(kdf) && kdf != "argon2id" && kdf != "argon2i" && kdf != "argon2d")
            return $"Unsupported kdf '{kdf}'.";

        if (memoryKiB < 0 || memoryKiB > maxMemoryKiB)
            return $"memoryKiB must be between 0 and {maxMemoryKiB} ({maxMemoryKiB / 1024} MiB).";

        if (iterations < 0 || iterations > maxIterations)
            return $"iterations must be between 0 and {maxIterations}.";

        if (parallelism < 0 || parallelism > maxParallelism)
            return $"parallelism must be between 0 and {maxParallelism}.";

        return null;
    }

    private static EncryptedMasterKey ToWrapping(MasterKeyWrappingDto dto, int version) => new()
    {
        CipherText = dto.CipherText,
        Salt = dto.Salt,
        Iv = dto.Iv ?? [],
        Argon2Iterations = dto.Iterations,
        Argon2Memory = dto.MemoryKiB,
        Argon2Parallelism = dto.Parallelism,
        // Both wrappings of one master key share a version - they wrap the same bytes.
        Version = version,
        Kdf = dto.Kdf,
        PublicVerifier = dto.PublicVerifier,
    };

    private static MasterKeyWrappingDto? FromWrapping(EncryptedMasterKey? key) => key is null
        ? null
        : new MasterKeyWrappingDto
        {
            Kdf = key.Kdf ?? "argon2id",
            Iterations = key.Argon2Iterations,
            MemoryKiB = key.Argon2Memory,
            Parallelism = key.Argon2Parallelism,
            Salt = key.Salt,
            Iv = key.Iv,
            CipherText = key.CipherText,
            PublicVerifier = key.PublicVerifier,
        };

    // ══════════════════════════════════════════════════════════════════════════
    // Per-device blob
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Uploads a device's backup blob.
    ///
    /// <para>Bound to the calling device for the same reason the read is: retention keeps three
    /// versions, so three writes to somebody else's blob destroy the real one. That it is a write
    /// rather than a read does not make it the harmless direction.</para>
    /// </summary>
    [HttpPut("devices/client/{deviceId}/backup")]
    [RequestSizeLimit(UserDeviceBackup.MaxSizeBytes + 4096)]
    public async Task<IActionResult> PutBackup(string deviceId)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var device = await ResolveOwnDeviceAsync(userId, deviceId);
        if (device is null) return DeviceRejection(deviceId);
        if (await RequireCallingDeviceAsync(userId, device) is { } denied) return denied;

        if (!int.TryParse(Request.Headers[BackupVersionHeader].ToString(), out var version) || version <= 0)
            return BadRequest($"{BackupVersionHeader} is required and must be a positive integer.");

        if (!int.TryParse(Request.Headers[RecoveryKeyVersionHeader].ToString(), out var recoveryKeyVersion))
            return BadRequest($"{RecoveryKeyVersionHeader} is required.");

        var account = await ctx.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.ProtectionLevel,
                u.EncryptedMasterKey,
                HasRecoveryCodeWrapping = u.RecoveryCodeWrappedMasterKey != null,
            })
            .FirstAsync();

        var currentRecoveryKeyVersion = account.EncryptedMasterKey?.Version ?? 0;

        // Under VerifiedDevices, cloud backup of engine state is off unless a recovery-code wrapping
        // exists. Engine state is the whole provider store: uploading it when the password is the
        // only credential that opens the master key puts every group's ratchet material one password
        // reset away, which is exactly the guarantee this tier exists to make.
        var includesEngine = string.Equals(
            Request.Headers[IncludesEngineHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase);

        if (includesEngine
            && account.ProtectionLevel == ProtectionLevel.VerifiedDevices
            && !account.HasRecoveryCodeWrapping)
        {
            return BadRequest("This account is on VerifiedDevices; engine state may only be backed up "
                              + "once the master key is also wrapped under a recovery code.");
        }

        // A blob sealed under an envelope the account no longer has is unopenable the moment it is
        // written. Refusing here is the difference between an error the client can act on and a
        // restore that fails months later with nothing left to fall back to.
        if (recoveryKeyVersion != currentRecoveryKeyVersion)
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed,
                new { currentRecoveryKeyVersion, submitted = recoveryKeyVersion });
        }

        var payload = await ReadBodyAsync(UserDeviceBackup.MaxSizeBytes);
        if (payload is null) return StatusCode(StatusCodes.Status413PayloadTooLarge,
            new { maxSizeBytes = UserDeviceBackup.MaxSizeBytes });
        if (payload.Length == 0) return BadRequest("Backup body is empty.");

        var existing = await ctx.UserDeviceBackups
            .Where(b => b.DeviceId == device.Id)
            .OrderByDescending(b => b.Version)
            .ToListAsync();

        var newest = existing.FirstOrDefault();

        var ifMatch = Request.Headers.IfMatch.ToString().Trim().Trim('"');
        if (newest is not null && !string.IsNullOrEmpty(ifMatch) && ifMatch != newest.ETag)
            return Conflict(new { expected = newest.ETag });

        var now = DateTimeOffset.UtcNow;

        // Rate limit before the version check, so a client hammering the endpoint is told to slow
        // down rather than being handed a version conflict it will "fix" by retrying harder.
        if (newest is not null && now - newest.UpdatedAt < UserDeviceBackup.MinWriteInterval)
        {
            var retryAfter = (int)Math.Ceiling(
                (UserDeviceBackup.MinWriteInterval - (now - newest.UpdatedAt)).TotalSeconds);
            Response.Headers.RetryAfter = retryAfter.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new { retryAfterSeconds = retryAfter });
        }

        if (newest is not null && version <= newest.Version)
            return Conflict(new { expected = newest.ETag, currentVersion = newest.Version });

        var backup = UserDeviceBackup.Create(new CreateUserDeviceBackupParams
        {
            UserId = userId,
            DeviceId = device.Id,
            Backup = payload,
            Version = version,
            RecoveryKeyVersion = recoveryKeyVersion,
            CreatedAt = now,
        });
        ctx.UserDeviceBackups.Add(backup);

        // Keep the newest few and drop the rest. Counting the incoming row means the retention
        // window is N versions total, not N plus whatever is being written.
        var superseded = existing.Skip(UserDeviceBackup.RetainedVersions - 1).ToList();
        if (superseded.Count > 0) ctx.UserDeviceBackups.RemoveRange(superseded);

        Audit(userId, IdentityAuditActions.BackupWritten, $"{deviceId} v{version} ({payload.Length} bytes)",
            await AuditDeviceIdAsync(userId));

        await ctx.SaveChangesAsync();

        Response.Headers.ETag = $"\"{backup.ETag}\"";

        return Ok(new PutBackupResultDto
        {
            BlobId = backup.Id,
            Version = backup.Version,
            ETag = backup.ETag,
            SizeBytes = backup.SizeBytes,
            UpdatedAt = backup.UpdatedAt,
        });
    }

    /// <summary>
    /// Downloads a device's newest backup blob.
    ///
    /// <para>Only the device itself may. See the type remarks: this is the operation that leaks
    /// everything, so it is bound to the device the <i>session</i> was established from - not to the
    /// <c>X-Device-Id</c> header, which is a string the caller writes and which therefore proved
    /// nothing at all. It is audited and announced to the account's other devices before the bytes
    /// go out, and both records now name the session-derived device, so the trail cannot be authored
    /// by whoever is reading.</para>
    /// </summary>
    [HttpGet("devices/client/{deviceId}/backup")]
    public async Task<IActionResult> GetBackup(string deviceId)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var device = await ResolveOwnDeviceAsync(userId, deviceId);
        if (device is null) return DeviceRejection(deviceId);
        if (await RequireCallingDeviceAsync(userId, device) is { } denied) return denied;

        var backup = await ctx.UserDeviceBackups.AsNoTracking()
            .Where(b => b.DeviceId == device.Id)
            .OrderByDescending(b => b.Version)
            .FirstOrDefaultAsync();

        if (backup is null) return NotFound();

        await RecordReadAsync(userId, device, backup);

        var currentRecoveryKeyVersion = await ctx.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.EncryptedMasterKey!.Version)
            .FirstOrDefaultAsync();

        Response.Headers.ETag = $"\"{backup.ETag}\"";
        Response.Headers[BackupVersionHeader] = backup.Version.ToString();
        Response.Headers[RecoveryKeyVersionHeader] = backup.RecoveryKeyVersion.ToString();

        // Reported, not withheld and not silently served as if fine. A blob sealed under a
        // superseded envelope will simply fail to decrypt, and a restore flow that discovers that
        // by trying has no way to tell it apart from a wrong passphrase or a corrupt file. It is
        // still served because it may be the only copy the user has.
        if (backup.RecoveryKeyVersion != currentRecoveryKeyVersion) Response.Headers[StaleHeader] = "true";

        return File(backup.Backup, "application/octet-stream");
    }

    /// <summary>Metadata only, so a client can decide whether a restore is worth downloading. Not
    /// audited as a read: no key material crosses the wire.
    ///
    /// <para>Still bound to the calling device, so that every route shaped
    /// <c>devices/client/{id}/...</c> obeys the same rule and none of them is the one place the
    /// header still decides something. The account-wide view a restore flow actually needs is
    /// <see cref="ListBackups"/>, which is deliberately not per-device.</para></summary>
    [HttpGet("devices/client/{deviceId}/backup/meta")]
    public async Task<IActionResult> GetBackupMeta(string deviceId)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var device = await ResolveOwnDeviceAsync(userId, deviceId);
        if (device is null) return DeviceRejection(deviceId);
        if (await RequireCallingDeviceAsync(userId, device) is { } denied) return denied;

        var backup = await ctx.UserDeviceBackups.AsNoTracking()
            .Where(b => b.DeviceId == device.Id)
            .OrderByDescending(b => b.Version)
            .FirstOrDefaultAsync();

        if (backup is null) return NotFound();

        return Ok(ToMeta(backup, device, await CurrentRecoveryKeyVersionAsync(userId)));
    }

    [HttpDelete("devices/client/{deviceId}/backup")]
    public async Task<IActionResult> DeleteBackup(string deviceId)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var device = await ResolveOwnDeviceAsync(userId, deviceId);
        if (device is null) return DeviceRejection(deviceId);
        if (await RequireCallingDeviceAsync(userId, device) is { } denied) return denied;

        var blobs = await ctx.UserDeviceBackups.Where(b => b.DeviceId == device.Id).ToListAsync();
        if (blobs.Count == 0) return NoContent();

        ctx.UserDeviceBackups.RemoveRange(blobs);
        Audit(userId, IdentityAuditActions.BackupDeleted, $"{deviceId} ({blobs.Count} versions)",
            await AuditDeviceIdAsync(userId));
        await ctx.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>Every device's backup metadata. The one call a restore flow makes first, to show the
    /// user which of their devices has something to restore from.</summary>
    [HttpGet("devices/backups")]
    public async Task<IActionResult> ListBackups()
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var devices = await ctx.UserDevices.AsNoTracking()
            .Where(d => d.UserId == userId)
            .ToListAsync();

        var deviceRowIds = devices.Select(d => d.Id).ToList();

        var blobs = await ctx.UserDeviceBackups.AsNoTracking()
            .Where(b => deviceRowIds.Contains(b.DeviceId))
            .ToListAsync();

        var newestPerDevice = blobs
            .GroupBy(b => b.DeviceId)
            .Select(g => g.MaxBy(b => b.Version)!)
            .ToList();

        var currentRecoveryKeyVersion = await CurrentRecoveryKeyVersionAsync(userId);

        return Ok(newestPerDevice
            .Select(b => ToMeta(b, devices.First(d => d.Id == b.DeviceId), currentRecoveryKeyVersion))
            .OrderByDescending(m => m.UpdatedAt)
            .ToList());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Device-to-device transfer
    // ══════════════════════════════════════════════════════════════════════════

    [HttpPost("backup/transfers")]
    public async Task<IActionResult> CreateTransfer([FromBody] CreateBackupTransferDto dto)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        // The source is the session's device, not a header. A transfer is a copy of a signing key;
        // letting the caller name which of the account's devices it is coming from is the same
        // mistake as letting it name which backup it may read.
        var sourceDevice = await SessionDeviceAsync(userId);
        if (sourceDevice is null) return SessionDeviceRequired();
        var source = sourceDevice.ClientDeviceId;

        if (dto.CipherText is null or { Length: 0 }) return BadRequest("cipherText is required");
        if (dto.WrappedTo is null or { Length: 0 }) return BadRequest("wrappedTo is required");
        if (string.IsNullOrWhiteSpace(dto.TargetDeviceId)) return BadRequest("targetDeviceId is required");
        if (dto.TargetDeviceId == source) return BadRequest("A device cannot transfer to itself.");

        // Both ends must be the *same* account's devices. A transfer is a copy of a signing key; the
        // only reason the server can be trusted to carry it is that it never crosses an account
        // boundary.
        if (await ResolveOwnDeviceAsync(userId, dto.TargetDeviceId) is null)
            return NotFound("Target device not found");

        if (dto.CipherText.LongLength > UserDeviceBackup.MaxSizeBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { maxSizeBytes = UserDeviceBackup.MaxSizeBytes });

        var now = DateTimeOffset.UtcNow;
        var lifetime = dto.ExpiresInSeconds is > 0
            ? TimeSpan.FromSeconds(dto.ExpiresInSeconds.Value)
            : UserBackupTransfer.DefaultLifetime;
        if (lifetime > UserBackupTransfer.MaxLifetime) lifetime = UserBackupTransfer.MaxLifetime;

        // One live transfer per (source, target). Re-issuing replaces rather than stacks: two
        // ciphertexts for the same handover means one of them is a copy nobody will ever claim and
        // nobody will ever delete.
        var superseded = await ctx.UserBackupTransfers
            .Where(t => t.UserId == userId && t.SourceDeviceId == source && t.TargetDeviceId == dto.TargetDeviceId)
            .ToListAsync();
        if (superseded.Count > 0) ctx.UserBackupTransfers.RemoveRange(superseded);

        var transfer = UserBackupTransfer.Create(new CreateUserBackupTransferParams
        {
            UserId = userId,
            SourceDeviceId = source,
            TargetDeviceId = dto.TargetDeviceId,
            WrappedTo = dto.WrappedTo,
            CipherText = dto.CipherText,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        });
        ctx.UserBackupTransfers.Add(transfer);

        Audit(userId, IdentityAuditActions.BackupTransferCreated, $"{source} -> {dto.TargetDeviceId}", source);
        await ctx.SaveChangesAsync();

        return Ok(new BackupTransferCreatedDto { TransferId = transfer.Id, ExpiresAt = transfer.ExpiresAt });
    }

    [HttpGet("backup/transfers/pending")]
    public async Task<IActionResult> PendingTransfers()
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var targetDevice = await SessionDeviceAsync(userId);
        if (targetDevice is null) return SessionDeviceRequired();
        var target = targetDevice.ClientDeviceId;

        var now = DateTimeOffset.UtcNow;

        // Sweep here rather than on a timer: this is the one call the receiving side makes, and an
        // expired transfer is a copy of a signing key that must not outlive its window.
        var expired = await ctx.UserBackupTransfers.Where(t => t.ExpiresAt <= now).ToListAsync();
        if (expired.Count > 0)
        {
            ctx.UserBackupTransfers.RemoveRange(expired);
            await ctx.SaveChangesAsync();
        }

        var pending = await ctx.UserBackupTransfers.AsNoTracking()
            .Where(t => t.UserId == userId && t.TargetDeviceId == target && t.ExpiresAt > now)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        return Ok(pending.Select(t => new PendingBackupTransferDto
        {
            TransferId = t.Id,
            SourceDeviceId = t.SourceDeviceId,
            WrappedTo = t.WrappedTo,
            CreatedAt = t.CreatedAt,
            ExpiresAt = t.ExpiresAt,
        }).ToList());
    }

    /// <summary>Hands over the ciphertext and deletes the row in the same transaction. Single use is
    /// enforced by the delete, not by a flag - a row that survives a claim is a spare copy of a
    /// signing key sitting in a database nobody is watching.</summary>
    [HttpPost("backup/transfers/{id}/claim")]
    public async Task<IActionResult> ClaimTransfer(string id)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        // The claim hands over a wrapped copy of a signing key. Whoever the session belongs to is
        // the only party entitled to it; a header naming a different device is not evidence of
        // being that device.
        var targetDevice = await SessionDeviceAsync(userId);
        if (targetDevice is null) return SessionDeviceRequired();
        var target = targetDevice.ClientDeviceId;

        var transfer = await ctx.UserBackupTransfers
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId && t.TargetDeviceId == target);

        // Not-found rather than forbidden for a transfer addressed elsewhere: confirming one exists
        // tells an attacker which of the user's devices are mid-handover.
        if (transfer is null) return NotFound();

        var now = DateTimeOffset.UtcNow;
        if (!transfer.IsClaimableAt(now))
        {
            ctx.UserBackupTransfers.Remove(transfer);
            await ctx.SaveChangesAsync();
            return NotFound();
        }

        var payload = new ClaimedBackupTransferDto
        {
            TransferId = transfer.Id,
            SourceDeviceId = transfer.SourceDeviceId,
            WrappedTo = transfer.WrappedTo,
            CipherText = transfer.CipherText,
        };

        ctx.UserBackupTransfers.Remove(transfer);
        Audit(userId, IdentityAuditActions.BackupTransferClaimed, $"{transfer.SourceDeviceId} -> {target}", target);
        await ctx.SaveChangesAsync();

        return Ok(payload);
    }

    // ══════════════════════════════════════════════════════════════════════════

    private Task<UserDevice?> ResolveOwnDeviceAsync(string userId, string clientDeviceId) =>
        ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserId == userId
                                      && d.ClientDeviceId == clientDeviceId
                                      && d.Status == DeviceStatus.Active)!;

    /// <summary>404 when nobody has that id, 403 when somebody else does. ClientDeviceId is only
    /// unique per user, so "signed in as the wrong account" is a real and distinguishable case, and
    /// it is the mistake that actually happens.</summary>
    private IActionResult DeviceRejection(string clientDeviceId) =>
        ctx.UserDevices.Any(d => d.ClientDeviceId == clientDeviceId) ? Forbid() : NotFound();

    /// <summary>
    /// The refusal every unbound session gets, with the way out attached.
    ///
    /// <para>Sessions issued before <c>/connect/token</c> recorded a device - and every session from a
    /// client that still sends no device id - have nothing to resolve, so binding these routes to the
    /// session takes them away from callers who had them yesterday. That is a genuine regression and
    /// pretending otherwise with a bare 403 would leave the client guessing at a fix it cannot infer.
    /// <c>remedy</c> names the route that repairs it in one call
    /// (<c>MlsDeviceEndpoint.BindSessionToDevice</c>) without losing the session; re-authenticating
    /// with the header set still works and costs the same password.</para>
    /// </summary>
    private IActionResult SessionDeviceRequired(string? clientDeviceId = null) =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "session_device_unbound",
            detail = "This session is not bound to a registered device, so it cannot act as one. "
                     + "Backup and transfer routes are per-device and resolve the device from the "
                     + "session rather than from the X-Device-Id header, which any caller can write.",
            remedy = clientDeviceId is null
                ? "POST api/v1/devices/client/{deviceId}/bind-session with the account password, or "
                  + "sign in again with the X-Device-Id header set."
                : $"POST api/v1/devices/client/{clientDeviceId}/bind-session with the account "
                  + "password, or sign in again with the X-Device-Id header set.",
        });

    private Task<int> CurrentRecoveryKeyVersionAsync(string userId) =>
        ctx.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.EncryptedMasterKey!.Version)
            .FirstOrDefaultAsync();

    private static BackupMetaDto ToMeta(UserDeviceBackup backup, UserDevice device,
        int currentRecoveryKeyVersion) => new()
    {
        BlobId = backup.Id,
        DeviceId = device.ClientDeviceId,
        DeviceName = device.DeviceName,
        Version = backup.Version,
        RecoveryKeyVersion = backup.RecoveryKeyVersion,
        SizeBytes = backup.SizeBytes,
        UpdatedAt = backup.UpdatedAt,
        ETag = backup.ETag,
        IsStale = backup.RecoveryKeyVersion != currentRecoveryKeyVersion,
    };

    private void Audit(string userId, string action, string? detail, string? clientDeviceId = null) =>
        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = action,
            ClientDeviceId = clientDeviceId,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }));

    /// <summary>
    /// Records and announces a backup read.
    ///
    /// <para>The push is transient - a device that is offline at 3am never learns of it - so the
    /// audit row is the durable half, and <c>GET api/v1/identity/audit-events</c> is what makes it
    /// readable. Both now carry the <i>session-derived</i> device id: the previous code recorded
    /// whatever the caller put in the header, so the one artifact meant to survive an exfiltration
    /// was written by the exfiltrator.</para>
    /// </summary>
    private async Task RecordReadAsync(string userId, UserDevice device, UserDeviceBackup backup)
    {
        var now = DateTimeOffset.UtcNow;
        var readBy = await AuditDeviceIdAsync(userId);

        Audit(userId, IdentityAuditActions.BackupRead, $"{device.ClientDeviceId} v{backup.Version}", readBy);

        // Committed before the bytes leave, so a read that is announced is a read that is recorded.
        await ctx.SaveChangesAsync();

        await bus.PublishAsync(new DeviceBackupRead
        {
            UserId = userId,
            DeviceId = device.ClientDeviceId,
            DeviceName = device.DeviceName,
            ReadByDeviceId = readBy,
            ReadAt = now,
        });
    }

    /// <summary>Reads the request body up to <paramref name="max"/> bytes, returning null the moment
    /// it goes over. Streams rather than buffering the whole thing first - the cap has to bound the
    /// memory this call can be made to allocate, not just what it stores.</summary>
    private async Task<byte[]?> ReadBodyAsync(long max)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
