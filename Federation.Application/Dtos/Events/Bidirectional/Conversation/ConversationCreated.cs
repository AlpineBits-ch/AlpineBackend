namespace Federation.Application.Dtos.Events.Bidirectional.Conversation;

public class ConversationCreated : FederationEvent
{
    public string ConversationId { get; set; } = string.Empty;
    public string[] MemberIds { get; set; } = [];
}