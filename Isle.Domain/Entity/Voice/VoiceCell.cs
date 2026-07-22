namespace Isle.Domain.Entity.Voice;

public class VoiceCell
{
    public MapCell Coord { get; }
    private readonly HashSet<string> _players = new();

    public VoiceCell(MapCell coord) => Coord = coord;

    public int Count => _players.Count;

    public bool AddPlayer(string playerId) => _players.Add(playerId);
    public bool RemovePlayer(string playerId) => _players.Remove(playerId);
    public IReadOnlyCollection<string> GetPlayers() => _players;
}