using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using Microsoft.EntityFrameworkCore;
using Isle.Api.Services.State;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Owns the durability lifecycle for <see cref="VoicePlayerRegistry"/> and <see
/// cref="PlayerPresenceManager"/>: The roster (<see cref="IBridgeClient.GetPlayersAsync"/>) is the
/// authoritative list of connected steamIds; presence is keyed by domain playerId, so we map steam
/// → playerId with a single query per tick.
/// </summary>
public sealed class VoicePresenceReconcileService(
    VoicePlayerRegistry voiceRegistry,
    PlayerPresenceManager presenceManager,
    IBridgeClient bridge,
    IServiceScopeFactory scopeFactory,
    ILogger<VoicePresenceReconcileService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Restart recovery: rebuild the caches from Redis before we start serving reads.
        await voiceRegistry.HydrateAsync();
        await presenceManager.HydrateAsync();

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
                await ReconcileAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Voice/presence reconcile tick failed");
            }
        }
    }

    /// <summary>Internal (rather than private) so unit tests can drive one reconcile pass directly without waiting on <see cref="Interval"/>.</summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        // Authoritative roster of steamIds currently connected to the game server.
        List<string> onlineSteamIds;
        try
        {
            var roster = await bridge.GetPlayersAsync(ct);
            onlineSteamIds = roster.Players;
        }
        catch (Exception ex)
        {
            // Roster unavailable this tick - still reconcile against Redis; TTL covers staleness.
            logger.LogDebug(ex, "Roster fetch failed; relying on TTL for staleness this tick");
            onlineSteamIds = [];
        }

        // Voice: drop TTL-expired entries, then slide the TTL for still-online participants.
        await voiceRegistry.ReconcileAsync();
        foreach (var steamId in onlineSteamIds)
            await voiceRegistry.TouchAsync(steamId);

        // Presence: drop TTL-expired entries from the local cache...
        await presenceManager.RebuildFromRedisAsync();

        // ...then refresh (and self-heal) presence for players the roster confirms are in-game.
        if (onlineSteamIds.Count > 0)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var onlinePlayerIds = await db.Players
                .AsNoTracking()
                .Where(p => onlineSteamIds.Contains(p.SteamId))
                .Select(p => p.Id)
                .ToListAsync(ct);

            foreach (var playerId in onlinePlayerIds)
                await presenceManager.AddPlayerIdAsync(playerId);
        }
    }
}
