using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Echo.RateLimiter;


public class RateLimitConfigFilter : IProxyConfigFilter
{
    /// <summary>
    /// Metadata key a route sets to opt out of <see cref="GatewayRateLimiting.PolicyName"/>.
    ///
    /// <para>Exists for static, cacheable, unauthenticated asset routes - today just the link
    /// preview media proxy. The per-user policy partitions anonymous callers by IP, and an
    /// <c>&lt;img&gt;</c> tag carries no bearer token, so a viewer scrolling a channel full of link
    /// previews arrives as one anonymous IP making a burst of requests and spends their whole
    /// anonymous budget on pictures - starving the API calls that budget is actually there to
    /// protect. These responses are immutable and served with a week-long <c>Cache-Control</c>, so
    /// the browser makes each request once.</para>
    ///
    /// <para>Opting a route out is a deliberate, reviewable act: it must be spelled here <b>and</b>
    /// in the route definition, and it must never be applied to anything that reads user data.</para>
    /// </summary>
    public const string ExemptMetadataKey = "RateLimitExempt";

    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
        => ValueTask.FromResult(cluster);

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel)
    {
        if (route.Metadata?.TryGetValue(ExemptMetadataKey, out var exempt) == true
            && string.Equals(exempt, "true", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(route);
        }

        var updated = route with { RateLimiterPolicy = GatewayRateLimiting.PolicyName };
        return ValueTask.FromResult(updated);
    }
}

/// <summary>
/// Removes <see cref="ProxySecretOptions.HeaderName"/> from every request YARP forwards.
///
/// <para>The gateway pipeline already strips it (see
/// <see cref="GatewayRateLimiting.UseEchoRateLimiter"/>), so in the normal case this transform has
/// nothing left to remove. It exists because the consequence of the strip being missed is
/// disproportionate to the cost of doing it twice: the header would then reach all eight backend
/// services, any of which may log inbound headers or include them in an error report, turning one
/// gateway secret into a secret that leaks from anywhere in the deployment. Applied unconditionally
/// to every route, including the ones that do not go through the limiter middleware's path.</para>
/// </summary>
public sealed class ProxySecretStrippingTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context)
    {
    }

    public void ValidateCluster(TransformClusterValidationContext context)
    {
    }

    public void Apply(TransformBuilderContext context)
    {
        context.AddRequestHeaderRemove(ProxySecretOptions.HeaderName);
    }
}
