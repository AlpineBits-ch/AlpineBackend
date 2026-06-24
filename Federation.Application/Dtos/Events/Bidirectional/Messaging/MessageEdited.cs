namespace Federation.Application.Dtos.Events.Bidirectional.Messaging;

public class MessageEdited : FederationEvent
{
    public string MessageId { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
}