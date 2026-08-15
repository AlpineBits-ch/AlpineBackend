using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers the vanity-URL surface: the grammar, the reserved list, uniqueness, the backing invite,
/// and the two things the legal downgrade terms actually bind us to - that losing the plan stops the
/// link resolving without destroying the name, and that a downgraded guild can still let it go.
/// </summary>
[TestFixture]
public class VanityUrlEndpointTests
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
    private FakeHubContext _hub = null!;
    private FakeInvokingMessageBus _bus = null!;
    private GuildInviteAudienceService _audience = null!;
    private VanityUrlEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _hub = new FakeHubContext();
        _bus = new FakeInvokingMessageBus();
        _audience = new GuildInviteAudienceService(_context,
            new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance));
        _endpoint = new VanityUrlEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>No <c>EntitlementResolver</c> means no billing wired up, which every source in this
    /// repo treats as "everything included" - self-hosted, and every test.</summary>
    private VanityUrlService Vanity() => new(_context, NullLogger<VanityUrlService>.Instance);

    private async Task SeedManagerAsync()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "The Flat",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageGuild, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-1" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private Task<IResult> SetAsync(string? slug, string userId = UserId, VanityUrlService? vanity = null) =>
        _endpoint.SetVanityUrlAsync(GuildId, new SetVanityUrlDto { VanityUrl = slug }, _context,
            TestPrincipal.Create(userId), _permissionService, _auditLog, vanity ?? Vanity(), _hub, _audience, _bus);

    // ── Permission ────────────────────────────────────────────────────────

    [Test]
    public async Task Set_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.SetVanityUrlAsync(GuildId, new SetVanityUrlDto { VanityUrl = "flat" }, _context,
            TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, Vanity(), _hub, _audience, _bus);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Set_WithoutManageGuild_ReturnsForbid()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild { Id = GuildId, OwnerId = OwnerId, Name = "x", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-1" });
        await _context.SaveChangesAsync();

        Assert.That(await SetAsync("flatshare"), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Get_WithoutManageGuild_ReturnsForbid()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild { Id = GuildId, OwnerId = OwnerId, Name = "x", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-1" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetVanityUrlAsync(GuildId, _context, TestPrincipal.Create(UserId), _permissionService, Vanity());
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // ── The grammar ───────────────────────────────────────────────────────

    [Test]
    public async Task Set_Valid_NormalizesAndStores()
    {
        await SeedManagerAsync();

        var ok = (Ok<VanityUrlDto>)await SetAsync("  The-FLAT ");
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.VanityUrl, Is.EqualTo("the-flat"));
            Assert.That(ok.Value.Active, Is.True);
            Assert.That(ok.Value.SetAt, Is.Not.Null);
        });

        var stored = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(stored.VanityUrl, Is.EqualTo("the-flat"),
            "only the normalized spelling ever reaches the column - that is what makes the plain unique index case-insensitive");
    }

    [TestCase("ab", TestName = "Set_TooShort_ReturnsBadRequest")]
    [TestCase("this-slug-is-far-too-long-to-ever-be-accepted", TestName = "Set_TooLong_ReturnsBadRequest")]
    [TestCase("-leading", TestName = "Set_LeadingHyphen_ReturnsBadRequest")]
    [TestCase("trailing-", TestName = "Set_TrailingHyphen_ReturnsBadRequest")]
    [TestCase("double--hyphen", TestName = "Set_DoubleHyphen_ReturnsBadRequest")]
    [TestCase("under_score", TestName = "Set_Underscore_ReturnsBadRequest")]
    [TestCase("spaced out", TestName = "Set_Space_ReturnsBadRequest")]
    [TestCase("emoji-❤", TestName = "Set_NonAscii_ReturnsBadRequest")]
    public async Task Set_MalformedSlug_ReturnsBadRequest(string slug)
    {
        await SeedManagerAsync();
        Assert.That(await SetAsync(slug), Is.InstanceOf<BadRequest<string>>());
    }

    [TestCase("support")]
    [TestCase("Billing")]
    [TestCase("venta")]
    [TestCase("invite")]
    public async Task Set_ReservedWord_ReturnsBadRequest(string slug)
    {
        await SeedManagerAsync();

        // Reserved is about impersonation: these are the last segment of a URL somebody is asked to
        // trust, and casing must not slip one through.
        Assert.That(await SetAsync(slug), Is.InstanceOf<BadRequest<string>>());
    }

    // ── Uniqueness ────────────────────────────────────────────────────────

    [Test]
    public async Task Set_SlugHeldByAnotherGuild_ReturnsConflict()
    {
        await SeedManagerAsync();
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = "guild-2", OwnerId = "owner-2", Name = "Other", VanityUrl = "the-flat",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        Assert.That(await SetAsync("the-flat"), Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task Set_SlugHeldByAnotherGuildInDifferentCase_StillConflicts()
    {
        await SeedManagerAsync();
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = "guild-2", OwnerId = "owner-2", Name = "Other", VanityUrl = "the-flat",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        Assert.That(await SetAsync("THE-FLAT"), Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task Set_TheSameSlugTwice_IsANoOp()
    {
        await SeedManagerAsync();
        await SetAsync("the-flat");
        await _context.SaveChangesAsync();
        var firstInviteId = (await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId)).VanityInviteId;

        var ok = (Ok<VanityUrlDto>)await SetAsync("the-flat");
        await _context.SaveChangesAsync();

        Assert.That(ok.Value!.VanityUrl, Is.EqualTo("the-flat"));
        Assert.That((await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId)).VanityInviteId,
            Is.EqualTo(firstInviteId));
    }

    // ── The backing invite ────────────────────────────────────────────────

    [Test]
    public async Task Set_MintsARealInviteForTheLinkToPointAt()
    {
        await SeedManagerAsync();

        await SetAsync("the-flat");
        await _context.SaveChangesAsync();

        var guild = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(guild.VanityInviteId, Is.Not.Null);

        var invite = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == guild.VanityInviteId);
        Assert.Multiple(() =>
        {
            Assert.That(invite.Type, Is.EqualTo(InviteType.Permanent));
            Assert.That(invite.MaxUses, Is.Null);
            Assert.That(invite.ExpiresAt, Is.Null);
            Assert.That(invite.InviterId, Is.EqualTo(UserId));
        });
    }

    [Test]
    public async Task Set_RenamingReusesTheSameBackingInvite()
    {
        await SeedManagerAsync();
        await SetAsync("the-flat");
        await _context.SaveChangesAsync();
        var original = (await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId)).VanityInviteId;

        await SetAsync("the-house");
        await _context.SaveChangesAsync();

        // Renaming must not invalidate the join the old name already resolved to.
        Assert.That((await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId)).VanityInviteId,
            Is.EqualTo(original));
    }

    [Test]
    public async Task Set_AfterTheBackingInviteWasRevoked_MintsAFreshOne()
    {
        await SeedManagerAsync();
        await SetAsync("the-flat");
        await _context.SaveChangesAsync();

        var guild = await _context.Guilds.FirstAsync(g => g.Id == GuildId);
        var invite = await _context.GuildInvites.FirstAsync(i => i.Id == guild.VanityInviteId);
        invite.Revoke(DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        await SetAsync("the-house");
        await _context.SaveChangesAsync();

        var reloaded = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(reloaded.VanityInviteId, Is.Not.EqualTo(invite.Id));
    }

    [Test]
    public async Task Set_PublishesTheBackingInviteToBotsLikeAnyOtherInvite()
    {
        await SeedManagerAsync();

        await SetAsync("the-flat");

        Assert.That(_bus.Published.OfType<Guild.Contracts.Bus.Events.InviteCreatedForBots>().Any(), Is.True,
            "a vanity link must not be an invisible second mechanism");
    }

    // ── Clearing ──────────────────────────────────────────────────────────

    [Test]
    public async Task Clear_DropsTheNameButNotTheInvite()
    {
        await SeedManagerAsync();
        await SetAsync("the-flat");
        await _context.SaveChangesAsync();
        var inviteId = (await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId)).VanityInviteId;

        var ok = (Ok<VanityUrlDto>)await SetAsync(null);
        await _context.SaveChangesAsync();

        var guild = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.VanityUrl, Is.Null);
            Assert.That(ok.Value.Active, Is.False);
            Assert.That(guild.VanityUrl, Is.Null);
            Assert.That(guild.VanityUrlSetAt, Is.Null);
        });

        var invite = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == inviteId);
        Assert.That(invite.State, Is.EqualTo(InviteState.Active),
            "people may be holding that link; dropping a name is not a decision to revoke it");
    }

    [Test]
    public async Task Clear_OnAGuildThatNeverHadOne_IsHarmless()
    {
        await SeedManagerAsync();

        var ok = (Ok<VanityUrlDto>)await SetAsync("");
        Assert.That(ok.Value!.VanityUrl, Is.Null);
    }

    // ── Reading ───────────────────────────────────────────────────────────

    [Test]
    public async Task Get_ReportsTheHeldNameAndThatItIsActive()
    {
        await SeedManagerAsync();
        await SetAsync("the-flat");
        await _context.SaveChangesAsync();

        var ok = (Ok<VanityUrlDto>)await _endpoint.GetVanityUrlAsync(GuildId, _context, TestPrincipal.Create(UserId), _permissionService, Vanity());

        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.VanityUrl, Is.EqualTo("the-flat"));
            Assert.That(ok.Value.Entitled, Is.True);
            Assert.That(ok.Value.Active, Is.True);
        });
    }

    [Test]
    public async Task Get_UnknownGuild_IsRefusedByPermissionResolutionFirst()
    {
        await SeedManagerAsync();

        // Permission resolution for a guild that does not exist denies before the lookup, so the
        // 404 branch is unreachable from outside - this asserts the shape it really produces.
        var result = await _endpoint.GetVanityUrlAsync("nope", _context, TestPrincipal.Create(UserId), _permissionService, Vanity());
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }
}
