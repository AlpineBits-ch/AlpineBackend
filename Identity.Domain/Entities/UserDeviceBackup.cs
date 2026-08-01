using Identity.Domain.Aggregates;
using Persistence;

namespace Identity.Domain.Entities;

public class CreateUserDeviceBackupParams
{
    public string UserId { get; init; } = null!;
    public string DeviceId { get; init; } = null!;
    public byte[] Backup { get; init; } = null!;
    public int Version { get; init; }
    public int RecoveryKeyVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// One version of one device's encrypted key backup. The server never parses
/// <see cref="Backup"/> - it is opaque ciphertext sealed under a passphrase-derived key that never
/// leaves the client, and storing it is the whole of the server's involvement.
///
/// <para><b>Versions are rows, not overwrites.</b> A backup is the only copy of a device's signing
/// key and group registry; a truncated or half-written upload that overwrote the previous blob
/// would destroy the one artifact the user is keeping precisely against that scenario. The newest
/// <see cref="RetainedVersions"/> versions per device are kept and older ones swept on write, so a
/// bad write costs a restore point rather than everything.</para>
///
/// <para><see cref="RecoveryKeyVersion"/> binds the blob to the recovery-key envelope it was sealed
/// under. Rotating the recovery key does not re-encrypt anything, so a blob whose version no longer
/// matches is simply unopenable - saying so up front is better than handing back bytes that fail to
/// decrypt with no explanation.</para>
/// </summary>
public class UserDeviceBackup : BaseEntity<UserDeviceBackup>, IPrefixedEntity
{
    public static string Prefix { get; } = "udba";

    /// <summary>How many versions of a device's backup are kept. Three, so a corrupt write plus a
    /// corrupt retry still leaves a good blob to restore from.</summary>
    public const int RetainedVersions = 3;

    /// <summary>Hard ceiling on one blob. <c>mls_state.json</c> is the entire OpenMLS provider store
    /// re-serialized and grows monotonically, so this is a real bound and not a formality.</summary>
    public const long MaxSizeBytes = 16L * 1024 * 1024;

    /// <summary>Minimum spacing between writes for one device. A backup is re-uploaded whenever
    /// local state changes; without this a busy client turns the blob store into a write loop of
    /// multi-megabyte payloads.</summary>
    public static readonly TimeSpan MinWriteInterval = TimeSpan.FromMinutes(1);

    public string UserId { get; set; } = null!;
    public virtual ApplicationUser User { get; set; } = null!;

    /// <summary>The <see cref="UserDevice"/> row id, not the client device id.</summary>
    public string DeviceId { get; set; } = null!;
    public virtual UserDevice Device { get; set; } = null!;

    public byte[] Backup { get; set; } = null!;

    /// <summary>Client-supplied, monotonic per device. Carried through so a client restoring on a
    /// replacement handset can tell which of the retained blobs it is looking at.</summary>
    public int Version { get; set; }

    /// <summary>Version of the recovery-key envelope this blob was sealed under.</summary>
    public int RecoveryKeyVersion { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Opaque concurrency token handed to the client as an <c>ETag</c> and required back as
    /// <c>If-Match</c>. Two sessions of the same device backing up concurrently would otherwise
    /// silently drop one side's state.</summary>
    public string ETag { get; set; } = null!;

    public static UserDeviceBackup Create(CreateUserDeviceBackupParams parameters)
    {
        var date = parameters.CreatedAt == default ? DateTimeOffset.UtcNow : parameters.CreatedAt;
        return new UserDeviceBackup
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            UserId = parameters.UserId,
            DeviceId = parameters.DeviceId,
            Backup = parameters.Backup,
            Version = parameters.Version,
            RecoveryKeyVersion = parameters.RecoveryKeyVersion,
            SizeBytes = parameters.Backup.LongLength,
            ETag = GenerateETag(),
        };
    }

    /// <summary>Random rather than a content hash: two devices that happen to upload identical bytes
    /// must not share an ETag, or an <c>If-Match</c> from one would satisfy the other.</summary>
    public static string GenerateETag() => Guid.NewGuid().ToString("N");

    public override string ToString()
    {
        return $"{Prefix}:{UserId}:{DeviceId}:v{Version}";
    }
}
