using System.Collections.Immutable;

namespace Federation.Domain.Events;
public record struct EventId(string Value)
{
    public override string ToString() => Value;
    public static EventId FromHash(string hash) => new(hash);
}
public abstract class FederatedEvent
{
    public EventId EventId { get; protected set; }
    public IImmutableSet<EventId> PreviousEventIds { get; protected set; }
    public long Depth { get; protected set; }
    public string ChannelId { get; protected set; }
    public string SenderId { get; protected set; }
    public DateTime OriginServerTime { get; protected set; }
    public abstract string EventType { get; }
    
    protected FederatedEvent(
        EventId eventId, 
        IEnumerable<EventId> prevEvents, 
        long depth, 
        string channelId, 
        string senderId, 
        DateTime originServerTimestamp)
    {
        EventId = eventId;
        PreviousEventIds = prevEvents.ToImmutableHashSet();
        Depth = depth;
        ChannelId = channelId;
        SenderId = senderId;
        OriginServerTime = originServerTimestamp;
    }
    
}