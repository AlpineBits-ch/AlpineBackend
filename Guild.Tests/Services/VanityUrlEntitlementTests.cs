using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
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

namespace Guild.Tests.Services;

/// <summary>The half of the vanity feature that the downgrade terms are binding on.</summary>
[TestFixture]
public class VanityUrlEntitlementTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private FakeHubContext _hub = null!;
    private FakeInvokingMessageBus _bus = null!;
    private GuildInviteAudienceService _audience = null!;
    private AuditLogService _auditLog = null!;
    private FakeInvitePreviewRateLimiter _limiter = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = PermissionTestFactory.Create(_cache, _context);
        _hub = new FakeHubContext();
        _bus = new FakeInvokingMessageBus();
        _auditLog = new AuditLogService(_context);
        _limiter = new FakeInvitePreviewRateLimiter();
        _audience = new GuildInviteAudienceService(_context,
            new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance));
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>An <see cref="EntitlementResolver"/> whose answer the test dictates - or that throws,
    /// which is the "Billing is unreachable" case the two fail directions differ on.</summary>
    private sealed class StubResolver(bool? granted) : EntitlementResolver([])
    {
        public override Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken = default)
        {
            if (granted is null) throw new InvalidOperationException("Billing is unreachable");

            return Task.FromResult(new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault)
                .Flag(EntitlementKeys.GuildVanityUrl, granted.Value)
                .Build());
        }
    }

    private VanityUrlService Vanity(bool? granted) =>
        new(_context, NullLogger<VanityUrlService>.Instance, new StubResolver(granted));

    private async Task<GuildInvite> SeedEntitledGuildAsync(string slug)
    {
        var guild = new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "The Flat",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.Guilds.Add(guild);
        _context.Roles.Add(new Role { Id = "role-1", GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageGuild, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = "member-1", GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-1" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = "role-1", MemberId = "member-1", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var invite = GuildInvite.Create(new CreateGuildInviteParams { GuildId = GuildId, Type = InviteType.Permanent });
        _context.GuildInvites.Add(invite);
        guild.VanityUrl = slug;
        guild.VanityUrlSetAt = DateTimeOffset.UtcNow;
        guild.VanityInviteId = invite.Id;
        await _context.SaveChangesAsync();
        return invite;
    }

    // ── Resolution degrades, and only degrades ────────────────────────────

    [Test]
    public async Task Resolution_WhileEntitled_FindsTheInvite()
    {
        var invite = await SeedEntitledGuildAsync("the-flat");

        var resolved = await Vanity(granted: true).ResolveInviteAsync("the-flat");

        Assert.That(resolved?.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task Resolution_AfterDowngrade_StopsResolving()
    {
        await SeedEntitledGuildAsync("the-flat");

        var resolved = await Vanity(granted: false).ResolveInviteAsync("the-flat");

        Assert.That(resolved, Is.Null, "9.1: a vanity invite not included in the new tier stops resolving");
    }

    [Test]
    public async Task Resolution_AfterDowngrade_DoesNotTouchTheStoredName()
    {
        await SeedEntitledGuildAsync("the-flat");

        await Vanity(granted: false).ResolveInviteAsync("the-flat");

        var guild = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(guild.VanityUrl, Is.EqualTo("the-flat"), "9.2: the name is held in reserve for the guild");
            Assert.That(guild.VanityInviteId, Is.Not.Null);
        });
    }

    [Test]
    public async Task Resolution_AfterRestoringThePlan_WorksAgainWithTheSameName()
    {
        var invite = await SeedEntitledGuildAsync("the-flat");

        Assert.That(await Vanity(granted: false).ResolveInviteAsync("the-flat"), Is.Null);

        // 12.1/12.2: automatic, no re-upload, no reconfiguration, no support request.
        var restored = await Vanity(granted: true).ResolveInviteAsync("the-flat");
        Assert.That(restored?.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task Resolution_AfterDowngrade_LeavesOrdinaryInvitesAlone()
    {
        await SeedEntitledGuildAsync("the-flat");
        var ordinary = GuildInvite.Create(new CreateGuildInviteParams { GuildId = GuildId, Type = InviteType.Permanent });
        _context.GuildInvites.Add(ordinary);
        await _context.SaveChangesAsync();

        var endpoint = new InviteEndpoint();
        var ok = (Ok<InviteDto>)await endpoint.GetInviteAsync(ordinary.Code, _context, new DefaultHttpContext(), _limiter, Vanity(granted: false));

        // 9.3: ordinary invite links are unaffected on every tier.
        Assert.That(ok.Value!.Id, Is.EqualTo(ordinary.Id));
    }

    [Test]
    public async Task Resolution_WhenBillingIsUnreachable_FailsOpen()
    {
        var invite = await SeedEntitledGuildAsync("the-flat");

        var resolved = await Vanity(granted: null).ResolveInviteAsync("the-flat");

        // An unreadable plan must not take every guild's landing page down with it.
        Assert.That(resolved?.Id, Is.EqualTo(invite.Id));
    }

    // ── Claiming fails the other way ──────────────────────────────────────

    [Test]
    public async Task Claim_WithoutTheEntitlement_IsRefused()
    {
        await SeedEntitledGuildAsync("the-flat");
        var endpoint = new VanityUrlEndpoint();

        var result = await endpoint.SetVanityUrlAsync(GuildId, new SetVanityUrlDto { VanityUrl = "somewhere-else" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, Vanity(granted: false), _hub, _audience, _bus);

        var statusCode = (int?)result.GetType().GetProperty("StatusCode")!.GetValue(result);
        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task Claim_WhenBillingIsUnreachable_FailsClosed()
    {
        await SeedEntitledGuildAsync("the-flat");
        var endpoint = new VanityUrlEndpoint();

        var result = await endpoint.SetVanityUrlAsync(GuildId, new SetVanityUrlDto { VanityUrl = "somewhere-else" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, Vanity(granted: null), _hub, _audience, _bus);

        // The claim is the persistent artifact 7.3 is about.
        var statusCode = (int?)result.GetType().GetProperty("StatusCode")!.GetValue(result);
        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        var guild = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(guild.VanityUrl, Is.EqualTo("the-flat"));
    }

    /// <summary>A downgraded guild must still be able to give the name up - 11.6 keeps the settings
    /// screens reachable on every tier, and a control that only moves one way is not a setting.</summary>
    [Test]
    public async Task Clear_WithoutTheEntitlement_IsAllowed()
    {
        await SeedEntitledGuildAsync("the-flat");
        var endpoint = new VanityUrlEndpoint();

        var result = await endpoint.SetVanityUrlAsync(GuildId, new SetVanityUrlDto { VanityUrl = null },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, Vanity(granted: false), _hub, _audience, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<VanityUrlDto>>());
        var guild = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(guild.VanityUrl, Is.Null);
    }

    [Test]
    public async Task Read_WhileDowngraded_ReportsTheNameAndThatItIsInactive()
    {
        await SeedEntitledGuildAsync("the-flat");
        var endpoint = new VanityUrlEndpoint();

        var ok = (Ok<VanityUrlDto>)await endpoint.GetVanityUrlAsync(GuildId, _context,
            TestPrincipal.Create(UserId), _permissionService, Vanity(granted: false));

        // Returning null here would be indistinguishable from the name having been taken away, which
        // is the one thing 9.2 promises does not happen.
        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.VanityUrl, Is.EqualTo("the-flat"));
            Assert.That(ok.Value.Entitled, Is.False);
            Assert.That(ok.Value.Active, Is.False);
        });
    }
}
