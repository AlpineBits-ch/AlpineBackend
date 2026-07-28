using Domain;

namespace Social.Domain.Events.Relationship;

public class FriendRemoved : DomainEvent
{
    public string TargetProfileId { get; set; }
    public string InitiatorProfileId { get; set; }
    public string RelationshipId { get; set; }
};
