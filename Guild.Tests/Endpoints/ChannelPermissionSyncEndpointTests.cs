using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints.Channel;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
        Permissions allow, Permissions deny)
    {
        var now = DateTimeOffset.UtcNow;
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(),
            ChannelId = channelId, CategoryId = categoryId, RoleId = roleId, MemberId = null,
            AllowPermissions = allow, DenyPermissions = deny,
            AllowModulePermissions = ModulePermissions.None,
            DenyModulePermissions = ModulePermissions.None,
            CreatedAt = now, UpdatedAt = now,
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

        var channel = await _context.Channels.FirstAsync(c => c.Id == ChannelId);
        Assert.That(channel.IsPrivate, Is.True);
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

    [Test]
    public async Task Sync_EmitsOneInvalidationEvent()
    {
        await SeedAsync();
        await AddOverwriteAsync(null, CategoryId, PlayerRoleId, Permissions.AddReactions, Permissions.None);

        var (_, evt) = await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(OwnerId), _service, _auditLog, _mfa, _privacy);

        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.GuildId, Is.EqualTo(GuildId));
    }

    [Test]
    public async Task Sync_WithNoCategoryOverwrites_ClearsTheChannel()
    {
        await SeedAsync();
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.None, Permissions.SendMessages);

        await ChannelPermissionSyncEndpoint.SyncChannelPermissionsAsync(
            ChannelId, _context, TestPrincipal.Create(OwnerId), _service, _auditLog, _mfa, _privacy);

        var rows = await _context.Set<ChannelPermission>()
            .Where(p => p.ChannelId == ChannelId).ToListAsync();

        Assert.That(rows, Is.Empty);
    }
}
