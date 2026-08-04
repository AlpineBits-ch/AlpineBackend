namespace Domain;

/// <summary>
/// Which direct-message attachments get run past a media classifier before they are shown.
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
