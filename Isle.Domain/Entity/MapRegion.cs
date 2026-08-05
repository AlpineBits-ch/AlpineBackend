using System.Numerics;
using Isle.Domain.Enums;

namespace Isle.Domain.Entity;

public sealed class MapRegion
{
    /// <summary>
    /// Extent used for a point-like region that sets no <see cref="Radius"/> of its own.
    /// </summary>
    public const float DefaultPointRadius = 25f;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    public RegionType Type { get; init; }

    public IReadOnlyList<Vector2> Polygon { get; init; } = [];

    public BoundingBox Bounds { get; set; }

    public string[] Aliases { get; init; } = [];

    /// <summary>
    /// Fallback extent for regions whose <see cref="Polygon"/> is a point or a line rather than a
    /// closed shape - every sanctuary below is a single marker coordinate.
    /// </summary>
    public float Radius { get; init; }

    /// <summary>True when <see cref="Polygon"/> is too thin to enclose anything and the region falls back to <see cref="Radius"/>.</summary>
    public bool IsPointLike => Polygon.Count < 3;

    public Vector2 Center => Polygon.Count == 0
        ? Vector2.Zero
        : new Vector2(Polygon.Average(p => p.X), Polygon.Average(p => p.Y));


    public static IReadOnlyCollection<MapRegion> GetRegions()
    {
        return
        [
            // TODO: replace with converted Unreal coordinates
            new()
            {
                Id = "south_plains",
                Name = "South Plains",
                Type = RegionType.Biome,
                Polygon =
                [
                    new(-420, -1100),
                    new(300, -1200),
                    new(700, -700),
                    new(400, -300),
                    new(-500, -400)
                ]
            },

            new()
            {
                Id = "highlands",
                Name = "Highlands",
                Type = RegionType.Biome,
                Polygon =
                [
                    new(-98000, 42000),
                    new(-28000, 82000),
                    new(22000, 52000),
                    new(-18000, -8000),
                    new(-76000, -12000)
                ]
            },

            new()
            {
                Id = "northern_jungle",
                Name = "Northern Jungle",
                Type = RegionType.Biome,
                Polygon =
                [
                    new(-43000, 90000),
                    new(10000, 110000),
                    new(60000, 70000),
                    new(20000, 30000),
                    new(-50000, 40000)
                ]
            },

            new()
            {
                Id = "swamps",
                Name = "Swamps",
                Type = RegionType.Biome,
                Polygon =
                [
                    new(70000, -30000),
                    new(140000, -30000),
                    new(140000, 70000),
                    new(70000, 70000)
                ]
            },


            // Sanctuaries

            new()
            {
                Id = "sanctuary_delta",
                Name = "Delta Sanctuary",
                Type = RegionType.Sanctuary,
                Polygon =
                [
                    new(-18,228)
                ]
            },

            new()
            {
                Id = "sanctuary_east_lake",
                Name = "East Lake Sanctuary",
                Type = RegionType.Sanctuary,
                Polygon =
                [
                    new(-173,436)
                ]
            },

            new()
            {
                Id = "sanctuary_highland",
                Name = "Highland Sanctuary",
                Type = RegionType.Sanctuary,
                Polygon =
                [
                    new(-67,-164),
                    new(-45,-172)
                ]
            },

            new()
            {
                Id = "sanctuary_mudflats",
                Name = "Mudflats Sanctuary",
                Type = RegionType.Sanctuary,
                Polygon =
                [
                    new(171,-329)
                ]
            },

            new()
            {
                Id = "sanctuary_south_plains",
                Name = "South Plains Sanctuary",
                Type = RegionType.Sanctuary,
                Polygon =
                [
                    new(229,-176)
                ]
            },

            new()
            {
                Id = "sanctuary_swamp",
                Name = "Swamp Sanctuary",
                Type = RegionType.Sanctuary,
                Polygon =
                [
                    new(282,28)
                ]
            },

            new()
            {
                Id = "sanctuary_verdant",
                Name = "Verdant Forest Sanctuary",
                Type = RegionType.Sanctuary,
                Polygon =
                [
                    new(-241,171)
                ]
            }
        ];
    }


    /// <summary>Whether a world position (map plane = X/Y) falls inside this region.</summary>
    public bool Contains(Vector2 point)
    {
        if (IsPointLike)
            return Vector2.Distance(Center, point) <= EffectiveRadius;

        if (!Bounds.Contains(point))
            return false;

        var inside = false;
        for (int i = 0, j = Polygon.Count - 1; i < Polygon.Count; j = i++)
        {
            var pi = Polygon[i];
            var pj = Polygon[j];
            var crosses = (pi.Y > point.Y) != (pj.Y > point.Y) &&
                          point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X;
            if (crosses) inside = !inside;
        }

        return inside;
    }

    /// <summary>
    /// Rough distance from a position to this region, used to pick a "near X" name when nothing
    /// contains the point and to spread quests away from crowded regions.
    /// </summary>
    public float DistanceTo(Vector2 point) => Vector2.Distance(Center, point);

    public float DistanceTo(MapRegion other) => Vector2.Distance(Center, other.Center);

    private float EffectiveRadius => Radius > 0 ? Radius : DefaultPointRadius;

    public void CalculateBounds()
    {
        if (Polygon.Count == 0)
            return;

        var minX = Polygon.Min(p => p.X);
        var maxX = Polygon.Max(p => p.X);
        var minY = Polygon.Min(p => p.Y);
        var maxY = Polygon.Max(p => p.Y);

        Bounds = new BoundingBox(
            new Vector2(minX, minY),
            new Vector2(maxX, maxY));
    }
}


public readonly record struct BoundingBox(Vector2 Min, Vector2 Max)
{
    public bool Contains(Vector2 point) =>
        point.X >= Min.X &&
        point.X <= Max.X &&
        point.Y >= Min.Y &&
        point.Y <= Max.Y;
}