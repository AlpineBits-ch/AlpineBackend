namespace Domain;

/// <summary>
/// The capability strings a client reports at device registration.
///
/// <para>Shared rather than duplicated per service because two independent decisions read them:
/// Identity gates the strict protection tier on every active device declaring
/// <see cref="ProtectionLevel"/> support, and the certificate-coverage telemetry that decides when
/// enforcement may be turned on counts <see cref="DeviceCertificateV1"/>.</para>
///
/// <para>Capabilities, not a version number, because a version only answers "can this device do X"
/// through a lookup table somebody has to maintain - and the failure mode of a stale table is an
/// account promoted into a tier one of its devices cannot enforce.</para>
/// </summary>
public static class MlsCapabilities
{
    /// <summary>Issues and validates account-signed device certificates (§H.2).</summary>
    public const string DeviceCertificateV1 = "mls.device-cert.v1";

    /// <summary>Submits and reviews conversation-scoped join requests (§B).</summary>
    public const string ConversationJoinRequestV1 = "mls.join-request.conversation.v1";

    /// <summary>Verifies the signed protection-level assertion and fails closed when it cannot (§G).</summary>
    public const string ProtectionLevelV1 = "mls.protection-level.v1";

    /// <summary>Reads and writes the encrypted backup blob (§C).</summary>
    public const string BackupV1 = "mls.backup.v1";
}

/// <summary>
/// How strictly clients should act on a leaf whose device certificate is missing or invalid.
///
/// <para><b>Server-supplied, never inferred from client version.</b> No device in the field has a
/// certificate yet, so a client that shipped the removal rule on its own judgement would start
/// proposing the removal of every other leaf in every group it is in - including its owner's other
/// devices. One early client must not be able to do that, so the phase comes from the server and
/// defaults to <see cref="Observe"/> whenever the policy cannot be read or parsed.</para>
/// </summary>
public enum CertificateEnforcement
{
    /// <summary>Allow everything, count what is missing, show nothing. The only safe initial state.</summary>
    Observe = 0,

    /// <summary>Allow, but mark unverified devices in the UI and warn on an invalid certificate.</summary>
    Warn = 1,

    /// <summary>Propose removal of a leaf with a missing or invalid certificate. Only once coverage
    /// telemetry says essentially every active device carries one.</summary>
    Enforce = 2,
}
