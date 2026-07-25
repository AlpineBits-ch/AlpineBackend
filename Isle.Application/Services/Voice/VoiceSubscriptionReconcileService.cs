using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;

namespace Isle.Api.Services;

/// <summary>
/// Periodically re-drives the proximity-voice subscription graph so it converges even when an
/// individual <c>SubscribeMutual</c> push was lost, and re-arms clients whose published track the
/// server forgot across a restart.
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

    // Players seen track-less last tick, and the last time each was told to republish.
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
    /// A player in the grid with no track in <see cref="VoiceTrackRegistry"/> is the
    /// restart-survivor signature: we know where they are (telemetry) but not how to pull their
    /// audio.
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
