using System.Security.Claims;
using Discovery.Contracts.Bus.Admin;
using Echo.Controllers.Admin;
using Echo.Domain.Entities.Moderation;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Echo.Tests.Support;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Echo.Tests.Controllers;

/// <summary>
/// The gateway console for banning a guild out of discovery. Discovery itself knows nothing about
/// staff, so what matters here is the staff gate, telling an outage apart from a refusal, and that
/// the actor recorded on a ban or a lift is the resolved principal rather than anything a caller
/// could put in the body.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AdminDiscoveryControllerTests
{
    private sealed class TestContext() : MicroserviceContext(
        new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Already configured via the constructor options. Calling base would add a conflicting
            // Postgres provider and throw at runtime.
        }
    }

    /// <summary>
    /// Answers the staff check from <paramref name="role"/>, null meaning the check itself fails
    /// rather than completing and saying no. Scripts a fixed Discovery response and records what was
    /// sent to it.
    /// </summary>
    private sealed class FakeBus(string? role) : IMessageBus
    {
        public bool DiscoveryUnavailable { get; set; }
        public bool NextLiftFindsNoBan { get; set; }
        public BanGuildFromDiscoveryRequest? LastBan { get; private set; }
        public LiftDiscoveryBanRequest? LastLift { get; private set; }
        public int DiscoveryCalls { get; private set; }

        public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        {
            if (message is IsUserAdministrativeRequest)
            {
                if (role is null) throw new TimeoutException("identity did not answer");

                object staffResponse = new IsUserAdministrativeResponse
                {
                    Role = role,
                    IsAdministrative = role == "Admin",
                    IsStaff = role is "Admin" or "Moderator",
                    UserName = "staff",
                };
                return Task.FromResult((T)staffResponse);
            }

            DiscoveryCalls++;
            if (DiscoveryUnavailable) throw new TimeoutException("discovery did not answer");

            object response = message switch
            {
                BanGuildFromDiscoveryRequest ban => RecordBan(ban),
                LiftDiscoveryBanRequest lift => RecordLift(lift),
                ListDiscoveryBansRequest => new ListDiscoveryBansResponse(),
                SearchDiscoveryListingsRequest => new SearchDiscoveryListingsResponse(),
                _ => throw new NotSupportedException(message.GetType().Name),
            };

            return Task.FromResult((T)response);
        }

        private BanGuildFromDiscoveryResponse RecordBan(BanGuildFromDiscoveryRequest ban)
        {
            LastBan = ban;
            return new BanGuildFromDiscoveryResponse { BanId = "dban_1" };
        }

        private LiftDiscoveryBanResponse RecordLift(LiftDiscoveryBanRequest lift)
        {
            LastLift = lift;
            return new LiftDiscoveryBanResponse { Lifted = !NextLiftFindsNoBan };
        }

        public Guid? CorrelationId => null;
        public string? TenantId { get; set; }
        public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => Task.CompletedTask;
        public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
        public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => throw new NotImplementedException();
        public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
        public Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
        public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
        public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
        public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, CancellationToken cancellation = default) => throw new NotImplementedException();
        public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, DeliveryOptions options, CancellationToken cancellation = default) => throw new NotImplementedException();
        public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
        public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
        public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null) => throw new NotImplementedException();
        public IReadOnlyList<Envelope> PreviewSubscriptions(object message) => throw new NotImplementedException();
        public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options) => throw new NotImplementedException();
        public IDestinationEndpoint EndpointFor(Uri uri) => throw new NotImplementedException();
        public IDestinationEndpoint EndpointFor(string endpointName) => throw new NotImplementedException();
    }

    private static (AdminDiscoveryController Controller, FakeBus Bus, TestContext Db) Console(
        string? role, string userId = "user_staff")
    {
        var bus = new FakeBus(role);
        var db = new TestContext();

        var controller = new AdminDiscoveryController(
            db,
            new StaffAccess(bus, new RecordingLogger<StaffAccess>()),
            bus,
            new RecordingLogger<AdminDiscoveryController>());

        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)]));
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return (controller, bus, db);
    }

    private static (int Status, string? Code) Refusal(IActionResult result)
    {
        var payload = (ObjectResult)result;
        var value = payload.Value!;
        return (payload.StatusCode!.Value, value.GetType().GetProperty("code")?.GetValue(value) as string);
    }

    // ── Who may reach it ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_non_staff_caller_is_refused_on_every_route_and_nothing_reaches_discovery()
    {
        var (console, bus, _) = Console(role: "None");

        var listings = Refusal(await console.ListingsAsync(query: null, cursor: null, CancellationToken.None));
        var bans = Refusal(await console.BansAsync(includeLifted: false, CancellationToken.None));
        var ban = Refusal(await console.BanAsync(
            new BanGuildRequest { GuildId = "gld_1", Reason = "Abuse" }, CancellationToken.None));
        var lift = Refusal(await console.LiftBanAsync("gld_1", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(listings.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(listings.Code, Is.EqualTo("staff_required"));
            Assert.That(bans.Code, Is.EqualTo("staff_required"));
            Assert.That(ban.Code, Is.EqualTo("staff_required"));
            Assert.That(lift.Code, Is.EqualTo("staff_required"));
        });

        Assert.That(bus.DiscoveryCalls, Is.Zero, "a refused caller must never reach Discovery");
    }

    /// <summary>The regression StaffRefusalTests pins in general; here specifically for the route a
    /// caller would actually forge an outage against, the write.</summary>
    [Test]
    public async Task A_staff_check_that_could_not_complete_is_refused_as_unavailable_not_as_forbidden()
    {
        var (console, bus, _) = Console(role: null);

        var result = Refusal(await console.BanAsync(
            new BanGuildRequest { GuildId = "gld_1", Reason = "Abuse" }, CancellationToken.None));

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(result.Code, Is.EqualTo("staff_check_unavailable"));
        Assert.That(result.Code, Is.Not.EqualTo("staff_required"), "an outage must not read as a plain refusal");
        Assert.That(bus.DiscoveryCalls, Is.Zero);
    }

    // ── Who gets recorded ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_ban_and_a_lift_record_the_resolved_principal_never_anything_from_the_body()
    {
        var (console, bus, db) = Console(role: "Moderator", userId: "user_moderator_7");

        await console.BanAsync(
            new BanGuildRequest { GuildId = "gld_1", Reason = "Abuse reports", StaffNote = "internal only" },
            CancellationToken.None);

        Assert.That(bus.LastBan!.StaffUserId, Is.EqualTo("user_moderator_7"));
        Assert.That(bus.LastBan.GuildId, Is.EqualTo("gld_1"));

        var banAudit = await db.ModerationAuditEntries.SingleAsync(e => e.Action == ModerationAuditActions.DiscoveryBanIssued);
        Assert.That(banAudit.ActorUserId, Is.EqualTo("user_moderator_7"));

        await console.LiftBanAsync("gld_1", CancellationToken.None);

        Assert.That(bus.LastLift!.StaffUserId, Is.EqualTo("user_moderator_7"));
        Assert.That(bus.LastLift.GuildId, Is.EqualTo("gld_1"));

        var liftAudit = await db.ModerationAuditEntries.SingleAsync(e => e.Action == ModerationAuditActions.DiscoveryBanLifted);
        Assert.That(liftAudit.ActorUserId, Is.EqualTo("user_moderator_7"));

        // A second window, banning as a different account, must record that account and not the
        // first: nothing about the actor is cached or reused across calls.
        var (secondConsole, secondBus, _) = Console(role: "Admin", userId: "user_admin_2");
        await secondConsole.BanAsync(
            new BanGuildRequest { GuildId = "gld_2", Reason = "Abuse reports" }, CancellationToken.None);

        Assert.That(secondBus.LastBan!.StaffUserId, Is.EqualTo("user_admin_2"));
    }

    /// <summary>No active ban to lift is an outcome, not a failure, and does not go on the record -
    /// there is nothing an actor did.</summary>
    [Test]
    public async Task Lifting_a_guild_with_no_active_ban_succeeds_without_an_audit_entry()
    {
        var (console, bus, db) = Console(role: "Admin", userId: "user_admin_1");
        bus.NextLiftFindsNoBan = true;

        var result = (OkObjectResult)await console.LiftBanAsync("gld_9", CancellationToken.None);

        Assert.That(result.StatusCode, Is.EqualTo(200));
        Assert.That(await db.ModerationAuditEntries.CountAsync(), Is.Zero);
    }
}
