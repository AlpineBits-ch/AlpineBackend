using System.Text.Json;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Services;

/// <summary>
/// Covers ConversationPermissionService end-to-end using an EF Core InMemory database
/// and a dictionary-backed fake distributed cache.
///
/// Each test gets its own database (Guid-named) to prevent state bleed.
///
/// Key behaviors exercised:
///   - HasPermission delegates to GetPermissionsForUser and checks set membership.
///   - GetPermissionsForUser queries ctx.Members filtered by UserId.
///   - Results are cached under "messaging:conversation-permissions:{userId}" with a 10-minute TTL.
///   - A cache hit skips the DB query entirely; rebuild=false is the default.
///   - rebuild=true bypasses the cache read and always queries the DB.
///
///   - rebuild=true always overwrites the existing cache entry with fresh DB data.
/// </summary>
[TestFixture]
public class ConversationPermissionServiceTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string UserId = "user-1";
    private const string ConversationId = "conv-1";

    // ── Per-test state ────────────────────────────────────────────────────────

    private string _dbName = null!;
    private FakeDistributedCache _cache = null!;
    private TestMessagingContext _context = null!;
    private ConversationPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _cache = new FakeDistributedCache();
        _context = new TestMessagingContext(_dbName);
        _service = new ConversationPermissionService(_context, _cache);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Entity factory helpers ────────────────────────────────────────────────

    private static ConversationMember MakeMember(
        string id, string userId, string conversationId) => new()
    {
        Id = id,
        UserId = userId,
        ConversationId = conversationId,
        PublicKey = [],
        CachedUserName = "test-user",
        CachedUserHash = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static string CacheKey(string userId) =>
        "messaging:conversation-permissions:" + userId;

    // ══════════════════════════════════════════════════════════════════════════ HasPermission —
    // membership checks ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task HasPermission_UserIsMember_ReturnsTrue()
    {
        _context.Members.Add(MakeMember("m-1", UserId, ConversationId));
        await _context.SaveChangesAsync();

        var result = await _service.HasPermission(UserId, ConversationId);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasPermission_UserIsNotMember_ReturnsFalse()
    {
        _context.Members.Add(MakeMember("m-1", "other-user", ConversationId));
        await _context.SaveChangesAsync();

        var result = await _service.HasPermission(UserId, ConversationId);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasPermission_NoMembersAtAll_ReturnsFalse()
    {
        var result = await _service.HasPermission(UserId, ConversationId);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasPermission_ConversationDoesNotExist_ReturnsFalse()
    {
        _context.Members.Add(MakeMember("m-1", UserId, ConversationId));
        await _context.SaveChangesAsync();

        var result = await _service.HasPermission(UserId, "nonexistent-conv");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasPermission_UserInMultipleConversations_AllReturnTrue()
    {
        _context.Members.AddRange(
            MakeMember("m-1", UserId, "conv-1"),
            MakeMember("m-2", UserId, "conv-2"),
            MakeMember("m-3", UserId, "conv-3"));
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.HasPermission(UserId, "conv-1"), Is.True);
            Assert.That(await _service.HasPermission(UserId, "conv-2"), Is.True);
            Assert.That(await _service.HasPermission(UserId, "conv-3"), Is.True);
        });
    }

    [Test]
    public async Task HasPermission_UserInSomeConversations_NonMemberConversationReturnsFalse()
    {
        _context.Members.AddRange(
            MakeMember("m-1", UserId, "conv-1"),
            MakeMember("m-2", UserId, "conv-2"));
        await _context.SaveChangesAsync();

        var result = await _service.HasPermission(UserId, "conv-not-member");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasPermission_MultipleUsers_OnlyActualMemberReturnsTrue()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-A", ConversationId),
            MakeMember("m-2", "user-B", ConversationId));
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.HasPermission("user-A", ConversationId), Is.True);
            Assert.That(await _service.HasPermission("user-B", ConversationId), Is.True);
            Assert.That(await _service.HasPermission("user-C", ConversationId), Is.False,
                "Non-member user must not be granted access");
        });
    }

    [Test]
    public void HasPermission_NullUserId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.HasPermission(null!, ConversationId));
    }

    [Test]
    public void HasPermission_EmptyUserId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.HasPermission("", ConversationId));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetPermissionsForUser — guard clauses
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void GetPermissionsForUser_NullUserId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.GetPermissionsForUser(null!));
    }

    [Test]
    public void GetPermissionsForUser_EmptyUserId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.GetPermissionsForUser(""));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetPermissionsForUser — database queries
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPermissionsForUser_NoMemberships_ReturnsEmptySet()
    {
        var result = await _service.GetPermissionsForUser(UserId);

        Assert.That(result.ConversationIds, Is.Empty);
    }

    [Test]
    public async Task GetPermissionsForUser_SingleMembership_ReturnsThatConversation()
    {
        _context.Members.Add(MakeMember("m-1", UserId, ConversationId));
        await _context.SaveChangesAsync();

        var result = await _service.GetPermissionsForUser(UserId);

        Assert.That(result.ConversationIds, Is.EquivalentTo(new[] { ConversationId }));
    }

    [Test]
    public async Task GetPermissionsForUser_MultipleMemberships_ReturnsAllConversations()
    {
        _context.Members.AddRange(
            MakeMember("m-1", UserId, "conv-1"),
            MakeMember("m-2", UserId, "conv-2"),
            MakeMember("m-3", UserId, "conv-3"));
        await _context.SaveChangesAsync();

        var result = await _service.GetPermissionsForUser(UserId);

        Assert.That(result.ConversationIds,
            Is.EquivalentTo(new[] { "conv-1", "conv-2", "conv-3" }));
    }

    [Test]
    public async Task GetPermissionsForUser_OtherUsersConversations_AreNotIncluded()
    {
        _context.Members.AddRange(
            MakeMember("m-1", UserId, "my-conv"),
            MakeMember("m-2", "other-user", "their-conv"));
        await _context.SaveChangesAsync();

        var result = await _service.GetPermissionsForUser(UserId);

        Assert.That(result.ConversationIds, Is.EquivalentTo(new[] { "my-conv" }),
            "Only the requesting user's conversations must be returned");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetPermissionsForUser — caching behavior
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPermissionsForUser_PopulatesCache_AfterFirstCall()
    {
        _context.Members.Add(MakeMember("m-1", UserId, ConversationId));
        await _context.SaveChangesAsync();

        await _service.GetPermissionsForUser(UserId);

        Assert.That(_cache.HasEntry(CacheKey(UserId)), Is.True,
            "Result must be written to cache on first DB query");
    }

    [Test]
    public async Task GetPermissionsForUser_CacheHit_ReturnsCachedDataWithoutQueryingDb()
    {
        // DB has a real membership; cache has different (fake) data.
        _context.Members.Add(MakeMember("m-1", UserId, "db-only-conv"));
        await _context.SaveChangesAsync();

        var fakePerms = new UserConversationPermissions
        {
            ConversationIds = ["cached-conv"]
        };
        _cache.SetEntry(CacheKey(UserId), JsonSerializer.Serialize(fakePerms));

        var result = await _service.GetPermissionsForUser(UserId);

        Assert.That(result.ConversationIds, Is.EquivalentTo(new[] { "cached-conv" }),
            "Cached data must be returned without hitting the DB");
    }

    [Test]
    public async Task GetPermissionsForUser_CacheHit_PreservesMultipleConversationIds()
    {
        var fakePerms = new UserConversationPermissions
        {
            ConversationIds = ["conv-a", "conv-b", "conv-c"]
        };
        _cache.SetEntry(CacheKey(UserId), JsonSerializer.Serialize(fakePerms));

        var result = await _service.GetPermissionsForUser(UserId);

        Assert.That(result.ConversationIds,
            Is.EquivalentTo(new[] { "conv-a", "conv-b", "conv-c" }));
    }

    [Test]
    public async Task GetPermissionsForUser_DifferentUsers_HaveIndependentCacheEntries()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-A", "conv-A"),
            MakeMember("m-2", "user-B", "conv-B"));
        await _context.SaveChangesAsync();

        var resultA = await _service.GetPermissionsForUser("user-A");
        var resultB = await _service.GetPermissionsForUser("user-B");

        Assert.Multiple(() =>
        {
            Assert.That(resultA.ConversationIds, Is.EquivalentTo(new[] { "conv-A" }));
            Assert.That(resultB.ConversationIds, Is.EquivalentTo(new[] { "conv-B" }));
            Assert.That(_cache.HasEntry(CacheKey("user-A")), Is.True);
            Assert.That(_cache.HasEntry(CacheKey("user-B")), Is.True);
        });
    }

    // ── Rebuild behavior ──────────────────────────────────────────────────────

    [Test]
    public async Task GetPermissionsForUser_RebuildTrue_NoCachedData_QueriesDbAndPopulatesCache()
    {
        _context.Members.Add(MakeMember("m-1", UserId, ConversationId));
        await _context.SaveChangesAsync();

        var result = await _service.GetPermissionsForUser(UserId, rebuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.ConversationIds, Is.EquivalentTo(new[] { ConversationId }),
                "Fresh DB data must be returned");
            Assert.That(_cache.HasEntry(CacheKey(UserId)), Is.True,
                "Cache must be populated when there was no prior entry");
        });
    }

    [Test]
    public async Task GetPermissionsForUser_RebuildTrue_WithStaleCachedData_ReturnsFreshDbData()
    {
        // Cache claims the user is in "stale-conv"; DB has a different, newer membership.
        var stalePerms = new UserConversationPermissions { ConversationIds = ["stale-conv"] };
        _cache.SetEntry(CacheKey(UserId), JsonSerializer.Serialize(stalePerms));

        _context.Members.Add(MakeMember("m-1", UserId, ConversationId));
        await _context.SaveChangesAsync();

        var result = await _service.GetPermissionsForUser(UserId, rebuild: true);

        Assert.That(result.ConversationIds, Is.EquivalentTo(new[] { ConversationId }),
            "rebuild=true must bypass the stale cache and return current DB data");
    }

    [Test]
    public async Task GetPermissionsForUser_RebuildTrue_UpdatesExistingCacheEntry()
    {
        // Stale cache: user is only in "old-conv".
        var stalePerms = new UserConversationPermissions { ConversationIds = ["old-conv"] };
        _cache.SetEntry(CacheKey(UserId), JsonSerializer.Serialize(stalePerms));

        // DB now also has a new membership that is not in the cache.
        _context.Members.AddRange(
            MakeMember("m-1", UserId, "old-conv"),
            MakeMember("m-2", UserId, "new-conv"));
        await _context.SaveChangesAsync();

        await _service.GetPermissionsForUser(UserId, rebuild: true);

        // A subsequent non-rebuild call must return the updated cache entry.
        var subsequentResult = await _service.GetPermissionsForUser(UserId);
        Assert.That(subsequentResult.ConversationIds, Contains.Item("new-conv"),
            "rebuild=true must overwrite the existing cache entry so subsequent calls see fresh data");
    }

    // ── HasPermission uses the cache ──────────────────────────────────────────

    [Test]
    public async Task HasPermission_UsesCachedPermissions_WhenCacheIsWarm()
    {
        // Pre-seed cache with a conversation the DB does not know about.
        var fakePerms = new UserConversationPermissions
        {
            ConversationIds = ["cached-conv"]
        };
        _cache.SetEntry(CacheKey(UserId), JsonSerializer.Serialize(fakePerms));

        // No DB rows — if HasPermission hits the DB it would return false.
        var result = await _service.HasPermission(UserId, "cached-conv");

        Assert.That(result, Is.True,
            "HasPermission must use the cached permission set when available");
    }

    [Test]
    public async Task HasPermission_AfterCacheRebuild_NewConversationIsVisible()
    {
        // Friend-request scenario: cache was populated before the new DM was created.
        var stalePerms = new UserConversationPermissions { ConversationIds = ["old-conv"] };
        _cache.SetEntry(CacheKey(UserId), JsonSerializer.Serialize(stalePerms));

        _context.Members.AddRange(
            MakeMember("m-1", UserId, "old-conv"),
            MakeMember("m-2", UserId, "new-dm-conv"));
        await _context.SaveChangesAsync();

        // Simulate what FriendRequestAcceptedHandler does: rebuild the cache.
        await _service.GetPermissionsForUser(UserId, rebuild: true);

        var result = await _service.HasPermission(UserId, "new-dm-conv");

        Assert.That(result, Is.True,
            "After a rebuild, HasPermission must reflect the newly added conversation");
    }

}
