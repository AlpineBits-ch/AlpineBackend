using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Response;
using Social.Api.Endpoints;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Endpoints;

/// <summary>
/// Blocking end to end inside Social (privacy spec T0-3): the one-directional row, the friendship
/// teardown, idempotency in both directions, the published cache-eviction events, and the fact that
/// the blocked party never gains a row of their own.
/// </summary>
[TestFixture]
public class BlockEndpointsTests
{
    private TestSocialContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _bus = new FakeMessageBus();
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

    private async Task<(Relationship A, Relationship B)> SeedFriendship(Profile a, Profile b)
    {
        var ab = new Relationship { Id = "rlsp_ab", OwnerId = a.Id, TargetId = b.Id, Status = RelationshipStatus.Friends, RelatedId = "rlsp_ba" };
        var ba = new Relationship { Id = "rlsp_ba", OwnerId = b.Id, TargetId = a.Id, Status = RelationshipStatus.Friends, RelatedId = "rlsp_ab" };
        _context.Relationships.AddRange(ab, ba);
        await _context.SaveChangesAsync();
        return (ab, ba);
    }

    private Task<Microsoft.AspNetCore.Http.IResult> BlockAsync(string caller, string target) =>
        BlockEndpoints.BlockAsync(target, _context, _cache, _bus, MakeUser(caller));

    private Task<Microsoft.AspNetCore.Http.IResult> UnblockAsync(string caller, string target) =>
        BlockEndpoints.UnblockAsync(target, _context, _cache, _bus, MakeUser(caller));

    // ── normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Block_CreatesAOneSidedRowOwnedByTheBlocker()
    {
        var blocker = await AddProfile("user-a", "blocker");
        var blocked = await AddProfile("user-b", "blocked");

        var result = await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());

