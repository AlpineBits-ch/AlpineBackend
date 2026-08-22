using Billing.Contracts.Bus.Events;
using Discovery.Api.Bus;
using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Tests.Helpers;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Realtime;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Tests.Bus;

/// <summary>
/// A lapsed plan suspends a published listing without deleting it, leaves a draft alone, ignores
/// billing traffic for an unrelated key or a user subject, and never republishes on its own when the
/// flag comes back - see the class doc on <see cref="EntitlementsChangedHandler"/>.
/// </summary>
[TestFixture]
public class EntitlementsChangedHandlerTests
{
    private const string GuildId = "gld_1";

    /// <summary>Answers a fixed grant for <see cref="EntitlementKeys.GuildPublicListing"/> and counts
    /// calls, so a filtered-out event can be pinned as never having reached Billing.</summary>
    private sealed class StubEntitlementResolver(bool granted) : EntitlementResolver([])
    {
        public int CallCount { get; private set; }

        public override Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault)
                .Flag(EntitlementKeys.GuildPublicListing, granted)
                .Build());
        }
    }

    /// <summary>Hand-rolled no-op IHubContext&lt;EchoRealtimeHub&gt; - local to this suite the same
    /// way ListingEndpointTests keeps its own rather than sharing one across Discovery.Tests.</summary>
    private sealed class FakeHub : IHubContext<EchoRealtimeHub>
    {
        public List<(string Method, IReadOnlyList<string> UserIds)> Sent { get; } = [];

        public IHubClients Clients { get; }
        public IGroupManager Groups => throw new NotSupportedException();

        public FakeHub() => Clients = new FakeHubClients(this);

        private sealed class FakeHubClients(FakeHub owner) : IHubClients
        {
            public IClientProxy All => throw new NotSupportedException();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Client(string connectionId) => throw new NotSupportedException();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
            public IClientProxy Group(string groupName) => throw new NotSupportedException();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
            public IClientProxy User(string userId) => throw new NotSupportedException();
            public IClientProxy Users(IReadOnlyList<string> userIds) => new FakeClientProxy(owner, userIds);
        }

        private sealed class FakeClientProxy(FakeHub owner, IReadOnlyList<string> userIds) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                owner.Sent.Add((method, userIds));
                return Task.CompletedTask;
            }
        }
    }

    private static FakeMessageBus BusWithMembers()
    {
        var bus = new FakeMessageBus();
        bus.RespondWith<ListGuildMembersRequest, ListGuildMembersResponse>(_ =>
            new ListGuildMembersResponse
            {
                Members =
                [
                    new GuildMemberSummary { UserId = "usr_member_1" },
                    new GuildMemberSummary { UserId = "usr_bot_1", IsBot = true },
                ],
            });
        return bus;
    }

    private static EntitlementsChanged Event(SubjectKind kind, string subjectId, params string[] changedKeys) => new()
    {
        SubjectKind = kind,
        SubjectId = subjectId,
        Reason = EntitlementsChangedReason.GrantExpired,
        Version = 1,
        ChangedKeys = changedKeys.ToList(),
        OccurredAt = DateTimeOffset.UtcNow,
    };

    private static Listing AddListing(TestDiscoveryContext ctx, ListingState state, SuspensionReason? suspendedReason = null)
    {
        var listing = Listing.Create(GuildId);
        listing.Headline = "Come play with us";
        listing.State = state;
        listing.SuspendedReason = suspendedReason;
        ctx.Listings.Add(listing);
        return listing;
    }

    [Test]
    public async Task Losing_the_flag_suspends_a_published_listing()
    {
        await using var ctx = TestDiscoveryContext.New();
        AddListing(ctx, ListingState.Published);
        await ctx.SaveChangesAsync();

        var bus = BusWithMembers();
        var hub = new FakeHub();
        var realtime = new ListingRealtime(hub, bus);
        var resolver = new StubEntitlementResolver(granted: false);

        await EntitlementsChangedHandler.Handle(
            Event(SubjectKind.Guild, GuildId, EntitlementKeys.GuildPublicListing.Name),
            ctx, realtime, resolver, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var listing = ctx.Listings.Single(l => l.GuildId == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Suspended));
            Assert.That(listing.SuspendedReason, Is.EqualTo(SuspensionReason.PlanLapsed));
            Assert.That(listing.Headline, Is.EqualTo("Come play with us"), "content survives a suspension");
            Assert.That(hub.Sent, Has.Count.EqualTo(1));
            Assert.That(hub.Sent[0].Method, Is.EqualTo("discovery.ListingSuspended"));
            Assert.That(hub.Sent[0].UserIds, Is.EquivalentTo(new[] { "usr_member_1" }), "the bot must not be in the audience");
        });
    }

    [Test]
    public async Task Losing_the_flag_leaves_a_draft_alone()
    {
        await using var ctx = TestDiscoveryContext.New();
        AddListing(ctx, ListingState.Draft);
        await ctx.SaveChangesAsync();

        var bus = BusWithMembers();
        var hub = new FakeHub();
        var realtime = new ListingRealtime(hub, bus);
        var resolver = new StubEntitlementResolver(granted: false);

        await EntitlementsChangedHandler.Handle(
            Event(SubjectKind.Guild, GuildId, EntitlementKeys.GuildPublicListing.Name),
            ctx, realtime, resolver, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var listing = ctx.Listings.Single(l => l.GuildId == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Draft));
            Assert.That(hub.Sent, Is.Empty, "a draft was never in the feed, so nothing left it");
        });
    }

    [Test]
    public async Task An_event_for_an_unrelated_key_changes_nothing()
    {
        await using var ctx = TestDiscoveryContext.New();
        AddListing(ctx, ListingState.Published);
        await ctx.SaveChangesAsync();

        var bus = BusWithMembers();
        var hub = new FakeHub();
        var realtime = new ListingRealtime(hub, bus);
        var resolver = new StubEntitlementResolver(granted: false);

        await EntitlementsChangedHandler.Handle(
            Event(SubjectKind.Guild, GuildId, EntitlementKeys.GuildRecruitment.Name),
            ctx, realtime, resolver, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var listing = ctx.Listings.Single(l => l.GuildId == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Published));
            Assert.That(hub.Sent, Is.Empty);
            // The load-bearing assertion: an unrelated key must never even resolve, or every plan
            // edit and grant across the instance would cost this handler a broker hop.
            Assert.That(resolver.CallCount, Is.Zero);
        });
    }

    [Test]
    public async Task Regaining_the_flag_does_not_republish_by_itself()
    {
        await using var ctx = TestDiscoveryContext.New();
        AddListing(ctx, ListingState.Suspended, SuspensionReason.PlanLapsed);
        await ctx.SaveChangesAsync();

        var bus = BusWithMembers();
        var hub = new FakeHub();
        var realtime = new ListingRealtime(hub, bus);
        var resolver = new StubEntitlementResolver(granted: true);

        await EntitlementsChangedHandler.Handle(
            Event(SubjectKind.Guild, GuildId, EntitlementKeys.GuildPublicListing.Name),
            ctx, realtime, resolver, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var listing = ctx.Listings.Single(l => l.GuildId == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Suspended),
                "republishing is the owner's own action, never a side effect of a billing webhook");
            Assert.That(listing.SuspendedReason, Is.EqualTo(SuspensionReason.PlanLapsed));
            Assert.That(hub.Sent, Is.Empty);
        });
    }

    [Test]
    public async Task A_user_subject_event_is_ignored()
    {
        await using var ctx = TestDiscoveryContext.New();
        AddListing(ctx, ListingState.Published);
        await ctx.SaveChangesAsync();

        var bus = BusWithMembers();
        var hub = new FakeHub();
        var realtime = new ListingRealtime(hub, bus);
        var resolver = new StubEntitlementResolver(granted: false);

        await EntitlementsChangedHandler.Handle(
            Event(SubjectKind.User, "usr_someone", EntitlementKeys.GuildPublicListing.Name),
            ctx, realtime, resolver, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var listing = ctx.Listings.Single(l => l.GuildId == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Published));
            Assert.That(hub.Sent, Is.Empty);
            Assert.That(resolver.CallCount, Is.Zero);
        });
    }
}
