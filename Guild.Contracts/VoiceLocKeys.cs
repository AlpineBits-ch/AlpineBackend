namespace Guild.Contracts;

/// <summary>The localization keys a voice-channel ring notification is written in.</summary>
public static class VoiceLocKeys
{
    /// <summary>"Asked you to join {0}." - {0} is the voice channel's name.</summary>
    public const string InviteBody = "voice_ring_invite_body";

    /// <summary>"Voice invite" - the title a recipient with HidePushContent gets instead of the
    /// inviter's name (privacy spec T2-23).</summary>
    public const string HiddenTitle = "voice_ring_hidden_title";

    /// <summary>"Someone asked you to join a voice channel" - deliberately names neither the person,
    /// the channel nor the server. Hiding push content is not satisfied by hiding only the half of
    /// the notification that happens to be a sentence.</summary>
    public const string HiddenBody = "voice_ring_hidden_body";

    /// <summary>Every key this feature is allowed to send.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        InviteBody,
        HiddenTitle,
        HiddenBody,
    };
}
