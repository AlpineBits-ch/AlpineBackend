using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

public sealed class QuestInstanceDto
{
    public string Id { get; init; } = string.Empty;

    /// <summary>The short id shown in game chat, so a page and the chat box can name the same run.</summary>
    public string FriendlyId { get; init; } = string.Empty;

    public string QuestId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public QuestType Type { get; init; }
    public QuestInstanceState State { get; init; }
    public string? RegionId { get; init; }
    public string? LocationName { get; init; }
    public double? WorldX { get; init; }
    public double? WorldY { get; init; }
    public string? TargetSpecies { get; init; }
    public bool IsAdminSpawned { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class MapRegionDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public RegionType Type { get; init; }
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public int PlayerCount { get; init; }
    public double PopulationShare { get; init; }
    public IReadOnlyList<double[]> Polygon { get; init; } = [];
}

/// <summary>Read-only quest surface for the companion website.</summary>
public static class QuestEndpoints
{
    [WolverineGet("/api/v1/quests/active")]
    public static async Task<IReadOnlyList<QuestInstanceDto>> ActiveQuests(
        [NotBody] MicroserviceContext db, [NotBody] CancellationToken ct)
    {
        var instances = await db.QuestInstances
            .AsNoTracking()
            .Where(i => i.State == QuestInstanceState.Active && i.Type != QuestType.Bounty)
            .OrderBy(i => i.ExpiresAt)
            .ToListAsync(ct);

        return instances.Select(Project).ToList();
    }

    [WolverineGet("/api/v1/quests/bounties")]
    public static async Task<IReadOnlyList<QuestInstanceDto>> ActiveBounties(
        [NotBody] MicroserviceContext db, [NotBody] CancellationToken ct)
    {
        var instances = await db.QuestInstances
            .AsNoTracking()
            .Where(i => i.State == QuestInstanceState.Active && i.Type == QuestType.Bounty)
            .OrderBy(i => i.ExpiresAt)
            .ToListAsync(ct);

        return instances.Select(Project).ToList();
    }

    [WolverineGet("/api/v1/quests/history")]
    public static async Task<IReadOnlyList<QuestInstanceDto>> History(
        [NotBody] MicroserviceContext db, [NotBody] CancellationToken ct)
    {
        var instances = await db.QuestInstances
            .AsNoTracking()
            .Where(i => i.State != QuestInstanceState.Active)
            .OrderByDescending(i => i.EndedAt)
            .Take(50)
            .ToListAsync(ct);

        return instances.Select(Project).ToList();
    }

    /// <summary>The region table with live headcounts — the data a map overlay needs.</summary>
    [WolverineGet("/api/v1/world/regions")]
    public static IReadOnlyList<MapRegionDto> Regions(
        [NotBody] RegionMap regions, [NotBody] PopulationHeatmap heatmap) =>
        regions.Regions
            .Select(region => new MapRegionDto
            {
                Id = region.Id,
                Name = region.Name,
                Type = region.Type,
                CenterX = region.Center.X,
                CenterY = region.Center.Y,
                PlayerCount = heatmap.CountIn(region.Id),
                PopulationShare = heatmap.ShareIn(region.Id),
                Polygon = region.Polygon.Select(p => new double[] { p.X, p.Y }).ToList(),
            })
            .ToList();

    private static QuestInstanceDto Project(QuestInstance i) => new()
    {
        Id = i.Id,
        FriendlyId = i.FriendlyId,
        QuestId = i.QuestId,
        Title = i.Title,
        Type = i.Type,
        State = i.State,
        RegionId = i.RegionId,
        LocationName = i.LocationName,
        WorldX = i.WorldX,
        WorldY = i.WorldY,
        TargetSpecies = i.TargetSpecies,
        IsAdminSpawned = i.IsAdminSpawned,
        StartedAt = i.StartedAt,
        ExpiresAt = i.ExpiresAt,
    };
}
