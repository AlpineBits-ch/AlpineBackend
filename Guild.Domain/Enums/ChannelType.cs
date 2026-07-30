namespace Guild.Domain.Enums;

public enum ChannelType
{
    Text,
    Voice,
    Forum,
    Ticket,
    Announcement,
    Thread,

    /// <summary>A forum variant intended to be rendered media-forward (Discord's Media channel).
    /// Behaviourally identical to Forum everywhere in this service - same posts, same tags, same
    /// config - so every forum check is written as "Forum or Media".
    /// Appended last deliberately: Npgsql maps this enum by name, and appending is the only
    /// addition Postgres can make to an existing enum type without a rewrite.</summary>
    Media
}

public static class ChannelTypeExtensions
{
    /// <summary>Forum and Media are the same channel behaviourally - only the client's intended
    /// rendering differs - so every forum-side check goes through this rather than spelling out
    /// the pair and eventually missing one.</summary>
    public static bool IsForum(this ChannelType type) => type is ChannelType.Forum or ChannelType.Media;
}