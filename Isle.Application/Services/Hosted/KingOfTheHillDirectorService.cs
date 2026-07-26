using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Interfaces;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Hosted;

/// <summary>King of the Hill's clock.</summary>
public sealed class KingOfTheHillDirectorService(
    IServiceScopeFactory scopeFactory,
    WorldRosterCache roster,
    KingOfTheHillMatchStateStore stateStore,
    ILogger<KingOfTheHillDirectorService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Let the roster service land its first read before trying to start or tick anything.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(40);

    /// <summary>One missed refresh is ordinary; past that the snapshot is describing where players used to be.</summary>
    private static readonly TimeSpan MaxRosterAge = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            await SafeTickAsync(ct);

            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SafeTickAsync(CancellationToken ct)
    {
        try
        {
            if (roster.IsStale(MaxRosterAge))
            {
                // Silent before this: a live match just sat un-credited for the cycle with nothing
                // in the log to explain why the standings came up empty later.
                if (await stateStore.ReadAsync() is { } liveMarker)
                    logger.LogWarning("KOTH tick skipped for {InstanceId}: roster snapshot is stale", liveMarker.InstanceId);

                return;
            }

            using var scope = scopeFactory.CreateScope();
            var services = scope.ServiceProvider;

            var marker = await stateStore.ReadAsync();
            if (marker is null)
            {
                var director = services.GetRequiredService<KingOfTheHillDirector>();
                var spawner = services.GetRequiredService<KingOfTheHillSpawner>();

                if (await director.ChooseAsync(ct) is { } candidate)
                    await spawner.SpawnAsync(candidate, ct);

                return;
            }

            var context = services.GetRequiredService<MicroserviceContext>();
            var definition = await context.GameModeDefinitions
                .FirstOrDefaultAsync(d => d.Id == marker.DefinitionId, ct);

            if (definition is null)
            {
                logger.LogWarning("KOTH match marker points at a missing definition {DefinitionId}; clearing it",
                    marker.DefinitionId);
                await stateStore.ClearAsync();
                return;
            }

            var behavior = services.GetRequiredService<IGameMode>();
            var instance = GameModeInstance.Resume(
                definition, behavior, marker.InstanceId, marker.StartedAt, marker.ParticipantIds);

            var tick = services.GetRequiredService<KingOfTheHillTickService>();
            var outcome = await tick.TickAsync(instance);

            if (outcome != KothTickOutcome.Continue)
            {
                var completion = services.GetRequiredService<KingOfTheHillCompletionService>();
                await completion.ResolveAsync(instance, outcome, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "KOTH director tick failed");
        }
    }
}
