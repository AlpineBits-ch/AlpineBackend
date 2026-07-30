using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Domain.Events.Relationship;

namespace Social.Tests.Domain;

[TestFixture]
public class RelationshipTests
{
    [Test]
    public void Create_ReturnsOutgoingAndIncomingPair_WithMirroredStatuses()
    {
        var pair = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = "profile-a",
            Subject = "profile-b",
        }).ToList();

        Assert.That(pair, Has.Count.EqualTo(2));

        var outgoing = pair.Single(r => r.OwnerId == "profile-a");
        var incoming = pair.Single(r => r.OwnerId == "profile-b");

        Assert.Multiple(() =>
        {
            Assert.That(outgoing.TargetId, Is.EqualTo("profile-b"));
            Assert.That(outgoing.Status, Is.EqualTo(RelationshipStatus.PendingOutgoing));
            Assert.That(incoming.TargetId, Is.EqualTo("profile-a"));
            Assert.That(incoming.Status, Is.EqualTo(RelationshipStatus.PendingIncoming));
        });
    }

    [Test]
    public void Create_CrossLinksRelatedIds()
    {
        var pair = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = "profile-a",
            Subject = "profile-b",
        }).ToList();

        var outgoing = pair.Single(r => r.OwnerId == "profile-a");
        var incoming = pair.Single(r => r.OwnerId == "profile-b");

        Assert.Multiple(() =>
        {
            Assert.That(outgoing.RelatedId, Is.EqualTo(incoming.Id));
            Assert.That(incoming.RelatedId, Is.EqualTo(outgoing.Id));
            Assert.That(outgoing.Id, Is.Not.EqualTo(incoming.Id));
        });
    }

    [Test]
    public void Create_RaisesFriendRequestCreatedOnlyOnOutgoingSide()
    {
        var pair = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = "profile-a",
            Subject = "profile-b",
        }).ToList();

        var outgoing = pair.Single(r => r.OwnerId == "profile-a");
        var incoming = pair.Single(r => r.OwnerId == "profile-b");

        Assert.That(outgoing.GetDomainEvents(), Has.Count.EqualTo(1));
        Assert.That(incoming.GetDomainEvents(), Is.Empty);

        var domainEvent = outgoing.GetDomainEvents().Single();
        Assert.That(domainEvent, Is.InstanceOf<FriendRequestCreated>());
        var created = (FriendRequestCreated)domainEvent;
        Assert.Multiple(() =>
        {
            Assert.That(created.InitiatorProfileId, Is.EqualTo("profile-a"));
            Assert.That(created.TargetProfileId, Is.EqualTo("profile-b"));
            Assert.That(created.RelationshipId, Is.EqualTo(outgoing.Id));
        });
    }

    [Test]
    public void Accept_FromPendingIncoming_RaisesFriendRequestAcceptedAndSetsFriends()
    {
        var relationship = new Relationship
        {
            Id = "rlsp_1",
            OwnerId = "profile-b",
            TargetId = "profile-a",
            Status = RelationshipStatus.PendingIncoming,
        };

        relationship.Accept();

        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.Friends));
        var domainEvent = relationship.GetDomainEvents().Single();
        Assert.That(domainEvent, Is.InstanceOf<FriendRequestAccepted>());
        var accepted = (FriendRequestAccepted)domainEvent;
        Assert.Multiple(() =>
        {
            Assert.That(accepted.TargetProfileId, Is.EqualTo("profile-b"));
            Assert.That(accepted.InitiatorProfileId, Is.EqualTo("profile-a"));
            Assert.That(accepted.RelationshipId, Is.EqualTo("rlsp_1"));
        });
    }

    [Test]
    public void Accept_FromNonPendingIncomingStatus_SetsFriendsWithoutRaisingEvent()
    {
        // Covers Related.Accept() being invoked from FriendshipEndpoints.AcceptAsync on the
        // PendingOutgoing side - it should still flip to Friends but not double-raise the event.
        var relationship = new Relationship
        {
            Id = "rlsp_2",
            OwnerId = "profile-a",
            TargetId = "profile-b",
            Status = RelationshipStatus.PendingOutgoing,
        };

        relationship.Accept();

        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.Friends));
        Assert.That(relationship.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void Reject_FromPendingIncoming_RaisesFriendRequestRejectedAndClearsStatus()
    {
        var relationship = new Relationship
        {
            Id = "rlsp_3",
            OwnerId = "profile-b",
            TargetId = "profile-a",
            Status = RelationshipStatus.PendingIncoming,
        };

        relationship.Reject();

        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.None));
        var domainEvent = relationship.GetDomainEvents().Single();
        Assert.That(domainEvent, Is.InstanceOf<FriendRequestRejected>());
    }

    [Test]
    public void Reject_FromNonPendingIncomingStatus_ClearsStatusWithoutRaisingEvent()
    {
        var relationship = new Relationship
        {
            Id = "rlsp_4",
            OwnerId = "profile-a",
            TargetId = "profile-b",
            Status = RelationshipStatus.PendingOutgoing,
        };

        relationship.Reject();

        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.None));
        Assert.That(relationship.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void Remove_FromFriendsStatus_RaisesFriendRemovedAndClearsStatus()
    {
        var relationship = new Relationship
        {
            Id = "rlsp_5",
            OwnerId = "profile-a",
            TargetId = "profile-b",
            Status = RelationshipStatus.Friends,
        };

        relationship.Remove();

        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.None));
        var domainEvent = relationship.GetDomainEvents().Single();
        Assert.That(domainEvent, Is.InstanceOf<FriendRemoved>());
        var removed = (FriendRemoved)domainEvent;
        Assert.Multiple(() =>
        {
            Assert.That(removed.TargetProfileId, Is.EqualTo("profile-a"));
            Assert.That(removed.InitiatorProfileId, Is.EqualTo("profile-b"));
        });
    }

    [Test]
    public void Remove_FromPendingOutgoingStatus_AlwaysRaisesFriendRemoved()
    {
        // Remove() unconditionally raises FriendRemoved regardless of prior status - covers the
        // "revoke my own pending outgoing request" usage documented on the method.
        var relationship = new Relationship
        {
            Id = "rlsp_6",
            OwnerId = "profile-a",
            TargetId = "profile-b",
            Status = RelationshipStatus.PendingOutgoing,
        };

        relationship.Remove();

        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.None));
        Assert.That(relationship.GetDomainEvents(), Has.Count.EqualTo(1));
    }

    // ── Idempotency ──────────────────────────────────────────────────────────
    // Each transition raises its domain event only on a real state change. That event is what
    // fans out the social.* websocket push and the outbound federation message, so a second
    // no-op call must stay silent rather than duplicating either.

    [Test]
    public void Accept_CalledTwice_RaisesTheEventOnceAndReportsNoSecondTransition()
    {
        var relationship = new Relationship
        {
            Id = "rlsp_7", OwnerId = "profile-b", TargetId = "profile-a",
            Status = RelationshipStatus.PendingIncoming,
        };

        Assert.Multiple(() =>
        {
            Assert.That(relationship.Accept(), Is.True);
            Assert.That(relationship.Accept(), Is.False);
        });

        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.Friends));
        Assert.That(relationship.GetDomainEvents().OfType<FriendRequestAccepted>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void Accept_OnClearedRelationship_DoesNothing()
    {
        // Rejected/removed rows stick around at None - accepting one would revive a friendship
        // the other side already refused, without a new request.
        var relationship = new Relationship
        {
            Id = "rlsp_8", OwnerId = "profile-b", TargetId = "profile-a",
            Status = RelationshipStatus.None,
        };

        Assert.That(relationship.Accept(), Is.False);
        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.None));
        Assert.That(relationship.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void Reject_CalledTwice_RaisesTheEventOnce()
    {
        var relationship = new Relationship
        {
            Id = "rlsp_9", OwnerId = "profile-b", TargetId = "profile-a",
            Status = RelationshipStatus.PendingIncoming,
        };

        Assert.Multiple(() =>
        {
            Assert.That(relationship.Reject(), Is.True);
            Assert.That(relationship.Reject(), Is.False);
        });

        Assert.That(relationship.GetDomainEvents().OfType<FriendRequestRejected>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void Remove_CalledTwice_RaisesTheEventOnce()
    {
        var relationship = new Relationship
        {
            Id = "rlsp_10", OwnerId = "profile-a", TargetId = "profile-b",
            Status = RelationshipStatus.Friends,
        };

        Assert.Multiple(() =>
        {
            Assert.That(relationship.Remove(), Is.True);
            Assert.That(relationship.Remove(), Is.False);
        });

        Assert.That(relationship.GetDomainEvents().OfType<FriendRemoved>().Count(), Is.EqualTo(1));
    }
}
