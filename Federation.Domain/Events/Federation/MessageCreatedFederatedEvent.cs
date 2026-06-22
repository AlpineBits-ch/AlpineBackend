namespace Federation.Domain.Events.Federation;

public class MessageCreatedFederatedEvent : FederatedEvent
{
    public override string EventType => "m.channel.message";

    public byte[] Content { get; private set; }

    public MessageCreatedFederatedEvent(
        EventId eventId, 
        IEnumerable<EventId> prevEvents, 
        long depth, 
        string channelId, 
        string senderId, 
        DateTime originServerTimestamp,
        byte[] content) 
        : base(eventId, prevEvents, depth, channelId, senderId, originServerTimestamp)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }
}