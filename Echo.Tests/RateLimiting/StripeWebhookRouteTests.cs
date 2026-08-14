using AppEnvironment;
using Echo.Proxy;
using Echo.RateLimiter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Configuration;

namespace Echo.Tests.RateLimiting;

/// <summary>
/// The gateway's half of the Stripe webhook: a route of its own, exempt from the limiter, and
/// preferred over the billing catch-all it sits inside.
/// </summary>
[TestFixture]
public class StripeWebhookRouteTests
{
    private const string WebhookPath = "/api/v1/billing/stripe/webhook";

    private const string CatchAllPattern = "/api/v1/billing/{**catch-all}";

    private string _originalMode = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _originalMode = Env.License.Mode;

        // Both billing routes are dropped in selfhost, which is the default.
        Env.License.Mode = LicenseConfiguration.Hosted;
    }

    [TearDown]
    public void TearDown() => Env.License.Mode = _originalMode;

    // ── Normal ───────────────────────────────────────────────────────────────

    /// <summary>The empirical half.</summary>
    [Test]
    public async Task The_router_prefers_the_webhook_route_over_the_billing_catch_all()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        await using var app = builder.Build();

        app.MapPost(CatchAllPattern, () => Results.Text("catch-all"));
        app.MapPost(WebhookPath, () => Results.Text("webhook"));

        await app.StartAsync();

        var matched = await app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = WebhookPath;
        });

        var other = await app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/v1/billing/subscriptions";
        });

        Assert.Multiple(() =>
        {
            Assert.That(matched.GetEndpoint()?.DisplayName, Does.Contain("stripe/webhook"),
                "the literal path has to win, or the webhook is rate limited after all");

            Assert.That(other.GetEndpoint()?.DisplayName, Does.Contain("catch-all"),
                "and the rest of billing still goes through the catch-all");
        });

        await app.StopAsync();
    }

    [Test]
    public void The_webhook_route_is_exempt_from_the_limiter()
    {
        var webhook = Webhook();

        Assert.Multiple(() =>
        {
            Assert.That(webhook, Is.Not.Null, "a hosted gateway must route the webhook somewhere");
            Assert.That(webhook!.Metadata?[RateLimitConfigFilter.ExemptMetadataKey], Is.EqualTo("true"));
            Assert.That(webhook.Match.Path, Is.EqualTo(WebhookPath));
        });
    }

    /// <summary>The exemption goes onto this path and nothing else.</summary>
    [Test]
    public void No_other_billing_route_is_exempt()
    {
        var exempt = ProxyConfig.GetRoutes()
            .Where(route => route.Metadata?.ContainsKey(RateLimitConfigFilter.ExemptMetadataKey) == true)
            .Select(route => route.RouteId)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(exempt, Does.Contain("billing-stripe-webhook-route"));
            Assert.That(exempt, Does.Not.Contain("billing-route"));

            // The link preview media proxy was the only exemption before this one, and is the only
            // other route that should ever be on this list.
            Assert.That(exempt, Is.EquivalentTo(new[] { "previews-route", "billing-stripe-webhook-route" }));
        });
    }

    /// <summary>
    /// The filter is what actually stamps the policy, so the exemption is worth exercising through it
    /// rather than only reading the metadata back.
    /// </summary>
    [Test]
    public async Task The_filter_leaves_the_webhook_route_unlimited_and_limits_the_rest()
    {
        var filter = new RateLimitConfigFilter();

        var webhook = await filter.ConfigureRouteAsync(Webhook()!, cluster: null, CancellationToken.None);
        var billing = await filter.ConfigureRouteAsync(Billing()!, cluster: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(webhook.RateLimiterPolicy, Is.Null);
            Assert.That(billing.RateLimiterPolicy, Is.EqualTo(GatewayRateLimiting.PolicyName));
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    /// <summary>Both billing routes share the billing cluster, and a route whose cluster is missing
    /// is a startup failure in YARP's config validation rather than a 404.</summary>
    [Test]
    public void The_webhook_route_points_at_a_cluster_that_exists()
    {
        var clusters = ProxyConfig.GetClusters().Select(cluster => cluster.ClusterId).ToList();

        Assert.That(clusters, Does.Contain(Webhook()!.ClusterId));
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>Selfhost is the default and is what nearly every deployment is.</summary>
    [Test]
    public void Neither_billing_route_exists_in_selfhost()
    {
        Env.License.Mode = LicenseConfiguration.SelfHost;

        var ids = ProxyConfig.GetRoutes().Select(route => route.RouteId).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Not.Contain("billing-route"));
            Assert.That(ids, Does.Not.Contain("billing-stripe-webhook-route"));
        });
    }

    private static RouteConfig? Webhook() =>
        ProxyConfig.GetRoutes().FirstOrDefault(route => route.RouteId == "billing-stripe-webhook-route");

    private static RouteConfig? Billing() =>
        ProxyConfig.GetRoutes().FirstOrDefault(route => route.RouteId == "billing-route");
}
