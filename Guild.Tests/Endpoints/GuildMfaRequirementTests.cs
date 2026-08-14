using System.Security.Claims;
using System.Text.Json;
using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// R28: a guild that requires a second factor for moderation and permission changes.
/// </summary>
[TestFixture]
public class GuildMfaRequirementTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ChannelId = "channel-1";
    private const string StaffRoleId = "role-staff";
    private const string TargetRoleId = "role-target";
    private const string MemberId = "member-1";
    private const string TargetMemberId = "member-2";
    private const string TargetUserId = "user-2";

    private TestGuildContext _context = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private MfaElevationService _mfa = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _permissionService = new GuildPermissionService(
            new FakeDistributedCache(), _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _mfa = new MfaElevationService(_context);
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _bus = new FakeMessageBus();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Principals ────────────────────────────────────────────────────────────
    // Built here rather than through TestPrincipal because the claim under test is the one that
    // helper deliberately does not carry.

    private static ClaimsPrincipal Session(string userId, params string[] amr)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(amr.Select(method => new Claim(MfaElevationService.AmrClaimType, method)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal PasswordOnly(string userId) => Session(userId, "pwd");

    private static ClaimsPrincipal WithMfa(string userId) => Session(userId, "pwd", "mfa");

    private static ClaimsPrincipal BotSession(string userId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(MfaElevationService.UserTypeClaimType, MfaElevationService.BotUserType),
            ], "TestAuth"));

    // ── Assertions ────────────────────────────────────────────────────────────

    private static void AssertIsMfaRejection(IResult result)
    {
        Assert.That(result, Is.InstanceOf<IStatusCodeHttpResult>());
        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        var body = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        Assert.That(body, Does.Contain("mfaRequired"),
            "a client has to be able to tell 'enrol and retry' apart from a plain refusal");
        Assert.That(body, Does.Contain("enrollMfa"));
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private async Task SeedAsync(bool mfaRequired)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild", MfaRequired = mfaRequired,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "general", Description = "d",
            Type = ChannelType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Position 5 so the actor outranks the position-1 target role and the roleless target member.
        _context.Roles.Add(new Role
        {
            Id = StaffRoleId, GuildId = GuildId, Name = "staff", Position = 5,
            Permissions = Permissions.ManageRoles | Permissions.ManagePermissions | Permissions.BanMembers
                          | Permissions.KickMembers | Permissions.ModerateMembers | Permissions.ManageGuild,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        var target = Role.Create(new CreateRoleParams { Name = "target", GuildId = GuildId });
        target.Id = TargetRoleId;
        target.Position = 1;
        _context.Roles.Add(target);

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = TargetMemberId, GuildId = GuildId, UserId = TargetUserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{TargetUserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-staff", RoleId = StaffRoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════ The gate on a role
    // edit ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RoleEdit_WithMfaRequired_AndNoSecondFactor_IsRejectedDistinguishably()
    {
        await SeedAsync(mfaRequired: true);

        var (result, _) = await new RoleEndpoint().UpdateRoleAsync(
            TargetRoleId, new UpdateRoleDto { Name = "renamed" },
            _context, PasswordOnly(UserId), _permissionService, _auditLog, _mfa);

        AssertIsMfaRejection(result);

        var role = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == TargetRoleId);
        Assert.That(role.Name, Is.EqualTo("target"), "and nothing was written");
    }

    [Test]
    public async Task RoleEdit_WithMfaRequired_AndASecondFactor_Proceeds()
    {
        await SeedAsync(mfaRequired: true);

        var (result, _) = await new RoleEndpoint().UpdateRoleAsync(
            TargetRoleId, new UpdateRoleDto { Name = "renamed" },
            _context, WithMfa(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status200OK));

        var role = await _context.Roles.AsNoTracking().FirstAsync(r => r.Id == TargetRoleId);
        Assert.That(role.Name, Is.EqualTo("renamed"));
    }

    [Test]
    public async Task RoleEdit_WithoutTheRequirement_NeedsNoSecondFactor()
    {
        await SeedAsync(mfaRequired: false);

        var (result, _) = await new RoleEndpoint().UpdateRoleAsync(
            TargetRoleId, new UpdateRoleDto { Name = "renamed" },
            _context, PasswordOnly(UserId), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task RoleEdit_ByABot_IsExemptFromTheRequirement()
    {
        // A bot token is not a user session: there is nobody to challenge and no authenticator to
        // enrol, so refusing it would break every integration in the guild rather than raise the bar.
        await SeedAsync(mfaRequired: true);

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-bot", GuildId = GuildId, UserId = "bot-1", JoinedAt = DateTime.UtcNow,
            Type = MemberType.Bot, AllowPermissions = Permissions.ManageRoles,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = "bot-1",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-bot", RoleId = StaffRoleId, MemberId = "member-bot",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var (result, _) = await new RoleEndpoint().UpdateRoleAsync(
            TargetRoleId, new UpdateRoleDto { Name = "renamed-by-bot" },
            _context, BotSession("bot-1"), _permissionService, _auditLog, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The gate on moderation and on overwrites
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Ban_WithMfaRequired_AndNoSecondFactor_IsRejected()
    {
        await SeedAsync(mfaRequired: true);

        var result = await new MemberEndpoint().BanMemberAsync(
            GuildId, new CreateBanDto { UserId = TargetUserId, Reason = "spam" },
            _context, PasswordOnly(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus, _mfa);

        AssertIsMfaRejection(result);
        Assert.That(await _context.Set<GuildBan>().CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Kick_WithMfaRequired_AndASecondFactor_Proceeds()
    {
        await SeedAsync(mfaRequired: true);

        var result = await new MemberEndpoint().KickMemberAsync(
            GuildId, TargetMemberId,
            _context, WithMfa(UserId), _permissionService, _auditLog, _hub, _hydrateService, _bus, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status204NoContent));
    }

    [Test]
    public async Task PermissionOverwrite_WithMfaRequired_AndNoSecondFactor_IsRejected()
    {
        await SeedAsync(mfaRequired: true);

        var (result, _) = await new PermissionOverwriteEndpoint().SetChannelRoleOverwriteAsync(
            ChannelId, TargetRoleId,
            new SetPermissionOverwriteDto { DenyPermissions = Permissions.SendMessages },
            _context, PasswordOnly(UserId), _permissionService, _auditLog, _mfa);

        AssertIsMfaRejection(result);
        Assert.That(await _context.Set<ChannelPermission>().CountAsync(), Is.Zero);
    }

    [Test]
    public async Task GuildSettings_WithMfaRequired_AndNoSecondFactor_AreRejected()
    {
        await SeedAsync(mfaRequired: true);

        var result = await new GuildEndpoint().UpdateGuild(
            GuildId, new UpdateGuildDto { Name = "renamed" },
            _context, PasswordOnly(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa);

        AssertIsMfaRejection(result);
    }

    [Test]
    public async Task TheGateRunsAfterThePermissionCheck_SoANonHolderLearnsNothing()
    {
        // A caller who would have been refused anyway must get a plain 403, not the enrolment
        // prompt - whether a guild requires 2FA is a fact about its security posture.
        await SeedAsync(mfaRequired: true);

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-nobody", GuildId = GuildId, UserId = "nobody", JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "nobody",
        });
        await _context.SaveChangesAsync();

        var (result, _) = await new RoleEndpoint().UpdateRoleAsync(
            TargetRoleId, new UpdateRoleDto { Name = "renamed" },
            _context, PasswordOnly("nobody"), _permissionService, _auditLog, _mfa);

        Assert.That(result, Is.Not.InstanceOf<IValueHttpResult>(),
            "a bare Results.Forbid() carries no body to read the requirement out of");
    }

    // ══════════════════════════════════════════════════════════════════════════ Owner lockout
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Owner_CannotTurnTheRequirementOnFromASessionThatCouldNotSatisfyIt()
    {
        await SeedAsync(mfaRequired: false);

        var result = await new GuildEndpoint().SetMfaRequirement(
            GuildId, new SetMfaRequirementDto { Required = true },
            _context, PasswordOnly(OwnerId), _auditLog, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        AssertIsMfaRejection(result);

        var guild = await _context.Guilds.AsNoTracking().FirstAsync();
        Assert.That(guild.MfaRequired, Is.False,
            "this is the whole lockout defence: an unenrolled owner never gets to impose it");
    }

    [Test]
    public async Task Owner_WithASecondFactor_TurnsItOn()
    {
        await SeedAsync(mfaRequired: false);

        var result = await new GuildEndpoint().SetMfaRequirement(
            GuildId, new SetMfaRequirementDto { Required = true },
            _context, WithMfa(OwnerId), _auditLog, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status204NoContent));
        Assert.That((await _context.Guilds.AsNoTracking().FirstAsync()).MfaRequired, Is.True);
    }

    [Test]
    public async Task Owner_CanAlwaysTurnItOffAgain_EvenAfterLosingTheirSecondFactor()
    {
        // The escape hatch, and deliberately asymmetric.
        await SeedAsync(mfaRequired: true);

        var result = await new GuildEndpoint().SetMfaRequirement(
            GuildId, new SetMfaRequirementDto { Required = false },
            _context, PasswordOnly(OwnerId), _auditLog, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status204NoContent));
        Assert.That((await _context.Guilds.AsNoTracking().FirstAsync()).MfaRequired, Is.False);
    }

    [Test]
    public async Task NonOwner_CannotToggleIt_EvenHoldingManageGuildAndASecondFactor()
    {
        await SeedAsync(mfaRequired: true);

        var result = await new GuildEndpoint().SetMfaRequirement(
            GuildId, new SetMfaRequirementDto { Required = false },
            _context, WithMfa(UserId), _auditLog, _hub, _hydrateService);

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>());
        Assert.That((await _context.Guilds.AsNoTracking().FirstAsync()).MfaRequired, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════ The claim reading
    // itself ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void HasMfa_ReadsOneOfSeveralAmrValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MfaElevationService.HasMfa(Session(UserId, "pwd", "mfa")), Is.True);
            Assert.That(MfaElevationService.HasMfa(Session(UserId, "steam", "mfa")), Is.True);
            Assert.That(MfaElevationService.HasMfa(Session(UserId, "pwd")), Is.False);
            Assert.That(MfaElevationService.HasMfa(Session(UserId)), Is.False);
        });
    }

    [Test]
    public async Task IsSatisfied_ForAGuildThatDoesNotRequireIt_IsTrueForEveryone()
    {
        await SeedAsync(mfaRequired: false);

        Assert.That(await _mfa.IsSatisfiedAsync(GuildId, PasswordOnly(UserId)), Is.True);
    }

    [Test]
    public async Task IsSatisfied_ForAGuildThatDoesNotExist_IsTrue()
    {
        // Fails open on purpose: a missing guild is a 404 the endpoint is about to return anyway,
        // and answering "MFA required" for it would turn this into a guild-existence oracle.
        Assert.That(await _mfa.IsSatisfiedAsync("no-such-guild", PasswordOnly(UserId)), Is.True);
    }
}
