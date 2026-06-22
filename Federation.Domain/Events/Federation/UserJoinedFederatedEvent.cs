namespace Federation.Domain.Events.Federation;

public class UserJoinedFederatedEvent : FederatedEvent
{
    public override string EventType => "m.channel.user.joined";
    
    public UserJoinedFederatedEvent(
        EventId eventId, 
        IEnumerable<EventId> prevEvents, 
        long depth, 
        string channelId, 
        string senderId, 
        DateTime originServerTimestamp) 
        : base(eventId, prevEvents, depth, channelId, senderId, originServerTimestamp)
    {
    }
}