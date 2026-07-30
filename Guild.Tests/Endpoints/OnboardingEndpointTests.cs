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
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers OnboardingEndpoint: admin config GET/PUT (ManageGuild-gated), the member-facing status
/// GET, and the accept action - including that accepting invalidates the user's cached permissions
/// (onboarding can grant/gate visibility of default channels going forward).
/// </summary>
[TestFixture]
public class OnboardingEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private OnboardingEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _endpoint = new OnboardingEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<GuildMember> SeedManagerMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageGuild, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var member = new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" };
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
        return member;
    }

    /// <summary>
    /// GuildMember.OnboardingCompletedAt defaults to "now" at the property declaration (members
    /// are onboarded-by-default unless a join flow explicitly nulls it out for a guild that has
    /// onboarding configured) - so tests exercising the "hasn't accepted yet" path must null it
    /// out explicitly, which this helper does by default.
    /// </summary>
    private async Task<GuildMember> SeedPlainMember(bool onboardingCompleted = false)
    {
        _context.Guilds.Add(MakeGuild());
        var member = new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}",
            OnboardingCompletedAt = onboardingCompleted ? DateTimeOffset.UtcNow : null,
        };
        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();
        return member;
    }

    // ══════════════════════════════════════════════════════════════════════
    // GetConfig (admin)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetConfig_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.GetConfig(GuildId, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetConfig_LacksManageGuild_ReturnsForbid()
    {
        await SeedPlainMember();
        var result = await _endpoint.GetConfig(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetConfig_NoRowYet_ReturnsDisabledDefaults()
    {
        await SeedManagerMember();
        var result = await _endpoint.GetConfig(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<UpdateOnboardingConfigDto>;
        Assert.That(ok!.Value!.Enabled, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════
    // UpdateConfig (admin)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateConfig_EnabledWithoutRulesText_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var dto = new UpdateOnboardingConfigDto { Enabled = true, RulesText = null };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_EnabledWithRulesText_CreatesConfig()
    {
        await SeedManagerMember();
        var dto = new UpdateOnboardingConfigDto { Enabled = true, RulesText = "Be nice", DefaultChannelIds = ["chan-1"] };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<UpdateOnboardingConfigDto>>());
        var config = _context.Set<GuildOnboardingConfig>().Find(GuildId);
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.RulesText, Is.EqualTo("Be nice"));
    }

    [Test]
    public async Task UpdateConfig_DisabledWithoutRulesText_IsAllowed()
    {
        await SeedManagerMember();
        var dto = new UpdateOnboardingConfigDto { Enabled = false, RulesText = null };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<UpdateOnboardingConfigDto>>());
    }

    [Test]
    public async Task UpdateConfig_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();
        await _endpoint.UpdateConfig(GuildId, new UpdateOnboardingConfigDto { Enabled = false }, _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == GuildId).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ActionType, Is.EqualTo(AuditActionType.OnboardingConfigUpdated));
    }

    // ══════════════════════════════════════════════════════════════════════
    // GetMyStatus (member-facing)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMyStatus_NotAMember_ReturnsNotFound()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetMyStatus(GuildId, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetMyStatus_NeverAccepted_ReportsNotCompleted()
    {
        await SeedPlainMember();
        _context.Set<GuildOnboardingConfig>().Add(new GuildOnboardingConfig { GuildId = GuildId, Enabled = true, RulesText = "Rules", UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetMyStatus(GuildId, _context, TestPrincipal.Create(UserId));

        var value = ((IValueHttpResult)result).Value!;
        Assert.That((bool)value.GetType().GetProperty("completed")!.GetValue(value)!, Is.False);
        Assert.That((string)value.GetType().GetProperty("rulesText")!.GetValue(value)!, Is.EqualTo("Rules"));
    }

    [Test]
    public async Task GetMyStatus_AlreadyAccepted_ReportsCompleted()
    {
        var member = await SeedPlainMember();
        member.OnboardingCompletedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetMyStatus(GuildId, _context, TestPrincipal.Create(UserId));

        var value = ((IValueHttpResult)result).Value!;
        Assert.That((bool)value.GetType().GetProperty("completed")!.GetValue(value)!, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Accept
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Accept_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.Accept(GuildId, _context, TestPrincipal.CreateAnonymous(), _permissionService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Accept_NotAMember_ReturnsNotFound()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _endpoint.Accept(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Accept_FirstTime_SetsOnboardingCompletedAt()
    {
        await SeedPlainMember();

        var result = await _endpoint.Accept(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        var member = _context.GuildMembers.Find(MemberId);
        Assert.That(member!.OnboardingCompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task Accept_FirstTime_InvalidatesPermissionCache()
    {
        await SeedPlainMember();
        var cacheKey = Guild.Application.Services.GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(cacheKey, "stale-cached-permissions");

        await _endpoint.Accept(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);

        Assert.That(_cache.HasEntry(cacheKey), Is.False, "Accepting onboarding must invalidate the user's cached permissions");
    }

    [Test]
    public async Task Accept_AlreadyAccepted_DoesNotChangeTimestamp()
    {
        var member = await SeedPlainMember();
        var originalTimestamp = DateTimeOffset.UtcNow.AddDays(-1);
        member.OnboardingCompletedAt = originalTimestamp;
        await _context.SaveChangesAsync();

        await _endpoint.Accept(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);
        await _context.SaveChangesAsync();

        var reloaded = _context.GuildMembers.Find(MemberId);
        Assert.That(reloaded!.OnboardingCompletedAt, Is.EqualTo(originalTimestamp));
    }
}
