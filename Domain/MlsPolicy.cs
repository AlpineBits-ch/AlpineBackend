namespace Domain;

/// <summary>
/// The rollout knobs for the MLS hardening work, in one place.
///
/// <para><b>Every value starts at the setting that leaves an unmodified old client working.</b>
/// There are clients in the field, and several of the hardening rules are breaking changes; each
/// knob here is one of those breaks, held shut until the fleet is ready. Flipping one is a
/// deliberate operational act taken on telemetry - which is why they are settable but nothing
/// automatic ever sets them.</para>
///
/// <para>Shared rather than per-service because two services read the same decisions: Identity
/// serves them to clients, and Messaging enforces the two that affect its own endpoints.</para>
/// </summary>
public static class MlsPolicy
{
    /// <summary>
    /// How strictly clients should act on a leaf whose device certificate is missing or invalid.
    ///
    /// <para>Starts at <see cref="CertificateEnforcement.Observe"/> and must: no device in the field
    /// has a certificate, so a client enforcing removal would propose evicting every other leaf in
    /// every group it is in. Advance only on the coverage number from the admin endpoint.</para>
    /// </summary>
    public static CertificateEnforcement CertificateEnforcement { get; set; } = CertificateEnforcement.Observe;

    /// <summary>Clients below this may keep using the pre-hardening contracts. Empty means "no floor
    /// yet", which is where the rollout starts.</summary>
    public static string MinClientVersion { get; set; } = "";

    /// <summary>When false, the Welcome fetch still serves the legacy no-<c>deviceId</c> call. It is
    /// already non-consuming, which is the half of the fix that removed the data loss; requiring the
    /// parameter is the half that breaks old clients, and it waits.</summary>
    public static bool RequireDeviceIdOnWelcomeFetch { get; set; }

    /// <summary>When false, creating an encrypted conversation with an unreachable member device
    /// succeeds and reports the devices rather than refusing. Old clients cannot pass the override
    /// flag, so refusing by default would take away their ability to create encrypted conversations
    /// entirely - a worse outcome than a partially covered conversation they are now told about.</summary>
    public static bool RejectUnreachableDevicesOnCreate { get; set; }
}
