using Isle.Api.Services.Rcon;

namespace Isle.Api.Services.Hosted;

/// <summary>Keeps the RCON socket warm.</summary>
public sealed class RconConnectionService(IRconGateway rcon, ILogger<RconConnectionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await rcon.EnsureConnectedAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RCON keepalive tick failed");
            }

            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
