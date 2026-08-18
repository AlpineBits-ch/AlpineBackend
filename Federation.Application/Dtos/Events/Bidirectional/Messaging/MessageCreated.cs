using Federation.Contracts.Materialization.Messaging;

namespace Federation.Application.Dtos.Events.Bidirectional.Messaging;

public class MessageCreated : FederationEvent
{
    public string MessageId { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public string[] Mentions { get; set; } = [];

    /// <summary>Name the origin instance rendered this message under, or null for a plain user post.</summary>
    public string? AuthorDisplayName { get; set; }

    /// <summary>Avatar that goes with AuthorDisplayName.</summary>
    public string? AuthorAvatarUrl { get; set; }

    public FederatedAuthorIdType AuthorIdType { get; set; } = FederatedAuthorIdType.User;
}
