using Federation.Application.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Federation.Application.Services;

/// <summary>
/// Periodically retries outbound federation events that previously failed to deliver (see the
/// reliability fix in VentaFederationProvider.PostSignedEventAsync - the POST result used to be
/// ignored entirely, silently dropping events forever). IFederationProvider/FederationDagService
/// are scoped (EF-backed), so this resolves a fresh scope each tick rather than injecting them
/// directly, matching VoiceHeartbeatCleanupService's pattern in Guild.Application.
/// </summary>
public class FederationOutboundRetryService(
    IServiceScopeFactory scopeFactory,
    ILogger<FederationOutboundRetryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var provider = scope.ServiceProvider.GetRequiredService<IFederationProvider>();
                await provider.RetryUndeliveredEventsAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Federation outbound retry sweep failed");
            }
        }
    }
}
