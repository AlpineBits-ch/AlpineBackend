using Isle.Domain.Entity;

namespace Isle.Api.Services.World;

/// <summary>How many players are sitting in one region right now, and what share of the server that is.</summary>
public sealed record RegionPopulation(string RegionId, string Name, int Count, double Share);

/// <summary>Reads the roster snapshot as a population distribution.</summary>
public sealed class PopulationHeatmap(WorldRosterCache roster, RegionMap regions)
{
    /// <summary>A region holding at least this share of the online population counts as crowded.</summary>
    public const double CrowdedShare = 0.35;

    /// <summary>Below this many players the distribution is noise and crowding is not worth acting on.</summary>
    private const int MinPopulationForCrowding = 6;

    public int OnlineCount => roster.OnlineCount;

    /// <summary>Per-region counts, busiest first. Players in unmapped positions are excluded.</summary>
    public IReadOnlyList<RegionPopulation> Distribution()
    {
        var entries = roster.Entries;
        if (entries.Count == 0)
            return [];

        var total = (double)entries.Count;

        return entries
            .Where(e => e.RegionId is not null)
            .GroupBy(e => e.RegionId!)
            .Select(g => new RegionPopulation(
                g.Key,
                regions.DescribeRegion(g.Key),
                g.Count(),
                g.Count() / total))
            .OrderByDescending(r => r.Count)
            .ToList();
    }

    /// <summary>Regions over <see cref="CrowdedShare"/>. Empty while the server is too small for the measure to mean anything.</summary>
    public IReadOnlyList<RegionPopulation> CrowdedRegions()
    {
        if (roster.OnlineCount < MinPopulationForCrowding)
            return [];

        return Distribution().Where(r => r.Share >= CrowdedShare).ToList();
    }

    /// <summary>The single busiest region, or null when nothing is crowded enough to matter.</summary>
    public RegionPopulation? Hotspot() => CrowdedRegions().FirstOrDefault();

    /// <summary>
    /// Headcount for one region, including regions nobody is standing in (which the grouped
    /// distribution omits entirely).
    /// </summary>
    public int CountIn(string? regionId) =>
        regionId is null ? 0 : roster.Entries.Count(e => e.RegionId == regionId);

    public double ShareIn(string? regionId)
    {
        var total = roster.OnlineCount;
        return total == 0 ? 0 : (double)CountIn(regionId) / total;
    }

    public MapRegion? RegionOf(string? regionId) => regions.GetById(regionId);
}
