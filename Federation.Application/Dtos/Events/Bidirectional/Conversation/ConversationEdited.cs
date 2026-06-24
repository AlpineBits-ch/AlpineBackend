namespace Federation.Application.Dtos.Events.Bidirectional.Conversation;

public class ConversationEdited : FederationEvent
{
    public string ConversationId { get; set; } = string.Empty;
    public string? Name { get; set; }
}