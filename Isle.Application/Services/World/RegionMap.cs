using System.Globalization;
using System.Numerics;
using Isle.Domain.Entity;
using Isle.Domain.Enums;

namespace Isle.Api.Services.World;

/// <summary>
/// Turns world coordinates into a place name players recognise ("East Lake Sanctuary"), which is
/// what every quest and bounty broadcast is built on.
/// </summary>
public sealed class RegionMap
{
    private const string UnknownPlace = "an unmapped part of the island";

    /// <summary>
    /// Beyond this centre-distance a region is no longer offered as a "near X" hint.
    /// </summary>
    private const float NearbyLimit = 60_000f;

    private readonly IReadOnlyList<MapRegion> _regions;

    public RegionMap()
    {
        var regions = MapRegion.GetRegions().ToList();
        foreach (var region in regions)
            region.CalculateBounds();

        _regions = regions;
    }

    public IReadOnlyList<MapRegion> Regions => _regions;

    public MapRegion? GetById(string? regionId) =>
        regionId is null ? null : _regions.FirstOrDefault(r => r.Id == regionId);

    /// <summary>
    /// Resolves a region by id, name or alias — the loose lookup <c>!questadmin</c> needs so an admin
    /// can type "swamps" instead of a generated id.
    /// </summary>
    public MapRegion? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return null;

        var needle = idOrName.Trim();

        return _regions.FirstOrDefault(r => string.Equals(r.Id, needle, StringComparison.OrdinalIgnoreCase))
               ?? _regions.FirstOrDefault(r => string.Equals(r.Name, needle, StringComparison.OrdinalIgnoreCase))
               ?? _regions.FirstOrDefault(r => r.Aliases.Any(a => string.Equals(a, needle, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The region containing this position, or null. Sanctuaries win ties: they sit inside biomes and are the more useful name.</summary>
    public MapRegion? Resolve(Vector2 position)
    {
        MapRegion? match = null;

        foreach (var region in _regions)
        {
            if (!region.Contains(position))
                continue;

            if (match is null || Specificity(region) > Specificity(match))
                match = region;
        }

        return match;
    }

    public MapRegion? Resolve(Vector3 position) => Resolve(new Vector2(position.X, position.Y));

    /// <summary>Closest region by centre distance, ignoring containment. Null when nothing is within <see cref="NearbyLimit"/>.</summary>
    public MapRegion? Nearest(Vector2 position)
    {
        MapRegion? best = null;
        var bestDistance = float.MaxValue;

        foreach (var region in _regions)
        {
            var distance = region.DistanceTo(position);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = region;
        }

        return bestDistance <= NearbyLimit ? best : null;
    }

    /// <summary>Player-facing place name for a position.</summary>
    public string Describe(Vector2 position)
    {
        if (Resolve(position) is { } exact)
            return exact.Name;

        return Nearest(position) is { } near ? $"near {near.Name}" : UnknownPlace;
    }

    public string Describe(Vector3 position) => Describe(new Vector2(position.X, position.Y));

    public string DescribeRegion(string? regionId) => GetById(regionId)?.Name ?? UnknownPlace;

    /// <summary>
    /// How the game itself prints a coordinate: grouped in thousands with a typographic apostrophe
    /// and carried to three decimals, e.g. <c>-321’806.894</c>.
    /// </summary>
    private static readonly NumberFormatInfo CoordinateFormat = new()
    {
        NumberGroupSeparator = "’",
        NumberDecimalSeparator = ".",
        NumberGroupSizes = [3],
        NegativeSign = "-",
    };

    /// <summary>
    /// Coordinate suffix appended to every broadcast, so a wrong or missing place name still leaves
    /// players something they can act on.
    /// </summary>
    public static string FormatCoordinates(double? x, double? y) =>
        x is null || y is null
            ? string.Empty
            : $"X: {x.Value.ToString("N3", CoordinateFormat)}, Y: {y.Value.ToString("N3", CoordinateFormat)}";

    // A sanctuary sits inside a biome polygon; naming the sanctuary is more useful than naming the
    // biome, and a landmark beats both.
    private static int Specificity(MapRegion region) => region.Type switch
    {
        RegionType.Landmark => 3,
        RegionType.Cave => 3,
        RegionType.NestingArea => 3,
        RegionType.Sanctuary => 2,
        RegionType.Lake => 2,
        RegionType.River => 2,
        RegionType.Biome => 1,
        _ => 0
    };
}
