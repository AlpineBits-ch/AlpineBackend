namespace Domain;

/// <summary>The capability strings a client reports at device registration.</summary>
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
/// </summary>
public enum CertificateEnforcement
{
    /// <summary>Allow everything, count what is missing, show nothing. The only safe initial state.</summary>
    Observe = 0,

    /// <summary>Allow, but mark unverified devices in the UI and warn on an invalid certificate.</summary>
    Warn = 1,

    /// <summary>Propose removal of a leaf with a missing or invalid certificate.</summary>
    Enforce = 2,
}
