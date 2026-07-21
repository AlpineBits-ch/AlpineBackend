namespace Isle.Api.Voice;

public readonly struct MapCell : IEquatable<MapCell>
{
    public float WorldX { get; init; }
    public float WorldY { get; init; }
    public float CellSize { get; init; }

    private int CellX => (int)MathF.Floor(WorldX / CellSize);
    private int CellY => (int)MathF.Floor(WorldY / CellSize);

    public bool Equals(MapCell other) =>
        CellSize == other.CellSize && CellX == other.CellX && CellY == other.CellY;

    public override bool Equals(object? obj) => obj is MapCell other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(CellX, CellY, CellSize);

    public static bool operator ==(MapCell left, MapCell right) => left.Equals(right);
    public static bool operator !=(MapCell left, MapCell right) => !left.Equals(right);

    public override string ToString() => $"{CellX}_{CellY}";
}