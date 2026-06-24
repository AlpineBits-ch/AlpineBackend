namespace Federation.Application.Dtos.Events.Bidirectional.Messaging;

public class MessageDeleted : FederationEvent
{
    public string MessageId { get; set; } = string.Empty;
}