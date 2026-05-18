using Domain;

namespace Messaging.Domain.Events.Conversation;

public class ConversationMemberAdded : DomainEvent
{
    public string UserId { get; set; }
    public string ConversationId { get; set; }

}