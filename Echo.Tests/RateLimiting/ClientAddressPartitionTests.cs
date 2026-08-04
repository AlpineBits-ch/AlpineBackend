using System.Net;
using Echo.RateLimiter;
using Microsoft.Extensions.Logging;

namespace Echo.Tests.RateLimiting;

/// <summary>
/// The anonymous partition key is the thing that decides whether switching the limiter on protects
/// the instance or takes it down. Behind nginx/Caddy every inbound connection has the same peer
/// address, so without a trust configuration the whole world shares one bucket; and with a naively
/// trusted X-Forwarded-For an attacker mints a fresh bucket per request. These pin both ends of
/// that for the address-allowlist mechanism - see <see cref="ProxySecretTrustTests"/> for the
/// recommended one.
/// </summary>
[TestFixture]
public class ClientAddressPartitionTests
{
    private static int Burst => GatewayRateLimitHarness.AnonymousBurst;

    private static GatewayRateLimitOptions Trusting(string entries) =>
        GatewayRateLimitHarness.Options(trustedProxies: TrustedProxyOptions.FromEnvironment(entries));

    // ---- unconfigured: the header is ignored --------------------------------------------------

    [Test]
    public async Task Without_a_trust_configuration_a_forged_forwarded_header_buys_nothing()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var statuses = new List<int>();
        for (var i = 0; i < Burst + 1; i++)
        {
            var context = await harness.SendAsync(peer: "203.0.113.77", forwardedFor: $"10.1.2.{i % 250}");
            statuses.Add(context.Response.StatusCode);
        }

