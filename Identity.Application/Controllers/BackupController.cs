using System.Security.Claims;
using Identity.Application.Dtos.Request;
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
public class BackupController(MicroserviceContext ctx, UserManager<ApplicationUser> users, IMessageBus bus)
    : ControllerBase
{
    /// <summary>Header carrying the calling device's client device id. A blob may only be read by
    /// the device that owns it, so this has to be present and validated - a web session holding a
    /// perfectly good token must not be able to download the desktop's keys.</summary>
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

    private string? CallingDeviceId
    {
        get
        {
            var value = Request.Headers[DeviceIdHeader].ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
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

        if (string.IsNullOrEmpty(dto.Password) || !await users.CheckPasswordAsync(user, dto.Password))
            return BadRequest("Incorrect password");

        if (dto.CipherText is null or { Length: 0 } || dto.Salt is null or { Length: 0 })
            return BadRequest("Salt and cipherText are required");

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

            if (dto.RecoveryCodeWrapping is null)
            {
                // Genuinely idempotent: nothing submitted that is not already stored.
                return Ok(new PutRecoveryKeyResultDto { Version = current.Version });
            }

            // Additive. Also covers regenerating a recovery code, which is a re-wrap of the same key
            // under a new code - forcing a version bump for that would orphan every blob on the
            // account to change a credential that none of them are sealed under.
            user.RecoveryCodeWrappedMasterKey = ToWrapping(dto.RecoveryCodeWrapping, dto.Version);

            Audit(userId, IdentityAuditActions.RecoveryKeyWritten,
                $"recovery-code wrapping added at v{dto.Version} (no rotation)", CallingDeviceId);

            await ctx.SaveChangesAsync();

            return Ok(new PutRecoveryKeyResultDto { Version = dto.Version });
        }

        // ── Version bump: a genuine rotation, which does orphan blobs ─────────────────
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

        user.RecoveryCodeWrappedMasterKey = dto.RecoveryCodeWrapping is null
            ? null
            : ToWrapping(dto.RecoveryCodeWrapping, dto.Version);

        // A fresh envelope means a freshly wrapped password half, so any stale stamp from an earlier
        // reset no longer applies.
        user.MasterKeyPasswordWrappingInvalidatedAt = null;

        Audit(userId, IdentityAuditActions.RecoveryKeyWritten,
            $"version {current?.Version.ToString() ?? "none"} -> {dto.Version}");

        await ctx.SaveChangesAsync();

        return Ok(new PutRecoveryKeyResultDto { Version = dto.Version, OrphanedBlobDeviceIds = orphaned });
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
    /// <para>No password check. Producing a valid wrapping of the master key <i>is</i> the proof,
    /// and the caller already holds an authenticated session; asking for the password on top would
    /// gate the recovery path on the thing that was just reset.</para>
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

        // This re-wraps the existing key; it does not mint a new one. A version mismatch means the
        // client is holding a master key the account has moved on from, and writing it would make
        // every blob sealed under the current version unopenable.
        if (dto.Version != user.EncryptedMasterKey.Version)
        {
            return Conflict(new { currentVersion = user.EncryptedMasterKey.Version, submitted = dto.Version });
        }

        user.EncryptedMasterKey = ToWrapping(dto.PasswordWrapping, dto.Version);
        user.MasterKeyPasswordWrappingInvalidatedAt = null;

        Audit(userId, IdentityAuditActions.MasterKeyRewrapped, $"password wrapping restored at v{dto.Version}",
            CallingDeviceId);

        await ctx.SaveChangesAsync();

        return Ok(new { version = dto.Version, encryptedHistoryRecoverable = true });
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

    [HttpPut("devices/client/{deviceId}/backup")]
    [RequestSizeLimit(UserDeviceBackup.MaxSizeBytes + 4096)]
    public async Task<IActionResult> PutBackup(string deviceId)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var device = await ResolveOwnDeviceAsync(userId, deviceId);
        if (device is null) return DeviceRejection(deviceId);

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
            CallingDeviceId);

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
    /// everything, so it is bound to a validated <c>X-Device-Id</c>, audited, and announced to the
    /// account's other devices before the bytes go out.</para>
    /// </summary>
    [HttpGet("devices/client/{deviceId}/backup")]
    public async Task<IActionResult> GetBackup(string deviceId)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var device = await ResolveOwnDeviceAsync(userId, deviceId);
        if (device is null) return DeviceRejection(deviceId);
        if (!IsCallingDevice(deviceId)) return Forbid();

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
    /// audited as a read: no key material crosses the wire.</summary>
    [HttpGet("devices/client/{deviceId}/backup/meta")]
    public async Task<IActionResult> GetBackupMeta(string deviceId)
    {
        var userId = CallerId;
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var device = await ResolveOwnDeviceAsync(userId, deviceId);
        if (device is null) return DeviceRejection(deviceId);

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
        if (!IsCallingDevice(deviceId)) return Forbid();

        var blobs = await ctx.UserDeviceBackups.Where(b => b.DeviceId == device.Id).ToListAsync();
        if (blobs.Count == 0) return NoContent();

        ctx.UserDeviceBackups.RemoveRange(blobs);
        Audit(userId, IdentityAuditActions.BackupDeleted, $"{deviceId} ({blobs.Count} versions)", CallingDeviceId);
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

        var source = CallingDeviceId;
        if (string.IsNullOrWhiteSpace(source)) return BadRequest($"{DeviceIdHeader} is required.");
        if (await ResolveOwnDeviceAsync(userId, source) is null) return Forbid();

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

        var target = CallingDeviceId;
        if (string.IsNullOrWhiteSpace(target)) return BadRequest($"{DeviceIdHeader} is required.");
        if (await ResolveOwnDeviceAsync(userId, target) is null) return Forbid();

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

        var target = CallingDeviceId;
        if (string.IsNullOrWhiteSpace(target)) return BadRequest($"{DeviceIdHeader} is required.");

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

    private bool IsCallingDevice(string clientDeviceId) =>
        string.Equals(CallingDeviceId, clientDeviceId, StringComparison.Ordinal);

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

    private async Task RecordReadAsync(string userId, UserDevice device, UserDeviceBackup backup)
    {
        var now = DateTimeOffset.UtcNow;

        Audit(userId, IdentityAuditActions.BackupRead, $"{device.ClientDeviceId} v{backup.Version}",
            CallingDeviceId);

        // Committed before the bytes leave, so a read that is announced is a read that is recorded.
        await ctx.SaveChangesAsync();

        await bus.PublishAsync(new DeviceBackupRead
        {
            UserId = userId,
            DeviceId = device.ClientDeviceId,
            DeviceName = device.DeviceName,
            ReadByDeviceId = CallingDeviceId,
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
