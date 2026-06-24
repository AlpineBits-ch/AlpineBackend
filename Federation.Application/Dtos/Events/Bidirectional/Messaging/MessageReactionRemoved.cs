namespace Federation.Application.Dtos.Events.Bidirectional.Messaging;

public class MessageReactionRemoved : FederationEvent
{
    public string MessageId { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
}