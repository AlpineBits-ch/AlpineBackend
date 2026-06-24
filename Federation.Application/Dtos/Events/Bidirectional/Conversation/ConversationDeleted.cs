namespace Federation.Application.Dtos.Events.Bidirectional.Conversation;

public class ConversationDeleted : FederationEvent
{
    public string ConversationId { get; set; } = string.Empty;
}