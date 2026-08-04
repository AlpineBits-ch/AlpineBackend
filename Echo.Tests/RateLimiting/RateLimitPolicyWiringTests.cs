using System.Security.Claims;
using Echo.RateLimiter;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Configuration;

namespace Echo.Tests.RateLimiting;

/// <summary>
/// Ties the pieces the enforcement tests cannot see together: the policy name YARP stamps onto
/// every proxied route has to be the one the gateway registers, and the partition key has to be
/// derived from something stable.
/// </summary>
[TestFixture]
public class RateLimitPolicyWiringTests
{
    [Test]
    public async Task Every_proxied_route_is_stamped_with_the_registered_policy_name()
    {
        var route = new RouteConfig { RouteId = "guild-route", ClusterId = "guild-cluster" };

        var stamped = await new RateLimitConfigFilter().ConfigureRouteAsync(route, cluster: null, CancellationToken.None);

        Assert.That(stamped.RateLimiterPolicy, Is.EqualTo(GatewayRateLimiting.PolicyName),
            "a route stamped with a policy name nobody registered is silently unlimited");
    }

    [Test]
    public void Authenticated_callers_partition_on_the_immutable_user_id()
    {
        // The Identity service issues sub (the user id), which JwtBearer maps inbound to
        // NameIdentifier, alongside a name claim carrying the username. The username is renameable,
        // so a partition keyed on it is a partition the caller can reset on demand.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "0198-user-id"),
            new Claim(ClaimTypes.Name, "renameable-username")
        ], "Bearer"));

        Assert.That(GatewayRateLimiting.ResolveSubject(principal), Is.EqualTo("0198-user-id"));
    }

    [Test]
    public void A_token_carrying_only_the_raw_sub_claim_still_resolves()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "0198-user-id")], "Bearer"));

        Assert.That(GatewayRateLimiting.ResolveSubject(principal), Is.EqualTo("0198-user-id"));
    }

    [Test]
    public void An_unauthenticated_principal_has_no_subject()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GatewayRateLimiting.ResolveSubject(null), Is.Null);
            Assert.That(GatewayRateLimiting.ResolveSubject(new ClaimsPrincipal(new ClaimsIdentity())), Is.Null);
            // An identity with claims but no authentication type is not authenticated, and must not
            // be able to claim a user partition.
            Assert.That(GatewayRateLimiting.ResolveSubject(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "spoofed")]))), Is.Null);
        });
    }

    [TestCase("/api/webhooks/abc/token", true, "abc")]
    [TestCase("/API/WEBHOOKS/abc/token", true, "abc")]
    [TestCase("/api/webhooks/abc/token/extra", true, "abc")]
    [TestCase("/api/webhooks/abc", false, "")]
    [TestCase("/api/webhooks//token", false, "")]
    [TestCase("/api/v1/guild/channels", false, "")]
    [TestCase("", false, "")]
    public void Webhook_ids_are_matched_by_path_shape(string path, bool expected, string expectedId)
    {
        var matched = GatewayRateLimiting.TryGetWebhookId(new PathString(string.IsNullOrEmpty(path) ? null : path), out var id);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.EqualTo(expected));
            Assert.That(id, Is.EqualTo(expectedId));
        });
    }
}
