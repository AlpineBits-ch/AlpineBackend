namespace Messaging.Domain.Entities;

/// <summary>How a message came to mention someone. Most specific wins when several apply.</summary>
public enum MentionKind
{
    /// <summary>Named outright with an @user.</summary>
    Direct,

    /// <summary>Present when an @here was sent. Recorded per user rather than as one broadcast row
    /// because its audience is the presence snapshot, which no longer exists by read time.</summary>
    Here,
}

/// <summary>
/// One row of the per-user mention index: "this message named you, at this time".
///
/// <para>Exists because messages are partitioned by context and there is no secondary index on the
/// mentions collection - so "every message that mentioned me, across every guild" is unanswerable
/// from the message table at any price. Same shape as the pinned_messages lookup table, for the same
/// reason.</para>
///
/// <para><b>Only direct and @here mentions land here.</b> @everyone and @role are recorded once per
/// message in Guild instead: their recipients are reconstructable from durable state at read time,
/// so materialising them per user would make a single @everyone cost one row per member. @here is
/// the exception because presence is volatile and gone by the time anything reads it.</para>
///
/// <para><b>No content column.</b> The row is a pointer; the body is fetched from the message table
/// for the page being rendered. That keeps E2EE material out of a second place and stops edits and
/// deletes from going stale in the index.</para>
/// </summary>
public class UserMention
{
    public string UserId { get; set; } = null!;

    /// <summary>The mentioning message's own CreatedAt - the clustering key, so a page is a
    /// contiguous slice of one partition.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public string MessageId { get; set; } = null!;

    /// <summary>Where the message lives: <c>ConversationId ?? ChannelId</c>, the same derivation the
    /// message itself uses. Enough to fetch the body back.</summary>
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
