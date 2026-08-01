namespace Domain;

/// <summary>The rollout knobs for the MLS hardening work, in one place.</summary>
public static class MlsPolicy
{
    /// <summary>
    /// How strictly clients should act on a leaf whose device certificate is missing or invalid.
    /// </summary>
    public static CertificateEnforcement CertificateEnforcement { get; set; } = CertificateEnforcement.Observe;

    /// <summary>Clients below this may keep using the pre-hardening contracts.</summary>
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
