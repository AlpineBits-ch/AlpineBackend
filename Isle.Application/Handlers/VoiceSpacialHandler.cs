using Isle.Contracts.Events.Voice;
using Isle.Domain.Aggregates;

namespace Isle.Api.Handlers;

public static class VoiceSpatialHandler
{
    public static async Task Handle(PlayerPositionUpdatedEvent @event, VoiceCluster cluster, ISfuClient sfu)
    {
        var roommates = cluster.GetRoommates(@event.PlayerId)
            .Where(p => p != @event.PlayerId)
            .ToList();

        if (roommates.Count == 0)
            return;

        await sfu.BroadcastPosition(@event.PlayerId, roommates, @event.WorldX, @event.WorldY, @event.WorldZ);
    }
}