using Isle.Contracts.Commands;
using IsleBridge.Sdk;

namespace Isle.Api.Services;

using Microsoft.Extensions.Hosting;
using Wolverine;

public sealed class PositionIngestionService(
    IStatsStream statsStream,
    VoicePlayerRegistry registry,
    IServiceScopeFactory scopeFactory,
    ILogger<PositionIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var snapshot in statsStream.StreamAsync(stoppingToken))
                {
                    // Not opted into voice — ignore. Covers players without a
                    // linked steamId/userId, and players who never pressed join.
                    if (!registry.TryGetPlayerId(snapshot.Steam, out var playerId))
                        continue;

                    if (snapshot.Pos is null)
                        continue;

                    using var scope = scopeFactory.CreateScope();
                    var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

                    await bus.InvokeAsync(new UpdatePlayerPositionCommand(
                        playerId,
                        (float)snapshot.Pos.X,
                        (float)snapshot.Pos.Y,
                        (float)snapshot.Pos.Z), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Stats stream dropped, reconnecting in 2s");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}