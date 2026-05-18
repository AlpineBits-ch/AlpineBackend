using Yarp.ReverseProxy.Configuration;
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