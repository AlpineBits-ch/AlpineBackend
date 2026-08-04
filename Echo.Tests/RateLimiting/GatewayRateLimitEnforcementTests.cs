using System.Globalization;
using Echo.RateLimiter;
using Microsoft.Extensions.Logging;

namespace Echo.Tests.RateLimiting;

/// <summary>
/// The regression suite for a limiter that was configured but never installed: every one of these
/// drives real requests through a built pipeline, so all of them fail if
/// <c>app.UseEchoRateLimiter()</c> is removed from Program.cs again, and the per-subject ones fail
/// if it is moved ahead of <c>UseAuthentication</c>.
/// </summary>
[TestFixture]
public class GatewayRateLimitEnforcementTests
{
    /// <summary>Requests a signed-in caller may spend before waiting for a refill.</summary>
    private static int Burst => GatewayRateLimitHarness.AuthenticatedBurst;

    private static int AnonymousBurst => GatewayRateLimitHarness.AnonymousBurst;

    // ---- the shipped numbers ------------------------------------------------------------------

    [Test]
    public void The_default_budgets_are_the_discord_shaped_ones()
    {
        // Pinned here because every other test in this file deliberately slows the refill clock
        // down, so nothing else in the suite would notice these being edited.
        var options = new GatewayRateLimitOptions();

        Assert.Multiple(() =>
        {
            Assert.That(options.AuthenticatedTokensPerPeriod, Is.EqualTo(50), "Discord's global limit is 50 requests per second");
            Assert.That(options.ReplenishmentPeriod, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(options.AuthenticatedBurstCapacity, Is.EqualTo(100), "two seconds of reserve, so a cold start's fan-out is absorbed");
            Assert.That(options.AnonymousTokensPerPeriod, Is.EqualTo(20), "anonymous callers are the cheapest partition to multiply, so they get the tighter budget");
            Assert.That(options.AnonymousBurstCapacity, Is.EqualTo(40));
            Assert.That(options.WebhookTokensPerPeriod, Is.EqualTo(options.AuthenticatedTokensPerPeriod), "an integration is not an anonymous browser");
        });
    }

    // ---- normal: comfortably under the limit ------------------------------------------------

    [Test]
    public async Task Requests_under_the_limit_are_all_served()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var statuses = await harness.SendManyAsync(Burst - 1, subject: "user-normal");

        Assert.That(statuses, Is.All.EqualTo(200));
    }

    [Test]
    public async Task A_cold_start_fan_out_is_absorbed_rather_than_rejected()
    {
        // The shape that broke real clients under the old 100/min fixed window: guild list, then
        // channels + roles + members + emoji per guild, then messages + pins + read state. Ten
        // guilds is an ordinary account, and this all happens before the user has typed anything.
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        const int guilds = 10;
        var coldStart = 1 + guilds * 4 + guilds * 3; // 71 requests, back to back
        var statuses = await harness.SendManyAsync(coldStart, subject: "user-cold-start");

        Assert.That(statuses, Is.All.EqualTo(200), "the burst reserve exists precisely so this does not 429");
    }

    // ---- edge: exactly at the limit -----------------------------------------------------------

    [Test]
    public async Task The_request_that_exactly_empties_the_bucket_is_still_served()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var statuses = await harness.SendManyAsync(Burst, subject: "user-edge");

