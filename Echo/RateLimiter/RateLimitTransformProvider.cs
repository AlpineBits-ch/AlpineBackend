using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Echo.RateLimiter;


public class RateLimitConfigFilter : IProxyConfigFilter
{
    /// <summary>
    /// Metadata key a route sets to opt out of <see cref="GatewayRateLimiting.PolicyName"/>.
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
