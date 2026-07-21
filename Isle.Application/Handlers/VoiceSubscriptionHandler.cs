using Echo.Realtime;
using Isle.Contracts.Events.Voice;
using Isle.Domain.Aggregates;
using Microsoft.AspNetCore.SignalR;

namespace Isle.Api.Handlers;

public static class VoiceSubscriptionHandler
{
    public static async Task Handle(PlayerJoinedCellEvent @event, VoiceCluster cluster, ISfuClient sfu)
    {
        var roommates = cluster.GetRoommates(@event.PlayerId)
            .Where(p => p != @event.PlayerId);

        foreach (var other in roommates)
            await sfu.SubscribeMutual(@event.PlayerId, other);
    }

    public static async Task Handle(PlayerLeftCellEvent @event, ISfuClient sfu)
    {
        await sfu.UnsubscribeAll(@event.PlayerId, @event.Cell.ToString());
    }
}