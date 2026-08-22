using Social.Api.Integration.Relationship.Events;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Domain.Events.Relationship;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

[TestFixture]
public class FriendRequestLifecycleHandlersTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;
    private FakeMessageBus _bus = null!;
    private Profile _initiator = null!;
    private Profile _target = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
        _bus = new FakeMessageBus();

        _initiator = Profile.Create(new CreateProfileParams { UserId = "user-initiator", Username = "initiator" });
        _target = Profile.Create(new CreateProfileParams { UserId = "user-target", Username = "target" });
        _context.Profiles.AddRange(_initiator, _target);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedBlockAsync(Profile blocker, Profile blocked)
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

    // ── normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_FriendRequestCreated_ResolvesUserIdsFromProfileIds()
    {
        await FriendRequestLifecycleHandler.Handle(new FriendRequestCreated
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_1",
        }, _context, _bus);

        var result = _bus.Published.OfType<FriendRequestCreatedEvent>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.InitiatorUserId, Is.EqualTo("user-initiator"));
            Assert.That(result.TargetUserId, Is.EqualTo("user-target"));
            Assert.That(result.RelationshipId, Is.EqualTo("rlsp_1"));
        });
    }

    [Test]
    public async Task Handle_FriendRequestRejected_ResolvesUserIdsFromProfileIds()
    {
        await FriendRequestLifecycleHandler.Handle(new FriendRequestRejected
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_2",
        }, _context, _bus);

        var result = _bus.Published.OfType<FriendRequestRejectedEvent>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.InitiatorUserId, Is.EqualTo("user-initiator"));
            Assert.That(result.TargetUserId, Is.EqualTo("user-target"));
            Assert.That(result.RelationshipId, Is.EqualTo("rlsp_2"));
        });
    }

    [Test]
    public async Task Handle_FriendRemoved_ResolvesUserIdsFromProfileIds()
    {
        await FriendRequestLifecycleHandler.Handle(new FriendRemoved
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_3",
        }, _context, _bus);

        var result = _bus.Published.OfType<FriendRemovedEvent>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.InitiatorUserId, Is.EqualTo("user-initiator"));
            Assert.That(result.TargetUserId, Is.EqualTo("user-target"));
            Assert.That(result.RelationshipId, Is.EqualTo("rlsp_3"));
        });
    }

    // ── negative: a block silences the cross-service contract ────────────────

    [Test]
    public async Task Handle_FriendRequestCreated_PublishesNothingWhenPairIsBlocked()
    {
        await SeedBlockAsync(_target, _initiator);

        await FriendRequestLifecycleHandler.Handle(new FriendRequestCreated
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_1",
        }, _context, _bus);

        Assert.That(_bus.Published, Is.Empty,
            "a friend request between a blocked pair must not reach federation or any other consumer");
    }

    [Test]
    public async Task Handle_FriendRequestRejected_PublishesNothingWhenPairIsBlocked()
    {
        await SeedBlockAsync(_initiator, _target);

        await FriendRequestLifecycleHandler.Handle(new FriendRequestRejected
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_2",
        }, _context, _bus);

        Assert.That(_bus.Published, Is.Empty);
    }

    // ── edge: removal is the deliberate exception ────────────────────────────

    [Test]
    public async Task Handle_FriendRemoved_StillPublishesWhenPairIsBlocked()
    {
        // Blocking tears the friendship down, and the blocked party has to learn the friendship
        // ended - that is exactly the "same as not friends" view the spec grants them.
        await SeedBlockAsync(_initiator, _target);

        await FriendRequestLifecycleHandler.Handle(new FriendRemoved
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_3",
        }, _context, _bus);

        Assert.That(_bus.Published.OfType<FriendRemovedEvent>().Count(), Is.EqualTo(1));
    }
}
