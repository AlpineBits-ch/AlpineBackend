using Persistence;

namespace Identity.Domain.Entities;

public class CreateUserBackupTransferParams
{
    public string UserId { get; init; } = null!;
    public string SourceDeviceId { get; init; } = null!;
    public string TargetDeviceId { get; init; } = null!;
    public byte[] WrappedTo { get; init; } = null!;
    public byte[] CipherText { get; init; } = null!;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// A one-shot handover of key material from one of a user's devices to another, wrapped to the
/// target's public key so the server is a courier and nothing more.
/// </summary>
public class UserBackupTransfer : BaseEntity<UserBackupTransfer>, IPrefixedEntity
{
    public static string Prefix { get; } = "ubtr";

    /// <summary>Longest a transfer may be left sitting.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    public string UserId { get; set; } = null!;

    /// <summary>Client device id of the device that created the transfer.</summary>
    public string SourceDeviceId { get; set; } = null!;

    /// <summary>Client device id of the only device permitted to claim it.</summary>
    public string TargetDeviceId { get; set; } = null!;

    /// <summary>The target's public key the payload was wrapped to, echoed back on claim so the
    /// receiving device can confirm it is looking at something sealed for the key it still holds
    /// rather than one it has since rotated away from.</summary>
    public byte[] WrappedTo { get; set; } = null!;

    public byte[] CipherText { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsClaimableAt(DateTimeOffset now) => ExpiresAt > now;

    public static UserBackupTransfer Create(CreateUserBackupTransferParams parameters)
    {
        var date = parameters.CreatedAt == default ? DateTimeOffset.UtcNow : parameters.CreatedAt;
        return new UserBackupTransfer
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            UserId = parameters.UserId,
            SourceDeviceId = parameters.SourceDeviceId,
            TargetDeviceId = parameters.TargetDeviceId,
            WrappedTo = parameters.WrappedTo,
            CipherText = parameters.CipherText,
            ExpiresAt = parameters.ExpiresAt,
        };
    }
}
