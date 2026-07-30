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

    /// <summary>Null for a locally-created relationship.</summary>
    public string? OriginInstanceId { get; set; }


    
    /// <summary>This creates two relationships.</summary>
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


    /// <summary>Transitions this side of the pair to Friends.</summary>
    /// <returns>True if this call actually changed the status.</returns>
    public bool Accept()
    {
        if (this.Status is not (RelationshipStatus.PendingIncoming or RelationshipStatus.PendingOutgoing))
            return false;

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
        return true;
    }

    /// <summary>Idempotent for the same reason as <see cref="Accept"/> - an already-cleared
    /// relationship is a no-op rather than a second FriendRequestRejected.</summary>
    /// <returns>True if this call actually changed the status.</returns>
    public bool Reject()
    {
        if (this.Status == RelationshipStatus.None) return false;

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
        return true;
    }

    /// <summary>Covers both "revoke my own pending outgoing request" and "unfriend an accepted
    /// friendship" - FriendEndpoint.RevokeAsync uses this for both, and federation only cares
    /// that the relationship no longer exists either way. Idempotent: removing an already-removed
    /// relationship raises nothing.</summary>
    /// <returns>True if this call actually changed the status.</returns>
    public bool Remove()
    {
        if (this.Status == RelationshipStatus.None) return false;

        this.AddDomainEvent(new FriendRemoved()
        {
            TargetProfileId = this.OwnerId,
            InitiatorProfileId = this.TargetId,
            RelationshipId = this.Id
        });
        this.Status = RelationshipStatus.None;
        return true;
    }
    
    
}