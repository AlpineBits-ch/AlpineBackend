namespace Guild.Contracts.Bus.Events;

/// <summary>
/// "These users should get a push for this channel message." Published by Guild (which owns
/// membership, presence and the notification settings needed to decide who) and consumed by
/// Messaging (which owns the Firebase credentials and the existing push plumbing, so it decides
/// how).
/// </summary>
public class ChannelPushRequested
{
    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string MessageId { get; set; } = null!;

    /// <summary>Who to notify.</summary>
    public List<string> UserIds { get; set; } = [];

    /// <summary>Author, for the notification title.</summary>
    public string AuthorId { get; set; } = null!;

    /// <summary>Plain-text body.</summary>
    public byte[] Content { get; set; } = [];

    public bool IsEncrypted { get; set; }
}
