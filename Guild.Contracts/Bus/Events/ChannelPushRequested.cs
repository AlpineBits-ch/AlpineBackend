namespace Guild.Contracts.Bus.Events;

/// <summary>
/// "These users should get a push for this channel message."
///
/// Published by Guild (which owns membership, presence and the notification settings needed to
/// decide *who*) and consumed by Messaging (which owns the Firebase credentials and the existing
/// push plumbing, so it decides *how*). Splitting it this way keeps Firebase initialization in
/// exactly one service instead of a second copy in Guild, and mirrors how MessageCreatedForChannel
/// already crosses the same boundary in the other direction.
///
/// Recipients are fully resolved before publishing - this event is a send list, not a request to
/// work out a send list, so Messaging never needs to reach back into Guild's data.
/// </summary>
public class ChannelPushRequested
{
    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string MessageId { get; set; } = null!;

    /// <summary>Who to notify. Already filtered by notification level, mute state, the mobile-push
    /// switch, and current connectedness.</summary>
    public List<string> UserIds { get; set; } = [];

    /// <summary>Author, for the notification title. Resolved to a display name by Messaging, which
    /// already makes that profile call for the DM push path.</summary>
    public string AuthorId { get; set; } = null!;

    /// <summary>Plain-text body. Empty for an encrypted message, where Messaging substitutes its
    /// own placeholder exactly as it does for encrypted DMs.</summary>
    public byte[] Content { get; set; } = [];

    public bool IsEncrypted { get; set; }
}
