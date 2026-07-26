using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Quests;

/// <summary>A template paired with the location the director wants to run it at.</summary>
public sealed record QuestCandidate(Quest Quest, QuestLocation Location, MapRegion Region, double Score);

/// <summary>Chooses what to spawn and where.</summary>
public sealed class QuestDirector(
    MicroserviceContext context,
    PopulationHeatmap heatmap,
    WorldRosterCache roster,
    RegionMap regions,
    ILogger<QuestDirector> logger)
{
    /// <summary>Instances allowed to be open at once (bounties excluded — they are targeted, not placed).</summary>
    public const int MaxConcurrentQuests = 2;

    /// <summary>A region used by one of the last N spawns is penalised, so quests do not pile into one corner.</summary>
    private const int RecentSpawnMemory = 4;

    private const double RecentlyUsedPenalty = 0.35;

    /// <summary>Roster older than this means we do not know where anyone is, so placement would be guesswork.</summary>
    private static readonly TimeSpan MaxRosterAge = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Picks the next quest to spawn, or null when nothing should spawn right now.
    /// </summary>
    public async Task<QuestCandidate?> ChooseAsync(CancellationToken ct = default)
    {
        if (roster.IsStale(MaxRosterAge))
        {
            logger.LogDebug("Skipping quest spawn: roster snapshot is stale");
            return null;
        }

        var online = roster.OnlineCount;
        if (online == 0)
            return null;

        var openCount = await context.QuestInstances
            .CountAsync(i => i.State == QuestInstanceState.Active && i.Type != QuestType.Bounty, ct);

        if (openCount >= MaxConcurrentQuests)
            return null;

        var now = DateTimeOffset.UtcNow;

        var openQuestIds = await context.QuestInstances
            .Where(i => i.State == QuestInstanceState.Active)
            .Select(i => i.QuestId)
            .ToListAsync(ct);

        var templates = await context.Quests
            .Include(q => q.Locations)
            .Where(q => q.Enabled && q.Type != QuestType.Bounty)
            .ToListAsync(ct);

        var eligible = templates
            .Where(q => q.IsSpawnable(online, now) && !openQuestIds.Contains(q.Id))
            .ToList();

        if (eligible.Count == 0)
            return null;

        var recentRegionIds = await RecentRegionIdsAsync(ct);
        var hotspot = heatmap.Hotspot();
        var hotRegion = heatmap.RegionOf(hotspot?.RegionId);

        var candidates = eligible
            .SelectMany(quest => quest.Locations.Select(location => BuildCandidate(quest, location, hotRegion, recentRegionIds)))
            .OfType<QuestCandidate>()
            .ToList();

        if (candidates.Count == 0)
        {
            logger.LogDebug("No spawnable quest had a location with a resolvable region");
            return null;
        }

        return PickWeighted(candidates);
    }

    /// <summary>Force-spawn path for <c>!questadmin spawn</c>: honours neither cooldown nor population gating.</summary>
    public async Task<QuestCandidate?> ChooseForcedAsync(string questIdOrName, string? regionIdOrName, CancellationToken ct = default)
    {
        var needle = questIdOrName.Trim();

        var quest = await context.Quests
            .Include(q => q.Locations)
            .FirstOrDefaultAsync(q => q.Id == needle, ct)
            ?? (await context.Quests
                    .Include(q => q.Locations)
                    .ToListAsync(ct))
                .FirstOrDefault(q => string.Equals(q.Name, needle, StringComparison.OrdinalIgnoreCase));

        if (quest is null || quest.Locations.Count == 0)
            return null;

        var wanted = regions.Find(regionIdOrName);

        var located = quest.Locations
            .Select(location => new { location, region = regions.GetById(location.RegionId) })
            .Where(x => x.region is not null)
            .ToList();

        if (located.Count == 0)
            return null;

        var chosen = wanted is null
            ? located[0]
            : located.FirstOrDefault(x => x.region!.Id == wanted.Id) ?? located[0];

        return new QuestCandidate(quest, chosen.location, chosen.region!, 1.0);
    }

    private QuestCandidate? BuildCandidate(
        Quest quest, QuestLocation location, MapRegion? hotRegion, IReadOnlyCollection<string> recentRegionIds)
    {
        var region = regions.GetById(location.RegionId);
        if (region is null)
            return null;   // unmapped location: we could not name it in a broadcast anyway

        var score = ScoreRegion(region, hotRegion, recentRegionIds);
        return new QuestCandidate(quest, location, region, score);
    }

    /// <summary>Higher is better.</summary>
    private double ScoreRegion(MapRegion region, MapRegion? hotRegion, IReadOnlyCollection<string> recentRegionIds)
    {
        // 1.0 when empty, 0.0 when the entire server is standing here.
        var score = 1.0 - heatmap.ShareIn(region.Id);

        if (hotRegion is not null)
        {
            if (region.Id == hotRegion.Id)
            {
                // Never send people back into the hotspot we are trying to break up.
                score *= 0.1;
            }
            else
            {
                // Normalised against the widest region-to-region gap on the map, so this stays a 0..1
                // term regardless of what scale the coordinate table ends up using.
                var spread = MaxRegionSpread();
                if (spread > 0)
                    score += 0.5 * Math.Clamp(region.DistanceTo(hotRegion) / spread, 0, 1);
            }
        }

        if (recentRegionIds.Contains(region.Id))
            score *= RecentlyUsedPenalty;

        return Math.Max(score, 0.01);
    }

    private double _cachedSpread = -1;

    private double MaxRegionSpread()
    {
        if (_cachedSpread >= 0)
            return _cachedSpread;

        var all = regions.Regions;
        var max = 0f;

        for (var i = 0; i < all.Count; i++)
        for (var j = i + 1; j < all.Count; j++)
            max = Math.Max(max, all[i].DistanceTo(all[j]));

        return _cachedSpread = max;
    }

    private async Task<IReadOnlyCollection<string>> RecentRegionIdsAsync(CancellationToken ct)
    {
        var ids = await context.QuestInstances
            .Where(i => i.RegionId != null)
            .OrderByDescending(i => i.StartedAt)
            .Take(RecentSpawnMemory)
            .Select(i => i.RegionId!)
            .ToListAsync(ct);

        return ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Weighted random over score × template weight.</summary>
    private static QuestCandidate PickWeighted(IReadOnlyList<QuestCandidate> candidates)
    {
        var total = candidates.Sum(c => c.Score * c.Quest.EffectiveWeight);
        if (total <= 0)
            return candidates[0];

        var roll = Random.Shared.NextDouble() * total;

        foreach (var candidate in candidates)
        {
            roll -= candidate.Score * candidate.Quest.EffectiveWeight;
            if (roll <= 0)
                return candidate;
        }

        return candidates[^1];
    }
}
