using Facet;
using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Request;

/// <summary>
/// A device asking to be admitted to an encrypted context.
///
/// <para>The key package travels with the request so reviewers approve exact bytes. Letting the
/// committer pull a key package separately would leave a window in which the server could
/// substitute one, and the whole review would guarantee nothing.</para>
/// </summary>
public class SubmitJoinRequestDto
{
    /// <summary>TLS-serialized KeyPackage for the requesting device.</summary>
    public byte[] KeyPackage { get; set; } = null!;

    /// <summary>Requester's client device id - admission is per device, since each holds its own leaf.</summary>
    public string DeviceId { get; set; } = null!;

    /// <summary>Fingerprint of the requester's long-lived signature key, for out-of-band comparison.
    /// Derived from the key package by the client; the committing client re-derives and checks it
    /// rather than trusting this value.</summary>
    public string SignatureKeyFingerprint { get; set; } = null!;
}

[Facet(typeof(MlsJoinRequest),
    nameof(MlsJoinRequest.Approvals),
    nameof(MlsJoinRequest.KeyPackage))]
public partial class MlsJoinRequestDto
{
    /// <summary>How many approvals this request needs, resolved at read time.</summary>
    public int RequiredApprovals { get; set; }

    /// <summary>Who has already vouched. Lets a reviewer see they have approved, and see who else
    /// did - a second opinion is only worth something if you can tell whose it was.</summary>
    public List<string> ApproverUserIds { get; set; } = new();
}

public class MlsJoinRequestApprovalResultDto
{
    public string RequestId { get; set; } = null!;
    public int Approvals { get; set; }
    public int RequiredApprovals { get; set; }

    /// <summary>True when this approval completed the threshold and the caller should now mint the
    /// Add commit.</summary>
    public bool ThresholdMet { get; set; }

    /// <summary>The exact bytes to add. Only present once <see cref="ThresholdMet"/>.</summary>
    public byte[]? KeyPackage { get; set; }

    /// <summary>SHA-256 of the key package, so the committer can verify before adding.</summary>
    public string KeyPackageHash { get; set; } = null!;

    public int Generation { get; set; }
}

public class MlsJoinRequestConflictDto
{
    public string RequestId { get; set; } = null!;
    public string State { get; set; } = null!;
    public string Reason { get; set; } = null!;
}
