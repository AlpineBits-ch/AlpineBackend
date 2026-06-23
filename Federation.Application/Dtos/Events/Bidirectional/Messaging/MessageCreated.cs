namespace Federation.Application.Dtos.Events.Bidirectional.Messaging;

public class MessageCreated : FederationEvent
{
    public byte[] Content { get; set; } = [];
}