using System.Net.Http;
using Echo.RateLimiter;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Echo.Tests.RateLimiting;

/// <summary>
/// The shared-secret trust mechanism: <c>GATEWAY_PROXY_SECRET</c> on the gateway,
/// <c>X-Echo-Proxy-Auth</c> on the wire.
///
/// <para>It exists because this deployment has no stable proxy address to put in an allowlist -
/// container addresses are reassigned on restart and cloud load balancers rotate - and an allowlist
/// that has drifted fails silently back to one shared bucket. Everything here drives real requests
/// through the built pipeline for the same reason the rest of the suite does.</para>
/// </summary>
[TestFixture]
public class ProxySecretTrustTests
{
    private const string Secret = "s3cr3t-value-from-the-env-file";

    private static int Burst => GatewayRateLimitHarness.AnonymousBurst;

    private static GatewayRateLimitOptions WithSecret(string? raw = Secret) =>
        GatewayRateLimitHarness.Options(proxySecret: ProxySecretOptions.FromEnvironment(raw));

    // ---- the secret is what makes the chain believable ----------------------------------------

    [Test]
    public async Task With_the_secret_each_forwarded_client_gets_its_own_budget()
    {
        // The deployed shape, and the one the CIDR mechanism cannot express: the peer address is
        // whatever the container happens to have today, and is not configured anywhere.
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret());

        var first = await harness.SendManyAsync(Burst + 1, peer: "10.42.7.3", forwardedFor: "198.51.100.40", proxySecret: Secret);
        var second = await harness.SendAsync(peer: "10.42.7.3", forwardedFor: "198.51.100.41", proxySecret: Secret);

