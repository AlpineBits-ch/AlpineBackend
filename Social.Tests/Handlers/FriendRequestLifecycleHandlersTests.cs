using Social.Api.Integration.Relationship.Events;
using Social.Domain.Aggregate;
using Social.Domain.Events.Relationship;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

[TestFixture]
public class FriendRequestLifecycleHandlersTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;
    private Profile _initiator = null!;
    private Profile _target = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);

        _initiator = Profile.Create(new CreateProfileParams { UserId = "user-initiator", Username = "initiator" });
        _target = Profile.Create(new CreateProfileParams { UserId = "user-target", Username = "target" });
        _context.Profiles.AddRange(_initiator, _target);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_FriendRequestCreated_ResolvesUserIdsFromProfileIds()
    {
        var result = await FriendRequestLifecycleHandlers.Handle(new FriendRequestCreated
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_1",
        }, _context);

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
        var result = await FriendRequestLifecycleHandlers.Handle(new FriendRequestRejected
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_2",
        }, _context);

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
        var result = await FriendRequestLifecycleHandlers.Handle(new FriendRemoved
        {
            InitiatorProfileId = _initiator.Id,
            TargetProfileId = _target.Id,
            RelationshipId = "rlsp_3",
        }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(result.InitiatorUserId, Is.EqualTo("user-initiator"));
            Assert.That(result.TargetUserId, Is.EqualTo("user-target"));
            Assert.That(result.RelationshipId, Is.EqualTo("rlsp_3"));
        });
    }
}
