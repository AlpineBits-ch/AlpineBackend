using Echo.Realtime;
using Isle.Contracts.Commands;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Isle.Api.Services;

public sealed class GameEventIngestionService(
    IEventStream eventStream,
    VoicePlayerRegistry registry,
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

                    
                    
                    if (evt.Kind != EventKind.Leave)
                        continue; // join/death/unknown are irrelevant to voice membership

                    if (!registry.TryGetPlayerId(evt.Steam, out var playerId))
                        continue; // wasn't opted into voice — nothing to clean up

                    registry.UnregisterBySteamId(evt.Steam);

                    var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

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