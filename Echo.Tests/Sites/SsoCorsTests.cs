using Echo.Proxy;
using Echo.Sites;

namespace Echo.Tests.Sites;

/// <summary>
/// The SSO CORS policy is open to any origin, so what it is attached to is the whole control.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SsoCorsTests
{
    private static readonly string[] ProtocolRoutes =
    [
        "identity-connect-route",
        "identity-openid-config-route",
        "identity-jwks-route",
    ];

    /// <summary>
    /// Without this a partner site's token exchange is blocked by the browser, and it fails with
    /// nothing in any server log - the request itself succeeded.
    /// </summary>
    [Test]
    public void The_oidc_endpoints_allow_cross_origin_calls()
    {
        var routes = ProxyConfig.GetRoutes()
            .Where(route => ProtocolRoutes.Contains(route.RouteId))
            .ToList();

        Assert.That(routes, Has.Count.EqualTo(ProtocolRoutes.Length), "a protocol route was renamed");
        Assert.That(routes.Select(route => route.CorsPolicy), Is.All.EqualTo(SsoCors.PolicyName));
    }

    /// <summary>The API carries sessions and keeps the credentialed allowlist.</summary>
    [Test]
    public void No_api_route_uses_the_open_policy()
    {
        var offenders = ProxyConfig.GetRoutes()
            .Where(route => route.CorsPolicy == SsoCors.PolicyName)
            .Where(route => route.Match.Path?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .Select(route => route.RouteId)
            .ToList();

        Assert.That(offenders, Is.Empty);
    }
}
