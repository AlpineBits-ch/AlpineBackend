namespace Messaging.Contracts.Bus.Commands;

/// <summary>One recipient of a mention, and how they came to be mentioned.</summary>
public class MentionRecipient
{
    public required string UserId { get; init; }

    /// <summary>`Direct` or `Here`. The name, not an ordinal - it crosses into Cassandra as text and
    /// renumbering an enum should not reinterpret historical rows.</summary>
    public required string Kind { get; init; }
}

/// <summary>
/// Writes mention-index rows for the users a message named.
///
/// <para>Offloaded rather than done inline, so the message-create path returns as soon as the
/// message is stored and the fan-out gets Wolverine's retry and error-queue handling for free.
/// Chunked at <see cref="MaxRecipients"/>, so the largest case is several ordinary messages rather
/// than one enormous one.</para>
///
/// <para>Only direct and @here mentions arrive here. @everyone and @role are one row each on the
/// Guild side - their recipients are reconstructable at read time, so materializing them per user
/// would put the write cost of a single ping in proportion to the size of the guild.</para>
/// </summary>
public class IndexMentionsCommand
{
    /// <summary>Recipients per command. An @here in a busy guild is bounded by how many people are
    /// connected, which is bounded by what the presence layer can hold - but that is still worth
    /// splitting rather than sending as one message.</summary>
    public const int MaxRecipients = 500;

    public required string MessageId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Where the message lives - <c>ConversationId ?? ChannelId</c>, matching how the
    /// message itself derives its partition.</summary>
    public required string ContextId { get; init; }

    public string? GuildId { get; init; }
    public string? ChannelId { get; init; }
    public string? ConversationId { get; init; }

    public required string AuthorId { get; init; }

    public required IReadOnlyList<MentionRecipient> Recipients { get; init; }
}
