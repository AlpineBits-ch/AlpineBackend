namespace Social.Contracts.Bus.Integration.Events;

public class FriendRemovedEvent
{
    public string InitiatorUserId { get; set; }
    public string TargetUserId { get; set; }
    public string RelationshipId { get; set; }
}
