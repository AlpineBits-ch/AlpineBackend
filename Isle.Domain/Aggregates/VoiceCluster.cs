using Isle.Api.Voice;

namespace Isle.Domain.Aggregates;

public class VoiceCluster
{
    private readonly Dictionary<MapCell, VoiceCell> _cells = new();
    private readonly Dictionary<string, PlayerVoiceState> _players = new();
    private readonly VoiceGridConfig _config;

    public VoiceCluster(VoiceGridConfig config) => _config = config;

    public IReadOnlyList<VoiceClusterChange> MovePlayer(string playerId, float worldX, float worldY, float worldZ, float yaw = 0f)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var newCell = new MapCell { WorldX = worldX, WorldY = worldY, CellSize = _config.CellSize };
        var changes = new List<VoiceClusterChange>();

        // Audible set before the move (3x3 block around the old cell).
        _players.TryGetValue(playerId, out var player);
        var oldAudible = player is null ? EmptySet : NeighbourhoodOf(player.CurrentCell, playerId);

        if (player is null)
        {
            player = new PlayerVoiceState
            {
                PlayerId = playerId,
                PosX = worldX,
                PosY = worldY,
                PosZ = worldZ,
                Yaw = yaw,
                CurrentCell = newCell,
                LastUpdateUnixMs = nowMs
                // velocity stays 0 for the first sample — nothing to derive a delta from yet.
            };
            _players[playerId] = player;
            AddToCell(playerId, newCell);
        }
        else
        {
            // Derive velocity (units/sec) from the delta since the last sample.
            var dtMs = nowMs - player.LastUpdateUnixMs;
            if (dtMs > 0)
            {
                var dt = dtMs / 1000f;
                player.VelX = (worldX - player.PosX) / dt;
                player.VelY = (worldY - player.PosY) / dt;
                player.VelZ = (worldZ - player.PosZ) / dt;
            }

            player.PosX = worldX;
            player.PosY = worldY;
            player.PosZ = worldZ;
            player.Yaw = yaw;
            player.LastUpdateUnixMs = nowMs;

            if (newCell != player.CurrentCell)
            {
                RemoveFromCell(playerId, player.CurrentCell);
                player.CurrentCell = newCell;
                AddToCell(playerId, newCell);
            }
        }

        // Diff the audible set (3x3 block).
        var newAudible = NeighbourhoodOf(newCell, playerId);

        foreach (var other in newAudible)
            if (!oldAudible.Contains(other))
                changes.Add(new VoiceClusterChange.PeerJoined(playerId, other));

        foreach (var other in oldAudible)
            if (!newAudible.Contains(other))
                changes.Add(new VoiceClusterChange.PeerLeft(playerId, other));

        EmitPositionIfMoved(player, worldX, worldY, worldZ, yaw, changes);
        return changes;
    }

    public IReadOnlyList<VoiceClusterChange> RemovePlayer(string playerId)
    {
        if (!_players.Remove(playerId, out var player))
            return [];

        // Tell everyone who could still hear this player that they're gone.
        var audible = NeighbourhoodOf(player.CurrentCell, playerId);
        RemoveFromCell(playerId, player.CurrentCell);

        return audible
            .Select(other => (VoiceClusterChange)new VoiceClusterChange.PeerLeft(playerId, other))
            .ToList();
    }

    /// <summary>Players within earshot of <paramref name="playerId"/> — the 3x3 block around their cell, excluding themselves.</summary>
    public IReadOnlyCollection<string> GetAudiblePeers(string playerId) =>
        _players.TryGetValue(playerId, out var player)
            ? NeighbourhoodOf(player.CurrentCell, playerId)
            : Array.Empty<string>();

    /// <summary>
    /// Last known world position + facing + velocity for a player, for seeding a newly-audible
    /// (or reconnecting) peer so they're placed and can extrapolate immediately.
    /// </summary>
    public bool TryGetPosition(string playerId, out (float X, float Y, float Z, float Yaw, float Vx, float Vy, float Vz, long TimestampMs) position)
    {
        if (_players.TryGetValue(playerId, out var player))
        {
            position = (player.PosX, player.PosY, player.PosZ, player.Yaw,
                player.VelX, player.VelY, player.VelZ, player.LastUpdateUnixMs);
            return true;
        }

        position = default;
        return false;
    }

    private static readonly HashSet<string> EmptySet = new();

    // Union of players across the 3x3 block of cells centred on `centre`, excluding `self`.
    private HashSet<string> NeighbourhoodOf(MapCell centre, string self)
    {
        var result = new HashSet<string>();
        foreach (var coord in centre.Neighbourhood())
            if (_cells.TryGetValue(coord, out var cell))
                foreach (var occupant in cell.GetPlayers())
                    if (occupant != self)
                        result.Add(occupant);

        return result;
    }

    private void EmitPositionIfMoved(PlayerVoiceState player, float x, float y, float z, float yaw, List<VoiceClusterChange> changes)
    {
        if (player.HasEmittedPosition)
        {
            var dx = x - player.LastEmittedX;
            var dy = y - player.LastEmittedY;
            var dz = z - player.LastEmittedZ;
            var distSq = dx * dx + dy * dy + dz * dz;

            var movedEnough = distSq >= _config.MovementEpsilon * _config.MovementEpsilon;
            var turnedEnough = MathF.Abs(DeltaAngle(yaw, player.LastEmittedYaw)) >= _config.YawEpsilon;

            if (!movedEnough && !turnedEnough)
                return; // neither moved nor turned enough to be worth broadcasting
        }

        player.LastEmittedX = x;
        player.LastEmittedY = y;
        player.LastEmittedZ = z;
        player.LastEmittedYaw = yaw;
        player.HasEmittedPosition = true;

        changes.Add(new VoiceClusterChange.Moved(player.PlayerId, x, y, z, yaw,
            player.VelX, player.VelY, player.VelZ, player.LastUpdateUnixMs));
    }

    // Smallest signed difference between two angles in degrees, in (-180, 180].
    private static float DeltaAngle(float a, float b) => ((a - b + 540f) % 360f) - 180f;

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
    // PlayerId and OtherId can now hear / no longer hear each other. Applied mutually.
    public record PeerJoined(string PlayerId, string OtherId) : VoiceClusterChange;
    public record PeerLeft(string PlayerId, string OtherId) : VoiceClusterChange;
    public record Moved(string PlayerId, float WorldX, float WorldY, float WorldZ, float Yaw,
        float Vx, float Vy, float Vz, long TimestampMs) : VoiceClusterChange;
}