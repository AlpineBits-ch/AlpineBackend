using Domain;

namespace Social.Domain.Events.Relationship;

public class FriendRequestRejected : DomainEvent
{
    public string TargetProfileId { get; set; }
    public string InitiatorProfileId { get; set; }
    public string RelationshipId { get; set; }

};