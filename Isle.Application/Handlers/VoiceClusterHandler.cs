using Isle.Contracts.Commands;
using Isle.Contracts.Events.Voice;
using Isle.Domain.Aggregates;
using RemovePlayer = Isle.Domain.Events.Voice.RemovePlayer;

namespace Isle.Api.Handlers;

public static class VoiceClusterHandler
{
    public static IEnumerable<object> Handle(UpdatePlayerPosition command, VoiceCluster cluster)
    {
        var changes = cluster.MovePlayer(command.PlayerId, command.WorldX, command.WorldY, command.WorldZ);
        return changes.Select(ToMessage);
    }

    public static IEnumerable<object> Handle(RemovePlayer command, VoiceCluster cluster)
    {
        var changes = cluster.RemovePlayer(command.PlayerId);
        return changes.Select(ToMessage);
    }

    public static RoommatesResponse Handle(GetRoommates query, VoiceCluster cluster)
    {
        var roommates = cluster.GetRoommates(query.PlayerId);
        return new RoommatesResponse(query.PlayerId, roommates);
    }

    private static object ToMessage(VoiceClusterChange change) => change switch
    {
        VoiceClusterChange.Joined j => new PlayerJoinedCellEvent(j.PlayerId, j.Cell),
        VoiceClusterChange.Left l => new PlayerLeftCellEvent(l.PlayerId, l.Cell),
        VoiceClusterChange.Moved m => new PlayerPositionUpdatedEvent(m.PlayerId, m.WorldX, m.WorldY, m.WorldZ),
        _ => throw new ArgumentOutOfRangeException(nameof(change))
    };
}