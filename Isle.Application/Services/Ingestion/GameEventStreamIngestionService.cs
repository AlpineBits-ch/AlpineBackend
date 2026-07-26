using Isle.Contracts.Events.Player;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Wolverine;

namespace Isle.Api.Services.Ingestion;

/// <summary>
/// The single consumer of the bridge join / leave / death / killfeed / damage feed.
/// </summary>
public sealed class GameEventStreamIngestionService(
    IEventStream eventStream,
    IServiceScopeFactory scopeFactory,
    ILogger<GameEventStreamIngestionService> logger)
    : BridgeStreamIngestionService<GameEvent>(scopeFactory, logger)
{
    protected override string StreamName => "game event";

    protected override IAsyncEnumerable<GameEvent> OpenStreamAsync(CancellationToken ct) =>
        eventStream.StreamAsync(ct);

    /// <summary>
    /// Damage is orders of magnitude louder than anything else on this feed, and the base class
    /// builds a DI scope for every message that gets past here.
    /// </summary>
    protected override bool IsRelevant(GameEvent message) => message switch
    {
        DamageDealtEvent damage =>
            damage.Damage > 0
            && !string.IsNullOrWhiteSpace(damage.AttackerSteam)
            && !string.IsNullOrWhiteSpace(damage.VictimSteam)
            && damage.AttackerSteam != damage.VictimSteam,
        _ => true,
    };

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

            // No log line: IsRelevant has already thrown away the noise, but what is left is still a
            // steady stream during any fight and would drown the log at anything above trace.
            case DamageDealtEvent damage:
                await bus.PublishAsync(new PlayerDamagedEvent
                {
                    AttackerSteamId = damage.AttackerSteam,
                    VictimSteamId = damage.VictimSteam,
                    Damage = damage.Damage,
                    Swings = damage.Swings,
                    OccurredAt = damage.Ts,
                });
                break;

            default:
                logger.LogDebug("Ignoring unknown game event for {Steam}", message.Steam);
                break;
        }
    }
}
