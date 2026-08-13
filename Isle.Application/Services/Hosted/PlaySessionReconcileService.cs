using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Drives <see cref="PlaySessionTracker.ReconcileAsync"/>: the loop that makes playtime a number
/// worth showing.
/// </summary>
public sealed class PlaySessionReconcileService(
    WorldRosterCache roster,
    IServiceScopeFactory scopeFactory,
    ILogger<PlaySessionReconcileService> logger) : BackgroundService
{
    /// <summary>How often a pass runs.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How old <see cref="WorldRosterCache"/> may be and still be trusted to say who is not
    /// playing.
    /// </summary>
    private static readonly TimeSpan MaxRosterAge = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Play session reconcile tick failed");
            }
        }
    }

    /// <summary>Internal so a test can drive one pass without waiting on <see cref="Interval"/>, the
    /// same way <see cref="VoicePresenceReconcileService.ReconcileAsync"/> is.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var tracker = scope.ServiceProvider.GetRequiredService<PlaySessionTracker>();

        var online = await ResolveOnlineAsync(db, ct);

        var closed = await tracker.ReconcileAsync(online, ct);
        if (closed > 0)
            logger.LogInformation("Play session reconcile closed {Closed} session(s)", closed);
    }

    /// <summary>
    /// The roster, resolved from Steam ids to player ids, or null when it is too old to act on.
    /// </summary>
    private async Task<IReadOnlyList<OnlinePlayer>?> ResolveOnlineAsync(MicroserviceContext db, CancellationToken ct)
    {
        if (roster.IsStale(MaxRosterAge))
        {
            logger.LogDebug("Roster is older than {MaxAge}; reconciling with the hard cap only", MaxRosterAge);
            return null;
        }

        var entries = roster.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Steam))
            .ToList();

        if (entries.Count == 0)
            return [];

        var steamIds = entries.Select(entry => entry.Steam).Distinct(StringComparer.Ordinal).ToList();

        var playerIdBySteam = await db.Players
            .AsNoTracking()
            .Where(player => steamIds.Contains(player.SteamId))
            .Select(player => new { player.SteamId, player.Id })
            .ToDictionaryAsync(row => row.SteamId, row => row.Id, StringComparer.Ordinal, ct);

        return entries
            .Where(entry => playerIdBySteam.ContainsKey(entry.Steam))
            .Select(entry => new OnlinePlayer(playerIdBySteam[entry.Steam], entry.Species))
            .ToList();
    }
}
