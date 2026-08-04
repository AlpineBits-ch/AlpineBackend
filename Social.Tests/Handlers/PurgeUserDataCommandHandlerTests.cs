using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Social.Api.Commands;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

/// <summary>
/// Covers the bug fix in PurgeUserDataCommandHandler: Relationship.OwnerId/TargetId store
/// Profile ids (not Identity user ids), so the relationship-purge query must resolve the local
/// Profile first and filter on its Id - filtering directly on command.UserId (the original code)
/// never matched a row, silently leaving the purged user in former friends' relationship lists.
/// </summary>
[TestFixture]
public class PurgeUserDataCommandHandlerTests
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

    private async Task<PurgeUserDataCommandResponse> Purge(string userId)
    {
        var response = await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = userId }, _context);
        await _context.SaveChangesAsync();
        return response;
    }

    [Test]
    public async Task Handle_ExistingProfile_AnonymizesUserNameAndClearsCustomization()
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = "user-123456", Username = "tester" });
        profile.Bio = "old bio";
        profile.AccentColor = "#FFFFFF";
        profile.Font = ProfileFont.Serif;
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();

        await Purge("user-123456");

        var stored = _context.Profiles.Single(p => p.UserId == "user-123456");
        Assert.Multiple(() =>
        {
            Assert.That(stored.UserName, Is.EqualTo("Deleted User 123456"));
            Assert.That(stored.Bio, Is.Null);
            Assert.That(stored.AccentColor, Is.Null);
            Assert.That(stored.Font, Is.EqualTo(ProfileFont.Default));
        });
    }

    [Test]
    public async Task Handle_ShortUserId_UsesWholeIdAsSuffix()
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = "abc", Username = "tester" });
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();

        await Purge("abc");

        var stored = _context.Profiles.Single(p => p.UserId == "abc");
        Assert.That(stored.UserName, Is.EqualTo("Deleted User abc"));
    }

    [Test]
    public async Task Handle_RemovesBothDirectionsOfRelationships()
    {
        var purged = Profile.Create(new CreateProfileParams { UserId = "user-purge", Username = "purged" });
        var friendA = Profile.Create(new CreateProfileParams { UserId = "user-a", Username = "friend-a" });
        var friendB = Profile.Create(new CreateProfileParams { UserId = "user-b", Username = "friend-b" });
        _context.Profiles.AddRange(purged, friendA, friendB);
        await _context.SaveChangesAsync();

        // purged is the Owner in one relationship and the Target in another - both must be removed.
        _context.Relationships.AddRange(
            new Relationship { Id = "rlsp_1", OwnerId = purged.Id, TargetId = friendA.Id, Status = RelationshipStatus.Friends },
            new Relationship { Id = "rlsp_2", OwnerId = friendA.Id, TargetId = purged.Id, Status = RelationshipStatus.Friends },
            new Relationship { Id = "rlsp_3", OwnerId = friendB.Id, TargetId = purged.Id, Status = RelationshipStatus.PendingIncoming },
            // Unrelated relationship between two other profiles must survive.
            new Relationship { Id = "rlsp_4", OwnerId = friendA.Id, TargetId = friendB.Id, Status = RelationshipStatus.Friends });
        await _context.SaveChangesAsync();

        await Purge("user-purge");

        Assert.Multiple(() =>
        {
            Assert.That(_context.Relationships.Any(r => r.Id == "rlsp_1"), Is.False);
            Assert.That(_context.Relationships.Any(r => r.Id == "rlsp_2"), Is.False);
            Assert.That(_context.Relationships.Any(r => r.Id == "rlsp_3"), Is.False);
            Assert.That(_context.Relationships.Any(r => r.Id == "rlsp_4"), Is.True, "unrelated relationship must not be touched");
        });
    }

    [Test]
    public async Task Handle_DeletesBlocksInBothDirections()
    {
        // Privacy spec T0-3: "blocks referencing a purged user are deleted by Social's
        // PurgeUserDataCommandHandler". A surviving block would let a deleted account go on
        // suppressing a live one's messages with no way for anyone to lift it.
        var purged = Profile.Create(new CreateProfileParams { UserId = "user-purge", Username = "purged" });
        var other = Profile.Create(new CreateProfileParams { UserId = "user-a", Username = "other" });
        var bystanderA = Profile.Create(new CreateProfileParams { UserId = "user-b", Username = "b" });
        var bystanderB = Profile.Create(new CreateProfileParams { UserId = "user-c", Username = "c" });
        _context.Profiles.AddRange(purged, other, bystanderA, bystanderB);
        await _context.SaveChangesAsync();

        _context.Relationships.AddRange(
            new Relationship { Id = "blck_1", OwnerId = purged.Id, TargetId = other.Id, Status = RelationshipStatus.Blocked },
            new Relationship { Id = "blck_2", OwnerId = other.Id, TargetId = purged.Id, Status = RelationshipStatus.Blocked },
            new Relationship { Id = "blck_3", OwnerId = bystanderA.Id, TargetId = bystanderB.Id, Status = RelationshipStatus.Blocked });
        await _context.SaveChangesAsync();

        await Purge("user-purge");

        Assert.Multiple(() =>
        {
            Assert.That(_context.Relationships.Any(r => r.Id == "blck_1"), Is.False, "blocks the purged user placed");
            Assert.That(_context.Relationships.Any(r => r.Id == "blck_2"), Is.False, "blocks placed against the purged user");
            Assert.That(_context.Relationships.Any(r => r.Id == "blck_3"), Is.True, "an unrelated block must survive");
        });
    }

    [Test]
    public async Task Handle_UnknownUserId_DoesNotThrowAndReturnsResponse()
    {
        var response = await Purge("no-such-user");

        Assert.Multiple(() =>
        {
            Assert.That(response.UserId, Is.EqualTo("no-such-user"));
            Assert.That(response.Service, Is.EqualTo("social"));
        });
    }
}
