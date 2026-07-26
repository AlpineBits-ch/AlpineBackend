using Isle.Api.Services.World;
using Isle.Contracts.Events.KingOfTheHill;
using Isle.Domain.Aggregates;
using Isle.Domain.Interfaces;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Services.KingOfTheHill;

/// <summary>Turns a chosen <see cref="GameModeDefinition"/> into a live, announced match.</summary>
public sealed class KingOfTheHillSpawner(
    MicroserviceContext context,
    IGameMode behavior,
    WorldRosterCache roster,
    KingOfTheHillMatchStateStore stateStore,
    KingOfTheHillAnnouncer announcer,
    IMessageBus bus,
    ILogger<KingOfTheHillSpawner> logger)
{
    public async Task<GameModeInstance> SpawnAsync(GameModeDefinition definition, CancellationToken ct = default)
    {
        var instance = new GameModeInstance(definition, behavior);
        instance.Start();

        var initialSteamIds = roster.Entries
            .Where(e => definition.Zone.Contains(e.Position))
            .Select(e => e.Steam)
            .ToList();

        var initialPlayerIds = initialSteamIds.Count == 0
            ? []
            : await context.Players
                .AsNoTracking()
                .Where(p => initialSteamIds.Contains(p.SteamId))
                .Select(p => p.Id)
                .ToListAsync(ct);

        foreach (var playerId in initialPlayerIds)
            instance.AddParticipant(playerId);

        // Tracked entity: the cooldown clock starts at spawn, not resolution, so a crash mid-match still
        // enforces it afterward.
        definition.LastRunAt = instance.StartedAt;
        await context.SaveChangesAsync(ct);

        await stateStore.WriteAsync(new KothMatchState(
            definition.Id, instance.InstanceId, instance.StartedAt, instance.ParticipantIds.ToList()));

        await behavior.OnStartAsync(instance);

        logger.LogInformation(
            "King of the Hill started (instance {InstanceId}), {Count} player(s) already in the zone",
            instance.InstanceId, initialPlayerIds.Count);

        await announcer.AnnounceStartedAsync(definition, ct);

        await bus.PublishAsync(new KothMatchStartedEvent
        {
            DefinitionId = definition.Id,
            InstanceId = instance.InstanceId,
            StartedAt = instance.StartedAt,
        });

        return instance;
    }
}
