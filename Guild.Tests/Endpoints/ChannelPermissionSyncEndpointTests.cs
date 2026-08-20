using Guild.Application.Bus.Events.Permission;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints.Channel;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Permission;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Tests.Endpoints;

/// <summary>Copying a category's overwrites onto one of its channels, atomically.</summary>
[TestFixture]
public class ChannelPermissionSyncEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string CategoryId = "cat-1";
    private const string ChannelId = "chan-1";
    private const string OrphanChannelId = "chan-orphan";
    private const string EveryoneRoleId = "role-everyone";
    private const string PlayerRoleId = "role-player";
    private const string ManagerRoleId = "role-manager";
    private const string ManagerMemberId = "member-manager";
    private const string ManagerUserId = "manager-1";
    private const string AdminRoleId = "role-admin";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _service = null!;
    private ChannelPrivacyService _privacy = null!;
    private AuditLogService _auditLog = null!;
    private MfaElevationService _mfa = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _service = PermissionTestFactory.Create(_cache, _context);
        _privacy = new ChannelPrivacyService(_context);
        _auditLog = new AuditLogService(_context);
        _mfa = new MfaElevationService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g", CreatedAt = now, UpdatedAt = now,
        });
        _context.Categories.Add(new Category
        {
            Id = CategoryId, GuildId = GuildId, Name = "cat", CreatedAt = now, UpdatedAt = now,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, CategoryId = CategoryId, Name = "c", Description = "d",
            Type = ChannelType.Text, CreatedAt = now, UpdatedAt = now,
        });
        _context.Channels.Add(new Channel
        {
            Id = OrphanChannelId, GuildId = GuildId, CategoryId = null, Name = "o", Description = "d",
            Type = ChannelType.Text, CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages, CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = PlayerRoleId, GuildId = GuildId, Name = "player", Type = RoleType.None,
            Permissions = Permissions.None, CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddOverwriteAsync(string? channelId, string? categoryId, string roleId,
        Permissions allow, Permissions deny,
        ModulePermissions allowModule = ModulePermissions.None, ModulePermissions denyModule = ModulePermissions.None)
    {
        var now = DateTimeOffset.UtcNow;
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(),
            ChannelId = channelId, CategoryId = categoryId, RoleId = roleId, MemberId = null,
            AllowPermissions = allow, DenyPermissions = deny,
            AllowModulePermissions = allowModule,
            DenyModulePermissions = denyModule,
            CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddMemberOverwriteAsync(string? channelId, string? categoryId, string memberId,
        Permissions allow, Permissions deny)
    {
        var now = DateTimeOffset.UtcNow;
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(),
            ChannelId = channelId, CategoryId = categoryId, RoleId = null, MemberId = memberId,
            AllowPermissions = allow, DenyPermissions = deny,
            AllowModulePermissions = ModulePermissions.None,
            DenyModulePermissions = ModulePermissions.None,
            CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    /// <param name="permissions">
    /// Includes ManagePermissions so the gate that reaches the clamp is satisfied; the clamp itself
    /// is what each escalation test exercises.
    /// </param>
    /// <param name="modulePermissions">The manager's module mask.</param>
    /// <param name="position">Where the manager ranks, for the tests that seed a role above it.</param>
    private async Task SeedManagerAsync(Permissions permissions,
        ModulePermissions modulePermissions = ModulePermissions.None, int position = 0)
    {
        var now = DateTimeOffset.UtcNow;
        _context.Roles.Add(new Role
        {
            Id = ManagerRoleId, GuildId = GuildId, Name = "manager", Position = position,
            Permissions = permissions, ModulePermissions = modulePermissions,
            CreatedAt = now, UpdatedAt = now,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = ManagerMemberId, GuildId = GuildId, UserId = ManagerUserId, JoinedAt = DateTime.UtcNow,
            SearchValue = "MANAGER-1", CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-manager", RoleId = ManagerRoleId, MemberId = ManagerMemberId, CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Sync_ReplacesTheChannelSetWithTheCategorySet()
    {
        await SeedAsync();
        await AddOverwriteAsync(null, CategoryId, PlayerRoleId, Permissions.AddReactions, Permissions.None);
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.None, Permissions.SendMessages);

        var (result, _) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(OwnerId), _service, _auditLog, _mfa, _privacy);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<List<ChannelPermissionDto>>>());

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].RoleId, Is.EqualTo(PlayerRoleId));
            Assert.That(rows[0].AllowPermissions, Is.EqualTo(Permissions.AddReactions));
            Assert.That(rows[0].CategoryId, Is.Null);
        });
    }

    [Test]
    public async Task Sync_CarriesTheEveryoneViewDenyAndTheChannelBecomesPrivate()
    {
        await SeedAsync();
        await AddOverwriteAsync(null, CategoryId, EveryoneRoleId, Permissions.None, Permissions.ViewChannel);

        await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(OwnerId), _service, _auditLog, _mfa, _privacy);
        await _context.SaveChangesAsync();

        var channel = await _context.Channels.FirstAsync(c => c.Id == ChannelId);
        Assert.That(channel.IsPrivate, Is.True);
    }

    [Test]
    public async Task Sync_RemovesTheEveryoneViewDenyAndTheChannelBecomesPublic()
    {
        await SeedAsync();
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.None, Permissions.ViewChannel);
        var before = await _context.Channels.FirstAsync(c => c.Id == ChannelId);
        before.IsPrivate = true;
        await _context.SaveChangesAsync();

        // The category carries no @everyone deny, so the sync should clear the flag it set above.
        await AddOverwriteAsync(null, CategoryId, PlayerRoleId, Permissions.AddReactions, Permissions.None);

        await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(OwnerId), _service, _auditLog, _mfa, _privacy);
        await _context.SaveChangesAsync();

        var channel = await _context.Channels.FirstAsync(c => c.Id == ChannelId);
        Assert.That(channel.IsPrivate, Is.False);
    }

    [Test]
    public async Task Sync_OnACategorylessChannel_IsNotFound()
    {
        await SeedAsync();

        var (result, _) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            OrphanChannelId, _context, TestPrincipal.Create(OwnerId), _service, _auditLog, _mfa, _privacy);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Sync_WithoutManagePermissions_IsForbidden()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = "user-1", JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1", CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = EveryoneRoleId, MemberId = "member-1", CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();

        var (result, _) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create("user-1"), _service, _auditLog, _mfa, _privacy);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    /// <summary>An event naming no target reaches neither branch of the handler, so this asserts
    /// the cache entry is actually gone rather than that some event was returned.</summary>
    [Test]
    public async Task Sync_DropsTheCachedMaskOfEverybodyItMoves()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = "user-1", JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1", CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = EveryoneRoleId, MemberId = "member-1", CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();

        // The category hides the channel from @everyone, which is the confidentiality case: a stale
        // cache keeps ViewChannel for fifteen minutes after IsPrivate flips.
        await AddOverwriteAsync(null, CategoryId, EveryoneRoleId, Permissions.None, Permissions.ViewChannel);

        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, "user-1");
        await _service.ComputePermissionsForUserAsync("user-1", GuildId);
        Assert.That(await _cache.GetStringAsync(cacheKey), Is.Not.Null, "cache not primed");

        var (_, events) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(OwnerId), _service, _auditLog, _mfa, _privacy);
        await _context.SaveChangesAsync();

        var hub = new FakeHubContext();
        foreach (var invalidation in events.OfType<ChannelPermissionChanged>())
            await ChannelPermissionChangedHandler.Handle(invalidation, _context, _service, hub);

        Assert.That(await _cache.GetStringAsync(cacheKey), Is.Null);
    }

    [Test]
    public async Task Sync_WithNoCategoryOverwrites_ClearsTheChannel()
    {
        await SeedAsync();
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.None, Permissions.SendMessages);

        await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(OwnerId), _service, _auditLog, _mfa, _privacy);
        await _context.SaveChangesAsync();

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.That(rows, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Escalation clamp: copying a category row must not bypass the
    // CanGrantPermissionsAsync clamp a direct overwrite write already has.
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Sync_CategoryAllowsAPermissionTheActorLacks_IsForbiddenAndWritesNothing()
    {
        await SeedAsync();
        await SeedManagerAsync(Permissions.ManagePermissions | Permissions.SendMessages);
        await AddOverwriteAsync(null, CategoryId, PlayerRoleId, Permissions.BanMembers, Permissions.None);
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.ViewChannel, Permissions.None);

        var (result, events) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(ManagerUserId), _service, _auditLog, _mfa, _privacy);

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(events, Is.Empty);
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].RoleId, Is.EqualTo(EveryoneRoleId));
            Assert.That(rows[0].AllowPermissions, Is.EqualTo(Permissions.ViewChannel));
        });
    }

    [Test]
    public async Task Sync_CategoryDeniesAPermissionTheActorLacks_IsForbiddenAndWritesNothing()
    {
        await SeedAsync();
        // PinMessages, not SendMessages: @everyone already grants SendMessages in SeedAsync, which
        // would satisfy the clamp regardless of what the manager role itself holds.
        await SeedManagerAsync(Permissions.ManagePermissions);
        await AddOverwriteAsync(null, CategoryId, PlayerRoleId, Permissions.None, Permissions.PinMessages);
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.ViewChannel, Permissions.None);

        var (result, events) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(ManagerUserId), _service, _auditLog, _mfa, _privacy);

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(events, Is.Empty);
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].RoleId, Is.EqualTo(EveryoneRoleId));
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Hierarchy gate: whose permissions the sync moves, on the rows it
    // deletes as well as the rows it creates.
    // ══════════════════════════════════════════════════════════════════════

    private async Task SeedAdminRoleAsync()
    {
        var now = DateTimeOffset.UtcNow;
        _context.Roles.Add(new Role
        {
            Id = AdminRoleId, GuildId = GuildId, Name = "admins", Type = RoleType.None, Position = 100,
            Permissions = Permissions.None, CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Sync_WouldClearTheActorsOwnMute_IsForbiddenAndWritesNothing()
    {
        await SeedAsync();
        await SeedManagerAsync(Permissions.ManagePermissions | Permissions.SendMessages, position: 10);
        await AddMemberOverwriteAsync(ChannelId, null, ManagerMemberId, Permissions.None, Permissions.SendMessages);

        var (result, events) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(ManagerUserId), _service, _auditLog, _mfa, _privacy);

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(events, Is.Empty);
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].MemberId, Is.EqualTo(ManagerMemberId));
            Assert.That(rows[0].DenyPermissions, Is.EqualTo(Permissions.SendMessages));
        });
    }

    [Test]
    public async Task Sync_WouldFreeARoleTheActorCannotManage_IsForbiddenAndWritesNothing()
    {
        await SeedAsync();
        await SeedAdminRoleAsync();
        await SeedManagerAsync(Permissions.ManagePermissions | Permissions.SendMessages, position: 10);
        await AddOverwriteAsync(ChannelId, null, AdminRoleId, Permissions.None, Permissions.SendMessages);

        var (result, events) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(ManagerUserId), _service, _auditLog, _mfa, _privacy);

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(events, Is.Empty);
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].RoleId, Is.EqualTo(AdminRoleId));
            Assert.That(rows[0].DenyPermissions, Is.EqualTo(Permissions.SendMessages));
        });
    }

    [Test]
    public async Task Sync_WouldSilenceARoleTheActorCannotManage_IsForbiddenAndWritesNothing()
    {
        await SeedAsync();
        await SeedAdminRoleAsync();
        // SendMessages comes from @everyone, so the grant clamp passes and the hierarchy gate is
        // the only thing left that can reject.
        await SeedManagerAsync(Permissions.ManagePermissions, position: 10);
        await AddOverwriteAsync(null, CategoryId, AdminRoleId, Permissions.None, Permissions.SendMessages);
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.ViewChannel, Permissions.None);

        var (result, events) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(ManagerUserId), _service, _auditLog, _mfa, _privacy);

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(events, Is.Empty);
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].RoleId, Is.EqualTo(EveryoneRoleId));
            Assert.That(rows[0].AllowPermissions, Is.EqualTo(Permissions.ViewChannel));
        });
    }

    [Test]
    public async Task Sync_ByAnActorWhoOutranksEveryTarget_Succeeds()
    {
        await SeedAsync();
        await SeedManagerAsync(Permissions.ManagePermissions | Permissions.AddReactions, position: 10);
        await AddOverwriteAsync(null, CategoryId, PlayerRoleId, Permissions.AddReactions, Permissions.None);
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.ViewChannel, Permissions.None);

        var (result, events) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(ManagerUserId), _service, _auditLog, _mfa, _privacy);
        await _context.SaveChangesAsync();

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<List<ChannelPermissionDto>>>());
            Assert.That(events, Is.Not.Empty);
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].RoleId, Is.EqualTo(PlayerRoleId));
        });
    }

    [Test]
    public async Task Sync_CategoryAllowsAModuleBitTheActorLacks_IsForbiddenAndWritesNothing()
    {
        await SeedAsync();
        await SeedManagerAsync(Permissions.ManagePermissions, ModulePermissions.ViewWiki);
        await AddOverwriteAsync(null, CategoryId, PlayerRoleId, Permissions.None, Permissions.None,
            allowModule: ModulePermissions.DeleteWikiPages);
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.ViewChannel, Permissions.None);

        var (result, events) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(ManagerUserId), _service, _auditLog, _mfa, _privacy);

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(events, Is.Empty);
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].RoleId, Is.EqualTo(EveryoneRoleId));
        });
    }
}
