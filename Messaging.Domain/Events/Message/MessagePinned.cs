using Domain;

namespace Messaging.Domain.Events.Message;

public class MessagePinned : DomainEvent
{
    public string MessageId { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string AuthorId { get; set; }
    public string PinnedById { get; set; }
    public DateTime PinnedAt { get; set; }
}

public class MessageUnpinned : DomainEvent
{
    public string MessageId { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string AuthorId { get; set; }
    public string UnpinnedById { get; set; }
}
