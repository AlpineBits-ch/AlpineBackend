using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AppEnvironment;
using Echo.Billing;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Wire;
using Echo.Moderation;
using Echo.Tests.Support;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Echo.Tests.Billing;

/// <summary>Who may do what on the billing surface.</summary>
[TestFixture]
[Category("Unit")]
[NonParallelizable]
public class BillingStaffGateTests
{
    // ── Doubles ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Answers the one request <see cref="StaffAccess"/> makes.</summary>
    private sealed class StaffBus(string role) : IMessageBus
    {
        public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        {
            object response = new IsUserAdministrativeResponse
            {
                Role = role,
                IsAdministrative = role == "Admin",
                IsStaff = role is "Admin" or "Moderator",
                UserName = "staff",
            };

            return Task.FromResult((T)response);
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

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedVersions : IEntitlementVersionProvider
    {
        public ValueTask<long> VersionAsync(EntitlementSubject subject, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(7L);
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    private string _mode = null!;
    private string _url = null!;

    [SetUp]
    public void Deploy()
    {
        _mode = Env.License.Mode;
        _url = Env.License.BillingServiceUrl;

        Env.License.Mode = LicenseConfiguration.Hosted;
        Env.License.BillingServiceUrl = "http://billing.test";
    }

    [TearDown]
    public void Restore()
    {
        Env.License.Mode = _mode;
        Env.License.BillingServiceUrl = _url;
    }

    /// <summary>A controller wired to a fake Identity answer and a counting Billing.</summary>
    private static (AdminBillingController Controller, CountingHandler Billing) Console(
        string role, HttpStatusCode billingAnswers = HttpStatusCode.BadRequest)
    {
        var handler = new CountingHandler(billingAnswers,
            """{"code":"reason_required","message":"A grant needs a reason."}""");

        var client = new BillingServiceClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://billing.test") },
            new RecordingLogger<BillingServiceClient>());

        var invalidator = new EntitlementCacheInvalidator(
            new DisabledEntitlementCacheStore(),
            new EntitlementCacheKeyspace("test", "abcdef01"),
            new EntitlementSetCodec(),
            new EntitlementCacheOptions(),
            new RecordingLogger<EntitlementCacheInvalidator>());

        var controller = new AdminBillingController(
            context: null!,
            new StaffAccess(new StaffBus(role), new RecordingLogger<StaffAccess>()),
            client,
            new EntitlementResolver([]),
            new FixedVersions(),
            invalidator,
            new RecordingLogger<AdminBillingController>());

        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user_staff")]));
        http.Request.Headers.Authorization = "Bearer console-token";

        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return (controller, handler);
    }

    private static (int Status, string? Code) Refusal(IActionResult result)
    {
        var payload = (ObjectResult)result;
        var code = payload.Value!.GetType().GetProperty("code")?.GetValue(payload.Value) as string;

        return (payload.StatusCode!.Value, code);
    }

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    // ── A moderator cannot write ──────────────────────────────────────────────────────────────

    [Test]
    public async Task A_moderator_cannot_issue_a_grant()
    {
        var (console, billing) = Console("Moderator");

        var (status, code) = Refusal(await console.IssueGrantAsync(
            Body("""{"subjectKind":"Guild","subjectId":"guild_1","grantKind":"Plan","plan":"pro","reason":"x","source":"Staff"}"""),
            CancellationToken.None));

        Assert.That(status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(code, Is.EqualTo("admin_required"));
        Assert.That(billing.Calls, Is.Zero, "the write must not reach Billing at all");
    }

    [Test]
    public async Task A_moderator_cannot_edit_a_plan()
    {
        var (console, billing) = Console("Moderator");

        var (status, code) = Refusal(await console.EditPlanAsync(
            "pro", Body("""{"values":{"voice.max_participants":"50"},"reason":"x"}"""), CancellationToken.None));

        Assert.That(status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(code, Is.EqualTo("admin_required"));
        Assert.That(billing.Calls, Is.Zero);
    }

    [Test]
    public async Task A_moderator_cannot_force_a_cache_invalidation()
    {
        var (console, _) = Console("Moderator");

        var (status, code) = Refusal(
            await console.InvalidateAsync("Guild", "guild_1", CancellationToken.None));

        Assert.That(status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(code, Is.EqualTo("admin_required"));
    }

    [Test]
    public async Task A_moderator_cannot_revoke_a_grant_or_move_a_subject_between_plan_versions()
    {
        var (console, billing) = Console("Moderator");

        var revoke = Refusal(await console.RevokeGrantAsync(
            "gnt_1", Body("""{"reason":"x"}"""), CancellationToken.None));

        var move = Refusal(await console.AssignPlanAsync(
            "Guild", "guild_1", Body("""{"plan":"pro","reason":"x"}"""), CancellationToken.None));

        Assert.That(revoke.Code, Is.EqualTo("admin_required"));
        Assert.That(move.Code, Is.EqualTo("admin_required"));
        Assert.That(billing.Calls, Is.Zero);
    }

    // ── A moderator can read ──────────────────────────────────────────────────────────────────

    /// <summary>The provenance screen is the one that makes a billing ticket answerable, and a
    /// support agent who has to escalate to see it escalates every ticket.</summary>
    [Test]
    public async Task A_moderator_can_read_the_provenance_screen()
    {
        var (console, _) = Console("Moderator");

        var result = (OkObjectResult)await console.ProvenanceAsync("Guild", "guild_1", CancellationToken.None);
        var payload = (EntitlementProvenanceDto)result.Value!;

        Assert.That(payload.SubjectId, Is.EqualTo("guild_1"));
        Assert.That(payload.Entries, Is.Not.Empty);
        Assert.That(payload.Version, Is.EqualTo(7));
    }

    /// <summary>The catalogue tells the console which controls to draw.</summary>
    [Test]
    public async Task The_catalogue_tells_a_moderator_it_cannot_write_and_an_admin_that_it_can()
    {
        var (moderator, _) = Console("Moderator");
        var (admin, _) = Console("Admin");

        var forModerator = (BillingConsoleCatalogueDto)((OkObjectResult)await moderator.CatalogueAsync()).Value!;
        var forAdmin = (BillingConsoleCatalogueDto)((OkObjectResult)await admin.CatalogueAsync()).Value!;

        Assert.That(forModerator.CanWrite, Is.False);
        Assert.That(forAdmin.CanWrite, Is.True);
        Assert.That(forAdmin.Keys, Is.Not.Empty);
    }

    // ── An admin can write ────────────────────────────────────────────────────────────────────

    /// <summary>The other side of the gate: an administrator's write is passed on.</summary>
    [Test]
    public async Task An_admin_reaches_billing_and_the_refusal_comes_back_intact()
    {
        var (console, billing) = Console("Admin");

        var result = (ContentResult)await console.IssueGrantAsync(
            Body("""{"subjectKind":"Guild","subjectId":"guild_1","grantKind":"Plan","plan":"pro","reason":"","source":"Staff"}"""),
            CancellationToken.None);

        Assert.That(billing.Calls, Is.EqualTo(1));
        Assert.That(result.StatusCode, Is.EqualTo(400));
        Assert.That(result.Content, Does.Contain("reason_required"));
    }

    // ── Failing closed ────────────────────────────────────────────────────────────────────────

    /// <summary>A non-staff account is refused as forbidden, and a check that could not be completed
    /// is refused as unavailable. Both deny; only one means the session is over, and conflating them
    /// is what makes an operator retype a password because the bus hiccuped.</summary>
    [Test]
    public async Task A_non_staff_account_is_refused()
    {
        var (console, billing) = Console("None");

        var (status, code) = Refusal(await console.CatalogueAsync());

        Assert.That(status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(code, Is.EqualTo("staff_required"));
        Assert.That(billing.Calls, Is.Zero);
    }

    /// <summary>An unknown subject kind is refused before anything is asked of Billing or of the
    /// resolver, and the refusal names the kinds that exist.</summary>
    [Test]
    public async Task An_unknown_subject_kind_is_refused_by_name()
    {
        var (console, billing) = Console("Admin");

        var result = (ObjectResult)await console.GrantsAsync("Channel", "chan_1", false, CancellationToken.None);
        var message = result.Value!.GetType().GetProperty("message")!.GetValue(result.Value) as string;

        Assert.That(result.StatusCode, Is.EqualTo(400));
        Assert.That(message, Does.Contain("Guild"));
        Assert.That(billing.Calls, Is.Zero);
    }
}
