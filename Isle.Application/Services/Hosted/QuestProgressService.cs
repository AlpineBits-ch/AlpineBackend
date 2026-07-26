using Isle.Api.Services.Quests;
using Isle.Api.Services.World;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Samples who is standing at an open quest, once per roster refresh.
///
/// <para>Its own loop rather than a step in <see cref="QuestDirectorService"/>'s three-minute tick: at
/// that cadence a player could cross the quest region and leave between two samples and never be seen
/// at all, which would make the dwell requirement a lottery on timing. This matches
/// <see cref="WorldRosterService"/>'s interval instead, because a sample is only ever as fresh as the
/// roster it reads.</para>
///
/// <para>Costs no RCON and no geometry — it reads the cache the roster service already filled and
/// writes one Redis batch per quest with anyone in it.</para>
/// </summary>
public sealed class QuestProgressService(
    IServiceScopeFactory scopeFactory,
    WorldRosterCache roster,
    ILogger<QuestProgressService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How old the roster may be and still be worth crediting. Two intervals: one missed refresh is
    /// ordinary and the positions are still roughly right, but past that the snapshot is describing
    /// where players used to be.
    /// </summary>
    private static readonly TimeSpan MaxRosterAge = TimeSpan.FromSeconds(60);

    /// <summary>Let the roster service land its first read before crediting anything.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(35);

    /// <summary>
    /// The roster timestamp the last credit ran against.
    ///
    /// <para>The guard that matters. This loop and the roster loop run at the same interval but are not
    /// synchronised, so they drift, and a roster refresh that fails leaves the previous snapshot in
    /// place — without this, a server whose RCON has gone away would keep crediting the same frozen
    /// snapshot every 30 seconds and pay a full quest reward to players who logged off ten minutes
    /// ago.</para>
    /// </summary>
    private DateTimeOffset? _lastCreditedRoster;

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
                return;

            if (roster.LastUpdatedAt is not { } updatedAt || updatedAt == _lastCreditedRoster)
                return;

            using var scope = scopeFactory.CreateScope();
            var completion = scope.ServiceProvider.GetRequiredService<QuestCompletionService>();

            await completion.TrackPresenceAsync(ct);

            _lastCreditedRoster = updatedAt;
        }
        catch (Exception ex)
        {
            // A dropped sample costs one tick of credit. Leaving _lastCreditedRoster alone means the
            // next tick retries against the same snapshot rather than skipping it.
            logger.LogWarning(ex, "Quest presence tick failed");
        }
    }
}
