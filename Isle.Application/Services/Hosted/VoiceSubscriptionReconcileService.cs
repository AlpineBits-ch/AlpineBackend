using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Periodically re-drives the proximity-voice subscription graph so it converges even when an
/// individual <c>SubscribeMutual</c> push was lost, and re-arms clients whose published track the
/// server forgot across a restart.
///
/// <para><b>Missed subscribe.</b> The subscription wiring is otherwise <b>one-shot</b>: a peer
/// relationship is wired the instant the audible-set diff fires (<see cref="VoiceCluster.MovePlayer"/>)
/// or the moment a player publishes their mic (<c>VoiceCloudflareEndpoints.TracksNew</c>). Both push
/// over SignalR with no acknowledgement, and <c>SubscribeMutual</c> only wires a direction whose peer
/// already has a published track. So a push that lands while the recipient's socket isn't ready — or
/// a direction skipped because the other side hadn't published yet — is never retried, leaving the
/// classic "I can hear them, they hear nothing / see 0 nearby" asymmetry until someone changes cells
/// or republishes. Every tick this takes a consistent snapshot of every audible pair and re-issues
/// <c>SubscribeMutual</c>; that call is idempotent and symmetric, so healthy subscriptions are
/// untouched while any missing direction is restored within one interval.</para>
///
/// <para><b>Lost track after a restart.</b> The SignalR hub is terminated on the Echo gateway, not on
/// this service, so an Isle restart wipes the in-memory <see cref="VoiceTrackRegistry"/> without
/// dropping any client socket. A client that stayed connected never reconnects and so never
/// republishes — position telemetry rebuilds the grid (peers get told to pull <i>its</i> newly-joined
/// neighbours) but nothing rebuilds its own track record, so nobody can pull it back ("he sees me, I
/// don't see him"). This service detects players that are in the grid but have no registered track and
/// sends them a <c>RepublishVoice</c> order. A one-tick grace avoids nagging clients that are simply
/// mid-publish on a normal join, and a per-user cooldown avoids spamming a slow client.</para>
/// </summary>
public sealed class VoiceSubscriptionReconcileService(
    VoiceCluster cluster,
    VoiceTrackRegistry tracks,
    IServiceScopeFactory scopeFactory,
    ILogger<VoiceSubscriptionReconcileService> logger) : BackgroundService
{
    // Fast enough that a dropped subscribe on join is corrected within a couple of seconds of
    // real-world "why can't they hear me", cheap enough that the re-push fan-out is negligible.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // Don't re-order a republish more than once per this window — a normal publish completes well
    // inside it, so a client that got the order and is acting on it isn't nagged again.
    private static readonly TimeSpan RepublishCooldown = TimeSpan.FromSeconds(20);

    // Players seen track-less last tick, and the last time each was told to republish. Only touched
    // from the single reconcile loop, so plain collections are safe.
    private HashSet<string> _tracklessLastTick = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRepublishOrder = new();

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
                await ReconcileAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Voice subscription reconcile tick failed");
            }
        }
    }

    private async Task ReconcileAsync()
    {
        // Snapshot under the grid lock, then do the SFU pushes outside it.
        var players = cluster.GetPlayers();
        var pairs = cluster.GetAudiblePairs(); // each unordered pair once; SubscribeMutual wires both directions
        if (players.Count == 0)
            return;

        // ISfuClient is scoped (RealtimeSfuClient), so resolve it inside a per-tick scope
        // rather than injecting it into this singleton hosted service.
        using var scope = scopeFactory.CreateScope();
        var sfu = scope.ServiceProvider.GetRequiredService<ISfuClient>();

        await OrderRepublishForForgottenTracks(players, sfu);

        foreach (var (a, b) in pairs)
            await sfu.SubscribeMutual(a, b);

        logger.LogDebug("Voice subscription reconcile re-drove {PairCount} audible pair(s)", pairs.Count);
    }

    /// <summary>
    /// A player in the grid with no track in <see cref="VoiceTrackRegistry"/> is the restart-survivor
    /// signature: we know where they are (telemetry) but not how to pull their audio. Tell them to
    /// republish. Requires the player to have been track-less for two consecutive ticks so a normal
    /// joiner still finishing its first publish is left alone, and rate-limits per user.
    /// </summary>
    private async Task OrderRepublishForForgottenTracks(IReadOnlyCollection<string> players, ISfuClient sfu)
    {
        var trackless = new HashSet<string>();
        foreach (var userId in players)
            if (!tracks.TryGet(userId, out _))
                trackless.Add(userId);

        var now = DateTimeOffset.UtcNow;
        var ordered = 0;
        foreach (var userId in trackless)
        {
            // Grace: only act on a player track-less across two ticks — skips fresh joiners mid-publish.
            if (!_tracklessLastTick.Contains(userId))
                continue;

            if (_lastRepublishOrder.TryGetValue(userId, out var last) && now - last < RepublishCooldown)
                continue;

            _lastRepublishOrder[userId] = now;
            await sfu.RequestRepublish(userId);
            ordered++;
        }

        // Forget cooldown state for anyone who now has a track (or left), so a future loss re-triggers.
        if (_lastRepublishOrder.Count > 0)
            foreach (var userId in _lastRepublishOrder.Keys.Where(u => !trackless.Contains(u)).ToList())
                _lastRepublishOrder.Remove(userId);

        _tracklessLastTick = trackless;

        if (ordered > 0)
            logger.LogInformation("Ordered {Count} client(s) to republish voice after a forgotten-track detection", ordered);
    }
}
