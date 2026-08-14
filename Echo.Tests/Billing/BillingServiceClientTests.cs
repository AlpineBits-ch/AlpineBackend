using System.Net;
using AppEnvironment;
using Echo.Billing;
using Echo.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Echo.Tests.Billing;

/// <summary>The console's channel to the Billing service.</summary>
[TestFixture]
[Category("Unit")]
[NonParallelizable]
public class BillingServiceClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to host");
    }

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

    private static BillingServiceClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://billing.test") },
            new RecordingLogger<BillingServiceClient>());

    private static HttpContext Caller(string? token = "Bearer console-token")
    {
        var context = new DefaultHttpContext();
        if (token is not null) context.Request.Headers.Authorization = token;

        return context;
    }

    /// <summary>The whole reason this forwards rather than mints a service token: Billing resolves
    /// the caller's tier from Identity per request, and it can only do that if it knows who is
    /// asking.</summary>
    [Test]
    public async Task The_callers_own_token_is_forwarded()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        await Client(handler).GetAsync(Caller(), "/api/v1/plans", CancellationToken.None);

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0].Headers.Authorization?.ToString(), Is.EqualTo("Bearer console-token"));
    }

    /// <summary>The refusal reaches the console intact.</summary>
    [Test]
    public async Task A_refusal_body_passes_through_verbatim()
    {
        const string refusal =
            """
            {"code":"unknown_entitlement_key","message":"'guild.emoij_slots' is not an entitlement key. Known keys: voice.max_participants, guild.emoji_slots."}
            """;

        var reply = await Client(new StubHandler(HttpStatusCode.BadRequest, refusal))
            .SendAsync(Caller(), HttpMethod.Post, "/api/v1/plans", "{}", CancellationToken.None);

        Assert.That(reply.Status, Is.EqualTo(400));
        Assert.That(reply.IsSuccess, Is.False);
        Assert.That(reply.Body, Is.EqualTo(refusal));
        Assert.That(reply.Body, Does.Contain("guild.emoij_slots"));
    }

    /// <summary>A service that is not there is a 503 with a code, never an exception.</summary>
    [Test]
    public async Task An_unreachable_service_answers_503_with_a_code()
    {
        var reply = await Client(new DeadHandler())
            .GetAsync(Caller(), "/api/v1/plans", CancellationToken.None);

        Assert.That(reply.Status, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(reply.Body, Does.Contain("billing_unreachable"));
    }

    /// <summary>Selfhost is the default and has no billing container.</summary>
    [Test]
    public async Task A_selfhost_instance_answers_without_calling_anything()
    {
        Env.License.Mode = LicenseConfiguration.SelfHost;
        Env.License.BillingServiceUrl = string.Empty;

        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var reply = await Client(handler).GetAsync(Caller(), "/api/v1/plans", CancellationToken.None);

        Assert.That(handler.Requests, Is.Empty);
        Assert.That(reply.Status, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(reply.Body, Does.Contain("billing_not_deployed"));
    }
}
