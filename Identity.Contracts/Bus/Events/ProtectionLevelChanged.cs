using Domain;

namespace Identity.Contracts.Bus.Events;

/// <summary>The account's device-admission protection level changed.</summary>
public class ProtectionLevelChanged
{
    public string UserId { get; init; } = null!;

    public ProtectionLevel PreviousLevel { get; init; }
    public ProtectionLevel Level { get; init; }

    public int Version { get; init; }

    /// <summary>The signed assertion, so a device can verify the new level without a second call.</summary>
    public byte[]? SignedAssertion { get; init; }

    /// <summary>Client device id that made the change, so the others can tell whether it was them.</summary>
    public string? ChangedByDeviceId { get; init; }

    /// <summary>True for <see cref="ProtectionLevel.VerifiedDevices"/> to
    /// <see cref="ProtectionLevel.TrustedSignIn"/>. The case clients must surface, not just log.</summary>
    public bool IsDowngrade { get; init; }

    public DateTimeOffset ChangedAt { get; init; }
}
