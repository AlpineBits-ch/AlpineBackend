using Domain;

namespace Messaging.Domain.Events.Reactions;

public class ReactionRemoved : DomainEvent
{
    public string MessageId { get; set; }
    public string Emoji { get; set; }
    public string UserId { get; set; }
    
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public override string ToString()
    {
        return $"ReactionRemoved: MessageId={MessageId}";
    }
}