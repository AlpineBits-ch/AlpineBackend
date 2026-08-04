using Social.Api.Integration.Relationship.Consumers;
using Social.Contracts.Bus.Integration.Request;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

/// <summary>The cross-service half of blocking (privacy spec T0-3).</summary>
[TestFixture]
public class GetBlockRelationshipsHandlerTests
{
    private TestSocialContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestSocialContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Profile> AddProfile(string userId, string userName)
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = userId, Username = userName });
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();
        return profile;
    }

    private async Task AddRelationship(Profile owner, Profile target, RelationshipStatus status)
    {
        _context.Relationships.Add(new Relationship
        {
            Id = Relationship.GenerateId(), OwnerId = owner.Id, TargetId = target.Id, Status = status,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Handle_ReturnsBlocksWhereTheUserIsTheBlocker()
    {
        var a = await AddProfile("user-a", "a");
        var b = await AddProfile("user-b", "b");
        await AddRelationship(a, b, RelationshipStatus.Blocked);

        var response = await GetBlockRelationshipsHandler.Handle(
            new GetBlockRelationshipsRequest { UserIds = ["user-a"] }, _context);

        var block = response.Blocks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(block.BlockerId, Is.EqualTo("user-a"));
            Assert.That(block.BlockedId, Is.EqualTo("user-b"));
        });
    }

    [Test]
    public async Task Handle_ReturnsBlocksWhereTheUserIsTheBlockedParty()
    {
        // The blocked party never sees this through the API, but the *services* enforcing on their
        // behalf have to - that is how "cannot reach the blocker" gets applied.
        var a = await AddProfile("user-a", "a");
        var b = await AddProfile("user-b", "b");
        await AddRelationship(a, b, RelationshipStatus.Blocked);

        var response = await GetBlockRelationshipsHandler.Handle(
            new GetBlockRelationshipsRequest { UserIds = ["user-b"] }, _context);

        Assert.That(response.Blocks.Single().BlockerId, Is.EqualTo("user-a"));
    }

    [Test]
    public async Task Handle_ReturnsBothDirectionsAsSeparateFacts()
    {
        var a = await AddProfile("user-a", "a");
        var b = await AddProfile("user-b", "b");
        await AddRelationship(a, b, RelationshipStatus.Blocked);
        await AddRelationship(b, a, RelationshipStatus.Blocked);

        var response = await GetBlockRelationshipsHandler.Handle(
            new GetBlockRelationshipsRequest { UserIds = ["user-a", "user-b"] }, _context);

        Assert.That(response.Blocks, Has.Count.EqualTo(2));
        Assert.That(response.Blocks.Select(b2 => b2.BlockerId), Is.EquivalentTo(new[] { "user-a", "user-b" }));
    }

    // ── negative ─────────────────────────────────────────────────────────────

    [TestCase(RelationshipStatus.Friends)]
    [TestCase(RelationshipStatus.PendingIncoming)]
    [TestCase(RelationshipStatus.PendingOutgoing)]
    [TestCase(RelationshipStatus.None)]
    public async Task Handle_NonBlockRowsAreNeverReturned(RelationshipStatus status)
    {
        var a = await AddProfile("user-a", "a");
        var b = await AddProfile("user-b", "b");
        await AddRelationship(a, b, status);

        var response = await GetBlockRelationshipsHandler.Handle(
            new GetBlockRelationshipsRequest { UserIds = ["user-a", "user-b"] }, _context);

        Assert.That(response.Blocks, Is.Empty);
    }

    [Test]
    public async Task Handle_UnrelatedBlockIsNotReturned()
    {
        var a = await AddProfile("user-a", "a");
        var b = await AddProfile("user-b", "b");
        var c = await AddProfile("user-c", "c");
        await AddRelationship(b, c, RelationshipStatus.Blocked);

        var response = await GetBlockRelationshipsHandler.Handle(
            new GetBlockRelationshipsRequest { UserIds = ["user-a"] }, _context);

        Assert.That(response.Blocks, Is.Empty);
    }

    [Test]
    public async Task Handle_EmptyRequest_ReturnsEmpty()
    {
        var response = await GetBlockRelationshipsHandler.Handle(
            new GetBlockRelationshipsRequest { UserIds = [] }, _context);

        Assert.That(response.Blocks, Is.Empty);
    }
}
