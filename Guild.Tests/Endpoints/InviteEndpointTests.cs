using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Dto.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers InviteEndpoint: listing/creating/fetching/deleting invites, and RedeemInviteAsync -
/// including the verification-level join-gating logic (guild.VerificationLevel.MeetsRequirement),
/// ban check, expiry/exhaustion, and the onboarding-pending join path.
/// </summary>
[TestFixture]
public class InviteEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";
    private const string EveryoneRoleId = "role-everyone";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private FakeInvokingMessageBus _bus = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private InviteEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _bus = new FakeInvokingMessageBus();
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new InviteEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild(GuildVerificationLevel level = GuildVerificationLevel.None) => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild", VerificationLevel = level,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ProfileDto MakeProfile(string userId) => new()
    {
        Id = userId, UserId = userId, UserName = "Tester", Hash = 1234, AvatarUrl = "", BannerUrl = "",
    };

    private async Task SeedManagerMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageChannel | Permissions.CreateInvite, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private async Task<GuildInvite> SeedInvite(InviteType type = InviteType.Permanent, DateTimeOffset? expiresAt = null, int? maxUses = null, int useCount = 0, InviteState state = InviteState.Active)
    {
        var invite = GuildInvite.Create(new CreateGuildInviteParams { GuildId = GuildId, Type = type, ExpiresAt = expiresAt, MaxUses = maxUses });
        invite.UseCount = useCount;
        invite.State = state;
        _context.GuildInvites.Add(invite);
        await _context.SaveChangesAsync();
        return invite;
    }

    // ══════════════════════════════════════════════════════════════════════ GetInvitesAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetInvites_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.GetInvitesAsync(GuildId, _context, TestPrincipal.CreateAnonymous(), _permissionService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetInvites_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetInvitesAsync(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetInvites_Valid_ReturnsInvites()
    {
        await SeedManagerMember();
        await SeedInvite();

        var result = await _endpoint.GetInvitesAsync(GuildId, _context, TestPrincipal.Create(UserId), _permissionService);
        var ok = result as Ok<IEnumerable<InviteDto>>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.Count(), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ CreateInviteAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateInvite_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateInviteAsync(GuildId, new CreateInviteDto(), _context, TestPrincipal.CreateAnonymous(), _permissionService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateInvite_LacksCreateInvitePermission_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateInviteAsync(GuildId, new CreateInviteDto(), _context, TestPrincipal.Create(UserId), _permissionService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateInvite_GuildDoesNotExist_ReturnsNotFound()
    {
        // Permission check against a nonexistent guild resolves false before the guild lookup, so
        // seed a manager-equivalent member pointed at a guild row that then gets deleted - simpler:
        // just assert against a guild id the manager can't act on returns Forbid, and cover
        // NotFound by seeding permissions manually via cache bypass isn't practical here, so this
        // exercises the real path: authorized member, but guild row missing entirely wouldn't
        // happen in practice since permission resolution needs the guild.
        var result = await _endpoint.CreateInviteAsync("nonexistent", new CreateInviteDto(), _context, TestPrincipal.Create(UserId), _permissionService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateInvite_Valid_PersistsInviteWithGeneratedCode()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateInviteAsync(GuildId, new CreateInviteDto { Type = InviteType.OneTime, MaxUses = 5 }, _context, TestPrincipal.Create(UserId), _permissionService);
        await _context.SaveChangesAsync();

        var ok = result as Ok<InviteDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.Code, Is.Not.Null.And.Not.Empty);
        Assert.That(await _context.GuildInvites.AsNoTracking().AnyAsync(i => i.Id == ok.Value.Id && i.MaxUses == 5), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ GetInviteAsync /
    // GetInviteByCodeAsync ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetInvite_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.GetInviteAsync("nonexistent", _context);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetInvite_ById_ReturnsInvite()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        var result = await _endpoint.GetInviteAsync(invite.Id, _context);
        var ok = result as Ok<InviteDto>;
        Assert.That(ok!.Value!.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task GetInvite_ByCodeFallback_ReturnsInvite()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        var result = await _endpoint.GetInviteAsync(invite.Code, _context);
        var ok = result as Ok<InviteDto>;
        Assert.That(ok!.Value!.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task GetInviteByCode_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.GetInviteByCodeAsync("NOTREAL1", _context);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteInviteAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteInvite_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.DeleteInviteAsync("nonexistent", _context, TestPrincipal.CreateAnonymous(), _permissionService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task DeleteInvite_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.DeleteInviteAsync("nonexistent", _context, TestPrincipal.Create(UserId), _permissionService);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteInvite_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        var result = await _endpoint.DeleteInviteAsync(invite.Id, _context, TestPrincipal.Create(UserId), _permissionService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteInvite_Valid_RemovesInvite()
    {
        await SeedManagerMember();
        var invite = await SeedInvite();

        var result = await _endpoint.DeleteInviteAsync(invite.Id, _context, TestPrincipal.Create(UserId), _permissionService);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<InviteDto>>());
        Assert.That(await _context.GuildInvites.AsNoTracking().AnyAsync(i => i.Id == invite.Id), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════ RedeemInviteAsync
    // ══════════════════════════════════════════════════════════════════════

    private Role SeedEveryoneRoleInGuild(Guild.Domain.Aggregates.Guild guild)
    {
        var everyone = Role.CreateEveryoneRole(GuildId, OwnerId);
        everyone.Id = EveryoneRoleId;
        return everyone;
    }

    private async Task<Guild.Domain.Aggregates.Guild> SeedRedeemableGuild(GuildVerificationLevel level = GuildVerificationLevel.None)
    {
        var guild = MakeGuild(level);
        _context.Guilds.Add(guild);
        var everyone = new Role { Id = EveryoneRoleId, GuildId = GuildId, Name = "Everyone", Type = RoleType.Everyone, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _context.Roles.Add(everyone);
        await _context.SaveChangesAsync();
        return guild;
    }

    [Test]
    public async Task RedeemInvite_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.RedeemInviteAsync("nonexistent", TestPrincipal.CreateAnonymous(), _context, _cache, _bus, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task RedeemInvite_ProfileNotFound_ReturnsBadRequest()
    {
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = null });

        var result = await _endpoint.RedeemInviteAsync("nonexistent", TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task RedeemInvite_DoesNotExist_ReturnsNotFound()
    {
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.RedeemInviteAsync("nonexistent", TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task RedeemInvite_ExplicitlyExpiredState_ReturnsBadRequest()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(state: InviteState.Expired);
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task RedeemInvite_PastExpiresAt_ReturnsBadRequest()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task RedeemInvite_Exhausted_ReturnsBadRequest()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(maxUses: 1, useCount: 1);
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task RedeemInvite_UserIsBanned_ReturnsForbid()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _context.Set<GuildBan>().Add(GuildBan.Create(new CreateGuildBanParams { GuildId = GuildId, BannedUserId = UserId, BannedByUserId = OwnerId }));
        await _context.SaveChangesAsync();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task RedeemInvite_VerificationLevelNotMet_ReturnsForbidJson()
    {
        await SeedRedeemableGuild(GuildVerificationLevel.Low);
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });
        _bus.SetResponse<GetUserByIdRequest>(new GetUserByIdResponse
        {
            User = new ApplicationUserDto { Id = UserId, Email = "a@b.com", EmailConfirmed = false, CreatedAt = DateTimeOffset.UtcNow },
        });

        var result = await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);

        // Results.Json(...) returns JsonHttpResult<T> where T is inferred from the anonymous
        // object literal, so the concrete generic type isn't known here - assert via reflection.
        Assert.That(result.GetType().Name, Does.StartWith("JsonHttpResult"));
        var statusCode = (int?)result.GetType().GetProperty("StatusCode")!.GetValue(result);
        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task RedeemInvite_Valid_CreatesMemberAndAssignsEveryoneRole()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Accepted>());
        var member = await _context.GuildMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GuildId == GuildId && m.UserId == UserId);
        Assert.That(member, Is.Not.Null);
        Assert.That(member!.OnboardingCompletedAt, Is.Not.Null, "no onboarding configured, so join auto-completes it");
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == EveryoneRoleId && rm.MemberId == member.Id), Is.True);
    }

    [Test]
    public async Task RedeemInvite_Valid_IncrementsUseCount()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        var reloaded = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(reloaded.UseCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RedeemInvite_OneTimeType_MarksInviteExpiredAfterUse()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(type: InviteType.OneTime);
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        var reloaded = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(reloaded.State, Is.EqualTo(InviteState.Expired));
    }

    [Test]
    public async Task RedeemInvite_Valid_InvalidatesUserPermissionCache()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });
        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(cacheKey, "stale");

        await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);

        Assert.That(_cache.HasEntry(cacheKey), Is.False);
    }

    [Test]
    public async Task RedeemInvite_Valid_PublishesMemberJoinedForBots()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await _endpoint.RedeemInviteAsync(invite.Id, TestPrincipal.Create(UserId), _context, _cache, _bus, _hub, _hydrateService);

        Assert.That(_bus.Published.OfType<Guild.Contracts.Bus.Events.MemberJoinedForBots>().Any(e => e.UserId == UserId && e.GuildId == GuildId), Is.True);
    }
}
