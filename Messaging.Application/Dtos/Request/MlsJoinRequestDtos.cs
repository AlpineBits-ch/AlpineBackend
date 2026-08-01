using Facet;
using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Request;

/// <summary>A device asking to be admitted to an encrypted context.</summary>
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

    /// <summary>Human-readable device name, carried so the approval prompt can say "Alice's new
    /// phone" rather than an opaque id. Display only - nothing is authorized on it.</summary>
    public string? DeviceName { get; set; }
}

[Facet(typeof(MlsJoinRequest),
    nameof(MlsJoinRequest.Approvals),
    nameof(MlsJoinRequest.KeyPackage))]
public partial class MlsJoinRequestDto
{
    /// <summary>How many approvals this request needs, resolved at read time.</summary>
    public int RequiredApprovals { get; set; }

    /// <summary>Who has already vouched.</summary>
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

/// <summary>An existing device's nonce for the joining device to sign.</summary>
public class IssueAdmissionChallengeDto
{
    /// <summary>Exactly 32 random bytes.</summary>
    public byte[] Challenge { get; set; } = null!;
}

public class SubmitAdmissionProofDto
{
    public string ChallengeId { get; set; } = null!;

    /// <summary>Signature over <c>challenge || requesterDeviceId || signatureKeyFingerprint</c> with
    /// a key derived from the account master key (HKDF, info <c>venta.device-admission.v1</c>).
    /// Opaque to the server, which cannot produce one and does not check it.</summary>
    public byte[] Proof { get; set; } = null!;
}

public class MlsAdmissionChallengeDto
{
    public string ChallengeId { get; set; } = null!;
    public string RequestId { get; set; } = null!;

    /// <summary>Null on the proof-submission response - echoing the nonce back to the device that
    /// just answered it serves nothing.</summary>
    public byte[]? Challenge { get; set; }

    public string? IssuedByDeviceId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Answered { get; set; }
}

/// <summary>Everything the verifying device needs, in one payload.</summary>
public class MlsAdmissionProofDto
{
    public string RequestId { get; set; } = null!;
    public string ChallengeId { get; set; } = null!;
    public byte[] Challenge { get; set; } = null!;
    public byte[]? Proof { get; set; }
    public string RequesterDeviceId { get; set; } = null!;
    public string SignatureKeyFingerprint { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
}