        Assert.Multiple(() =>
        {
            Assert.That(statuses.Take(Burst), Is.All.EqualTo(200));
            Assert.That(statuses[^1], Is.EqualTo(429), "spoofing X-Forwarded-For must not reset the budget");
        });
    }

    [Test]
    public async Task Without_a_trust_configuration_startup_warns_that_anonymous_callers_share_a_bucket()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var warnings = harness.Logs.For(GatewayRateLimiting.LoggerCategory)
            .Where(l => l.Level == LogLevel.Warning)
            .ToList();

        Assert.Multiple(() =>
        {
            // Both mechanisms are named, because the operator has to be told which one to reach for
            // and the recommended one is not the one that used to be here.
            Assert.That(warnings.Any(l => l.Message.Contains(ProxySecretOptions.EnvironmentVariable)), Is.True,
                "an operator putting this behind a proxy must be told the anonymous partition has collapsed, and how to fix it");
            Assert.That(warnings.Any(l => l.Message.Contains(TrustedProxyOptions.EnvironmentVariable)), Is.True);
        });
    }

    // ---- configured: the header is believed, from trusted hops only ---------------------------

    [Test]
    public async Task Behind_a_trusted_proxy_each_forwarded_client_gets_its_own_budget()
    {
        // The deployed shape: one proxy address, many real clients. Without this the second client
        // would already be rejected, because the peer address is identical for both.
        await using var harness = await GatewayRateLimitHarness.StartAsync(Trusting("203.0.113.77"));

        var first = await harness.SendManyAsync(Burst + 1, peer: "203.0.113.77", forwardedFor: "198.51.100.40");
        var second = await harness.SendAsync(peer: "203.0.113.77", forwardedFor: "198.51.100.41");

        Assert.Multiple(() =>
        {
            Assert.That(first.Take(Burst), Is.All.EqualTo(200));
            Assert.That(first[^1], Is.EqualTo(429));
            Assert.That(second.Response.StatusCode, Is.EqualTo(200));
        });
    }

    [Test]
    public async Task An_untrusted_peer_is_charged_to_itself_even_when_it_claims_otherwise()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync(Trusting("203.0.113.77"));

        var statuses = new List<int>();
        for (var i = 0; i < Burst + 1; i++)
        {
            var context = await harness.SendAsync(peer: "203.0.113.99", forwardedFor: $"198.51.100.{i % 250}");
            statuses.Add(context.Response.StatusCode);
        }

        Assert.That(statuses[^1], Is.EqualTo(429), "only a vouched-for proxy may speak for somebody else");
    }

    [Test]
    public async Task A_client_supplied_prefix_of_the_forwarded_chain_is_not_believed()
    {
        // The proxy appends the real client to whatever the client already sent, so the left-hand
        // entries are attacker input. Only the right-hand end - what our own proxy wrote - counts.
        await using var harness = await GatewayRateLimitHarness.StartAsync(Trusting("203.0.113.77"));

        var statuses = new List<int>();
        for (var i = 0; i < Burst + 1; i++)
        {
            var context = await harness.SendAsync(peer: "203.0.113.77", forwardedFor: $"10.9.9.{i % 250}, 198.51.100.50");
            statuses.Add(context.Response.StatusCode);
        }

        Assert.Multiple(() =>
        {
            Assert.That(statuses.Take(Burst), Is.All.EqualTo(200));
            Assert.That(statuses[^1], Is.EqualTo(429));
        });
    }

    [Test]
    public async Task Trusted_hops_are_skipped_to_find_the_real_client()
    {
        // Two of our own proxies in front of the gateway (edge -> internal -> Echo).
        await using var harness = await GatewayRateLimitHarness.StartAsync(Trusting("203.0.113.77,10.0.0.0/8"));

        var exhausted = await harness.SendManyAsync(Burst + 1, peer: "203.0.113.77", forwardedFor: "198.51.100.60, 10.0.0.7");
        var otherClient = await harness.SendAsync(peer: "203.0.113.77", forwardedFor: "198.51.100.61, 10.0.0.7");

        Assert.Multiple(() =>
        {
            Assert.That(exhausted[^1], Is.EqualTo(429));
            Assert.That(otherClient.Response.StatusCode, Is.EqualTo(200));
        });
    }

    [Test]
    public async Task Startup_reports_the_trusted_proxy_configuration_when_present()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync(Trusting("203.0.113.77"));

        var lines = harness.Logs.For(GatewayRateLimiting.LoggerCategory).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(lines.Any(l => l.Level == LogLevel.Information && l.Message.Contains("trusted proxy")), Is.True);
            Assert.That(lines.Any(l => l.Level == LogLevel.Warning), Is.False,
                "a configured gateway must not emit the collapsed-partition warning");
            // Still pointed at the mechanism that survives an address change.
            Assert.That(lines.Any(l => l.Message.Contains(ProxySecretOptions.EnvironmentVariable)), Is.True);
        });
    }

    // ---- parsing ------------------------------------------------------------------------------

    [Test]
    public void Trusted_proxy_parsing_accepts_addresses_and_cidr_and_skips_junk()
    {
        var options = TrustedProxyOptions.FromEnvironment("127.0.0.1, ::1; 172.16.0.0/12  not-an-address 999.1.1.1/8");

        Assert.Multiple(() =>
        {
            Assert.That(options.HasTrustedProxies, Is.True);
            Assert.That(options.IsTrustedProxy(IPAddress.Parse("127.0.0.1")), Is.True);
            Assert.That(options.IsTrustedProxy(IPAddress.Parse("::1")), Is.True);
            Assert.That(options.IsTrustedProxy(IPAddress.Parse("172.20.3.4")), Is.True);
            Assert.That(options.IsTrustedProxy(IPAddress.Parse("8.8.8.8")), Is.False);
        });
    }

    [Test]
    public void An_ipv4_mapped_ipv6_peer_matches_its_ipv4_trust_entry()
    {
        // Kestrel reports loopback as ::ffff:127.0.0.1 on a dual-stack socket, which is exactly the
        // shape a same-host nginx deployment produces.
        var options = TrustedProxyOptions.FromEnvironment("127.0.0.1");

        Assert.That(options.IsTrustedProxy(IPAddress.Parse("::ffff:127.0.0.1")), Is.True);
    }

    [Test]
    public void Nothing_configured_means_nothing_trusted()
    {
        var options = TrustedProxyOptions.FromEnvironment("   ");

        Assert.Multiple(() =>
        {
            Assert.That(options.HasTrustedProxies, Is.False);
            Assert.That(options.IsTrustedProxy(IPAddress.Loopback), Is.False);
        });
    }
}
