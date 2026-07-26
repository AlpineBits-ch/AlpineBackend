using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Wolverine;

namespace Isle.Api.Services.Ingestion;

/// <summary>
/// The single consumer of the bridge per-player stats feed, which is what drives proximity voice.
///
/// <para>Unlike the chat and game-event feeds this one publishes the already-resolved
/// <see cref="UpdatePlayerPositionCommand"/> rather than a raw snapshot event. The feed ticks about
/// once per second <i>per player</i>, and every bus message costs a RabbitMQ round trip plus a
/// durable-inbox write (see <c>Messaging.ConfigureWolverine</c>) — an extra translation hop would
/// double the busiest path in the service, and no subscriber wants the raw form.</para>
/// </summary>
public sealed class StatsStreamIngestionService(
    IStatsStream statsStream,
    VoicePlayerRegistry registry,
    IServiceScopeFactory scopeFactory,
    ILogger<StatsStreamIngestionService> logger)
    : BridgeStreamIngestionService<StatsSnapshot>(scopeFactory, logger)
{
    protected override string StreamName => "stats";

    protected override IAsyncEnumerable<StatsSnapshot> OpenStreamAsync(CancellationToken ct) =>
        statsStream.StreamAsync(ct);

    // Not opted into voice — ignore. Covers players without a linked steamId/userId, and players
    // who never pressed join. Filtering here keeps the hot path off the DI container entirely.
    protected override bool IsRelevant(StatsSnapshot message) =>
        registry.TryGetPlayerId(message.Steam, out _);

    protected override async Task PublishAsync(StatsSnapshot message, IMessageBus bus, CancellationToken ct)
    {
        // A fresh snapshot proves this player is still in-game — slide their voice TTL
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
