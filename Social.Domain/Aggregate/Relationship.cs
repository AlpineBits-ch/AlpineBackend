using Domain;
using Persistence;
using Social.Domain.Enums;
using Social.Domain.Events.Relationship;

namespace Social.Domain.Aggregate;

public struct CreateRelationshipParams
{
    public string Initiator { get; init; }
    public string Subject { get; init; }
}
public class Relationship : Aggregate<Relationship>, IPrefixedEntity
{
    public static string Prefix { get; } = "rlsp";
    
    public virtual Profile Owner { get; set; } = null!;
    public string OwnerId { get; set; }
    public virtual Profile Target { get; set; } = null!;
    public string TargetId { get; set; }
    
    public RelationshipStatus Status { get; set; }
    
    public string? RelatedId { get; set; }
    public virtual Relationship Related { get; set; } = null!;

    /// <summary>
    /// Null for a locally-created relationship. Set to the owning Federation.Application
    /// instance's FederationInstance id when this friendship/request is a shadow copy
    /// materialized from a remote instance (see the canonical-ID / shadow-entity model in the
    /// federation protocol doc) - doubles as the "is this remote" flag.
    /// </summary>
    public string? OriginInstanceId { get; set; }


    
    /// <summary>
    /// This creates two relationships. The owner id in the params is the initiator.
    /// </summary>
    /// <param name="param"></param>
    /// <returns></returns>
    public static ICollection<Relationship> Create(CreateRelationshipParams param)
    {
        var incommingId = GenerateId();
        var outgoingId = GenerateId();

        var outgoingRequest = new Relationship()
        {
            Id = outgoingId,
            OwnerId = param.Initiator,
            TargetId = param.Subject,
            Status = RelationshipStatus.PendingOutgoing,
            RelatedId = incommingId,

        };

        var incomingRequest = new Relationship()
        {
            Id = incommingId,
            OwnerId = param.Subject,
            TargetId = param.Initiator,
            Status = RelationshipStatus.PendingIncoming,
            RelatedId = outgoingId,
        };
        
        outgoingRequest.AddDomainEvent(new FriendRequestCreated()
        {
            TargetProfileId = param.Subject,
            InitiatorProfileId = param.Initiator,
            RelationshipId = outgoingId,
        });
        return new List<Relationship> { outgoingRequest, incomingRequest };
        
    }


    public void Accept()
    {
        if (this.Status == RelationshipStatus.PendingIncoming)
        {
            this.AddDomainEvent(new FriendRequestAccepted()
            {
                TargetProfileId = this.OwnerId,
                InitiatorProfileId = this.TargetId,
                RelationshipId = this.Id
            });
        }
        this.Status = RelationshipStatus.Friends;
        
    }

    public void Reject()
    {
        if (this.Status == RelationshipStatus.PendingIncoming)
        {
            this.AddDomainEvent(new FriendRequestRejected()
            {
                TargetProfileId = this.OwnerId,
                InitiatorProfileId = this.TargetId,
                RelationshipId = this.Id
            });
        }
        this.Status = RelationshipStatus.None;
    }

    /// <summary>Covers both "revoke my own pending outgoing request" and "unfriend an accepted
    /// friendship" - FriendEndpoint.RevokeAsync uses this for both, and federation only cares
    /// that the relationship no longer exists either way.</summary>
    public void Remove()
    {
        this.AddDomainEvent(new FriendRemoved()
        {
            TargetProfileId = this.OwnerId,
            InitiatorProfileId = this.TargetId,
            RelationshipId = this.Id
        });
        this.Status = RelationshipStatus.None;
    }
    
    
}