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

/// <summary>One version of one device's encrypted key backup.</summary>
public class UserDeviceBackup : BaseEntity<UserDeviceBackup>, IPrefixedEntity
{
    public static string Prefix { get; } = "udba";

    /// <summary>How many versions of a device's backup are kept.</summary>
    public const int RetainedVersions = 3;

    /// <summary>Hard ceiling on one blob.</summary>
    public const long MaxSizeBytes = 16L * 1024 * 1024;

    /// <summary>Minimum spacing between writes for one device.</summary>
    public static readonly TimeSpan MinWriteInterval = TimeSpan.FromMinutes(1);

    public string UserId { get; set; } = null!;
    public virtual ApplicationUser User { get; set; } = null!;

    /// <summary>The <see cref="UserDevice"/> row id, not the client device id.</summary>
    public string DeviceId { get; set; } = null!;
    public virtual UserDevice Device { get; set; } = null!;

    public byte[] Backup { get; set; } = null!;

    /// <summary>Client-supplied, monotonic per device.</summary>
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
