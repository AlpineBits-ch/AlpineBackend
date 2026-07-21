using Isle.Contracts.Events.Voice;
using Isle.Domain.Aggregates;

namespace Isle.Api.Handlers;

public static class VoiceSpatialHandler
{
    public static async Task Handle(PlayerPositionUpdatedEvent @event, VoiceCluster cluster, ISfuClient sfu)
    {
        // The mover always needs their own position + facing to place peers relative to itself,
        // even when alone in a cell.
        await sfu.SendSelfPosition(@event.PlayerId, @event.WorldX, @event.WorldY, @event.WorldZ, @event.Yaw);

        var roommates = cluster.GetRoommates(@event.PlayerId)
            .Where(p => p != @event.PlayerId)
            .ToList();

        if (roommates.Count == 0)
            return;

        await sfu.BroadcastPosition(@event.PlayerId, roommates, @event.WorldX, @event.WorldY, @event.WorldZ, @event.Yaw);
    }
}
