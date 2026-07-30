namespace Guild.Domain.Enums;

public enum ChannelType
{
    Text,
    Voice,
    Forum,
    Ticket,
    Announcement,
    Thread,

    /// <summary>
    /// A forum variant intended to be rendered media-forward (Discord's Media channel).
    /// </summary>
    Media
}

public static class ChannelTypeExtensions
{
    /// <summary>Forum and Media are the same channel behaviourally - only the client's intended
    /// rendering differs - so every forum-side check goes through this rather than spelling out
    /// the pair and eventually missing one.</summary>
    public static bool IsForum(this ChannelType type) => type is ChannelType.Forum or ChannelType.Media;
}