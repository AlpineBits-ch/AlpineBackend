using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Temporary membership end to end: a disconnect schedules, a reconnect cancels, and only the sweep
/// removes anybody.
/// </summary>
[TestFixture]
public class TemporaryMembershipTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string MemberId = "member-1";
    private const string EveryoneRoleId = "role-everyone";

    private TestGuildContext _context = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private GuildHydrateService _hydrate = null!;
    private GuildPermissionService _permissions = null!;
    private TemporaryMembershipSweepService _sweep = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _hydrate = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _permissions = new GuildPermissionService(new FakeDistributedCache(), _context, NullLogger<GuildPermissionService>.Instance);
        _sweep = new TemporaryMembershipSweepService(_hub, new ThrowingScopeFactory(), NullLogger<TemporaryMembershipSweepService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _sweep.Dispose();
        await _context.DisposeAsync();
    }

    /// <summary>The sweep's own scope factory is never used - the test drives
    /// <c>SweepWithAsync</c> directly with the dependencies it already has - so a factory that
    /// refuses is the honest stand-in.</summary>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException(
            "The sweep under test is driven directly; nothing here should resolve a scope.");
    }

    private async Task SeedGuildAsync()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "The Flat",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role { Id = EveryoneRoleId, GuildId = GuildId, Name = "Everyone", Type = RoleType.Everyone, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private async Task<GuildMember> SeedMemberAsync(bool temporary, string memberId = MemberId, string userId = UserId,
        DateTimeOffset? dueAt = null)
    {
        var member = new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = userId.ToUpperInvariant(), TemporaryMembership = temporary,
            TemporaryEvictionDueAt = dueAt,
        };
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(new RoleMember { Id = $"rm-everyone-{memberId}", RoleId = EveryoneRoleId, MemberId = memberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
        return member;
    }

    private async Task GrantAnExtraRoleAsync(string memberId)
    {
        _context.Roles.Add(new Role { Id = "role-regular", GuildId = GuildId, Name = "Regular", Type = RoleType.None, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-regular", RoleId = "role-regular", MemberId = memberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private async Task<DateTimeOffset?> DueAtAsync(string memberId = MemberId) =>
        (await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.Id == memberId)).TemporaryEvictionDueAt;

    // ── Scheduling ────────────────────────────────────────────────────────

    [Test]
    public async Task Disconnect_SchedulesATemporaryMemberRatherThanEvictingThem()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true);
        var now = DateTimeOffset.UtcNow;

        await TemporaryMembershipService.ScheduleEvictionsAsync(UserId, _context, now);
        await _context.SaveChangesAsync();

        var stillThere = await _context.GuildMembers.AsNoTracking().AnyAsync(m => m.Id == MemberId);
        var dueAt = await DueAtAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stillThere, Is.True, "a socket closing is not a departure");
            Assert.That(dueAt, Is.EqualTo(now + TemporaryMembershipService.Grace));
        });
    }

    [Test]
    public async Task Disconnect_LeavesAnOrdinaryMembershipAlone()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: false);

        await TemporaryMembershipService.ScheduleEvictionsAsync(UserId, _context, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        Assert.That(await DueAtAsync(), Is.Null);
    }

    [Test]
    public async Task Disconnect_LeavesATemporaryMemberWhoEarnedARoleAlone()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true);
        await GrantAnExtraRoleAsync(MemberId);

        await TemporaryMembershipService.ScheduleEvictionsAsync(UserId, _context, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        // Being given a role is what converts a temporary membership into a permanent one; @everyone
        // is not a role in that sense, because everybody has it.
        Assert.That(await DueAtAsync(), Is.Null);
    }

    [Test]
    public async Task Disconnect_SchedulesEveryGuildTheAccountIsTemporarilyIn()
    {
        await SeedGuildAsync();
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild { Id = "guild-2", OwnerId = OwnerId, Name = "Other", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
        await SeedMemberAsync(temporary: true);
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-2", GuildId = "guild-2", UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = "USER-1", TemporaryMembership = true,
        });
        await _context.SaveChangesAsync();

        await TemporaryMembershipService.ScheduleEvictionsAsync(UserId, _context, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        Assert.That(await DueAtAsync("member-2"), Is.Not.Null);
    }

    [Test]
    public async Task Reconnect_CancelsThePendingEviction()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true, dueAt: DateTimeOffset.UtcNow.AddMinutes(2));

        await TemporaryMembershipService.CancelEvictionsAsync(UserId, _context);
        await _context.SaveChangesAsync();

        Assert.That(await DueAtAsync(), Is.Null, "a blip costs nothing");
    }

    // ── The sweep ─────────────────────────────────────────────────────────

    private Task SweepAsync(DateTimeOffset now) =>
        _sweep.SweepWithAsync(_context, _bus, _hydrate, _permissions, now);

    [Test]
    public async Task Sweep_RemovesAMemberWhoseGraceHasRunOut()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true, dueAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await SweepAsync(DateTimeOffset.UtcNow);

        Assert.That(await _context.GuildMembers.AsNoTracking().AnyAsync(m => m.Id == MemberId), Is.False);
    }

    [Test]
    public async Task Sweep_LeavesAMemberWhoseGraceHasNotElapsed()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true, dueAt: DateTimeOffset.UtcNow.AddMinutes(3));

        await SweepAsync(DateTimeOffset.UtcNow);

        Assert.That(await _context.GuildMembers.AsNoTracking().AnyAsync(m => m.Id == MemberId), Is.True);
    }

    [Test]
    public async Task Sweep_IgnoresMembersWithNoPendingEviction()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true);
        await SeedMemberAsync(temporary: false, memberId: "member-2", userId: "user-2");

        await SweepAsync(DateTimeOffset.UtcNow);

        Assert.That(await _context.GuildMembers.AsNoTracking().CountAsync(), Is.EqualTo(2));
    }

    /// <summary>A role granted while the member was offline never passes through the disconnect
    /// handler, so the sweep has to ask again or the grant is lost minutes later.</summary>
    [Test]
    public async Task Sweep_SparesAMemberGivenARoleDuringTheGraceWindow()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true, dueAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await GrantAnExtraRoleAsync(MemberId);

        await SweepAsync(DateTimeOffset.UtcNow);

        var member = await _context.GuildMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == MemberId);
        Assert.Multiple(() =>
        {
            Assert.That(member, Is.Not.Null);
            Assert.That(member!.TemporaryEvictionDueAt, Is.Null, "and the question is not reconsidered");
        });
    }

    [Test]
    public async Task Sweep_AnnouncesTheDepartureTheWayLeavingDoes()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true, dueAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await SweepAsync(DateTimeOffset.UtcNow);

        var sent = ((FakeHubClients)_hub.Clients).SentMessages.Select(m => m.Method).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(sent, Does.Contain("guild.MemberLeft"));
            Assert.That(((FakeHubClients)_hub.Clients).RecipientsOf("guild.MemberLeft"), Does.Contain(UserId),
                "the member being removed has to hear about it too - nobody else will tell their client");
        });
    }

    [Test]
    public async Task Sweep_PublishesMemberRemovedForBotsWithItsOwnReason()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true, dueAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await SweepAsync(DateTimeOffset.UtcNow);

        var removed = _bus.Published.OfType<Guild.Contracts.Bus.Events.MemberRemovedForBots>().SingleOrDefault();
        Assert.That(removed, Is.Not.Null);
        Assert.That(removed!.Reason, Is.EqualTo("TemporaryMembershipEnded"));
    }

    [Test]
    public async Task Sweep_WithNothingDue_DoesNothingAtAll()
    {
        await SeedGuildAsync();
        await SeedMemberAsync(temporary: true);

        await SweepAsync(DateTimeOffset.UtcNow);

        Assert.That(((FakeHubClients)_hub.Clients).SentMessages, Is.Empty);
        Assert.That(_bus.Published, Is.Empty);
    }
}
