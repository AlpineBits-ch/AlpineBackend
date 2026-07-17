using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AppEnvironment;

public static class GracefulShutdownHealthCheckExtensions
{
    /// <summary>
    /// Registers a "self" health check that reports Healthy until the app receives a shutdown
    /// signal (SIGTERM), at which point it immediately reports Unhealthy.
    /// </summary>
    public static IServiceCollection AddGracefulShutdownHealthCheck(
        this IServiceCollection services,
        string name = "self")
    {
        services.AddSingleton<ShutdownState>();

        services.AddHealthChecks()
            .AddCheck<GracefulShutdownHealthCheck>(name);

        return services;
    }

    /// <summary>
    /// Hooks ApplicationStopping so the health check starts failing immediately on shutdown signal.
    /// </summary>
    public static WebApplication UseGracefulShutdownHealthCheck(this WebApplication app)
    {
        var state = app.Services.GetRequiredService<ShutdownState>();
        app.Lifetime.ApplicationStopping.Register(() => state.IsShuttingDown = true);
        return app;
    }
}

internal sealed class ShutdownState
{
    public volatile bool IsShuttingDown;
}

internal sealed class GracefulShutdownHealthCheck : IHealthCheck
{
    private readonly ShutdownState _state;

    public GracefulShutdownHealthCheck(ShutdownState state) => _state = state;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_state.IsShuttingDown
            ? HealthCheckResult.Unhealthy("Shutting down")
            : HealthCheckResult.Healthy());
    }
}