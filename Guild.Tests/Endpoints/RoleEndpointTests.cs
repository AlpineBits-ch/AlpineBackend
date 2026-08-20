using System.Text.Json;
using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Validators;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers RoleEndpoint: CreateRole (ManageRoles + CanGrantPermissions escalation guard + shape
/// validation + the role cap), UpdateRole/DeleteRole/AddMemberToRole/RemoveMemberFromRole (all
/// additionally gated by CanManageRoleAsync - actor must outrank the target role), the @everyone
/// and managed-role guards, ReorderRoles including the requested-position guard, the bulk member
/// role edit, and the two read endpoints.
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
    private const string TargetUserId = "target-user";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private MfaElevationService _mfa = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private FakeMessageBus _bus = null!;
    private RoleEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = PermissionTestFactory.Create(_cache, _context);
        _auditLog = new AuditLogService(_context);
        _mfa = new MfaElevationService(_context);
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _bus = new FakeMessageBus();
        _endpoint = new RoleEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Seeds a member holding ManageRoles at role Position=5 (so it can outrank
    /// lower-positioned roles it needs to manage, per CanManageRoleAsync).</summary>
    private async Task SeedManagerMember(Permissions permissions = Permissions.ManageRoles, int position = 5)
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

    private async Task<Role> SeedEveryoneRole()
    {
        var role = Role.CreateEveryoneRole(GuildId, MemberId);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();
        return role;
    }

    private async Task<Role> SeedManagedRole(int position = 1)
    {
        var role = Role.Create(new CreateRoleParams { Name = "bot-role", GuildId = GuildId });
        role.Position = position;
        role.IsManaged = true;
        role.BotUserId = "bot-1";
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();
        return role;
    }

    private async Task<GuildMember> SeedTargetMember()
    {
        var member = new GuildMember { Id = TargetMemberId, GuildId = GuildId, UserId = TargetUserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{TargetUserId}#{GuildId}" };
        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();
        return member;
    }

    private static CreateRoleDto NewRole(string name = "new-role") => new() { Name = name, Color = "#123456" };

    private string LastMetadata(AuditActionType actionType) =>
        _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == actionType).ToList().Last().Metadata ?? "";

    // ══════════════════════════════════════════════════════════════════════ CreateRoleAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateRole_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateRoleAsync(GuildId, NewRole(), _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateRole_LacksManagePermissions_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateRoleAsync(GuildId, NewRole(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateRole_RequestsPermissionActorDoesNotHold_ReturnsForbid()
    {
        // Manager only holds ManageRoles, not BanMembers - requesting BanMembers on the
        // new role must be rejected by the escalation guard.
        await SeedManagerMember();

        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleDto { Name = "r", Permissions = Permissions.BanMembers }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateRole_Valid_InsertsJustAboveEveryoneAndShiftsTheRest()
    {
        await SeedManagerMember(Permissions.ManageRoles | Permissions.ManageChannel);
        var existing = await SeedTargetRole(position: 1);

        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleDto { Name = "new-role", Color = "#123456", Permissions = Permissions.ManageChannel }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.RoleDto>;
        Assert.That(ok, Is.Not.Null);

        var created = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == ok!.Value!.Id);
        var shifted = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == existing.Id);
        var manager = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == ManagerRoleId);

        Assert.Multiple(() =>
        {
            Assert.That(created.Position, Is.EqualTo(1), "Discord inserts a new role directly above @everyone");
            Assert.That(created.Type, Is.EqualTo(RoleType.None), "the request body cannot choose a role type");
            Assert.That(shifted.Position, Is.EqualTo(2), "everything at 1 and above shifts up so no tie is created");
            Assert.That(manager.Position, Is.EqualTo(6));
        });
    }

    [Test]
    public async Task CreateRole_Valid_IsManageableByItsCreator()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateRoleAsync(GuildId, NewRole(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var roleId = (result as Ok<Guild.Application.Dtos.Response.RoleDto>)!.Value!.Id;
        Assert.That(await _permissionService.CanManageRoleAsync(UserId, GuildId, roleId), Is.True,
            "a role its creator cannot subsequently edit, delete or assign is useless to them");
    }

    [Test]
    public async Task CreateRole_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();

        await _endpoint.CreateRoleAsync(GuildId, NewRole("r"), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.RoleCreated).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task CreateRole_NameTooLong_ReturnsBadRequestAndPersistsNothing()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateRoleAsync(GuildId, NewRole(new string('a', RoleValidator.MaxNameLength + 1)), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(await _context.Roles.CountAsync(r => r.GuildId == GuildId), Is.EqualTo(1), "only the seeded manager role");
    }

    [Test]
    public async Task CreateRole_EmptyName_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateRoleAsync(GuildId, NewRole("  "), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateRole_MalformedColor_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleDto { Name = "r", Color = "rebeccapurple" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateRole_ShorthandHexColor_IsAccepted()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleDto { Name = "r", Color = "#f0c" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.RoleDto>>());
    }

    [Test]
    public async Task CreateRole_BothIconAndEmoji_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateRoleAsync(GuildId, new CreateRoleDto { Name = "r", Color = "#ffffff", IconUrl = "https://cdn.example/role.png", UnicodeEmoji = "\U0001F984" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateRole_AtRoleCap_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var filler = Enumerable.Range(0, RoleValidator.MaxRolesPerGuild - 1)
            .Select(i => new Role { Id = $"role-filler-{i}", GuildId = GuildId, Name = $"filler-{i}", Position = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow })
            .ToList();
        _context.Roles.AddRange(filler);
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateRoleAsync(GuildId, NewRole(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateRoleAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateRole_Unauthenticated_ReturnsUnauthorized()
    {
        var (result, evt) = await _endpoint.UpdateRoleAsync("nonexistent", new UpdateRoleDto { Name = "x", Color = "#fff" }, _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
        Assert.That(evt, Is.Null);
    }

    [Test]
    public async Task UpdateRole_DoesNotExist_ReturnsNotFound()
    {
        var (result, _) = await _endpoint.UpdateRoleAsync("nonexistent", new UpdateRoleDto { Name = "x", Color = "#fff" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateRole_TargetOutranksActor_ReturnsForbid()
    {
        await SeedManagerMember(position: 1);
        var target = await SeedTargetRole(position: 5); // outranks the manager (position 1)

        var (result, _) = await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Name = "x", Color = "#fff" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateRole_RequestsUngrantablePermission_ReturnsForbid()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var (result, _) = await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Name = "x", Color = "#fff", Permissions = Permissions.BanMembers }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateRole_Valid_UpdatesFieldsAndReturnsEvent()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var (result, evt) = await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Name = "renamed", Color = "#123456", Permissions = Permissions.None, Hoist = true, Mentionable = false }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.RoleId, Is.EqualTo(target.Id));
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == target.Id);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Name, Is.EqualTo("renamed"));
            Assert.That(reloaded.Color, Is.EqualTo("#123456"));
            Assert.That(reloaded.Hoist, Is.True);
            Assert.That(reloaded.Mentionable, Is.False);
        });
    }

    [Test]
    public async Task UpdateRole_OmittedFields_AreLeftAlone()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        target.Hoist = true;
        target.Permissions = Permissions.None;
        await _context.SaveChangesAsync();

        await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Color = "#abcdef" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == target.Id);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Color, Is.EqualTo("#abcdef"));
            Assert.That(reloaded.Name, Is.EqualTo("target"), "an omitted name is not a rename to null");
            Assert.That(reloaded.Hoist, Is.True, "an omitted flag is not a request to clear it");
        });
    }

    [Test]
    public async Task UpdateRole_EveryoneRole_CannotBeRenamed()
    {
        await SeedManagerMember();
        var everyone = await SeedEveryoneRole();

        var (result, evt) = await _endpoint.UpdateRoleAsync(everyone.Id, new UpdateRoleDto { Name = "Peasants" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(evt, Is.Null);
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == everyone.Id);
        Assert.That(reloaded.Name, Is.EqualTo(Role.EveryoneRoleName));
    }

    [Test]
    public async Task UpdateRole_EveryoneRole_PermissionsStillEditable()
    {
        await SeedManagerMember(Permissions.ManageRoles | Permissions.ManageChannel);
        var everyone = await SeedEveryoneRole();

        var (result, _) = await _endpoint.UpdateRoleAsync(everyone.Id, new UpdateRoleDto { Permissions = Permissions.ManageChannel }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == everyone.Id);
        Assert.That(reloaded.Permissions, Is.EqualTo(Permissions.ManageChannel));
    }

    [Test]
    public async Task UpdateRole_ManagedRole_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var managed = await SeedManagedRole();

        var (result, evt) = await _endpoint.UpdateRoleAsync(managed.Id, new UpdateRoleDto { Name = "hijacked" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(evt, Is.Null);
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == managed.Id);
        Assert.That(reloaded.Name, Is.EqualTo("bot-role"));
    }

    [Test]
    public async Task UpdateRole_OverLongName_ReturnsBadRequestAndLeavesRoleUntouched()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var (result, _) = await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Name = new string('a', RoleValidator.MaxNameLength + 1) }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == target.Id);
        Assert.That(reloaded.Name, Is.EqualTo("target"), "the SaveChanges middleware commits whatever the handler left tracked");
    }

    [Test]
    public async Task UpdateRole_Valid_AuditRecordsBeforeAndAfter()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        await _endpoint.UpdateRoleAsync(target.Id, new UpdateRoleDto { Name = "renamed", Color = "#123456" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        using var metadata = JsonDocument.Parse(LastMetadata(AuditActionType.RoleUpdated));
        var changes = metadata.RootElement.GetProperty("Changes").EnumerateArray().ToList();
        var name = changes.Single(c => c.GetProperty("Field").GetString() == "Name");

        Assert.Multiple(() =>
        {
            Assert.That(name.GetProperty("Old").GetString(), Is.EqualTo("target"));
            Assert.That(name.GetProperty("New").GetString(), Is.EqualTo("renamed"));
            Assert.That(changes.Any(c => c.GetProperty("Field").GetString() == "Color"), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ ReorderRolesAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReorderRoles_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.ReorderRolesAsync(GuildId, new ReorderRolesDto(), _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task ReorderRoles_EmptyList_ReturnsNoContent()
    {
        await SeedManagerMember();
        var result = await _endpoint.ReorderRolesAsync(GuildId, new ReorderRolesDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        Assert.That(result, Is.InstanceOf<NoContent>());
    }

    [Test]
    public async Task ReorderRoles_UnknownRoleId_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = "nonexistent", Position = 1 }] };

        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ReorderRoles_RoleOutranksActor_ReturnsForbid()
    {
        await SeedManagerMember(position: 1);
        var target = await SeedTargetRole(position: 5);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 2 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ReorderRoles_Valid_UpdatesPositions()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 3 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == target.Id);
        Assert.That(reloaded.Position, Is.EqualTo(3));
    }

    [Test]
    public async Task ReorderRoles_RequestedPositionAtActorsOwn_ReturnsForbid()
    {
        // The actor's own highest role sits at 5.
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 5 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == target.Id);
        Assert.That(reloaded.Position, Is.EqualTo(1));
    }

    [Test]
    public async Task ReorderRoles_RequestedPositionAboveActor_ReturnsForbid()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 999 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ReorderRoles_Owner_MayMoveARoleAnywhere()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 999 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(OwnerId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == target.Id);
        Assert.That(reloaded.Position, Is.EqualTo(999));
    }

    [Test]
    public async Task ReorderRoles_EveryoneRole_ReturnsBadRequest()
    {
        // The whole point of the guard: @everyone at position 999 makes every member of the guild
        // outrank every other, and CanModerateTargetAsync needs strictly greater - so nobody can
        // moderate anybody, permanently.
        await SeedManagerMember();
        var everyone = await SeedEveryoneRole();

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = everyone.Id, Position = 1 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(OwnerId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        var reloaded = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == everyone.Id);
        Assert.That(reloaded.Position, Is.EqualTo(0));
    }

    [Test]
    public async Task ReorderRoles_ZeroOrNegativePosition_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var zero = await _endpoint.ReorderRolesAsync(GuildId, new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 0 }] }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        var negative = await _endpoint.ReorderRolesAsync(GuildId, new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = -3 }] }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);

        Assert.Multiple(() =>
        {
            Assert.That(zero, Is.InstanceOf<BadRequest<string>>());
            Assert.That(negative, Is.InstanceOf<BadRequest<string>>());
        });
    }

    [Test]
    public async Task ReorderRoles_DuplicatePositions_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var first = await SeedTargetRole(position: 1);
        var second = Role.Create(new CreateRoleParams { Name = "second", GuildId = GuildId });
        second.Position = 2;
        _context.Roles.Add(second);
        await _context.SaveChangesAsync();

        var dto = new ReorderRolesDto
        {
            Roles =
            [
                new RolePositionDto { RoleId = first.Id, Position = 3 },
                new RolePositionDto { RoleId = second.Id, Position = 3 },
            ],
        };

        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ReorderRoles_PositionHeldByAnUnmovedRole_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var first = await SeedTargetRole(position: 1);
        var second = Role.Create(new CreateRoleParams { Name = "second", GuildId = GuildId });
        second.Position = 2;
        _context.Roles.Add(second);
        await _context.SaveChangesAsync();

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = first.Id, Position = 2 }] };

        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ReorderRoles_SwapOfTwoSubmittedRoles_IsAllowed()
    {
        await SeedManagerMember();
        var first = await SeedTargetRole(position: 1);
        var second = Role.Create(new CreateRoleParams { Name = "second", GuildId = GuildId });
        second.Position = 2;
        _context.Roles.Add(second);
        await _context.SaveChangesAsync();

        var dto = new ReorderRolesDto
        {
            Roles =
            [
                new RolePositionDto { RoleId = first.Id, Position = 2 },
                new RolePositionDto { RoleId = second.Id, Position = 1 },
            ],
        };

        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var reloaded = await _context.Roles.AsNoTracking().Where(r => r.GuildId == GuildId).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.First(r => r.Id == first.Id).Position, Is.EqualTo(2));
            Assert.That(reloaded.First(r => r.Id == second.Id).Position, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ReorderRoles_ManagedRole_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var managed = await SeedManagedRole();

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = managed.Id, Position = 2 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    /// <summary>Seeds <paramref name="count"/> ordinary roles at positions 1..count.</summary>
    private async Task<List<Role>> SeedRolesAsync(int count)
    {
        var roles = new List<Role>();
        for (var i = 1; i <= count; i++)
        {
            var role = Role.Create(new CreateRoleParams { Name = $"role-{i}", GuildId = GuildId });
            role.Position = i;
            _context.Roles.Add(role);
            roles.Add(role);
        }

        await _context.SaveChangesAsync();
        return roles;
    }

    [Test]
    public async Task ReorderRoles_MovingThreeRoles_DispatchesThreeRoleUpdatesAndNoMemberEvents()
    {
        // Discord answers a reorder with one GUILD_ROLE_UPDATE per moved role.
        await SeedManagerMember();
        var roles = await SeedRolesAsync(3);

        var dto = new ReorderRolesDto
        {
            Roles =
            [
                new RolePositionDto { RoleId = roles[0].Id, Position = 3 },
                new RolePositionDto { RoleId = roles[1].Id, Position = 1 },
                new RolePositionDto { RoleId = roles[2].Id, Position = 2 },
            ],
        };

        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        await _context.SaveChangesAsync();

        var dispatched = _bus.Published.OfType<RoleUpdatedForBots>().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(dispatched, Has.Count.EqualTo(3));
            Assert.That(dispatched.Select(e => e.Role.Id), Is.EquivalentTo(roles.Select(r => r.Id)));
            Assert.That(dispatched.All(e => e.GuildId == GuildId), Is.True);
            Assert.That(_bus.Published.OfType<MemberUpdatedForBots>(), Is.Empty,
                "a reorder changes no member's role set, and a member event would say it did");
        });
    }

    [Test]
    public async Task ReorderRoles_DispatchesTheNewPosition()
    {
        // The snapshot is built after the assignment, not before: a bot that applied the payload it
        // was sent and got the old position back would be told nothing at all.
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 4 }] };
        await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        await _context.SaveChangesAsync();

        var dispatched = _bus.Published.OfType<RoleUpdatedForBots>().Single();
        Assert.That(dispatched.Role.Position, Is.EqualTo(4));
    }

    [Test]
    public async Task ReorderRoles_OnARoleWithMembers_DispatchesOneMessage()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        var member = await SeedTargetMember();

        _context.RoleMembers.Add(new RoleMember { Id = "rm-reorder-1", RoleId = target.Id, MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-reorder-2", RoleId = target.Id, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 3 }] };
        await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_bus.Published.OfType<RoleUpdatedForBots>().Count(), Is.EqualTo(1),
                "one message about the role, never one per holder");
            Assert.That(_bus.Published.OfType<MemberUpdatedForBots>(), Is.Empty);
        });
    }

    [Test]
    public async Task ReorderRoles_Rejected_DispatchesNothing()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var dto = new ReorderRolesDto { Roles = [new RolePositionDto { RoleId = target.Id, Position = 999 }] };
        var result = await _endpoint.ReorderRolesAsync(GuildId, dto, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa, _bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(_bus.Published, Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AddMemberToRoleAsync / RemoveMemberFromRoleAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AddMemberToRole_Unauthenticated_ReturnsUnauthorized()
    {
        var (result, _) = await _endpoint.AddMemberToRoleAsync("nonexistent", "nonexistent", _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task AddMemberToRole_RoleDoesNotExist_ReturnsNotFound()
    {
        var (result, _) = await _endpoint.AddMemberToRoleAsync("nonexistent", "nonexistent", _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task AddMemberToRole_Valid_CreatesRoleMembership()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        var member = await SeedTargetMember();

        var (result, evt) = await _endpoint.AddMemberToRoleAsync(target.Id, member.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Accepted>());
        Assert.That(evt!.MemberId, Is.EqualTo(member.Id));
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == target.Id && rm.MemberId == member.Id), Is.True);
    }

    [Test]
    public async Task AddMemberToRole_Repeated_IsIdempotent()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        var member = await SeedTargetMember();

        await _endpoint.AddMemberToRoleAsync(target.Id, member.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var (result, evt) = await _endpoint.AddMemberToRoleAsync(target.Id, member.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var rows = await _context.RoleMembers.AsNoTracking().CountAsync(rm => rm.RoleId == target.Id && rm.MemberId == member.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>(), "Discord answers 204 when the member already holds the role");
            Assert.That(evt, Is.Null, "nothing changed, so nothing to invalidate");
            Assert.That(rows, Is.EqualTo(1),
                "a second row would survive the single-row delete and leave the member holding a revoked role");
        });
    }

    [Test]
    public async Task RemoveMemberFromRole_MemberNotInRole_ReturnsNotFound()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        var member = await SeedTargetMember();

        var (result, _) = await _endpoint.RemoveMemberFromRoleAsync(target.Id, member.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
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

        var (result, evt) = await _endpoint.RemoveMemberFromRoleAsync(target.Id, member.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Accepted>());
        Assert.That(evt!.MemberId, Is.EqualTo(member.Id));
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == target.Id && rm.MemberId == member.Id), Is.False);
    }

    [Test]
    public async Task RemoveMemberFromRole_EveryoneRole_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var everyone = await SeedEveryoneRole();

        var (result, evt) = await _endpoint.RemoveMemberFromRoleAsync(everyone.Id, MemberId, _context, TestPrincipal.Create(OwnerId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(evt, Is.Null);
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == everyone.Id && rm.MemberId == MemberId), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ SetMemberRolesAsync
    // (bulk) ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SetMemberRoles_Unauthenticated_ReturnsUnauthorized()
    {
        var (result, _) = await _endpoint.SetMemberRolesAsync(GuildId, TargetMemberId, new SetMemberRolesDto(), _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task SetMemberRoles_MemberNotInGuild_ReturnsNotFound()
    {
        await SeedManagerMember();

        var (result, _) = await _endpoint.SetMemberRolesAsync(GuildId, "member-elsewhere", new SetMemberRolesDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task SetMemberRoles_UnknownRole_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var member = await SeedTargetMember();

        var (result, _) = await _endpoint.SetMemberRolesAsync(GuildId, member.Id, new SetMemberRolesDto { RoleIds = ["role-from-another-guild"] }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task SetMemberRoles_AddsAndRemovesInOneCall()
    {
        await SeedManagerMember();
        var member = await SeedTargetMember();
        var keep = await SeedTargetRole(position: 1);
        var add = Role.Create(new CreateRoleParams { Name = "add", GuildId = GuildId });
        add.Position = 2;
        var drop = Role.Create(new CreateRoleParams { Name = "drop", GuildId = GuildId });
        drop.Position = 3;
        _context.Roles.AddRange(add, drop);
        _context.RoleMembers.AddRange(
            new RoleMember { Id = "rm-keep", RoleId = keep.Id, MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
            new RoleMember { Id = "rm-drop", RoleId = drop.Id, MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var (result, evt) = await _endpoint.SetMemberRolesAsync(GuildId, member.Id, new SetMemberRolesDto { RoleIds = [keep.Id, add.Id] }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var held = await _context.RoleMembers.AsNoTracking().Where(rm => rm.MemberId == member.Id).Select(rm => rm.RoleId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<List<string>>>());
            Assert.That(held, Is.EquivalentTo(new[] { keep.Id, add.Id }));
            Assert.That(evt!.AddedRoleIds, Is.EquivalentTo(new[] { add.Id }));
            Assert.That(evt.RemovedRoleIds, Is.EquivalentTo(new[] { drop.Id }));
        });
    }

    [Test]
    public async Task SetMemberRoles_RemovalOfARoleAboveTheActor_ReturnsForbid()
    {
        // The hierarchy check has to cover removals: an actor who can strip a role above their own
        // can demote everyone above them and then do as they please.
        await SeedManagerMember(position: 3);
        var member = await SeedTargetMember();
        var senior = await SeedTargetRole(position: 9);
        _context.RoleMembers.Add(new RoleMember { Id = "rm-senior", RoleId = senior.Id, MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var (result, evt) = await _endpoint.SetMemberRolesAsync(GuildId, member.Id, new SetMemberRolesDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(evt, Is.Null);
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == senior.Id && rm.MemberId == member.Id), Is.True);
    }

    [Test]
    public async Task SetMemberRoles_NoChange_WritesNothing()
    {
        await SeedManagerMember();
        var member = await SeedTargetMember();
        var role = await SeedTargetRole(position: 1);
        _context.RoleMembers.Add(new RoleMember { Id = "rm-held", RoleId = role.Id, MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var (result, evt) = await _endpoint.SetMemberRolesAsync(GuildId, member.Id, new SetMemberRolesDto { RoleIds = [role.Id] }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<List<string>>>());
            Assert.That(evt, Is.Null, "no diff, so no invalidation and no bot dispatch");
            Assert.That(_context.Set<GuildAuditLogEntry>().ToList(), Is.Empty);
        });
    }

    [Test]
    public async Task SetMemberRoles_EveryoneRole_IsNeverRemoved()
    {
        await SeedManagerMember();
        var everyone = await SeedEveryoneRole();
        var member = await SeedTargetMember();
        _context.RoleMembers.Add(new RoleMember { Id = "rm-everyone-2", RoleId = everyone.Id, MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var (result, evt) = await _endpoint.SetMemberRolesAsync(GuildId, member.Id, new SetMemberRolesDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<List<string>>>());
            Assert.That(evt, Is.Null);
        });
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == everyone.Id && rm.MemberId == member.Id), Is.True);
    }

    [Test]
    public async Task SetMemberRoles_WritesOneAuditEntryCarryingTheDeltas()
    {
        await SeedManagerMember();
        var member = await SeedTargetMember();
        var add = await SeedTargetRole(position: 1);

        await _endpoint.SetMemberRolesAsync(GuildId, member.Id, new SetMemberRolesDto { RoleIds = [add.Id] }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.RoleUpdated).ToList();
        Assert.That(entries, Has.Count.EqualTo(1), "one entry for the whole change, not one per role");

        using var metadata = JsonDocument.Parse(entries[0].Metadata!);
        Assert.Multiple(() =>
        {
            Assert.That(metadata.RootElement.GetProperty("Added").EnumerateArray().Select(e => e.GetString()), Is.EquivalentTo(new[] { add.Id }));
            Assert.That(metadata.RootElement.GetProperty("Removed").EnumerateArray().ToList(), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteRoleAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteRole_Unauthenticated_ReturnsUnauthorized()
    {
        var (result, _) = await _endpoint.DeleteRoleAsync("nonexistent", _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task DeleteRole_DoesNotExist_ReturnsNotFound()
    {
        var (result, _) = await _endpoint.DeleteRoleAsync("nonexistent", _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteRole_TargetOutranksActor_ReturnsForbid()
    {
        await SeedManagerMember(position: 1);
        var target = await SeedTargetRole(position: 5);

        var (result, _) = await _endpoint.DeleteRoleAsync(target.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteRole_Valid_RemovesRole()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var (result, evt) = await _endpoint.DeleteRoleAsync(target.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        Assert.That(evt!.RoleId, Is.EqualTo(target.Id));
        Assert.That(await _context.Roles.AsNoTracking().AnyAsync(r => r.Id == target.Id), Is.False);
    }

    [Test]
    public async Task DeleteRole_Valid_EventCarriesTheHoldersReadBeforeTheDelete()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);
        var member = await SeedTargetMember();
        _context.RoleMembers.Add(new RoleMember { Id = "rm-doomed", RoleId = target.Id, MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var (_, evt) = await _endpoint.DeleteRoleAsync(target.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(evt!.UserIds, Is.EquivalentTo(new[] { TargetUserId }),
            "after the commit the membership rows are gone, so the holders can only come off the event");
    }

    [Test]
    public async Task DeleteRole_EveryoneRole_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var everyone = await SeedEveryoneRole();

        var (result, evt) = await _endpoint.DeleteRoleAsync(everyone.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(evt, Is.Null);
        Assert.That(await _context.Roles.AsNoTracking().AnyAsync(r => r.Id == everyone.Id), Is.True,
            "deleting it breaks the join path permanently");
    }

    [Test]
    public async Task DeleteRole_ManagedRole_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var managed = await SeedManagedRole();

        var (result, evt) = await _endpoint.DeleteRoleAsync(managed.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(evt, Is.Null);
        Assert.That(await _context.Roles.AsNoTracking().AnyAsync(r => r.Id == managed.Id), Is.True);
    }

    [Test]
    public async Task DeleteRole_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        await _endpoint.DeleteRoleAsync(target.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.RoleDeleted).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ Reads
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ListGuildRoles_Member_ReturnsRolesHighestFirst()
    {
        await SeedManagerMember(Permissions.ManageRoles | Permissions.ViewChannel);
        var target = await SeedTargetRole(position: 1);

        var result = await _endpoint.ListGuildRolesAsync(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);

        var ok = result as Ok<List<Guild.Application.Dtos.Response.RoleDto>>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.Select(r => r.Id).ToList(), Is.EqualTo(new List<string> { ManagerRoleId, target.Id }));
    }

    [Test]
    public async Task ListGuildRoles_NonMember_ReturnsForbid()
    {
        await SeedManagerMember();

        var result = await _endpoint.ListGuildRolesAsync(GuildId, _context, TestPrincipal.Create("stranger"), _permissionService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetRole_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.GetRoleAsync("nonexistent", _context, TestPrincipal.Create(UserId), _permissionService);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetRole_Member_ExposesTheDisplayAndIntegrationFields()
    {
        await SeedManagerMember(Permissions.ManageRoles | Permissions.ViewChannel);
        var target = await SeedTargetRole(position: 1);
        target.Hoist = true;
        target.Mentionable = false;
        target.SetBadge(null, "\U0001F984");
        target.ModulePermissions = ModulePermissions.ViewWiki;
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetRoleAsync(target.Id, _context, TestPrincipal.Create(UserId), _permissionService);

        var ok = result as Ok<Guild.Application.Dtos.Response.RoleDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ok!.Value!.Hoist, Is.True);
            Assert.That(ok.Value.Mentionable, Is.False);
            Assert.That(ok.Value.UnicodeEmoji, Is.EqualTo("\U0001F984"));
            Assert.That(ok.Value.IconUrl, Is.Null);
            Assert.That(ok.Value.IsManaged, Is.False);
            Assert.That(ok.Value.ModulePermissions, Is.EqualTo(ModulePermissions.ViewWiki));
        });
    }

    [Test]
    public async Task GetRole_NonMember_ReturnsForbid()
    {
        await SeedManagerMember();
        var target = await SeedTargetRole(position: 1);

        var result = await _endpoint.GetRoleAsync(target.Id, _context, TestPrincipal.Create("stranger"), _permissionService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }
}
