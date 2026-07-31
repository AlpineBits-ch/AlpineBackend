using Messaging.Domain.Aggregates;
using Messaging.Domain.Enums;
using Persistence;

namespace Messaging.Domain.Entities;

public class CreateMlsJoinRequestParams
{
    public string ContextId { get; init; } = null!;
    public string? ConversationId { get; init; }
    public string? ChannelId { get; init; }
    public int Generation { get; init; }
    public string RequesterUserId { get; init; } = null!;
    public string RequesterDeviceId { get; init; } = null!;
    public byte[] KeyPackage { get; init; } = null!;
    public string KeyPackageHash { get; init; } = null!;
    public string SignatureKeyFingerprint { get; init; } = null!;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>A device asking to be let into a context's MLS group.</summary>
public class MlsJoinRequest : BaseEntity<MlsJoinRequest>, IPrefixedEntity
{
    public static string Prefix { get; } = "mljr";

    /// <summary>How long a request stays actionable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>Approvals needed.</summary>
    public const int RequiredApprovals = 2;

    public string ContextId { get; set; } = null!;
    public string? ConversationId { get; set; }
    public string? ChannelId { get; set; }

    /// <summary>Which era of the context is being joined.</summary>
    public int Generation { get; set; }

    public string RequesterUserId { get; set; } = null!;

    /// <summary>Client device id.</summary>
    public string RequesterDeviceId { get; set; } = null!;

    /// <summary>The exact TLS-serialized KeyPackage to be added.</summary>
    public byte[] KeyPackage { get; set; } = null!;

    /// <summary>SHA-256 of <see cref="KeyPackage"/>. What binds an approval to these bytes.</summary>
    public string KeyPackageHash { get; set; } = null!;

    /// <summary>Fingerprint of the requester's long-lived signature key - the value a reviewer
    /// compares with the requester out of band.</summary>
    public string SignatureKeyFingerprint { get; set; } = null!;

    public MlsJoinRequestState State { get; set; } = MlsJoinRequestState.Pending;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when a commit actually admitted this device.</summary>
    public DateTimeOffset? FulfilledAt { get; set; }

    public string? DeniedByUserId { get; set; }
    public DateTimeOffset? DeniedAt { get; set; }

    public virtual ICollection<MlsJoinRequestApproval> Approvals { get; set; } = new List<MlsJoinRequestApproval>();

    public bool IsActionableAt(DateTimeOffset now) =>
        State == MlsJoinRequestState.Pending && ExpiresAt > now;

    public static MlsJoinRequest Create(CreateMlsJoinRequestParams parameters)
    {
        return new MlsJoinRequest
        {
            Id = GenerateId(),
            CreatedAt = parameters.CreatedAt,
            UpdatedAt = parameters.CreatedAt,
            ContextId = parameters.ContextId,
            ConversationId = parameters.ConversationId,
            ChannelId = parameters.ChannelId,
            Generation = parameters.Generation,
            RequesterUserId = parameters.RequesterUserId,
            RequesterDeviceId = parameters.RequesterDeviceId,
            KeyPackage = parameters.KeyPackage,
            KeyPackageHash = parameters.KeyPackageHash,
            SignatureKeyFingerprint = parameters.SignatureKeyFingerprint,
            State = MlsJoinRequestState.Pending,
            ExpiresAt = parameters.ExpiresAt,
        };
    }
}

/// <summary>One member vouching for a request.</summary>
public class MlsJoinRequestApproval : BaseEntity<MlsJoinRequestApproval>, IPrefixedEntity
{
    public static string Prefix { get; } = "mlja";

    public string JoinRequestId { get; set; } = null!;
    public virtual MlsJoinRequest JoinRequest { get; set; } = null!;

    public string ApproverUserId { get; set; } = null!;

    public static MlsJoinRequestApproval Create(string joinRequestId, string approverUserId, DateTimeOffset now)
    {
        return new MlsJoinRequestApproval
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            JoinRequestId = joinRequestId,
            ApproverUserId = approverUserId,
        };
    }
}
