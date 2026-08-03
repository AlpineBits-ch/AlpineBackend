using System.Security.Claims;
using Social.Api.Dtos.Request;
using Social.Api.Endpoints;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Domain.Events.Relationship;
using Social.Tests.Helpers;

namespace Social.Tests.Endpoints;

/// <summary>
/// Covers FriendshipEndpoints (WolverineHttp static endpoints, not bus-dispatched handlers).
/// Also exercises the fix for three separate missing-SaveChangesAsync bugs (Create/Accept/Reject/
/// Revoke all mutated the DbContext without committing - WolverineHttp endpoints don't get the
/// auto-commit middleware bus handlers get) plus the RejectAsync fix that now also rejects the
/// Related side, matching Accept/Revoke's existing both-sides behavior.
/// </summary>
[TestFixture]
public class FriendshipEndpointsTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
        _cache = new FakeDistributedCache();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ClaimsPrincipal MakeUser(string userId) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private async Task<Profile> AddProfile(string userId, string userName)
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = userId, Username = userName });
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();
        return profile;
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var result = await FriendshipEndpoints.CreateAsync(
            new CreateFriendshipDto { UserName = "target" }, _context, new ClaimsPrincipal());

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateAsync_InitiatorProfileMissing_ReturnsBadRequest()
    {
        var result = await FriendshipEndpoints.CreateAsync(
            new CreateFriendshipDto { UserName = "target" }, _context, MakeUser("no-such-user"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>());
    }

    [Test]
    public async Task CreateAsync_TargetProfileMissing_ReturnsBadRequest()
    {
        await AddProfile("user-a", "initiator");

        var result = await FriendshipEndpoints.CreateAsync(
            new CreateFriendshipDto { UserName = "no-such-target" }, _context, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>());
    }

    [Test]
    public async Task CreateAsync_TargetIsSelf_ReturnsBadRequest()
    {
        await AddProfile("user-a", "solo");

        var result = await FriendshipEndpoints.CreateAsync(
            new CreateFriendshipDto { UserName = "solo" }, _context, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>());
    }

    [Test]
    public async Task CreateAsync_RelationshipAlreadyExists_ReturnsConflict()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_existing", OwnerId = initiator.Id, TargetId = target.Id, Status = RelationshipStatus.PendingOutgoing,
        });
        await _context.SaveChangesAsync();

        var result = await FriendshipEndpoints.CreateAsync(
            new CreateFriendshipDto { UserName = "target" }, _context, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>());
    }

    [Test]
    public async Task CreateAsync_ValidRequest_PersistsBothRelationshipRowsCrossLinked()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");

        var result = await FriendshipEndpoints.CreateAsync(
            new CreateFriendshipDto { UserName = "target" }, _context, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Ok>());

        // CreateAsync's second half (Add(second) + first.RelatedId = second.Id) relies on
        // WolverineHttp's auto-commit, same as Accept/Reject/Revoke - simulate that here.
        await _context.SaveChangesAsync();

        // Regression coverage: both rows (not just the first) must be persisted, and RelatedId
        // must be restored on the outgoing side.
        Assert.That(_context.Relationships.Count(), Is.EqualTo(2));

        var outgoing = _context.Relationships.Single(r => r.OwnerId == initiator.Id);
        var incoming = _context.Relationships.Single(r => r.OwnerId == target.Id);

        Assert.Multiple(() =>
        {
            Assert.That(outgoing.Status, Is.EqualTo(RelationshipStatus.PendingOutgoing));
            Assert.That(outgoing.RelatedId, Is.EqualTo(incoming.Id));
            Assert.That(incoming.Status, Is.EqualTo(RelationshipStatus.PendingIncoming));
            Assert.That(incoming.RelatedId, Is.EqualTo(outgoing.Id));
        });
    }

    // ── AcceptAsync ──────────────────────────────────────────────────────────

    private async Task<(Relationship Incoming, Relationship Outgoing)> SeedPendingPair(Profile initiator, Profile target)
    {
        var outgoing = new Relationship
        {
            Id = "rlsp_out", OwnerId = initiator.Id, TargetId = target.Id,
            Status = RelationshipStatus.PendingOutgoing, RelatedId = "rlsp_in",
        };
        var incoming = new Relationship
        {
            Id = "rlsp_in", OwnerId = target.Id, TargetId = initiator.Id,
            Status = RelationshipStatus.PendingIncoming, RelatedId = "rlsp_out",
        };
        _context.Relationships.AddRange(outgoing, incoming);
        await _context.SaveChangesAsync();
        return (incoming, outgoing);
    }

    [Test]
    public async Task AcceptAsync_UnknownId_ReturnsNotFound()
    {
        var result = await FriendshipEndpoints.AcceptAsync("does-not-exist", _context, _cache, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.NotFound>());
    }

    [Test]
    public async Task AcceptAsync_CallerIsNotOwner_ReturnsForbid()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        await SeedPendingPair(initiator, target);

        // rlsp_in is owned by target (user-b); calling as the initiator must be rejected.
        var result = await FriendshipEndpoints.AcceptAsync("rlsp_in", _context, _cache, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>());
    }

    [Test]
    public async Task AcceptAsync_ValidOwner_SetsBothSidesToFriendsAndPersists()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        await SeedPendingPair(initiator, target);

        var result = await FriendshipEndpoints.AcceptAsync("rlsp_in", _context, _cache, MakeUser("user-b"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Accepted>());

        var incoming = _context.Relationships.Single(r => r.Id == "rlsp_in");
        var outgoing = _context.Relationships.Single(r => r.Id == "rlsp_out");
        Assert.Multiple(() =>
        {
            Assert.That(incoming.Status, Is.EqualTo(RelationshipStatus.Friends));
            Assert.That(outgoing.Status, Is.EqualTo(RelationshipStatus.Friends), "Related side must also flip - previously never persisted at all");
        });
    }

    [Test]
    public async Task AcceptAsync_InvalidatesBothUsersProfileCache()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        await SeedPendingPair(initiator, target);

        _cache.SetEntry("integration_profile:user_id:user-a", "stale-a");
        _cache.SetEntry("integration_profile:user_id:user-b", "stale-b");

        await FriendshipEndpoints.AcceptAsync("rlsp_in", _context, _cache, MakeUser("user-b"));

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry("integration_profile:user_id:user-a"), Is.False);
            Assert.That(_cache.HasEntry("integration_profile:user_id:user-b"), Is.False);
        });
    }

    // ── AcceptAsync idempotency (double accept) ──────────────────────────────

    [Test]
    public async Task AcceptAsync_CalledTwice_StillLeavesExactlyOneRelationshipPair()
    {
        // Full flow rather than a seeded pair: the concern is that a double accept could end up
        // duplicating the relationship rows themselves, so the create path has to be in scope.
        var initiator = await AddProfile("user-a", "initiator");
        await AddProfile("user-b", "target");

        await FriendshipEndpoints.CreateAsync(
            new CreateFriendshipDto { UserName = "target" }, _context, MakeUser("user-a"));
        await _context.SaveChangesAsync();

        var incoming = _context.Relationships.Single(r => r.OwnerId != initiator.Id);

        // SaveChangesAsync between the two calls stands in for WolverineHttp's per-request
        // auto-commit, so the second call is a genuine second request against committed state.
        var first = await FriendshipEndpoints.AcceptAsync(incoming.Id, _context, _cache, MakeUser("user-b"));
        await _context.SaveChangesAsync();
        var second = await FriendshipEndpoints.AcceptAsync(incoming.Id, _context, _cache, MakeUser("user-b"));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Accepted>());
            Assert.That(second, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Accepted>(),
                "a repeated accept must stay successful/idempotent, not error");
            Assert.That(_context.Relationships.Count(), Is.EqualTo(2),
                "accepting twice must not add a second pair of relationship rows");
            Assert.That(_context.Relationships.Count(r => r.Status == RelationshipStatus.Friends), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task AcceptAsync_CalledTwice_RaisesFriendRequestAcceptedOnlyOnce()
    {
        // The event is what drives both the federation outbound message and the
        // social.FriendRequestAccepted push - raising it twice would show the friend twice
        // client-side and re-federate an already-federated acceptance.
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        var (incoming, outgoing) = await SeedPendingPair(initiator, target);

        await FriendshipEndpoints.AcceptAsync("rlsp_in", _context, _cache, MakeUser("user-b"));
        await FriendshipEndpoints.AcceptAsync("rlsp_in", _context, _cache, MakeUser("user-b"));

        Assert.Multiple(() =>
        {
            Assert.That(incoming.GetDomainEvents().OfType<FriendRequestAccepted>().Count(), Is.EqualTo(1));
            Assert.That(outgoing.GetDomainEvents(), Is.Empty,
                "the mirrored PendingOutgoing side never raises the accepted event");
        });
    }

    [Test]
    public async Task AcceptAsync_OnClearedRelationship_ReturnsBadRequestAndStaysCleared()
    {
        // A rejected/revoked request keeps its rows around at Status = None. Accepting one of
        // those would conjure a friendship out of a request the other side already refused.
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        await SeedPendingPair(initiator, target);
        await FriendshipEndpoints.RejectAsync("rlsp_in", _context, MakeUser("user-b"));

        var result = await FriendshipEndpoints.AcceptAsync("rlsp_in", _context, _cache, MakeUser("user-b"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>());
            Assert.That(_context.Relationships.Single(r => r.Id == "rlsp_in").Status, Is.EqualTo(RelationshipStatus.None));
            Assert.That(_context.Relationships.Single(r => r.Id == "rlsp_out").Status, Is.EqualTo(RelationshipStatus.None));
        });
    }

    // ── RejectAsync ──────────────────────────────────────────────────────────

    [Test]
    public async Task RejectAsync_UnknownId_ReturnsNotFound()
    {
        var result = await FriendshipEndpoints.RejectAsync("does-not-exist", _context, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.NotFound>());
    }

    [Test]
    public async Task RejectAsync_ValidRequest_ClearsBothSidesAndPersists()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        await SeedPendingPair(initiator, target);

        var result = await FriendshipEndpoints.RejectAsync("rlsp_in", _context, MakeUser("user-b"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Accepted>());

        var incoming = _context.Relationships.Single(r => r.Id == "rlsp_in");
        var outgoing = _context.Relationships.Single(r => r.Id == "rlsp_out");
        Assert.Multiple(() =>
        {
            Assert.That(incoming.Status, Is.EqualTo(RelationshipStatus.None));
            Assert.That(outgoing.Status, Is.EqualTo(RelationshipStatus.None),
                "Related (initiator's) side must also clear, otherwise it stays stuck at PendingOutgoing forever");
        });
    }

    // ── RevokeAsync ──────────────────────────────────────────────────────────

    [Test]
    public async Task RevokeAsync_UnknownId_ReturnsNotFound()
    {
        var result = await FriendshipEndpoints.RevokeAsync("does-not-exist", _context, _cache, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.NotFound>());
    }

    [Test]
    public async Task RevokeAsync_ValidRequest_ClearsBothSidesAndPersists()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        var outgoing = new Relationship
        {
            Id = "rlsp_out", OwnerId = initiator.Id, TargetId = target.Id,
            Status = RelationshipStatus.Friends, RelatedId = "rlsp_in",
        };
        var incoming = new Relationship
        {
            Id = "rlsp_in", OwnerId = target.Id, TargetId = initiator.Id,
            Status = RelationshipStatus.Friends, RelatedId = "rlsp_out",
        };
        _context.Relationships.AddRange(outgoing, incoming);
        await _context.SaveChangesAsync();

        var result = await FriendshipEndpoints.RevokeAsync("rlsp_out", _context, _cache, MakeUser("user-a"));

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Ok>());

        Assert.Multiple(() =>
        {
            Assert.That(_context.Relationships.Single(r => r.Id == "rlsp_out").Status, Is.EqualTo(RelationshipStatus.None));
            Assert.That(_context.Relationships.Single(r => r.Id == "rlsp_in").Status, Is.EqualTo(RelationshipStatus.None),
                "previously never persisted at all without a SaveChangesAsync call");
        });
    }

    [Test]
    public async Task RejectAsync_CalledTwice_RaisesFriendRequestRejectedOnlyOnce()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        var (incoming, _) = await SeedPendingPair(initiator, target);

        await FriendshipEndpoints.RejectAsync("rlsp_in", _context, MakeUser("user-b"));
        await FriendshipEndpoints.RejectAsync("rlsp_in", _context, MakeUser("user-b"));

        Assert.That(incoming.GetDomainEvents().OfType<FriendRequestRejected>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task RevokeAsync_CalledTwice_RaisesFriendRemovedOnlyOnce()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        var (incoming, outgoing) = await SeedPendingPair(initiator, target);

        await FriendshipEndpoints.RevokeAsync("rlsp_out", _context, _cache, MakeUser("user-a"));
        await FriendshipEndpoints.RevokeAsync("rlsp_out", _context, _cache, MakeUser("user-a"));

        Assert.Multiple(() =>
        {
            Assert.That(outgoing.GetDomainEvents().OfType<FriendRemoved>().Count(), Is.EqualTo(1));
            Assert.That(incoming.GetDomainEvents().OfType<FriendRemoved>().Count(), Is.EqualTo(1));
        });
    }
}
