using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Echo.RateLimiter;


public class RateLimitConfigFilter : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
        => ValueTask.FromResult(cluster);

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel)
    {
        var updated = route with { RateLimiterPolicy = "PerUserPolicy" };
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
