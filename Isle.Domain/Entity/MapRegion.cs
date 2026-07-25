using System.Numerics;
using Isle.Domain.Enums;

namespace Isle.Domain.Entity;

public sealed class MapRegion
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    public RegionType Type { get; init; }

    public IReadOnlyList<Vector2> Polygon { get; init; } = [];

    public BoundingBox Bounds { get; set; }

    public string[] Aliases { get; init; } = [];


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