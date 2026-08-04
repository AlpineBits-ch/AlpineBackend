using Social.Api.Dtos.Realtime;
using Social.Api.Integration.Relationship.Events;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

/// <summary>Covers the social.* websocket fan-out.</summary>
[TestFixture]
public class RelationshipRealtimeHandlersTests
{
    private TestSocialContext _context = null!;
    private FakeSocialHubContext _hub = null!;
    private Profile _initiator = null!;
    private Profile _target = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _hub = new FakeSocialHubContext();

        _initiator = Profile.Create(new CreateProfileParams { UserId = "user-initiator", Username = "initiator" });
        _target = Profile.Create(new CreateProfileParams { UserId = "user-target", Username = "target" });
        _context.Profiles.AddRange(_initiator, _target);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedPair(RelationshipStatus outgoingStatus, RelationshipStatus incomingStatus)
    {
        _context.Relationships.AddRange(
            new Relationship
            {
                Id = "rlsp_out", OwnerId = _initiator.Id, TargetId = _target.Id,
                Status = outgoingStatus, RelatedId = "rlsp_in",
            },
            new Relationship
            {
                Id = "rlsp_in", OwnerId = _target.Id, TargetId = _initiator.Id,
                Status = incomingStatus, RelatedId = "rlsp_out",
            });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task FriendRequestCreated_PushesToBothSidesWithTheirOwnRowAndStatus()
    {
        await SeedPair(RelationshipStatus.PendingOutgoing, RelationshipStatus.PendingIncoming);

        await RelationshipRealtimeHandlers.Handle(
            new FriendRequestCreatedEvent
            {
                InitiatorUserId = "user-initiator",
                TargetUserId = "user-target",
                RelationshipId = "rlsp_out",
            }, _context, _hub);

        Assert.That(_hub.Sent, Has.Count.EqualTo(2), "both parties must be notified, not just the recipient");
        Assert.That(_hub.Sent.Select(s => s.Method), Is.All.EqualTo("social.FriendRequestCreated"));

        var toInitiator = _hub.To("user-initiator").Single().Payload<FriendRelationshipPayload>();
        var toTarget = _hub.To("user-target").Single().Payload<FriendRelationshipPayload>();

        Assert.Multiple(() =>
        {
            // Initiator's view: its own outgoing row, counterpart is the target.
            Assert.That(toInitiator.RelationshipId, Is.EqualTo("rlsp_out"));
            Assert.That(toInitiator.Status, Is.EqualTo(RelationshipStatus.PendingOutgoing));
            Assert.That(toInitiator.UserId, Is.EqualTo("user-target"));
            Assert.That(toInitiator.ProfileId, Is.EqualTo(_target.Id));
            Assert.That(toInitiator.UserName, Is.EqualTo("target"));

            // Target's view: its own incoming row, counterpart is the initiator.
            Assert.That(toTarget.RelationshipId, Is.EqualTo("rlsp_in"));
            Assert.That(toTarget.Status, Is.EqualTo(RelationshipStatus.PendingIncoming));
            Assert.That(toTarget.UserId, Is.EqualTo("user-initiator"));
            Assert.That(toTarget.ProfileId, Is.EqualTo(_initiator.Id));
            Assert.That(toTarget.UserName, Is.EqualTo("initiator"));
        });
    }

    [Test]
    public async Task FriendshipAccepted_PushesFriendsToBothSides()
    {
        await SeedPair(RelationshipStatus.Friends, RelationshipStatus.Friends);

        await RelationshipRealtimeHandlers.Handle(
            new FriendshipAcceptedEvent
            {
                InitiatorUserId = "user-initiator",
                AcceptantUserId = "user-target",
                InitiatorUserName = "initiator",
                AcceptantUserName = "target",
                FriendshipId = "rlsp_in",
            }, _context, _hub);

        Assert.That(_hub.Sent, Has.Count.EqualTo(2));
        Assert.That(_hub.Sent.Select(s => s.Method), Is.All.EqualTo("social.FriendRequestAccepted"));

        Assert.Multiple(() =>
        {
            var toInitiator = _hub.To("user-initiator").Single().Payload<FriendRelationshipPayload>();
            var toTarget = _hub.To("user-target").Single().Payload<FriendRelationshipPayload>();

            Assert.That(toInitiator.RelationshipId, Is.EqualTo("rlsp_out"));
            Assert.That(toInitiator.Status, Is.EqualTo(RelationshipStatus.Friends));
            Assert.That(toTarget.RelationshipId, Is.EqualTo("rlsp_in"));
            Assert.That(toTarget.Status, Is.EqualTo(RelationshipStatus.Friends));
        });
    }

    [Test]
    public async Task FriendRequestRejected_PushesClearedStatusToBothSides()
    {
        await SeedPair(RelationshipStatus.None, RelationshipStatus.None);

        await RelationshipRealtimeHandlers.Handle(
            new FriendRequestRejectedEvent
            {
                InitiatorUserId = "user-initiator",
                TargetUserId = "user-target",
                RelationshipId = "rlsp_in",
            }, _context, _hub);

        Assert.That(_hub.Sent, Has.Count.EqualTo(2));
        Assert.That(_hub.Sent.Select(s => s.Method), Is.All.EqualTo("social.FriendRequestRejected"));
        Assert.That(_hub.Sent.Select(s => s.Payload<FriendRelationshipPayload>().Status),
            Is.All.EqualTo(RelationshipStatus.None));
    }

    [Test]
    public async Task FriendRemoved_PushesClearedStatusToBothSides()
    {
        await SeedPair(RelationshipStatus.None, RelationshipStatus.None);

        await RelationshipRealtimeHandlers.Handle(
            new FriendRemovedEvent
            {
                InitiatorUserId = "user-initiator",
                TargetUserId = "user-target",
                RelationshipId = "rlsp_out",
            }, _context, _hub);

        Assert.That(_hub.Sent, Has.Count.EqualTo(2));
        Assert.That(_hub.Sent.Select(s => s.Method), Is.All.EqualTo("social.FriendRemoved"));
    }

    [Test]
    public async Task Push_RelationshipWithNoRelatedRow_NotifiesOnlyTheLocalSide()
    {
        // Federation materializes only the local half of a remote friendship - RelatedId points at
        // a row that doesn't exist on this instance, so there is exactly one side to notify.
        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_local", OwnerId = _target.Id, TargetId = _initiator.Id,
            Status = RelationshipStatus.PendingIncoming, RelatedId = null,
        });
        await _context.SaveChangesAsync();

