using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
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
/// Covers MemberEndpoint: Ban/Unban/GetBans, Kick, Mute/Unmute, and LeaveGuild - including the
/// CanModerateTargetAsync role-hierarchy guard (actor must outrank the target) shared across
/// ban/kick/mute, and the owner-can't-leave rule.
/// </summary>
[TestFixture]
public class MemberEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ModRoleId = "role-mod";
    private const string ModMemberId = "member-mod";
    private const string TargetUserId = "target-user";
    private const string TargetMemberId = "member-target";
    private const string TargetRoleId = "role-target";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private FakeMessageBus _bus = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private MemberEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _bus = new FakeMessageBus();
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new MemberEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Seeds a moderator (all four moderation permissions, role Position=5) and a lower-
    /// ranked target member (role Position=1), so CanModerateTargetAsync grants by default.</summary>
    private async Task<GuildMember> SeedModeratorAndTarget(Permissions modPermissions)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = ModRoleId, GuildId = GuildId, Name = "mod", Permissions = modPermissions, Position = 5, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = ModMemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-mod", RoleId = ModRoleId, MemberId = ModMemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        _context.Roles.Add(new Role { Id = TargetRoleId, GuildId = GuildId, Name = "target-role", Position = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var target = new GuildMember { Id = TargetMemberId, GuildId = GuildId, UserId = TargetUserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{TargetUserId}#{GuildId}" };
        _context.GuildMembers.Add(target);
        _context.RoleMembers.Add(new RoleMember { Id = "rm-target", RoleId = TargetRoleId, MemberId = TargetMemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        await _context.SaveChangesAsync();
        return target;
    }

    // ══════════════════════════════════════════════════════════════════════ BanMemberAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task BanMember_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.BanMemberAsync(GuildId, new CreateBanDto { UserId = TargetUserId }, _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task BanMember_LacksBanMembers_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = ModMemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.BanMemberAsync(GuildId, new CreateBanDto { UserId = TargetUserId }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task BanMember_TargetOutranksActor_ReturnsForbid()
    {
        // Moderator (position 1) tries to ban a target with a higher role (position 5).
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = ModRoleId, GuildId = GuildId, Name = "mod", Permissions = Permissions.BanMembers, Position = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = ModMemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-mod", RoleId = ModRoleId, MemberId = ModMemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.Roles.Add(new Role { Id = TargetRoleId, GuildId = GuildId, Name = "target-role", Position = 5, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = TargetMemberId, GuildId = GuildId, UserId = TargetUserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{TargetUserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-target", RoleId = TargetRoleId, MemberId = TargetMemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.BanMemberAsync(GuildId, new CreateBanDto { UserId = TargetUserId }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task BanMember_AlreadyBanned_ReturnsConflict()
    {
        await SeedModeratorAndTarget(Permissions.BanMembers);
        _context.Set<GuildBan>().Add(GuildBan.Create(new CreateGuildBanParams { GuildId = GuildId, BannedUserId = TargetUserId, BannedByUserId = UserId }));
        await _context.SaveChangesAsync();

        var result = await _endpoint.BanMemberAsync(GuildId, new CreateBanDto { UserId = TargetUserId }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task BanMember_Valid_CreatesBanAndRemovesMembership()
    {
        await SeedModeratorAndTarget(Permissions.BanMembers);

        var result = await _endpoint.BanMemberAsync(GuildId, new CreateBanDto { UserId = TargetUserId, Reason = "spam" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.GuildBanDto>>());
        Assert.That(await _context.Set<GuildBan>().AsNoTracking().AnyAsync(b => b.GuildId == GuildId && b.BannedUserId == TargetUserId), Is.True);
        Assert.That(await _context.GuildMembers.AsNoTracking().AnyAsync(m => m.Id == TargetMemberId), Is.False);
        Assert.That(_bus.Published.OfType<MemberRemovedForBots>().Any(e => e.UserId == TargetUserId && e.Reason == "Banned"), Is.True);
    }

    [Test]
    public async Task BanMember_Valid_WritesAuditLogEntry()
    {
        await SeedModeratorAndTarget(Permissions.BanMembers);

        await _endpoint.BanMemberAsync(GuildId, new CreateBanDto { UserId = TargetUserId }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.MemberBanned).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ UnbanMemberAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UnbanMember_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.UnbanMemberAsync(GuildId, TargetUserId, _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UnbanMember_NotBanned_ReturnsNotFound()
    {
        await SeedModeratorAndTarget(Permissions.BanMembers);
        var result = await _endpoint.UnbanMemberAsync(GuildId, TargetUserId, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UnbanMember_Valid_RemovesBan()
    {
        await SeedModeratorAndTarget(Permissions.BanMembers);
        _context.Set<GuildBan>().Add(GuildBan.Create(new CreateGuildBanParams { GuildId = GuildId, BannedUserId = TargetUserId, BannedByUserId = UserId }));
        await _context.SaveChangesAsync();

        var result = await _endpoint.UnbanMemberAsync(GuildId, TargetUserId, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.Set<GuildBan>().AsNoTracking().AnyAsync(b => b.BannedUserId == TargetUserId), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════ GetBansAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetBans_LacksBanMembers_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = ModMemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetBansAsync(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetBans_Valid_ReturnsBans()
    {
        await SeedModeratorAndTarget(Permissions.BanMembers);
        _context.Set<GuildBan>().Add(GuildBan.Create(new CreateGuildBanParams { GuildId = GuildId, BannedUserId = TargetUserId, BannedByUserId = UserId }));
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetBansAsync(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);
        var ok = result as Ok<List<Guild.Application.Dtos.Response.GuildBanDto>>;
        Assert.That(ok!.Value, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ KickMemberAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task KickMember_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.KickMemberAsync(GuildId, "nonexistent", _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task KickMember_DoesNotExist_ReturnsNotFound()
    {
        await SeedModeratorAndTarget(Permissions.KickMembers);
        var result = await _endpoint.KickMemberAsync(GuildId, "nonexistent", _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task KickMember_Valid_RemovesMembership()
    {
        await SeedModeratorAndTarget(Permissions.KickMembers);

        var result = await _endpoint.KickMemberAsync(GuildId, TargetMemberId, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.GuildMembers.AsNoTracking().AnyAsync(m => m.Id == TargetMemberId), Is.False);
        Assert.That(_bus.Published.OfType<MemberRemovedForBots>().Any(e => e.UserId == TargetUserId && e.Reason == "Kicked"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ MuteMemberAsync /
    // UnmuteMemberAsync ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MuteMember_NonPositiveDuration_ReturnsBadRequest()
    {
        await SeedModeratorAndTarget(Permissions.ModerateMembers);
        var result = await _endpoint.MuteMemberAsync(GuildId, TargetMemberId, new MuteMemberDto { DurationMinutes = 0 }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task MuteMember_LacksModerateMembers_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = ModMemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.MuteMemberAsync(GuildId, "nonexistent", new MuteMemberDto { DurationMinutes = 10 }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task MuteMember_DoesNotExist_ReturnsNotFound()
    {
        await SeedModeratorAndTarget(Permissions.ModerateMembers);
        var result = await _endpoint.MuteMemberAsync(GuildId, "nonexistent", new MuteMemberDto { DurationMinutes = 10 }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task MuteMember_Valid_SetsMutedUntilAndInvalidatesCache()
    {
        await SeedModeratorAndTarget(Permissions.ModerateMembers);
        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, TargetUserId);
        _cache.SetEntry(cacheKey, "stale");

        var result = await _endpoint.MuteMemberAsync(GuildId, TargetMemberId, new MuteMemberDto { DurationMinutes = 15 }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var reloaded = await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.Id == TargetMemberId);
        Assert.That(reloaded.MutedUntil, Is.Not.Null);
        Assert.That(reloaded.MutedUntil, Is.GreaterThan(DateTimeOffset.UtcNow));
        Assert.That(_cache.HasEntry(cacheKey), Is.False);
    }

    [Test]
    public async Task UnmuteMember_Valid_ClearsMutedUntil()
    {
        var target = await SeedModeratorAndTarget(Permissions.ModerateMembers);
        target.MutedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
        await _context.SaveChangesAsync();

        var result = await _endpoint.UnmuteMemberAsync(GuildId, TargetMemberId, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var reloaded = await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.Id == TargetMemberId);
        Assert.That(reloaded.MutedUntil, Is.Null);
    }

    [Test]
    public async Task UnmuteMember_DoesNotExist_ReturnsNotFound()
    {
        await SeedModeratorAndTarget(Permissions.ModerateMembers);
        var result = await _endpoint.UnmuteMemberAsync(GuildId, "nonexistent", _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════ LeaveGuildAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task LeaveGuild_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.LeaveGuildAsync(GuildId, _context, TestPrincipal.CreateAnonymous(), _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task LeaveGuild_GuildDoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.LeaveGuildAsync("nonexistent", _context, TestPrincipal.Create(UserId), _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task LeaveGuild_Owner_ReturnsBadRequest()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _endpoint.LeaveGuildAsync(GuildId, _context, TestPrincipal.Create(OwnerId), _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task LeaveGuild_NotAMember_ReturnsNotFound()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _endpoint.LeaveGuildAsync(GuildId, _context, TestPrincipal.Create(UserId), _auditLog, _hub, _hydrateService, _bus);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task LeaveGuild_Valid_RemovesMembership()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = TargetMemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.LeaveGuildAsync(GuildId, _context, TestPrincipal.Create(UserId), _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.GuildMembers.AsNoTracking().AnyAsync(m => m.Id == TargetMemberId), Is.False);
        Assert.That(_bus.Published.OfType<MemberRemovedForBots>().Any(e => e.UserId == UserId && e.Reason == "Left"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════
    // UpdateOwnNicknameAsync / UpdateMemberNicknameAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateOwnNickname_LacksChangeNickname_ReturnsForbid()
    {
        await SeedModeratorAndTarget(Permissions.None);

        var result = await _endpoint.UpdateOwnNicknameAsync(GuildId, new UpdateNicknameDto { Nickname = "Newt" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateOwnNickname_Valid_SetsNicknameAndSearchValue()
    {
        await SeedModeratorAndTarget(Permissions.ChangeNickname);

        var result = await _endpoint.UpdateOwnNicknameAsync(GuildId, new UpdateNicknameDto { Nickname = "  Newt  " },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.Not.InstanceOf<ForbidHttpResult>().And.Not.InstanceOf<BadRequest<string>>());
        var member = await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.Id == ModMemberId);
        Assert.That(member.Nickname, Is.EqualTo("Newt"), "surrounding whitespace is trimmed");
        Assert.That(member.SearchValue, Does.Contain("NEWT"), "nickname must be searchable");
        Assert.That(member.SearchValue, Does.Contain(UserId.ToUpperInvariant()),
            "the username segment must survive the rewrite");
    }

    [Test]
    public async Task UpdateOwnNickname_TooLong_ReturnsBadRequest()
    {
        await SeedModeratorAndTarget(Permissions.ChangeNickname);

        var result = await _endpoint.UpdateOwnNicknameAsync(GuildId, new UpdateNicknameDto { Nickname = new string('x', 33) },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateOwnNickname_Empty_ClearsNickname()
    {
        var target = await SeedModeratorAndTarget(Permissions.ChangeNickname);
        var mod = await _context.GuildMembers.FirstAsync(m => m.Id == ModMemberId);
        mod.Nickname = "Existing";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateOwnNicknameAsync(GuildId, new UpdateNicknameDto { Nickname = "   " },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        var reloaded = await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.Id == ModMemberId);
        Assert.That(reloaded.Nickname, Is.Null);
        Assert.That(reloaded.SearchValue, Does.Not.Contain("EXISTING"));
    }

    [Test]
    public async Task UpdateMemberNickname_LacksManageNicknames_ReturnsForbid()
    {
        await SeedModeratorAndTarget(Permissions.ChangeNickname);

        var result = await _endpoint.UpdateMemberNicknameAsync(GuildId, TargetMemberId, new UpdateNicknameDto { Nickname = "Renamed" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>(),
            "ChangeNickname covers only your own nickname, never someone else's");
    }

    [Test]
    public async Task UpdateMemberNickname_TargetOutranksActor_ReturnsForbid()
    {
        // Actor at position 1 with ManageNicknames, target at position 5 - hierarchy must win.
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = ModRoleId, GuildId = GuildId, Name = "mod", Permissions = Permissions.ManageNicknames, Position = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = ModMemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-mod", RoleId = ModRoleId, MemberId = ModMemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.Roles.Add(new Role { Id = TargetRoleId, GuildId = GuildId, Name = "target-role", Position = 5, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = TargetMemberId, GuildId = GuildId, UserId = TargetUserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{TargetUserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-target", RoleId = TargetRoleId, MemberId = TargetMemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.UpdateMemberNicknameAsync(GuildId, TargetMemberId, new UpdateNicknameDto { Nickname = "Renamed" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateMemberNickname_Valid_RenamesAndWritesAuditLog()
    {
        await SeedModeratorAndTarget(Permissions.ManageNicknames);

        await _endpoint.UpdateMemberNicknameAsync(GuildId, TargetMemberId, new UpdateNicknameDto { Nickname = "Renamed" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        var member = await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.Id == TargetMemberId);
        Assert.That(member.Nickname, Is.EqualTo("Renamed"));

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.MemberNicknameChanged).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].TargetId, Is.EqualTo(TargetUserId));
        Assert.That(_bus.Published.OfType<MemberUpdatedForBots>().Any(e => e.UserId == TargetUserId), Is.True);
    }

    [Test]
    public async Task UpdateMemberNickname_SelfViaMemberIdRoute_OnlyNeedsChangeNickname()
    {
        await SeedModeratorAndTarget(Permissions.ChangeNickname);

        var result = await _endpoint.UpdateMemberNicknameAsync(GuildId, ModMemberId, new UpdateNicknameDto { Nickname = "Self" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.Not.InstanceOf<ForbidHttpResult>());
        var member = await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.Id == ModMemberId);
        Assert.That(member.Nickname, Is.EqualTo("Self"));
    }
}
