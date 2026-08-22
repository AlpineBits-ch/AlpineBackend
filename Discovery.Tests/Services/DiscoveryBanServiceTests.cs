using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Tests.Helpers;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;

namespace Discovery.Tests.Services;

/// <summary>
/// Pins the rules in spec 8.3: active is lifted-at-null-and-not-expired, evaluated on every read
/// with no sweeper; a lifted ban keeps its row so a guild can be banned again; and the two effects a
/// ban has on a published listing - suspend it with StaffAction, and never undo that on a lift.
/// </summary>
[TestFixture]
public class DiscoveryBanServiceTests
{
    private const string GuildId = "gld_1";
    private const string StaffId = "usr_staff";

    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static FakeMessageBus BusWithMembers()
    {
        var bus = new FakeMessageBus();
        bus.RespondWith<ListGuildMembersRequest, ListGuildMembersResponse>(_ =>
            new ListGuildMembersResponse { Members = [new GuildMemberSummary { UserId = "usr_member_1" }] });
        return bus;
    }

    private static DiscoveryBanService Service(TestDiscoveryContext ctx, out FakeHub hub) =>
        new(ctx, new ListingRealtime(hub = new FakeHub(), BusWithMembers()));

    [Test]
    public async Task A_ban_with_no_expiry_stays_active()
    {
        await using var ctx = TestDiscoveryContext.New();
        var service = Service(ctx, out _);

        await service.BanAsync(GuildId, "Repeated harassment reports.", null, StaffId, Now, expiresAt: null, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var active = await service.IsBannedAsync(GuildId, Now + TimeSpan.FromDays(365), CancellationToken.None);

        Assert.That(active, Is.Not.Null);
    }

    [Test]
    public async Task An_expired_ban_is_not_active()
    {
        await using var ctx = TestDiscoveryContext.New();
        var service = Service(ctx, out _);

        await service.BanAsync(GuildId, "Temporary cooldown.", null, StaffId, Now, expiresAt: Now + TimeSpan.FromDays(7), CancellationToken.None);
        await ctx.SaveChangesAsync();

        var active = await service.IsBannedAsync(GuildId, Now + TimeSpan.FromDays(8), CancellationToken.None);

        Assert.That(active, Is.Null);
    }

    [Test]
    public async Task A_lifted_ban_is_not_active()
    {
        await using var ctx = TestDiscoveryContext.New();
        var service = Service(ctx, out _);

        await service.BanAsync(GuildId, "Spam listing.", null, StaffId, Now, expiresAt: null, CancellationToken.None);
        await ctx.SaveChangesAsync();

        await service.LiftAsync(GuildId, StaffId, Now + TimeSpan.FromHours(1), CancellationToken.None);
        await ctx.SaveChangesAsync();

        var active = await service.IsBannedAsync(GuildId, Now + TimeSpan.FromHours(2), CancellationToken.None);

        Assert.That(active, Is.Null);
    }

    [Test]
    public async Task Lifting_keeps_the_row_and_records_who()
    {
        await using var ctx = TestDiscoveryContext.New();
        var service = Service(ctx, out _);

        await service.BanAsync(GuildId, "Off-topic recruitment.", "Confirmed via three reports.", StaffId, Now, expiresAt: null, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var liftedAt = Now + TimeSpan.FromHours(1);
        await service.LiftAsync(GuildId, "usr_other_staff", liftedAt, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var row = ctx.DiscoveryBans.Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.LiftedAt, Is.EqualTo(liftedAt));
            Assert.That(row.LiftedByUserId, Is.EqualTo("usr_other_staff"));
            // The original ban is untouched history, not overwritten.
            Assert.That(row.Reason, Is.EqualTo("Off-topic recruitment."));
            Assert.That(row.StaffNote, Is.EqualTo("Confirmed via three reports."));
        });
    }

    [Test]
    public async Task A_guild_can_be_banned_again_after_a_lift()
    {
        await using var ctx = TestDiscoveryContext.New();
        var service = Service(ctx, out _);

        await service.BanAsync(GuildId, "First offense.", null, StaffId, Now, expiresAt: null, CancellationToken.None);
        await ctx.SaveChangesAsync();
        await service.LiftAsync(GuildId, StaffId, Now + TimeSpan.FromDays(1), CancellationToken.None);
        await ctx.SaveChangesAsync();

        // A unique index on GuildId would make this insert fail - it must not exist.
        await service.BanAsync(GuildId, "Second offense.", null, StaffId, Now + TimeSpan.FromDays(2), expiresAt: null, CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ctx.DiscoveryBans.Count(), Is.EqualTo(2));
            Assert.That(ctx.DiscoveryBans.Count(b => b.GuildId == GuildId), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Banning_suspends_a_published_listing_with_staff_action()
    {
        await using var ctx = TestDiscoveryContext.New();
        var service = Service(ctx, out var hub);

        var listing = Listing.Create(GuildId);
        listing.Publish(Now);
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        await service.BanAsync(GuildId, "Directory abuse.", "Automated detection flag 88.", StaffId, Now, expiresAt: null, CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Suspended));
            Assert.That(listing.SuspendedReason, Is.EqualTo(SuspensionReason.StaffAction));
            Assert.That(hub.Sent, Has.Count.EqualTo(1));
            Assert.That(hub.Sent[0].Method, Is.EqualTo("discovery.ListingSuspended"));
        });
    }

    [Test]
    public async Task Lifting_does_not_republish()
    {
        await using var ctx = TestDiscoveryContext.New();
        var service = Service(ctx, out _);

        var listing = Listing.Create(GuildId);
        listing.Publish(Now);
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        await service.BanAsync(GuildId, "Directory abuse.", null, StaffId, Now, expiresAt: null, CancellationToken.None);
        await ctx.SaveChangesAsync();

        await service.LiftAsync(GuildId, StaffId, Now + TimeSpan.FromHours(1), CancellationToken.None);
        await ctx.SaveChangesAsync();

        // Returning to the public feed is the owner's decision, the same rule as a lapsed plan -
        // lifting the ban must not touch the listing at all.
        Assert.That(listing.State, Is.EqualTo(ListingState.Suspended));
    }
}
