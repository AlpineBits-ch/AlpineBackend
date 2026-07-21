using Isle.Contracts.Commands;
using Isle.Contracts.Events.Voice;
using Isle.Domain.Aggregates;
using RemovePlayer = Isle.Domain.Events.Voice.RemovePlayer;

namespace Isle.Api.Handlers;

public static class VoiceClusterHandler
{
    public static IEnumerable<object> Handle(UpdatePlayerPositionCommand command, VoiceCluster cluster)
    {
        var changes = cluster.MovePlayer(command.PlayerId, command.WorldX, command.WorldY, command.WorldZ, command.Yaw);
        return changes.Select(ToMessage);
    }

    public static IEnumerable<object> Handle(RemovePlayer command, VoiceCluster cluster)
    {
        var changes = cluster.RemovePlayer(command.PlayerId);
        return changes.Select(ToMessage);
    }

    public static RoommatesCommandResponse Handle(GetRoommatesCommand query, VoiceCluster cluster)
    {
        var roommates = cluster.GetRoommates(query.PlayerId);
        return new RoommatesCommandResponse(query.PlayerId, roommates);
    }

    private static object ToMessage(VoiceClusterChange change) => change switch
    {
        VoiceClusterChange.Joined j => new PlayerJoinedCellEvent(j.PlayerId, j.Cell),
        VoiceClusterChange.Left l => new PlayerLeftCellEvent(l.PlayerId, l.Cell),
        VoiceClusterChange.Moved m => new PlayerPositionUpdatedEvent(m.PlayerId, m.WorldX, m.WorldY, m.WorldZ, m.Yaw),
        _ => throw new ArgumentOutOfRangeException(nameof(change))
    };
}