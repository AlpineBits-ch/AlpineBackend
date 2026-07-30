using Isle.Api.Services.Rcon;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Runs a world sweep on every full hour (:00) over RCON: wipes all corpses and clears AI.
/// </summary>
public sealed class WorldCleanupService(
    IRconGateway rcon,
    WorldCleaner cleaner,
    ILogger<WorldCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan AnnounceLead = TimeSpan.FromMinutes(2);

    /// <summary>Auto-cleanup is skipped at or above this population.</summary>
    private const int PopulationGate = 40;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var wipeAt = NextFullHour(now);
            var announceAt = wipeAt - AnnounceLead;

            // Started inside the warning window — can't give a full 2-minute heads-up, so aim
            // at the following hour instead of firing a truncated warning.
            if (announceAt <= now)
            {
                wipeAt = wipeAt.AddHours(1);
                announceAt = wipeAt - AnnounceLead;
            }

            logger.LogInformation("Next world cleanup at {WipeAt:HH:mm} UTC (warning at {AnnounceAt:HH:mm})",
                wipeAt, announceAt);

            if (!await DelayUntil(announceAt, ct)) break;

            // Gate at the warning point: on a busy server, skip the whole cycle so we neither
            // announce nor wipe.
            if (!await IsBelowPopulationGateAsync())
            {
                continue;
            }

            await SafeAnnounceAsync();

            if (!await DelayUntil(wipeAt, ct)) break;
            await SafeCleanupAsync();
        }
    }

    /// <summary>Internal (rather than private) so unit tests can exercise the gate check without a real RCON socket or the hourly loop.</summary>
    internal async Task<bool> IsBelowPopulationGateAsync()
    {
        try
        {
            var details = await rcon.ExecuteAsync(client => client.GetServerDetails());
            if (details.CurrentPlayers >= PopulationGate)
            {
                logger.LogInformation(
                    "Skipping scheduled world cleanup: {Players} players online (gate {Gate})",
                    details.CurrentPlayers, PopulationGate);
                return false;
            }
        }
        catch (Exception ex)
        {
            // Can't read population — fall back to running the cleanup rather than silently stalling it.
            logger.LogWarning(ex, "Could not read server population; running scheduled cleanup anyway");
        }

        return true;
    }

    internal async Task SafeAnnounceAsync()
    {
        try
        {
            await rcon.ExecuteAsync(client =>
                client.Announce("Server cleanup in 2 minutes: all corpses will be wiped and AI reset."));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "World cleanup announcement failed");
        }
    }

    internal async Task SafeCleanupAsync()
    {
        try
        {
            await cleaner.WipeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hourly world cleanup failed");
        }
    }

    /// <summary>Internal (rather than private) so unit tests can exercise its edge cases (already-past target, cancellation) directly.</summary>
    internal static async Task<bool> DelayUntil(DateTimeOffset target, CancellationToken ct)
    {
        var delay = target - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero) return true;

        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Internal (rather than private) so unit tests can exercise its scheduling math directly.</summary>
    internal static DateTimeOffset NextFullHour(DateTimeOffset now) =>
        new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
}
