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


    /// <summary>
    /// Accepts an inbound friend request. Only the recipient's own (PendingIncoming) row may be
    /// accepted - accepting is the recipient's consent, so the initiator must not be able to
    /// transition their own PendingOutgoing row and manufacture a friendship the other party never
    /// agreed to. The mirrored outgoing row is flipped separately via
    /// <see cref="AcceptCounterpart"/>, which is the only sanctioned way to move a PendingOutgoing
    /// row to Friends.
    ///
    /// Idempotent: a relationship that is already Friends is left alone and raises nothing, so a
    /// client that fires the accept endpoint twice (double tap, retry, two devices) can't emit a
    /// second FriendRequestAccepted - which would otherwise fan out a duplicate
    /// social.FriendRequestAccepted push and have the client append the same friend to its list
    /// twice. Also refuses to resurrect a dead (None) relationship into a friendship without a
    /// fresh request.
    /// </summary>
    /// <returns>True if this call actually changed the status.</returns>
    public bool Accept()
    {
        if (this.Status != RelationshipStatus.PendingIncoming)
            return false;

        this.AddDomainEvent(new FriendRequestAccepted()
        {
            TargetProfileId = this.OwnerId,
            InitiatorProfileId = this.TargetId,
            RelationshipId = this.Id
        });

        this.Status = RelationshipStatus.Friends;
        return true;
    }

    /// <summary>
    /// Flips the initiator's mirrored PendingOutgoing row once the recipient has accepted via
    /// <see cref="Accept"/>. Raises no event - the accepted event is raised once, on the
    /// recipient's row, so the pair produces exactly one social.FriendRequestAccepted push.
    /// Separate from <see cref="Accept"/> so that reaching Friends from PendingOutgoing is only
    /// possible as a consequence of a real acceptance, never as a directly-callable transition.
    /// </summary>
    /// <returns>True if this call actually changed the status.</returns>
    public bool AcceptCounterpart()
    {
        if (this.Status != RelationshipStatus.PendingOutgoing)
            return false;

        this.Status = RelationshipStatus.Friends;
        return true;
    }

    /// <summary>Idempotent for the same reason as <see cref="Accept"/> - an already-cleared
    /// relationship is a no-op rather than a second FriendRequestRejected. Refuses on a
    /// <see cref="RelationshipStatus.Blocked"/> row for the same reason as <see cref="Remove"/>:
    /// a block is only lifted through <see cref="Unblock"/>, which is what publishes
    /// <c>UserUnblockedEvent</c>.</summary>
    /// <returns>True if this call actually changed the status.</returns>
    public bool Reject()
    {
        if (this.Status is RelationshipStatus.None or RelationshipStatus.Blocked) return false;

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
    /// relationship raises nothing.
    ///
    /// <para>Refuses on a <see cref="RelationshipStatus.Blocked"/> row. A block is only ever lifted
    /// through <see cref="Unblock"/> behind <c>DELETE /api/v1/relationships/{userId}/block</c>,
    /// which is also what publishes <c>UserUnblockedEvent</c> - so letting the ordinary unfriend
    /// path clear one would silently drop a block while every other service's cache went on
    /// believing it was in force. It also stops the person being blocked from clearing the block by
    /// blocking back, which is how they would otherwise discover it.</para></summary>
    /// <returns>True if this call actually changed the status.</returns>
    public bool Remove()
    {
        if (this.Status is RelationshipStatus.None or RelationshipStatus.Blocked) return false;

        this.AddDomainEvent(new FriendRemoved()
        {
            TargetProfileId = this.OwnerId,
            InitiatorProfileId = this.TargetId,
            RelationshipId = this.Id
        });
        this.Status = RelationshipStatus.None;
        return true;
    }

    /// <summary>
    /// Turns this row into the blocker's one-sided block record (privacy spec T0-3).
    ///
    /// <para>Blocking is deliberately <b>not</b> a mirrored pair like a friend request: A blocking B
    /// is a fact about A's row only, and B must never gain a row that would let them tell "blocked"
    /// apart from "not friends". <see cref="RelatedId"/> is therefore cleared - the block row stands
    /// alone, and the counterpart row (if the pair had one) is torn down separately with
    /// <see cref="Remove"/> so the other side sees an ordinary un-friending.</para>
    ///
    /// <para>Raises no domain event: the block itself is announced across services as a
    /// <c>UserBlockedEvent</c> published by the endpoint, because the interesting identity is the
    /// pair of <i>user</i> ids, not the profile ids a domain event carries.</para>
    /// </summary>
    /// <returns>True if this call actually changed the status - false when already blocked, which
    /// keeps a repeated block idempotent.</returns>
    public bool Block()
    {
        if (this.Status == RelationshipStatus.Blocked) return false;

        this.Status = RelationshipStatus.Blocked;
        this.RelatedId = null;
        return true;
    }

    /// <summary>
    /// Lifts a block. Only a <see cref="RelationshipStatus.Blocked"/> row may be unblocked, so an
    /// unblock can never quietly clear a live friendship or pending request.
    ///
    /// <para>The caller deletes the row afterwards rather than leaving it at
    /// <see cref="RelationshipStatus.None"/>: a lingering row for the pair would make the
    /// "relationship already exists" guard in the create endpoint refuse every future friend request
    /// between the two, so the block would keep costing them a friendship after it was lifted.</para>
    /// </summary>
    /// <returns>True if this row was a block.</returns>
    public bool Unblock()
    {
        if (this.Status != RelationshipStatus.Blocked) return false;

        this.Status = RelationshipStatus.None;
        this.RelatedId = null;
        return true;
    }
}