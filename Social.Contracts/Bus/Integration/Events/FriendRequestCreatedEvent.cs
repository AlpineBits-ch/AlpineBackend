namespace Social.Contracts.Bus.Integration.Events;

public class FriendRequestCreatedEvent
{
    public string InitiatorUserId { get; set; }
    public string TargetUserId { get; set; }
    public string RelationshipId { get; set; }
}