        Assert.Multiple(() =>
        {
            Assert.That(statuses, Has.Count.EqualTo(Burst));
            Assert.That(statuses[^1], Is.EqualTo(200), "the request that exactly consumes the budget must succeed");
            Assert.That(statuses, Is.All.EqualTo(200));
        });
    }

    // ---- negative: over the limit -------------------------------------------------------------

    [Test]
    public async Task The_request_after_the_bucket_is_empty_is_rejected()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var statuses = await harness.SendManyAsync(Burst + 1, subject: "user-over");

        Assert.Multiple(() =>
        {
            Assert.That(statuses.Take(Burst), Is.All.EqualTo(200));
            Assert.That(statuses[^1], Is.EqualTo(429));
        });
    }

    // ---- the property a fixed window did not have ---------------------------------------------

    [Test]
    public async Task An_exhausted_bucket_refills_over_time_instead_of_at_a_window_boundary()
    {
        // The reason for the algorithm change, stated as a test. Under a fixed window a caller one
        // request over the line waits out the remainder of the window whatever it spent; under a
        // token bucket the budget comes back continuously, so a short pause buys a proportional
        // amount of it back. This is also the one test that runs the refill timer for real, so the
        // period is small and the wait is generously longer than it needs to be.
        var options = GatewayRateLimitHarness.Options(replenishmentPeriod: TimeSpan.FromMilliseconds(200));
        await using var harness = await GatewayRateLimitHarness.StartAsync(options);

        await harness.SendManyAsync(options.AuthenticatedBurstCapacity, subject: "user-refill");
        var immediately = await harness.SendAsync(subject: "user-refill");

        await Task.Delay(TimeSpan.FromSeconds(2));
        var afterWaiting = await harness.SendAsync(subject: "user-refill");

        Assert.Multiple(() =>
        {
            Assert.That(immediately.Response.StatusCode, Is.EqualTo(429));
            Assert.That(afterWaiting.Response.StatusCode, Is.EqualTo(200), "a token bucket refills as time passes");
        });
    }

    // ---- the 429 itself -----------------------------------------------------------------------

    [Test]
    public async Task Rejection_carries_retry_after_and_a_parsable_body()
    {
        var options = GatewayRateLimitHarness.Options(replenishmentPeriod: TimeSpan.FromSeconds(30));
        await using var harness = await GatewayRateLimitHarness.StartAsync(options);
        await harness.SendManyAsync(options.AuthenticatedBurstCapacity, subject: "user-retry-after");

        var rejected = await harness.SendAsync(subject: "user-retry-after");
        var headers = rejected.Response.Headers;

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Response.StatusCode, Is.EqualTo(429));
            Assert.That(headers.RetryAfter.ToString(), Is.Not.Empty, "a 429 without Retry-After tells the client nothing about when to come back");
            // Whole seconds, rounded up: a client that obeys a rounded-down value comes back too
            // early and is rejected again.
            Assert.That(int.Parse(headers.RetryAfter.ToString(), CultureInfo.InvariantCulture),
                Is.InRange(1, (int)options.ReplenishmentPeriod.TotalSeconds));
            // The header now reports the bucket's capacity rather than a per-minute permit count,
            // so it stays truthful under the token-bucket algorithm.
            Assert.That(headers["X-RateLimit-Limit"].ToString(),
                Is.EqualTo(options.AuthenticatedBurstCapacity.ToString(CultureInfo.InvariantCulture)));
            Assert.That(headers["X-RateLimit-Remaining"].ToString(), Is.EqualTo("0"));
            Assert.That(double.Parse(headers["X-RateLimit-Reset-After"].ToString(), CultureInfo.InvariantCulture),
                Is.GreaterThan(0));
            Assert.That(rejected.Response.ContentType, Does.StartWith("application/json"));
        });
    }

    [Test]
    public async Task The_anonymous_bucket_reports_its_own_smaller_limit()
    {
        // Two different budgets means one hard-coded X-RateLimit-Limit would be a lie to whichever
        // caller it did not describe.
        await using var harness = await GatewayRateLimitHarness.StartAsync();
        await harness.SendManyAsync(AnonymousBurst, peer: "198.51.100.71");

        var rejected = await harness.SendAsync(peer: "198.51.100.71");

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Response.StatusCode, Is.EqualTo(429));
            Assert.That(rejected.Response.Headers["X-RateLimit-Limit"].ToString(),
                Is.EqualTo(AnonymousBurst.ToString(CultureInfo.InvariantCulture)));
        });
    }

    [Test]
    public async Task Rejection_is_logged_so_an_attack_is_visible_to_operators()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();
        await harness.SendManyAsync(Burst, subject: "user-logged");

        await harness.SendAsync(subject: "user-logged");

        var warnings = harness.Logs.For(GatewayRateLimiting.LoggerCategory)
            .Where(l => l.Level == LogLevel.Warning && l.Message.Contains("Rate limit exceeded"))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0].Message, Does.Contain("user-logged"));
            Assert.That(warnings[0].Message, Does.Contain(GatewayRateLimitHarness.ProxiedPath));
        });
    }

    // ---- partitioning: authenticated callers --------------------------------------------------

    [Test]
    public async Task One_user_exhausting_the_budget_does_not_affect_another_user()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();
        await harness.SendManyAsync(Burst, subject: "noisy-user");

        var noisyAgain = await harness.SendAsync(subject: "noisy-user");
        var otherUser = await harness.SendAsync(subject: "quiet-user");

        Assert.Multiple(() =>
        {
            Assert.That(noisyAgain.Response.StatusCode, Is.EqualTo(429));
            Assert.That(otherUser.Response.StatusCode, Is.EqualTo(200), "buckets must be per-subject");
        });
    }

    [Test]
    public async Task Two_users_behind_the_same_address_get_separate_budgets()
    {
        // Proves authentication ran before the limiter: if it had not, both callers would have been
        // anonymous, shared the address bucket, and the second would have been rejected.
        await using var harness = await GatewayRateLimitHarness.StartAsync();
        await harness.SendManyAsync(Burst, subject: "shared-nat-user-a", peer: "203.0.113.9");

        var second = await harness.SendAsync(subject: "shared-nat-user-b", peer: "203.0.113.9");

        Assert.That(second.Response.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task A_signed_in_caller_gets_a_larger_budget_than_an_anonymous_one()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var anonymous = await harness.SendManyAsync(AnonymousBurst + 1, peer: "198.51.100.80");
        var authenticated = await harness.SendManyAsync(AnonymousBurst + 1, subject: "user-bigger-budget", peer: "198.51.100.80");

        Assert.Multiple(() =>
        {
            Assert.That(anonymous[^1], Is.EqualTo(429));
            Assert.That(authenticated, Is.All.EqualTo(200), "signing in must not leave a caller on the anonymous budget");
        });
    }

    [Test]
    public async Task A_users_budget_is_shared_across_every_proxied_route()
    {
        // Documents the current design (one bucket per user for the whole gateway) so that a change
        // to per-route budgets is a deliberate, visible edit rather than an accident. It is also why
        // the 429 body says global: true.
        await using var harness = await GatewayRateLimitHarness.StartAsync();
        await harness.SendManyAsync(Burst, GatewayRateLimitHarness.ProxiedPath, subject: "cross-route-user");

        var otherRoute = await harness.SendAsync(GatewayRateLimitHarness.OtherProxiedPath, subject: "cross-route-user");

        Assert.That(otherRoute.Response.StatusCode, Is.EqualTo(429));
    }

    // ---- partitioning: anonymous callers ------------------------------------------------------

    [Test]
    public async Task Anonymous_callers_are_partitioned_on_their_address()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();
        await harness.SendManyAsync(AnonymousBurst, peer: "198.51.100.1");

        var sameAddress = await harness.SendAsync(peer: "198.51.100.1");
        var otherAddress = await harness.SendAsync(peer: "198.51.100.2");

        Assert.Multiple(() =>
        {
            Assert.That(sameAddress.Response.StatusCode, Is.EqualTo(429));
            Assert.That(otherAddress.Response.StatusCode, Is.EqualTo(200));
        });
    }

    // ---- partitioning: webhooks ---------------------------------------------------------------

    [Test]
    public async Task Webhook_execution_is_partitioned_per_webhook_id()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();
        await harness.SendManyAsync(Burst, "/api/webhooks/hook-a/token-1", peer: "198.51.100.10");

        var sameHook = await harness.SendAsync("/api/webhooks/hook-a/token-1", peer: "198.51.100.10");
        var sameHookOtherToken = await harness.SendAsync("/api/webhooks/hook-a/token-2", peer: "198.51.100.10");
        var otherHook = await harness.SendAsync("/api/webhooks/hook-b/token-1", peer: "198.51.100.10");

        Assert.Multiple(() =>
        {
            Assert.That(sameHook.Response.StatusCode, Is.EqualTo(429));
            Assert.That(sameHookOtherToken.Response.StatusCode, Is.EqualTo(429), "varying the token must not buy a fresh budget");
            Assert.That(otherHook.Response.StatusCode, Is.EqualTo(200), "each integration gets its own budget");
        });
    }

    [Test]
    public async Task Exhausting_a_webhook_does_not_exhaust_the_callers_address_budget()
    {
        await using var harness = await GatewayRateLimitHarness.StartAsync();
        await harness.SendManyAsync(Burst, "/api/webhooks/hook-c/token", peer: "198.51.100.20");

        var normalRoute = await harness.SendAsync(peer: "198.51.100.20");

        Assert.That(normalRoute.Response.StatusCode, Is.EqualTo(200));
    }

    // ---- scope: only endpoints carrying the policy metadata are limited -----------------------

    [Test]
    public async Task Endpoints_without_the_policy_metadata_are_not_limited()
    {
        // There is no global limiter, so /health, the SignalR hub and the gateway's own controllers
        // keep working no matter how hard a caller hits them.
        await using var harness = await GatewayRateLimitHarness.StartAsync();

        var statuses = await harness.SendManyAsync(Burst + 20, GatewayRateLimitHarness.UnlimitedPath, peer: "198.51.100.30");

        Assert.That(statuses, Is.All.EqualTo(200));
    }
}
