using Microsoft.Extensions.Configuration;

namespace Domain;

/// <summary>The rollout knobs for the MLS hardening work, in one place.</summary>
public static class MlsPolicy
{
    /// <summary>Configuration section, e.g. <c>Mls:CertificateEnforcement=Enforce</c> or the
    /// environment variable <c>MLS__CERTIFICATEENFORCEMENT</c>.</summary>
    public const string SectionName = "Mls";

    /// <summary>Applies configured values over the defaults.</summary>
    public static void Bind(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        if (Enum.TryParse<CertificateEnforcement>(
                section[nameof(CertificateEnforcement)], ignoreCase: true, out var enforcement))
        {
            CertificateEnforcement = enforcement;
        }

        if (section[nameof(MinClientVersion)] is { Length: > 0 } minVersion)
            MinClientVersion = minVersion;

        if (bool.TryParse(section[nameof(RequireDeviceIdOnWelcomeFetch)], out var requireDeviceId))
            RequireDeviceIdOnWelcomeFetch = requireDeviceId;

        if (bool.TryParse(section[nameof(RejectUnreachableDevicesOnCreate)], out var rejectUnreachable))
            RejectUnreachableDevicesOnCreate = rejectUnreachable;

        if (bool.TryParse(section[nameof(ServeGroupInfoToNonParticipants)], out var serveGroupInfo))
            ServeGroupInfoToNonParticipants = serveGroupInfo;
    }

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

    /// <summary>
    /// Whether a context's live <c>GroupInfo</c> may be served to a caller with no evidence of ever
    /// having been in the group.
    /// </summary>
    public static bool ServeGroupInfoToNonParticipants { get; set; }
}
