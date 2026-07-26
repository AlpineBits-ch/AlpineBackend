using System.Globalization;
using System.Numerics;
using Isle.Domain.Entity;
using Isle.Domain.Enums;

namespace Isle.Api.Services.World;

/// <summary>
/// Turns world coordinates into a place name players recognise ("East Lake Sanctuary"), which is what
/// every quest and bounty broadcast is built on.
///
/// <para><b>The region table is known to be wrong.</b> <see cref="MapRegion.GetRegions"/> still holds
/// placeholder polygons on visibly inconsistent scales, so the names this returns will not match the
/// real map until that table is rebuilt from Unreal coordinates. Nothing here throws or drops a
/// broadcast on a miss: an unmatched position degrades to the nearest region ("near the Highlands")
/// and then to a generic phrase, so the announcement is always sendable and only the label is wrong.
/// Announcements also carry raw X/Y for exactly this reason.</para>
/// </summary>
public sealed class RegionMap
{
    private const string UnknownPlace = "an unmapped part of the island";

    /// <summary>
    /// Beyond this centre-distance a region is no longer offered as a "near X" hint. Same provisional
    /// scale problem as <see cref="MapRegion.DefaultPointRadius"/>.
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

    /// <summary>
    /// Player-facing place name for a position. Always returns something printable.
    /// </summary>
    public string Describe(Vector2 position)
    {
        if (Resolve(position) is { } exact)
            return exact.Name;

        return Nearest(position) is { } near ? $"near {near.Name}" : UnknownPlace;
    }

    public string Describe(Vector3 position) => Describe(new Vector2(position.X, position.Y));

    public string DescribeRegion(string? regionId) => GetById(regionId)?.Name ?? UnknownPlace;

    /// <summary>
    /// How the game itself prints a coordinate: grouped in thousands with a typographic apostrophe and
    /// carried to three decimals, e.g. <c>-321’806.894</c>. The apostrophe rather than a comma matters
    /// because a comma already separates the axes in a broadcast, and the decimals matter because they
    /// are what make a broadcast coordinate comparable, digit for digit, against the readout a player
    /// is looking at on their own screen.
    /// <para>If the in-game chat font ever mangles U+2019, the fix is to swap it for an ASCII
    /// apostrophe here — nothing else reads this.</para>
    /// </summary>
    private static readonly NumberFormatInfo CoordinateFormat = new()
    {
        NumberGroupSeparator = "'",
        NumberDecimalSeparator = ".",
        NumberGroupSizes = [3],
        NegativeSign = "-",
    };

    /// <summary>
    /// Coordinate suffix appended to every broadcast, so a wrong or missing place name still leaves
    /// players something they can act on.
    ///
    /// <para><b>The numbers are only as good as their source.</b> A bounty's coordinates come from the
    /// live roster or a bridge read and are real Unreal centimetres, on the ±300'000 scale a player
    /// sees in game. A placed quest's come from <see cref="MapRegion.GetRegions"/>, and the biome
    /// polygons there (±140'000) and the sanctuary markers (±500) are on two different scales, neither
    /// confirmed against the real map — so a quest at a sanctuary still prints a three-digit
    /// coordinate. That is a data problem in the region table, not a formatting one, and it goes away
    /// when the table is rebuilt from real coordinates.</para>
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
