namespace Identity.Contracts.Bus.Events;

/// <summary>
/// The account identity key was replaced.
///
/// <para>A security event, not a routine write: it invalidates every peer's pinning and every device
/// certificate issued under the old key. Broadcast to all of the user's devices so a rotation nobody
/// on the account performed is visible immediately rather than discovered when peers start showing
/// safety-number warnings.</para>
/// </summary>
public class AccountIdentityKeyRotated
{
    public string UserId { get; init; } = null!;

    public int PreviousVersion { get; init; }
    public int Version { get; init; }

    public byte[] PublicKey { get; init; } = null!;

    /// <summary>False when the outgoing key was gone and continuity could not be proven. Peers must
    /// then re-verify out of band; auto-accepting would make rotation a way in.</summary>
    public bool SignedByOutgoingKey { get; init; }

    public string? ChangedByDeviceId { get; init; }

    public DateTimeOffset RotatedAt { get; init; }
}
