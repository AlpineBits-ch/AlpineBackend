using Domain;

namespace Messaging.Domain.Events.Conversation;

/// <summary>A group conversation's name or icon changed.</summary>
public class ConversationUpdated : DomainEvent
{
    public string ConversationId { get; set; }

    /// <summary>The name as it now stands, null when the group has none.</summary>
    public string? Name { get; set; }

    /// <summary>When the icon was last written, null when the group has none.</summary>
    public DateTimeOffset? IconUpdatedAt { get; set; }
}
