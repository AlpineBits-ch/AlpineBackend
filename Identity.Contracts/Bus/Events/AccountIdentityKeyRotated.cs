namespace Identity.Contracts.Bus.Events;

/// <summary>The account identity key was replaced.</summary>
public class AccountIdentityKeyRotated
{
    public string UserId { get; init; } = null!;

    public int PreviousVersion { get; init; }
    public int Version { get; init; }

    public byte[] PublicKey { get; init; } = null!;

    /// <summary>False when the outgoing key was gone and continuity could not be proven.</summary>
    public bool SignedByOutgoingKey { get; init; }

    public string? ChangedByDeviceId { get; init; }

    public DateTimeOffset RotatedAt { get; init; }
}
