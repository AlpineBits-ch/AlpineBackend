using Isle.Contracts.Events.Voice;
using Isle.Domain;
using Isle.Domain.Aggregates;

namespace Isle.Api.Handlers;

public static class VoiceSpatialHandler
{
    public static async Task Handle(PlayerPositionUpdatedEvent @event, VoiceCluster cluster, ISfuClient sfu)
    {
        // The mover always needs their own position + facing to place peers relative to itself,
        // even when alone.
        await sfu.SendSelfPosition(@event.PlayerId, @event.WorldX, @event.WorldY, @event.WorldZ, @event.Yaw,
            @event.Vx, @event.Vy, @event.Vz, @event.TimestampMs);

        var audiblePeers = cluster.GetAudiblePeers(@event.PlayerId)
            .Where(p => p != @event.PlayerId)
            .ToList();

        if (audiblePeers.Count == 0)
            return;

        await sfu.BroadcastPosition(@event.PlayerId, audiblePeers, @event.WorldX, @event.WorldY, @event.WorldZ, @event.Yaw,
            @event.Vx, @event.Vy, @event.Vz, @event.TimestampMs);
    }
}
