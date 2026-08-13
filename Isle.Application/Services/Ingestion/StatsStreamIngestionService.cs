using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Wolverine;

namespace Isle.Api.Services.Ingestion;

/// <summary>
/// The single consumer of the bridge per-player stats feed, which is what drives proximity voice.
/// </summary>
public sealed class StatsStreamIngestionService(
    IStatsStream statsStream,
    VoicePlayerRegistry registry,
    PlayerVitalsCache vitals,
    IServiceScopeFactory scopeFactory,
    ILogger<StatsStreamIngestionService> logger)
    : BridgeStreamIngestionService<StatsSnapshot>(scopeFactory, logger)
{
    protected override string StreamName => "stats";

    protected override IAsyncEnumerable<StatsSnapshot> OpenStreamAsync(CancellationToken ct) =>
        statsStream.StreamAsync(ct);

    /// <summary>Caches every player's vitals, whether or not they use voice.</summary>
    protected override Task ObserveAsync(StatsSnapshot message, CancellationToken ct) =>
        vitals.CaptureAsync(message, ct);

    // Not opted into voice - ignore.
    protected override bool IsRelevant(StatsSnapshot message) =>
        registry.TryGetPlayerId(message.Steam, out _);

    protected override async Task PublishAsync(StatsSnapshot message, IMessageBus bus, CancellationToken ct)
    {
        // A fresh snapshot proves this player is still in-game - slide their voice TTL
        // (throttled internally, so this is a no-op most ticks and never a hot-path cost).
        await registry.TouchAsync(message.Steam);

        if (message.Pos is null)
            return;

        // Re-read rather than trusting the pre-filter: a leave could have unregistered them in between.
        if (!registry.TryGetPlayerId(message.Steam, out var playerId))
            return;

        await bus.PublishAsync(new UpdatePlayerPositionCommand(
            playerId,
            (float)message.Pos.X,
            (float)message.Pos.Y,
            (float)message.Pos.Z,
            (float)(message.Rot?.Yaw ?? 0)));
    }
}
