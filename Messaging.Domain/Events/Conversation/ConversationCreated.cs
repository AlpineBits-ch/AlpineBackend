using Domain;

namespace Messaging.Domain.Events.Conversation;

public class ConversationCreated : DomainEvent
{
    public string ConversationId { get; set; }

    /// <summary>
    /// Populated at raise time in Conversation.Create() - added so federation's outbound
    /// wiring doesn't need a separate cross-service round trip just to learn who's in a newly
    /// created conversation.
    /// </summary>
    public string[] MemberIds { get; set; } = [];
}