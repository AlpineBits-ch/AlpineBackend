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

/// <summary>The console's promotion routes, which are forwarding and nothing else.</summary>
[TestFixture]
[Category("Unit")]
[NonParallelizable]
public class PromotionAdminForwardingTests
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

    private const string Campaign =
        """
        {"id":"pcmp_1","code":"pro-trial","description":"Thirty days of Pro.","plan":"pro","trialDays":30,"subjectKind":"guild","totalBudgetRedemptions":500,"issuedRedemptions":120,"remainingRedemptions":380,"maxPerSubject":1,"requiredRules":["verified_email"],"minimumAccountAgeDays":0,"alertThresholdPercent":80,"alertedAt":null,"startsAt":null,"endsAt":null,"pausedAt":null,"pausedBy":null,"createdBy":"user_staff","createdAt":"2026-08-01T00:00:00+00:00"}
        """;

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
        string role, HttpStatusCode billingAnswers = HttpStatusCode.OK, string billingBody = Campaign)
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

    /// <summary>Every read route the service publishes, verbatim.</summary>
    [Test]
    public async Task Every_read_lands_on_the_route_the_service_publishes()
    {
        var (console, billing) = Console("Admin");

        await console.PromotionCampaignsAsync(CancellationToken.None);
        await console.PromotionRulesAsync(CancellationToken.None);
        await console.PromotionCampaignAsync("pro-trial", CancellationToken.None);
        await console.PromotionRedemptionsAsync("pro-trial", 0, CancellationToken.None);
        await console.SubjectRedemptionsAsync("user", "user_1", CancellationToken.None);

        Assert.That(billing.Sent.Select(sent => sent.Path), Is.EqualTo(new[]
        {
            "/api/v1/promotions/campaigns",
            "/api/v1/promotions/rules",
            "/api/v1/promotions/campaigns/pro-trial",
            "/api/v1/promotions/campaigns/pro-trial/redemptions?limit=200",
            "/api/v1/promotions/subjects/User/user_1/redemptions",
        }));

        Assert.That(billing.Sent.Select(sent => sent.Method), Has.All.EqualTo("GET"));
    }

    /// <summary>The service's answer, unaltered.</summary>
    [Test]
    public async Task A_campaign_read_comes_back_whole()
    {
        var (console, _) = Console("Moderator");

        var result = (ContentResult)await console.PromotionCampaignAsync("pro-trial", CancellationToken.None);

        Assert.That(result.StatusCode, Is.EqualTo(200));
        Assert.That(result.Content, Is.EqualTo(Campaign));
        Assert.That(result.ContentType, Is.EqualTo("application/json"));
    }

    /// <summary>An unspecified limit becomes the service's own default rather than being sent as zero,
    /// which the service would read as "no rows".</summary>
    [Test]
    public async Task The_redemption_list_defaults_its_limit_rather_than_sending_zero()
    {
        var (console, billing) = Console("Admin");

        await console.PromotionRedemptionsAsync("pro-trial", 0, CancellationToken.None);
        await console.PromotionRedemptionsAsync("pro-trial", 25, CancellationToken.None);

        Assert.That(billing.Sent[0].Path,
            Is.EqualTo("/api/v1/promotions/campaigns/pro-trial/redemptions?limit=200"));
        Assert.That(billing.Sent[1].Path,
            Is.EqualTo("/api/v1/promotions/campaigns/pro-trial/redemptions?limit=25"));
    }

    /// <summary>A body is forwarded as it was posted rather than rebuilt.</summary>
    [Test]
    public async Task A_create_forwards_the_verb_the_route_and_the_body_unaltered()
    {
        var (console, billing) = Console("Admin", HttpStatusCode.BadRequest,
            """{"code":"campaign_code_taken","message":"There is already a campaign called 'pro-trial'.","failedRules":[]}""");

        const string body =
            """
            {"code":"pro-trial","description":"Thirty days of Pro.","plan":"pro","trialDays":30,"totalBudgetRedemptions":500,"subjectKind":"Guild","requiredRules":["verified_email","registered_device"],"minimumAccountAgeDays":0,"maxPerSubject":1,"alertThresholdPercent":80,"startsAt":null,"endsAt":null}
            """;

        await console.CreatePromotionCampaignAsync(Body(body), CancellationToken.None);

        Assert.That(billing.Sent[0].Method, Is.EqualTo("POST"));
        Assert.That(billing.Sent[0].Path, Is.EqualTo("/api/v1/promotions/campaigns"));
        Assert.That(billing.Sent[0].Body, Is.EqualTo(body));
    }

    /// <summary>The two operations whose whole request is their URL send no body at all.</summary>
    [Test]
    public async Task A_pause_and_a_resume_send_nothing_but_their_route()
    {
        var (console, billing) = Console("Admin", HttpStatusCode.NotFound,
            """{"code":"unknown_campaign","message":"'pro-trial' is not a campaign on this instance.","failedRules":[]}""");

        await console.PausePromotionCampaignAsync("pro-trial", CancellationToken.None);
        await console.ResumePromotionCampaignAsync("pro-trial", CancellationToken.None);

        Assert.That(billing.Sent[0].Path, Is.EqualTo("/api/v1/promotions/campaigns/pro-trial/pause"));
        Assert.That(billing.Sent[0].Body, Is.Null);
        Assert.That(billing.Sent[1].Path, Is.EqualTo("/api/v1/promotions/campaigns/pro-trial/resume"));
        Assert.That(billing.Sent[1].Body, Is.Null);
    }

    [Test]
    public async Task A_budget_change_forwards_a_patch_with_its_body()
    {
        var (console, billing) = Console("Admin", HttpStatusCode.BadRequest,
            """{"code":"campaign_budget_below_issued","message":"Campaign 'pro-trial' has already been redeemed 120 times.","failedRules":[]}""");

        const string body = """{"totalBudgetRedemptions":100}""";

        await console.SetPromotionCampaignBudgetAsync("pro-trial", Body(body), CancellationToken.None);

        Assert.That(billing.Sent[0].Method, Is.EqualTo("PATCH"));
        Assert.That(billing.Sent[0].Path, Is.EqualTo("/api/v1/promotions/campaigns/pro-trial/budget"));
        Assert.That(billing.Sent[0].Body, Is.EqualTo(body));
    }

    /// <summary>A campaign code is a string somebody pasted out of a ticket, not a path segment. One
    /// with a slash in it must address one campaign rather than a route that does not exist.</summary>
    [Test]
    public async Task A_code_is_escaped_into_the_path()
    {
        var (console, billing) = Console("Admin");

        await console.PromotionCampaignAsync("summer/2026 trial", CancellationToken.None);

        Assert.That(billing.Sent[0].Path,
            Is.EqualTo("/api/v1/promotions/campaigns/summer%2F2026%20trial"));
    }

    /// <summary>A subject kind that is not one is refused here, with the kinds this build knows, and
    /// nothing is asked of Billing. The service refuses it too; this exists so a typed URL fails as a
    /// sentence rather than as a round trip.</summary>
    [Test]
    public async Task An_unknown_subject_kind_is_refused_before_anything_is_sent()
    {
        var (console, billing) = Console("Admin");

        var refusal = Refusal(await console.SubjectRedemptionsAsync(
            "household", "hh_1", CancellationToken.None));

        Assert.That(refusal.Status, Is.EqualTo(400));
        Assert.That(refusal.Code, Is.EqualTo("unknown_subject_kind"));
        Assert.That(refusal.Message, Does.Contain("Guild"));
        Assert.That(billing.Calls, Is.Zero);
    }

    // ── The refusal, which is the point of the hop ────────────────────────────────────────────

    /// <summary>Billing's refusal, code and sentence, straight through.</summary>
    [Test]
    public async Task A_refusal_survives_the_hop_with_its_code_and_its_sentence()
    {
        const string refusal =
            """
            {"code":"campaign_budget_exhausted","message":"Campaign 'pro-trial' has run out: all 500 of its redemptions have been taken.","failedRules":[]}
            """;

        var (console, billing) = Console("Admin", HttpStatusCode.BadRequest, refusal);

        var result = (ContentResult)await console.SetPromotionCampaignBudgetAsync(
            "pro-trial", Body("""{"totalBudgetRedemptions":500}"""), CancellationToken.None);

        Assert.That(billing.Calls, Is.EqualTo(1));
        Assert.That(result.StatusCode, Is.EqualTo(400));
        Assert.That(result.Content, Is.EqualTo(refusal), "the body must not be reshaped");
        Assert.That(result.Content, Does.Contain("campaign_budget_exhausted"));
        Assert.That(result.Content, Does.Contain("all 500 of its redemptions have been taken"));
    }

    /// <summary>
    /// The rule refusal keeps the list of rules the service knows, and the <c>failedRules</c> array
    /// alongside it.
    /// </summary>
    [Test]
    public async Task An_unknown_rule_refusal_keeps_the_rules_the_service_knows()
    {
        const string refusal =
            """
            {"code":"campaign_unknown_rule","message":"'verified_phone' is not an eligibility rule. Known rules: verified_email, phone_number_on_file, minimum_account_age, registered_device, no_prior_subscription, payment_card.","failedRules":[]}
            """;

        var (console, _) = Console("Admin", HttpStatusCode.BadRequest, refusal);

        var result = (ContentResult)await console.CreatePromotionCampaignAsync(
            Body("""{"code":"c","description":"d","plan":"pro","trialDays":30,"totalBudgetRedemptions":10,"requiredRules":["verified_phone"]}"""),
            CancellationToken.None);

        Assert.That(result.StatusCode, Is.EqualTo(400));
        Assert.That(result.Content, Is.EqualTo(refusal));
        Assert.That(result.Content, Does.Contain("phone_number_on_file"),
            "the editor recovers from a rejected rule by reading the ones the service does know");
        Assert.That(result.Content, Does.Contain("failedRules"));
    }

    /// <summary>A 404 stays a 404. The service answers one for a campaign that does not exist, and
    /// collapsing that into a 400 would make "you typed the wrong code" look like "your request was
    /// malformed".</summary>
    [Test]
    public async Task A_not_found_from_billing_is_not_turned_into_something_else()
    {
        var (console, _) = Console("Admin", HttpStatusCode.NotFound,
            """{"code":"unknown_campaign","message":"'nope' is not a campaign on this instance.","failedRules":[]}""");

        var result = (ContentResult)await console.PausePromotionCampaignAsync("nope", CancellationToken.None);

        Assert.That(result.StatusCode, Is.EqualTo(404));
        Assert.That(result.Content, Does.Contain("unknown_campaign"));
    }

    /// <summary>A write with no body at all is a client bug, refused as one rather than left to become
    /// a 500 out of <c>GetRawText</c> on an undefined element.</summary>
    [Test]
    public async Task A_bodyless_write_is_refused_before_anything_is_sent()
    {
        var (console, billing) = Console("Admin");

        var create = Refusal(await console.CreatePromotionCampaignAsync(default, CancellationToken.None));
        var budget = Refusal(await console.SetPromotionCampaignBudgetAsync(
            "pro-trial", default, CancellationToken.None));

        Assert.That(create.Status, Is.EqualTo(400));
        Assert.That(create.Code, Is.EqualTo("body_required"));
        Assert.That(budget.Code, Is.EqualTo("body_required"));
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

        var campaigns = Refusal(await console.PromotionCampaignsAsync(CancellationToken.None));
        var rules = Refusal(await console.PromotionRulesAsync(CancellationToken.None));

        var subject = Refusal(await console.SubjectRedemptionsAsync(
            "user", "user_1", CancellationToken.None));

        var write = Refusal(await console.PausePromotionCampaignAsync("pro-trial", CancellationToken.None));

        Assert.That(campaigns.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(campaigns.Code, Is.EqualTo("staff_required"));
        Assert.That(rules.Code, Is.EqualTo("staff_required"));
        Assert.That(subject.Code, Is.EqualTo("staff_required"));
        Assert.That(write.Code, Is.EqualTo("staff_required"));
        Assert.That(billing.Calls, Is.Zero);
    }

    /// <summary>A moderator reads and does not write.</summary>
    [Test]
    public async Task A_moderator_can_read_the_campaigns_and_cannot_change_one()
    {
        var (console, billing) = Console("Moderator");

        var campaigns = (ContentResult)await console.PromotionCampaignsAsync(CancellationToken.None);
        await console.PromotionRulesAsync(CancellationToken.None);
        await console.PromotionCampaignAsync("pro-trial", CancellationToken.None);
        await console.PromotionRedemptionsAsync("pro-trial", 0, CancellationToken.None);
        await console.SubjectRedemptionsAsync("guild", "guild_1", CancellationToken.None);

        var reads = billing.Calls;

        var writes = new[]
        {
            Refusal(await console.CreatePromotionCampaignAsync(
                Body("""{"code":"c","description":"d","plan":"pro","trialDays":30,"totalBudgetRedemptions":10}"""),
                CancellationToken.None)),
            Refusal(await console.PausePromotionCampaignAsync("pro-trial", CancellationToken.None)),
            Refusal(await console.ResumePromotionCampaignAsync("pro-trial", CancellationToken.None)),
            Refusal(await console.SetPromotionCampaignBudgetAsync(
                "pro-trial", Body("""{"totalBudgetRedemptions":10}"""), CancellationToken.None)),
        };

        Assert.That(campaigns.StatusCode, Is.EqualTo(200));
        Assert.That(reads, Is.EqualTo(5));

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

    /// <summary>An instance with no billing service answers the promotion routes the way it answers
    /// every other one: a sentence with a code on it, and nothing sent. The console hides the tab in
    /// selfhost, and this is what it falls back to when something asks anyway.</summary>
    [Test]
    public async Task A_selfhost_instance_answers_the_promotion_routes_without_calling_anything()
    {
        Env.License.Mode = LicenseConfiguration.SelfHost;
        Env.License.BillingServiceUrl = string.Empty;

        var (console, billing) = Console("Admin");

        var campaigns = (ContentResult)await console.PromotionCampaignsAsync(CancellationToken.None);
        var pause = (ContentResult)await console.PausePromotionCampaignAsync("pro-trial", CancellationToken.None);

        Assert.That(billing.Calls, Is.Zero);
        Assert.That(campaigns.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(campaigns.Content, Does.Contain("billing_not_deployed"));
        Assert.That(pause.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
    }
}
