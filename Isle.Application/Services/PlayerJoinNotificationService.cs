using Echo.Realtime;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services;

public sealed class PlayerJoinNotificationService(
    IEventStream eventStream,
    IServiceScopeFactory scopeFactory,
    IHubContext<EchoRealtimeHub> hubContext,
    ILogger<PlayerJoinNotificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PlayerJoinNotificationService started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in eventStream.StreamAsync(stoppingToken))
                {
                    logger.LogInformation("[PlayerJoinNotificationService] Received event {EventKind} for steamId {SteamId}", evt.Kind, evt.Steam);
                    if (evt.Kind != EventKind.Join)
                        continue; // leave/death/unknown are irrelevant here

                    logger.LogInformation("[PlayerJoinNotificationService] Processing join event for steamId {SteamId}", evt.Steam);
                    using var scope = scopeFactory.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

                    var player = await context.Players
                        .AsNoTracking()
                        .FirstOrDefaultAsync(p => p.SteamId == evt.Steam, stoppingToken);

                    if (player is null)
                    {
                        logger.LogWarning("[PlayerJoinNotificationService] Join event for unknown steamId {SteamId}", evt.Steam);
                        continue;
                    }

                    if (player.UserId is null)
                    {
                        logger.LogWarning("[PlayerJoinNotificationService] Join event for player {PlayerId} with no linked account", player.Id);
                        continue; // no linked account — nowhere to route the socket message
                    }

                    await hubContext.Clients.User(player.UserId).SendAsync(
                        "isle.PlayerJoined",
                        new { playerId = player.Id, steamId = player.SteamId },
                        stoppingToken);
                     logger.LogWarning("[PlayerJoinNotificationService] Player joined notification sent to user {UserId}", player.UserId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("[PlayerJoinNotificationService] Join notification stream dropped, reconnecting in 2s");
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("[PlayerJoinNotificationService] Join notification stream dropped, reconnecting in 2s");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}