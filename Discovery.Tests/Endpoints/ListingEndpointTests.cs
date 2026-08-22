using System.Security.Claims;
using Discovery.Api.Dtos.Request;
using Discovery.Api.Dtos.Response;
using Discovery.Api.Endpoints;
using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Discovery.Tests.Helpers;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Realtime;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests.Endpoints;

/// <summary>
/// Covers the plan gate (draft saves skip it, publish enforces it strictly), the two permission
/// layers (ManageGuild via the bus, the entitlement via <see cref="ListingWriteService"/>), and the
/// two write-time validation rules likeliest to regress: the topic cap and the link allowlist.
/// </summary>
[TestFixture]
public class ListingEndpointTests
{
    private const string GuildId = "gld_1";
    private const string ManagerId = "usr_manager";
    private const string OutsiderId = "usr_outsider";

    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>An <see cref="EntitlementResolver"/> whose answer the test dictates - or that
    /// throws when <paramref name="granted"/> is null, standing in for "Billing is unreachable".
    /// <see cref="CallCount"/> is the actual proof a caller never resolved this: <c>IsEntitledAsync</c>
    /// catches any exception here and turns it into a <c>NotEntitled</c> refusal the draft-save
    /// endpoint does not even branch on, so a swallowed call would still return 200 with a null DTO -
    /// asserting on the throw alone would not have caught that.</summary>
    private sealed class StubEntitlementResolver(bool? granted) : EntitlementResolver([])
    {
        public int CallCount { get; private set; }

        public override Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (granted is null) throw new InvalidOperationException("Billing is unreachable");

