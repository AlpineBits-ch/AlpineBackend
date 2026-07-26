using Isle.Contracts.Events.Voice;
using Isle.Domain;
using Isle.Domain.Aggregates;

namespace Isle.Api.Handlers.Voice;

public static class VoiceSubscriptionHandler
{
    public static async Task Handle(PeerBecameAudibleEvent @event, VoiceCluster cluster, ISfuClient sfu)
    {
        // Wire the audio both ways (SubscribeMutual only pushes to a side whose peer has already
        // published a track, so it's safe to call before either has a mic up).
        await sfu.SubscribeMutual(@event.PlayerId, @event.OtherId);

        // Seed each side with the other's last-known position so a stationary peer is placed
        // correctly on subscribe, rather than sitting at the origin until they next move.
        if (cluster.TryGetPosition(@event.OtherId, out var other))
            await sfu.SendPeerPosition(@event.PlayerId, @event.OtherId, other.X, other.Y, other.Z, other.Yaw,
                other.Vx, other.Vy, other.Vz, other.TimestampMs);

        if (cluster.TryGetPosition(@event.PlayerId, out var self))
            await sfu.SendPeerPosition(@event.OtherId, @event.PlayerId, self.X, self.Y, self.Z, self.Yaw,
                self.Vx, self.Vy, self.Vz, self.TimestampMs);
    }

    public static async Task Handle(PeerBecameInaudibleEvent @event, ISfuClient sfu)
    {
        await sfu.UnsubscribePair(@event.PlayerId, @event.OtherId);
    }
}
