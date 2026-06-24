namespace Federation.Application.Dtos.Events.Bidirectional.Conversation;

public class ConversationMemberAdded : FederationEvent
{
    public string ConversationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}