            return Task.FromResult(new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault)
                .Flag(EntitlementKeys.GuildPublicListing, granted.Value)
                .Build());
        }
    }

    /// <summary>Hand-rolled no-op IHubContext&lt;EchoRealtimeHub&gt; - Discovery.Tests has no
    /// existing one (unlike Guild.Tests/Messaging.Tests), and this suite is the only place under
    /// Discovery.Tests that needs it, so it stays local rather than becoming a new shared file.</summary>
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
            public IClientProxy User(string userId) => new FakeClientProxy(owner, [userId]);
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

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    /// <summary>A bus that answers the permission check every route makes, and the membership
    /// lookup a realtime push makes - one real member and one bot, so a fan-out test can pin that
    /// the bot is excluded.</summary>
    private static FakeMessageBus BusWithPermission(bool allowed)
    {
        var bus = new FakeMessageBus();
        bus.RespondWith<HasUserPermissionToGuildRequest, HasUserPermissionToGuildResponse>(request =>
            new HasUserPermissionToGuildResponse
            {
                GuildId = request.GuildId, UserId = request.UserId, IsAllowed = allowed, Permission = request.Permission,
            });
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

    private static UpsertListingDraftDto ValidDraft(
        int topicCount = 1, IReadOnlyList<string>? links = null, IReadOnlyList<string>? topics = null) => new()
    {
        Headline = "Come play with us",
        Pitch = "A friendly, low-drama community looking for more people.",
        Language = "en",
        JoinPolicy = "Open",
        Topics = topics ?? Enumerable.Range(0, topicCount).Select(i => $"tag:topic-{i}").ToList(),
        Links = links ?? [],
    };

    private static object GetValue(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;
    private static int? GetStatusCode(IResult result) => (int?)result.GetType().GetProperty("StatusCode")?.GetValue(result);

    // ── The plan gate ────────────────────────────────────────────────────────

    [Test]
    public async Task Publishing_without_the_entitlement_answers_the_documented_error_code()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now),
            NullLogger<ListingWriteService>.Instance, new StubEntitlementResolver(false));
        var realtime = new ListingRealtime(hub, bus);

        await ListingEndpoint.SaveDraftAsync(GuildId, ValidDraft(), writes, realtime, Principal(ManagerId), bus, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var result = await ListingEndpoint.PublishAsync(GuildId, writes, realtime, Principal(ManagerId), bus, CancellationToken.None);

        var value = GetValue(result);
        Assert.Multiple(() =>
        {
            Assert.That(GetStatusCode(result), Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(value.GetType().GetProperty("error")!.GetValue(value), Is.EqualTo("public_listing_not_entitled"));
        });
    }

    [Test]
    public async Task Publishing_with_the_entitlement_publishes_and_pushes_one_event()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now),
            NullLogger<ListingWriteService>.Instance, new StubEntitlementResolver(true));
        var realtime = new ListingRealtime(hub, bus);

        await ListingEndpoint.SaveDraftAsync(GuildId, ValidDraft(), writes, realtime, Principal(ManagerId), bus, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var result = await ListingEndpoint.PublishAsync(GuildId, writes, realtime, Principal(ManagerId), bus, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var ok = (Ok<ListingDto>)result;
        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.State, Is.EqualTo("Published"));
            // Exactly one push: the draft save that preceded this had nobody to tell, since nothing
            // was public yet.
            Assert.That(hub.Sent, Has.Count.EqualTo(1));
            Assert.That(hub.Sent[0].Method, Is.EqualTo("discovery.ListingPublished"));
            Assert.That(hub.Sent[0].UserIds, Is.EquivalentTo(new[] { "usr_member_1" }), "the bot must not be in the audience");
        });
    }

    [Test]
    public async Task Saving_a_draft_never_checks_the_entitlement()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var resolver = new StubEntitlementResolver(true);
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now),
            NullLogger<ListingWriteService>.Instance, resolver);
        var realtime = new ListingRealtime(hub, bus);

        var result = await ListingEndpoint.SaveDraftAsync(GuildId, ValidDraft(), writes, realtime, Principal(ManagerId), bus, CancellationToken.None);

        // The counter is the assertion, not the response shape - see the class doc on
        // StubEntitlementResolver for why a swallowed call would still look like a 200 here.
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ListingDto>>());
            Assert.That(resolver.CallCount, Is.Zero);
        });
    }

    // ── Bump ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Bumping_inside_the_cooldown_answers_409_with_the_next_available_time()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now),
            NullLogger<ListingWriteService>.Instance, new StubEntitlementResolver(true));
        var realtime = new ListingRealtime(hub, bus);

        await ListingEndpoint.SaveDraftAsync(GuildId, ValidDraft(), writes, realtime, Principal(ManagerId), bus, CancellationToken.None);
        await ctx.SaveChangesAsync();
        await ListingEndpoint.PublishAsync(GuildId, writes, realtime, Principal(ManagerId), bus, CancellationToken.None);
        await ctx.SaveChangesAsync();

        // Publish itself counts as the first bump, so the cooldown is already running.
        var result = await ListingEndpoint.BumpAsync(GuildId, writes, Principal(ManagerId), bus, CancellationToken.None);

        var value = GetValue(result);
        var bumpAvailableAt = (DateTimeOffset?)value.GetType().GetProperty("bumpAvailableAt")!.GetValue(value);
        Assert.Multiple(() =>
        {
            Assert.That(GetStatusCode(result), Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(bumpAvailableAt, Is.EqualTo(Now + Listing.BumpCooldown),
                "the client renders its countdown from this value - a bare 409 would leave the button lying");
        });
    }

    [Test]
    public async Task Bumping_an_unpublished_draft_does_not_claim_a_fake_cooldown()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now), NullLogger<ListingWriteService>.Instance);
        var realtime = new ListingRealtime(hub, bus);

        await ListingEndpoint.SaveDraftAsync(GuildId, ValidDraft(), writes, realtime, Principal(ManagerId), bus, CancellationToken.None);
        await ctx.SaveChangesAsync();

        // Never published - Listing.Bump would answer the same false as a real cooldown would, which
        // is exactly the collapse that made a Draft (and, from task 11, a Suspended listing) lie to
        // the client about waiting out a cooldown that does not exist.
        var result = await ListingEndpoint.BumpAsync(GuildId, writes, Principal(ManagerId), bus, CancellationToken.None);

        var value = GetValue(result);
        Assert.Multiple(() =>
        {
            Assert.That(GetStatusCode(result), Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(value.GetType().GetProperty("error")!.GetValue(value), Is.EqualTo("listing_not_published"));
            Assert.That(value.GetType().GetProperty("bumpAvailableAt"), Is.Null,
                "this shape must not carry a countdown field at all - there is nothing to count down from");
        });
    }

    // ── Permission ───────────────────────────────────────────────────────────

    [Test]
    public async Task A_user_without_ManageGuild_cannot_write_the_listing()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(false);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now), NullLogger<ListingWriteService>.Instance);
        var realtime = new ListingRealtime(hub, bus);

        var result = await ListingEndpoint.SaveDraftAsync(GuildId, ValidDraft(), writes, realtime, Principal(OutsiderId), bus, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Test]
    public async Task More_than_eight_topics_is_refused()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now), NullLogger<ListingWriteService>.Instance);
        var realtime = new ListingRealtime(hub, bus);

        var result = await ListingEndpoint.SaveDraftAsync(GuildId, ValidDraft(topicCount: 9), writes, realtime, Principal(ManagerId), bus, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(ctx.Listings.Any(), Is.False, "a rejected request must not write a partial listing");
        });
    }

    [Test]
    public async Task Links_outside_the_allowlist_are_refused()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now), NullLogger<ListingWriteService>.Instance);
        var realtime = new ListingRealtime(hub, bus);

        var dto = ValidDraft(links: ["https://not-on-the-list.example.com/invite"]);
        var result = await ListingEndpoint.SaveDraftAsync(GuildId, dto, writes, realtime, Principal(ManagerId), bus, CancellationToken.None);

        var badRequest = (BadRequest<string>)result;
        Assert.Multiple(() =>
        {
            // A user pasting their own domain must be told a known set of sites is all that is
            // allowed right now, not shown a generic validation error that reads as a bug.
            Assert.That(badRequest.Value, Does.Contain("known set of sites"));
            Assert.That(ctx.Listings.Any(), Is.False, "a rejected request must not write a partial listing");
        });
    }

    [TestCase("https://discord.gg/abc123", TestName = "An_allowed_link_host_is_accepted_Original")]
    [TestCase("https://roll20.net/campaigns/details/12345", TestName = "An_allowed_link_host_is_accepted_NewlyAdded")]
    public async Task An_allowed_link_host_is_accepted(string link)
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now), NullLogger<ListingWriteService>.Instance);
        var realtime = new ListingRealtime(hub, bus);

        var dto = ValidDraft(links: [link]);
        var result = await ListingEndpoint.SaveDraftAsync(GuildId, dto, writes, realtime, Principal(ManagerId), bus, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Ok<ListingDto>>());
    }

    [TestCase("https://www.youtube.com/@x", TestName = "The_www_form_of_an_allowed_host_is_accepted_Youtube")]
    [TestCase("https://www.reddit.com/r/x", TestName = "The_www_form_of_an_allowed_host_is_accepted_Reddit")]
    public async Task The_www_form_of_an_allowed_host_is_accepted(string link)
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now), NullLogger<ListingWriteService>.Instance);
        var realtime = new ListingRealtime(hub, bus);

        var dto = ValidDraft(links: [link]);
        var result = await ListingEndpoint.SaveDraftAsync(GuildId, dto, writes, realtime, Principal(ManagerId), bus, CancellationToken.None);

        // Copy-paste from the address bar hands out this form for most of the allowlist - refusing
        // it would make the list reject its own entries.
        Assert.That(result, Is.InstanceOf<Ok<ListingDto>>());
    }

    [Test]
    public async Task A_non_http_scheme_is_refused_even_on_an_allowed_host()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now), NullLogger<ListingWriteService>.Instance);
        var realtime = new ListingRealtime(hub, bus);

        var dto = ValidDraft(links: ["ftp://discord.gg/x"]);
        var result = await ListingEndpoint.SaveDraftAsync(GuildId, dto, writes, realtime, Principal(ManagerId), bus, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task An_unknown_game_topic_is_refused_and_nothing_is_written()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = BusWithPermission(true);
        var hub = new FakeHub();
        var writes = new ListingWriteService(ctx, new TopicResolver(ctx), new TestClock(Now), NullLogger<ListingWriteService>.Instance);
        var realtime = new ListingRealtime(hub, bus);

        var dto = ValidDraft(topics: ["game:gapp_nonexistent"]);
        var result = await ListingEndpoint.SaveDraftAsync(GuildId, dto, writes, realtime, Principal(ManagerId), bus, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(ctx.Listings.Any(), Is.False, "a rejected request must not write a partial listing");
            Assert.That(ctx.ListingTopics.Any(), Is.False,
                "an unknown game must not be persisted as a topic - the describe fallback would then hand it back as a mislabelled tag");
        });
    }

    // ── DI wiring ────────────────────────────────────────────────────────────

    [Test]
    public void ListingWriteService_resolves_through_a_real_service_provider()
    {
        // Every other test in this file constructs ListingWriteService by hand, which cannot catch a
        // missing DI registration - Program.cs shipped with no TimeProvider registered for a while
        // and every unit test here stayed green through it. This one goes through a real
        // ServiceProvider instead, mirroring Program.cs's registrations for this slice of the graph
        // (MicroserviceContext swapped for the InMemory test context, since a unit test cannot reach
        // the real Postgres/Redis/RabbitMQ Program.cs also wires up).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<MicroserviceContext>(_ => TestDiscoveryContext.New());
        services.AddScoped<TopicResolver>();
        services.AddScoped<ListingWriteService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.That(scope.ServiceProvider.GetRequiredService<ListingWriteService>(), Is.Not.Null);
    }
}