        Assert.Multiple(() =>
        {
            Assert.That(first.Take(Burst), Is.All.EqualTo(200));
            Assert.That(first[^1], Is.EqualTo(429));
            Assert.That(second.Response.StatusCode, Is.EqualTo(200), "a rotated container address must not break client identification");
        });
    }

    [Test]
    public async Task The_secret_keeps_working_when_the_peer_address_changes()
    {
        // The whole point: two requests from the same real client arriving via two different
        // container addresses still land in the same bucket.
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret());

        await harness.SendManyAsync(Burst, peer: "10.42.7.3", forwardedFor: "198.51.100.55", proxySecret: Secret);
        var viaAnotherProxyInstance = await harness.SendAsync(peer: "172.19.0.9", forwardedFor: "198.51.100.55", proxySecret: Secret);

        Assert.That(viaAnotherProxyInstance.Response.StatusCode, Is.EqualTo(429));
    }

    [Test]
    public async Task A_wrong_secret_buys_nothing()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret());

        var statuses = new List<int>();
        for (var i = 0; i < Burst + 1; i++)
        {
            var context = await harness.SendAsync(
                peer: "203.0.113.99", forwardedFor: $"198.51.100.{i % 250}", proxySecret: "not-the-secret");
            statuses.Add(context.Response.StatusCode);
        }

        Assert.That(statuses[^1], Is.EqualTo(429), "an attacker with the header but not the value stays on the peer bucket");
    }

    [Test]
    public async Task Two_copies_of_the_header_are_not_believed()
    {
        // A client that adds its own guess alongside the proxy's value. Refusing the multi-valued
        // case means the worst it can achieve is putting itself on the coarse peer bucket - never
        // on a bucket of its own choosing.
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret());

        var statuses = new List<int>();
        for (var i = 0; i < Burst + 1; i++)
        {
            var context = await harness.SendAsync(
                path: GatewayRateLimitHarness.ProxiedPath,
                peer: "203.0.113.120",
                forwardedFor: $"198.51.100.{i % 250}",
                proxySecretValues: ["guess", Secret]);
            statuses.Add(context.Response.StatusCode);
        }

        Assert.That(statuses[^1], Is.EqualTo(429));
    }

    // ---- either mechanism is sufficient -------------------------------------------------------

    [Test]
    public async Task An_allowlisted_peer_is_still_trusted_without_the_secret()
    {
        // The CIDR mechanism is kept as an alternative, not replaced: a deployment that does have
        // stable ranges must keep working after this change.
        var options = new GatewayRateLimitOptions
        {
            TrustedProxies = TrustedProxyOptions.FromEnvironment("203.0.113.77"),
            ProxySecret = ProxySecretOptions.FromEnvironment(Secret),
            ReplenishmentPeriod = GatewayRateLimitHarness.StableRefill
        };
        await using var harness = await GatewayRateLimitHarness.StartAsync(options);

        var exhausted = await harness.SendManyAsync(Burst + 1, peer: "203.0.113.77", forwardedFor: "198.51.100.90");
        var otherClient = await harness.SendAsync(peer: "203.0.113.77", forwardedFor: "198.51.100.91");

        Assert.Multiple(() =>
        {
            Assert.That(exhausted[^1], Is.EqualTo(429));
            Assert.That(otherClient.Response.StatusCode, Is.EqualTo(200));
        });
    }

    // ---- unset / blank ------------------------------------------------------------------------

    [Test]
    public async Task Without_a_configured_secret_the_header_is_ignored_entirely()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var statuses = new List<int>();
        for (var i = 0; i < Burst + 1; i++)
        {
            var context = await harness.SendAsync(
                peer: "203.0.113.77", forwardedFor: $"198.51.100.{i % 250}", proxySecret: "anything at all");
            statuses.Add(context.Response.StatusCode);
        }

        Assert.That(statuses[^1], Is.EqualTo(429), "no secret configured means no forwarded header is believed");
    }

    [Test]
    public async Task A_blank_secret_never_matches_a_blank_header()
    {
        // The failure that would be catastrophic and silent: a variable set to "" or to a stray
        // newline comparing equal to an absent or empty header, and trusting every request.
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret("   \n"));

        var statuses = new List<int>();
        for (var i = 0; i < Burst + 1; i++)
        {
            var context = await harness.SendAsync(
                peer: "203.0.113.77", forwardedFor: $"198.51.100.{i % 250}", proxySecret: "");
            statuses.Add(context.Response.StatusCode);
        }

        Assert.That(statuses[^1], Is.EqualTo(429));
    }

    [Test]
    public async Task A_blank_secret_is_reported_separately_from_an_unset_one()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret("\n"));

        var warnings = harness.Logs.For(GatewayRateLimiting.LoggerCategory)
            .Where(l => l.Level == LogLevel.Warning)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(warnings.Any(l => l.Message.Contains("only whitespace")), Is.True,
                "\"I set it and nothing happened\" is a different mistake from \"I never set it\"");
            Assert.That(warnings.Any(l => l.Message.Contains("single shared bucket")), Is.True,
                "a blank secret leaves the partition collapsed, so the collapse warning must still fire");
        });
    }

    [Test]
    public async Task Startup_reports_the_secret_mechanism_when_it_is_configured()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret());

        var lines = harness.Logs.For(GatewayRateLimiting.LoggerCategory).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(lines.Any(l => l.Level == LogLevel.Information && l.Message.Contains(ProxySecretOptions.HeaderName)), Is.True);
            Assert.That(lines.Any(l => l.Level == LogLevel.Warning), Is.False);
        });
    }

    // ---- the header never leaves the gateway --------------------------------------------------

    [Test]
    public async Task The_header_is_stripped_before_the_request_reaches_anything_downstream()
    {
        // A gateway secret that reaches the eight backend services is a secret any of them can log.
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret());

        var context = await harness.SendAsync(GatewayRateLimitHarness.HeaderEchoPath, proxySecret: Secret);

        Assert.That(context.Response.Headers[GatewayRateLimitHarness.SawProxySecretHeader].ToString(), Is.EqualTo("no"));
    }

    [Test]
    public async Task The_header_is_stripped_even_when_it_did_not_match()
    {
        // Otherwise a wrong guess is forwarded downstream and a backend service logs an attacker's
        // guesses at our secret, which is only marginally better than logging the secret.
        await using var harness = await GatewayRateLimitHarness.StartAsync(WithSecret());

        var context = await harness.SendAsync(GatewayRateLimitHarness.HeaderEchoPath, proxySecret: "wrong");

        Assert.That(context.Response.Headers[GatewayRateLimitHarness.SawProxySecretHeader].ToString(), Is.EqualTo("no"));
    }

    [Test]
    public async Task The_header_is_stripped_when_no_secret_is_configured_at_all()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var context = await harness.SendAsync(GatewayRateLimitHarness.HeaderEchoPath, proxySecret: Secret);

        Assert.That(context.Response.Headers[GatewayRateLimitHarness.SawProxySecretHeader].ToString(), Is.EqualTo("no"));
    }

    [Test]
    public async Task Yarp_removes_the_header_from_every_proxied_request_as_well()
    {
        // Second layer. The middleware above already removed it; this is what still holds if the
        // middleware is moved, or if a route is ever served without going through it.
        var builderContext = new TransformBuilderContext
        {
            Services = new ServiceCollection().BuildServiceProvider(),
            Route = new RouteConfig { RouteId = "guild-route", ClusterId = "guild-cluster" }
        };
        new ProxySecretStrippingTransformProvider().Apply(builderContext);

        using var proxyRequest = new HttpRequestMessage();
        proxyRequest.Headers.TryAddWithoutValidation(ProxySecretOptions.HeaderName, Secret);

        var transformContext = new RequestTransformContext
        {
            HttpContext = new DefaultHttpContext(),
            ProxyRequest = proxyRequest,
            HeadersCopied = true
        };
        foreach (var transform in builderContext.RequestTransforms) await transform.ApplyAsync(transformContext);

        Assert.That(proxyRequest.Headers.Contains(ProxySecretOptions.HeaderName), Is.False);
    }

    // ---- comparison semantics -----------------------------------------------------------------

    [Test]
    public void The_configured_value_and_the_presented_value_are_both_trimmed()
    {
        // A trailing newline in a hand-edited .env is the single likeliest way this gets
        // misconfigured, and it must not silently disable the mechanism.
        var options = ProxySecretOptions.FromEnvironment($"  {Secret}\n");

        Assert.Multiple(() =>
        {
            Assert.That(options.IsConfigured, Is.True);
            Assert.That(options.Matches(Secret), Is.True);
            Assert.That(options.Matches($" {Secret} "), Is.True);
        });
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("   ", false)]
    [TestCase("wrong", false)]
    [TestCase("s3cr3t-value-from-the-env-fil", false)]
    [TestCase("s3cr3t-value-from-the-env-filE", false)]
    [TestCase("S3CR3T-VALUE-FROM-THE-ENV-FILE", false)]
    [TestCase(Secret, true)]
    public void Only_the_exact_secret_matches(string? presented, bool expected)
    {
        Assert.That(ProxySecretOptions.FromEnvironment(Secret).Matches(presented), Is.EqualTo(expected));
    }

    [Test]
    public void An_unset_secret_matches_nothing_including_null_and_empty()
    {
        var unset = ProxySecretOptions.FromEnvironment(null);

        Assert.Multiple(() =>
        {
            Assert.That(unset.IsConfigured, Is.False);
            Assert.That(unset.WasSetButBlank, Is.False);
            Assert.That(unset.Matches((string?)null), Is.False);
            Assert.That(unset.Matches(""), Is.False);
            Assert.That(unset.Matches("anything"), Is.False);
        });
    }

    [Test]
    public void A_whitespace_only_secret_is_treated_as_unset_and_flagged()
    {
        var blank = ProxySecretOptions.FromEnvironment(" \t\r\n ");

        Assert.Multiple(() =>
        {
            Assert.That(blank.IsConfigured, Is.False);
            Assert.That(blank.WasSetButBlank, Is.True);
            Assert.That(blank.Matches(""), Is.False);
            Assert.That(blank.Matches(" "), Is.False);
        });
    }

    [Test]
    public void The_variable_and_header_names_are_the_ones_the_deployment_configures()
    {
        // Spelled out in deploy/.env, both installers, the generated Caddyfile and deploy/README.md.
        // A rename here without a rename there silently disables the mechanism in production.
        Assert.Multiple(() =>
        {
            Assert.That(ProxySecretOptions.EnvironmentVariable, Is.EqualTo("GATEWAY_PROXY_SECRET"));
            Assert.That(ProxySecretOptions.HeaderName, Is.EqualTo("X-Echo-Proxy-Auth"));
        });
    }
}