        var rows = await _context.Relationships.ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1), "blocking must not mint a mirrored row for the blocked party");
            Assert.That(rows[0].OwnerId, Is.EqualTo(blocker.Id));
            Assert.That(rows[0].TargetId, Is.EqualTo(blocked.Id));
            Assert.That(rows[0].Status, Is.EqualTo(RelationshipStatus.Blocked));
            Assert.That(rows[0].RelatedId, Is.Null);
        });
    }

    [Test]
    public async Task Block_PublishesUserBlockedEventForCacheEviction()
    {
        await AddProfile("user-a", "blocker");
        await AddProfile("user-b", "blocked");

        await BlockAsync("user-a", "user-b");

        var published = _bus.Published.OfType<UserBlockedEvent>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(published.BlockerId, Is.EqualTo("user-a"));
            Assert.That(published.BlockedId, Is.EqualTo("user-b"));
        });
    }

    [Test]
    public async Task Block_EvictsBothIntegrationProfileCacheEntries()
    {
        await AddProfile("user-a", "blocker");
        await AddProfile("user-b", "blocked");
        _cache.SetEntry("integration_profile:user_id:user-a", "stale-a");
        _cache.SetEntry("integration_profile:user_id:user-b", "stale-b");

        await BlockAsync("user-a", "user-b");

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry("integration_profile:user_id:user-a"), Is.False);
            Assert.That(_cache.HasEntry("integration_profile:user_id:user-b"), Is.False);
        });
    }

    [Test]
    public async Task Block_RemovesAnExistingFriendshipOnBothSides()
    {
        var blocker = await AddProfile("user-a", "blocker");
        var blocked = await AddProfile("user-b", "blocked");
        await SeedFriendship(blocker, blocked);

        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        var forward = await _context.Relationships.SingleAsync(r => r.Id == "rlsp_ab");
        var reverse = await _context.Relationships.SingleAsync(r => r.Id == "rlsp_ba");

        Assert.Multiple(() =>
        {
            Assert.That(forward.Status, Is.EqualTo(RelationshipStatus.Blocked));
            Assert.That(reverse.Status, Is.EqualTo(RelationshipStatus.None),
                "the blocked party must see an ordinary un-friending, not a block");
            Assert.That(forward.RelatedId, Is.Null);
            Assert.That(reverse.RelatedId, Is.Null);
        });
    }

    [Test]
    public async Task Block_CancelsAPendingRequestInEitherDirection()
    {
        var blocker = await AddProfile("user-a", "blocker");
        var blocked = await AddProfile("user-b", "blocked");
        _context.Relationships.AddRange(
            new Relationship { Id = "rlsp_in", OwnerId = blocker.Id, TargetId = blocked.Id, Status = RelationshipStatus.PendingIncoming, RelatedId = "rlsp_out" },
            new Relationship { Id = "rlsp_out", OwnerId = blocked.Id, TargetId = blocker.Id, Status = RelationshipStatus.PendingOutgoing, RelatedId = "rlsp_in" });
        await _context.SaveChangesAsync();

        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.Relationships.Single(r => r.Id == "rlsp_in").Status, Is.EqualTo(RelationshipStatus.Blocked));
            Assert.That(_context.Relationships.Single(r => r.Id == "rlsp_out").Status, Is.EqualTo(RelationshipStatus.None));
        });
    }

    // ── idempotency / edge ───────────────────────────────────────────────────

    [Test]
    public async Task Block_CalledTwice_StaysOneRowAndPublishesOnce()
    {
        await AddProfile("user-a", "blocker");
        await AddProfile("user-b", "blocked");

        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();
        var second = await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.InstanceOf<NoContent>());
            Assert.That(_context.Relationships.Count(), Is.EqualTo(1));
            Assert.That(_bus.Published.OfType<UserBlockedEvent>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Block_Self_ReturnsBadRequest()
    {
        await AddProfile("user-a", "blocker");

        var result = await BlockAsync("user-a", "user-a");

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Block_UnknownUser_ReturnsNotFound()
    {
        await AddProfile("user-a", "blocker");

        var result = await BlockAsync("user-a", "nobody");

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Block_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var result = await BlockEndpoints.BlockAsync("user-b", _context, _cache, _bus, new ClaimsPrincipal());

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    // ── unblock ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Unblock_DeletesTheRowSoThePairCanBecomeFriendsAgain()
    {
        await AddProfile("user-a", "blocker");
        await AddProfile("user-b", "blocked");
        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        var result = await UnblockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(_context.Relationships.Count(), Is.Zero,
                "a lingering row would trip the 'relationship already exists' guard forever");
            Assert.That(_bus.Published.OfType<UserUnblockedEvent>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Unblock_AlsoSweepsTheInertCounterpartRowLeftByTheBlock()
    {
        var blocker = await AddProfile("user-a", "blocker");
        var blocked = await AddProfile("user-b", "blocked");
        await SeedFriendship(blocker, blocked);

        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();
        await UnblockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        Assert.That(_context.Relationships.Count(), Is.Zero);
    }

    [Test]
    public async Task Unblock_WhenNotBlocked_IsANoOpAndPublishesNothing()
    {
        await AddProfile("user-a", "blocker");
        await AddProfile("user-b", "other");

        var result = await UnblockAsync("user-a", "user-b");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(_bus.Published, Is.Empty);
        });
    }

    [Test]
    public async Task Block_DoesNotClearAnExistingBlockInTheOtherDirection()
    {
        // Negative, and the sharpest one here: if blocking back cleared the other party's block,
        // the blocked user would have a one-tap way to both detect and defeat it.
        var a = await AddProfile("user-a", "a");
        var b = await AddProfile("user-b", "b");
        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        await BlockAsync("user-b", "user-a");
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.Relationships.Single(r => r.OwnerId == a.Id).Status, Is.EqualTo(RelationshipStatus.Blocked));
            Assert.That(_context.Relationships.Single(r => r.OwnerId == b.Id).Status, Is.EqualTo(RelationshipStatus.Blocked));
        });
    }

    [Test]
    public async Task Unblock_LeavesTheOtherPartysOwnBlockInPlace()
    {
        // Negative: A lifting their block must not lift B's. Blocks are independent facts.
        var a = await AddProfile("user-a", "a");
        var b = await AddProfile("user-b", "b");
        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();
        await BlockAsync("user-b", "user-a");
        await _context.SaveChangesAsync();

        await UnblockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        var remaining = _context.Relationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(remaining.OwnerId, Is.EqualTo(b.Id));
            Assert.That(remaining.TargetId, Is.EqualTo(a.Id));
            Assert.That(remaining.Status, Is.EqualTo(RelationshipStatus.Blocked));
        });
    }

    [Test]
    public async Task Revoke_CannotBeUsedToLiftABlock()
    {
        // The unfriend endpoint takes a relationship id, and a block *is* a relationship row. Left
        // unguarded it would clear the block without publishing UserUnblockedEvent, so every other
        // service's cache would go on enforcing a block Social no longer had.
        var a = await AddProfile("user-a", "a");
        await AddProfile("user-b", "b");
        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();
        var blockRow = _context.Relationships.Single();

        await FriendshipEndpoints.RevokeAsync(blockRow.Id, _context, _cache, MakeUser("user-a"));
        await _context.SaveChangesAsync();

        Assert.That(_context.Relationships.Single().Status, Is.EqualTo(RelationshipStatus.Blocked));
    }

    // ── list ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task ListBlocked_ReturnsOnlyTheCallersOwnBlocks()
    {
        await AddProfile("user-a", "a");
        await AddProfile("user-b", "b");
        await AddProfile("user-c", "c");

        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();
        await BlockAsync("user-c", "user-a");
        await _context.SaveChangesAsync();

        var result = await BlockEndpoints.ListBlockedAsync(_context, MakeUser("user-a"));
        var page = ((Ok<BlockedUsersPageDto>)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(page.Blocked, Has.Count.EqualTo(1));
            Assert.That(page.Blocked[0].UserId, Is.EqualTo("user-b"));
            Assert.That(page.NextCursor, Is.Null);
        });
    }

    [Test]
    public async Task ListBlocked_ForTheBlockedParty_IsEmpty()
    {
        // The whole asymmetry in one assertion: B has no row, so B has no way to observe the block.
        await AddProfile("user-a", "a");
        await AddProfile("user-b", "b");
        await BlockAsync("user-a", "user-b");
        await _context.SaveChangesAsync();

        var result = await BlockEndpoints.ListBlockedAsync(_context, MakeUser("user-b"));
        var page = ((Ok<BlockedUsersPageDto>)result).Value!;

        Assert.That(page.Blocked, Is.Empty);
    }

    [Test]
    public async Task ListBlocked_PagesWithACursor()
    {
        await AddProfile("user-a", "a");
        for (var i = 0; i < 3; i++) await AddProfile($"user-{i}", $"u{i}");
        for (var i = 0; i < 3; i++)
        {
            await BlockAsync("user-a", $"user-{i}");
            await _context.SaveChangesAsync();
        }

        var first = ((Ok<BlockedUsersPageDto>)await BlockEndpoints.ListBlockedAsync(_context, MakeUser("user-a"), limit: 2)).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(first.Blocked, Has.Count.EqualTo(2));
            Assert.That(first.NextCursor, Is.Not.Null);
        });

        var second = ((Ok<BlockedUsersPageDto>)await BlockEndpoints.ListBlockedAsync(
            _context, MakeUser("user-a"), limit: 2, cursor: first.NextCursor)).Value!;

        var allIds = first.Blocked.Concat(second.Blocked).Select(b => b.UserId).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(second.Blocked, Has.Count.EqualTo(1));
            Assert.That(allIds, Is.Unique);
            Assert.That(allIds, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public async Task ListBlocked_MalformedCursor_ReturnsBadRequest()
    {
        await AddProfile("user-a", "a");

        var result = await BlockEndpoints.ListBlockedAsync(_context, MakeUser("user-a"), cursor: "!!!not-base64!!!");

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }
}
