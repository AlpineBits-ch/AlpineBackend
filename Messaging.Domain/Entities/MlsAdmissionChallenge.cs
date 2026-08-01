using Persistence;

namespace Messaging.Domain.Entities;

public class CreateMlsAdmissionChallengeParams
{
    public string JoinRequestId { get; init; } = null!;
    public string ContextId { get; init; } = null!;
    public string IssuedByUserId { get; init; } = null!;
    public string? IssuedByDeviceId { get; init; }
    public byte[] Challenge { get; init; } = null!;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// One round of the device-admission proof: a nonce issued by an existing device, and the joining
/// device's signature over it.
///
/// <para><b>The server is a relay and must remain incapable of being anything else.</b> The proof is
/// made with a key derived from the account master key, which the server holds only in wrapped form
/// - so it cannot produce a valid proof for a device it injected, and it does not attempt to check
/// the ones it carries. Verification happens on the existing device, which holds the master key too.
/// A server that validated proofs would be asserting something it cannot know, and a client that
/// trusted that assertion would have handed back exactly the power this design removes.</para>
///
/// <para>Single-use and short-lived, both enforced here rather than left to the clients: a nonce
/// that can be replayed is not a nonce, and a proof that stays valid indefinitely turns one
/// intercepted signature into a permanent admission ticket.</para>
/// </summary>
public class MlsAdmissionChallenge : BaseEntity<MlsAdmissionChallenge>, IPrefixedEntity
{
    public static string Prefix { get; } = "mlac";

    /// <summary>How long a challenge stays answerable. Fifteen minutes is long enough for a device
    /// that has to be unlocked and typed into, short enough that an intercepted proof is worthless
    /// by the time anyone could use it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Required nonce length. Fixed rather than "at least", so a client cannot weaken its
    /// own challenge by sending four bytes and have the server carry it anyway.</summary>
    public const int ChallengeLength = 32;

    public string JoinRequestId { get; set; } = null!;
    public virtual MlsJoinRequest JoinRequest { get; set; } = null!;

    /// <summary>Carried alongside the request id so the "is this challenge for this context" check
    /// does not need a join.</summary>
    public string ContextId { get; set; } = null!;

    public string IssuedByUserId { get; set; } = null!;

    /// <summary>Client device id that issued it, so the answering device can tell which of the
    /// user's devices is waiting on it.</summary>
    public string? IssuedByDeviceId { get; set; }

    /// <summary>32 random bytes chosen by the issuing device. The server neither generates nor
    /// inspects it - generating it here would let the server pick a nonce it had precomputed
    /// against.</summary>
    public byte[] Challenge { get; set; } = null!;

    /// <summary>The joining device's signature over
    /// <c>challenge || requesterDeviceId || signatureKeyFingerprint</c>. Opaque here.</summary>
    public byte[]? Proof { get; set; }

    public DateTimeOffset? ProofSubmittedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Answering consumes the challenge. A second proof against the same nonce is refused
    /// rather than overwriting - two different signatures over one challenge means one of them did
    /// not come from the device that should have made it.</summary>
    public bool IsAnswerableAt(DateTimeOffset now) => Proof is null && ExpiresAt > now;

    public bool IsUsableProofAt(DateTimeOffset now) => Proof is { Length: > 0 } && ExpiresAt > now;

    public static MlsAdmissionChallenge Create(CreateMlsAdmissionChallengeParams parameters)
    {
        var date = parameters.CreatedAt == default ? DateTimeOffset.UtcNow : parameters.CreatedAt;
        return new MlsAdmissionChallenge
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            JoinRequestId = parameters.JoinRequestId,
            ContextId = parameters.ContextId,
            IssuedByUserId = parameters.IssuedByUserId,
            IssuedByDeviceId = parameters.IssuedByDeviceId,
            Challenge = parameters.Challenge,
            ExpiresAt = parameters.ExpiresAt,
        };
    }
}
