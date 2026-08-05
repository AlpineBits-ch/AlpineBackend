using Isle.Contracts.Commands;
using Isle.Contracts.Events.Voice;
using Isle.Domain.Aggregates;

namespace Isle.Api.Handlers.Voice;

public static class VoiceClusterHandler
{
    public static IEnumerable<object> Handle(UpdatePlayerPositionCommand command, VoiceCluster cluster)
    {
        var changes = cluster.MovePlayer(command.PlayerId, command.WorldX, command.WorldY, command.WorldZ, command.Yaw);
        return changes.Select(ToMessage);
    }

    // Note: handles RemovePlayerCommand (Isle.Contracts) - the type actually dispatched by
    // /voice/leave and the game-leave ingestion.
    public static IEnumerable<object> Handle(RemovePlayerCommand command, VoiceCluster cluster)
    {
        var changes = cluster.RemovePlayer(command.PlayerId);
        return changes.Select(ToMessage);
    }

    public static RoommatesCommandResponse Handle(GetRoommatesCommand query, VoiceCluster cluster)
    {
        var audiblePeers = cluster.GetAudiblePeers(query.PlayerId);
        return new RoommatesCommandResponse(query.PlayerId, audiblePeers);
    }

    private static object ToMessage(VoiceClusterChange change) => change switch
    {
        VoiceClusterChange.PeerJoined j => new PeerBecameAudibleEvent(j.PlayerId, j.OtherId),
        VoiceClusterChange.PeerLeft l => new PeerBecameInaudibleEvent(l.PlayerId, l.OtherId),
        VoiceClusterChange.Moved m => new PlayerPositionUpdatedEvent(m.PlayerId, m.WorldX, m.WorldY, m.WorldZ, m.Yaw, m.Vx, m.Vy, m.Vz, m.TimestampMs),
        _ => throw new ArgumentOutOfRangeException(nameof(change))
    };
}
