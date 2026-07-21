namespace Isle.Api.Voice;

public readonly struct MapCell : IEquatable<MapCell>
{
    public float WorldX { get; init; }
    public float WorldY { get; init; }
    public float CellSize { get; init; }

    public int CellX => (int)MathF.Floor(WorldX / CellSize);
    public int CellY => (int)MathF.Floor(WorldY / CellSize);

    // The 3x3 block of cells centred on this one (this cell + its 8 neighbours).
    public IEnumerable<MapCell> Neighbourhood()
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
            yield return FromIndices(CellX + dx, CellY + dy, CellSize);
    }

    // Builds a cell from integer indices by pointing WorldX/Y at the cell centre, which
    // floors back to exactly (cellX, cellY) regardless of rounding — keeping equality/hashing
    // (which are index-based) intact.
    private static MapCell FromIndices(int cellX, int cellY, float cellSize) => new()
    {
        WorldX = (cellX + 0.5f) * cellSize,
        WorldY = (cellY + 0.5f) * cellSize,
        CellSize = cellSize,
    };

    public bool Equals(MapCell other) =>
        CellSize == other.CellSize && CellX == other.CellX && CellY == other.CellY;

    public override bool Equals(object? obj) => obj is MapCell other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(CellX, CellY, CellSize);

    public static bool operator ==(MapCell left, MapCell right) => left.Equals(right);
    public static bool operator !=(MapCell left, MapCell right) => !left.Equals(right);

    public override string ToString() => $"{CellX}_{CellY}";
}