using Domain;

namespace Identity.Contracts.Bus.Events;

/// <summary>
/// The account's device-admission protection level changed.
///
/// <para>Broadcast to every one of the user's devices, unconditionally. A downgrade is the move an
/// attacker holding a session makes before admitting a device of their own, so a client that sees
/// one it did not participate in is supposed to warn loudly - which it can only do if it is told.
/// Identity hosts no SignalR hub, so this takes the bus to a service that does.</para>
/// </summary>
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
