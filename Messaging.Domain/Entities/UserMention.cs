namespace Messaging.Domain.Entities;

/// <summary>How a message came to mention someone. Most specific wins when several apply.</summary>
public enum MentionKind
{
    /// <summary>Named outright with an @user.</summary>
    Direct,

    /// <summary>Present when an @here was sent.</summary>
    Here,
}

/// <summary>
/// One row of the per-user mention index: "this message named you, at this time".
/// </summary>
public class UserMention
{
    public string UserId { get; set; } = null!;

    /// <summary>The mentioning message's own CreatedAt - the clustering key, so a page is a
    /// contiguous slice of one partition.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public string MessageId { get; set; } = null!;

    /// <summary>Where the message lives: <c>ConversationId ??</summary>
    public string ContextId { get; set; } = null!;

    /// <summary>Null for DMs, which is also how the Mentions tab's include-DMs filter is served.</summary>
    public string? GuildId { get; set; }

    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }

    public string AuthorId { get; set; } = null!;

    /// <summary>Stored as its name rather than an ordinal: this crosses into Cassandra as text, and
    /// renumbering an enum should not silently reinterpret historical rows.</summary>
    public string Kind { get; set; } = nameof(MentionKind.Direct);
}
