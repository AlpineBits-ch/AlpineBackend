namespace Messaging.Contracts.Bus.Commands;

/// <summary>One recipient of a mention, and how they came to be mentioned.</summary>
public class MentionRecipient
{
    public required string UserId { get; init; }

    /// <summary>`Direct` or `Here`.</summary>
    public required string Kind { get; init; }
}

/// <summary>Writes mention-index rows for the users a message named.</summary>
public class IndexMentionsCommand
{
    /// <summary>Recipients per command.</summary>
    public const int MaxRecipients = 500;

    public required string MessageId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Where the message lives - <c>ConversationId ??</summary>
    public required string ContextId { get; init; }

    public string? GuildId { get; init; }
    public string? ChannelId { get; init; }
    public string? ConversationId { get; init; }

    public required string AuthorId { get; init; }

    public required IReadOnlyList<MentionRecipient> Recipients { get; init; }
}
