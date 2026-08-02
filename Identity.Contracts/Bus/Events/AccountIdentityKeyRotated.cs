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

    /// <summary>
    /// True when the account had no identity key at all until this write.
    ///
    /// <para>Announced as loudly as a rotation, and for the same reason. Whoever publishes first
    /// becomes the account's cryptographic identity to every peer that TOFU-pins it, and until this
    /// change that cost one session token and left no trace anywhere - no password, no audit row, no
    /// broadcast. A first publication the owner did not perform is exactly as serious as a rotation
    /// they did not perform, so it is exactly as visible.</para>
    /// </summary>
    public bool IsFirstPublication { get; init; }

    public DateTimeOffset RotatedAt { get; init; }
}
