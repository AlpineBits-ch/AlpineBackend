using Guild.Application.Bus.Events.Role;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Role;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Events;

[TestFixture]
public class RoleUpdatedHandlerTests
{
    private const string GuildId = "guild-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1"; // GuildMember.Id (prefixed entity ID)
    private const string UserId = "user-1";     // GuildMember.UserId (auth identity - the cache key)

    private string _dbName = null!;
    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(_dbName);
        _service = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Role MakeRole(string id = RoleId, Permissions permissions = Permissions.SendMessages) => new()
    {
        Id = id, GuildId = GuildId, Type = RoleType.None, Name = "test-role",
        Permissions = permissions,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GuildMember MakeGuildMember(string id = MemberId, string userId = UserId) => new()
    {
        Id = id, GuildId = GuildId, UserId = userId,
        JoinedAt = DateTime.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        SearchValue = $"{userId}#{GuildId}",
    };

    private static RoleMember MakeRoleMember(string id, string roleId, string memberId) => new()
    {
        Id = id, RoleId = roleId, MemberId = memberId,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task Handle_RemoveMember_InvalidatesRemovedMemberCacheImmediately()
    {
        // Post-removal DB state: role exists but member is no longer in role.Members.
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        await _context.SaveChangesAsync();

        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(cacheKey, "stale-data");

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = MemberId },
            _context, _service, new FakeMessageBus());

        Assert.That(_cache.HasEntry(cacheKey), Is.False,
            "Removed member's cache must be cleared immediately so the permission loss takes effect");
    }

    [Test]
    public async Task Handle_AddMember_InvalidatesAddedMemberCache()
    {
        // Post-add DB state: the new RoleMember is already persisted.
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(cacheKey, "stale-data");

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = MemberId },
            _context, _service, new FakeMessageBus());

        Assert.That(_cache.HasEntry(cacheKey), Is.False,
            "Added member's cache must be cleared so new role permissions take effect immediately");
    }

    [Test]
    public async Task Handle_UpdateRolePermissions_InvalidatesAllMembersCache()
    {
        const string MemberId2 = "member-2";
        const string UserId2 = "user-2";

        _context.Roles.Add(MakeRole());
        _context.GuildMembers.AddRange(MakeGuildMember(), MakeGuildMember(MemberId2, UserId2));
        _context.RoleMembers.AddRange(
            MakeRoleMember("rm-1", RoleId, MemberId),
            MakeRoleMember("rm-2", RoleId, MemberId2));
        await _context.SaveChangesAsync();

        var key1 = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        var key2 = GuildPermissionsForUser.GetCacheKey(GuildId, UserId2);
        _cache.SetEntry(key1, "stale-1");
        _cache.SetEntry(key2, "stale-2");

        // Role permissions updated - no specific member added or removed.
        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = null },
            _context, _service, new FakeMessageBus());

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(key1), Is.False, "First member cache invalidated");
            Assert.That(_cache.HasEntry(key2), Is.False, "Second member cache invalidated");
        });
    }
}