        await RelationshipRealtimeHandlers.PushBothSidesAsync(
            _context, _hub, "rlsp_local", "social.FriendRequestCreated");

        Assert.That(_hub.Sent, Has.Count.EqualTo(1));
        Assert.That(_hub.To("user-target").Single().Payload<FriendRelationshipPayload>().RelationshipId,
            Is.EqualTo("rlsp_local"));
    }

    [Test]
    public async Task Push_RelationshipAlreadyGone_PushesNothing()
    {
        // Account deletion purges relationship rows; the bus hop is asynchronous, so the row can
        // legitimately have vanished before the push handler runs.
        await RelationshipRealtimeHandlers.PushBothSidesAsync(
            _context, _hub, "rlsp_deleted", "social.FriendRemoved");

        Assert.That(_hub.Sent, Is.Empty);
    }

    // ── T0-3: no social.* traffic between a blocked pair ─────────────────────

    private async Task SeedBlock(Profile blocker, Profile blocked)
    {
        _context.Relationships.Add(new Relationship
        {
            Id = Relationship.GenerateId(),
            OwnerId = blocker.Id,
            TargetId = blocked.Id,
            Status = RelationshipStatus.Blocked,
        });
        await _context.SaveChangesAsync();
    }

    [TestCase("social.FriendRequestCreated")]
    [TestCase("social.FriendRequestAccepted")]
    [TestCase("social.FriendRequestRejected")]
    public async Task Push_BlockedPair_SuppressesEverythingButRemoval(string eventName)
    {
        await SeedPair(RelationshipStatus.PendingOutgoing, RelationshipStatus.PendingIncoming);
        await SeedBlock(_target, _initiator);

        await RelationshipRealtimeHandlers.PushBothSidesAsync(_context, _hub, "rlsp_out", eventName);

        Assert.That(_hub.Sent, Is.Empty);
    }

    [Test]
    public async Task Push_BlockedPair_StillDeliversFriendRemoved()
    {
        // The blocked party has to learn the friendship ended - that is exactly the "same as not
        // friends" view they are entitled to, and suppressing it would strand their client on a
        // friend they no longer have.
        await SeedPair(RelationshipStatus.None, RelationshipStatus.None);
        await SeedBlock(_initiator, _target);

        await RelationshipRealtimeHandlers.PushBothSidesAsync(_context, _hub, "rlsp_out", "social.FriendRemoved");

        Assert.That(_hub.Sent, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Push_BlockInEitherDirection_Suppresses()
    {
        // Direction does not matter: once either side has blocked, neither gets the other's events.
        await SeedPair(RelationshipStatus.PendingOutgoing, RelationshipStatus.PendingIncoming);
        await SeedBlock(_initiator, _target);

        await RelationshipRealtimeHandlers.PushBothSidesAsync(
            _context, _hub, "rlsp_in", "social.FriendRequestCreated");

        Assert.That(_hub.Sent, Is.Empty);
    }
}
