using Isle.Contracts.Events.Player;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Wolverine;

namespace Isle.Api.Services.Ingestion;

/// <summary>The single consumer of the bridge join / leave / death / killfeed feed.</summary>
public sealed class GameEventStreamIngestionService(
    IEventStream eventStream,
    IServiceScopeFactory scopeFactory,
    ILogger<GameEventStreamIngestionService> logger)
    : BridgeStreamIngestionService<GameEvent>(scopeFactory, logger)
{
    protected override string StreamName => "game event";

    protected override IAsyncEnumerable<GameEvent> OpenStreamAsync(CancellationToken ct) =>
        eventStream.StreamAsync(ct);

    protected override async Task PublishAsync(GameEvent message, IMessageBus bus, CancellationToken ct)
    {
        switch (message)
        {
            case JoinEvent:
                logger.LogInformation("Player {Steam} joined", message.Steam);
                await bus.PublishAsync(new UserJoinedIsleServerEvent { SteamId = message.Steam });
                break;

            case LeaveEvent:
                logger.LogInformation("Player {Steam} left", message.Steam);
                await bus.PublishAsync(new UserLeftIsleServerEvent { SteamId = message.Steam });
                break;

            case DeathEvent death:
                logger.LogInformation("Player {Steam} died", message.Steam);
                await bus.PublishAsync(new UserDiedOnIsleServerEvent
                {
                    SteamId = message.Steam,
                    Species = death.Species,
                });
                break;

            case KillfeedEvent kill:
                await bus.PublishAsync(new PlayerKillfeedReportedEvent
                {
                    KillerSteamId = kill.KillerSteam,
                    VictimSteamId = kill.VictimSteam,
                    VictimWeightInKg = kill.VictimWeightKg,
                    IdempotencyKey = kill.IdempotencyKey,
                });
                break;

            default:
                logger.LogDebug("Ignoring unknown game event for {Steam}", message.Steam);
                break;
        }
    }
}
