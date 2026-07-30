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
/// Covers RoleEndpoint: CreateRole (ManagePermissions + CanGrantPermissions escalation guard),
/// UpdateRole/DeleteRole/AddMemberToRole/RemoveMemberFromRole (all additionally gated by
/// CanManageRoleAsync - actor must outrank the target role), and ReorderRoles.
/// </summary>
[TestFixture]
public class RoleEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ManagerRoleId = "role-manager";
    private const string MemberId = "member-1";
    private const string TargetMemberId = "member-2";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private RoleEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new RoleEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Seeds a member holding ManagePermissions at role Position=5 (so it can outrank
    /// lower-positioned roles it needs to manage, per CanManageRoleAsync).</summary>
    private async Task SeedManagerMember(Permissions permissions = Permissions.ManagePermissions, int position = 5)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = ManagerRoleId, GuildId = GuildId, Name = "manager", Permissions = permissions, Position = position, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-manager", RoleId = ManagerRoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private async Task<Role> SeedTargetRole(int position = 1, Permissions permissions = Permissions.None)
    {
        var role = Role.Create(new CreateRoleParams { Name = "target", GuildId = GuildId, Permissions = permissions });
        role.Position = position;
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();
        return role;
    }

    // ══════════════════════════════════════════════════════════════════════ CreateRoleAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateRole_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleParams { Name = "r", GuildId = GuildId }, _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateRole_LacksManagePermissions_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleParams { Name = "r", GuildId = GuildId }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateRole_RequestsPermissionActorDoesNotHold_ReturnsForbid()
    {
        // Manager only holds ManagePermissions, not BanMembers - requesting BanMembers on the
        // new role must be rejected by the escalation guard.
        await SeedManagerMember();

        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleParams { Name = "r", GuildId = GuildId, Permissions = Permissions.BanMembers }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateRole_Valid_PersistsAtNextPosition()
    {
        await SeedManagerMember(Permissions.ManagePermissions | Permissions.ManageChannel);
        await SeedTargetRole(position: 1);

        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleParams { Name = "new-role", GuildId = GuildId, Permissions = Permissions.ManageChannel }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.RoleDto>;
        Assert.That(ok, Is.Not.Null);
        var created = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == ok!.Value!.Id);
        Assert.That(created.Position, Is.EqualTo(6), "one above the current max (manager role at 5)");
    }

    [Test]
    public async Task CreateRole_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();

        await _endpoint.CreateRoleAsync(GuildId, new CreateRoleParams { Name = "r", GuildId = GuildId }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.RoleCreated).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateRoleAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateRole_Unauthenticated_ReturnsUnauthorized()
    {
        var (result, evt) = await _endpoint.UpdateRoleAsync("nonexistent", new UpdateRoleDto { Name = "x", Color = "#fff" }, _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
        Assert.That(evt, Is.Null);
    }

    [Test]
    public async Task UpdateRole_DoesNotExist_ReturnsNotFound()
    {
        var (result, _) = await _endpoint.UpdateRoleAsync("nonexistent", new UpdateRoleDto { Name = "x", Color = "#fff" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateRole_TargetOutranksActor_ReturnsForbid()
    {
        await SeedManagerMember(position: 1);
        var target = await SeedTargetRole(position: 5); // outranks the manager (position 1)

        var (result, _) = await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Name = "x", Color = "#fff" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateRole_RequestsUngrantablePermission_ReturnsForbid()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var (result, _) = await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Name = "x", Color = "#fff", Permissions = Permissions.BanMembers }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateRole_Valid_UpdatesFieldsAndReturnsEvent()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var (result, evt) = await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Name = "renamed", Color = "#123456", Permissions = Permissions.None }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.RoleId, Is.EqualTo(target.Id));
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == target.Id);
        Assert.That(reloaded.Name, Is.EqualTo("renamed"));
        Assert.That(reloaded.Color, Is.EqualTo("#123456"));
    }

    // ══════════════════════════════════════════════════════════════════════ ReorderRolesAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReorderRoles_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.ReorderRolesAsync(GuildId, new ReorderRolesDto(), _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task ReorderRoles_EmptyList_ReturnsNoContent()
    {
        await SeedManagerMember();
        var result = await _endpoint.ReorderRolesAsync(GuildId, new ReorderRolesDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<NoContent>());
    }

    [Test]
    public async Task ReorderRoles_UnknownRoleId_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = "nonexistent", Position = 0 }] };

        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ReorderRoles_RoleOutranksActor_ReturnsForbid()
    {
        await SeedManagerMember(position: 1);
        var target = await SeedTargetRole(position: 5);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 2 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ReorderRoles_Valid_UpdatesPositions()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 9 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == target.Id);
        Assert.That(reloaded.Position, Is.EqualTo(9));
    }

    // ══════════════════════════════════════════════════════════════════════
    // AddMemberToRoleAsync / RemoveMemberFromRoleAsync
    // ══════════════════════════════════════════════════════════════════════

    private async Task<GuildMember> SeedTargetMember()
    {
        var member = new GuildMember { Id = TargetMemberId, GuildId = GuildId, UserId = "target-user", JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"target-user#{GuildId}" };
        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();
        return member;
    }

    [Test]
    public async Task AddMemberToRole_Unauthenticated_ReturnsUnauthorized()
    {
        var (result, _) = await _endpoint.AddMemberToRoleAsync("nonexistent", "nonexistent", _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task AddMemberToRole_RoleDoesNotExist_ReturnsNotFound()
    {
        var (result, _) = await _endpoint.AddMemberToRoleAsync("nonexistent", "nonexistent", _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task AddMemberToRole_Valid_CreatesRoleMembership()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        var member = await SeedTargetMember();

        var (result, evt) = await _endpoint.AddMemberToRoleAsync(target.Id, member.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Accepted>());
        Assert.That(evt!.MemberId, Is.EqualTo(member.Id));
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == target.Id && rm.MemberId == member.Id), Is.True);
    }

    [Test]
    public async Task RemoveMemberFromRole_MemberNotInRole_ReturnsNotFound()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        var member = await SeedTargetMember();

        var (result, _) = await _endpoint.RemoveMemberFromRoleAsync(target.Id, member.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task RemoveMemberFromRole_Valid_RemovesRoleMembership()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        var member = await SeedTargetMember();
        _context.RoleMembers.Add(new RoleMember { Id = "rm-target", RoleId = target.Id, MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var (result, evt) = await _endpoint.RemoveMemberFromRoleAsync(target.Id, member.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Accepted>());
        Assert.That(evt!.MemberId, Is.EqualTo(member.Id));
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == target.Id && rm.MemberId == member.Id), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteRoleAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteRole_Unauthenticated_ReturnsUnauthorized()
    {
        var (result, _) = await _endpoint.DeleteRoleAsync("nonexistent", _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task DeleteRole_DoesNotExist_ReturnsNotFound()
    {
        var (result, _) = await _endpoint.DeleteRoleAsync("nonexistent", _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteRole_TargetOutranksActor_ReturnsForbid()
    {
        await SeedManagerMember(position: 1);
        var target = await SeedTargetRole(position: 5);

        var (result, _) = await _endpoint.DeleteRoleAsync(target.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteRole_Valid_RemovesRole()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var (result, evt) = await _endpoint.DeleteRoleAsync(target.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        Assert.That(evt!.RoleId, Is.EqualTo(target.Id));
        Assert.That(await _context.Roles.AsNoTracking().AnyAsync(r => r.Id == target.Id), Is.False);
    }

    [Test]
    public async Task DeleteRole_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        await _endpoint.DeleteRoleAsync(target.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.RoleDeleted).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }
}
