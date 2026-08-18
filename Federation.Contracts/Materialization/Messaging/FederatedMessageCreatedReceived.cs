namespace Federation.Contracts.Materialization.Messaging;

/// <summary>A remote channel message plus the display identity it was rendered under - there is
/// deliberately no persona id, because a foreign one would be a dangling cross-instance
/// reference.</summary>
public class FederatedMessageCreatedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required byte[] Content { get; init; }

    /// <summary>Name the origin instance rendered this message under, or null for a plain user post.</summary>
    public string? AuthorDisplayName { get; init; }

    /// <summary>Avatar that goes with AuthorDisplayName.</summary>
    public string? AuthorAvatarUrl { get; init; }

    /// <summary>What kind of author spoke, so the receiving instance can badge a persona or webhook
    /// without owning a row for it.</summary>
    public FederatedAuthorIdType AuthorIdType { get; init; } = FederatedAuthorIdType.User;
}

public class FederatedMessageEditedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required byte[] Content { get; init; }

    /// <summary>Name the origin instance rendered this message under, or null for a plain user post.</summary>
    public string? AuthorDisplayName { get; init; }

    /// <summary>Avatar that goes with AuthorDisplayName.</summary>
    public string? AuthorAvatarUrl { get; init; }

    /// <summary>What kind of author spoke, so the receiving instance can badge a persona or webhook
    /// without owning a row for it.</summary>
    public FederatedAuthorIdType AuthorIdType { get; init; } = FederatedAuthorIdType.User;
}

public class FederatedMessageDeletedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
}

public class FederatedMessageReactionAddedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required string Emoji { get; init; }
}

public class FederatedMessageReactionRemovedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required string Emoji { get; init; }
}
