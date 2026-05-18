using Domain;

namespace Messaging.Domain.Events.Conversation;

public class ConversationMemberRemoved : DomainEvent
{
    public string UserId { get; set; }
    public string ConversationId { get; set; }
    public bool HasLeft { get; set; }

}