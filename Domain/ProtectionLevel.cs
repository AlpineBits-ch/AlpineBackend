namespace Domain;

/// <summary>
/// How hard it is to get a new device into this account's encrypted conversations.
///
/// <para>Lives in the shared Domain project because two services need the same values for the same
/// decision: Identity stores the level, and Messaging refuses to mark a join request auto-admissible
/// without it. A copy on each side would drift, and the direction it drifts in is "the strict tier
/// silently reads as the permissive one".</para>
///
/// <para><b>Neither level trusts the server.</b> Both require the joining device to prove possession
/// of the account master key, which the server holds only in wrapped form. The difference is whether
/// that proof is <i>sufficient</i>; the server can no more forge a proof under
/// <see cref="TrustedSignIn"/> than under <see cref="VerifiedDevices"/>.</para>
/// </summary>
public enum ProtectionLevel
{
    /// <summary>
    /// Default. A device is admitted on proof of the account master key alone - no human has to be
    /// awake and holding a second device.
    ///
    /// <para>Its honest limitation, which the settings UI must state plainly: this reduces to the
    /// strength of the account password, because the master key is derived from it. It defends fully
    /// against a hostile server and against the network; it does not defend against someone who
    /// knows the password.</para>
    /// </summary>
    TrustedSignIn = 0,

    /// <summary>
    /// Opt-in. Proof of the master key <i>and</i> a human approving on an existing device.
    ///
    /// <para>Defends against someone who knows the password, at the cost of being unrecoverable if
    /// every device and the recovery code are lost - no password reset can restore end-to-end
    /// encrypted history. Also disables the reusable last-resort key package, since that is a
    /// forward-secrecy loss on join and is exactly what this tier is paying usability for.</para>
    /// </summary>
    VerifiedDevices = 1,
}
