using Social.Api.Integration.Relationship.Events;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Domain.Events.Relationship;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

[TestFixture]
public class FriendshipAcceptedHandlerTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_ExistingRelationship_BuildsEventFromOwnerAndTarget()
    {
        var owner = Profile.Create(new CreateProfileParams { UserId = "user-owner", Username = "owner-name" });
        var target = Profile.Create(new CreateProfileParams { UserId = "user-target", Username = "target-name" });
        _context.Profiles.AddRange(owner, target);
        await _context.SaveChangesAsync();

        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_1",
            OwnerId = owner.Id,
            TargetId = target.Id,
            Status = RelationshipStatus.Friends,
        });
        await _context.SaveChangesAsync();

        var result = FriendshipAcceptedHandler.Handle(new FriendRequestAccepted
        {
            TargetProfileId = owner.Id,
            InitiatorProfileId = target.Id,
            RelationshipId = "rlsp_1",
        }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(result.AcceptantUserId, Is.EqualTo("user-owner"));
            Assert.That(result.AcceptantUserName, Is.EqualTo("owner-name"));
            Assert.That(result.InitiatorUserId, Is.EqualTo("user-target"));
            Assert.That(result.InitiatorUserName, Is.EqualTo("target-name"));
            Assert.That(result.FriendshipId, Is.EqualTo("rlsp_1"));
        });
    }

    [Test]
    public void Handle_UnknownRelationshipId_Throws()
    {
        Assert.Throws<Exception>(() => FriendshipAcceptedHandler.Handle(new FriendRequestAccepted
        {
            TargetProfileId = "prfl_x",
            InitiatorProfileId = "prfl_y",
            RelationshipId = "does-not-exist",
        }, _context));
    }
}
