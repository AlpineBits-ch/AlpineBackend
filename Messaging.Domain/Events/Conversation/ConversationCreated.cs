using Domain;

namespace Messaging.Domain.Events.Conversation;

public class ConversationCreated : DomainEvent
{
    public string ConversationId { get; set; }
}