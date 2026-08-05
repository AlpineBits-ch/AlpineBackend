using Isle.Api.Services.World;
using Isle.Domain.Aggregates;

namespace Isle.Api.Services.KingOfTheHill;

/// <summary>What a tick found, for the hosted service to act on.</summary>
public enum KothTickOutcome
{
    Continue,
    TimedOut,
    HeldAloneToWin,
}

/// <summary>The per-tick zone-presence and control-accrual logic for a running match.</summary>
public sealed class KingOfTheHillTickService(
    WorldRosterCache roster,
    KingOfTheHillControlLedger ledger,
    ILogger<KingOfTheHillTickService> logger)
{
    /// <summary>
    /// How long a player must hold the hill alone before the match ends early instead of running
    /// the full <c>MaxDuration</c>.
    /// </summary>
    public static readonly TimeSpan HeldAloneToWin = TimeSpan.FromMinutes(3);

    public async Task<KothTickOutcome> TickAsync(GameModeInstance instance)
    {
        var steamIdsInZone = roster.Entries
            .Where(e => instance.Definition.Zone.Contains(e.Position))
            .Select(e => e.Steam)
            .ToList();

        await ledger.ApplyPresenceAsync(instance.InstanceId, steamIdsInZone);
        await instance.Behavior.OnTickAsync(instance, DateTime.UtcNow - instance.StartedAt);

        // Cheap and low-cardinality (one line per 30s tick of a running match), but this is the only
        // place that ever sees who the zone check actually matched - the last time ticks silently
        // credited nobody the whole match, there was nothing in the logs to say why.
        logger.LogInformation("KOTH {InstanceId} tick: {Count} in zone: {SteamIds}",
            instance.InstanceId, steamIdsInZone.Count, string.Join(", ", steamIdsInZone));

        if (instance.HasTimedOut())
            return KothTickOutcome.TimedOut;

        if (await ledger.GetHolderStreakAsync(instance.InstanceId) is { } holder && holder.Streak >= HeldAloneToWin)
        {
            logger.LogInformation(
                "KOTH {InstanceId}: {Steam} has held the hill alone for {Streak}, ending early",
                instance.InstanceId, holder.SteamId, holder.Streak);
            return KothTickOutcome.HeldAloneToWin;
        }

        return KothTickOutcome.Continue;
    }
}
