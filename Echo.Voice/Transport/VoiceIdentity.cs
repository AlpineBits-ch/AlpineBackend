using Echo.Realtime.LiveKit;

namespace Echo.Voice.Transport;

/// <summary>How a user is named to the SFU, in the neutral vocabulary the rooms use.</summary>
public static class VoiceIdentity
{
    /// <summary>The identity carrying a user's microphone. One per user per room.</summary>
    public static string Primary(string userId) => LiveKitIdentity.Primary(userId);

    /// <summary>An additional connection belonging to the same user, tagged so it cannot evict their
    /// primary one.</summary>
    public static string Secondary(string userId, string? tag) =>
        LiveKitIdentity.Secondary(userId, tag ?? "screen");

    /// <summary>The user behind an identity, whichever kind it is.</summary>
    public static string UserOf(string identity) => LiveKitIdentity.UserOf(identity);

    /// <summary>Whether this identity is the one carrying the user's microphone.</summary>
    public static bool IsPrimary(string identity) => LiveKitIdentity.IsPrimary(identity);
}
