using System.Numerics;
using Isle.Api.Services.World;
using Isle.Contracts.Events.Quest;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using Wolverine;

namespace Isle.Api.Services.Quests;

/// <summary>
/// Turns a director choice into a live, announced <see cref="QuestInstance"/>. Shared by the background
/// tick and <c>!questadmin</c> so a forced spawn behaves exactly like an automatic one.
///
/// <para>Spawning only. Closing an instance belongs to whoever knows what satisfying it means —
/// <see cref="QuestCompletionService"/> for exploration and hunt, <see cref="BountyService"/> for
/// bounties, since a bounty also has to unmark the target and restore their skin.</para>
/// </summary>
public sealed class QuestSpawner(
    MicroserviceContext context,
    QuestAnnouncer announcer,
    IMessageBus bus,
    ILogger<QuestSpawner> logger)
{
    public async Task<QuestInstance> SpawnAsync(QuestCandidate candidate, bool adminSpawned = false, CancellationToken ct = default)
    {
        var (x, y) = ResolveCoordinates(candidate.Location, candidate.Region);

        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = candidate.Quest.Id,
            Type = candidate.Quest.Type,
            Title = candidate.Quest.Name,
            Duration = candidate.Quest.Duration,
            LocationId = candidate.Location.Id,
            RegionId = candidate.Region.Id,
            LocationName = candidate.Region.Name,
            WorldX = x,
            WorldY = y,
            IsAdminSpawned = adminSpawned,
        });

        context.QuestInstances.Add(instance);

        // Tracked entity: the cooldown clock only starts once the instance is actually persisted.
        candidate.Quest.LastSpawnedAt = instance.StartedAt;

        await context.SaveChangesAsync(ct);

        logger.LogInformation("Spawned quest {Quest} at {Region} (instance {InstanceId}, admin={Admin})",
            candidate.Quest.Name, candidate.Region.Name, instance.Id, adminSpawned);

        await announcer.AnnounceQuestAsync(candidate.Quest, instance, ct);

        await bus.PublishAsync(new QuestSpawnedEvent
        {
            QuestInstanceId = instance.Id,
            QuestInstanceFriendlyId = instance.FriendlyId,
            QuestId = instance.QuestId,
            Title = instance.Title,
            Type = instance.Type,
            RegionId = instance.RegionId,
            LocationName = instance.LocationName,
            WorldX = instance.WorldX,
            WorldY = instance.WorldY,
            ExpiresAt = instance.ExpiresAt,
        });

        return instance;
    }

    /// <summary>
    /// Coordinates to print in the broadcast. Prefers an author-set geofence centre, falling back to
    /// the region centroid.
    /// <para>Note the plane mismatch worth reconciling when the coordinate table is rebuilt:
    /// <see cref="Isle.Domain.ValueObjects.GeoFenceData"/> does its containment maths on XZ, while
    /// <see cref="MapRegion"/> and the RCON roster use XY. Only X/Y are printed, so the announcement
    /// is consistent either way.</para>
    /// </summary>
    private (double? X, double? Y) ResolveCoordinates(QuestLocation location, MapRegion region)
    {
        if (location.GeoFence is { } fence && fence.Center != Vector3.Zero)
            return (fence.Center.X, fence.Center.Y);

        var center = region.Center;
        return center == Vector2.Zero ? (null, null) : (center.X, center.Y);
    }
}
