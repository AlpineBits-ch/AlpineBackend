namespace Domain;

/// <summary>
/// Which direct-message attachments get run past a media classifier before they are shown.
///
/// <para>No classifier is wired in yet; the setting exists so the enforcement point can exist and
/// fail closed before one is. See T2-20 in docs/specs/privacy.md.</para>
/// </summary>
public enum ExplicitContentFilter
{
    /// <summary>Nothing is filtered.</summary>
    Off,

    /// <summary>Attachments from people the account is not friends with. The default.</summary>
    UnknownSenders,

    /// <summary>Every attachment, friends included.</summary>
    Everyone,
}
