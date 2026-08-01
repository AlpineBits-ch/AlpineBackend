using Persistence;

namespace Identity.Domain.Entities;

public static class IdentityAuditActions
{
    public const string BackupRead = "backup.read";
    public const string BackupWritten = "backup.written";
    public const string BackupDeleted = "backup.deleted";
    public const string RecoveryKeyWritten = "recovery-key.written";
    public const string BackupTransferCreated = "backup.transfer.created";
    public const string BackupTransferClaimed = "backup.transfer.claimed";
    public const string KeyPackagesReset = "key-packages.reset";
    public const string DeviceIdentityRotated = "device.identity-rotated";

    /// <summary>Append-only and surfaced in the UI on purpose: a silent downgrade from
    /// VerifiedDevices is the step that makes auto-admitting an attacker's device possible.</summary>
    public const string ProtectionLevelChanged = "protection-level.changed";

    /// <summary>Invalidates every peer's pinning and every device certificate under the old key.</summary>
    public const string AccountIdentityKeyRotated = "account-identity-key.rotated";

    /// <summary>A password reset left the master key's password wrapping undecryptable.</summary>
    public const string MasterKeyPasswordWrappingInvalidated = "master-key.password-wrapping-invalidated";

    public const string MasterKeyRewrapped = "master-key.rewrapped";
}

public class CreateIdentityAuditEventParams
{
    public string UserId { get; init; } = null!;
    public string Action { get; init; } = null!;
    public string? ClientDeviceId { get; init; }
    public string? Detail { get; init; }
    public string? IpAddress { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// An append-only record of the security-sensitive things that happened to an account.
/// </summary>
public class IdentityAuditEvent : BaseEntity<IdentityAuditEvent>, IPrefixedEntity
{
    public static string Prefix { get; } = "idau";

    public string UserId { get; set; } = null!;

    /// <summary>One of <see cref="IdentityAuditActions"/>.</summary>
    public string Action { get; set; } = null!;

    /// <summary>Client device id the action was performed from, when one was validated.</summary>
    public string? ClientDeviceId { get; set; }

    /// <summary>Short human-readable context, e.g. which device's blob was read. Never the blob.</summary>
    public string? Detail { get; set; }

    public string? IpAddress { get; set; }

    public static IdentityAuditEvent Create(CreateIdentityAuditEventParams parameters)
    {
        var date = parameters.CreatedAt == default ? DateTimeOffset.UtcNow : parameters.CreatedAt;
        return new IdentityAuditEvent
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            UserId = parameters.UserId,
            Action = parameters.Action,
            ClientDeviceId = parameters.ClientDeviceId,
            Detail = parameters.Detail,
            IpAddress = parameters.IpAddress,
        };
    }
}
