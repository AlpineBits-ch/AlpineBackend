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

/// <summary>The console's credit routes, which are forwarding and nothing else.</summary>
[TestFixture]
[Category("Unit")]
[NonParallelizable]
public class CreditAdminForwardingTests
{
    // ── Doubles ───────────────────────────────────────────────────────────────────────────────

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

    /// <summary>Records what was sent, because on this surface the request is the thing under test.
    /// The body is read inside the handler: the client disposes the request the moment it
    /// returns.</summary>
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Sent { get; } = [];

        public int Calls => Sent.Count;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Sent.Add((request.Method.Method, request.RequestUri!.PathAndQuery, content));

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FixedVersions : IEntitlementVersionProvider
    {
        public ValueTask<long> VersionAsync(EntitlementSubject subject, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(7L);
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    private const string Wallet =
        """{"userId":"user_1","balance":500,"capPoints":10000,"lots":[],"disclaimer":"No cash value."}""";

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

    private static (AdminBillingController Controller, RecordingHandler Billing) Console(
        string role, HttpStatusCode billingAnswers = HttpStatusCode.OK, string billingBody = Wallet)
    {
        var handler = new RecordingHandler(billingAnswers, billingBody);

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

    private static (int Status, string? Code, string? Message) Refusal(IActionResult result)
    {
        var payload = (ObjectResult)result;
        var value = payload.Value!;

        return (
            payload.StatusCode!.Value,
            value.GetType().GetProperty("code")?.GetValue(value) as string,
            value.GetType().GetProperty("message")?.GetValue(value) as string);
    }

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    // ── The happy path ────────────────────────────────────────────────────────────────────────

    /// <summary>The route the service publishes, verbatim, and its answer back unaltered.</summary>
    [Test]
    public async Task A_wallet_read_lands_on_the_service_route_and_the_answer_comes_back_whole()
    {
        var (console, billing) = Console("Admin");

        var result = (ContentResult)await console.CreditWalletAsync("user_1", CancellationToken.None);

        Assert.That(billing.Sent, Has.Count.EqualTo(1));
        Assert.That(billing.Sent[0].Method, Is.EqualTo("GET"));
        Assert.That(billing.Sent[0].Path, Is.EqualTo("/api/v1/credit/wallets/user_1"));
        Assert.That(result.StatusCode, Is.EqualTo(200));
        Assert.That(result.Content, Is.EqualTo(Wallet));
        Assert.That(result.ContentType, Is.EqualTo("application/json"));
    }

    /// <summary>An unspecified limit becomes the service's own default rather than being sent as zero,
    /// which the service would read as "no rows".</summary>
    [Test]
    public async Task The_ledger_carries_a_limit_and_defaults_it_rather_than_sending_zero()
    {
        var (console, billing) = Console("Admin");

        await console.CreditLedgerAsync("user_1", 0, CancellationToken.None);
        await console.CreditLedgerAsync("user_1", 50, CancellationToken.None);

        Assert.That(billing.Sent[0].Path, Is.EqualTo("/api/v1/credit/wallets/user_1/ledger?limit=200"));
        Assert.That(billing.Sent[1].Path, Is.EqualTo("/api/v1/credit/wallets/user_1/ledger?limit=50"));
    }

    /// <summary>The two operations whose whole request is their URL send no body at all.</summary>
    [Test]
    public async Task A_pause_and_a_rebuild_send_nothing_but_their_route()
    {
        var (console, billing) = Console("Admin", HttpStatusCode.BadRequest,
            """{"code":"campaign_not_found","message":"No campaign summer."}""");

        await console.RebuildCreditAsync("user_1", CancellationToken.None);
        await console.PauseCreditCampaignAsync("summer", CancellationToken.None);

        Assert.That(billing.Sent[0].Path, Is.EqualTo("/api/v1/credit/wallets/user_1/rebuild"));
        Assert.That(billing.Sent[0].Body, Is.Null);
        Assert.That(billing.Sent[1].Path, Is.EqualTo("/api/v1/credit/campaigns/summer/pause"));
        Assert.That(billing.Sent[1].Body, Is.Null);
    }

    /// <summary>A body is forwarded as it was posted rather than reshaped.</summary>
    [Test]
    public async Task A_budget_change_forwards_the_verb_the_route_and_the_body_unaltered()
    {
        var (console, billing) = Console("Admin", HttpStatusCode.BadRequest,
            """{"code":"campaign_budget_below_issued","message":"3200 credits are already issued."}""");

        const string body = """{"totalBudgetPoints":2000}""";

        await console.SetCreditCampaignBudgetAsync("summer", Body(body), CancellationToken.None);

        Assert.That(billing.Sent[0].Method, Is.EqualTo("PATCH"));
        Assert.That(billing.Sent[0].Path, Is.EqualTo("/api/v1/credit/campaigns/summer/budget"));
        Assert.That(billing.Sent[0].Body, Is.EqualTo(body));
    }

    /// <summary>An id is a string somebody pasted out of a ticket, not a path segment.</summary>
    [Test]
    public async Task An_id_is_escaped_into_the_path()
    {
        var (console, billing) = Console("Admin");

        await console.CreditWalletAsync("user 1/2", CancellationToken.None);

        Assert.That(billing.Sent[0].Path, Is.EqualTo("/api/v1/credit/wallets/user%201%2F2"));
    }

    // ── The refusal, which is the point of the hop ────────────────────────────────────────────

    /// <summary>Billing's refusal, code and sentence, straight through.</summary>
    [Test]
    public async Task A_refusal_survives_the_hop_with_its_code_and_its_sentence()
    {
        const string refusal =
            """
            {"code":"campaign_budget_exhausted","message":"The campaign 'summer' has 120 credits left and this issuance is 500. Raise the budget or issue outside the campaign."}
            """;

        var (console, billing) = Console("Admin", HttpStatusCode.BadRequest, refusal);

        var result = (ContentResult)await console.IssueCreditAsync(
            "user_1",
            Body("""{"amount":500,"reason":"goodwill","idempotencyKey":"k1","campaign":"summer"}"""),
            CancellationToken.None);

        Assert.That(billing.Calls, Is.EqualTo(1));
        Assert.That(result.StatusCode, Is.EqualTo(400));
        Assert.That(result.Content, Is.EqualTo(refusal), "the body must not be reshaped");
        Assert.That(result.Content, Does.Contain("campaign_budget_exhausted"));
        Assert.That(result.Content, Does.Contain("Raise the budget"));
    }

    /// <summary>The same for the refusal that is a rule rather than a limit.</summary>
    [Test]
    public async Task The_no_refund_refusal_reaches_the_console_in_the_services_own_words()
    {
        const string refusal =
            """
            {"code":"not_reversible","message":"Only an issuance can be reversed. Revoke the grant instead."}
            """;

        var (console, _) = Console("Admin", HttpStatusCode.BadRequest, refusal);

        var result = (ContentResult)await console.ReverseCreditEntryAsync(
            "cent_1", Body("""{"reason":"issued by mistake"}"""), CancellationToken.None);

        Assert.That(result.StatusCode, Is.EqualTo(400));
        Assert.That(result.Content, Is.EqualTo(refusal));
    }

    /// <summary>A 404 stays a 404. Billing answers one for a campaign that does not exist and for an
    /// entry it cannot find, and collapsing those into 400 would make "you typed the wrong code" look
    /// like "your request was malformed".</summary>
    [Test]
    public async Task A_not_found_from_billing_is_not_turned_into_something_else()
    {
        var (console, _) = Console("Admin", HttpStatusCode.NotFound,
            """{"code":"campaign_not_found","message":"No campaign summer."}""");

        var result = (ContentResult)await console.PauseCreditCampaignAsync("summer", CancellationToken.None);

        Assert.That(result.StatusCode, Is.EqualTo(404));
        Assert.That(result.Content, Does.Contain("campaign_not_found"));
    }

    // ── The reason, refused here ──────────────────────────────────────────────────────────────

    /// <summary>A blank reason never reaches the service.</summary>
    [Test]
    public async Task An_issue_with_a_blank_or_whitespace_reason_never_reaches_billing()
    {
        var (console, billing) = Console("Admin");

        var blank = Refusal(await console.IssueCreditAsync(
            "user_1", Body("""{"amount":500,"reason":"","idempotencyKey":"k1"}"""), CancellationToken.None));

        var whitespace = Refusal(await console.IssueCreditAsync(
            "user_1", Body("""{"amount":500,"reason":"   ","idempotencyKey":"k1"}"""), CancellationToken.None));

        var missing = Refusal(await console.IssueCreditAsync(
            "user_1", Body("""{"amount":500,"idempotencyKey":"k1"}"""), CancellationToken.None));

        Assert.That(blank.Status, Is.EqualTo(400));
        Assert.That(blank.Code, Is.EqualTo("reason_required"));
        Assert.That(blank.Message, Does.Contain("append-only"));
        Assert.That(whitespace.Code, Is.EqualTo("reason_required"));
        Assert.That(missing.Code, Is.EqualTo("reason_required"));
        Assert.That(billing.Calls, Is.Zero, "nothing may reach the ledger without a reason");
    }

    /// <summary>Every write that moves a balance, not only the issuance.</summary>
    [Test]
    public async Task An_adjustment_a_reversal_and_a_void_all_need_a_reason_before_anything_is_sent()
    {
        var (console, billing) = Console("Admin");

        var adjust = Refusal(await console.AdjustCreditAsync(
            "user_1", Body("""{"amount":-100,"idempotencyKey":"k1"}"""), CancellationToken.None));

        var reverse = Refusal(await console.ReverseCreditEntryAsync(
            "cent_1", Body("""{"reason":" "}"""), CancellationToken.None));

        var voided = Refusal(await console.VoidCreditAsync(
            "user_1", Body("{}"), CancellationToken.None));

        Assert.That(adjust.Code, Is.EqualTo("reason_required"));
        Assert.That(reverse.Code, Is.EqualTo("reason_required"));
        Assert.That(voided.Code, Is.EqualTo("reason_required"));
        Assert.That(billing.Calls, Is.Zero);
    }

    // ── Who may do what ───────────────────────────────────────────────────────────────────────

    /// <summary>A non-staff account is refused exactly as it is on every other billing route: 403 with
    /// <c>staff_required</c>, and nothing asked of Billing. The console discards the session on that
    /// code, which is why it must not be the code an unreachable dependency produces.</summary>
    [Test]
    public async Task A_non_staff_caller_is_refused_the_way_every_other_billing_route_refuses_one()
    {
        var (console, billing) = Console("None");

        var read = Refusal(await console.CreditWalletAsync("user_1", CancellationToken.None));
        var campaigns = Refusal(await console.CreditCampaignsAsync(CancellationToken.None));

        var write = Refusal(await console.IssueCreditAsync(
            "user_1", Body("""{"amount":500,"reason":"x","idempotencyKey":"k1"}"""), CancellationToken.None));

        Assert.That(read.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(read.Code, Is.EqualTo("staff_required"));
        Assert.That(campaigns.Code, Is.EqualTo("staff_required"));
        Assert.That(write.Code, Is.EqualTo("staff_required"));
        Assert.That(billing.Calls, Is.Zero);
    }

    /// <summary>A moderator reads and does not write.</summary>
    [Test]
    public async Task A_moderator_can_read_a_wallet_and_cannot_move_a_balance()
    {
        var (console, billing) = Console("Moderator");

        var wallet = (ContentResult)await console.CreditWalletAsync("user_1", CancellationToken.None);
        var reads = billing.Calls;

        var writes = new[]
        {
            Refusal(await console.IssueCreditAsync(
                "user_1", Body("""{"amount":5,"reason":"x","idempotencyKey":"k1"}"""), CancellationToken.None)),
            Refusal(await console.AdjustCreditAsync(
                "user_1", Body("""{"amount":5,"reason":"x","idempotencyKey":"k1"}"""), CancellationToken.None)),
            Refusal(await console.ReverseCreditEntryAsync(
                "cent_1", Body("""{"reason":"x"}"""), CancellationToken.None)),
            Refusal(await console.VoidCreditAsync(
                "user_1", Body("""{"reason":"x"}"""), CancellationToken.None)),
            Refusal(await console.RebuildCreditAsync("user_1", CancellationToken.None)),
            Refusal(await console.CreateCreditCampaignAsync(
                Body("""{"code":"summer","description":"d","totalBudgetPoints":10}"""), CancellationToken.None)),
            Refusal(await console.PauseCreditCampaignAsync("summer", CancellationToken.None)),
            Refusal(await console.SetCreditCampaignBudgetAsync(
                "summer", Body("""{"totalBudgetPoints":10}"""), CancellationToken.None)),
        };

        Assert.That(wallet.StatusCode, Is.EqualTo(200));
        Assert.That(reads, Is.EqualTo(1));

        Assert.Multiple(() =>
        {
            foreach (var write in writes)
            {
                Assert.That(write.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
                Assert.That(write.Code, Is.EqualTo("admin_required"));
            }
        });

        Assert.That(billing.Calls, Is.EqualTo(reads),
            "a moderator's write must not reach Billing at all, so a billing outage is not the "
            + "difference between refused and not");
    }

    /// <summary>An instance with no billing service answers the credit routes the way it answers every
    /// other one: a sentence with a code on it, and nothing sent. Section 8.8 hides the surface in
    /// selfhost, and this is what it falls back to when something asks anyway.</summary>
    [Test]
    public async Task A_selfhost_instance_answers_the_credit_routes_without_calling_anything()
    {
        Env.License.Mode = LicenseConfiguration.SelfHost;
        Env.License.BillingServiceUrl = string.Empty;

        var (console, billing) = Console("Admin");

        var result = (ContentResult)await console.CreditWalletAsync("user_1", CancellationToken.None);

        Assert.That(billing.Calls, Is.Zero);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(result.Content, Does.Contain("billing_not_deployed"));
    }
}
