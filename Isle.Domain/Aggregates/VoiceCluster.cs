using Isle.Api.Voice;

namespace Isle.Domain.Aggregates;

public class VoiceCluster
{
    private readonly Dictionary<MapCell, VoiceCell> _cells = new();
    private readonly Dictionary<string, PlayerVoiceState> _players = new();
    private readonly VoiceGridConfig _config;

    public VoiceCluster(VoiceGridConfig config) => _config = config;

    public IReadOnlyList<VoiceClusterChange> MovePlayer(string playerId, float worldX, float worldY, float worldZ)
    {
        var newCell = new MapCell { WorldX = worldX, WorldY = worldY, CellSize = _config.CellSize };
        var changes = new List<VoiceClusterChange>();

        if (!_players.TryGetValue(playerId, out var player))
        {
            player = new PlayerVoiceState
            {
                PlayerId = playerId,
                PosX = worldX,
                PosY = worldY,
                PosZ = worldZ,
                CurrentCell = newCell
            };
            _players[playerId] = player;

            AddToCell(playerId, newCell);
            changes.Add(new VoiceClusterChange.Joined(playerId, newCell));
            EmitPositionIfMoved(player, worldX, worldY, worldZ, changes);
            return changes;
        }

        player.PosX = worldX;
        player.PosY = worldY;
        player.PosZ = worldZ;

        if (newCell != player.CurrentCell)
        {
            var oldCell = player.CurrentCell;
            RemoveFromCell(playerId, oldCell);
            player.CurrentCell = newCell;
            AddToCell(playerId, newCell);

            changes.Add(new VoiceClusterChange.Left(playerId, oldCell));
            changes.Add(new VoiceClusterChange.Joined(playerId, newCell));
        }

        EmitPositionIfMoved(player, worldX, worldY, worldZ, changes);
        return changes;
    }

    public IReadOnlyList<VoiceClusterChange> RemovePlayer(string playerId)
    {
        if (!_players.Remove(playerId, out var player))
            return [];

        RemoveFromCell(playerId, player.CurrentCell);
        return [new VoiceClusterChange.Left(playerId, player.CurrentCell)];
    }

    public IReadOnlyCollection<string> GetRoommates(string playerId) =>
        _players.TryGetValue(playerId, out var player) && _cells.TryGetValue(player.CurrentCell, out var cell)
            ? cell.GetPlayers()
            : Array.Empty<string>();

    private void EmitPositionIfMoved(PlayerVoiceState player, float x, float y, float z, List<VoiceClusterChange> changes)
    {
        if (player.HasEmittedPosition)
        {
            var dx = x - player.LastEmittedX;
            var dy = y - player.LastEmittedY;
            var dz = z - player.LastEmittedZ;
            var distSq = dx * dx + dy * dy + dz * dz;

            if (distSq < _config.MovementEpsilon * _config.MovementEpsilon)
                return; // hasn't moved enough to be worth broadcasting
        }

        player.LastEmittedX = x;
        player.LastEmittedY = y;
        player.LastEmittedZ = z;
        player.HasEmittedPosition = true;

        changes.Add(new VoiceClusterChange.Moved(player.PlayerId, x, y, z));
    }

    private void AddToCell(string playerId, MapCell coord)
    {
        if (!_cells.TryGetValue(coord, out var cell))
        {
            cell = new VoiceCell(coord);
            _cells[coord] = cell;
        }
        cell.AddPlayer(playerId);
    }

    private void RemoveFromCell(string playerId, MapCell coord)
    {
        if (!_cells.TryGetValue(coord, out var cell))
            return;

        cell.RemovePlayer(playerId);
        if (cell.Count == 0)
            _cells.Remove(coord);
    }
}

public abstract record VoiceClusterChange
{
    public record Joined(string PlayerId, MapCell Cell) : VoiceClusterChange;
    public record Left(string PlayerId, MapCell Cell) : VoiceClusterChange;
    public record Moved(string PlayerId, float WorldX, float WorldY, float WorldZ) : VoiceClusterChange;
}