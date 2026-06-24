namespace Federation.Application.Dtos.Events.Bidirectional.Messaging;

public class MessageCreated : FederationEvent
{
    public string MessageId { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public string[] Mentions { get; set; } = [];
}