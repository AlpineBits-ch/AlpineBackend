using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers PermissionOverwriteEndpoint: the shared UpsertAsync/RemoveAsync private helpers behind
/// all eight channel/category x role/member routes - exercised via the channel+role variant
/// (representative of all eight, which differ only in which FK is populated) plus the
/// category+member variant to confirm the ResolveGuildIdAsync category branch.
/// </summary>
[TestFixture]
public class PermissionOverwriteEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ManagerRoleId = "role-manager";
    private const string ManagerMemberId = "member-manager";
    private const string TargetRoleId = "role-target";
    private const string TargetMemberId = "member-target";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private PermissionOverwriteEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _endpoint = new PermissionOverwriteEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <param name="managerPermissions">
    /// Includes SendMessages by default because an overwrite may only allow *or deny* bits the actor
    /// holds itself - denying a permission you do not have is refused, same rule Discord applies.
    /// </param>
    private async Task<Channel> SeedManagerAndChannel(
        Permissions managerPermissions = Permissions.ManagePermissions | Permissions.SendMessages)
    {
        _context.Guilds.Add(MakeGuild());

        // Positions matter: writing an overwrite for a role/member now requires the actor to
        // outrank it (CanManageRoleAsync / CanModerateTargetAsync), so the manager sits above the
        // target rather than both defaulting to 0.
        _context.Roles.Add(new Role { Id = ManagerRoleId, GuildId = GuildId, Name = "manager", Permissions = managerPermissions, Position = 10, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = ManagerMemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-manager", RoleId = ManagerRoleId, MemberId = ManagerMemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.Roles.Add(new Role { Id = TargetRoleId, GuildId = GuildId, Name = "target", Position = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        // The overwrite target must be a real member of this guild - an overwrite may not reference
        // a member (or role) from somewhere else.
        _context.GuildMembers.Add(new GuildMember { Id = TargetMemberId, GuildId = GuildId, UserId = "user-target", JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"user-target#{GuildId}" });

        var channel = Channel.Create(new CreateChannelParams { Name = "general", Type = ChannelType.Text, GuildId = GuildId, Description = "d" });
        _context.Channels.Add(channel);
        await _context.SaveChangesAsync();
        return channel;
    }

    // ══════════════════════════════════════════════════════════════════════
    // SetChannelRoleOverwriteAsync (representative Upsert path)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SetChannelRoleOverwrite_Unauthenticated_ReturnsUnauthorized()
    {
        var (result, evt) = await _endpoint.SetChannelRoleOverwriteAsync("nonexistent", "nonexistent", new SetPermissionOverwriteDto(), _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
        Assert.That(evt, Is.Null);
    }

    [Test]
    public async Task SetChannelRoleOverwrite_ChannelDoesNotExist_ReturnsNotFound()
    {
        var (result, _) = await _endpoint.SetChannelRoleOverwriteAsync("nonexistent", "nonexistent", new SetPermissionOverwriteDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task SetChannelRoleOverwrite_LacksManagePermissions_ReturnsForbid()
    {
        var channel = await SeedManagerAndChannel(managerPermissions: Permissions.None);
        var (result, _) = await _endpoint.SetChannelRoleOverwriteAsync(channel.Id, TargetRoleId, new SetPermissionOverwriteDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task SetChannelRoleOverwrite_RequestsUngrantablePermission_ReturnsForbid()
    {
        var channel = await SeedManagerAndChannel();
        var dto = new SetPermissionOverwriteDto { AllowPermissions = Permissions.BanMembers };
        var (result, _) = await _endpoint.SetChannelRoleOverwriteAsync(channel.Id, TargetRoleId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task SetChannelRoleOverwrite_Valid_CreatesOverwrite()
    {
        var channel = await SeedManagerAndChannel();
        var dto = new SetPermissionOverwriteDto { AllowPermissions = Permissions.ViewChannel, DenyPermissions = Permissions.SendMessages };

        var (result, evt) = await _endpoint.SetChannelRoleOverwriteAsync(channel.Id, TargetRoleId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelPermissionDto>>());
        Assert.That(evt!.GuildId, Is.EqualTo(GuildId));
        Assert.That(evt.RoleId, Is.EqualTo(TargetRoleId));
        var created = await _context.Set<ChannelPermission>().AsNoTracking().FirstAsync(p => p.ChannelId == channel.Id && p.RoleId == TargetRoleId);
        Assert.That(created.AllowPermissions, Is.EqualTo(Permissions.ViewChannel));
        Assert.That(created.DenyPermissions, Is.EqualTo(Permissions.SendMessages));
    }

    [Test]
    public async Task SetChannelRoleOverwrite_DenyingAPermissionTheActorLacks_IsForbidden()
    {
        // Only AllowPermissions used to be clamped.
        var channel = await SeedManagerAndChannel(Permissions.ManagePermissions);
        var dto = new SetPermissionOverwriteDto { DenyPermissions = Permissions.SendMessages };

        var (result, evt) = await _endpoint.SetChannelRoleOverwriteAsync(channel.Id, TargetRoleId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
            Assert.That(_context.Set<ChannelPermission>().Any(), Is.False);
        });
    }

    [Test]
    public async Task SetChannelRoleOverwrite_TargetRoleOutranksActor_IsForbidden()
    {
        // No rank check existed at all, so a low-positioned ManagePermissions holder could rewrite
        // the overwrites of roles above their own.
        var channel = await SeedManagerAndChannel();

        var senior = await _context.Roles.FirstAsync(r => r.Id == TargetRoleId);
        senior.Position = 99;
        await _context.SaveChangesAsync();

        var (result, evt) = await _endpoint.SetChannelRoleOverwriteAsync(
            channel.Id, TargetRoleId, new SetPermissionOverwriteDto { AllowPermissions = Permissions.ViewChannel },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task SetChannelRoleOverwrite_Valid_WritesAuditLogEntry()
    {
        var channel = await SeedManagerAndChannel();
        await _endpoint.SetChannelRoleOverwriteAsync(channel.Id, TargetRoleId, new SetPermissionOverwriteDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.ChannelPermissionChanged).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SetChannelRoleOverwrite_ExistingOverwrite_IsReplacedNotDuplicated()
    {
        // Manager needs SendMessages itself to be allowed to grant it on the second call
        // (CanGrantPermissionsAsync clamps AllowPermissions to what the actor's own base
        // permissions include) - ManagePermissions alone only implies ViewChannel.
        var channel = await SeedManagerAndChannel(managerPermissions: Permissions.ManagePermissions | Permissions.SendMessages);
        await _endpoint.SetChannelRoleOverwriteAsync(channel.Id, TargetRoleId, new SetPermissionOverwriteDto { AllowPermissions = Permissions.ViewChannel }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        await _endpoint.SetChannelRoleOverwriteAsync(channel.Id, TargetRoleId, new SetPermissionOverwriteDto { AllowPermissions = Permissions.SendMessages }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        var overwrites = _context.Set<ChannelPermission>().Where(p => p.ChannelId == channel.Id && p.RoleId == TargetRoleId).ToList();
        Assert.That(overwrites, Has.Count.EqualTo(1));
        Assert.That(overwrites[0].AllowPermissions, Is.EqualTo(Permissions.SendMessages));
    }

    // ══════════════════════════════════════════════════════════════════════
    // DeleteChannelRoleOverwriteAsync (representative Remove path)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteChannelRoleOverwrite_DoesNotExist_ReturnsNotFound()
    {
        var channel = await SeedManagerAndChannel();
        var (result, _) = await _endpoint.DeleteChannelRoleOverwriteAsync(channel.Id, TargetRoleId, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteChannelRoleOverwrite_Valid_RemovesOverwrite()
    {
        var channel = await SeedManagerAndChannel();
        await _endpoint.SetChannelRoleOverwriteAsync(channel.Id, TargetRoleId, new SetPermissionOverwriteDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        var (result, evt) = await _endpoint.DeleteChannelRoleOverwriteAsync(channel.Id, TargetRoleId, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(evt!.RoleId, Is.EqualTo(TargetRoleId));
        Assert.That(await _context.Set<ChannelPermission>().AsNoTracking().AnyAsync(p => p.ChannelId == channel.Id && p.RoleId == TargetRoleId), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SetCategoryMemberOverwriteAsync (category + member branch)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SetCategoryMemberOverwrite_CategoryDoesNotExist_ReturnsNotFound()
    {
        await SeedManagerAndChannel();
        var (result, _) = await _endpoint.SetCategoryMemberOverwriteAsync("nonexistent", TargetMemberId, new SetPermissionOverwriteDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task SetCategoryMemberOverwrite_Valid_CreatesOverwrite()
    {
        await SeedManagerAndChannel();
        var category = Category.Create(new CreateCategoryParams { Name = "cat", GuildId = GuildId, Position = 0 });
        _context.Categories.Add(category);
        await _context.SaveChangesAsync();

        var dto = new SetPermissionOverwriteDto { AllowPermissions = Permissions.ViewChannel };
        var (result, evt) = await _endpoint.SetCategoryMemberOverwriteAsync(category.Id, TargetMemberId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelPermissionDto>>());
        Assert.That(evt!.MemberId, Is.EqualTo(TargetMemberId));
        Assert.That(await _context.Set<ChannelPermission>().AsNoTracking().AnyAsync(p => p.CategoryId == category.Id && p.MemberId == TargetMemberId), Is.True);
    }

    [Test]
    public async Task DeleteCategoryMemberOverwrite_DoesNotExist_ReturnsNotFound()
    {
        await SeedManagerAndChannel();
        var category = Category.Create(new CreateCategoryParams { Name = "cat", GuildId = GuildId, Position = 0 });
        _context.Categories.Add(category);
        await _context.SaveChangesAsync();

        var (result, _) = await _endpoint.DeleteCategoryMemberOverwriteAsync(category.Id, TargetMemberId, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }
}
