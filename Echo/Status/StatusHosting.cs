using System.Globalization;
using System.Threading.RateLimiting;
using Echo.RateLimiter;
using Microsoft.AspNetCore.RateLimiting;
using Yarp.ReverseProxy;

namespace Echo.Status;

/// <summary>Cross-origin access for the status endpoints, and only for them.</summary>
public static class StatusCors
{
    public const string PolicyName = "StatusPolicy";

    public static IServiceCollection AddStatusCors(this IServiceCollection services) =>
        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .WithMethods("GET", "HEAD", "OPTIONS")));
}

/// <summary>A budget of its own for the status endpoints.</summary>
public static class StatusRateLimiting
{
    public const string PolicyName = "StatusPollPolicy";

    public const int RequestsPerMinute = 120;

    public static IServiceCollection AddStatusRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter => limiter.AddPolicy(PolicyName, context =>
        {
            var options = context.RequestServices.GetService<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();
            var address = ClientIpResolver.Resolve(context, options)?.ToString() ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter($"status:{address}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = RequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }));

        return services;
    }
}

/// <summary>Registration for everything the status feature needs.</summary>
public static class StatusServices
{
    public static IServiceCollection AddVentaStatus(this IServiceCollection services)
    {
        services.AddSingleton(StatusOptions.FromEnvironment());
        services.AddSingleton<StatusMetrics>();
        services.AddSingleton<StatusSnapshot>();
        services.AddSingleton<StatusSignalBoard>();
        services.AddHostedService<StatusProbe>();

        services.AddStatusCors();
        services.AddStatusRateLimiting();

        return services;
    }

    /// <summary>The cluster ids YARP actually knows about, for the startup drift warning and the
    /// architecture test.</summary>
    public static IReadOnlyCollection<string> ProxyClusters(IProxyStateLookup lookup) =>
        StatusSeeder.ClusterIds(lookup);

    public static string Percent(double rate) =>
        (rate * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
