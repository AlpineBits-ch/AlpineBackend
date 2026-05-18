using Domain;

namespace Messaging.Domain.Events.Message;

public class MessageDeleted : DomainEvent
{
    public string MessageId { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    
    public string AuthorId { get; set; }
}