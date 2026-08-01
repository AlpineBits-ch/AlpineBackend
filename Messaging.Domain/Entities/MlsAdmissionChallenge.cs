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
/// </summary>
public class MlsAdmissionChallenge : BaseEntity<MlsAdmissionChallenge>, IPrefixedEntity
{
    public static string Prefix { get; } = "mlac";

    /// <summary>How long a challenge stays answerable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Required nonce length.</summary>
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

    /// <summary>32 random bytes chosen by the issuing device.</summary>
    public byte[] Challenge { get; set; } = null!;

    /// <summary>The joining device's signature over
    /// <c>challenge || requesterDeviceId || signatureKeyFingerprint</c>. Opaque here.</summary>
    public byte[]? Proof { get; set; }

    public DateTimeOffset? ProofSubmittedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Answering consumes the challenge.</summary>
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
