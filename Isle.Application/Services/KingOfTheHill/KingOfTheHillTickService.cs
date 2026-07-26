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

/// <summary>
/// The per-tick zone-presence and control-accrual logic for a running match. Pure logic, no hosting
/// concerns — <see cref="Hosted.KingOfTheHillDirectorService"/> owns the loop and the DI scope.
/// </summary>
public sealed class KingOfTheHillTickService(
    WorldRosterCache roster,
    KingOfTheHillControlLedger ledger,
    ILogger<KingOfTheHillTickService> logger)
{
    /// <summary>
    /// How long a player must hold the hill alone before the match ends early instead of running the
    /// full <c>MaxDuration</c>. Not config-driven for v1 — adding a column for it is a schema change this
    /// feature deliberately avoids; move it here if it ever needs to vary per definition.
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
