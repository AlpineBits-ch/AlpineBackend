using Microsoft.Extensions.Configuration;

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
///
/// <para><b>Bound from configuration at startup</b> - see <see cref="Bind"/>. Properties on a static
/// class are settable in the language sense and that is not the same as being <i>operable</i>: with
/// nothing reading configuration, the certificate-enforcement phase could only be moved by editing
/// the default and redeploying, and two instances started from different builds would disagree about
/// it. §I.1 calls this a kill switch, so it has to be one.</para>
/// </summary>
public static class MlsPolicy
{
    /// <summary>Configuration section, e.g. <c>Mls:CertificateEnforcement=Enforce</c> or the
    /// environment variable <c>MLS__CERTIFICATEENFORCEMENT</c>.</summary>
    public const string SectionName = "Mls";

    /// <summary>
    /// Applies configured values over the defaults. Anything absent or unparsable keeps its default,
    /// which is the safe end of every knob: a typo in a deployment variable must not be able to
    /// switch certificate enforcement on across the fleet.
    /// </summary>
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

    /// <summary>
    /// Whether a context's live <c>GroupInfo</c> may be served to a caller with no evidence of ever
    /// having been in the group.
    ///
    /// <para><b>Defaults to false, and there is no good reason to turn it on.</b> A GroupInfo is
    /// everything needed to external-commit into a group, and the channel state route required only
    /// <c>ViewChannel</c> - so any channel viewer could walk straight in and bypass the join-request
    /// review that exists to stop exactly that. The only defence on the client side is gated on
    /// <see cref="CertificateEnforcement"/>, which starts at Observe and which the server itself
    /// serves; a switch a malicious server controls is not a defence.</para>
    ///
    /// <para>The knob exists because the restriction is a breaking change for any client whose
    /// rejoin-after-state-loss path relies on fetching a GroupInfo it is no longer entitled to, and
    /// an operator debugging that needs a way to confirm the cause. It is not a rollout stage.</para>
    /// </summary>
    public static bool ServeGroupInfoToNonParticipants { get; set; }
}
