namespace Domain;

/// <summary>
/// How hard it is to get a new device into this account's encrypted conversations.
/// </summary>
public enum ProtectionLevel
{
    /// <summary>Default.</summary>
    TrustedSignIn = 0,

    /// <summary>Opt-in.</summary>
    VerifiedDevices = 1,
}
