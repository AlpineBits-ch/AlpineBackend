using System.Numerics;
using Isle.Api.Services.World;
using Isle.Contracts.Events.Quest;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Services.Quests;

/// <summary>
/// Turns a director choice into a live, announced <see cref="QuestInstance"/>, and closes instances
/// whose window has run out. Shared by the background tick and <c>!questadmin</c> so a forced spawn
/// behaves exactly like an automatic one.
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
    /// Closes every non-bounty instance past its window. Bounty expiry is <c>BountyService</c>'s job —
    /// it has to unmark the player and restore their skin, which is more than a state flip.
    /// </summary>
    public async Task<int> ExpireDueQuestsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await context.QuestInstances
            .Where(i => i.State == QuestInstanceState.Active && i.Type != QuestType.Bounty && i.ExpiresAt <= now)
            .ToListAsync(ct);

        if (due.Count == 0)
            return 0;

        foreach (var instance in due)
            instance.TryClose(QuestInstanceState.Expired);

        await context.SaveChangesAsync(ct);

        foreach (var instance in due)
        {
            await announcer.AnnounceQuestExpiredAsync(instance, ct);

            await bus.PublishAsync(new QuestInstanceExpiredEvent
            {
                QuestInstanceId = instance.Id,
                QuestInstanceFriendlyId = instance.FriendlyId,
                QuestId = instance.QuestId,
                Title = instance.Title,
                Type = instance.Type,
                RegionId = instance.RegionId,
                LocationName = instance.LocationName,
                ExpiresAt = instance.ExpiresAt,
            });
        }

        return due.Count;
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
