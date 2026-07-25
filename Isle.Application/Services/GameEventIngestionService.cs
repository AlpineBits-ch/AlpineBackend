using Echo.Realtime;
using Isle.Contracts.Commands;
using Isle.Contracts.Events.Player;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Services;

public sealed class GameEventIngestionService(
    IEventStream eventStream,
    VoicePlayerRegistry registry,
    PlayerSpawnTracker spawnTracker,
    IServiceScopeFactory scopeFactory,
    ILogger<GameEventIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in eventStream.StreamAsync(stoppingToken))
                {
                    using var scope = scopeFactory.CreateScope();

                    var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                    var context = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

                    if (evt.Kind == EventKind.Leave)
                    {
                        await bus.PublishAsync(new UserLeftIsleServerEvent()
                        {
                            SteamId = @evt.Steam,
                        });
                    }

                    if (evt.Kind == EventKind.Join)
                    {
                        await bus.PublishAsync(new UserJoinedIsleServerEvent()
                        {
                            SteamId = @evt.Steam,
                        });
                    }


                    if (evt is KillfeedEvent killfeedEvent)
                    {
                        var killerPlayerId = (await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.SteamId == killfeedEvent.KillerSteam, cancellationToken: stoppingToken))?.Id;
                        var victimPlayerId = (await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.SteamId == killfeedEvent.VictimSteam, cancellationToken: stoppingToken))?.Id;
                        if (killerPlayerId == null || victimPlayerId == null) continue;
                        await bus.PublishAsync(new PlayerKillEvent()
                        {
                            KilerId = killerPlayerId,
                            VictimId = victimPlayerId,
                            VictimWeightInKg = killfeedEvent.VictimWeightKg,
                        });
                    }
                    
                    
                    // Approximate "spawned" from the events the bridge exposes: a fresh connect
                    // spawns a dino, and a death is immediately followed by a respawn.
                    if (evt.Kind is EventKind.Join or EventKind.Death)
                    {
                        await spawnTracker.MarkSpawnedAsync(evt.Steam, stoppingToken);
                    }

                    if (evt.Kind == EventKind.Death)
                    {
                        // A dino tied to a deployed storage slot just died — wipe that slot
                        // asynchronously so it frees up for a new dino.
                        await bus.PublishAsync(new WipeDeployedSlotsCommand { SteamId = evt.Steam });
                    }

                    if (evt.Kind != EventKind.Leave)
                    {
                      
                        continue; // join/death/unknown are irrelevant to voice membership

                    }

                    if (!registry.TryGetPlayerId(evt.Steam, out var playerId))
                        continue; // wasn't opted into voice — nothing to clean up

                    await registry.UnregisterBySteamIdAsync(evt.Steam);


                    await bus.InvokeAsync(new RemovePlayerCommand(playerId), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Event stream dropped, reconnecting in 2s");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}