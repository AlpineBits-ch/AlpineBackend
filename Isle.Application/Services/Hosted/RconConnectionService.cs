using Isle.Api.Services.Rcon;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Keeps the RCON socket warm. Previously the connection was made inline in <c>Program.cs</c> with a
/// blocking <c>await</c> before the container was even built: an unreachable game server meant the
/// whole API failed to start, and a socket that died later stayed dead until the next deploy.
///
/// <para>Connecting is now the gateway's job and happens on demand; this service just nudges it on a
/// slow interval so the first real command doesn't pay the connect cost, and so a server that comes
/// back after an outage is picked up without traffic.</para>
/// </summary>
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
