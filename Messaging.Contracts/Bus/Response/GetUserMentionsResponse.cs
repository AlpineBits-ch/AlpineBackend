namespace Messaging.Contracts.Bus.Response;

/// <summary>One indexed mention. A pointer, not the message - the body is fetched separately for the
/// page actually being rendered, which is what keeps ciphertext out of a second store and stops
/// edits and deletes going stale here.</summary>
public class UserMentionEntry
{
    public required string UserId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string MessageId { get; init; }
    public required string ContextId { get; init; }

    /// <summary>Null for DMs.</summary>
    public string? GuildId { get; init; }

    public string? ChannelId { get; init; }
    public string? ConversationId { get; init; }
    public required string AuthorId { get; init; }

    /// <summary>`Direct` or `Here`.</summary>
    public required string Kind { get; init; }
}

public class GetUserMentionsResponse
{
    public required IReadOnlyList<UserMentionEntry> Mentions { get; init; }
}
