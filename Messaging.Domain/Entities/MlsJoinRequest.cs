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

/// <summary>
/// A device asking to be let into a context's MLS group.
///
/// <para>The server cannot admit anyone: only a current group member holds the keys to produce an
/// Add commit. So admission is a request that members review and approve, and the approval that
/// meets the threshold is what causes some member's client to mint the Welcome.</para>
///
/// <para><b>The approval is bound to exact bytes.</b> <see cref="KeyPackage"/> is the blob that will
/// be added, and <see cref="KeyPackageHash"/> is what the committing client checks before adding it.
/// If approvers vouched for a person in the abstract and the committer then pulled whatever key
/// package the server offered, a malicious server could substitute its own key between approval and
/// add - and the review would have bought nothing.</para>
///
/// <para><see cref="SignatureKeyFingerprint"/> is the separate, human-facing half: the requester's
/// long-lived identity key, stable across all their key packages, which is what a reviewer actually
/// compares out of band. A key-package hash changes on every request and so is useless to read out
/// over a phone call.</para>
/// </summary>
public class MlsJoinRequest : BaseEntity<MlsJoinRequest>, IPrefixedEntity
{
    public static string Prefix { get; } = "mljr";

    /// <summary>How long a request stays actionable. Long enough to catch a reviewer's next login,
    /// short enough that a stale request is not approved months later against a key the requester
    /// has since rotated away from.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>Approvals needed. Two people, so one compromised or careless account cannot let an
    /// impostor into an encrypted room on its own.</summary>
    public const int RequiredApprovals = 2;

    public string ContextId { get; set; } = null!;
    public string? ConversationId { get; set; }
    public string? ChannelId { get; set; }

    /// <summary>Which era of the context is being joined. A request against a generation that has
    /// since been terminated is void - that group no longer exists to be joined.</summary>
    public int Generation { get; set; }

    public string RequesterUserId { get; set; } = null!;

    /// <summary>Client device id. Admission is per device, not per user: each device holds its own
    /// leaf, so a user's second device needs its own request.</summary>
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

/// <summary>One member vouching for a request. Keyed per <i>user</i>, not per device - approving
/// from a phone and a laptop is one person twice, which is exactly what the threshold exists to
/// prevent.</summary>
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
