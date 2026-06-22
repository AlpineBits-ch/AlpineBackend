namespace Federation.Domain.Events.Federation;

public class UserLeftFederatedEvent : FederatedEvent
{
    public override string EventType => "m.channel.user.left";

    public UserLeftFederatedEvent(
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